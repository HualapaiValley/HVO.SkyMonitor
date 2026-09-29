using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.SiteProfile;

/// <summary>
/// Covers the operator site profile record: whole-profile replacement under an expected version, idempotent
/// replay, normalization and bounds, bounded retention, restart durability, and refusal of damaged records.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class FileSiteProfileStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, 123, TimeSpan.Zero);

    private static readonly SiteProfileValues Profile = new(
        "Hualapai Valley Observatory", "East dome", "Pat Example", "owner@home.lan");

    private string _root = null!;
    private MutableTimeProvider _timeProvider = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-site-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _timeProvider = new MutableTimeProvider(Now);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string RecordPath => Path.Combine(_root, "data", ".site", "site-profile.v1.json");

    [TestMethod]
    public async Task GetAsync_BeforeAnyProfile_ReportsVersionZeroAndTheInstallerName()
    {
        using var store = CreateStore(displayName: "  hvo-cam-01 ");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0L, state.Version);
        Assert.AreEqual(SiteProfileValues.Empty, state.Profile);
        Assert.AreEqual("hvo-cam-01", state.EffectiveCameraName);
        Assert.AreEqual("hvo-cam-01", state.ConfiguredCameraName);
        Assert.IsNull(state.UpdatedAtUtc);
        Assert.IsNull(state.UpdatedBy);
        Assert.IsEmpty(state.History);
        Assert.IsFalse(File.Exists(RecordPath));
    }

    [TestMethod]
    public async Task ApplyAsync_RecordsANormalizedRevisionThatSurvivesRestart()
    {
        using (var store = CreateStore(displayName: "hvo-cam-01"))
        {
            var result = await store.ApplyAsync(
                new SiteProfileRequest(
                    new SiteProfileValues("  Hualapai Valley Observatory ", "   ", "Pat Example", null),
                    0,
                    " key-1 ",
                    " owner-id ",
                    "  first light  "),
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(SiteProfileStatus.Applied, result.Status);
            Assert.AreEqual(1L, result.State.Version);
            Assert.AreEqual(
                new SiteProfileValues("Hualapai Valley Observatory", null, "Pat Example", null),
                result.State.Profile);
            // A blank camera name falls back to the installer's name rather than showing nothing.
            Assert.AreEqual("hvo-cam-01", result.State.EffectiveCameraName);
        }

        using var restarted = CreateStore(displayName: "hvo-cam-01");
        var state = await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1L, state.Version);
        Assert.AreEqual("Hualapai Valley Observatory", state.Profile.ObservatoryName);
        Assert.AreEqual("owner-id", state.UpdatedBy);
        // Stored timestamps are millisecond precision so a reload round-trips them exactly.
        Assert.AreEqual(new DateTimeOffset(2026, 7, 23, 12, 0, 0, 123, TimeSpan.Zero), state.UpdatedAtUtc);
        var revision = state.History.Single();
        Assert.AreEqual("key-1", revision.IdempotencyKey);
        Assert.AreEqual("first light", revision.Reason);
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(RecordPath));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(RecordPath)!));
        }
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(RecordPath)!, "*.tmp"));
    }

    [TestMethod]
    public async Task ApplyAsync_RecordedCameraNameWinsOverTheInstallerName()
    {
        using var store = CreateStore(displayName: "hvo-cam-01");

        var result = await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);

        Assert.AreEqual("East dome", result.State.EffectiveCameraName);
        Assert.AreEqual("hvo-cam-01", result.State.ConfiguredCameraName);
    }

    [TestMethod]
    public async Task ApplyAsync_ReplayingAKey_ReturnsTheRecordedOutcomeOrAConflict()
    {
        using var store = CreateStore();
        await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);

        // A retry after a lost response carries the same key and the version the operator originally read.
        var replay = await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);
        var reused = await ApplyAsync(store, Profile with { OwnerName = "Sam Example" }, 1, "key-1").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Replayed, replay.Status);
        Assert.AreEqual(1L, replay.State.Version);
        Assert.AreEqual(SiteProfileStatus.Conflict, reused.Status);
        Assert.AreEqual(SiteProfileLimits.IdempotencyKeyConflictReasonCode, reused.ReasonCode);
        Assert.AreEqual("idempotencyKey", reused.FieldPath);
        Assert.AreEqual(1L, reused.State.Version);
        Assert.AreEqual("Pat Example", reused.State.Profile.OwnerName);
    }

    [TestMethod]
    public async Task ApplyAsync_WithAStaleVersion_ConflictsWithoutChangingTheRecord()
    {
        using var store = CreateStore();
        await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);

        var stale = await ApplyAsync(store, Profile with { OwnerName = "Sam Example" }, 0, "key-2").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Conflict, stale.Status);
        Assert.AreEqual(SiteProfileLimits.ExpectedVersionConflictReasonCode, stale.ReasonCode);
        Assert.AreEqual("expectedVersion", stale.FieldPath);
        Assert.AreEqual(1L, stale.State.Version);
        Assert.AreEqual("Pat Example", stale.State.Profile.OwnerName);
    }

    [TestMethod]
    public async Task ApplyAsync_WithTheCurrentValues_IsUnchangedAndWritesNothing()
    {
        using var store = CreateStore();

        var empty = await ApplyAsync(store, new SiteProfileValues(" ", null, "", null), 0, "key-0").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Unchanged, empty.Status);
        Assert.AreEqual(0L, empty.State.Version);
        Assert.IsFalse(File.Exists(RecordPath));

        await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);
        var written = File.GetLastWriteTimeUtc(RecordPath);
        var same = await ApplyAsync(store, Profile with { OwnerName = " Pat Example " }, 1, "key-2").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Unchanged, same.Status);
        Assert.AreEqual(1L, same.State.Version);
        Assert.HasCount(1, same.State.History);
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(RecordPath));
    }

    [TestMethod]
    [DataRow("observatory", "observatoryName")]
    [DataRow("camera", "cameraName")]
    [DataRow("owner", "ownerName")]
    [DataRow("contact", "ownerContact")]
    [DataRow("control", "ownerContact")]
    public async Task ApplyAsync_WithAnOutOfBoundsField_IsInvalidAndNamesTheField(string scenario, string field)
    {
        using var store = CreateStore();
        var longName = new string('n', SiteProfileLimits.MaximumNameLength + 1);
        var profile = scenario switch
        {
            "observatory" => Profile with { ObservatoryName = longName },
            "camera" => Profile with { CameraName = longName },
            "owner" => Profile with { OwnerName = longName },
            "contact" => Profile with { OwnerContact = new string('c', SiteProfileLimits.MaximumContactLength + 1) },
            _ => Profile with { OwnerContact = "owner@home.lan\nBcc: someone@example.test" }
        };

        var result = await ApplyAsync(store, profile, 0, "key-1").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Invalid, result.Status);
        Assert.AreEqual(SiteProfileLimits.InvalidFieldReasonCode, result.ReasonCode);
        Assert.AreEqual(field, result.FieldPath);
        Assert.AreEqual(0L, result.State.Version);
        Assert.IsFalse(File.Exists(RecordPath));
    }

    [TestMethod]
    public async Task ApplyAsync_AtTheFieldBounds_IsAccepted()
    {
        using var store = CreateStore();
        var profile = new SiteProfileValues(
            new string('o', SiteProfileLimits.MaximumNameLength),
            new string('c', SiteProfileLimits.MaximumNameLength),
            new string('w', SiteProfileLimits.MaximumNameLength),
            new string('@', SiteProfileLimits.MaximumContactLength));

        var result = await ApplyAsync(store, profile, 0, "key-1").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Applied, result.Status);
        Assert.AreEqual(profile, result.State.Profile);
    }

    [TestMethod]
    [DataRow("actor")]
    [DataRow("key")]
    [DataRow("reason")]
    [DataRow("actor control")]
    [DataRow("key control")]
    [DataRow("reason control")]
    public async Task ApplyAsync_WithAMalformedCommand_IsInvalid(string scenario)
    {
        using var store = CreateStore();
        var request = scenario switch
        {
            "actor" => new SiteProfileRequest(Profile, 0, "key-1", " ", null),
            "key" => new SiteProfileRequest(Profile, 0, new string('k', SiteProfileLimits.MaximumIdempotencyKeyLength + 1), "owner-id", null),
            "reason" => new SiteProfileRequest(Profile, 0, "key-1", "owner-id", new string('r', SiteProfileLimits.MaximumReasonLength + 1)),
            "actor control" => new SiteProfileRequest(Profile, 0, "key-1", "owner\u0007id", null),
            "key control" => new SiteProfileRequest(Profile, 0, "key\n1", "owner-id", null),
            _ => new SiteProfileRequest(Profile, 0, "key-1", "owner-id", "first\u0000light")
        };

        var result = await store.ApplyAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Invalid, result.Status);
        Assert.AreEqual(SiteProfileLimits.InvalidCommandReasonCode, result.ReasonCode);
        Assert.AreEqual("command", result.FieldPath);
        Assert.IsFalse(File.Exists(RecordPath));
    }

    [TestMethod]
    public async Task ApplyAsync_KeepsABoundedHistoryAndProjectsOnlyTheNewestRevisions()
    {
        const int applied = SiteProfileLimits.MaximumRetainedRevisions + 1;
        using (var store = CreateStore())
        {
            for (var version = 1; version <= applied; version++)
            {
                _timeProvider.UtcNow = Now.AddMinutes(version);
                var result = await ApplyAsync(
                    store, Profile with { OwnerName = $"Owner {version}" }, version - 1, $"key-{version}").ConfigureAwait(false);
                Assert.AreEqual(SiteProfileStatus.Applied, result.Status);
            }
        }

        var stored = JsonNode.Parse(await File.ReadAllTextAsync(RecordPath).ConfigureAwait(false))!;
        Assert.HasCount(SiteProfileLimits.MaximumRetainedRevisions, stored["revisions"]!.AsArray());

        using var restarted = CreateStore();
        var state = await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual((long)applied, state.Version);
        Assert.HasCount(SiteProfileLimits.MaximumProjectedRevisions, state.History);
        Assert.AreEqual((long)applied, state.History[0].Version);
        Assert.AreEqual((long)applied - SiteProfileLimits.MaximumProjectedRevisions + 1, state.History[^1].Version);
        // The first key fell out of the retained window, so it is a new command rather than a replay.
        var reused = await ApplyAsync(restarted, Profile with { OwnerName = "Reused" }, applied, "key-1").ConfigureAwait(false);
        Assert.AreEqual(SiteProfileStatus.Applied, reused.Status);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("unknown member")]
    [DataRow("version mismatch")]
    [DataRow("profile mismatch")]
    [DataRow("out of order")]
    [DataRow("schema")]
    [DataRow("older revision profile")]
    [DataRow("older revision actor")]
    [DataRow("older revision reason")]
    [DataRow("missing revision")]
    public async Task GetAsync_WithADamagedRecord_RefusesToGuess(string scenario)
    {
        using (var store = CreateStore())
        {
            await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);
            await ApplyAsync(store, Profile with { OwnerName = "Sam Example" }, 1, "key-2").ConfigureAwait(false);
        }
        var record = JsonNode.Parse(await File.ReadAllTextAsync(RecordPath).ConfigureAwait(false))!.AsObject();
        var revisions = record["revisions"]!.AsArray();
        switch (scenario)
        {
            case "not json":
                await File.WriteAllTextAsync(RecordPath, "{ \"schemaVersion\": 1,").ConfigureAwait(false);
                break;
            case "unknown member":
                record["extra"] = true;
                break;
            case "version mismatch":
                record["version"] = 3;
                break;
            case "profile mismatch":
                record["profile"]!["ownerName"] = "Someone else";
                break;
            case "out of order":
                revisions[0]!["version"] = 2;
                break;
            case "older revision profile":
                // Only the latest revision mirrors the current profile, so every retained one is checked on its own.
                revisions[0]!["profile"]!["ownerName"] = new string('o', SiteProfileLimits.MaximumNameLength + 1);
                break;
            case "older revision actor":
                revisions[0]!["actor"] = new string('a', SiteProfileLimits.MaximumActorLength + 1);
                break;
            case "older revision reason":
                revisions[0]!["reason"] = "first\u0001light";
                break;
            case "missing revision":
                revisions[0] = null;
                break;
            default:
                record["schemaVersion"] = 2;
                break;
        }
        if (scenario != "not json")
        {
            await File.WriteAllTextAsync(RecordPath, record.ToJsonString()).ConfigureAwait(false);
        }

        using var restarted = CreateStore();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_AfterAFailureFollowingPublication_ReadsThePublishedRevisionBack()
    {
        var failures = 1;
        using var store = CreateStore(syncPublishedDirectory: _ =>
        {
            if (failures-- > 0)
            {
                throw new IOException("Directory sync failed after the replace.");
            }
        });

        // The replace lands but the directory sync after it fails, so the caller sees an error for a revision
        // that is in fact on disk.
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false)).ConfigureAwait(false);

        // A different command still holding version zero must meet the published revision, not overwrite it.
        var stale = await ApplyAsync(store, Profile with { OwnerName = "Sam Example" }, 0, "key-2").ConfigureAwait(false);
        Assert.AreEqual(SiteProfileStatus.Conflict, stale.Status);
        Assert.AreEqual(SiteProfileLimits.ExpectedVersionConflictReasonCode, stale.ReasonCode);
        Assert.AreEqual(1L, stale.State.Version);
        // Retrying the failed command is recognised as already recorded.
        var retried = await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false);
        Assert.AreEqual(SiteProfileStatus.Replayed, retried.Status);

        using var restarted = CreateStore();
        var state = await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, state.Version);
        Assert.AreEqual(Profile, state.Profile);
        Assert.AreEqual("key-1", state.History.Single().IdempotencyKey);
    }

    [TestMethod]
    public async Task ApplyAsync_RefusesARecordDirectoryReachedThroughASymbolicLink()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Symbolic link creation needs elevated rights on Windows.");
        }
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "data", ".site"), elsewhere);
        using var store = CreateStore();

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await ApplyAsync(store, Profile, 0, "key-1").ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsEmpty(Directory.GetFileSystemEntries(elsewhere));
    }

    private FileSiteProfileStore CreateStore(string? displayName = null, Action<string>? syncPublishedDirectory = null)
        => new(
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Path.Combine(_root, "data"),
                DisplayName = displayName
            }),
            _timeProvider,
            NullLogger<FileSiteProfileStore>.Instance)
        {
            SyncPublishedDirectory = syncPublishedDirectory ?? RawIngressFileStore.SyncDirectory
        };

    private static ValueTask<SiteProfileResult> ApplyAsync(
        FileSiteProfileStore store,
        SiteProfileValues profile,
        long expectedVersion,
        string key)
        => store.ApplyAsync(
            new SiteProfileRequest(profile, expectedVersion, key, "owner-id", null),
            CancellationToken.None);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
