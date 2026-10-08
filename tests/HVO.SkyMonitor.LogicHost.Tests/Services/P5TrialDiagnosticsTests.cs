using System.Text.Json;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class P5TrialDiagnosticsTests
{
    private string _root = null!;
    private string DirectoryPath => Path.Combine(_root, "private");

    [TestInitialize]
    public void Initialize()
    {
        // The trusted temporary base may contain platform symlinks (for example /var on macOS).
        // Resolve those before creating the fixture; the writer must still reject links inside it.
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var physical = Path.GetPathRoot(temporary)!;
        foreach (var segment in temporary[physical.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(physical, segment));
            physical = (directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory).FullName;
        }
        _root = Path.Combine(physical, $"hvo-p5-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedPlateauRetainsReconstructableSamplesAndOriginalAssertion()
    {
        var samples = new long[] { 0, 0, 0, 10, 20, 30, 50, 60, 70 };
        var failure = await Assert.ThrowsAsync<AssertFailedException>(() => P5TrialDiagnostics.AssertPlateauAsync(
            false, "P5 RSS median growth 40 exceeded the 30-byte full-frame envelope.",
            () => Task.FromResult<object>(new { Trial = 3, Concurrency = 1, Samples = samples, Middle = 20, Final = 60 }),
            DirectoryPath, "trial.json")).ConfigureAwait(false);

        StringAssert.Contains(failure.Message, "P5 RSS median growth 40");
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(DirectoryPath, "trial.json")).ConfigureAwait(false));
        var retained = record.RootElement.GetProperty("samples").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        Assert.AreEqual(3, record.RootElement.GetProperty("trial").GetInt32());
        Assert.AreEqual(20L, retained.Skip(3).Take(3).Order().ElementAt(1));
        Assert.AreEqual(60L, retained.Skip(6).Order().ElementAt(1));
        Assert.IsNull(failure.InnerException);
    }

    [TestMethod]
    public async Task PassedPlateauDoesNotInvokeRecorderOrCreateOutput()
    {
        await P5TrialDiagnostics.AssertPlateauAsync(true, "unused",
            () => throw new InvalidOperationException("must never run"), DirectoryPath, "trial.json").ConfigureAwait(false);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public async Task CanceledRetentionPreservesFailedScientificAssertion()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(false);
        var failure = await Assert.ThrowsAsync<AssertFailedException>(() => P5TrialDiagnostics.AssertPlateauAsync(
            false, "original RSS failure", () => Task.FromResult<object>(new { Trial = 2 }),
            DirectoryPath, "trial.json", canceled.Token)).ConfigureAwait(false);
        StringAssert.Contains(failure.Message, "original RSS failure");
        Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
        Assert.IsInstanceOfType<OperationCanceledException>(((AggregateException)failure.InnerException!).InnerExceptions[1]);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public async Task RecordConstructionFailurePreservesScientificFailure()
    {
        var failure = await Assert.ThrowsAsync<AssertFailedException>(() => P5TrialDiagnostics.AssertPlateauAsync(
            false, "original RSS failure", () => throw new InvalidOperationException("fixture"),
            DirectoryPath, "trial.json")).ConfigureAwait(false);
        StringAssert.Contains(failure.Message, "original RSS failure");
        Assert.IsInstanceOfType<InvalidOperationException>(((AggregateException)failure.InnerException!).InnerExceptions[1]);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public async Task OversizedRecordRemovesStagingAndLeavesNoPublishedRecord()
    {
        await Assert.ThrowsAsync<IOException>(() => P5TrialDiagnostics.WriteAsync(
            DirectoryPath, "trial.json", new { Value = new string('x', 32_768) }, maximumBytes: 128)).ConfigureAwait(false);
        Assert.IsEmpty(Directory.GetFiles(DirectoryPath));
    }

    [TestMethod]
    public async Task CancellationDuringSerializationRemovesStaging()
    {
        using var stopping = new CancellationTokenSource();
        var record = new CancelSerialization(stopping);
        await Assert.ThrowsAsync<OperationCanceledException>(() => P5TrialDiagnostics.WriteAsync(
            DirectoryPath, "trial.json", record, cancellationToken: stopping.Token)).ConfigureAwait(false);
        Assert.IsEmpty(Directory.GetFiles(DirectoryPath));
    }

    [TestMethod]
    public async Task ExistingRecordIsNeverOverwritten()
    {
        await P5TrialDiagnostics.WriteAsync(DirectoryPath, "trial.json", new { Trial = 1 }).ConfigureAwait(false);
        var original = await File.ReadAllBytesAsync(Path.Combine(DirectoryPath, "trial.json")).ConfigureAwait(false);
        await Assert.ThrowsAsync<IOException>(() => P5TrialDiagnostics.WriteAsync(
            DirectoryPath, "trial.json", new { Trial = 2 })).ConfigureAwait(false);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(DirectoryPath, "trial.json")).ConfigureAwait(false));
        Assert.HasCount(1, Directory.GetFiles(DirectoryPath));
    }

    [TestMethod]
    public async Task PrivateDirectoryAndFileHaveOwnerOnlyModes()
    {
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => P5TrialDiagnostics.WriteAsync(
                DirectoryPath, "trial.json", new { Trial = 1 })).ConfigureAwait(false);
            return;
        }
        await P5TrialDiagnostics.WriteAsync(DirectoryPath, "trial.json", new { Trial = 1 }).ConfigureAwait(false);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(DirectoryPath));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(DirectoryPath, "trial.json")));
    }

    [TestMethod]
    public async Task ExistingNonprivateDirectoryIsRejectedWithoutChangingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Directory.CreateDirectory(DirectoryPath);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(DirectoryPath, mode);
        await Assert.ThrowsAsync<IOException>(() => P5TrialDiagnostics.WriteAsync(
            DirectoryPath, "trial.json", new { Trial = 1 })).ConfigureAwait(false);
        Assert.AreEqual(mode, File.GetUnixFileMode(DirectoryPath));
        Assert.IsEmpty(Directory.GetFiles(DirectoryPath));
    }

    [TestMethod]
    public async Task SymlinkAncestorIsRejectedWithoutWritingThroughIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), target);
        await Assert.ThrowsAsync<IOException>(() => P5TrialDiagnostics.WriteAsync(
            Path.Combine(_root, "link", "private"), "trial.json", new { Trial = 1 })).ConfigureAwait(false);
        Assert.IsEmpty(Directory.GetFileSystemEntries(target));
    }

    private sealed class CancelSerialization(CancellationTokenSource stopping)
    {
        public int Value
        {
            get
            {
                stopping.Cancel();
                return 1;
            }
        }
    }
}
