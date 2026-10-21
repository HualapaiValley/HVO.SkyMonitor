using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

/// <summary>
/// Startup checks committed evidence by structure only, so it does not grow with retained payload bytes, while every
/// payload's content is still verified before it is used (#1494).
/// </summary>
public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task InitializeAsync_ManyCommittedCaptures_ReadsNoPayloadBytes()
    {
        const int captures = 32;
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                for (var index = 0; index < captures; index++)
                {
                    var payload = Enumerable.Repeat((byte)index, 4).ToArray();
                    Assert.IsNotNull(await original.AcceptAsync(
                        configuration, CreateSubmission(Timestamp(1).AddMinutes(index), payload), CancellationToken.None).ConfigureAwait(false));
                }
            }
            SqliteConnection.ClearAllPools();
            var state = new RawIngressState(TimeProvider.System);
            var reads = new PayloadReadCounter();
            using var restarted = CreateIngress(root, state, reads);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, reads.Opens, "Startup opened a committed payload.");
            Assert.AreEqual(0L, reads.BytesRead, "Startup read committed payload bytes.");
            Assert.AreEqual(captures, state.LastReconciliation?.Inspected);
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(5)]
    public async Task InitializeAsync_CommittedPayloadOfAnotherLength_IsQuarantinedWithoutReadingPayloads(int length)
    {
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            RawCaptureReceipt intact;
            RawCaptureReceipt changed;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                intact = (await original.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(4), [4, 4, 4, 4]), CancellationToken.None).ConfigureAwait(false))!;
                changed = (await original.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
            }
            SqliteConnection.ClearAllPools();
            await File.WriteAllBytesAsync(
                changed.StoredFrame.AbsolutePath, Enumerable.Repeat((byte)5, length).ToArray()).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            var reads = new PayloadReadCounter();
            using var restarted = CreateIngress(root, state, reads);

            // As before: the mismatched committed evidence is quarantined, and raw ingress reports it unavailable.
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(0, reads.Opens, "The length check needs no payload bytes.");
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.IsFalse(File.Exists(changed.StoredFrame.AbsolutePath));
            Assert.IsTrue(File.Exists(intact.StoredFrame.AbsolutePath));
            Assert.HasCount(1, Directory.EnumerateFiles(Path.Combine(root, "quarantine"),
                Path.GetFileName(changed.StoredFrame.AbsolutePath), SearchOption.AllDirectories).ToArray());
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("quarantined", await ScalarStringAsync(connection,
                $"SELECT state FROM raw_captures WHERE capture_id = '{changed.Manifest.Descriptor.Capture.CaptureId:N}';").ConfigureAwait(false));
            Assert.AreEqual("committed", await ScalarStringAsync(connection,
                $"SELECT state FROM raw_captures WHERE capture_id = '{intact.Manifest.Descriptor.Capture.CaptureId:N}';").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// A payload whose content changed but not its length passes startup, and processing, which verifies a payload's
    /// checksum before it uses the frame, reports it unavailable. The pass that follows verifies every committed payload
    /// and quarantines it, as a startup used to, and while capture is paused it defers the move until capture runs.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SameLengthCorruption_IsFoundBeforeProcessingUsesItAndQuarantined(bool paused)
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        var configuration = CreateConfiguration();
        var submission = CreateSubmission(Timestamp(5), [5, 5, 5, 5]);
        RawCaptureReceipt accepted;
        using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            accepted = (await original.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false))!;
        }
        SqliteConnection.ClearAllPools();
        var payload = accepted.StoredFrame.AbsolutePath;
        await File.WriteAllBytesAsync(payload, [6, 6, 6, 6]).ConfigureAwait(false);
        using var admission = await hold.AdmissionAsync(paused).ConfigureAwait(false);
        var state = new RawIngressState(TimeProvider.System);
        var reads = new PayloadReadCounter();
        using var restarted = CreateIngress(root, state, reads, maintenanceGate: hold.Gate);

        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, reads.BytesRead, "Startup read a committed payload.");
        Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);

        var channel = new FrameProcessingChannel(2);
        await channel.WriteAsync(new FrameProcessingItem(configuration, submission, accepted), CancellationToken.None).ConfigureAwait(false);
        channel.Complete();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new FrameProcessingWorker(channel, [new RawReadingStep()], NullLogger.Instance, restarted)
                .RunAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.IsTrue(File.Exists(payload));

        if (paused)
        {
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(payload), "A paused verification moved committed evidence.");
            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            await admission.ResumeAsync("corruption-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(paused ? 8L : 4L, reads.BytesRead, "The passes after the reported failure did not verify the payload.");
        Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.IsFalse(File.Exists(payload), "The corrupted evidence was never quarantined.");
        Assert.HasCount(1, Directory.EnumerateFiles(
            Path.Combine(root, "quarantine"), Path.GetFileName(payload), SearchOption.AllDirectories).ToArray());
        using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(connection,
            $"SELECT state FROM raw_captures WHERE capture_id = '{accepted.Manifest.Descriptor.Capture.CaptureId:N}';").ConfigureAwait(false));
    }

    /// <summary>
    /// A crash after both files are published but before the journal commits leaves evidence with no committed row.
    /// Recovery builds that row from the files, so it still hashes the payload in full; a committed payload is not read.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializeAsync_EvidenceACrashLeftUncommitted_IsVerifiedInFull(bool corrupt)
    {
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            using (var committed = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                await committed.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(4), [4, 4, 4, 4]), CancellationToken.None).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using (var crashed = CreateIngress(
                root, new RawIngressState(TimeProvider.System), new OneShotFaultInjector(RawIngressFaultPoint.BeforeJournalCommit)))
            {
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(() => crashed.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            var orphan = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories)
                .Single(path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 5, 5, 5, 5 }));
            if (corrupt)
            {
                await File.WriteAllBytesAsync(orphan, [6, 6, 6, 6]).ConfigureAwait(false);
            }
            var state = new RawIngressState(TimeProvider.System);
            var reads = new PayloadReadCounter();
            using var restarted = CreateIngress(root, state, reads);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, reads.Opens, "Only the payload without a committed row is read.");
            Assert.AreEqual(4L, reads.BytesRead, "The uncommitted payload was not hashed in full.");
            Assert.AreEqual(corrupt ? 0 : 1, state.LastReconciliation?.Recovered);
            Assert.AreEqual(corrupt ? 1 : 0, state.LastReconciliation?.Quarantined);
            Assert.AreEqual(!corrupt, File.Exists(orphan));
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(corrupt ? 1L : 2L, await ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM raw_captures WHERE state = 'committed';").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// The installer starts a candidate with capture paused, so stage cleanup is held, and then resumes capture. The
    /// resume runs the held stage cleanup and nothing else: the raw ingress reconciliation already ran in this process.
    /// </summary>
    [TestMethod]
    public async Task ResumeAfterAPausedStart_RunsTheHeldMaintenanceWithoutReconcilingAgain()
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            await original.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var stage = Path.Combine(root, "staging", "projected-scenes", $"{new string('f', 64)}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        await File.WriteAllTextAsync(stage, "{}").ConfigureAwait(false);
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        using var staging = new ProjectedSceneStagingStore(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
        var logger = new RecordingLogger();
        var reads = new PayloadReadCounter();
        using var restarted = CreateIngress(
            root, new RawIngressState(TimeProvider.System), reads, logger: logger, maintenanceGate: hold.Gate,
            projectedSceneStaging: staging);

        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(stage), "A paused start removed a stage.");
        Assert.AreEqual(1, logger.Snapshot.Count(entry => entry.EventId == 2041));

        await admission.ResumeAsync("reconcile-once-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            if (!File.Exists(stage)) break;
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.IsFalse(File.Exists(stage), "The held stage cleanup never ran.");
        Assert.AreEqual(1, logger.Snapshot.Count(entry => entry.EventId == 2041), "The resume reconciled raw ingress again.");
        Assert.AreEqual(0, reads.Opens);
    }

    private sealed class RawReadingStep : ICaptureProcessingStep
    {
        public string Name => "raw-reader";

        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => throw new AssertFailedException("Processing used a payload whose checksum does not match.");
    }

    /// <summary>Counts every payload raw ingress opens to verify, and every byte it reads from one.</summary>
    private sealed class PayloadReadCounter : IRawIngressFaultInjector
    {
        private int _opens;
        private long _bytesRead;

        public int Opens => Volatile.Read(ref _opens);

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public void Inject(RawIngressFaultPoint point)
        {
        }

        public Stream OpenPayloadForVerification(string path)
        {
            Interlocked.Increment(ref _opens);
            return new CountingStream(RawIngressReconciler.OpenPayload(path), this);
        }

        private void Count(int bytes) => Interlocked.Add(ref _bytesRead, bytes);

        private sealed class CountingStream(Stream inner, PayloadReadCounter counter) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));
            public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            private int Counted(int read)
            {
                counter.Count(read);
                return read;
            }
        }
    }
}
