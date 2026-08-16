using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentAsi676ProfileTests
{
    private static readonly ObservatoryLocation Location = new(35.5599378, -113.9119818, 520, "America/Phoenix");
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    [TestMethod]
    public async Task Profiles_PinFullResolutionCadenceAndRecurringScenario()
    {
        foreach (var profile in new[]
        {
            (File: "allsky01.virtual-asi676mc.full.json", Format: CameraPixelFormat.BayerRggb16),
            (File: "hvo-edge-01.virtual-asi676mm.full.json", Format: CameraPixelFormat.Mono16)
        })
        {
            var configuration = await LoadAsync(profile.File).ConfigureAwait(false);
            var layout = SensorReadoutResolver.Resolve(configuration.Rig.Sensor, configuration.Rig.Readout!).Layout;
            var options = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(
                configuration.Module.Options!.Value, StrictJsonOptions)!;

            Assert.AreEqual(3552, layout.Width);
            Assert.AreEqual(3552, layout.Height);
            Assert.AreEqual(7104, layout.StrideBytes);
            Assert.AreEqual(25_233_408L, layout.ByteLength);
            Assert.IsTrue(layout.Width * layout.Height <= Linear16TransientExtraction.MaximumDetectorPixels);
            Assert.IsTrue(layout.Width * layout.Height <= Linear16TransientReconstruction.MaximumDetectorPixels);
            Assert.AreEqual(profile.Format, layout.PixelFormat);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Rig.Pipeline.DayExposure);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Rig.Pipeline.NightExposure);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Rig.Pipeline.CaptureInterval);
            Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, configuration.Rig.Pipeline.CadenceMode);
            Assert.IsNotNull(configuration.Schedule);
            Assert.HasCount(1, configuration.Schedule.SetpointProfiles);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Schedule.SetpointProfiles[0].Exposure);
            Assert.AreEqual(TimeSpan.FromSeconds(20), configuration.Schedule.SetpointProfiles[0].CaptureInterval);
            Assert.IsNotNull(options.TransientScenario?.Recurrence);
            Assert.AreEqual(4096, options.TransientScenario.Recurrence.EventCount);
            Assert.HasCount(2, options.TransientScenario.Recurrence.Profiles);
            options.TransientScenario.ValidateSensorBounds(layout.Width, layout.Height);
            var module = new VirtualSkyCameraModule(
                TimeProvider.System,
                new InMemoryCelestialCatalog([]),
                new ProjectedSceneStore(),
                StandardConstellationTopology.CreateD3Celestial(),
                new AstronomyEnginePlanetEphemeris());
            ((ICameraModuleConfigurationPreflight)module).ValidateConfiguration(configuration);
            if (profile.Format == CameraPixelFormat.Mono16)
            {
                await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var capture = await module.CaptureAsync(
                    new CaptureRequest(
                        new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                        TimeSpan.FromSeconds(20),
                        CaptureMode.Still,
                        new CaptureSetpoint(TimeSpan.FromSeconds(20), 82, null, null)),
                    CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(capture.Frame);
                Assert.AreEqual(layout.Width, capture.Frame.Width);
                Assert.AreEqual(layout.Height, capture.Frame.Height);
                Assert.AreEqual(CameraPixelFormat.Mono16, capture.Frame.PixelFormat);
                Assert.IsNotNull(capture.Frame.Metadata.Scene?.TransientScenario);

                var detectorFrame = new Linear16Frame(
                    capture.Frame.Width,
                    capture.Frame.Height,
                    capture.Frame.StrideBytes!.Value,
                    capture.Frame.PixelFormat,
                    capture.Frame.PixelData);
                var emptyMask = Linear16MaskOperations.Empty(capture.Frame.Width, capture.Frame.Height);
                var extractionProfile = TransientCandidateExtractionProfiles.EdgeV1;
                var extraction = Linear16TransientExtraction.Extract(
                    detectorFrame,
                    detectorFrame,
                    emptyMask,
                    emptyMask,
                    new Linear16TransientExtractionOptions(
                        extractionProfile.MinimumResidualAdu,
                        extractionProfile.MinimumComponentPixels,
                        extractionProfile.MinimumIntegratedSignalAdu,
                        extractionProfile.MaximumCandidates,
                        extractionProfile.ProfileSampleCount,
                        extractionProfile.MaximumSaturationBridgePixels,
                        extractionProfile.MaximumForegroundPixels,
                        extractionProfile.MaximumFragmentGapPixels,
                        extractionProfile.MinimumFragmentAlignmentCosine));
                Assert.IsFalse(extraction.CandidateLimitExceeded);
                Assert.AreEqual(0, extraction.Components.Count);
                Assert.IsTrue(extraction.BytesScanned >= layout.ByteLength * 2);

                var reconstruction = Linear16TransientReconstruction.Reconstruct([
                    new Linear16TransientReconstructionObservation(
                        detectorFrame,
                        detectorFrame,
                        emptyMask,
                        new Linear16TransientReconstructionBounds(0, 0, layout.Width, layout.Height))
                ]);
                Assert.AreEqual(layout.Width, reconstruction.Reconstruction.Width);
                Assert.AreEqual(layout.Height, reconstruction.Reconstruction.Height);
                Assert.AreEqual(0, reconstruction.EventPixelCount);
            }
        }
    }

    [TestMethod]
    public async Task Asi676MonoNativeReadoutRejectsSensorPlaneTransientTracks()
    {
        var configuration = await LoadAsync("hvo-edge-01.virtual-asi676mm.full.json").ConfigureAwait(false);
        var options = configuration.Module.Options!.Value.Deserialize<VirtualSkyCameraModuleOptions>(StrictJsonOptions)!;
        options.TransientScenario = options.TransientScenario! with
        {
            SensorTracks =
            [
                new VirtualTransientSensorTrack
                {
                    PrimitiveId = "native-readout-sensor-track",
                    Keyframes =
                    [
                        new VirtualTransientSensorKeyframe { OffsetSeconds = 0, PixelX = 100, PixelY = 100 },
                        new VirtualTransientSensorKeyframe { OffsetSeconds = 1, PixelX = 200, PixelY = 200 }
                    ]
                }
            ]
        };
        configuration = configuration with
        {
            Module = configuration.Module with { Options = JsonSerializer.SerializeToElement(options, StrictJsonOptions) }
        };
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            new InMemoryCelestialCatalog([]),
            new ProjectedSceneStore(),
            StandardConstellationTopology.CreateD3Celestial(),
            new AstronomyEnginePlanetEphemeris());

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((ICameraModuleConfigurationPreflight)module).ValidateConfiguration(configuration));
    }

    [TestMethod]
    public async Task Profiles_DeclareExplicitProcessingGraphsAndDistinctIdentities()
    {
        var color = await LoadAsync("allsky01.virtual-asi676mc.full.json").ConfigureAwait(false);
        var mono = await LoadAsync("hvo-edge-01.virtual-asi676mm.full.json").ConfigureAwait(false);
        using var provider = CreateProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var colorPlan = factory.PreviewPlan(color);
        var monoPlan = factory.PreviewPlan(mono);

        Assert.HasCount(8, colorPlan.EffectiveNodes);
        Assert.HasCount(8, monoPlan.EffectiveNodes);
        var colorPipeline = color.Pipeline ?? throw new AssertFailedException("Color pipeline is required.");
        var monoPipeline = mono.Pipeline ?? throw new AssertFailedException("Mono pipeline is required.");
        Assert.IsTrue(colorPipeline.Steps.All(static step => step.DependsOn is { Count: > 0 }));
        Assert.IsTrue(monoPipeline.Steps.All(static step => step.DependsOn is { Count: > 0 }));
        Assert.AreNotEqual(CameraRigProfileIdentity.ComputeSha256(color.Rig), CameraRigProfileIdentity.ComputeSha256(mono.Rig));
        Assert.IsFalse(color.Module.Options!.Value.GetRawText().Contains("meteor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(color.Module.Options.Value.GetRawText().Contains("fireball", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(mono.Module.Options!.Value.GetRawText().Contains("meteor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(mono.Module.Options.Value.GetRawText().Contains("fireball", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<CameraModuleConfig> LoadAsync(string fileName)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, fileName),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = Location
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CameraAgent:RawIngressRoot"] = Path.GetTempPath() })
            .Build());
        return services.BuildServiceProvider();
    }
}
