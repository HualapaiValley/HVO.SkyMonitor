using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Deployment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ConfigurationTests
{
    private static readonly string[] ExpectedArchiveDependencies =
        ["$raw", "ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer"];
    private static readonly string[] ExpectedTelemetryDependencies =
        ["ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer", "LocalStorage", "ArchiveStorage"];
    private static readonly string[] ExpectedManifestDependencies =
        ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation"];
    private static readonly string[] ExpectedMaterializerDependencies =
        ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest"];

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
        Assert.AreEqual("installer-virtualsky-v2", rig.GetProperty("profileVersion").GetString());
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
        Assert.IsFalse(rig.GetProperty("optics").GetProperty("horizontalFlip").GetBoolean());
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
    }

    [TestMethod]
    public async Task Generate_LoadsAndCompilesWithCameraAgentPipeline()
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
        var path = Path.Combine(Path.GetTempPath(), $"installer-profile-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, generated.Json).ConfigureAwait(false);
            var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = path,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
                Observatory = new ObservatoryLocation(
                    request.LatitudeDegrees, request.LongitudeDegrees, request.ElevationMeters, request.TimeZoneId)
            }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
            var configuration = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["CameraAgent:RawIngressRoot"] = Path.GetTempPath() })
                .Build());
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var graph = factory.CreateGraph(configuration);
            try
            {
                Assert.HasCount(configuration.Pipeline.Steps.Count, graph.Nodes);
                Assert.IsNotNull(graph.SharedPlan);
            }
            finally
            {
                graph.DisposeSteps();
            }

            await using var module = new VirtualSkyCameraModule(
                TimeProvider.System,
                new InMemoryCelestialCatalog([]),
                new ProjectedSceneStore(),
                StandardConstellationTopology.CreateD3Celestial(),
                new AstronomyEnginePlanetEphemeris());
            ((ICameraModuleConfigurationPreflight)module).ValidateConfiguration(configuration);
            await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(
                new CaptureRequest(
                    new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromSeconds(25),
                    CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
                CancellationToken.None).ConfigureAwait(false);
            var frame = capture.Frame ?? throw new AssertFailedException("VirtualSky did not return a frame.");
            var layout = frame.Layout ?? throw new AssertFailedException("VirtualSky did not report a readout layout.");
            var expected = SensorReadoutResolver.Resolve(configuration.Rig.Sensor, configuration.Rig.Readout!).Layout;

            Assert.AreEqual(1936, frame.Width);
            Assert.AreEqual(1216, frame.Height);
            Assert.AreEqual(CameraPixelFormat.Mono16, frame.PixelFormat);
            Assert.AreEqual(3872, frame.StrideBytes);
            Assert.AreEqual(4_708_352, frame.PixelData.Length);
            Assert.AreEqual(expected, layout);
            Assert.IsNotNull(layout.Readout);
            Assert.AreEqual(new FrameReadoutDescriptor(1936, 1216, 0, 0, 1936, 1216, 1, 1,
                FrameBinningAlgorithm.IdentityV1, null, null), layout.Readout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] Dependencies(JsonElement step)
        => step.GetProperty("dependsOn").EnumerateArray()
            .Select(static dependency => dependency.GetString()!)
            .ToArray();
}
