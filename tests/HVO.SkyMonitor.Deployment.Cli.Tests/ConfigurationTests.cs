using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ConfigurationTests
{
    private static readonly string[] ExpectedArchiveDependencies = ["Annotation"];
    private static readonly string[] ExpectedTelemetryDependencies =
        ["Calibration", "Preview", "Annotation", "LocalStorage", "ArchiveStorage"];

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
        var steps = json.RootElement.GetProperty("pipeline").GetProperty("steps").EnumerateArray().ToArray();

        Assert.AreEqual("VirtualSky", json.RootElement.GetProperty("module").GetProperty("type").GetString());
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
        Assert.AreEqual(64, generated.Sha256.Length);
        Assert.AreEqual(64, generated.RigProfileSha256.Length);
        Assert.AreEqual(64, generated.ScheduleSha256.Length);
    }

    private static string[] Dependencies(JsonElement step)
        => step.GetProperty("dependsOn").EnumerateArray()
            .Select(static dependency => dependency.GetString()!)
            .ToArray();
}
