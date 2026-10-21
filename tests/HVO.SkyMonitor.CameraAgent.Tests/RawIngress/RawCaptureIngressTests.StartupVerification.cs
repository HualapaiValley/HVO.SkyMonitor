using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

/// <summary>
/// Startup checks committed evidence by structure only, so it does not grow with retained payload bytes, while every
/// payload's content is still verified before it is used (#1494). A payload that processing refuses ends its lane work as
/// <c>evidence-unavailable</c>, and every raw pass, a restart's included, verifies that payload in full until it is
/// quarantined.
/// </summary>
public sealed partial class RawCaptureIngressTests
{
    private static readonly CaptureLaneDefinition StandardLane = new("standard", true, true, true, new string('A', 64));

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
                // Processing that fails for a reason other than its evidence does not ask for the payload to be verified.
                var lease = await original.ClaimAsync(StandardLane, "processing-test", configuration, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                await original.FailAsync(lease, CaptureLaneHandlerResult.Terminal("processing.unhandled"), CancellationToken.None).ConfigureAwait(false);
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
    /// A payload whose content changed but not its length passes startup. Processing, which verifies a payload's checksum
    /// before it uses the frame, refuses it and ends the capture's lane work as <c>evidence-unavailable</c>. The pass that
    /// follows verifies that payload and quarantines it, as a startup used to, and while capture is paused it defers the
    /// move until capture runs.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SameLengthCorruption_IsFoundBeforeProcessingUsesItAndQuarantined(bool paused)
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        var configuration = CreateConfiguration();
        RawCaptureReceipt accepted;
        using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            accepted = (await original.AcceptAsync(
                configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
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

        await ReportThroughStandardLaneAsync(restarted, configuration).ConfigureAwait(false);
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
    /// The lane's terminal outcome is what asks for a reported payload to be verified, so the request survives a restart
    /// that comes before the pass that would quarantine it: a stop while capture runs, or the installer restarting a
    /// candidate it started with capture paused, whose pass held the quarantine.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AReportedMismatch_IsQuarantinedAfterARestart(bool paused)
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        var configuration = CreateConfiguration();
        RawCaptureReceipt accepted;
        using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            accepted = (await original.AcceptAsync(
                configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
        }
        SqliteConnection.ClearAllPools();
        var payload = accepted.StoredFrame.AbsolutePath;
        await File.WriteAllBytesAsync(payload, [6, 6, 6, 6]).ConfigureAwait(false);
        using (var admission = await hold.AdmissionAsync(paused).ConfigureAwait(false))
        using (var reporting = CreateIngress(root, new RawIngressState(TimeProvider.System), maintenanceGate: hold.Gate))
        {
            await reporting.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await ReportThroughStandardLaneAsync(reporting, configuration).ConfigureAwait(false);
            if (paused)
            {
                await reporting.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(File.Exists(payload), "A paused verification moved committed evidence.");
            }
        }
        SqliteConnection.ClearAllPools();
        using var restartedAdmission = await hold.RestartAsync().ConfigureAwait(false);
        var state = new RawIngressState(TimeProvider.System);
        var reads = new PayloadReadCounter();
        using var restarted = CreateIngress(root, state, reads, maintenanceGate: hold.Gate);

        if (paused)
        {
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(4L, reads.BytesRead, "The restarted candidate did not verify the reported payload.");
            Assert.IsTrue(File.Exists(payload), "A paused start moved committed evidence.");
            Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
            await restartedAdmission.ResumeAsync("restart-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(paused ? 8L : 4L, reads.BytesRead, "The restart did not verify the reported payload.");
        Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.IsFalse(File.Exists(payload), "The reported evidence was never quarantined.");
        Assert.HasCount(1, Directory.EnumerateFiles(
            Path.Combine(root, "quarantine"), Path.GetFileName(payload), SearchOption.AllDirectories).ToArray());
        using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(connection,
            $"SELECT state FROM raw_captures WHERE capture_id = '{accepted.Manifest.Descriptor.Capture.CaptureId:N}';").ConfigureAwait(false));
    }

    /// <summary>
    /// The lane worker reports a payload without any ingress lock, so the report can arrive while an initialization runs.
    /// That initialization must not clear it when it publishes: the next operation runs the raw pass again, and once the
    /// lane's outcome is recorded that pass quarantines the payload.
    /// </summary>
    [TestMethod]
    public async Task AReportMadeDuringARawPass_IsNotClearedByIt()
    {
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            RawCaptureReceipt accepted;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                accepted = (await original.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(4), [4, 4, 4, 4]), CancellationToken.None).ConfigureAwait(false))!;
            }
            SqliteConnection.ClearAllPools();
            // Evidence a crash left without a committed row is verified in full, which gives the pass a payload to open.
            using (var crashed = CreateIngress(
                root, new RawIngressState(TimeProvider.System), new OneShotFaultInjector(RawIngressFaultPoint.BeforeJournalCommit)))
            {
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(() => crashed.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            var payload = accepted.StoredFrame.AbsolutePath;
            await File.WriteAllBytesAsync(payload, [6, 6, 6, 6]).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            var reads = new PayloadReadCounter();
            var logger = new RecordingLogger();
            using var restarted = CreateIngress(root, state, reads, logger: logger);
            reads.BeforeNextOpen(restarted.InvalidateEvidence);

            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, logger.Snapshot.Count(entry => entry.EventId == 2041));
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, logger.Snapshot.Count(entry => entry.EventId == 2041), "The pass cleared a report made while it ran.");

            await ReportThroughStandardLaneAsync(restarted, configuration).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(8L, reads.BytesRead, "Only the crash-left payload and the reported one are verified.");
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.IsFalse(File.Exists(payload), "The reported evidence was never quarantined.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// Once a reported payload is quarantined, raw ingress stays unavailable until an operator releases the evidence, and
    /// each initialization meanwhile runs the raw pass again. That pass verifies neither the quarantined payload nor any
    /// other committed one.
    /// </summary>
    [TestMethod]
    public async Task AQuarantinedReport_IsNotVerifiedAgainWhileRawIngressIsUnavailable()
    {
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            RawCaptureReceipt corrupted;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                corrupted = (await original.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
                Assert.IsNotNull(await original.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(6), [6, 6, 6, 6]), CancellationToken.None).ConfigureAwait(false));
            }
            SqliteConnection.ClearAllPools();
            var payload = corrupted.StoredFrame.AbsolutePath;
            await File.WriteAllBytesAsync(payload, [7, 7, 7, 7]).ConfigureAwait(false);
            var state = new RawIngressState(TimeProvider.System);
            var reads = new PayloadReadCounter();
            using var restarted = CreateIngress(root, state, reads);
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await ReportThroughStandardLaneAsync(restarted, configuration).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(payload), "The reported evidence was never quarantined.");
            Assert.AreEqual(4L, reads.BytesRead);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }

