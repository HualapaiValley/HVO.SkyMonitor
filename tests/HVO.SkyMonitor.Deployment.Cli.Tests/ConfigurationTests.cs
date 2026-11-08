using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ConfigurationTests
{
    private static readonly string[] ExpectedCloudInput = ["$raw"];
    private static readonly string[] ExpectedCloudLayerInput = ["CloudAssessment"];
    private static readonly string[] ExpectedArchiveDependencies =
        ["$raw", "ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer"];
    private static readonly string[] ExpectedTelemetryDependencies =
        ["ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer", "LocalStorage", "ArchiveStorage"];
    private static readonly string[] ExpectedManifestDependencies =
        ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "CloudPresentation"];
    private static readonly string[] ExpectedMaterializerDependencies =
        ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "CloudPresentation", "OverlayManifest"];

    [TestMethod]
    public void Generate_ProducesValidatedSunsetToSunriseStandaloneConfiguration()
    {
        var request = new InstallRequest
        {
            FriendlyName = "Camera",
            OwnerEmail = "admin@example.test",
            CatalogBundle = "/srv/catalog.bundle",
            ImageReference = $"sha256:{new string('a', 64)}",
            LatitudeDegrees = 35.2,
            LongitudeDegrees = -114.1,
            ElevationMeters = 800,
            TimeZoneId = "America/Phoenix"
        };

        var generated = CameraConfiguration.Generate(request, Guid.Parse("65c0dd43-6490-4f10-b972-0f68c79e45a8"));
        using var json = JsonDocument.Parse(generated.Json);
        var schedule = json.RootElement.GetProperty("schedule");
        var rig = json.RootElement.GetProperty("rig");
        var sensor = rig.GetProperty("sensor");
        var readout = rig.GetProperty("readout");
        var steps = json.RootElement.GetProperty("pipeline").GetProperty("steps").EnumerateArray().ToArray();

        Assert.AreEqual("VirtualSky", json.RootElement.GetProperty("module").GetProperty("type").GetString());
        Assert.AreEqual("VirtualAsi174Mm", sensor.GetProperty("name").GetString());
        Assert.AreEqual(1936, sensor.GetProperty("widthPixels").GetInt32());
        Assert.AreEqual(1216, sensor.GetProperty("heightPixels").GetInt32());
        Assert.AreEqual(3872, sensor.GetProperty("strideBytes").GetInt32());
        Assert.AreEqual("installer-virtualsky-v4", rig.GetProperty("profileVersion").GetString());
        Assert.AreEqual(1, readout.GetProperty("binX").GetInt32());
        Assert.AreEqual(1, readout.GetProperty("binY").GetInt32());
        Assert.AreEqual("IdentityV1", readout.GetProperty("binningAlgorithm").GetString());
        Assert.AreEqual(1936, readout.GetProperty("roi").GetProperty("width").GetInt32());
        Assert.AreEqual(1216, readout.GetProperty("roi").GetProperty("height").GetInt32());
        Assert.AreEqual(12, readout.GetProperty("sampleDepthBits").GetInt32());
        Assert.AreEqual(16, readout.GetProperty("containerDepthBits").GetInt32());
        Assert.AreEqual(968, rig.GetProperty("optics").GetProperty("principalPointX").GetInt32());
        Assert.AreEqual(608, rig.GetProperty("optics").GetProperty("principalPointY").GetInt32());
        Assert.AreEqual(595.84, rig.GetProperty("optics").GetProperty("imageCircleRadiusPixels").GetDouble());
        Assert.IsTrue(rig.GetProperty("optics").GetProperty("horizontalFlip").GetBoolean());
        Assert.IsFalse(json.RootElement.TryGetProperty("observatory", out _));
        Assert.IsFalse(json.RootElement.TryGetProperty("moduleType", out _));
        Assert.AreEqual(7, schedule.GetProperty("weeklyWindows").GetArrayLength());
        Assert.IsTrue(schedule.GetProperty("weeklyWindows").EnumerateArray().All(window =>
            window.GetProperty("start").GetProperty("kind").GetString() == nameof(CaptureScheduleBoundaryKind.Sunset) &&
            window.GetProperty("end").GetProperty("kind").GetString() == nameof(CaptureScheduleBoundaryKind.Sunrise)));
        Assert.IsFalse(generated.Json.Contains("example.invalid", StringComparison.Ordinal));
        Assert.IsFalse(generated.Json.Contains("/workspaces", StringComparison.Ordinal));
        CollectionAssert.AreEqual(
            ExpectedArchiveDependencies,
            Dependencies(steps.Single(static step => step.GetProperty("id").GetString() == "ArchiveStorage")));
        CollectionAssert.AreEquivalent(
            ExpectedTelemetryDependencies,
            Dependencies(steps.Single(static step => step.GetProperty("id").GetString() == "Telemetry")));
        Assert.IsFalse(steps.Any(static step => step.GetProperty("id").GetString() == "Annotation"));
        CollectionAssert.AreEqual(ExpectedManifestDependencies,
            Dependencies(steps.Single(static step => step.GetProperty("id").GetString() == "OverlayManifest")));
        CollectionAssert.AreEqual(ExpectedMaterializerDependencies,
            Dependencies(steps.Single(static step => step.GetProperty("id").GetString() == "PresentationMaterializer")));
        Assert.AreEqual(64, generated.Sha256.Length);
        Assert.AreEqual(64, generated.RigProfileSha256.Length);
        Assert.AreEqual(64, generated.ScheduleSha256.Length);
        var preview = steps.Single(static step => step.GetProperty("id").GetString() == "CombinedPreview")
            .GetProperty("options");
        Assert.AreEqual(0.9997, preview.GetProperty("whitePercentile").GetDouble());
        Assert.AreEqual(8.0, preview.GetProperty("asinhStrength").GetDouble());
    }

    private static string[] Dependencies(JsonElement step)
        => step.GetProperty("dependsOn").EnumerateArray()
            .Select(static dependency => dependency.GetString()!)
            .ToArray();

    [TestMethod]
    public void Generate_EnablesDurableOptionalCloudBranchWithoutBlockingRawOrPreview()
    {
        var generated = CameraConfiguration.Generate(new InstallRequest { FriendlyName = "Cloud acceptance" }, Guid.NewGuid());
        using var json = JsonDocument.Parse(generated.Json);
        var steps = json.RootElement.GetProperty("pipeline").GetProperty("steps").EnumerateArray().ToArray();
        var cloud = steps.SingleOrDefault(step => step.GetProperty("type").GetString() == "CloudAssessment");
        Assert.AreNotEqual(JsonValueKind.Undefined, cloud.ValueKind, "The installed profile must include image-derived cloud assessment.");
        var layer = steps.Single(step => step.GetProperty("type").GetString() == "CloudPresentationLayer");
        foreach (var step in new[] { cloud, layer })
        {
            Assert.IsFalse(step.GetProperty("required").GetBoolean());
            Assert.AreEqual("durable-local", step.GetProperty("publication").GetProperty("persistence").GetString());
        }
        CollectionAssert.AreEqual(ExpectedCloudInput, Dependencies(cloud));
        CollectionAssert.AreEqual(ExpectedCloudLayerInput, Dependencies(layer));
        Assert.AreEqual("references/cloud/clear-reference.manifest.json", cloud.GetProperty("options").GetProperty("clearReferenceManifestPath").GetString());
        Assert.AreEqual(1936, layer.GetProperty("options").GetProperty("widthPixels").GetInt32());
        Assert.AreEqual(1216, layer.GetProperty("options").GetProperty("heightPixels").GetInt32());
        Assert.IsTrue(layer.GetProperty("options").GetProperty("requireQuantifiedAssessment").GetBoolean());
        foreach (var id in new[] { "OverlayManifest", "PresentationMaterializer" })
            CollectionAssert.Contains(Dependencies(steps.Single(step => step.GetProperty("id").GetString() == id)), "CloudPresentation");
        foreach (var id in new[] { "RollingCombination", "CombinedPreview", "LocalStorage", "ArchiveStorage", "Telemetry" })
            CollectionAssert.DoesNotContain(Dependencies(steps.Single(step => step.GetProperty("id").GetString() == id)), "CloudPresentation");
    }
}
