using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Settings;

namespace HVO.SkyMonitor.CameraAgent.Tests.Settings;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSettingsCatalogueTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] GroupOrder = ["site", "location", "skyMap", "camera", "schedule"];

    [TestMethod]
    public void Catalogue_DescribesEachSettingOnceInOneGroupWithEveryAttribute()
    {
        var keys = CameraAgentSettingsCatalogue.All.Select(setting => setting.Key).ToArray();

        CollectionAssert.AllItemsAreUnique(keys);
        CollectionAssert.AreEqual(
            GroupOrder,
            CameraAgentSettingsCatalogue.Groups.Select(group => group.Name).ToArray());
        foreach (var group in CameraAgentSettingsCatalogue.Groups)
        {
            Assert.IsNotEmpty(group.Settings, group.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(group.Title));
            Assert.IsFalse(string.IsNullOrWhiteSpace(group.Description));
            foreach (var setting in group.Settings)
            {
                Assert.AreEqual(group.Name, setting.Group, setting.Key);
                StringAssert.StartsWith(setting.Key, group.Name + ".", StringComparison.Ordinal);
                Assert.IsFalse(string.IsNullOrWhiteSpace(setting.Title), setting.Key);
                Assert.IsFalse(string.IsNullOrWhiteSpace(setting.Description), setting.Key);
                Assert.AreEqual(CameraAgentSettingRole.OperationsRead, setting.ReadRole, setting.Key);
                Assert.AreEqual(CameraAgentSettingRole.OperationsMutate, setting.WriteRole, setting.Key);
                Assert.IsFalse(setting.Secret, setting.Key);
                Assert.AreEqual(setting.Application == CameraAgentSettingApplication.Restart, setting.AppliesAfterRestart);
                Assert.AreSame(setting, CameraAgentSettingsCatalogue.Find(setting.Key));
            }
        }
        // All of a group is kept in one store, so one group is one atomic write.
        Assert.AreEqual(CameraAgentSettingsCatalogue.SettingsFileStore,
            CameraAgentSettingsCatalogue.FindGroup("site")!.Store);
        Assert.AreEqual(CameraAgentSettingsCatalogue.RigRevisionStore,
            CameraAgentSettingsCatalogue.FindGroup("camera")!.Store);
    }

    [TestMethod]
    public void Catalogue_TakesItsRulesFromTheStoresThatEnforceThem()
    {
        Assert.AreEqual(SettingsFileSiteProfileStore.ObservatoryNameKey, CameraAgentSettingsCatalogue.ObservatoryName.ConfigurationKey);
        Assert.AreEqual(SettingsFileSiteProfileStore.CameraNameKey, CameraAgentSettingsCatalogue.CameraName.ConfigurationKey);
        Assert.AreEqual(SettingsFileSiteProfileStore.OwnerNameKey, CameraAgentSettingsCatalogue.OwnerName.ConfigurationKey);
        Assert.AreEqual(SettingsFileSiteProfileStore.OwnerContactKey, CameraAgentSettingsCatalogue.OwnerContact.ConfigurationKey);
        Assert.AreEqual(SiteProfileLimits.MaximumNameLength, CameraAgentSettingsCatalogue.ObservatoryName.MaximumLength);
        Assert.AreEqual(SiteProfileLimits.MaximumContactLength, CameraAgentSettingsCatalogue.OwnerContact.MaximumLength);
        Assert.AreEqual(SkyMapOptions.MaximumObjectsKey, CameraAgentSettingsCatalogue.MaximumSceneObjects.ConfigurationKey);
        Assert.AreEqual(CameraAgentSkyMapProjection.MaximumConfigurableObjects, CameraAgentSettingsCatalogue.MaximumSceneObjects.Maximum);
        Assert.AreEqual(CameraAgentSkyMapProjection.DefaultMaximumObjects, CameraAgentSettingsCatalogue.MaximumSceneObjects.Default);
        Assert.AreEqual(ManualDeploymentLocationContract.MinimumElevationMeters, CameraAgentSettingsCatalogue.Elevation.Minimum);
        Assert.AreEqual(ManualDeploymentLocationContract.MaximumElevationMeters, CameraAgentSettingsCatalogue.Elevation.Maximum);
        CollectionAssert.AreEquivalent(
            Enum.GetNames<VirtualSkyIlluminationMode>(),
            CameraAgentSettingsCatalogue.SkyLighting.AllowedValues!.Select(choice => choice.Value).ToArray());
        Assert.AreEqual(nameof(VirtualSkyIlluminationMode.LegacyScalarSolar), CameraAgentSettingsCatalogue.SkyLighting.Default);
        // The Camera & rig page offers exactly the catalogue's choices.
        CollectionAssert.AreEqual(
            CameraAgentSettingsCatalogue.SkyLighting.AllowedValues!.Select(choice => (choice.Value, choice.Label, choice.Description)).ToArray(),
            EquipmentFormModel.SkyLightingChoices.Select(choice => (choice.Value, choice.Label, choice.Help)).ToArray());
    }

    [TestMethod]
    public void Catalogue_DescribesWhenEachChangeAppliesAndWhatItAffects()
    {
        Assert.AreEqual(CameraAgentSettingApplication.Immediate, CameraAgentSettingsCatalogue.ObservatoryName.Application);
        Assert.AreEqual(CameraAgentSettingSafety.Descriptive, CameraAgentSettingsCatalogue.ObservatoryName.Safety);
        Assert.AreEqual(CameraAgentSettingApplication.Immediate, CameraAgentSettingsCatalogue.MaximumSceneObjects.Application);
        Assert.AreEqual(CameraAgentSettingSafety.CaptureOutput, CameraAgentSettingsCatalogue.MaximumSceneObjects.Safety);
        Assert.AreEqual(CameraAgentSettingApplication.Restart, CameraAgentSettingsCatalogue.Latitude.Application);
        Assert.AreEqual(CameraAgentSettingSafety.CaptureGeometry, CameraAgentSettingsCatalogue.Latitude.Safety);
        Assert.IsTrue(CameraAgentSettingsCatalogue.SkyLighting.AppliesAfterRestart);
        Assert.AreEqual(CameraAgentSettingApplication.NextCaptureBoundary,
            CameraAgentSettingsCatalogue.ActiveScheduleRevision.Application);
        Assert.IsFalse(CameraAgentSettingsCatalogue.ActiveScheduleRevision.AppliesAfterRestart);
    }

    [TestMethod]
    public void CatalogueResponse_SerializesTheWireContract()
    {
        var json = JsonSerializer.SerializeToElement(CameraAgentSettingsCatalogue.ToResponse(), WebJson);

        Assert.AreEqual(CameraAgentSettingsContract.SchemaVersion, json.GetProperty("schemaVersion").GetInt32());
        var skyMap = json.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("name").GetString() == "skyMap");
        Assert.AreEqual("settingsFile", skyMap.GetProperty("store").GetString());
        var limit = skyMap.GetProperty("settings")[0];
        Assert.AreEqual("skyMap.maximumObjects", limit.GetProperty("key").GetString());
        Assert.AreEqual("Integer", limit.GetProperty("type").GetString());
        Assert.AreEqual(1, limit.GetProperty("minimum").GetDouble());
        Assert.AreEqual(5000, limit.GetProperty("maximum").GetDouble());
        Assert.AreEqual(200, limit.GetProperty("default").GetInt32());
        Assert.AreEqual("OperationsMutate", limit.GetProperty("writeRole").GetString());
        Assert.AreEqual("CaptureOutput", limit.GetProperty("safety").GetString());
        Assert.AreEqual("Immediate", limit.GetProperty("application").GetString());
        Assert.IsFalse(limit.GetProperty("appliesAfterRestart").GetBoolean());
        Assert.IsFalse(limit.GetProperty("secret").GetBoolean());
        Assert.AreEqual("CameraAgent:SkyMap:MaximumObjects", limit.GetProperty("configurationKey").GetString());
        var schedule = json.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("name").GetString() == "schedule");
        Assert.AreEqual(JsonValueKind.Null, schedule.GetProperty("settings")[0].GetProperty("allowedValues").ValueKind);
    }

    [TestMethod]
    [DataRow("400", true, 400)]
    [DataRow("1", true, 1)]
    [DataRow("5000", true, 5000)]
    [DataRow("0", false, null)]
    [DataRow("5001", false, null)]
    [DataRow("1.5", false, null)]
    [DataRow("\"400\"", false, null)]
    [DataRow("true", false, null)]
    [DataRow("null", true, null)]
    public void MaximumSceneObjects_AcceptsOnlyAWholeNumberInRangeOrNull(string json, bool valid, int? expected)
    {
        var definition = CameraAgentSettingsCatalogue.MaximumSceneObjects;

        var parsed = definition.TryParse(JsonElement.Parse(json), null, out var value, out var problem);

        Assert.AreEqual(valid, parsed, problem);
        Assert.AreEqual(expected, (int?)value);
        if (!valid)
        {
            // The problem names the rule, never the value that was sent.
            CollectionAssert.Contains(
                new[] { definition.NumberRule, "Scene object limit must be a whole number." },
                problem);
        }
    }

    [TestMethod]
    public void SiteText_IsTrimmedClearedWhenBlankAndBoundedWithoutControlCharacters()
    {
        var definition = CameraAgentSettingsCatalogue.ObservatoryName;

        Assert.IsTrue(definition.TryParse(Json("  North Dome  "), null, out var trimmed, out _));
        Assert.IsTrue(definition.TryParse(Json("   "), null, out var cleared, out _));
        Assert.IsFalse(definition.TryParse(Json(new string('n', 81)), null, out _, out var tooLong));
        Assert.IsFalse(definition.TryParse(Json("North\nDome"), null, out _, out var control));
        Assert.IsFalse(definition.TryParse(JsonElement.Parse("42"), null, out _, out var wrongType));

        Assert.AreEqual("North Dome", trimmed);
        Assert.IsNull(cleared);
        Assert.AreEqual("Observatory name must be at most 80 characters without control characters.", tooLong);
        Assert.AreEqual(tooLong, control);
        Assert.AreEqual("Observatory name must be text.", wrongType);
    }

    [TestMethod]
    public void Coordinates_AreBoundedAndTheTimeZoneMustResolve()
    {
        Assert.IsTrue(CameraAgentSettingsCatalogue.Latitude.TryParse(JsonElement.Parse("-90"), null, out _, out _));
        Assert.IsFalse(CameraAgentSettingsCatalogue.Latitude.TryParse(JsonElement.Parse("90.5"), null,
            out _, out var latitude));
        Assert.IsFalse(CameraAgentSettingsCatalogue.Longitude.TryParse(JsonElement.Parse("181"), null,
            out _, out var longitude));
        Assert.IsFalse(CameraAgentSettingsCatalogue.Latitude.TryParse(JsonElement.Parse("null"), null,
            out _, out var cleared));
        Assert.IsTrue(CameraAgentSettingsCatalogue.TimeZone.TryParse(Json("America/Phoenix"), null, out var zone, out _));
        Assert.IsFalse(CameraAgentSettingsCatalogue.TimeZone.TryParse(Json("Mars/Olympus_Mons"), null, out _, out var unknown));

        Assert.AreEqual("Latitude must be a number between -90 and 90 degrees.", latitude);
        Assert.AreEqual("Longitude must be a number between -180 and 180 degrees, east positive.", longitude);
        Assert.AreEqual("Latitude cannot be cleared.", cleared);
        Assert.AreEqual("America/Phoenix", zone);
        StringAssert.StartsWith(unknown, "Time zone must be an IANA identifier", StringComparison.Ordinal);
    }

    [TestMethod]
    public void SkyLighting_IsMatchedWithoutRegardToCaseAndReturnedCanonical()
    {
        var definition = CameraAgentSettingsCatalogue.SkyLighting;

        Assert.IsTrue(definition.TryParse(Json("controllednight"), null, out var choice, out _));
        Assert.IsFalse(definition.TryParse(Json("Daylight"), null, out _, out var unknown));
        Assert.IsFalse(definition.TryParse(JsonElement.Parse("null"), null, out _, out _));

        Assert.AreEqual(nameof(VirtualSkyIlluminationMode.ControlledNight), choice);
        Assert.AreEqual("Sky lighting must be one of LegacyScalarSolar, SolarDriven, ControlledNight.", unknown);
    }

    [TestMethod]
    public void ScheduleRevision_IsCheckedAgainstTheRevisionsTheHostListsNow()
    {
        var definition = CameraAgentSettingsCatalogue.ActiveScheduleRevision;
        CameraAgentSettingChoice[] revisions = [new("rev-2", "Revision 2 (saved draft)", "draft"), new("rev-1", "Revision 1", "")];

        Assert.IsTrue(definition.TryParse(Json("rev-2"), revisions, out var chosen, out _));
        Assert.IsFalse(definition.TryParse(Json("rev-3"), revisions, out _, out _));
        Assert.IsFalse(definition.TryParse(Json("rev-2"), null, out _, out _));

        Assert.AreEqual("rev-2", chosen);
    }

    private static JsonElement Json(string text) => JsonSerializer.SerializeToElement(text);
}