            Assert.AreEqual(4L, reads.BytesRead, "A later initialization verified committed payloads again.");
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
        }
        finally
        {
            DeleteRoot(root);
        }
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

    [TestMethod]
    public async Task FailAsync_AfterAnInterveningRawPass_RequestsReconciliationAgain()
    {
        var root = CreateRoot();
        try
        {
            var configuration = CreateConfiguration();
            RawCaptureReceipt accepted;
            using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                accepted = (await original.AcceptAsync(
                    configuration, CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
            }
            SqliteConnection.ClearAllPools();
            var payload = accepted.StoredFrame.AbsolutePath;
            await File.WriteAllBytesAsync(payload, [6, 6, 6, 6]).ConfigureAwait(false);
            var reads = new PayloadReadCounter();
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System), reads);
            var lease = await ingress.ClaimAsync(StandardLane, "reconciliation-race", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            using var standard = new StandardCaptureLaneHandler(
                new RawReadingPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, ingress);
            var result = await standard.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("evidence-unavailable", result.Reason);

            // Consume the handler's request before its terminal lane outcome has been committed.
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, reads.BytesRead);
            Assert.IsTrue(File.Exists(payload));

            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure,
                await ingress.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(4L, reads.BytesRead);
            Assert.IsFalse(File.Exists(payload));
            Assert.HasCount(1, Directory.EnumerateFiles(
                Path.Combine(root, "quarantine"), Path.GetFileName(payload), SearchOption.AllDirectories).ToArray());
            using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("quarantined", await ScalarStringAsync(connection,
                $"SELECT state FROM raw_captures WHERE capture_id = '{accepted.Manifest.Descriptor.Capture.CaptureId:N}';").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// Runs the capture's standard lane work as the lane worker does: loading the raw frame verifies its checksum, the
    /// handler reports the evidence unavailable, and the lane's terminal outcome is recorded.
    /// </summary>
    private static async Task ReportThroughStandardLaneAsync(RawCaptureIngress ingress, CameraModuleConfig configuration)
    {
        var lease = await ingress.ClaimAsync(StandardLane, "processing-test", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        using var standard = new StandardCaptureLaneHandler(
            new RawReadingPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, ingress);
        var result = await standard.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("evidence-unavailable", result.Reason);
        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure,
            await ingress.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false));
    }

    private sealed class RawReadingPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
            => new([new CaptureProcessingGraphNode("raw-reader", new RawReadingStep(), [], true, null, null, null, new string('R', 64))]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
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
        private Action? _beforeNextOpen;

        public int Opens => Volatile.Read(ref _opens);

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        /// <summary>Runs <paramref name="action"/> inside the reconciliation pass, as it opens its next payload.</summary>
        public void BeforeNextOpen(Action action) => Volatile.Write(ref _beforeNextOpen, action);

        public void Inject(RawIngressFaultPoint point)
        {
        }

        public Stream OpenPayloadForVerification(string path)
        {
            Interlocked.Exchange(ref _beforeNextOpen, null)?.Invoke();
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
