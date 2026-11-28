using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow(true, false, DisplayName = "Previously committed profile admits raw and canonical scene on restart")]
    [DataRow(true, true, DisplayName = "Previously committed profile admits raw through VirtualSky scene migration")]
    [DataRow(false, false, DisplayName = "New crossed profile is rejected before bootstrap commit")]
    public async Task CrossedAstrometry_ActiveProfileRestartsAndAdmitsFreshRawWithCanonicalScene(bool persistedProfile, bool legacyScene)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-crossed-astrometry-startup");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = timeout.Token;
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
            var location = DeploymentLocationSnapshot.Create("crossed-site", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, 35, -115, 1100, "UTC");
            var locationStore = new Mock<IDeploymentLocationStore>();
            locationStore.SetupGet(value => value.Active).Returns(location);
            locationStore.SetupGet(value => value.RecentRetainedEvidenceReady).Returns(true);
            locationStore.Setup(value => value.Resolve(It.IsAny<CaptureLocationProvenance>(), It.IsAny<DateTimeOffset?>()))
                .Returns(location);
            var baseline = CreateProjectedSceneConfiguration();
            var file = baseline with
            {
                Observatory = new(35, -115, 1100, "UTC"),
                DeploymentLocation = location,
                Rig = baseline.Rig with
                {
                    Sensor = new SensorProfile("test", 64, 48, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                        ResponseMode: SensorResponseMode.Monochrome),
                    Optics = new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                        PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23,
                        CalibrationVersion: "projection-v1"),
                    Readout = new SensorReadoutProfile(new(0, 0, 64, 48), 1, 1, FrameBinningAlgorithm.IdentityV1,
                        CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1,
                        FrameLevelCodeSpace.StoredContainer, 0, ushort.MaxValue),
                    ControlPolicy = new() { ExposureControl = AutomaticControlOwnership.Disabled, GainControl = AutomaticControlOwnership.Disabled }
                },
                Schedule = new("capture-schedule-v1", [new("night", TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(1))],
                    [new("monday", DayOfWeek.Monday, new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(0, 0)),
                        new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")])
            };
            var source = CreateReferenceCatalog();
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var canonical = file.Pipeline.Steps.Single() with
            {
                Publication = durable,
                Options = legacyScene ? JsonSerializer.SerializeToElement(new { independentPrediction = true }) : null
            };
            var storage = new CaptureProcessingStepConfig("Storage", "archive", DependsOn: ["$raw", canonical.Id!],
                Options: JsonSerializer.SerializeToElement(new SiloCaptureProcessingStepOptions
                { UpdateLatestFrame = false }));
            file = file with { Pipeline = file.Pipeline with { Steps = [canonical, storage] } };
            var (generated, _) = await ReferenceAstrometryPreset.ApplyAsync(file.Pipeline, 3.25, source, token).ConfigureAwait(false);
            var crossed = generated with
            {
                Steps = generated.Steps.Select(step => step.Type == "MeasuredStellarAssociations"
                    ? step with
                    {
                        DependsOn = [canonical.Id!],
                        Options = JsonSerializer.SerializeToElement(new { outputVariant = "canonical-measurement" })
                    }
                    : step).ToArray()
            };
            var committedConfiguration = file with { Pipeline = crossed };
            var loader = new Mock<ICameraAgentConfigurationLoader>();
            loader.Setup(value => value.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(committedConfiguration);
            void Configure(IServiceCollection services)
            {
                services.AddSingleton(locationStore.Object);
                services.AddSingleton(source);
                services.AddSingleton<ICelestialCatalog>(new QueryFixtureCatalog());
                services.AddSingleton(loader.Object);
                services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
            }

            if (!persistedProfile)
            {
                using var fresh = CreateProvider(root, configureServices: Configure);
                var initializer = fresh.GetRequiredService<CameraAgentConfigurationInitializer>();
                var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initializer.StartAsync(token)).ConfigureAwait(false);
                StringAssert.Contains(failure.Message, "must depend on the same scene producer", StringComparison.Ordinal);
                Assert.IsFalse(fresh.GetRequiredService<ICameraAgentConfigurationAccessor>().IsConfigured);
                var runtimeFailure = await Assert.ThrowsExactlyAsync<CaptureProfileCompatibilityException>(() =>
                    fresh.GetRequiredService<CaptureScheduleRuntimeCoordinator>().InitializeAsync(committedConfiguration, token)).ConfigureAwait(false);
                Assert.AreEqual(failure.Message, runtimeFailure.InnerException?.Message);
                var missingState = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    fresh.GetRequiredService<SqliteCaptureScheduleStore>().GetSnapshotAsync(token)).ConfigureAwait(false);
                Assert.AreEqual("Capture schedule state has not been initialized.", missingState.Message);
                return;
            }

            CaptureScheduleRevisionSnapshot committedProfile;
            ProcessingGraphRevisionSnapshot committedGraph;
            using (var predecessor = CreateProvider(root, configureServices: Configure))
            {
                // The retained compiler still produces the predecessor's frozen graph. Persist it and the active
                // schedule using the production stores, then reopen them with a new host service provider.
                var schedule = predecessor.GetRequiredService<SqliteCaptureScheduleStore>();
                committedProfile = (await schedule.InitializeAsync(committedConfiguration, token).ConfigureAwait(false)).ActiveRevision;
                committedGraph = await predecessor.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                    .SelectLiveRevisionAsync(committedConfiguration, retainedCapture: true, token).ConfigureAwait(false);
            }

            using var restarted = CreateProvider(root, configureServices: Configure);
            await restarted.GetRequiredService<CameraAgentConfigurationInitializer>().StartAsync(token).ConfigureAwait(false);
            var activeConfiguration = await restarted.GetRequiredService<ICameraAgentConfigurationAccessor>()
                .WaitForConfigurationAsync(token).ConfigureAwait(false);
            var runtime = await restarted.GetRequiredService<CaptureScheduleRuntimeCoordinator>()
                .InitializeAsync(activeConfiguration, token).ConfigureAwait(false);
            Assert.AreEqual(committedProfile.RevisionId, runtime.Revision.RevisionId);
            Assert.AreEqual(committedProfile.ProfileSha256, runtime.Revision.ProfileSha256);

            var operations = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await operations.CreateRevisionAsync(
                "crossed", "new", crossed, "create", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);
            var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(now, autoAdvance: true),
                restarted.GetRequiredService<ICelestialCatalog>(), restarted.GetRequiredService<IProjectedSceneStore>(),
                stagingStore: restarted.GetRequiredService<IProjectedSceneStagingStore>(),
                graphSelection: new VirtualSkyGraphSelectionProvider(operations, enabled: true));
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(runtime.Configuration, token).ConfigureAwait(false);
            var request = new CaptureRequest(now, TimeSpan.FromSeconds(1), CaptureMode.Still);
            var capture = await module.CaptureAsync(request, token).ConfigureAwait(false);
            var receipt = await restarted.GetRequiredService<IRawCaptureIngress>().AcceptAsync(runtime.Configuration,
                new CaptureLoopSubmission(request, capture, now, request.TargetInterval, TimeSpan.Zero), token).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
            Assert.AreEqual(1L, receipt.Manifest.Descriptor.Capture.CaptureSequence);
            Assert.IsTrue(File.Exists(receipt.StoredFrame.AbsolutePath));
            var store = restarted.GetRequiredService<SqliteCaptureProcessingStore>();
            var originalGraph = await store.ReadRevisionAsync(committedGraph.State.RevisionId, token).ConfigureAwait(false);
            CollectionAssert.AreEqual(committedGraph.FrozenPlanJson, originalGraph.FrozenPlanJson);
            if (legacyScene)
            {
                var successor = await store.ReadRevisionAsync(receipt.Manifest.CaptureGraphRevisionId!, token).ConfigureAwait(false);
                Assert.AreEqual(committedGraph.State.RevisionId, successor.State.Revision);
                Assert.AreNotEqual(committedGraph.State.RevisionId, successor.State.RevisionId);
            }
            else Assert.AreEqual(committedGraph.State.RevisionId, receipt.Manifest.CaptureGraphRevisionId);
            var retainedGraph = restarted.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(runtime.Configuration);
            try { Assert.IsTrue(committedGraph.IsCompiledBy(retainedGraph)); }
            finally { retainedGraph.DisposeSteps(); }

            await CompleteLiveAsync(restarted, runtime.Configuration).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            var solver = await store.ReadNodeAsync(captureId, "Astrometry", token).ConfigureAwait(false);
            Assert.IsNotNull(solver);
            Assert.AreEqual(DurableProcessingNodeStatus.Skipped, solver.Status);
            Assert.AreEqual("astrometry.crossed-scene-dependencies", solver.Reason);
            Assert.IsEmpty(solver.Outputs);
            var canonicalOutput = await store.ReadNodeAsync(captureId, canonical.Id!, token).ConfigureAwait(false);
            Assert.IsNotNull(canonicalOutput);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, canonicalOutput.Status, canonicalOutput.Reason);
            Assert.HasCount(1, canonicalOutput.Outputs);
            var associations = await store.ReadNodeAsync(captureId, "Measured", token).ConfigureAwait(false);
            Assert.IsNotNull(associations);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, associations.Status, associations.Reason);
            var archived = await store.ReadNodeAsync(captureId, storage.Id!, token).ConfigureAwait(false);
            Assert.IsNotNull(archived);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, archived.Status, archived.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
