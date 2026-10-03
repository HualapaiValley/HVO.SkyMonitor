using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
public sealed class ManualFocusSessionStoreTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
    private static readonly string[] NewestThree =
        ["20261002T140300Z-session-3", "20261002T140200Z-session-2", "20261002T140100Z-session-1"];
    private string _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Path.Combine(Path.GetTempPath(), "hvo-focus-store-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Save_PublishesAChecksummedRecordThatReadsBackExactly()
    {
        using var store = new ManualFocusSessionStore(_root);
        var record = Record("session-a", SavedAt);

        var summary = await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        var content = await store.ReadAsync(record.RecordId, CancellationToken.None).ConfigureAwait(false);

        var directory = Path.Combine(_root, ManualFocusSessionStore.DirectoryName);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, record.RecordId + ".json")).ConfigureAwait(false);
        var sidecar = await File.ReadAllTextAsync(Path.Combine(directory, record.RecordId + ".sha256")).ConfigureAwait(false);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.AreEqual("20261002T140000Z-session-a", record.RecordId);
        Assert.AreEqual($"{sha256}  {record.RecordId}.json\n", sidecar, "The sidecar is sha256sum-compatible.");
        Assert.AreEqual(sha256, summary.Sha256);
        Assert.IsTrue(summary.Verified);
        Assert.AreEqual(bytes.Length, summary.Bytes);
        CollectionAssert.AreEqual(bytes, content!.Utf8Json.ToArray());
        Assert.AreEqual(ManualFocusRetentionState.Saved, content.Record.Session.Retention);
        Assert.AreEqual(record.RecordId, content.Record.Session.SavedRecordId);
        Assert.AreEqual(ManualFocusSessionRecord.Statement, content.Record.RetentionStatement);
        Assert.AreEqual("alice", content.Summary.SavedBy);
        Assert.AreEqual(ManualFocusSessionState.Stopped, content.Summary.State);
        Assert.IsFalse(Directory.EnumerateFiles(directory).Any(static path => !path.EndsWith(".json", StringComparison.Ordinal) &&
            !path.EndsWith(".sha256", StringComparison.Ordinal)), "No publication temporaries remain.");
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(record, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TamperedRecords_AreNeverReturnedAsVerified()
    {
        using var store = new ManualFocusSessionStore(_root);
        var record = Record("session-a", SavedAt);
        await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        var path = Path.Combine(_root, ManualFocusSessionStore.DirectoryName, record.RecordId + ".json");
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, text.Replace("alice", "mallo", StringComparison.Ordinal)).ConfigureAwait(false);

        var listed = await store.ListAsync(10, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, listed);
        Assert.IsFalse(listed[0].Verified);
        Assert.IsNull(listed[0].SavedBy, "Nothing from an unverified record is surfaced.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.ReadAsync(record.RecordId, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Save_PrunesTheOldestRecordsAndInterruptedSavesBeyondTheBound()
    {
        using var store = new ManualFocusSessionStore(_root, maximumRecords: 3);
        for (var index = 0; index < 3; index++)
        {
            await store.SaveAsync(Record($"session-{index}", SavedAt.AddMinutes(index)), CancellationToken.None).ConfigureAwait(false);
        }
        var directory = Path.Combine(_root, ManualFocusSessionStore.DirectoryName);
        var interrupted = Record("interrupted", SavedAt.AddMinutes(10));
        await File.WriteAllTextAsync(Path.Combine(directory, interrupted.RecordId + ".json"), "{}").ConfigureAwait(false);

        await store.SaveAsync(Record("session-3", SavedAt.AddMinutes(3)), CancellationToken.None).ConfigureAwait(false);
        var listed = await store.ListAsync(10, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(NewestThree, listed.Select(static summary => summary.RecordId).ToArray());
        Assert.IsTrue(listed.All(static summary => summary.Verified));
        Assert.HasCount(6, Directory.GetFiles(directory), "Only the retained records and their sidecars remain.");
        Assert.HasCount(2, await store.ListAsync(2, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OversizedInvalidAndMissingRecords_AreRefused()
    {
        using var store = new ManualFocusSessionStore(_root);
        var oversized = Record("huge", SavedAt) is var record
            ? record with { Session = record.Session with { LastFailure = new string('x', ManualFocusSessionStore.MaximumRecordBytes) } }
            : null!;

        await Assert.ThrowsExactlyAsync<ManualFocusSessionStateException>(() =>
            store.SaveAsync(oversized, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            store.ReadAsync("../escape", CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            store.SaveAsync(oversized with { RecordId = "a/b" }, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await store.ReadAsync("20261002T140000Z-absent", CancellationToken.None).ConfigureAwait(false));
        Assert.IsEmpty(await store.ListAsync(10, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, ManualFocusSessionStore.DirectoryName)));
    }

    [TestMethod]
    public async Task LinkedRecordDirectory_IsRejected()
    {
        var elsewhere = _root + "-elsewhere";
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(elsewhere);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, ManualFocusSessionStore.DirectoryName), elsewhere);
            using var store = new ManualFocusSessionStore(_root);

            var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(() =>
                store.SaveAsync(Record("session-a", SavedAt), CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
            Assert.IsEmpty(Directory.GetFiles(elsewhere));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    private static ManualFocusSessionRecord Record(string sessionId, DateTimeOffset savedUtc)
        => ManualFocusSessionRecord.Create(ManualFocusSessionSnapshot.Idle with
        {
            SessionId = sessionId,
            State = ManualFocusSessionState.Stopped,
            OwnerId = "alice",
            StartedUtc = savedUtc.AddMinutes(-5),
            EndedUtc = savedUtc.AddMinutes(-1),
            EndReason = ManualFocusReasonCodes.StoppedByOperator,
            TotalSamples = 4,
            HistoryCapacity = 100,
            ModuleType = "VirtualSky"
        }, "alice", savedUtc);
}
