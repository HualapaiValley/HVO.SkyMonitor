using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Settings;

/// <summary>The settings service's values, sources and version-checked writes against real stores.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSettingsServiceTests
{
    private const string MaximumObjects = "skyMap.maximumObjects";
    private const string MinimumRefresh = "currentSky.minimumRefreshSeconds";

    [TestMethod]
    public async Task Get_ListsEveryGroupInCatalogueOrderWithItsValuesAndSourcesAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);

        var response = await harness.Service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            CameraAgentSettingsCatalogue.Groups.Select(group => group.Name).ToArray(),
            response.Groups.Select(group => group.Group).ToArray());
        var site = response.Groups.Single(group => group.Group == "site");
        Assert.IsTrue(site.CanWrite, site.ReadOnlyReason);
        // The first read moves the installation's camera name into the settings file, which then holds it.
        var camera = site.Settings.Single(setting => setting.Key == "site.cameraName");
        Assert.AreEqual(CameraAgentSettingsHarness.InstalledName, camera.Value!.Value.GetString());
        Assert.AreEqual(CameraAgentSettingSource.SettingsFile, camera.Source);
        var limit = Setting(response, MaximumObjects);
        Assert.AreEqual(CameraAgentSkyMapProjection.DefaultMaximumObjects, limit.Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.Default, limit.Source);
        Assert.IsFalse(limit.IsSet);
        var latitude = Setting(response, "location.latitudeDegrees");
        Assert.AreEqual(35d, latitude.Value!.Value.GetDouble());
        Assert.AreEqual(CameraAgentSettingSource.DeploymentLocation, latitude.Source);
        Assert.AreEqual("Version 3 (installer)", latitude.SourceDetail);
        var lighting = Setting(response, "camera.skyLighting");
        Assert.AreEqual(nameof(VirtualSkyIlluminationMode.LegacyScalarSolar), lighting.Value!.Value.GetString());
        Assert.AreEqual(CameraAgentSettingSource.RigRevision, lighting.Source);
        Assert.IsTrue(lighting.CanWrite, lighting.ReadOnlyReason);
        var schedule = Setting(response, "schedule.activeRevision");
        var active = await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active.ActiveRevision.RevisionId, schedule.Value!.Value.GetString());
        Assert.AreEqual(CameraAgentSettingSource.ScheduleRevision, schedule.Source);
        Assert.AreEqual(active.ActiveRevision.RevisionId, schedule.AllowedValues!.Single().Value);
    }

    [TestMethod]
    public async Task Write_RefusesARequestWithoutAVersionValuesOrWithAnUnusableReasonAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var version = (await harness.GroupAsync("skyMap").ConfigureAwait(false)).Version;
        var value = new Dictionary<string, object?> { [MaximumObjects] = 400 };

        var noVersion = await harness.WriteAsync("skyMap", null, value).ConfigureAwait(false);
        var noValues = await harness.WriteAsync("skyMap", version, new Dictionary<string, object?>()).ConfigureAwait(false);
        var longReason = await harness.WriteAsync("skyMap", version, value,
            reason: new string('r', CameraAgentSettingsContract.MaximumReasonLength + 1)).ConfigureAwait(false);
        var controlReason = await harness.WriteAsync("skyMap", version, value, reason: "line\nbreak").ConfigureAwait(false);
        var longKey = await harness.WriteAsync("skyMap", version, value, idempotencyKey: new string('k', 129))
            .ConfigureAwait(false);
        var unknownGroup = await harness.WriteAsync("network", version, value).ConfigureAwait(false);

        foreach (var refused in new[] { noVersion, noValues, longReason, controlReason, longKey })
        {
            Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, refused.Outcome);
        }
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.NotFound, unknownGroup.Outcome);
        Assert.AreEqual(string.Empty, harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task SkyMap_AppliesAtOnceAndClearingRestoresTheDefaultAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var before = await harness.GroupAsync("skyMap").ConfigureAwait(false);

        var saved = await harness.WriteAsync("skyMap", before.Version,
            new Dictionary<string, object?> { [MaximumObjects] = 400 }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, saved.Outcome, saved.Message);
        Assert.AreEqual(400, harness.SceneObjectBound.Current);
        var limit = saved.Group!.Settings.Single();
        Assert.AreEqual(400, limit.Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.SettingsFile, limit.Source);
        Assert.AreNotEqual(before.Version, saved.Group.Version);
        StringAssert.Contains(harness.ReadSettingsFile(), "\"MaximumObjects\": 400", StringComparison.Ordinal);

        var again = await harness.WriteAsync("skyMap", saved.Group.Version,
            new Dictionary<string, object?> { [MaximumObjects] = 400 }).ConfigureAwait(false);
        var cleared = await harness.WriteAsync("skyMap", saved.Group.Version,
            new Dictionary<string, object?> { [MaximumObjects] = null }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Unchanged, again.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, cleared.Outcome, cleared.Message);
        Assert.AreEqual(CameraAgentSkyMapProjection.DefaultMaximumObjects, harness.SceneObjectBound.Current);
        Assert.AreEqual(CameraAgentSettingSource.Default, cleared.Group!.Settings.Single().Source);
    }

    [TestMethod]
    public async Task SkyMap_WithAStaleVersion_ConflictsAndLeavesAHandEditInPlaceAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("skyMap").ConfigureAwait(false);
        await File.WriteAllTextAsync(harness.SettingsPath,
            """{ "CameraAgent": { "SkyMap": { "MaximumObjects": 250 } } }""").ConfigureAwait(false);

        var result = await harness.WriteAsync("skyMap", read.Version,
            new Dictionary<string, object?> { [MaximumObjects] = 400 }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, result.Outcome);
        StringAssert.Contains(harness.ReadSettingsFile(), "250", StringComparison.Ordinal);
        Assert.AreNotEqual(read.Version, result.Group!.Version);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(5001)]
    public async Task SkyMap_OutOfRange_IsRefusedBeforeTheFileIsTouchedAsync(int limit)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("skyMap").ConfigureAwait(false);

        var result = await harness.WriteAsync("skyMap", read.Version,
            new Dictionary<string, object?> { [MaximumObjects] = limit }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, result.Outcome);
        Assert.AreEqual(CameraAgentSettingsCatalogue.MaximumSceneObjects.NumberRule, result.Problems![MaximumObjects]);
        Assert.AreEqual(string.Empty, harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task SkyMap_SetByASourceAboveTheFile_IsReadOnlyWithItsSourceAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync(overrides: new Dictionary<string, string?>
        {
            [SkyMapOptions.MaximumObjectsKey] = "300"
        }).ConfigureAwait(false);
        var read = await harness.GroupAsync("skyMap").ConfigureAwait(false);

        var result = await harness.WriteAsync("skyMap", read.Version,
            new Dictionary<string, object?> { [MaximumObjects] = 400 }).ConfigureAwait(false);

        var limit = read.Settings.Single();
        Assert.AreEqual(300, limit.Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.Host, limit.Source);
        Assert.IsFalse(read.CanWrite);
        Assert.IsFalse(limit.CanWrite);
        StringAssert.Contains(limit.ReadOnlyReason, "takes precedence over the settings file", StringComparison.Ordinal);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.ReadOnly, result.Outcome);
        Assert.AreEqual(string.Empty, harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task SkyMap_AHandEditTheHostCannotUse_IsReportedAsAProblemAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(harness.SettingsPath,
            """{ "CameraAgent": { "SkyMap": { "MaximumObjects": 90000 } } }""").ConfigureAwait(false);
        ((Microsoft.Extensions.Configuration.IConfigurationRoot)harness.Configuration).Reload();

        var limit = (await harness.GroupAsync("skyMap").ConfigureAwait(false)).Settings.Single();

        Assert.AreEqual(CameraAgentSkyMapProjection.DefaultMaximumObjects, limit.Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.SettingsFile, limit.Source);
        StringAssert.StartsWith(limit.Problem, "The settings file value is not a whole number from 1 to 5,000",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CurrentSky_AppliesAtOnceAndClearingRestoresTheDefaultAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var before = await harness.GroupAsync("currentSky").ConfigureAwait(false);
        Assert.AreEqual(CurrentSkyRefreshSetting.DefaultSeconds, before.Settings.Single().Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.Default, before.Settings.Single().Source);

        var saved = await harness.WriteAsync("currentSky", before.Version,
            new Dictionary<string, object?> { [MinimumRefresh] = 120 }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, saved.Outcome, saved.Message);
        Assert.AreEqual(120, harness.CurrentSkyRefresh.Seconds);
        var interval = saved.Group!.Settings.Single();
        Assert.AreEqual(120, interval.Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.SettingsFile, interval.Source);
        StringAssert.Contains(harness.ReadSettingsFile(), "\"MinimumRefreshSeconds\": 120", StringComparison.Ordinal);

        var cleared = await harness.WriteAsync("currentSky", saved.Group.Version,
            new Dictionary<string, object?> { [MinimumRefresh] = null }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, cleared.Outcome, cleared.Message);
        Assert.AreEqual(CurrentSkyRefreshSetting.DefaultSeconds, harness.CurrentSkyRefresh.Seconds);
        Assert.AreEqual(CameraAgentSettingSource.Default, cleared.Group!.Settings.Single().Source);
    }

    [TestMethod]
    [DataRow(14)]
    [DataRow(3601)]
    public async Task CurrentSky_OutOfRange_IsRefusedBeforeTheFileIsTouchedAsync(int seconds)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("currentSky").ConfigureAwait(false);

        var result = await harness.WriteAsync("currentSky", read.Version,
            new Dictionary<string, object?> { [MinimumRefresh] = seconds }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, result.Outcome);
        Assert.AreEqual(CameraAgentSettingsCatalogue.Find(MinimumRefresh)!.NumberRule, result.Problems![MinimumRefresh]);
        Assert.AreEqual(string.Empty, harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task CurrentSky_AHandEditTheHostCannotUse_IsReportedAsAProblemAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(harness.SettingsPath,
            """{ "CameraAgent": { "CurrentSky": { "MinimumRefreshSeconds": 5 } } }""").ConfigureAwait(false);
        ((Microsoft.Extensions.Configuration.IConfigurationRoot)harness.Configuration).Reload();

        var interval = (await harness.GroupAsync("currentSky").ConfigureAwait(false)).Settings.Single();

        Assert.AreEqual(CurrentSkyRefreshSetting.DefaultSeconds, interval.Value!.Value.GetInt32());
        Assert.AreEqual(CameraAgentSettingSource.SettingsFile, interval.Source);
        StringAssert.StartsWith(interval.Problem, "The settings file value is not a whole number from 15 to 3,600",
            StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("currentSky", "{ \"value\": 5 }", "15 to 3,600", "Default")]
    [DataRow("currentSky", "[ 30 ]", "15 to 3,600", "Default")]
    [DataRow("currentSky", "\"soon\"", "15 to 3,600", "SettingsFile")]
    [DataRow("skyMap", "{ \"value\": 400 }", "1 to 5,000", "Default")]
    public async Task FileInteger_AStoredValueOfAnotherShape_IsReportedAsAProblemAsync(
        string group, string stored, string range, string source)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var (section, name) = group == "skyMap" ? ("SkyMap", "MaximumObjects") : ("CurrentSky", "MinimumRefreshSeconds");
        await File.WriteAllTextAsync(harness.SettingsPath,
            $"{{ \"CameraAgent\": {{ \"{section}\": {{ \"{name}\": {stored} }} }} }}").ConfigureAwait(false);
        ((Microsoft.Extensions.Configuration.IConfigurationRoot)harness.Configuration).Reload();

        var setting = (await harness.GroupAsync(group).ConfigureAwait(false)).Settings.Single();

        // The default applies, and the stored value is still named as one the host cannot use.
        var expected = group == "skyMap" ? CameraAgentSkyMapProjection.DefaultMaximumObjects : CurrentSkyRefreshSetting.DefaultSeconds;
        Assert.AreEqual(expected, setting.Value!.Value.GetInt32());
        Assert.AreEqual(Enum.Parse<CameraAgentSettingSource>(source), setting.Source);
        StringAssert.StartsWith(setting.Problem, $"The settings file value is not a whole number from {range}", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Site_MergesTheChangedFieldsAndKeepsTheRestAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("site").ConfigureAwait(false);

        var result = await harness.WriteAsync("site", read.Version, new Dictionary<string, object?>
        {
            ["site.observatoryName"] = "  Hualapai Valley Observatory  ",
            ["site.ownerName"] = "Pat Example"
        }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, result.Outcome, result.Message);
        var values = result.Group!.Settings.ToDictionary(setting => setting.Key, setting => setting.Value?.GetString());
        Assert.AreEqual("Hualapai Valley Observatory", values["site.observatoryName"]);
        Assert.AreEqual("Pat Example", values["site.ownerName"]);
        // The installer's camera name moved into the file on the first read and is kept.
        Assert.AreEqual(CameraAgentSettingsHarness.InstalledName, values["site.cameraName"]);
        Assert.AreEqual(CameraAgentSettingSource.SettingsFile,
            result.Group.Settings.Single(setting => setting.Key == "site.cameraName").Source);
        Assert.IsNull(values["site.ownerContact"]);

        var clearing = await harness.WriteAsync("site", result.Group.Version,
            new Dictionary<string, object?> { ["site.ownerName"] = "   " }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, clearing.Outcome, clearing.Message);
        Assert.IsNull(clearing.Group!.Settings.Single(setting => setting.Key == "site.ownerName").Value);
    }

    [TestMethod]
    public async Task Site_RefusesAnUnknownKeyATooLongValueAndAStaleVersionAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("site").ConfigureAwait(false);
        var fileAfterRead = harness.ReadSettingsFile();

        var unknown = await harness.WriteAsync("site", read.Version, new Dictionary<string, object?>
        {
            ["site.observatoryName"] = "North",
            [MaximumObjects] = 400
        }).ConfigureAwait(false);
        var tooLong = await harness.WriteAsync("site", read.Version, new Dictionary<string, object?>
        {
            ["site.ownerContact"] = new string('c', 201)
        }).ConfigureAwait(false);
        var stale = await harness.WriteAsync("site", "0000", new Dictionary<string, object?>
        {
            ["site.observatoryName"] = "North"
        }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, unknown.Outcome);
        Assert.IsTrue(unknown.Problems!.ContainsKey(MaximumObjects));
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, tooLong.Outcome);
        Assert.AreEqual("Owner contact must be at most 200 characters without control characters.",
            tooLong.Problems!["site.ownerContact"]);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, stale.Outcome);
        Assert.AreEqual(fileAfterRead, harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task Site_WhileTheSettingsFileIsUnreadable_IsReadOnlyAndShowsTheInstallationNameAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(harness.SettingsPath, "{ not json").ConfigureAwait(false);

        var site = await harness.GroupAsync("site").ConfigureAwait(false);
        var result = await harness.WriteAsync("site", site.Version,
            new Dictionary<string, object?> { ["site.ownerName"] = "Pat" }).ConfigureAwait(false);

        Assert.IsFalse(site.CanWrite);
        StringAssert.StartsWith(site.ReadOnlyReason, "The settings file is not valid JSON", StringComparison.Ordinal);
        var camera = site.Settings.Single(setting => setting.Key == "site.cameraName");
        Assert.AreEqual(CameraAgentSettingsHarness.InstalledName, camera.Value!.Value.GetString());
        Assert.AreEqual(CameraAgentSettingSource.Installation, camera.Source);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.ReadOnly, result.Outcome);
        Assert.AreEqual("{ not json", harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task Location_RecordsAManualVersionWithTheCallersVersionAndKeyAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("location").ConfigureAwait(false);

        var result = await harness.WriteAsync("location", read.Version, new Dictionary<string, object?>
        {
            ["location.latitudeDegrees"] = 31.5,
            ["location.timeZoneId"] = "America/Phoenix"
        }, idempotencyKey: "cli-7", reason: "moved the dome").ConfigureAwait(false);

        Assert.AreEqual("3.0", read.Version);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, result.Outcome, result.Message);
        var request = harness.Location.Requests.Single();
        Assert.AreEqual(31.5, request.LatitudeDegrees);
        Assert.AreEqual(-114, request.LongitudeDegrees);
        Assert.AreEqual(1000, request.ElevationMeters);
        Assert.AreEqual("America/Phoenix", request.TimeZoneId);
        Assert.AreEqual(3L, request.ExpectedVersion);
        Assert.AreEqual(0L, request.ExpectedManualSequence);
        Assert.AreEqual("cli-7", request.IdempotencyKey);
        Assert.AreEqual(CameraAgentSettingsHarness.Actor, request.Actor);
        Assert.AreEqual("moved the dome", request.Reason);
    }

    [TestMethod]
    public async Task Location_BuildsOnAPendingManualEntryAndShowsItAsPendingAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        harness.Location.Manual = harness.Location.Manual with
        {
            KnownVersion = 4,
            ManualSequence = 2,
            PendingVersion = 4,
            Override = new ManualDeploymentLocationOverride(
                30, -110, 1200, "America/Denver", PendingRestart: true, DateTimeOffset.UnixEpoch, "owner-id", null)
        };
        var read = await harness.GroupAsync("location").ConfigureAwait(false);

        var result = await harness.WriteAsync("location", read.Version, new Dictionary<string, object?>
        {
            ["location.elevationMeters"] = 1300
        }).ConfigureAwait(false);

        Assert.AreEqual(35d, read.Settings.Single(setting => setting.Key == "location.latitudeDegrees").Value!.Value.GetDouble());
        Assert.AreEqual(30d,
            read.Settings.Single(setting => setting.Key == "location.latitudeDegrees").PendingValue!.Value.GetDouble());
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, result.Outcome, result.Message);
        var request = harness.Location.Requests.Single();
        Assert.AreEqual(30, request.LatitudeDegrees);
        Assert.AreEqual(1300, request.ElevationMeters);
        Assert.AreEqual("America/Denver", request.TimeZoneId);
        Assert.AreEqual(4L, request.ExpectedVersion);
        Assert.AreEqual(2L, request.ExpectedManualSequence);
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.IdempotencyKey));
    }

    [TestMethod]
    public async Task Location_AStaleVersionOrAnInvalidValueNeverReachesTheStoreAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("location").ConfigureAwait(false);

        var stale = await harness.WriteAsync("location", "2.0", new Dictionary<string, object?>
        {
            ["location.latitudeDegrees"] = 31.5
        }).ConfigureAwait(false);
        var invalid = await harness.WriteAsync("location", read.Version, new Dictionary<string, object?>
        {
            ["location.latitudeDegrees"] = 91,
            ["location.timeZoneId"] = "Mars/Olympus_Mons"
        }).ConfigureAwait(false);
        harness.Location.Manual = ManualDeploymentLocationState.Unsupported;
        var unsupported = await harness.WriteAsync("location", (await harness.GroupAsync("location").ConfigureAwait(false)).Version,
            new Dictionary<string, object?> { ["location.latitudeDegrees"] = 31.5 }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, stale.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, invalid.Outcome);
        Assert.HasCount(2, invalid.Problems!);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.ReadOnly, unsupported.Outcome);
        Assert.IsEmpty(harness.Location.Requests);
    }

    [TestMethod]
    [DataRow(ManualDeploymentLocationStatus.Conflict, nameof(CameraAgentSettingsWriteOutcome.Conflict))]
    [DataRow(ManualDeploymentLocationStatus.Unchanged, nameof(CameraAgentSettingsWriteOutcome.Unchanged))]
    [DataRow(ManualDeploymentLocationStatus.Replayed, nameof(CameraAgentSettingsWriteOutcome.PendingRestart))]
    [DataRow(ManualDeploymentLocationStatus.Invalid, nameof(CameraAgentSettingsWriteOutcome.Invalid))]
    public async Task Location_MapsEveryStoreDispositionAsync(
        ManualDeploymentLocationStatus status,
        string expectedOutcome)
    {
        var expected = Enum.Parse<CameraAgentSettingsWriteOutcome>(expectedOutcome);
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        harness.Location.Status = status;
        harness.Location.FieldPath = status == ManualDeploymentLocationStatus.Invalid ? "location.timeZoneId" : null;
        var read = await harness.GroupAsync("location").ConfigureAwait(false);

        var result = await harness.WriteAsync("location", read.Version,
            new Dictionary<string, object?> { ["location.timeZoneId"] = "UTC" }).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Outcome);
        if (status == ManualDeploymentLocationStatus.Invalid)
        {
            Assert.IsTrue(result.Problems!.ContainsKey("location.timeZoneId"));
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(51)]
    public async Task Location_RealStore_ReplaysTheOriginalPartialCommandAfterStateAdvancesAsync(int laterChanges)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync(realLocation: true).ConfigureAwait(false);
        var before = await harness.GroupAsync("location").ConfigureAwait(false);
        var values = new Dictionary<string, object?> { ["location.latitudeDegrees"] = 31.5 };
        var saved = await harness.WriteAsync("location", before.Version, values,
            idempotencyKey: "test-location-replay").ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, saved.Outcome, saved.Message);
        Assert.AreNotEqual(before.Version, saved.Group!.Version);
        for (var index = 0; index < laterChanges; index++)
        {
            var current = await harness.GroupAsync("location").ConfigureAwait(false);
            var next = await harness.WriteAsync("location", current.Version,
                new Dictionary<string, object?> { ["location.longitudeDegrees"] = -110 + index / 10d })
                .ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, next.Outcome, next.Message);
        }
        var sequence = harness.LocationStore.Manual.ManualSequence;
        var governing = harness.LocationStore.Manual.Override;
        var retry = await harness.WriteAsync("location", before.Version, values,
            idempotencyKey: "test-location-replay").ConfigureAwait(false);
        var different = await harness.WriteAsync("location", before.Version,
            new Dictionary<string, object?> { ["location.latitudeDegrees"] = 32 },
            idempotencyKey: "test-location-replay").ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, retry.Outcome, retry.Message);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, different.Outcome);
        Assert.AreEqual(sequence, harness.LocationStore.Manual.ManualSequence);
        Assert.AreEqual(governing, harness.LocationStore.Manual.Override);
    }

    [TestMethod]
    [DataRow("location")]
    [DataRow("schedule")]
    public async Task RealStore_ReplayIsLoggedWithoutAChangeEventAsync(string group)
    {
        var logger = new RecordingSettingsLogger();
        using var harness = await CameraAgentSettingsHarness.CreateAsync(realLocation: true, logger: logger)
            .ConfigureAwait(false);
        var values = new Dictionary<string, object?> { ["location.latitudeDegrees"] = 31.5 };
        var expectedOutcome = CameraAgentSettingsWriteOutcome.PendingRestart;
        if (group == "schedule")
        {
            var draft = await harness.StageScheduleDraftAsync("logging-draft", 2).ConfigureAwait(false);
            values = new Dictionary<string, object?> { ["schedule.activeRevision"] = draft.PendingRevision!.RevisionId };
            expectedOutcome = CameraAgentSettingsWriteOutcome.Applied;
        }
        var before = await harness.GroupAsync(group).ConfigureAwait(false);
        var saved = await harness.WriteAsync(group, before.Version, values, idempotencyKey: "test-replay-log")
            .ConfigureAwait(false);
        Assert.AreEqual(expectedOutcome, saved.Outcome, saved.Message);
        Assert.HasCount(1, logger.Entries);
        Assert.AreEqual(7620, logger.Entries.Single().EventId);
        logger.Entries.Clear();

        var replay = await harness.WriteAsync(group, before.Version, values, idempotencyKey: "test-replay-log")
            .ConfigureAwait(false);

        Assert.AreEqual(expectedOutcome, replay.Outcome, replay.Message);
        Assert.IsFalse(logger.Entries.Any(entry => entry.EventId == 7620), "A replay must not log a change.");
        Assert.HasCount(1, logger.Entries);
        var entry = logger.Entries.Single();
        Assert.AreEqual(7623, entry.EventId);
        Assert.AreEqual(LogLevel.Information, entry.Level);
        Assert.AreEqual($"Settings group {group} write replayed for {CameraAgentSettingsHarness.Actor}; no change",
            entry.Message);
    }

    [TestMethod]
    [DataRow("schedule", false)]
    [DataRow("schedule", true)]
    [DataRow("location", false)]
    [DoNotParallelize] // Keep this deliberate interleaving away from unrelated global telemetry listeners.
    public async Task ConcurrentDuplicate_AfterChecks_UsesAtomicReplayDispositionAsync(string group, bool rollback)
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var logger = new RecordingSettingsLogger();
        using var harness = await CameraAgentSettingsHarness.CreateAsync(realLocation: true, logger: logger,
            beforeStoreWrite: async (writingGroup, cancellationToken) =>
            {
                Assert.AreEqual(group, writingGroup);
                if (Interlocked.Increment(ref writes) == 1)
                {
                    paused.SetResult();
                    await resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }, timeProvider: new FixedTimeProvider()).ConfigureAwait(false);
        var values = new Dictionary<string, object?> { ["location.latitudeDegrees"] = 31.5 };
        var expected = CameraAgentSettingsWriteOutcome.PendingRestart;
        if (group == "schedule")
        {
            var initial = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision;
            var staged = await harness.StageScheduleDraftAsync("concurrent-replay", 2).ConfigureAwait(false);
            var target = staged.PendingRevision!.RevisionId;
            if (rollback)
            {
                _ = await harness.Runtime.ActivateAsync(target, "test-setup-activate", staged.Version,
                    CameraAgentSettingsHarness.Actor, null, CancellationToken.None).ConfigureAwait(false);
                target = initial.RevisionId;
            }
            values = new Dictionary<string, object?> { ["schedule.activeRevision"] = target };
            expected = CameraAgentSettingsWriteOutcome.Applied;
        }
        var before = await harness.GroupAsync(group).ConfigureAwait(false);
        const string command = "test-concurrent-settings-replay";
        // Schedule B passes its replay lookup and version check; location B has captured its original inputs.
        var duplicate = harness.WriteAsync(group, before.Version, values, idempotencyKey: command);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var applied = await harness.WriteAsync(group, before.Version, values, idempotencyKey: command)
                .ConfigureAwait(false);
            Assert.AreEqual(expected, applied.Outcome, applied.Message);
            Assert.IsFalse(applied.Replayed);
            Assert.HasCount(1, logger.Entries);
            Assert.AreEqual(7620, logger.Entries.Single().EventId);
            var durable = await DurableStateAsync().ConfigureAwait(false);
            var counts = await harness.ScheduleMutationCountsAsync().ConfigureAwait(false);
            var runtime = harness.Runtime.Snapshot!;
            var runtimeState = JsonSerializer.Serialize(runtime);
            var changed = harness.Runtime.RevisionChanged;
            logger.Entries.Clear();

            // A is complete. B now enters the runtime/store, whose atomic decision must report replay.
            resume.SetResult();
            var replay = await duplicate.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            Assert.AreEqual(expected, replay.Outcome, replay.Message);
            Assert.AreEqual(durable, await DurableStateAsync().ConfigureAwait(false));
            Assert.AreEqual(counts, await harness.ScheduleMutationCountsAsync().ConfigureAwait(false));
            Assert.AreEqual(runtimeState, JsonSerializer.Serialize(harness.Runtime.Snapshot));
            Assert.AreSame(runtime.Configuration, harness.Runtime.Snapshot!.Configuration);
            Assert.AreSame(runtime.Revision, harness.Runtime.Snapshot.Revision);
            Assert.AreSame(runtime.Preview, harness.Runtime.Snapshot.Preview);
            Assert.AreEqual(changed, harness.Runtime.RevisionChanged);
            Assert.IsFalse(changed.IsCancellationRequested);
            Assert.IsTrue(replay.Replayed,
                $"The atomic disposition must be replay; events: {string.Join(", ", logger.Entries.Select(entry => entry.EventId))}.");
            Assert.IsFalse(logger.Entries.Any(entry => entry.EventId == 7620));
            Assert.HasCount(1, logger.Entries);
            var entry = logger.Entries.Single();
            Assert.AreEqual(7623, entry.EventId);
            Assert.AreEqual(LogLevel.Information, entry.Level);
            Assert.AreEqual($"Settings group {group} write replayed for {CameraAgentSettingsHarness.Actor}; no change",
                entry.Message);
        }
        finally
        {
            _ = resume.TrySetResult();
            _ = await duplicate.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }

        async Task<string> DurableStateAsync()
            => group == "location"
                ? JsonSerializer.Serialize(harness.LocationStore.Manual)
                : JsonSerializer.Serialize(await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SkyLighting_StagesARigRevisionForTheNextStartAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("camera").ConfigureAwait(false);
        var before = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await harness.WriteAsync("camera", read.Version, new Dictionary<string, object?>
        {
            ["camera.skyLighting"] = "controlledNight"
        }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, result.Outcome, result.Message);
        Assert.HasCount(3, result.Recorded!);
        StringAssert.StartsWith(result.Recorded![0], "Created camera", StringComparison.Ordinal);
        var catalog = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.Selection.ActiveRevisionId, catalog.Selection.ActiveRevisionId);
        var pending = catalog.Revisions.Single(revision => revision.RevisionId == catalog.Selection.PendingRevisionId);
        Assert.AreEqual("ControlledNight", pending.Module.Options!.Value.GetProperty("illuminationMode").GetString());
        var lighting = result.Group!.Settings.Single();
        Assert.AreEqual(nameof(VirtualSkyIlluminationMode.LegacyScalarSolar), lighting.Value!.Value.GetString());
        Assert.AreEqual(nameof(VirtualSkyIlluminationMode.ControlledNight), lighting.PendingValue!.Value.GetString());
        Assert.IsFalse(result.Group.CanWrite);
        StringAssert.Contains(result.Group.ReadOnlyReason, "awaiting restart", StringComparison.Ordinal);

        var second = await harness.WriteAsync("camera", result.Group.Version, new Dictionary<string, object?>
        {
            ["camera.skyLighting"] = nameof(VirtualSkyIlluminationMode.SolarDriven)
        }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.ReadOnly, second.Outcome);
    }

    [TestMethod]
    public async Task SkyLighting_UnchangedStaleOrUnsupportedRecordsNothingAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("camera").ConfigureAwait(false);
        var inventory = await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);

        var unchanged = await harness.WriteAsync("camera", read.Version, new Dictionary<string, object?>
        {
            ["camera.skyLighting"] = nameof(VirtualSkyIlluminationMode.LegacyScalarSolar)
        }).ConfigureAwait(false);
        var stale = await harness.WriteAsync("camera", "999." + read.Version.Split('.', 2)[1], new Dictionary<string, object?>
        {
            ["camera.skyLighting"] = nameof(VirtualSkyIlluminationMode.ControlledNight)
        }).ConfigureAwait(false);
        var unsupported = await harness.WriteAsync("camera", read.Version, new Dictionary<string, object?>
        {
            ["camera.skyLighting"] = "Daylight"
        }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Unchanged, unchanged.Outcome, unchanged.Message);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, stale.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, unsupported.Outcome);
        CollectionAssert.AreEquivalent(inventory.Equipment.ToArray(),
            (await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false)).Equipment.ToArray());
        Assert.IsNull((await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
    }

    [TestMethod]
    public async Task SkyLighting_OnAPhysicalCamera_IsReadOnlyAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync(moduleType: "test").ConfigureAwait(false);
        var read = await harness.GroupAsync("camera").ConfigureAwait(false);

        var result = await harness.WriteAsync("camera", read.Version, new Dictionary<string, object?>
        {
            ["camera.skyLighting"] = nameof(VirtualSkyIlluminationMode.ControlledNight)
        }).ConfigureAwait(false);

        Assert.IsFalse(read.CanWrite);
        Assert.AreEqual("Sky lighting applies only to a virtual sky camera.", read.ReadOnlyReason);
        Assert.IsNull(read.Settings.Single().Value);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.ReadOnly, result.Outcome);
    }

    [TestMethod]
    public async Task Schedule_AppliesRollsBackAndReappliesAtTheCaptureBoundaryAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var initial = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision;
        var staged = await harness.StageScheduleDraftAsync("draft", 2).ConfigureAwait(false);
        var draft = staged.PendingRevision!;
        var read = await harness.GroupAsync("schedule").ConfigureAwait(false);
        var setting = read.Settings.Single();
        Assert.AreEqual(draft.RevisionId, setting.PendingValue!.Value.GetString());
        Assert.IsTrue(setting.AllowedValues!.Any(choice => choice.Value == draft.RevisionId &&
            choice.Label.EndsWith("(saved draft)", StringComparison.Ordinal)));

        var applied = await harness.WriteAsync("schedule", read.Version, new Dictionary<string, object?>
        {
            ["schedule.activeRevision"] = draft.RevisionId
        }, idempotencyKey: "apply-draft").ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, applied.Outcome, applied.Message);
        Assert.AreEqual(draft.RevisionId,
            (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
        Assert.AreEqual(draft.RevisionId, applied.Group!.Settings.Single().Value!.Value.GetString());

        var stale = await harness.WriteAsync("schedule", read.Version, new Dictionary<string, object?>
        {
            ["schedule.activeRevision"] = initial.RevisionId
        }).ConfigureAwait(false);
        var rolledBack = await harness.WriteAsync("schedule", applied.Group.Version, new Dictionary<string, object?>
        {
            ["schedule.activeRevision"] = initial.RevisionId
        }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, stale.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, rolledBack.Outcome, rolledBack.Message);
        Assert.AreEqual(initial.RevisionId,
            (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);

        Assert.IsTrue(rolledBack.Group!.Settings.Single().AllowedValues!.Any(choice => choice.Value == draft.RevisionId));
        var reapplied = await harness.WriteAsync("schedule", rolledBack.Group.Version, new Dictionary<string, object?>
        {
            ["schedule.activeRevision"] = draft.RevisionId
        }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, reapplied.Outcome, reapplied.Message);
        Assert.AreEqual(draft.RevisionId,
            (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, 21)]
    [DataRow(true, 21)]
    public async Task Schedule_RealStore_ReplaysActivationAndRollbackWithoutReapplyingAsync(bool rollback, int laterRevisions)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var initial = (await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision;
        var staged = await harness.StageScheduleDraftAsync("replay-draft", 2).ConfigureAwait(false);
        var draft = staged.PendingRevision!;
        if (rollback)
        {
            var applied = await harness.WriteAsync("schedule", staged.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<string, object?> { ["schedule.activeRevision"] = draft.RevisionId }).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, applied.Outcome, applied.Message);
        }
        var before = await harness.GroupAsync("schedule").ConfigureAwait(false);
        var target = rollback ? initial.RevisionId : draft.RevisionId;
        var other = rollback ? draft.RevisionId : initial.RevisionId;
        var values = new Dictionary<string, object?> { ["schedule.activeRevision"] = target.ToUpperInvariant() };
        var saved = await harness.WriteAsync("schedule", before.Version, values,
            idempotencyKey: "test-schedule-replay", reason: "test reason").ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, saved.Outcome, saved.Message);
        Assert.AreNotEqual(before.Version, saved.Group!.Version);
        var retry = await harness.WriteAsync("schedule", before.Version, values,
            idempotencyKey: "test-schedule-replay", reason: "test reason").ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, retry.Outcome, retry.Message);

        var moved = await harness.WriteAsync("schedule", saved.Group.Version,
            new Dictionary<string, object?> { ["schedule.activeRevision"] = other }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, moved.Outcome, moved.Message);
        for (var index = 0; index < laterRevisions; index++)
        {
            var later = await harness.StageScheduleDraftAsync($"later-{index}", 3 + index % 7).ConfigureAwait(false);
            other = later.PendingRevision!.RevisionId;
            moved = await harness.WriteAsync("schedule", later.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<string, object?> { ["schedule.activeRevision"] = other }).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, moved.Outcome, moved.Message);
        }
        if (laterRevisions > 0)
        {
            Assert.IsFalse(moved.Group!.Settings.Single().AllowedValues!.Any(choice => choice.Value == target));
        }
        var lateRetry = await harness.WriteAsync("schedule", before.Version, values,
            idempotencyKey: "test-schedule-replay", reason: "test reason").ConfigureAwait(false);
        var different = await harness.WriteAsync("schedule", moved.Group!.Version,
            new Dictionary<string, object?> { ["schedule.activeRevision"] = other },
            idempotencyKey: "test-schedule-replay", reason: "test reason").ConfigureAwait(false);
        var invalid = await harness.WriteAsync("schedule", before.Version,
            new Dictionary<string, object?> { ["schedule.activeRevision"] = " " },
            idempotencyKey: "test-schedule-replay").ConfigureAwait(false);
        var changedReason = await harness.WriteAsync("schedule", before.Version, values,
            idempotencyKey: "test-schedule-replay", reason: "different test reason").ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, lateRetry.Outcome, lateRetry.Message);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, different.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, changedReason.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, invalid.Outcome, invalid.Message);
        var after = await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(moved.Group.Version, after.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(other, after.ActiveRevision.RevisionId);
        Assert.AreEqual(other, harness.Runtime.Snapshot!.Revision.RevisionId);
    }

    [TestMethod]
    public async Task Schedule_AnUnknownRevisionIsRefusedAndTheActiveOneIsUnchangedAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var read = await harness.GroupAsync("schedule").ConfigureAwait(false);
        var active = read.Settings.Single().Value!.Value.GetString();

        var unknown = await harness.WriteAsync("schedule", read.Version, new Dictionary<string, object?>
        {
            ["schedule.activeRevision"] = "no-such-revision"
        }).ConfigureAwait(false);
        var same = await harness.WriteAsync("schedule", read.Version, new Dictionary<string, object?>
        {
            ["schedule.activeRevision"] = active
        }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, unknown.Outcome);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Unchanged, same.Outcome);
    }

    [TestMethod]
    public async Task Schedule_BeforeTheCameraConfigurationLoads_IsUnavailableRatherThanWaitingAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync(configured: false).ConfigureAwait(false);

        var schedule = await harness.GroupAsync("schedule").ConfigureAwait(false);

        Assert.IsFalse(schedule.CanWrite);
        Assert.AreEqual(CameraAgentSettingSource.Unavailable, schedule.Settings.Single().Source);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 5, 19, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingSettingsLogger : ILogger<CameraAgentSettingsService>
    {
        internal List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }

    private static CameraAgentSettingState Setting(CameraAgentSettingsResponse response, string key)
        => response.Groups.SelectMany(group => group.Settings).Single(setting => setting.Key == key);
}
