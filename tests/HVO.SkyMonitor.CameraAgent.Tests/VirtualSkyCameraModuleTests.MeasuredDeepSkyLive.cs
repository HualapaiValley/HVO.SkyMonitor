using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515", Justification = "MSTest requires a public test class.")]
public sealed class MeasuredDeepSkyLiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public Task OrdinaryTwentySecondCadenceReopensExpectedLayerWithAnAssessedEmptyStack() => RunAsync(3, poleAligned: true);

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public Task OrdinaryTwentySecondZenithRetainsRegistrationPairChecks() => RunAsync(3);

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task OrdinaryTwentySecondReferenceRecordsNativeSourcesAndClipping() => RunAsync(1);

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public Task HeldOutDiffuseSignalReopensAsAQualifiedMeasuredLayer() => RunAsync(3, poleAligned: true, injectFixture: true);

    [TestMethod]
    [Timeout(100_000, CooperativeCancellation = true)]
    public Task OptionalDeepSkyAssessmentOverlapsANativeExposure() =>
        RunAsync(3, poleAligned: true, injectFixture: true, compareResources: true);

    private async Task RunAsync(int exposureCount, bool poleAligned = false, bool injectFixture = false, bool compareResources = false)
    {
        var root = Environment.GetEnvironmentVariable("HVO_1926_CATALOG_INPUTS");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set HVO_1926_CATALOG_INPUTS to the directory containing the approved HYG 4.2 and OpenNGC bundles.");
        }

        await VirtualSkyCameraModuleTests.RunMeasuredDeepSkyLiveProofAsync(root, exposureCount,
            poleAligned, injectFixture, compareResources, TestContext).ConfigureAwait(false);
    }
}

