using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// A VirtualSky window that crosses a gain step, stacked by the mixed-gain recipe through the real ingress, standard
/// lane and host adapter with the active family composed of the agent's own virtual reference acquisitions (#1524).
/// </summary>
[TestClass]
public sealed class MixedGainRollingWindowEndToEndTests
{
    private const string NodeId = "mixed-gain";
    private const double LowGain = 82;
    private const double HighGain = 100;
    private static readonly DateTimeOffset FixtureUtc = new(2026, 2, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LightExposure = TimeSpan.FromMilliseconds(32);

    private static readonly VirtualCalibrationLightOptions VirtualCalibration = new()
    {
        SourceModel = new VirtualCalibrationSourceModelV1 { Seed = 1524 },
        BiasExposure = TimeSpan.FromMilliseconds(1),
        DarkExposure = TimeSpan.FromSeconds(2),
        FlatExposure = TimeSpan.FromMilliseconds(100),
        DefectExposure = TimeSpan.FromMilliseconds(3),
        Offset = 1,
        TemperatureC = -10
    };

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AWindowAcrossAGainStepIsFrozenExecutedThroughTheAdapterAndStoredWithItsPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        VirtualSkyCameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration(root);
            ((CameraAgentConfigurationAccessor)provider.GetRequiredService<ICameraAgentConfigurationAccessor>())
                .SetConfiguration(configuration);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await provider.GetRequiredService<CaptureAdmissionCoordinator>().InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var library = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            _ = await library.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            // The agent acquires its own references at each gain of the step.
            var acquisition = provider.GetRequiredService<VirtualCalibrationAcquisitionCoordinator>();
            foreach (var gain in new[] { LowGain, HighGain })
            {
                var job = await acquisition.AcquireAsync(AcquisitionRequest(gain), CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(CalibrationAcquisitionStates.Published, job.State, job.FailureReason);
            }
            var bundles = (await library.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false))
                .ToDictionary(static snapshot => snapshot.Bundle.Applicability.MinimumGain, static snapshot => snapshot.Bundle);

            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            module = new VirtualSkyCameraModule(
                TimeProvider.System,
                provider.GetRequiredService<ICelestialCatalog>(),
                provider.GetRequiredService<IProjectedSceneStore>(),
                provider.GetRequiredService<IConstellationTopology>(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var low = await ingress.AcceptAsync(
                configuration, await CreateSubmissionAsync(module, 0, LowGain).ConfigureAwait(false), CancellationToken.None)
                .ConfigureAwait(false);
            var high = await ingress.AcceptAsync(
                configuration, await CreateSubmissionAsync(module, 1, HighGain).ConfigureAwait(false), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(low);
            Assert.IsNotNull(high);

            // The operator activates one family whose members are those acquisitions.
            var light = high.Manifest.Descriptor;
            var family = Family(light, bundles);
            var stored = await provider.GetRequiredService<SqliteCalibrationFamilyStore>()
                .StoreAsync(family, CancellationToken.None).ConfigureAwait(false);
            var activated = await provider.GetRequiredService<CalibrationFamilyActivationService>().ActivateAsync(
                new CalibrationFamilyActivationRequest(
                    "mixed-gain-activate",
                    new CalibrationFamilyActivationTarget(
                        light.Capture.AgentId, light.Capture.RigId, light.Profiles.Rig.Sha256, light.Profiles.Sensor.Sha256,
                        light.Profiles.Calibration),
                    stored.IdentitySha256,
                    "operator",
                    null),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(activated.IsAccepted);

            await DrainOneAsync(provider, configuration).ConfigureAwait(false);
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);

            var captureId = light.Capture.CaptureId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var node = await store.ReadNodeAsync(captureId, NodeId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status);
            Assert.HasCount(1, node.Outputs);

            // The window's execution froze its plan across the gain step, and the stored product is what it planned.
            var execution = (await operations.ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
                .Single(state => state.CaptureId == captureId);
            var frozen = await store.ReadNormalizationPlanAsync(execution.ExecutionId, NodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            Assert.AreEqual(stored.IdentitySha256, frozen.Plan.CalibrationFamilyIdentitySha256);
            var selected = frozen.Plan.Inputs
                .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
                .OrderBy(static input => input.WindowPosition)
                .ToArray();
            CollectionAssert.AreEqual(new[] { -1, 0 }, selected.Select(static input => input.WindowPosition).ToArray());
            CollectionAssert.AreEqual(new[] { LowGain, HighGain }, selected.Select(static input => input.Gain).ToArray());
            CollectionAssert.AreEqual(
                new[] { low.Manifest.Descriptor.Artifact.ArtifactId, light.Artifact.ArtifactId },
                selected.Select(static input => input.ArtifactId).ToArray());
            var masters = selected.SelectMany(static input => input.Masters!).Select(static master => master.ArtifactId)
                .ToArray();
            CollectionAssert.AreEquivalent(
                bundles.Values.SelectMany(LibraryMasters).Select(static master => master.ArtifactId).ToArray(),
                masters);
            CollectionAssert.AreEquivalent(
                selected.Select(static input => input.ArtifactId).Concat(masters).ToArray(),
                node.Outputs[0].Descriptor!.Artifact.SourceArtifactIds.ToArray());
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task DrainOneAsync(ServiceProvider provider, CameraModuleConfig configuration)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");
        var lease = await laneStore.ClaimAsync(
            standard, "mixed-gain-window-test", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(
            static candidate => candidate.Lane == "standard");
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().NotifyLiveWorkChanged();
    }

    private static VirtualCalibrationAcquisitionRequestV1 AcquisitionRequest(double gain)
        => new(
            VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
            FormattableString.Invariant($"mixed-gain-references-{gain}"),
            gain,
            VirtualCalibration.Offset,
            VirtualCalibration.TemperatureC,
            VirtualCalibration.BiasExposure,
            VirtualCalibration.DarkExposure,
            VirtualCalibration.FlatExposure,
            VirtualCalibration.DefectExposure,
            LightExposure,
            FixtureUtc.AddDays(-1),
            FixtureUtc.AddYears(1),
            VirtualCalibration.SourceModel,
            "operator",
            "mixed-gain window");

    private static CalibrationFamilyV1 Family(
        ReconstructionDescriptor light,
        IReadOnlyDictionary<double, CalibrationLibraryBundleV1> bundles)
        => new(
            CalibrationFamilyV1.CurrentSchemaVersion,
            "virtual-mixed-gain-family",
            light.Capture.AgentId,
            light.Capture.RigId,
            light.Profiles.Rig.Sha256,
            light.Profiles.Sensor.Sha256,
            light.Profiles.Calibration,
            light.Profiles.Mask.Sha256,
            [.. bundles.OrderBy(static pair => pair.Key).Select(pair => Member(pair.Key, pair.Value, light.Profiles.Mask.Sha256))]);

    private static CalibrationFamilyMemberV1 Member(double gain, CalibrationLibraryBundleV1 bundle, string maskSha256)
    {
        var applicability = bundle.Applicability;
        var masters = LibraryMasters(bundle).ToArray();
        return new CalibrationFamilyMemberV1(
            FormattableString.Invariant($"gain-{gain}"),
            gain,
            applicability.MinimumOffset,
            applicability.InputLayout,
            applicability.MinimumTemperatureC!.Value,
            applicability.MaximumTemperatureC!.Value,
            applicability.MinimumLightExposure!.Value,
            applicability.MaximumLightExposure!.Value,
            applicability.EffectiveFromUtc,
            null,
            maskSha256,
            [.. CalibrationReferenceKinds.All
                .Select(kind => masters.Single(master => master.Kind == kind))
                .Select(static master => new CalibrationFamilyMasterV1(master.Kind, master.ArtifactId, master.PayloadSha256))]);
    }

    private static IEnumerable<CalibrationLibraryArtifactV1> LibraryMasters(CalibrationLibraryBundleV1 bundle)
        => bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master);

    private static async Task<CaptureLoopSubmission> CreateSubmissionAsync(VirtualSkyCameraModule module, int index, double gain)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 5L);
        var setpoint = new CaptureSetpoint(LightExposure, gain, null, VirtualCalibration.TemperatureC);
        var request = new CaptureRequest(startedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint);
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        return new CaptureLoopSubmission(
            request,
            result with
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.Add(LightExposure), startedUtc.Add(LightExposure).AddMilliseconds(100))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog(
            [new CelestialCatalogObject("star", "Star", 2.5, 20, 1)]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateConfiguration(string root)
    {
        var sensor = new SensorProfile(
            "MixedGainWindow", 64, 48, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome, SensorRecipeVersion: "mixed-gain-window-sensor-v1")
        {
            SimulationResponse = new ConfiguredSensorResponseProfile(
                "mixed-gain-window-response-v1",
                16,
                0,
                400,
                4.0,
                200,
                60_000,
                [new SensorReadNoisePoint(0, 2.0), new SensorReadNoisePoint(400, 1.0)],
                0,
                "ZWO 0.1 dB",
                "mixed-gain window test response")
        };
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, 64, 48), 1, 1, FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            0, ushort.MaxValue, 128);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(
                new VirtualSkyCameraModuleOptions
                {
                    MaximumResults = 10,
                    FixedSceneUtc = FixtureUtc,
                    VirtualCalibration = VirtualCalibration
                })),
            new CameraRigConfig(
                sensor,
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), LightExposure, LightExposure, LowGain, LowGain),
                ProfileVersion: "mixed-gain-window-rig-v1",
                Readout: readout),
            new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        "RollingCombination",
                        NodeId,
                        Options: JsonSerializer.SerializeToElement(new
                        {
                            windowSize = 2,
                            recipe = BuiltInProcessingRecipes.MixedGainNormalizedMean,
                            outputVariant = "mixed-gain"
                        }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "ProjectedScene", "scene", DependsOn: ["$raw"],
                        Publication: new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal)),
                    new CaptureProcessingStepConfig(
                        "Storage", "storage", Order: 100, DependsOn: ["scene"],
                        Options: JsonSerializer.SerializeToElement(new { storageRoot = root, queueForUpload = false }))
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "mixed-gain-window-agent");
    }
}
