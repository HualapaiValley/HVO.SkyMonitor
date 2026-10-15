using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

/// <summary>Real VirtualSky pixels, durable lanes and detector receipts for the measured-overlay browser gate.</summary>
internal static class MeasuredTrailBrowserFixture
{
    internal static async Task<Guid> PrepareAsync(string root)
    {
        using var provider = CreateProvider(root);
        var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var configuration = CreateConfiguration(root, CreateOneFrameScenario(epoch));
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var module = new VirtualSkyCameraModule(TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(), new ProjectedSceneStore(),
            stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var lanes = provider.GetRequiredService<ICaptureLaneStore>();
        var definitions = provider.GetRequiredService<CaptureLanePolicy>().Definitions;
        var handlers = provider.GetServices<ICaptureLaneHandler>().ToArray();
        for (var index = 0; index < 7; index++)
        {
            var request = new CaptureRequest(epoch.AddSeconds(index * 5), TimeSpan.FromSeconds(5),
                CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var frame = capture.Frame!;
            capture = capture with
            {
                Frame = frame with
                {
                    Metadata = frame.Metadata with
                    {
                        Extra = new Dictionary<string, string>(frame.Metadata.Extra ?? new Dictionary<string, string>())
                        {
                            ["sensorAdcBitDepth"] = "16", ["blackLevelAdu"] = "0", ["whiteLevelAdu"] = "65535"
                        }
                    },
                    Layout = frame.Layout! with { BlackLevel = 0, WhiteLevel = ushort.MaxValue }
                }
            };
            var submission = new CaptureLoopSubmission(request, capture, request.RequestedStartUtc,
                TimeSpan.FromSeconds(1), TimeSpan.Zero);
            Assert.IsNotNull(await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false));
            foreach (var definition in definitions)
            {
                var lease = await lanes.ClaimAsync(definition, "issue-1005-browser", configuration, CancellationToken.None).ConfigureAwait(false);
                if (lease is null) continue;
                var result = await handlers.Single(value => value.Lane == definition.Name)
                    .HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await lanes.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
        }
        var worker = provider.GetRequiredService<TransientWorkerService>();
        for (var index = 0; index < 7; index++)
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
        var projection = provider.GetRequiredService<ICameraAgentTransientOperatorProjection>();
        var candidates = await projection.GetPageAsync(new(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, candidates.Items.Count, "The fixture must produce one real extracted candidate.");
        var detail = await projection.GetCandidateAsync(candidates.Items[0].CandidateId, CancellationToken.None).ConfigureAwait(false);
        return detail!.Sources!.Single(value => value.EvidenceId == detail.CandidateEvidence.CenterEvidenceId).CaptureId;
    }

    internal static async Task FinalizeAsync(string root)
    {
        using var provider = CreateProvider(root);
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(CreateConfiguration(root));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var worker = provider.GetRequiredService<TransientWorkerService>();
        for (var index = 0; index < 24; index++)
            if (!await worker.ProcessCandidateAsync(timeout.Token).ConfigureAwait(false) &&
                !await worker.ProcessFrameAsync(timeout.Token).ConfigureAwait(false)) return;
        Assert.Fail("The bounded detector finalization did not drain.");
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:TransientDetection:Mode"] = "Edge",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CaptureProcessingStepConfig Step(string type, string id, int order,
        IReadOnlyList<string> dependencies, object options) =>
        new(type, id, order, Options: JsonSerializer.SerializeToElement(options), DependsOn: dependencies,
            Publication: new(CaptureProcessingPersistenceMode.DurableLocal));

    private static CameraModuleConfig CreateConfiguration(
        string root,
        VirtualTransientScenarioDefinition? scenario = null,
        VirtualCloudScenarioDefinition? cloudScenario = null,
        int width = 64,
        int height = 48)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 63,
            maximumResults = 1,
            magnitudeZeroElectronsPerSecond = 1_000d,
            backgroundElectronsPerSecond = 1d,
            psfSigmaPixels = 1d,
            psfRadiusPixels = 4d,
            vignettingStrength = 0.1,
            bias = 0d,
            readNoiseStandardDeviation = 0d,
            shotNoiseEnabled = false,
            transientScenario = scenario,
            cloudScenario
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    "TransientRuntimeFixture", width, height, 5.86, SensorColorMode.Mono,
                    CameraPixelFormat.Mono16, SensorResponseMode.Monochrome,
                    SensorRecipeVersion: "transient-runtime-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    width / 2d, height / 2d, Math.Min(width, height) / 2d - 1,
                    CalibrationVersion: "transient-runtime-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    1,
                    1)),
            new CapturePipelineConfig([
                
                Step("Calibration", "calibration", 1, ["$raw"], new { strategy = "None" }),
                Step("ProjectedScene", "scene", 5, ["$raw"], new { }),
                Step("RollingCombination", "rolling", 10, ["calibration"], new { windowSize = 1, outputVariant = "rolling-mean" }),
                Step("CombinedPreview", "combined-preview", 20, ["rolling"], new { outputVariant = "combined-preview" }),
                Step("ScenePresentationLayer", "scene-layers", 30, ["scene"], new { markerRadius = 1, labelScale = 1 }),
                Step("EnvironmentPresentationLayer", "facts", 40, ["scene", "combined-preview"],
                    new { widthPixels = width, heightPixels = height, environmentalKinds = Array.Empty<string>() }),
                Step("OverlayManifest", "manifest", 50, ["combined-preview", "scene-layers", "facts"], new { }),
                new CaptureProcessingStepConfig("Storage", "storage", 100,
                    JsonSerializer.SerializeToElement(new { storageRoot = root, retentionDays = 3650,
                        updateLatestFrame = false, queueForUpload = false }), DependsOn: ["$raw", "manifest"])

            ]),
            AgentId: CameraAgentKestrelFixture.AgentId);
    }

    private static VirtualTransientScenarioDefinition CreateOneFrameScenario(
        DateTimeOffset epoch,
        double offsetSeconds = 20) => new()
        {
            ScenarioId = "runtime-one-frame",
            ScenarioVersion = "1",
            Seed = 63,
            EpochUtc = epoch,
            TemporalSampleCount = 8,
            SkyTracks =
        [
            new VirtualTransientSkyTrack
            {
                PrimitiveId = "runtime-sky-track",
                Keyframes =
                [
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = offsetSeconds,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 270,
                        Magnitude = -10,
                        AngularWidthDegrees = 0.25
                    },
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = offsetSeconds + 1,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 90,
                        Magnitude = -10,
                        AngularWidthDegrees = 0.25
                    }
                ]
            }
        ],
            SensorTracks =
        [
            new VirtualTransientSensorTrack
            {
                PrimitiveId = "runtime-track",
                Keyframes =
                [
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = offsetSeconds,
                        PixelX = 16,
                        PixelY = 24,
                        ElectronsPerSecond = 10_000_000
                    },
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = offsetSeconds + 1,
                        PixelX = 48,
                        PixelY = 24,
                        ElectronsPerSecond = 10_000_000
                    }
                ]
            }
        ]
        };

}
