using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.SiteProfile;

/// <summary>
/// Covers the site profile kept in the operator settings file: whole-profile replacement under the file's version,
/// normalization and bounds, preservation of the operator's other settings, the one-time move of an installer name
/// and an earlier release's record into the file, and refusal to overwrite a file the operator must correct.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SettingsFileSiteProfileStoreTests
{
    private static readonly SiteProfileValues Profile = new(
        "Hualapai Valley Observatory", "East dome", "Pat Example", "owner@home.lan");

    private string _root = null!;
    private int _reloads;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-site-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _reloads = 0;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string InstallerSeed = """
        {
          "CameraAgent": {
            "DisplayName": "North Camera"
          }
        }

        """;

    private string SettingsPath => Path.Combine(_root, "appsettings.local.json");

    private string LegacyRecordPath => Path.Combine(_root, "data", ".site", "site-profile.v1.json");

    [TestMethod]
    public async Task WithoutASettingsFile_ShowsTheInstallerNameAndRefusesToSave()
    {
        using var store = CreateStore(displayName: "  hvo-cam-01 ", withFile: false);

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await ApplyAsync(store, Profile, state.Version).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsFile.AbsentVersion, state.Version);
        Assert.AreEqual(SiteProfileValues.Empty, state.Profile);
        Assert.AreEqual("hvo-cam-01", state.EffectiveCameraName);
        Assert.IsNull(state.SettingsFilePath);
        Assert.IsNotNull(state.Problem);
        Assert.AreEqual(SiteProfileStatus.Invalid, result.Status);
        Assert.AreEqual(SiteProfileLimits.UnavailableReasonCode, result.ReasonCode);
    }

    [TestMethod]
    public async Task GetAsync_BeforeAnyProfile_MovesTheInstallerNameIntoTheFile()
    {
        using var store = CreateStore(displayName: "  hvo-cam-01 ");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new SiteProfileValues(null, "hvo-cam-01", null, null), state.Profile);
        Assert.AreEqual("hvo-cam-01", state.EffectiveCameraName);
        Assert.AreEqual(SettingsPath, state.SettingsFilePath);
        Assert.IsNull(state.Problem);
        Assert.AreEqual("hvo-cam-01", ReadSetting("CameraAgent", "Site", "CameraName"));
        Assert.AreEqual(1, _reloads);
    }

    [TestMethod]
    public async Task GetAsync_WithNothingToMove_LeavesTheFileAbsent()
    {
        using var store = CreateStore();

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsFile.AbsentVersion, state.Version);
        Assert.AreEqual(SiteProfileValues.Empty, state.Profile);
        Assert.IsNull(state.EffectiveCameraName);
        Assert.IsFalse(File.Exists(SettingsPath));
    }

    [TestMethod]
    public async Task ApplyAsync_SavesANormalizedProfileBesideTheOperatorsOtherSettings()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            {
              // Written by hand.
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "CameraAgent": { "SkyMap": { "MaximumObjects": 900 } },
            }
            """).ConfigureAwait(false);
        using (var store = CreateStore(displayName: "hvo-cam-01"))
        {
            var before = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await ApplyAsync(
                    store,
                    new SiteProfileValues("  Hualapai Valley Observatory ", " East dome ", "Pat Example", "   "),
                    before.Version)
                .ConfigureAwait(false);

            Assert.AreEqual(SiteProfileStatus.Applied, result.Status);
            Assert.AreEqual(
                new SiteProfileValues("Hualapai Valley Observatory", "East dome", "Pat Example", null),
                result.State.Profile);
            Assert.AreEqual("East dome", result.State.EffectiveCameraName);
            Assert.AreNotEqual(before.Version, result.State.Version);
        }

        using var restarted = CreateStore(displayName: "hvo-cam-01");
        var state = await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("Hualapai Valley Observatory", state.Profile.ObservatoryName);
        Assert.AreEqual("East dome", state.EffectiveCameraName);
        Assert.AreEqual("Warning", ReadSetting("Logging", "LogLevel", "Default"));
        Assert.AreEqual("900", ReadSetting("CameraAgent", "SkyMap", "MaximumObjects"));
        Assert.IsNull(ReadSetting("CameraAgent", "Site", "OwnerContact"));
    }

    [TestMethod]
    public async Task ApplyAsync_ClearingEveryField_KeepsTheInstallerNameFromReturning()
    {
        using (var store = CreateStore(displayName: "hvo-cam-01"))
        {
            var before = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await ApplyAsync(store, SiteProfileValues.Empty, before.Version).ConfigureAwait(false);

            Assert.AreEqual(SiteProfileStatus.Applied, result.Status);
            Assert.AreEqual(SiteProfileValues.Empty, result.State.Profile);
            Assert.IsNull(result.State.EffectiveCameraName);
        }

        using var restarted = CreateStore(displayName: "hvo-cam-01");
        var state = await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(state.EffectiveCameraName);
        Assert.IsNotNull(JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false))!["CameraAgent"]!["Site"]);
    }

    [TestMethod]
    public async Task ApplyAsync_AfterAHandEdit_ConflictsWithoutOverwritingIt()
    {
        using var store = CreateStore();
        var before = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);
        const string handEdit = """{ "CameraAgent": { "Site": { "ObservatoryName": "Edited by hand" } } }""";
        await File.WriteAllTextAsync(SettingsPath, handEdit).ConfigureAwait(false);

        var result = await ApplyAsync(store, Profile, before.Version).ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Conflict, result.Status);
        Assert.AreEqual(SiteProfileLimits.ExpectedVersionConflictReasonCode, result.ReasonCode);
        Assert.AreEqual("Edited by hand", result.State.Profile.ObservatoryName);
        Assert.AreEqual(handEdit, await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));
        Assert.AreEqual(0, _reloads);
    }

    [TestMethod]
    public async Task ApplyAsync_WithTheCurrentValues_IsUnchangedAndWritesNothing()
    {
        using var store = CreateStore();
        var saved = await ApplyAsync(store, Profile, OperatorSettingsFile.AbsentVersion).ConfigureAwait(false);
        var written = (await File.ReadAllBytesAsync(SettingsPath).ConfigureAwait(false));

        var result = await ApplyAsync(store, Profile with { CameraName = " East dome " }, "stale").ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Unchanged, result.Status);
        Assert.AreEqual(saved.State.Version, result.State.Version);
        CollectionAssert.AreEqual(written, await File.ReadAllBytesAsync(SettingsPath).ConfigureAwait(false));
        Assert.AreEqual(1, _reloads);
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

        var result = await ApplyAsync(store, profile, OperatorSettingsFile.AbsentVersion).ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Invalid, result.Status);
        Assert.AreEqual(SiteProfileLimits.InvalidFieldReasonCode, result.ReasonCode);
        Assert.AreEqual(field, result.FieldPath);
        Assert.IsFalse(File.Exists(SettingsPath));
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

        var result = await ApplyAsync(store, profile, OperatorSettingsFile.AbsentVersion).ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Applied, result.Status);
        Assert.AreEqual(profile, result.State.Profile);
    }

    [TestMethod]
    [DataRow("actor")]
    [DataRow("actor length")]
    [DataRow("actor control")]
    [DataRow("version")]
    public async Task ApplyAsync_WithAMalformedCommand_IsInvalid(string scenario)
    {
        using var store = CreateStore();
        var request = scenario switch
        {
            "actor" => new SiteProfileRequest(Profile, OperatorSettingsFile.AbsentVersion, " "),
            "actor length" => new SiteProfileRequest(
                Profile, OperatorSettingsFile.AbsentVersion, new string('a', SiteProfileLimits.MaximumActorLength + 1)),
            "actor control" => new SiteProfileRequest(Profile, OperatorSettingsFile.AbsentVersion, "owner\u0007id"),
            _ => new SiteProfileRequest(Profile, null!, "owner-id")
        };

        var result = await store.ApplyAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(SiteProfileStatus.Invalid, result.Status);
        Assert.AreEqual(SiteProfileLimits.InvalidCommandReasonCode, result.ReasonCode);
        Assert.AreEqual("command", result.FieldPath);
        Assert.IsFalse(File.Exists(SettingsPath));
    }

    [TestMethod]
    public async Task AnUnreadableFile_IsReportedAndLeftForTheOperatorToCorrect()
    {
        const string broken = """{ "CameraAgent": { "Site": { "CameraName": "East dome" }""";
        await File.WriteAllTextAsync(SettingsPath, broken).ConfigureAwait(false);
        using var store = CreateStore(displayName: "hvo-cam-01");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await ApplyAsync(store, Profile, state.Version).ConfigureAwait(false);

        Assert.IsNotNull(state.Problem);
        Assert.AreEqual("hvo-cam-01", state.EffectiveCameraName);
        Assert.AreEqual(SiteProfileStatus.Invalid, result.Status);
        Assert.AreEqual(SiteProfileLimits.UnreadableReasonCode, result.ReasonCode);
        Assert.AreEqual(broken, await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));

        // Once corrected, the installer name moves in as it would have at first.
        await File.WriteAllTextAsync(SettingsPath, """{ "Logging": {} }""").ConfigureAwait(false);
        var corrected = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(corrected.Problem);
        Assert.AreEqual("hvo-cam-01", corrected.Profile.CameraName);
    }

    [TestMethod]
    public async Task GetAsync_ReadsAHandEditWithoutARestart()
    {
        using var store = CreateStore();
        _ = await ApplyAsync(store, Profile, OperatorSettingsFile.AbsentVersion).ConfigureAwait(false);

        await File.WriteAllTextAsync(SettingsPath, """{ "CameraAgent": { "Site": { "cameraName": "  West dome " } } }""").ConfigureAwait(false);
        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new SiteProfileValues(null, "West dome", null, null), state.Profile);
    }

    [TestMethod]
    public async Task GetAsync_MovesAnEarlierReleasesRecordIntoTheFileAndRemovesIt()
    {
        WriteLegacyRecord("""
            { "schemaVersion": 1, "version": 3,
              "profile": { "observatoryName": " Hualapai Valley Observatory ", "cameraName": null, "ownerName": "Pat Example", "ownerContact": "owner@home.lan" },
              "revisions": [] }
            """);
        using var store = CreateStore(displayName: "hvo-cam-01");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(
            new SiteProfileValues("Hualapai Valley Observatory", "hvo-cam-01", "Pat Example", "owner@home.lan"),
            state.Profile);
        Assert.IsFalse(File.Exists(LegacyRecordPath));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(LegacyRecordPath)));
    }

    [TestMethod]
    public async Task GetAsync_AfterAnInstallerSeed_MovesTheSeededNameIntoTheSite()
    {
        // The file exactly as the installer seeds it; the host loads its name as CameraAgent:DisplayName.
        await File.WriteAllTextAsync(SettingsPath, InstallerSeed).ConfigureAwait(false);
        using var store = CreateStore(displayName: "North Camera");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new SiteProfileValues(null, "North Camera", null, null), state.Profile);
        Assert.AreEqual("North Camera", ReadSetting("CameraAgent", "Site", "CameraName"));
        Assert.IsNull(ReadSetting("CameraAgent", "DisplayName"));
    }

    [TestMethod]
    public async Task GetAsync_AfterAnInstallerSeed_KeepsTheEarlierReleasesProfile()
    {
        // An instance installed before the settings file existed is reinstalled: the installer seeds a new file
        // while the profile the operator recorded under the earlier release is still on disk.
        await File.WriteAllTextAsync(SettingsPath, InstallerSeed).ConfigureAwait(false);
        WriteLegacyRecord("""
            { "profile": { "observatoryName": "Recorded earlier", "cameraName": "East dome", "ownerName": "Pat Example", "ownerContact": "owner@home.lan" } }
            """);
        using var store = CreateStore(displayName: "North Camera");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(
            new SiteProfileValues("Recorded earlier", "East dome", "Pat Example", "owner@home.lan"),
            state.Profile);
        Assert.AreEqual("Recorded earlier", ReadSetting("CameraAgent", "Site", "ObservatoryName"));
        Assert.IsNull(ReadSetting("CameraAgent", "DisplayName"));
        Assert.IsFalse(File.Exists(LegacyRecordPath));
    }

    [TestMethod]
    public async Task GetAsync_AfterAnInstallerSeedAndAHandWrittenSite_KeepsTheSiteAndRemovesTheSeededName()
    {
        // The operator wrote a site section by hand into the installer's seed before CameraAgent first read it.
        await File.WriteAllTextAsync(SettingsPath, """
            { "CameraAgent": { "DisplayName": "North Camera", "Site": { "ObservatoryName": "Written by hand", "CameraName": "East dome" } } }
            """).ConfigureAwait(false);
        var logger = new Mock<ILogger<SettingsFileSiteProfileStore>>();
        logger.Setup(item => item.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        using var store = CreateStore(displayName: "North Camera", logger: logger.Object);

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new SiteProfileValues("Written by hand", "East dome", null, null), state.Profile);
        Assert.AreEqual("East dome", ReadSetting("CameraAgent", "Site", "CameraName"));
        Assert.IsNull(ReadSetting("CameraAgent", "DisplayName"));
        // The profile was already in the file, so the log names the removed seed rather than a move.
        Assert.AreEqual(1, LoggedEvents(logger, 7604), "The seeded name removal was not logged.");
        Assert.AreEqual(0, LoggedEvents(logger, 7601), "The removal was logged as a site profile move.");
    }

    [TestMethod]
    public async Task GetAsync_WhenTheFileAlreadyHasASiteSection_KeepsItAndRemovesTheEarlierRecord()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "CameraAgent": { "Site": { "CameraName": "Written by hand" } } }""").ConfigureAwait(false);
        WriteLegacyRecord("""{ "profile": { "observatoryName": "Recorded earlier", "cameraName": "Old name" } }""");
        using var store = CreateStore(displayName: "hvo-cam-01");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new SiteProfileValues(null, "Written by hand", null, null), state.Profile);
        Assert.IsFalse(File.Exists(LegacyRecordPath));
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("no profile")]
    [DataRow("out of bounds")]
    public async Task GetAsync_WithAnUnreadableEarlierRecord_LeavesItInPlace(string scenario)
    {
        WriteLegacyRecord(scenario switch
        {
            "not json" => "{ \"profile\": ",
            "no profile" => """{ "version": 2 }""",
            _ => $$"""{ "profile": { "cameraName": "{{new string('c', SiteProfileLimits.MaximumNameLength + 1)}}" } }"""
        });
        using var store = CreateStore(displayName: "hvo-cam-01");

        var state = await store.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new SiteProfileValues(null, "hvo-cam-01", null, null), state.Profile);
        Assert.IsTrue(File.Exists(LegacyRecordPath));
    }

    [TestMethod]
    public async Task ApplyAsync_RefusesASettingsFileThatIsASymbolicLink()
    {
        var elsewhere = Path.Combine(_root, "elsewhere.json");
        await File.WriteAllTextAsync(elsewhere, "{}").ConfigureAwait(false);
        File.CreateSymbolicLink(SettingsPath, elsewhere);
        using var store = CreateStore();

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await ApplyAsync(store, Profile, OperatorSettingsFile.AbsentVersion).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual("{}", await File.ReadAllTextAsync(elsewhere).ConfigureAwait(false));
    }

    private void WriteLegacyRecord(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyRecordPath)!);
        File.WriteAllText(LegacyRecordPath, json);
    }

    private string? ReadSetting(params string[] path)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(SettingsPath));
        foreach (var segment in path)
        {
            node = node?[segment];
        }
        return node?.ToString();
    }

    private static int LoggedEvents(Mock<ILogger<SettingsFileSiteProfileStore>> logger, int eventId)
        => logger.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(ILogger.Log) && invocation.Arguments[1] is EventId logged && logged.Id == eventId);

    private SettingsFileSiteProfileStore CreateStore(
        string? displayName = null,
        bool withFile = true,
        ILogger<SettingsFileSiteProfileStore>? logger = null)
        => new(
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Path.Combine(_root, "data"),
                DisplayName = displayName
            }),
            logger ?? NullLogger<SettingsFileSiteProfileStore>.Instance,
            withFile ? new OperatorSettingsFile(SettingsPath, () => _reloads++, _ => []) : null);

    private static ValueTask<SiteProfileResult> ApplyAsync(
        SettingsFileSiteProfileStore store,
        SiteProfileValues profile,
        string expectedVersion)
        => store.ApplyAsync(new SiteProfileRequest(profile, expectedVersion, "owner-id"), CancellationToken.None);
}
