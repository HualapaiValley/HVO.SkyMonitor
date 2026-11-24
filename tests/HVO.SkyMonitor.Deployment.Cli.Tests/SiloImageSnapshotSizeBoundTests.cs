using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The staged Silo archive's size bound (#1966 review): the private snapshot never receives a byte past the bound, even
/// when the staged file grows after its length was read, so a concurrent append cannot spend scratch space before the
/// verifier would refuse it. The copy the snapshot uses is driven at a scaled bound by a staged file that an appender
/// extends each time the reader reaches its end.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloImageSnapshotSizeBoundTests
{
    private const long Bound = 1000;
    private const string Name = "silo-amd64.tar";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(11L, 1, 4096, DisplayName = "one byte past the bound in one append")]
    [DataRow(100L, 1, 4096, DisplayName = "past the bound within one read")]
    [DataRow(4096L, 64, 4096, DisplayName = "far past the bound across many appends and reads")]
    [DataRow(4096L, -1, 4096, DisplayName = "an appender that never stops")]
    [DataRow(11L, 1, 7, DisplayName = "one byte past the bound in short reads")]
    public async Task Copy_RefusesAStagedArchiveThatGrowsPastTheBoundAfterItsLengthWasReadWithoutWritingPastIt(long each, int appends, int largestRead)
    {
        using var staged = new GrowingStream(Bound - 10, each, appends, largestRead);
        using var snapshot = new MemoryStream();

        var error = await Capture(() => SiloImageSnapshot.CopyStagedAsync(staged, snapshot, Bound, Name, TestContext.CancellationToken));

        Assert.IsLessThanOrEqualTo(Bound, snapshot.Length, $"The snapshot received {snapshot.Length} bytes against a bound of {Bound}.");
        var refusal = Assert.IsInstanceOfType<InstallerException>(error, $"Expected a refusal; got {error?.GetType().Name ?? "success"}: {error?.Message}");
        Assert.IsTrue(refusal.IsRefusal);
        Assert.AreEqual($"The staged Silo archive {Name} is larger than any image of the pinned release.", refusal.Message);
        Assert.IsLessThanOrEqualTo(Bound + 1, staged.Served, "Reading stops one byte past the bound.");
    }

    [TestMethod]
    [DataRow(0L, 4096)]
    [DataRow(1L, 4096)]
    [DataRow(Bound - 1, 4096)]
    [DataRow(Bound, 4096)]
    [DataRow(Bound, 7)]
    [DataRow(Bound, 1)]
    public async Task Copy_CopiesAStagedArchiveUpToAndIncludingTheBoundExactly(long length, int largestRead)
    {
        using var staged = new GrowingStream(length, each: 0, appends: 0, largestRead);
        using var snapshot = new MemoryStream();

        await SiloImageSnapshot.CopyStagedAsync(staged, snapshot, Bound, Name, TestContext.CancellationToken);

        CollectionAssert.AreEqual(GrowingStream.Expected(length), snapshot.ToArray());
    }

    [TestMethod]
    public async Task Copy_RefusesAStagedArchiveLargerThanTheBoundBeforeReadingOrWritingAnything()
    {
        using var staged = new GrowingStream(Bound + 1, each: 0, appends: 0, largestRead: 4096);
        using var snapshot = new MemoryStream();

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            SiloImageSnapshot.CopyStagedAsync(staged, snapshot, Bound, Name, TestContext.CancellationToken));

        Assert.IsTrue(refusal.IsRefusal);
        Assert.AreEqual(0L, staged.Served);
        Assert.AreEqual(0L, snapshot.Length);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "cancelled before the copy")]
    [DataRow(true, DisplayName = "cancelled during the copy")]
    public async Task Copy_CancellationStopsTheCopyAsCancellationRatherThanARefusal(bool duringCopy)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        if (!duringCopy)
            await cancellation.CancelAsync();
        using var staged = new GrowingStream(Bound - 10, each: 4096, appends: -1, largestRead: 100);
        staged.AfterRead = () => cancellation.Cancel();
        using var snapshot = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SiloImageSnapshot.CopyStagedAsync(staged, snapshot, Bound, Name, cancellation.Token));

        Assert.AreEqual(duringCopy ? 100L : 0L, staged.Served, "Nothing is read once cancellation is requested.");
        Assert.AreEqual(0L, snapshot.Length, "Nothing is written once cancellation is requested.");
    }

    [TestMethod]
    public async Task Snapshot_RefusesAStagedFileOverTheRealBoundAndKeepsNothing()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-silo-bound-");
        try
        {
            var staged = Path.Combine(directory.FullName, Name);
            await using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                file.SetLength(ReleaseImageArchive.MaximumSiloArchiveBytes + 1); // Sparse: nothing is written.

            var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                SiloImageSnapshot.CreateAsync(staged, SiloHarness.Amd64, TestContext.CancellationToken));

            Assert.IsTrue(refusal.IsRefusal);
            Assert.AreEqual($"The staged Silo archive {Name} is larger than any image of the pinned release.", refusal.Message);
            Assert.IsFalse(Directory.EnumerateDirectories(Path.GetTempPath(), "hvo-silo-image-*").Any(path =>
                File.Exists(Path.Combine(path, "archive.tar"))));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>How the copy ends: null on success, or the refusal, runaway or cancellation it ended with.</summary>
    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception) when (exception is InstallerException or InvalidOperationException or OperationCanceledException)
        {
            return exception;
        }
    }

    /// <summary>
    /// A staged file of <c>length</c> deterministic bytes, read at most <c>largestRead</c> bytes at a time. Each time the
    /// reader reaches its end, an appender adds <c>each</c> more bytes, <c>appends</c> times (-1: without end), as a
    /// concurrent writer would after its length was read. An appender that serves 1 MiB is a runaway and fails the read.
    /// </summary>
    private sealed class GrowingStream(long length, long each, int appends, int largestRead) : Stream
    {
        private const long Runaway = 1024 * 1024;
        private long visible = length;
        private long position;
        private int appended;

        public long Served => position;

        public Action? AfterRead { get; set; }

        public static byte[] Expected(long length)
        {
            var bytes = new byte[length];
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] = At(index);
            return bytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => visible;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            if (position == visible && (appends < 0 || appended < appends))
            {
                visible += each;
                appended++;
            }
            if (position >= Runaway)
                throw new InvalidOperationException($"The appender ran away: {position} bytes served.");
            var count = (int)Math.Min(Math.Min(buffer.Length, largestRead), visible - position);
            for (var index = 0; index < count; index++)
                buffer[index] = At(position + index);
            position += count;
            AfterRead?.Invoke();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static byte At(long index) => unchecked((byte)((index * 31) + 7));
    }
}