public sealed partial class VirtualSkyCameraModuleTests
{
    internal static async Task RunMeasuredDeepSkyLiveProofAsync(string catalogRoot, int exposureCount,
        bool poleAligned, bool injectFixture, bool compareResources, TestContext evidence)
    {
        const string hyg = "hyg-v4.2-p3-s2-r1";
        const string ngc = "openngc-v20260501-p6-s5-r1";
        // A complete bright-star slice of the approved catalog fits the ordinary graph's
        // 64 KiB settings contract while retaining stars across the full field.
        const double magnitude = 3.25;
        var token = evidence.CancellationToken;
        var stars = CatalogSnapshotResolver.ResolveDirectory(Path.Combine(catalogRoot, hyg + ".bundle"),
            new(catalogRoot, "hyg-v42-production") { ExpectedPackageVersion = hyg });
        var objects = CatalogSnapshotResolver.ResolveDirectory(Path.Combine(catalogRoot, ngc + ".bundle"),
            new(catalogRoot, "openngc-production") { ExpectedPackageVersion = ngc });
        var catalog = new InstalledCatalogPair(stars.Catalog, objects);
        var openNgc = catalog.DeepSky?.Semantics.SnapshotIdentity;
        Assert.IsNotNull(openNgc);
        var stellar = await catalog.ReadAsync(magnitude, AstrometricCatalogData.MaximumEntries, token).ConfigureAwait(false);
        Assert.IsTrue(stellar.IsCompleteForRequestedMagnitude);
        Assert.IsNotNull(stellar.Provenance);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            HYG = stellar.Provenance,
            OpenNGC = openNgc,
            stellar.CompletenessMagnitudeLimit,
            StarCount = stellar.Stars.Count
        }));

        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-measured-deep-sky-ordinary");
        try
        {
            var exposure = TimeSpan.FromSeconds(20);
            var cadence = TimeSpan.FromSeconds(24);
            var epoch = new DateTimeOffset(2026, 11, 19, 21, 13, 49, TimeSpan.Zero);
            var fixtureClock = injectFixture ? new CenteredClock { UtcNow = epoch } : null;
            TimeProvider clock = fixtureClock is null ? new MeasuredDeepSkyRealClock(epoch) : fixtureClock;
            var verifyDeadline = poleAligned && !injectFixture;
            const int width = 1936;
            const int height = 1216;
            const int stride = width * sizeof(ushort);
            const double gain = 150;
            var profile = CenteredRuntimeProfile(root, clock.GetUtcNow(), CameraPixelFormat.Mono16);
            // Native mono, optics and illumination from the November deployed #1561 capture.
            var raster = CreateAsi174Config(width, height);
            var locationSeed = new DeploymentLocationSeed("centered-live-test", "virtual", null,
                DateTimeOffset.UnixEpoch, null, new(35.5599378, -113.9119818, 520, "America/Phoenix"));
            var config = profile with
            {
                Rig = profile.Rig with
                {
                    Sensor = raster.Rig.Sensor with { StrideBytes = stride },
                    Optics = raster.Rig.Optics with { HorizontalFlip = true },
                    // At the site latitude looking north, the field rotates almost rigidly
                    // over a sidereal cadence. Use the normal sparse-plate registration path.
                    Orientation = poleAligned ? new(locationSeed.Coordinates.LatitudeDegrees, 0, 0) : raster.Rig.Orientation,
                    Readout = profile.Rig.Readout! with { Roi = new(0, 0, width, height), BlackLevel = 64, StrideBytes = stride },
                    Pipeline = new(cadence, exposure, exposure, gain, gain)
                },
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 2025,
                    illuminationMode = "ControlledNight",
                    magnitudeZeroElectronsPerSecond = 300,
                    maximumMagnitude = 6.5,
                    maximumResults = 2000,
                    bortleClass = 3,
                    asi174Sensor = new { enabled = true, blackLevelAdu = 64 }
                })),
                Pipeline = profile.Pipeline with
                {
                    Steps = BuildProofGraph(profile, stellar, magnitude)
                }
            };
            evidence.WriteLine(JsonSerializer.Serialize(new { config.Rig.Orientation, config.Rig.Optics, exposure, cadence, injectFixture }));
            ReconstructionDescriptor reference;
            PriorMeasuredProofRevision[] priorRevisions = [];
            CenteredViewArchiveBase? expectedOnlyStack = null;
            using (var provider = CreateMeasuredProofProvider(root, clock, catalog))
            {
                await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var location = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(locationSeed, token)
                    .ConfigureAwait(false);
                config = config with { DeploymentLocation = location };
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, config)
                    .ConfigureAwait(false);
                reference = await AcquireProofExposuresAsync(provider, active, clock, catalog, exposureCount, fixtureClock, evidence,
                    verifyDeadline)
                    .ConfigureAwait(false);
                if (poleAligned)
                {
                    var archiveBefore = await provider.GetRequiredService<ICenteredViewLookup>()
                        .ReadArchiveAsync(reference.Capture.CaptureId, token).ConfigureAwait(false);
                    Assert.HasCount(2, archiveBefore);
                    priorRevisions = new PriorMeasuredProofRevision[archiveBefore.Count];
                    for (var position = 0; position < archiveBefore.Count; position++)
                    {
                        var published = archiveBefore[position];
                        priorRevisions[position] = await ReadPriorMeasuredProofRevisionAsync(provider, published.Base.BaseId,
                            published.LatestRevisionId, token).ConfigureAwait(false);
                    }
                }
                if (injectFixture && !compareResources)
                {
                    var expectedOnly = active with
                    {
                        Pipeline = active.Pipeline with
                        { Steps = BuildProofGraph(profile, stellar, magnitude, assessMeasuredDeepSky: false) }
                    };
                    var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
                    var state = await provider.GetRequiredService<SqliteCaptureScheduleStore>().GetSnapshotAsync(token)
                        .ConfigureAwait(false);
                    var staged = await runtime.StageAsync(LocalCaptureProfileDefinition.CreateV2(expectedOnly, expectedOnly.Schedule!),
                        "expected-only-stage", state.Version, "test", null, token).ConfigureAwait(false);
                    await runtime.ActivateAsync(staged.PendingRevision!.RevisionId, "expected-only-activate", staged.Version,
                        "test", null, token).ConfigureAwait(false);
                    var later = await AcquireProofExposuresAsync(provider, runtime.Snapshot!.Configuration, clock, catalog,
                        exposureCount, fixtureClock, evidence).ConfigureAwait(false);
                    var laterArchive = await provider.GetRequiredService<ICenteredViewLookup>()
                        .ReadArchiveAsync(later.Capture.CaptureId, token).ConfigureAwait(false);
                    expectedOnlyStack = Assert.ContainsSingle(laterArchive.Where(view => view.Base.Definition.Kind == CenteredViewKind.Stack));
                    var views = provider.GetRequiredService<SqliteCenteredViews>();
                    var latest = await views.ReadRevisionAsync(expectedOnlyStack.Base.BaseId, expectedOnlyStack.LatestRevisionId, token)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(latest);
                    var expected = Assert.ContainsSingle(latest.Revision.Layers.Where(layer => layer.Key.Kind == "scene-deep-sky"));
                    // Keep both initial revisions behind a full recent page at the next startup.
                    for (var order = 1; order <= 129; order++)
                    {
                        var advanced = await views.PublishLayerAsync(latest.Base.BaseId,
                            expected with { EvidenceOrder = expected.EvidenceOrder + order }, token)
                            .ConfigureAwait(false);
                        Assert.IsNotNull(advanced);
                        expectedOnlyStack = expectedOnlyStack with { LatestRevisionId = advanced.Revision.RevisionId };
                    }
                    var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                    await journal.MarkEvidenceFailureAsync(later.Capture.CaptureId, "missing_evidence", "required-file-missing", token)
                        .ConfigureAwait(false);
                }
            }
            if (exposureCount == 1)
            {
                return;
            }

            var firstId = reference.Capture.CaptureId;
            using var reopened = CreateMeasuredProofProvider(root, clock, catalog);
            var reopenedIngress = reopened.GetRequiredService<RawCaptureIngress>();
            if (expectedOnlyStack is null)
            {
                await reopenedIngress.InitializeAsync(token).ConfigureAwait(false);
            }
            else
            {
                Assert.IsFalse(await ((IRawCaptureIngress)reopenedIngress).InitializeForOperationsAsync(token).ConfigureAwait(false));
            }
            await reopened.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(locationSeed, token).ConfigureAwait(false);
            var archive = await reopened.GetRequiredService<ICenteredViewLookup>().ReadArchiveAsync(firstId, token).ConfigureAwait(false);
            // The first admitted ordinal has a measured solution at the configured stride. At the beginning
            // of a run its two earlier slots are outside-run and its publication is archive-only.
            var window = await reopened.GetRequiredService<ICenteredWindowReader>().ReadAsync(firstId, token).ConfigureAwait(false);
            evidence.WriteLine(JsonSerializer.Serialize(window));
            if (verifyDeadline)
            {
                Assert.IsNotNull(window);
                Assert.AreEqual(window.DeadlineUtc, window.EvidenceCutoffUtc);
            }
            var evidenceId = window?.EvidenceArtifactId;
            Assert.IsNotNull(evidenceId);
            var receipt = await ReadCenteredRegistrationAsync(reopened, evidenceId.Value, token).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var registered = CenteredRegisteredStackEvidence.Read(receipt);
            var output = Environment.GetEnvironmentVariable("HVO_1926_PROOF_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                output = Path.Combine(output, compareResources ? "resource-control" : injectFixture ? "held-out" : poleAligned ? "pole" : "zenith");
            }
            await RecordRegistrationProofAsync(registered, receipt, output, evidence, token).ConfigureAwait(false);
            if (!poleAligned)
            {
                return;
            }

            var stack = Assert.ContainsSingle(archive.Where(view => view.Base.Definition.Kind == CenteredViewKind.Stack));
            var definition = stack.Base.Definition;
            var referenceSha256 = CaptureContractJson.ComputeDescriptorSha256(reference);
            Assert.AreEqual(referenceSha256, CaptureContractJson.ComputeDescriptorSha256(definition.Reference.Input.Raw));
            var registeredReference = registered.Plan.Slots[2].Exposure;
            Assert.IsNotNull(registeredReference);
            Assert.AreEqual(firstId, registeredReference.Key.CaptureId);
            Assert.AreEqual(reference.Artifact.ArtifactId, registeredReference.Raw.ArtifactId);
            Assert.AreEqual(referenceSha256, registeredReference.Raw.DescriptorSha256);
            Assert.AreEqual(reference.Artifact.ChecksumSha256, registeredReference.Raw.PayloadSha256);
            Assert.AreEqual(evidenceId.Value, definition.Science.ArtifactId);
            Assert.AreEqual(receipt.OutputIdentitySha256, definition.Science.IdentitySha256);
            Assert.AreEqual(receipt.ChecksumSha256, definition.Science.PayloadSha256);
            Assert.AreEqual(registered.PlanSha256, definition.Window?.RegistrationPlanIdentitySha256);
            evidence.WriteLine(JsonSerializer.Serialize(new { ReferenceDescriptorSha256 = referenceSha256, definition.Science }));
            var index = reopened.GetRequiredService<SqliteCenteredViews>();
            var assessment = reopened.GetRequiredService<ICenteredMeasuredDeepSky>();
            var scheduler = reopened.GetRequiredService<CenteredMeasuredDeepSkyScheduler>();
            if (expectedOnlyStack is not null)
            {
                var horizon = await index.ReadMeasuredStackRecoveryOrderAsync(128, token).ConfigureAwait(false);
                var initial = await index.ReadRevisionAsync(stack.Base.BaseId, stack.InitialRevisionId, token).ConfigureAwait(false);
                Assert.IsNotNull(initial);
                Assert.IsTrue(initial.Revision.Order <= horizon);
                var expectedReference = await reopenedIngress.ReadCenteredRawAsync(expectedOnlyStack.Base.Definition.Reference.Input.Key,
                    token).ConfigureAwait(false);
                Assert.IsNotNull(expectedReference);
                Assert.IsFalse(expectedReference.IsCommitted);
                Assert.IsFalse(CaptureProjectedSceneStager.ResolveOptions(expectedReference.Run.Configuration)!.AssessMeasuredDeepSky);
                var recoveryPage = await index.ReadMeasuredStackWorkAsync(0, horizon, 128, token).ConfigureAwait(false);
                Assert.IsTrue(recoveryPage.Work.Any(work => work.BaseId == expectedOnlyStack.Base.BaseId));
                evidence.WriteLine(JsonSerializer.Serialize(new { RecoveryHorizon = horizon, InitialOrder = initial.Revision.Order }));
            }
            if (compareResources)
            {
                await CompareAssessmentDuringExposureAsync(reopened, config, catalog, scheduler, evidence)
                    .ConfigureAwait(false);
            }
            await scheduler.StartAsync(token).ConfigureAwait(false);
            var schedulerStopped = false;
            try
            {
                var measuredRevision = await WaitForMeasuredProofAsync(index, stack.Base.BaseId, token).ConfigureAwait(false);
                var read = await assessment.ReadAsync(stack.Base.BaseId, measuredRevision.RevisionId, token).ConfigureAwait(false);
                Assert.IsNotNull(read);
                evidence.WriteLine(JsonSerializer.Serialize(read));
                var presentation = reopened.GetRequiredService<ICenteredViewPresentationReader>();
                var exact = await presentation.ReadAsync(stack.Base.BaseId, measuredRevision.RevisionId, token).ConfigureAwait(false);
                var bytes = await ReadMeasuredProofPreviewAsync(presentation, stack.Base.BaseId, measuredRevision.RevisionId, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(stack.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(bytes));
                var decoded = JpegImageCodec.DecodeJpeg(bytes, token);
                Assert.AreEqual(stack.Base.Definition.Preview.Transform.PreviewWidth, decoded.Width);
                evidence.WriteLine(JsonSerializer.Serialize(new
                {
                    stack.Base.BaseId,
                    measuredRevision.RevisionId,
                    stack.Base.Definition.Window,
                    stack.Base.Definition.Preview.PayloadSha256,
                    decoded.Width,
                    decoded.Height
                }));
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var persistence = reopened.GetRequiredService<CaptureProcessingPersistence>();
                    await ExportMeasuredProofAsync(output, persistence, stack.Base, measuredRevision, read, bytes,
                        exact.Presentation?.Svg, token)
                        .ConfigureAwait(false);
                }
                Assert.AreEqual(openNgc, read.Catalog);
                Assert.AreEqual(firstId, read.ReferenceCaptureId);
                Assert.AreEqual(registered.ReferenceMidpointUtc, read.ReferenceMidpointUtc);
                Assert.AreEqual(definition.Science.ArtifactId, read.Stack.ArtifactId);
                Assert.AreEqual(definition.Science.IdentitySha256, read.Stack.OutputIdentitySha256);
                Assert.AreEqual(definition.Science.PayloadSha256, read.Stack.PayloadSha256);
                Assert.HasCount(3, read.Contributors);
                Assert.IsNotNull(read.MappingSolutionSha256, read.Reason.ToString());
                Assert.IsNotNull(read.Extraction);
                Assert.IsGreaterThan(0, read.Extraction.SupportedPositions);
                Assert.IsGreaterThan(0, read.CatalogCentersWithLocalSupport);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, exact.Status);
                Assert.IsNotNull(exact.Presentation);
                Assert.AreEqual(firstId, exact.Presentation.CaptureId);
                Assert.ContainsSingle(exact.Presentation.Layers.Where(layer => layer.Kind == "scene-deep-sky"));
                if (injectFixture)
                {
                    await VerifyQualifiedReplayAsync(reopened, config.Rig, locationSeed.Coordinates, catalog.DeepSky!,
                        stack.Base, measuredRevision, exact.Presentation, read, token).ConfigureAwait(false);
                }
                else
                {
                    Assert.AreEqual(DeepSkyStackStatus.AssessedEmpty, read.Status, read.Reason.ToString());
                    Assert.AreEqual(read.CatalogCentersInField, read.CatalogCentersWithLocalSupport);
                    Assert.IsFalse(read.Identifications.Any(hit => hit.Status == DeepSkyIdentificationStatus.Qualified));
                    Assert.IsFalse(exact.Presentation.Layers.Any(layer => layer.Kind == "measured-deep-sky"));
                }
                foreach (var prior in priorRevisions)
                {
                    Assert.AreNotEqual(measuredRevision.RevisionId, prior.RevisionId);
                    Assert.AreEqual(prior, await ReadPriorMeasuredProofRevisionAsync(reopened, prior.BaseId,
                        prior.RevisionId, token).ConfigureAwait(false));
                    evidence.WriteLine(JsonSerializer.Serialize(prior));
                }
                if (expectedOnlyStack is not null)
                {
                    await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    schedulerStopped = true;
                    await scheduler.RunOnceAsync(token).ConfigureAwait(false);
                    var expectedOnlyArchive = await reopened.GetRequiredService<ICenteredViewLookup>()
                        .ReadArchiveAsync(expectedOnlyStack.Base.Definition.Reference.Capture.CaptureId, token).ConfigureAwait(false);
                    var expectedOnly = Assert.ContainsSingle(expectedOnlyArchive.Where(view => view.Base.BaseId == expectedOnlyStack.Base.BaseId));
                    Assert.AreNotEqual(definition.Stream.GenerationId, expectedOnly.Base.Definition.Stream.GenerationId);
                    Assert.AreEqual(expectedOnlyStack.LatestRevisionId, expectedOnly.LatestRevisionId);
                    var separate = await presentation.ReadAsync(expectedOnly.Base.BaseId, expectedOnly.LatestRevisionId, token)
                        .ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, separate.Status);
                    Assert.IsNotNull(separate.Presentation);
                    Assert.ContainsSingle(separate.Presentation.Layers.Where(layer => layer.Kind == "scene-deep-sky"));
                    Assert.IsFalse(separate.Presentation.Layers.Any(layer => layer.Kind == "measured-deep-sky"));
                    Assert.IsNull(await assessment.ReadAsync(expectedOnly.Base.BaseId, expectedOnly.LatestRevisionId, token)
                        .ConfigureAwait(false));
                    var exactExpectedOnly = await index.ReadRevisionAsync(expectedOnly.Base.BaseId, expectedOnly.LatestRevisionId, token)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(exactExpectedOnly);
                    Assert.IsFalse(exactExpectedOnly.Revision.Layers.Any(layer => layer.Key.Kind == "measured-deep-sky"));
                    evidence.WriteLine(JsonSerializer.Serialize(new
                    {
                        ExpectedOnlyBaseId = expectedOnly.Base.BaseId,
                        expectedOnly.LatestRevisionId,
                        ExpectedOnlyGraph = expectedOnly.Base.Definition.Stream.GenerationId,
                        ExpectedOnlyRawState = "missing_evidence",
                        MeasuredGraph = definition.Stream.GenerationId
                    }));
                }
            }
            finally
            {
                if (!schedulerStopped)
                    await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RecordRegistrationProofAsync(CenteredRegisteredStackEvidence registered,
        ProcessingProduct receipt, string? output, TestContext evidence, CancellationToken token)
    {
        var pairs = registered.Plan.Slots.Zip(registered.Sources, (slot, source) =>
        {
            var exposure = slot.Exposure;
            var midpoint = exposure?.ExposureStartedUtc + (exposure?.ExposureEndedUtc - exposure?.ExposureStartedUtc) / 2;
            return new
            {
                source.Offset,
                source.CaptureId,
                source.Reason,
                exposure?.GeometrySha256,
                source.Method,
                GapSeconds = (midpoint - registered.ReferenceMidpointUtc)?.TotalSeconds,
                source.SupportedPixels,
                source.Similarity,
                source.PairCheck
            };
        });
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            receipt.OutputIdentitySha256,
            receipt.ChecksumSha256,
            registered.PlanSha256,
            registered.Disposition,
            registered.Width,
            registered.Height,
            registered.ReferenceMidpointUtc,
            Pairs = pairs
        }));
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(Path.Combine(output, "window-registration.json"), receipt.Payload, token)
                .ConfigureAwait(false);
        }
    }

    private static ServiceProvider CreateMeasuredProofProvider(string root, TimeProvider clock, ICelestialCatalog catalog) =>
        CenteredRuntimeProvider(root, clock, services => services
            .AddSingleton(catalog)
            .AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>()
            .AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning)));

    private static CaptureProcessingStepConfig[] BuildProofGraph(CameraModuleConfig profile, AstrometricCatalogData catalog, double magnitude,
        bool assessMeasuredDeepSky = true)
    {
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var scene = profile.Pipeline.Steps.Single(step => step.Type == "ProjectedScene") with
        {
            Options = JsonSerializer.SerializeToElement(new { maximumMagnitude = magnitude, maximumResults = 2000, deepSkyEnabled = true, assessMeasuredDeepSky })
        };
        var snapshot = new AstrometryCatalogSnapshotV1(catalog.Metadata, catalog.Stars, catalog.Provenance!,
            catalog.IsCompleteForRequestedMagnitude, catalog.CompletenessMagnitudeLimit, catalog.CoordinateModel);
        return
        [
            .. profile.Pipeline.Steps.Where(step => step.Type != "ProjectedScene"), scene,
            new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable),
            new("MeasuredAstrometry", "fit", DependsOn: ["scene", "associated"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions
                {
                    Catalog = snapshot,
                    Recipe = new(Solver: new(MaximumCatalogMagnitude: magnitude), MaximumExecutionMilliseconds: 5000)
                })),
            new("ScenePresentationLayer", "sky-layers", DependsOn: ["scene"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new { includeDeepSkyLayer = true }))
        ];
    }

    private static async Task<ReconstructionDescriptor> AcquireProofExposuresAsync(ServiceProvider provider, CameraModuleConfig config,
        TimeProvider clock, ICelestialCatalog catalog, int exposureCount, CenteredClock? fixtureClock, TestContext evidence,
        bool verifyDeadline = false)
    {
        var token = evidence.CancellationToken;
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
        var fixtureDevice = fixtureClock is null ? null : new ExposureTestTimeProvider(clock.GetUtcNow(), autoAdvance: true);
        var camera = new VirtualSkyCameraModule(fixtureDevice ?? clock, catalog, new ProjectedSceneStore(),
            stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
        await using var lease = camera.ConfigureAwait(false);
        await camera.InitializeAsync(config, token).ConfigureAwait(false);
        var cadence = config.Rig.Pipeline.CaptureInterval;
        var exposure = config.Rig.Pipeline.NightExposure;
        var gain = config.Rig.Pipeline.NightGain;
        var setpoint = new CaptureSetpoint(exposure, gain, null, null);
        var start = clock.GetUtcNow().AddSeconds(1);
        ReconstructionDescriptor? reference = null;
        var pending = Task.CompletedTask;
        var previousAcquisitionDuration = TimeSpan.Zero;
        try
        {
            for (var index = 0; index < exposureCount; index++)
            {
                var scheduled = start + index * cadence;
                if (fixtureClock is not null)
                {
                    fixtureClock.UtcNow = scheduled;
                    fixtureDevice!.Advance(scheduled - fixtureDevice.GetUtcNow());
                }
                var reservation = await ingress.ReserveCenteredAsync(config, resources, null, token).ConfigureAwait(false);
                var request = new CaptureRequest(scheduled, cadence, CaptureMode.Still, setpoint)
                {
                    BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                    BoundGraph = reservation.Revision.Pipeline
                };
                CenteredDispatchSchedule? dispatch = verifyDeadline
                    ? new(scheduled, cadence, config.Rig.Pipeline.CadenceMode, PreviousAcquisitionDuration: previousAcquisitionDuration)
                    : null;
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "measured-deep-sky-proof", token,
                    scheduled, cadence, schedule: dispatch).ConfigureAwait(false);
                var delay = scheduled - clock.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                var began = Stopwatch.GetTimestamp();
                var capture = await camera.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(began);
                if (fixtureClock is null)
                {
                    Assert.IsGreaterThanOrEqualTo(exposure, elapsed);
                }
                else
                {
                    var timing = capture.AcquisitionTiming!;
                    fixtureClock.UtcNow = timing.ReadoutCompletedUtc.AddMilliseconds(1);
                    elapsed = timing.ReadoutCompletedUtc - scheduled;
                    capture = InjectHeldOutDiffuse(capture, config);
                }
                previousAcquisitionDuration = elapsed;
                var receipt = await ingress.AcceptCenteredAsync(config, new(request, capture, scheduled, cadence, elapsed), reservation, token)
                    .ConfigureAwait(false);
                var raw = receipt.Manifest.Descriptor;
                Assert.AreEqual(exposure, raw.Controls.EffectiveExposure);
                Assert.AreEqual(gain, raw.Controls.EffectiveGain);
                if (index == 0)
                {
                    reference = raw;
                }
                evidence.WriteLine(JsonSerializer.Serialize(new
                {
                    raw.Capture.CaptureId,
                    raw.Capture.CaptureSequence,
                    raw.Timing,
                    raw.Artifact.ChecksumSha256,
                    scheduled,
                    elapsed,
                    cadence
                }));

                // Keep at most one lane task in flight while the next real exposure starts.
                // A preview or astrometry step cannot shift the configured shutter calendar.
                await pending.ConfigureAwait(false);
                var key = reservation.Pending.Key.Camera;
                var mappingReference = index == 0 ? raw.Capture.CaptureId : (Guid?)null;
                if (fixtureClock is null)
                {
                    pending = Task.Run(() => SettleAsync(key, mappingReference), token);
                }
                else
                {
                    await SettleAsync(key, mappingReference).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await pending.ConfigureAwait(false);
        }
        Assert.IsNotNull(reference);
        if (verifyDeadline)
        {
            await CloseMeasuredProofAtDeadlineAsync(provider, clock, reference.Capture.CaptureId, cadence, exposure, evidence)
                .ConfigureAwait(false);
        }
        return reference;

        async Task SettleAsync(CameraKey key, Guid? mappingReference)
        {
            await ProcessCenteredRuntimeExposureAsync(provider, config, token).ConfigureAwait(false);
            var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(key, token).ConfigureAwait(false));
            Assert.IsNotNull(stream);
            if (mappingReference is { } reference)
            {
                await RequireProofMappingAsync(provider, stream, reference, evidence).ConfigureAwait(false);
            }
            await provider.GetRequiredService<CenteredViewPublicationWorker>().DrainCameraAsync(key, stream, CancellationToken.None, token)
                .ConfigureAwait(false);
            if (!verifyDeadline)
            {
                await provider.GetRequiredService<CenteredWindowCoordinator>().CloseReadyAsync(token).ConfigureAwait(false);
                await provider.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);
                await provider.GetRequiredService<CenteredViewWindowPublicationWorker>().PublishReadyAsync(token).ConfigureAwait(false);
            }
        }
    }

    private static async Task CloseMeasuredProofAtDeadlineAsync(ServiceProvider provider, TimeProvider clock, Guid reference,
        TimeSpan cadence, TimeSpan exposure, TestContext evidence)
    {
        var token = evidence.CancellationToken;
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var tracked = await store.ReadCenteredWindowStateAsync(reference, token).ConfigureAwait(false);
        Assert.IsNotNull(tracked);
        Assert.IsNull(tracked.Closed);
        var checkpoint = tracked.Checkpoint;
        Assert.IsNotNull(checkpoint);
        var calendar = checkpoint.DispatchCalendar;
        Assert.IsNotNull(calendar);
        Assert.AreEqual(cadence, calendar.ForecastInterval);
        var schedule = checkpoint.Schedule;
        Assert.AreEqual(schedule.ReferenceStartUtc + cadence + cadence + exposure + TimeSpan.FromSeconds(5), schedule.DeadlineUtc);

        // Let the persisted deadline close the window after all three exposures have settled.
        var beforeDeadline = clock.GetUtcNow();
        Assert.IsLessThan(schedule.DeadlineUtc, beforeDeadline);
        for (var remaining = schedule.DeadlineUtc - beforeDeadline; remaining > TimeSpan.Zero;
            remaining = schedule.DeadlineUtc - clock.GetUtcNow())
        {
            await Task.Delay(remaining, clock, token).ConfigureAwait(false);
        }
        var deadlineSweepUtc = clock.GetUtcNow();
        await provider.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
        await provider.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);
        await provider.GetRequiredService<CenteredViewWindowPublicationWorker>().PublishReadyAsync(token).ConfigureAwait(false);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            schedule.ReferenceStartUtc,
            schedule.DeadlineUtc,
            calendar.ForecastInterval,
            BeforeDeadlineUtc = beforeDeadline,
            DeadlineSweepUtc = deadlineSweepUtc
        }));
    }

    private static async Task RequireProofMappingAsync(ServiceProvider provider, CenteredViewStream stream, Guid capture,
        TestContext evidence)
    {
        var token = evidence.CancellationToken;
        var execution = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(capture, stream.GenerationId);
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var sources = await store.ReadCenteredViewSourcesAsync(new(stream.Camera, stream.RunId, capture), execution, 0, 24, token)
            .ConfigureAwait(false);
        var detail = await provider.GetRequiredService<IProcessingGraphOperations>().ReadExecutionDetailAsync(execution, token)
            .ConfigureAwait(false);
        evidence.WriteLine(JsonSerializer.Serialize(detail?.Nodes.Select(node => new { node.NodeId, node.Status, node.Reason })));
        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
        var measurementSource = Assert.ContainsSingle(sources.Where(item => item.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion));
        Assert.IsNotNull(measurementSource.Product, measurementSource.Reason);
        var measured = measurementSource.Product;
        var (_, measurement) = await persistence.ReadCenteredViewMeasurementAsync(capture,
            new(measured.ArtifactId, measured.OutputIdentitySha256, measured.PayloadSha256), token).ConfigureAwait(false);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            measurement.Input.Raw.Layout,
            measurement.Input.ApertureProjection,
            measurement.RawBrightness,
            measurement.LinearOutputClipping,
            measurement.Sources.Status,
            measurement.Sources.CandidateCount,
            MeasuredSources = measurement.Sources.Detections.Count,
            Excluded = measurement.Sources.Exclusions.GroupBy(exclusion => exclusion.ReasonCode)
                .ToDictionary(group => group.Key, group => group.Count())
        }));
        Assert.IsNotNull(measurement.RawBrightness);
        Assert.AreEqual(1936L * 1216, measurement.RawBrightness.SampleCount);
        Assert.AreEqual((ushort)4095, measurement.RawBrightness.SensorClipping.StoredWhite);
        Assert.IsNotNull(measurement.RawBrightness.SensorClipping.Count);
        var source = Assert.ContainsSingle(sources.Where(item => item.Slot.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion));
        Assert.IsNotNull(source.Product, source.Reason);
        var product = await persistence.ReadCenteredViewRetainedProductAsync(capture,
            new(source.Product.ArtifactId, source.Product.OutputIdentitySha256, source.Product.PayloadSha256), token).ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            solution.SolutionIdentitySha256,
            solution.Assessment.Status,
            solution.Assessment.ReasonCode,
            solution.Assessment.Quality,
            solution.Assessment.Frame.MidpointUtc,
            solution.Residuals.QualifiedCount
        }));
        Assert.IsTrue(solution.Assessment.HasMeasuredMapping, solution.Assessment.ReasonCode);
    }

    private static async Task<CenteredViewRevision> WaitForMeasuredProofAsync(SqliteCenteredViews index, string baseId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            var revisions = await index.ReadRevisionsAsync(baseId, 0, 32, timeout.Token).ConfigureAwait(false);
            if (revisions.Count > 0 && revisions[^1] is { } revision &&
                revision.Layers.Any(layer => layer.Key.Kind == "measured-deep-sky"))
            {
                return revision;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task ExportMeasuredProofAsync(string path, CaptureProcessingPersistence persistence, CenteredViewBase view,
        CenteredViewRevision revision, MeasuredDeepSkyStackV1 assessment, byte[] jpeg, ReadOnlyMemory<byte>? svg,
        CancellationToken token)
    {
        var root = Path.Combine(path, view.BaseId, revision.RevisionId);
        Directory.CreateDirectory(root);
        var assessmentPin = Assert.ContainsSingle(revision.Layers.Where(layer => layer.Key.Kind == "measured-deep-sky")).Evidence;
        Assert.IsNotNull(assessmentPin);
        var capture = view.Definition.Reference.Capture.CaptureId;
        var product = await persistence.ReadCenteredViewRetainedProductAsync(capture, assessmentPin, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(root, "assessment.json"), product.Payload, token).ConfigureAwait(false);
        var stack = await persistence.ReadCenteredViewRetainedProductAsync(capture, view.Definition.Science, token).ConfigureAwait(false);
        var registered = CenteredRegisteredStackEvidence.Read(stack);
        var planes = new List<ProcessingProduct>();
        foreach (var pin in assessment.Planes)
        {
            var retained = await persistence.ReadCenteredViewRetainedProductAsync(capture,
                new(pin.ArtifactId, pin.OutputIdentitySha256, pin.PayloadSha256), token).ConfigureAwait(false);
            planes.Add(retained);
        }
        registered.VerifyPlanes(planes, token);
        await File.WriteAllBytesAsync(Path.Combine(root, "registration.json"), stack.Payload, token).ConfigureAwait(false);
        for (var index = 0; index < planes.Count; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(root, $"plane-{index}.bin"), planes[index].Payload, token).ConfigureAwait(false);
        }
        await File.WriteAllBytesAsync(Path.Combine(root, "preview.jpg"), jpeg, token).ConfigureAwait(false);
        if (svg is { } rendered)
            await File.WriteAllBytesAsync(Path.Combine(root, "presentation.svg"), rendered, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, "publication.json"),
            JsonSerializer.Serialize(new { view, revision, assessmentPin }), token).ConfigureAwait(false);
    }

    private sealed class MeasuredDeepSkyRealClock(DateTimeOffset start) : TimeProvider
    {
        private readonly long _began = Stopwatch.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => start + Stopwatch.GetElapsedTime(_began);
    }
}
