using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly DateTimeOffset RegionalNightUtc = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero); // New moon night in Arizona
    private static readonly DateTimeOffset RegionalDayUtc = new(2026, 11, 9, 19, 0, 0, TimeSpan.Zero); // Noon local in Arizona
    private static readonly DateTimeOffset RegionalBrightMoonUtc = new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero); // Full moon high in Arizona sky

    [TestMethod]
    public async Task RegionalStellarVisibility_CoexistsWithCloudAssessmentAndSurvivesColdRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regional-vis-coexist");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);

        try
        {
            var profile = CreateRegionalVisibilityProfile(
                root,
                RegionalNightUtc,
                CameraPixelFormat.Mono16,
                includeClearReferenceCloudAssessment: true);
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4);

            Guid captureId;
            Guid executionId;
            string originalAssessmentIdentity;
            CenteredViewArchiveBase archived;

            using (var provider = RegionalVisibilityProvider(root, clock, profile.DeploymentLocation!, catalog))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var operations = provider.GetRequiredService<IProcessingGraphOperations>();

                // Verify ProcessingGraphDraftModel / ProcessingNodeOptionsForm round-trip for step 30b.
                var registry = await operations.GetRegistryAsync(token).ConfigureAwait(false);
                var activePipeline = (await operations.ReadRevisionPipelineAsync(registry.ActiveRevisionId, token).ConfigureAwait(false))!;
                var draft = ProcessingGraphDraftModel.FromPipeline(activePipeline, registry.ActiveRevisionId, "regional-test", "2");
                var stepRow = draft.Nodes.Single(node => node.Type == RegionalStellarVisibilityProducts.StepAlias);
                Assert.IsNotNull(stepRow.OptionsForm);
                Assert.AreSame(RegionalStellarVisibilityOptionDescriptors.Descriptor, stepRow.OptionsForm.Descriptor);
                Assert.IsTrue(draft.TryBuild(out var rebuilt, out var draftErrors), string.Join("; ", draftErrors));
                Assert.IsFalse(draft.HasGraphChanges(rebuilt));

                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);

                var (receipt, reservation) = await CaptureAndAdmitRegionalAsync(
                    provider, ingress, module, deviceClock, clock, active, RegionalNightUtc, token).ConfigureAwait(false);
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;

                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

                var liveExecutionId = await operations.ReadLiveExecutionIdAsync(captureId, token).ConfigureAwait(false);
                Assert.IsNotNull(liveExecutionId);
                executionId = liveExecutionId.Value;

                var ui = RegionalVisibilityOperations(provider, operations);
                var detailResult = await ui.GetExecutionDetailAsync(executionId, token).ConfigureAwait(false);
                Assert.IsTrue(detailResult.IsSuccess, detailResult.Message);
                var detail = detailResult.Value!;

                // Both step 30a (CloudAssessment) and step 30b (RegionalStellarVisibility) ran on the same exposure with separate provenance!
                var cloudNode = detail.Nodes.Single(node => node.NodeId == "cloud");
                var visibilityNode = detail.Nodes.Single(node => node.NodeId == "regional-visibility");
                Assert.AreEqual("Completed", cloudNode.Status);
                Assert.AreEqual("Completed", visibilityNode.Status);
                Assert.AreNotEqual(cloudNode.Outputs.Single().ArtifactId, visibilityNode.Outputs.Single().ArtifactId);
                Assert.AreNotEqual(cloudNode.Outputs.Single().OutputIdentitySha256, visibilityNode.Outputs.Single().OutputIdentitySha256);

                var visibilityRead = detail.RegionalStellarVisibility;
                Assert.IsNotNull(visibilityRead);
                Assert.IsNull(visibilityRead.UnavailableReason);
                var assessment = visibilityRead.Assessment!;
                originalAssessmentIdentity = assessment.AssessmentIdentitySha256;

                Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessment.Status, string.Join(',', assessment.ReasonCodes));
                Assert.AreEqual(RegionalStellarVisibilityV1.CurrentMethodId, assessment.MethodId);
                Assert.AreEqual(RegionalVisibilityGeometryBasis.FixedGeometry, assessment.GeometryBasis);
                Assert.AreEqual(captureId, assessment.Sources.CaptureId);
                Assert.AreEqual(receipt.Manifest.Descriptor.Artifact.ArtifactId, assessment.Sources.RawArtifactId);
                Assert.IsNotNull(assessment.Sources.MeasurementArtifactId);
                Assert.IsNotNull(assessment.Sources.PredictionArtifactId);
                Assert.IsGreaterThan(0, assessment.Grid.ClearTileCount);
                Assert.AreEqual(0, assessment.Grid.DeficitTileCount);
                Assert.AreEqual(16, assessment.Tiles.Count);

                // Publish centered view and verify regional-stellar-visibility layer publishes Ready.
                var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, captureId);
                var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                var stream = new CenteredViewStream(key.Camera, key.RunId, reservation.Revision.State.RevisionId);
                var initial = await provider.GetRequiredService<ICenteredViewProducer>().PublishSingleAsync(
                    stream, linear.Product.Slot.ExecutionId, receipt.Manifest.Descriptor.Capture.CaptureSequence, linear,
                    new(MaximumDimension: 224), token).ConfigureAwait(false);
                Assert.IsNotNull(initial);
                var view = await DrainCenteredReferenceAsync(provider, key, token).ConfigureAwait(false);
                var visibilityLayer = view.Revision.Layers.Single(layer => layer.Key.Kind == RegionalStellarVisibilityProducts.LayerKind);
                Assert.AreEqual(CenteredViewReadiness.Ready, visibilityLayer.Readiness, visibilityLayer.Reason);
                Assert.IsNotNull(visibilityLayer.Drawable);
                archived = Assert.ContainsSingle(await provider.GetRequiredService<SqliteCenteredViews>()
                    .ReadArchiveAsync(captureId, token).ConfigureAwait(false));
            }

            // Cold restart: reopen from disk without running any new capture or recomputation.
            using (var reopened = RegionalVisibilityProvider(root, clock, profile.DeploymentLocation!, catalog))
            {
                await StoragePolicyRawIngressIntegrationTests.InitializeAsync(reopened, profile).ConfigureAwait(false);
                var reopenedOps = reopened.GetRequiredService<IProcessingGraphOperations>();
                var reopenedUi = RegionalVisibilityOperations(reopened, reopenedOps);
                var reopenedDetail = await reopenedUi.GetExecutionDetailAsync(executionId, token).ConfigureAwait(false);
                Assert.IsTrue(reopenedDetail.IsSuccess, reopenedDetail.Message);
                var restored = reopenedDetail.Value!.RegionalStellarVisibility?.Assessment;
                Assert.IsNotNull(restored);
                Assert.AreEqual(originalAssessmentIdentity, restored.AssessmentIdentitySha256);
                Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, restored.Status);
                Assert.AreEqual(0, restored.Grid.DeficitTileCount);
                Assert.IsGreaterThan(0, restored.Grid.ClearTileCount);

                var reopenedArchive = Assert.ContainsSingle(await reopened.GetRequiredService<SqliteCenteredViews>()
                    .ReadArchiveAsync(captureId, token).ConfigureAwait(false));
                Assert.AreEqual(archived.LatestRevisionId, reopenedArchive.LatestRevisionId);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, "clear", DisplayName = "RegionalStellarVisibility_HeldOut_ClearMono16")]
    [DataRow(CameraPixelFormat.BayerRggb16, "clear", DisplayName = "RegionalStellarVisibility_HeldOut_ClearBayerRggb16")]
    [DataRow(CameraPixelFormat.Mono16, "patchy", DisplayName = "RegionalStellarVisibility_HeldOut_PatchyCloudMono16")]
    [DataRow(CameraPixelFormat.BayerRggb16, "patchy", DisplayName = "RegionalStellarVisibility_HeldOut_PatchyCloudBayerRggb16")]
    [DataRow(CameraPixelFormat.Mono16, "overcast", DisplayName = "RegionalStellarVisibility_HeldOut_OvercastMono16")]
    public async Task RegionalStellarVisibility_HeldOutVirtualSky_ClearPatchyOvercastAndBayer(
        CameraPixelFormat format, string scenario)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-regvis-{scenario}");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);

        var profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, format);
        (int Column, int Row)[] clearTiles = [];
        (int Column, int Row)[] opaqueTiles = [];
        if (scenario == "patchy")
        {
            VirtualCloudScenarioDefinition patchy;
            (patchy, clearTiles, opaqueTiles) = FindHeldOutPatchyCloudScenario(profile, RegionalNightUtc);
            profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, format, cloudScenario: patchy);
        }
        else if (scenario == "overcast")
        {
            var overcast = new VirtualCloudScenarioDefinition
            {
                ScenarioId = "heldout-overcast-1925",
                Seed = 9912,
                EpochUtc = RegionalNightUtc,
                SpatialFrequency = 1.5,
                Octaves = 2,
                EdgeSoftness = 0.05,
                HorizonFadeDegrees = 0,
                TemporalSampleCount = 1,
                Keyframes = [new VirtualCloudKeyframe { OffsetSeconds = 0, Coverage = 1.0, MaximumOpacity = 0.99, ScatterFraction = 0.08 }]
            };
            profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, format, cloudScenario: overcast);
        }

        try
        {
            var testTiles = scenario == "patchy" ? clearTiles.Concat(opaqueTiles).ToHashSet() : null;
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4, tiles: testTiles);
            var assessment = await RunSingleRegionalVisibilityAsync(root, clock, deviceClock, profile, catalog, RegionalNightUtc, token)
                .ConfigureAwait(false);

            TestContext.WriteLine(
                $"{format}/{scenario}: status={assessment.Status}, clear={assessment.Grid.ClearTileCount}, " +
                $"deficit={assessment.Grid.DeficitTileCount}, lowSupport={assessment.Grid.InsufficientEvidenceTileCount}, " +
                $"excluded={assessment.Grid.ExcludedTileCount}, expected={assessment.Grid.TotalExpectedDetectableStars}, " +
                $"matched={assessment.Grid.TotalMatchedMeasuredStars}, marginal={assessment.Grid.TotalMarginalStars}, " +
                $"masked={assessment.Grid.TotalMaskedOutStars}, unassoc={assessment.Grid.TotalUnassociatedMeasuredStars}, " +
                $"bg={assessment.Conditions.MeasuredBackgroundAdu}, noise={assessment.Conditions.MeasuredNoiseSigmaAdu}, " +
                $"hfd={assessment.Conditions.MeasuredMedianHfdPixels}, shift={assessment.Conditions.EstimatedShiftPixels}, " +
                $"roll={assessment.Conditions.EstimatedRollDegrees}, reasons=[{string.Join(',', assessment.ReasonCodes)}]");
            foreach (var tile in assessment.Tiles.Where(t => t.PredictedStarCount > 0))
            {
                TestContext.WriteLine(
                    $"  tile({tile.Column},{tile.Row}): {tile.Status} pred={tile.PredictedStarCount} exp={tile.ExpectedDetectableCount} " +
                    $"measExcl={tile.MeasurementExcludedCount} matched={tile.MatchedMeasuredCount} offset={tile.OffsetCandidateCount} " +
                    $"reasons=[{string.Join(',', tile.ReasonCodes)}]");
            }

            Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessment.Status, string.Join(',', assessment.ReasonCodes));
            Assert.AreEqual(format, assessment.Conditions.PixelFormat);
            Assert.IsGreaterThan(0, assessment.Grid.AssessedTileCount);

            switch (scenario)
            {
                case "clear":
                    Assert.AreEqual(0, assessment.Grid.DeficitTileCount);
                    Assert.IsGreaterThanOrEqualTo(4, assessment.Grid.ClearTileCount);
                    break;
                case "patchy":
                    CollectionAssert.AreEquivalent(opaqueTiles, Coordinates(RegionalVisibilityTileStatus.DetectionDeficit));
                    CollectionAssert.AreEquivalent(clearTiles, Coordinates(RegionalVisibilityTileStatus.Clear));
                    break;
                case "overcast":
                    Assert.IsGreaterThanOrEqualTo(4, assessment.Grid.DeficitTileCount);
                    Assert.AreEqual(0, assessment.Grid.ClearTileCount);
                    break;
            }

            (int Column, int Row)[] Coordinates(RegionalVisibilityTileStatus status)
                => assessment.Tiles.Where(tile => tile.Status == status).Select(static tile => (tile.Column, tile.Row)).ToArray();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RegionalStellarVisibility_UnreadableLinearLeavesOpaqueTilesInsufficient()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-unreadable-linear");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);
        try
        {
            var profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, CameraPixelFormat.Mono16);
            var (patchy, clearTiles, opaqueTiles) = FindHeldOutPatchyCloudScenario(profile, RegionalNightUtc);
            profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, CameraPixelFormat.Mono16, cloudScenario: patchy);
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4,
                tiles: clearTiles.Concat(opaqueTiles).ToHashSet());
            var fault = new MissingLinearBeforeStep30bFault(root);

            var assessment = await RunSingleRegionalVisibilityAsync(root, clock, deviceClock, profile, catalog,
                RegionalNightUtc, token, fault).ConfigureAwait(false);

            Assert.IsTrue(fault.Injected);
            Assert.AreEqual(0, assessment.Grid.DeficitTileCount);
            foreach (var (column, row) in opaqueTiles)
            {
                var tile = assessment.Tiles.Single(item => item.Column == column && item.Row == row);
                Assert.AreEqual(RegionalVisibilityTileStatus.InsufficientEvidence, tile.Status);
                CollectionAssert.Contains(tile.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.TileBackgroundUnavailable);
            }
            foreach (var (column, row) in clearTiles)
            {
                var tile = assessment.Tiles.Single(item => item.Column == column && item.Row == row);
                Assert.AreEqual(RegionalVisibilityTileStatus.Clear, tile.Status);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MissingLinearBeforeStep30bFault(string root) : ICaptureProcessingFaultInjector
    {
        public bool Injected { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (!Injected && nodeId == "regional-visibility" && point == CaptureProcessingFaultPoint.BeforeNodeExecution)
            {
                var linear = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories)
                    .Single(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Calibrated");
                File.Move(linear, linear + ".moved");
                Injected = true;
            }
        }
    }

    [TestMethod]
    [DataRow("defocused", RegionalVisibilityAssessmentStatus.Withheld, RegionalVisibilityReasonCodes.PoorFocus,
        DisplayName = "RegionalStellarVisibility_Confounder_PoorFocusWithholds")]
    [DataRow("daylight", RegionalVisibilityAssessmentStatus.Withheld, RegionalVisibilityReasonCodes.Daylight,
        DisplayName = "RegionalStellarVisibility_Confounder_DaylightWithholds")]
    [DataRow("bright-moon", RegionalVisibilityAssessmentStatus.Withheld, RegionalVisibilityReasonCodes.MoonlightConfounded,
        DisplayName = "RegionalStellarVisibility_Confounder_BrightMoonlightWithholds")]
    [DataRow("rgb24-unsupported", RegionalVisibilityAssessmentStatus.Withheld, RegionalVisibilityReasonCodes.UnsupportedFormat,
        DisplayName = "RegionalStellarVisibility_UnsupportedRgb24_WithholdsWithoutCrashing")]
    [DataRow("sparse-catalog", RegionalVisibilityAssessmentStatus.InsufficientEvidence, RegionalVisibilityReasonCodes.InsufficientSupport,
        DisplayName = "RegionalStellarVisibility_SparseSky_ReportsInsufficientEvidence")]
    public async Task RegionalStellarVisibility_HeldOutConfoundersAndUnsupportedInputs_WithholdWithoutFalseDeficits(
        string caseName,
        RegionalVisibilityAssessmentStatus expectedStatus,
        string expectedReason)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-regvis-neg-{caseName}");
        var token = TestContext.CancellationToken;
        var utc = caseName switch
        {
            "daylight" => RegionalDayUtc,
            "bright-moon" => RegionalBrightMoonUtc,
            _ => RegionalNightUtc
        };
        var format = caseName == "rgb24-unsupported" ? CameraPixelFormat.Rgb24 : CameraPixelFormat.Mono16;
        var psfSigma = caseName == "defocused" ? 2.6 : 1.1;
        var illuminationMode = caseName == "daylight" ? "SolarDriven" : "ControlledNight";
        var clock = new CenteredClock { UtcNow = utc };
        var deviceClock = new ExposureTestTimeProvider(utc, autoAdvance: true);

        try
        {
            var profile = CreateRegionalVisibilityProfile(
                root, utc, format, psfSigmaPixels: psfSigma, illuminationMode: illuminationMode);
            var catalog = caseName == "sparse-catalog"
                ? new RegionalVisibilityTestCatalog([])
                : CreateRegionalVisibilityCatalog(utc, profile, starsPerInnerTile: 4);

            var assessment = await RunSingleRegionalVisibilityAsync(root, clock, deviceClock, profile, catalog, utc, token)
                .ConfigureAwait(false);

            TestContext.WriteLine(
                $"{caseName}: status={assessment.Status}, reasons=[{string.Join(',', assessment.ReasonCodes)}], " +
                $"deficit={assessment.Grid.DeficitTileCount}, assessed={assessment.Grid.AssessedTileCount}");

            Assert.AreEqual(expectedStatus, assessment.Status, string.Join(',', assessment.ReasonCodes));
            CollectionAssert.Contains(assessment.ReasonCodes.ToArray(), expectedReason);
            Assert.AreEqual(0, assessment.Grid.DeficitTileCount);
            Assert.AreEqual(0, assessment.Grid.AssessedTileCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, DisplayName = "RegionalStellarVisibility_WithholdsWhenDefectMaskPayloadIsCorrupt")]
    [DataRow(true, DisplayName = "RegionalStellarVisibility_WithholdsWhenDefectMaskManifestIsASymlink")]
    public async Task RegionalStellarVisibility_ReferenceCalibratedExposureResolvesDefectMaskByIdAndWithholdsWhenUnverifiable(
        bool linkManifest)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-refcal");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);

        try
        {
            var syntheticCalibration = new SyntheticCalibrationModelV1 { Gain = 1 };
            var profile = CreateRegionalVisibilityProfile(
                root,
                RegionalNightUtc,
                CameraPixelFormat.Mono16,
                syntheticCalibration: syntheticCalibration);
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4);

            using var provider = RegionalVisibilityProvider(root, clock, profile.DeploymentLocation!, catalog);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);

            var (receipt, _) = await CaptureAndAdmitRegionalAsync(
                provider, ingress, module, deviceClock, clock, active, RegionalNightUtc, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var operations = provider.GetRequiredService<IProcessingGraphOperations>();
            var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(executionId);

            var ui = RegionalVisibilityOperations(provider, operations);
            var detail = await ui.GetExecutionDetailAsync(executionId.Value, token).ConfigureAwait(false);
            Assert.IsTrue(detail.IsSuccess, detail.Message);
            var assessment = detail.Value?.RegionalStellarVisibility?.Assessment;
            Assert.IsNotNull(assessment, detail.Value?.RegionalStellarVisibility?.UnavailableReason);
            Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessment.Status, string.Join(',', assessment.ReasonCodes));
            Assert.AreEqual(ExposureCalibrationDisposition.ReferenceCalibrated, assessment.Conditions.CalibrationDisposition);
            Assert.IsNotNull(assessment.Sources.DefectMaskArtifactId);
            Assert.IsNotNull(assessment.Sources.DefectMaskSha256);
            Assert.AreEqual(0, assessment.Grid.DeficitTileCount);
            Assert.IsGreaterThan(0, assessment.Grid.ClearTileCount);

            // Corrupt the pinned defect master payload right before step 30b runs on a second capture:
            // Calibration and ExposureMeasurement succeed, then step 30b must fail-closed (withhold with DefectMaskUnavailable).
            var defectArtifactId = assessment.Sources.DefectMaskArtifactId.Value;
            var defectSha = assessment.Sources.DefectMaskSha256!;
            var calibrationStore = provider.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration.SqliteCalibrationLibraryStore>();
            var defectEntry = await calibrationStore.FindMasterArtifactAsync(
                defectArtifactId, CalibrationReferenceKinds.Defect, defectSha, token).ConfigureAwait(false);
            Assert.IsNotNull(defectEntry);
            var faultRoot = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-refcal-corrupt");
            try
            {
                var corruptProfile = CreateRegionalVisibilityProfile(
                    faultRoot,
                    RegionalNightUtc,
                    CameraPixelFormat.Mono16,
                    syntheticCalibration: syntheticCalibration);
                var corruptCatalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, corruptProfile, starsPerInnerTile: 4);
                var corruptFault = new UnverifiableDefectBeforeStep30bFault(
                    faultRoot, linkManifest ? Path.Combine(root, "external-defect.json") : null);
                var corruptClock = new CenteredClock { UtcNow = RegionalNightUtc };
                using var corruptProvider = RegionalVisibilityProvider(
                    faultRoot, corruptClock, corruptProfile.DeploymentLocation!, corruptCatalog, corruptFault);
                var corruptActive = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(corruptProvider, corruptProfile).ConfigureAwait(false);
                var corruptIngress = corruptProvider.GetRequiredService<RawCaptureIngress>();
                var corruptDeviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);
                var corruptModule = new VirtualSkyCameraModule(corruptDeviceClock, corruptCatalog, new ProjectedSceneStore(),
                    stagingStore: corruptProvider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var corruptCamera = corruptModule.ConfigureAwait(false);
                await corruptModule.InitializeAsync(corruptActive, token).ConfigureAwait(false);

                var (corruptReceipt, _) = await CaptureAndAdmitRegionalAsync(
                    corruptProvider, corruptIngress, corruptModule, corruptDeviceClock,
                    corruptClock, corruptActive, RegionalNightUtc, token).ConfigureAwait(false);
                await ProcessCenteredRuntimeExposureAsync(corruptProvider, corruptActive, token).ConfigureAwait(false);

                Assert.IsTrue(corruptFault.Injected);
                var corruptOps = corruptProvider.GetRequiredService<IProcessingGraphOperations>();
                var corruptExecId = (await corruptOps.ReadLiveExecutionIdAsync(
                    corruptReceipt.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false))!.Value;
                var corruptUi = RegionalVisibilityOperations(corruptProvider, corruptOps);
                var corruptDetail = await corruptUi.GetExecutionDetailAsync(corruptExecId, token).ConfigureAwait(false);
                Assert.IsTrue(corruptDetail.IsSuccess, corruptDetail.Message);
                var withheldAssessment = corruptDetail.Value?.RegionalStellarVisibility?.Assessment;
                Assert.IsNotNull(withheldAssessment, corruptDetail.Value?.RegionalStellarVisibility?.UnavailableReason);
                Assert.AreEqual(RegionalVisibilityAssessmentStatus.Withheld, withheldAssessment.Status);
                CollectionAssert.Contains(withheldAssessment.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.DefectMaskUnavailable);
                Assert.AreEqual(0, withheldAssessment.Grid.DeficitTileCount);
            }
            finally
            {
                Directory.Delete(faultRoot, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class UnverifiableDefectBeforeStep30bFault(string root, string? externalManifest) : ICaptureProcessingFaultInjector
    {
        public bool Injected { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (!Injected &&
                string.Equals(nodeId, "regional-visibility", StringComparison.Ordinal) &&
                point == CaptureProcessingFaultPoint.BeforeNodeExecution)
            {
                var defectFile = Directory.EnumerateFiles(Path.Combine(root, "calibration"), "defect.bin", SearchOption.AllDirectories)
                    .Single();
                if (externalManifest is not null)
                {
                    var manifest = Path.ChangeExtension(defectFile, ".json");
                    File.Move(manifest, externalManifest);
                    File.CreateSymbolicLink(manifest, externalManifest);
                }
                else
                {
                    var bytes = File.ReadAllBytes(defectFile);
                    bytes[0] ^= 0xFF;
                    File.WriteAllBytes(defectFile, bytes);
                }
                Injected = true;
            }
        }
    }

    [TestMethod]
    public async Task RegionalStellarVisibility_VirtualSkyRejectsMismatchedBayerChannelResponseAtMatchingMagnitudeZero()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-bayer-response");
        var token = TestContext.CancellationToken;
        try
        {
            var profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, CameraPixelFormat.BayerRggb16,
                photometricProfileColorResponse: new ConfiguredColorResponseProfile(5.0, 5.0, 5.0));
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4);
            var assessment = await RunSingleRegionalVisibilityAsync(
                root, new CenteredClock { UtcNow = RegionalNightUtc },
                new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true), profile, catalog, RegionalNightUtc, token)
                .ConfigureAwait(false);

            Assert.AreEqual(RegionalVisibilityAssessmentStatus.Withheld, assessment.Status);
            CollectionAssert.Contains(assessment.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.ResponseBasisMismatch);
            Assert.AreEqual(RegionalVisibilityTileStatus.InsufficientEvidence,
                assessment.Tiles.Single(static tile => tile.Column == 1 && tile.Row == 1).Status);
            Assert.AreEqual(0, assessment.Grid.AssessedTileCount);
            Assert.AreEqual(0, assessment.Grid.TotalExpectedDetectableStars);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RegionalStellarVisibility_VirtualSkyFallbackBindsRendererDefaultsAndRejectsMismatch()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-vs-fallback");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);

        try
        {
            // Omit explicit PhotometricProfile and use VirtualSky's default MagnitudeZeroElectronsPerSecond (1,000 e-/s).
            // Mag 4.0 stars would appear expected-detectable under an erroneous 10,000 e-/s fallback, but are too faint at 1,000 e-/s.
            var fallbackProfile = CreateRegionalVisibilityProfile(
                root,
                RegionalNightUtc,
                CameraPixelFormat.Mono16,
                omitExplicitPhotometricProfile: true,
                magnitudeZeroElectronsPerSecond: null);
            var faintCatalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, fallbackProfile, starsPerInnerTile: 4, starMagnitude: 4.0);

            var fallbackAssessment = await RunSingleRegionalVisibilityAsync(
                root, clock, deviceClock, fallbackProfile, faintCatalog, RegionalNightUtc, token).ConfigureAwait(false);
            Assert.AreEqual(RegionalVisibilityAssessmentStatus.InsufficientEvidence, fallbackAssessment.Status,
                string.Join(',', fallbackAssessment.ReasonCodes));
            Assert.AreEqual(0, fallbackAssessment.Grid.DeficitTileCount);
            Assert.AreEqual(0, fallbackAssessment.Grid.TotalExpectedDetectableStars);

            // Explicit PhotometricProfile whose MagnitudeZeroElectronsPerSecond disagrees with VirtualSky (14,000 vs 1,000) must withhold.
            var mismatchRoot = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-vs-mismatch");
            try
            {
                var mismatchProfile = CreateRegionalVisibilityProfile(
                    mismatchRoot,
                    RegionalNightUtc,
                    CameraPixelFormat.Mono16,
                    magnitudeZeroElectronsPerSecond: 1_000,
                    photometricProfileMagnitudeZero: 14_000);
                var brightCatalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, mismatchProfile, starsPerInnerTile: 4, starMagnitude: 1.0);
                var mismatchAssessment = await RunSingleRegionalVisibilityAsync(
                    mismatchRoot, new CenteredClock { UtcNow = RegionalNightUtc },
                    new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true),
                    mismatchProfile, brightCatalog, RegionalNightUtc, token).ConfigureAwait(false);
                Assert.AreEqual(RegionalVisibilityAssessmentStatus.Withheld, mismatchAssessment.Status);
                CollectionAssert.Contains(mismatchAssessment.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.ResponseBasisMismatch);
                Assert.AreEqual(0, mismatchAssessment.Grid.DeficitTileCount);
            }
            finally
            {
                Directory.Delete(mismatchRoot, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("exact")]
    [DataRow("rain-source-disabled")]
    [DataRow("missing")]
    [DataRow("altered")]
    public async Task RegionalStellarVisibility_RecentCaptureReplayUsesFrozenEnvironment(string scenario)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-replay");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);

        try
        {
            var profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, CameraPixelFormat.Mono16);
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4);
            var hasRain = scenario == "rain-source-disabled";
            var sources = new List<EnvironmentalSourceConfiguration>();
            if (hasRain)
            {
                sources.Add(new()
                {
                    Id = "rain-state",
                    Type = "VirtualEnvironment",
                    Kind = EnvironmentalObservationKind.RainState,
                    Triggers = [EnvironmentalAcquisitionTrigger.Periodic]
                });
            }
            var weatherOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new() { Enabled = true, Sources = sources }
            });
            using var weatherOutbox = new SqliteEnvironmentalObservationOutbox();
            var associations = new EnvironmentalAssociationService(weatherOutbox, weatherOutbox, weatherOptions, clock);
            var environment = new CameraAgentCloudEnvironment(associations, weatherOutbox, weatherOptions);

            using var provider = RegionalVisibilityProvider(root, clock, profile.DeploymentLocation!, catalog, environment: environment);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);

            var (receipt, _) = await CaptureAndAdmitRegionalAsync(
                provider, ingress, module, deviceClock, clock, active, RegionalNightUtc, token).ConfigureAwait(false);
            var rainObservationId = Guid.NewGuid();
            if (hasRain)
            {
                var descriptor = receipt.Manifest.Descriptor;
                var start = descriptor.Timing.ExposureStartedUtc;
                var end = descriptor.Timing.ExposureEndedUtc;
                var parameters = JsonSerializer.SerializeToElement(new { });
                var fact = new EnvironmentalObservationFactV1(
                    EnvironmentalObservationSchemaVersions.V1, rainObservationId,
                    new("test-provider", "rain-state", "1.0.0", EnvironmentalObservationSourceKind.Measured,
                        new(new("fixture", "1.0.0"), parameters, CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
                    start, null, null, start.AddMinutes(-1), end.AddMinutes(1), end,
                    new(EnvironmentalObservationKind.RainState, EnvironmentalObservationUnit.Boolean, null, true,
                        EnvironmentalObservationQuality.Good), []);
                await weatherOutbox.CommitLocalAsync(root, fact, token).ConfigureAwait(false);
                await associations.AssociateAsync(descriptor.Capture.CaptureId, descriptor.Capture.CaptureSequence,
                    start, end, descriptor.Capture.RigId, [EnvironmentalObservationKind.RainState], token).ConfigureAwait(false);
            }
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var operations = provider.GetRequiredService<IProcessingGraphOperations>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var ui = RegionalVisibilityOperations(provider, operations);
            var liveExecutionId = (await operations.ReadLiveExecutionIdAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false))!.Value;
            var liveDetail = (await operations.ReadExecutionDetailAsync(liveExecutionId, token).ConfigureAwait(false))!;
            var liveUi = (await ui.GetExecutionDetailAsync(liveExecutionId, token).ConfigureAwait(false)).Value!;
            var liveAssessment = liveUi.RegionalStellarVisibility!.Assessment!;
            var liveNode = liveDetail.Nodes.Single(node => node.NodeId == "regional-visibility");
            var liveOutput = Assert.ContainsSingle(liveNode.Outputs);
            if (hasRain)
            {
                Assert.AreEqual(EnvironmentalObservationMatchStatus.Fresh, liveAssessment.Conditions.PrecipitationStatus);
                Assert.IsTrue(liveAssessment.Conditions.PrecipitationDetected);
                CollectionAssert.Contains(liveAssessment.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.PrecipitationContaminated);
                var retained = await provider.GetRequiredService<CaptureProcessingPersistence>()
                    .RestoreRegionalVisibilityAsync(liveOutput.ArtifactId, token).ConfigureAwait(false);
                Assert.AreEqual(rainObservationId, retained.RecipeDescriptor!.Options
                    .GetProperty("environment").GetProperty("precipitationObservationId").GetGuid());
                sources.Clear();
            }

            var requested = await operations.SubmitReplayAsync(
                new(liveDetail.Execution.CaptureId, liveDetail.Execution.GraphRevisionId, liveDetail.Execution.PrimaryArtifactId),
                "regional-visibility-recent-replay", "regional-vis-test", token).ConfigureAwait(false);
            Assert.AreNotEqual(liveExecutionId, requested.Execution.ExecutionId);

            var frozenFailure = scenario switch
            {
                "missing" => RegionalStellarVisibilityRunRead.FrozenEnvironmentMissing,
                "altered" => RegionalStellarVisibilityRunRead.FrozenEnvironmentAltered,
                _ => null
            };
            if (frozenFailure is not null)
            {
                var original = await store.ReadOutputByArtifactIdAsync(liveOutput.ArtifactId, token).ConfigureAwait(false);
                Assert.IsNotNull(original);
                var payloadPath = Path.Combine(root, original.PayloadRelativePath);
                if (scenario == "missing")
                {
                    File.Move(payloadPath, payloadPath + ".moved");
                }
                else
                {
                    var payload = await File.ReadAllBytesAsync(payloadPath, token).ConfigureAwait(false);
                    payload[0] ^= 1;
                    await File.WriteAllBytesAsync(payloadPath, payload, token).ConfigureAwait(false);
                }
            }

            var replayLease = await store.ClaimReplayAsync("regional-replay-worker", token).ConfigureAwait(false);
            Assert.IsNotNull(replayLease);
            var replayExecution = new ProcessingExecutionContext(
                replayLease.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                replayLease.Execution.GraphRevisionId, replayLease.Execution.LocalPlanIdentitySha256, false,
                replayLease.WorkId, replayLease.LeaseToken, replayLease.LeaseOwner,
                replayLease.Execution.DeadlineUtc, replayLease.ClaimCount);
            var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(replayLease.Configuration);
            try
            {
                var replayOutcome = await FrameProcessingWorker.ProcessGraphItemAsync(
                    new(replayLease.Configuration, replayLease.Submission, replayLease.RawCapture,
                        replayLease.WorkId, replayLease.LeaseToken, replayExecution),
                    graph,
                    provider.GetRequiredService<CaptureProcessingPersistence>(),
                    provider.GetRequiredService<CaptureProcessingTelemetry>(),
                    replayLease.Execution.AttemptCount,
                    NullLogger.Instance,
                    token,
                    durableAttempt: replayLease.ClaimCount,
                    frozenPlanSuperseded: !replayLease.Revision.IsCompiledBy(graph)).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replayOutcome.Outcome, replayOutcome.Reason);
                var completed = await store.CompleteReplayAsync(replayLease, replayOutcome, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status);
            }
            finally
            {
                graph.DisposeSteps();
            }

            var replayDetail = (await operations.ReadExecutionDetailAsync(requested.Execution.ExecutionId, token).ConfigureAwait(false))!;
            var replayNode = replayDetail.Nodes.Single(node => node.NodeId == "regional-visibility");
            var replayUi = await ui.GetExecutionDetailAsync(requested.Execution.ExecutionId, token).ConfigureAwait(false);
            Assert.IsTrue(replayUi.IsSuccess, replayUi.Message);
            if (frozenFailure is not null)
            {
                Assert.AreEqual(frozenFailure, replayNode.Reason);
                Assert.IsEmpty(replayNode.Outputs);
                Assert.AreEqual(frozenFailure, replayUi.Value?.RegionalStellarVisibility?.UnavailableReason);
                return;
            }

            Assert.AreEqual("Completed", replayNode.Status);
            var replayOutput = Assert.ContainsSingle(replayNode.Outputs);
            Assert.IsFalse(replayOutput.Published);
            Assert.AreEqual(liveOutput.ArtifactId, replayOutput.ArtifactId);
            Assert.AreEqual(liveOutput.OutputIdentitySha256, replayOutput.OutputIdentitySha256);
            Assert.AreEqual(liveOutput.PayloadSha256, replayOutput.PayloadSha256);
            CollectionAssert.AreEqual(liveOutput.SourceArtifactIds.ToArray(), replayOutput.SourceArtifactIds.ToArray());

            var replayedAssessment = replayUi.Value?.RegionalStellarVisibility?.Assessment;
            Assert.IsNotNull(replayedAssessment);
            Assert.AreEqual(liveAssessment.AssessmentIdentitySha256, replayedAssessment.AssessmentIdentitySha256);
            Assert.AreEqual(hasRain ? RegionalVisibilityAssessmentStatus.Withheld : RegionalVisibilityAssessmentStatus.Assessed,
                replayedAssessment.Status);
            Assert.AreEqual(liveAssessment.Conditions.PrecipitationStatus, replayedAssessment.Conditions.PrecipitationStatus);
            Assert.AreEqual(hasRain, replayedAssessment.Conditions.PrecipitationDetected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RegionalStellarVisibility_InterruptedPublicationRecoversWithoutAdvertisingUncommittedOutput()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-regvis-interrupt");
        var token = TestContext.CancellationToken;
        var clock = new CenteredClock { UtcNow = RegionalNightUtc };
        var deviceClock = new ExposureTestTimeProvider(RegionalNightUtc, autoAdvance: true);

        try
        {
            var profile = CreateRegionalVisibilityProfile(root, RegionalNightUtc, CameraPixelFormat.Mono16);
            var catalog = CreateRegionalVisibilityCatalog(RegionalNightUtc, profile, starsPerInnerTile: 4);
            using var interruptionCts = new CancellationTokenSource();
            var fault = new RegionalVisibilityCommitInterruption("regional-visibility", interruptionCts);

            using var provider = RegionalVisibilityProvider(root, clock, profile.DeploymentLocation!, catalog, fault);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);

            var (receipt, reservation) = await CaptureAndAdmitRegionalAsync(
                provider, ingress, module, deviceClock, clock, active, RegionalNightUtc, token).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            var exposure = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, captureId);

            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            await laneStore.InitializeLanesAsync(token).ConfigureAwait(false);
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
            var lease = await laneStore.ClaimAsync(lane, "regvis-interrupt-first", active, token).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var executionId = lease.Context.Execution!.ExecutionId;
            var regionalSlot = new ExposureProductSlot(
                executionId, "regional-visibility",
                RegionalStellarVisibilityProducts.SchemaVersion, RegionalStellarVisibilityProducts.Variant);
            var handler = provider.GetRequiredService<StandardCaptureLaneHandler>();
            var reader = provider.GetRequiredService<IExposureProductReader>();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await handler.HandleAsync(lease.Context, interruptionCts.Token).ConfigureAwait(false)).ConfigureAwait(false);

            // An interrupted publication must NOT advertise an uncommitted regional assessment.
            Assert.IsTrue(fault.InterruptedOnce);
            Assert.IsNull(await reader.ReadProductAsync(exposure, regionalSlot, token).ConfigureAwait(false));
            var operations = provider.GetRequiredService<IProcessingGraphOperations>();
            var ui = RegionalVisibilityOperations(provider, operations);
            var duringInterruption = await ui.GetExecutionDetailAsync(executionId, token).ConfigureAwait(false);
            Assert.IsTrue(duringInterruption.IsSuccess, duringInterruption.Message);
            Assert.IsNull(duringInterruption.Value?.RegionalStellarVisibility?.Assessment);

            // Release lease and recover cleanly on retry.
            await laneStore.ReleaseAsync(lease, token).ConfigureAwait(false);
            lease = await laneStore.ClaimAsync(lane, "regvis-interrupt-retry", active, token).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            Assert.AreEqual(executionId, lease.Context.Execution!.ExecutionId);

            var recoveredOutcome = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, recoveredOutcome.Outcome, recoveredOutcome.Reason);
            await laneStore.CompleteAsync(lease, token).ConfigureAwait(false);

            var recoveredProduct = await reader.ReadProductAsync(exposure, regionalSlot, token).ConfigureAwait(false);
            Assert.IsNotNull(recoveredProduct);
            Assert.AreEqual(ExposureProductAvailability.Available, recoveredProduct.Availability);

            var afterRecovery = await ui.GetExecutionDetailAsync(executionId, token).ConfigureAwait(false);
            Assert.IsTrue(afterRecovery.IsSuccess, afterRecovery.Message);
            var recoveredAssessment = afterRecovery.Value?.RegionalStellarVisibility?.Assessment;
            Assert.IsNotNull(recoveredAssessment, afterRecovery.Value?.RegionalStellarVisibility?.UnavailableReason);
            Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, recoveredAssessment.Status);
            Assert.AreEqual(0, recoveredAssessment.Grid.DeficitTileCount);
            Assert.IsGreaterThan(0, recoveredAssessment.Grid.ClearTileCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RegionalVisibilityCommitInterruption(string targetNodeId, CancellationTokenSource cancellation)
        : ICaptureProcessingFaultInjector
    {
        public bool InterruptedOnce { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (!InterruptedOnce &&
                string.Equals(nodeId, targetNodeId, StringComparison.Ordinal) &&
                point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit)
            {
                InterruptedOnce = true;
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private static async Task<RegionalStellarVisibilityV1> RunSingleRegionalVisibilityAsync(
        string root,
        CenteredClock clock,
        ExposureTestTimeProvider deviceClock,
        CameraModuleConfig profile,
        ICelestialCatalog catalog,
        DateTimeOffset captureUtc,
        CancellationToken token,
        ICaptureProcessingFaultInjector? fault = null)
    {
        using var provider = RegionalVisibilityProvider(root, clock, profile.DeploymentLocation!, catalog, fault);
        var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
            stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
        await using var camera = module.ConfigureAwait(false);
        await module.InitializeAsync(active, token).ConfigureAwait(false);

        var (receipt, _) = await CaptureAndAdmitRegionalAsync(
            provider, ingress, module, deviceClock, clock, active, captureUtc, token).ConfigureAwait(false);
        await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

        var operations = provider.GetRequiredService<IProcessingGraphOperations>();
        var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, token)
            .ConfigureAwait(false);
        Assert.IsNotNull(executionId);

        var ui = RegionalVisibilityOperations(provider, operations);
        var detail = await ui.GetExecutionDetailAsync(executionId.Value, token).ConfigureAwait(false);
        Assert.IsTrue(detail.IsSuccess, detail.Message);
        Assert.IsNotNull(detail.Value?.RegionalStellarVisibility?.Assessment, detail.Value?.RegionalStellarVisibility?.UnavailableReason);
        return detail.Value!.RegionalStellarVisibility!.Assessment!;
    }

    private static ServiceProvider RegionalVisibilityProvider(
        string root, CenteredClock clock, DeploymentLocationSnapshot location, ICelestialCatalog catalog,
        ICaptureProcessingFaultInjector? fault = null, CameraAgentCloudEnvironment? environment = null)
        => CenteredRuntimeProvider(root, clock, services =>
        {
            services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(catalog);
            var locations = new Mock<IDeploymentLocationStore>();
            locations.Setup(store => store.Resolve(location.ToProvenance(), It.IsAny<DateTimeOffset?>())).Returns(location);
            services.AddSingleton(locations.Object);
            if (fault is not null)
            {
                services.AddSingleton(fault);
            }
            if (environment is not null)
            {
                services.AddSingleton(environment);
            }
        });

    private static async Task<(RawCaptureReceipt Receipt, CenteredExposureReservation Reservation)> CaptureAndAdmitRegionalAsync(
        ServiceProvider provider,
        RawCaptureIngress ingress,
        VirtualSkyCameraModule module,
        ExposureTestTimeProvider deviceClock,
        CenteredClock clock,
        CameraModuleConfig active,
        DateTimeOffset captureUtc,
        CancellationToken token)
    {
        clock.UtcNow = captureUtc;
        var delta = captureUtc - deviceClock.GetUtcNow();
        if (delta > TimeSpan.Zero)
        {
            deviceClock.Advance(delta);
        }

        var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
        var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "regional-vis-test", token,
            captureUtc, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var request = new CaptureRequest(captureUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint)
        {
            BoundGraphRevisionId = reservation.Revision.State.RevisionId,
            BoundGraph = reservation.Revision.Pipeline
        };
        var result = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        var completedUtc = result.AcquisitionTiming?.ReadoutCompletedUtc ?? deviceClock.GetUtcNow();
        if (completedUtc > clock.UtcNow)
        {
            clock.UtcNow = completedUtc;
        }
        var receipt = await ingress.AcceptCenteredAsync(
            active,
            new(request, result, completedUtc, request.TargetInterval, request.TargetInterval),
            reservation,
            token).ConfigureAwait(false);
        return (receipt, reservation);
    }

    private static CameraModuleConfig CreateRegionalVisibilityProfile(
        string root,
        DateTimeOffset startUtc,
        CameraPixelFormat format,
        VirtualCloudScenarioDefinition? cloudScenario = null,
        double psfSigmaPixels = 1.1,
        string illuminationMode = "ControlledNight",
        bool includeClearReferenceCloudAssessment = false,
        SyntheticCalibrationModelV1? syntheticCalibration = null,
        bool omitExplicitPhotometricProfile = false,
        double? magnitudeZeroElectronsPerSecond = 14_000,
        double? photometricProfileMagnitudeZero = null,
        ConfiguredColorResponseProfile? photometricProfileColorResponse = null)
    {
        var baseline = StoragePolicyRawIngressIntegrationTests.Profile(root);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var camera = CreateConfig(format, 256, 256);
        var readout = syntheticCalibration is not null ? null : format switch
        {
            CameraPixelFormat.Mono16 => new SensorReadoutProfile(new SensorCrop(0, 0, 256, 256), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                FrameLevelCodeSpace.NativeSample, 64, 4095, 512),
            CameraPixelFormat.BayerRggb16 => new SensorReadoutProfile(new SensorCrop(0, 0, 256, 256), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.BayerRggb16, 12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                FrameLevelCodeSpace.NativeSample, 64, 4095, 512,
                CfaPattern: ColorFilterArrayPattern.Rggb, CfaOriginX: 0, CfaOriginY: 0),
            _ => null
        };
        var simResponse = format == CameraPixelFormat.Rgb24 || syntheticCalibration is not null
            ? null
            : new ConfiguredSensorResponseProfile(
                ModelVersion: "virtual-regional-v1",
                AdcBitDepth: 12,
                MinimumGainControl: 0,
                MaximumGainControl: 500,
                ElectronsPerAduAtZeroGain: 1.0,
                GainControlDivisor: 100.0,
                MaximumFullWellElectrons: 20_000,
                ReadNoisePoints: [new SensorReadNoisePoint(0, 2.0), new SensorReadNoisePoint(500, 2.0)],
                BlackLevelAdu: 64,
                GainUnits: "tenth-db",
                CompatibilityLabel: "virtual-regional-v1",
                ColorResponse: format == CameraPixelFormat.BayerRggb16
                    ? new ConfiguredColorResponseProfile(0.85, 1.0, 0.78)
                    : null);

        var calibrationOptions = syntheticCalibration is null
            ? JsonSerializer.SerializeToElement(new { strategy = "None", outputVariant = "qualified-linear" })
            : JsonSerializer.SerializeToElement(new { strategy = "SyntheticReferences", outputVariant = "qualified-linear", syntheticCalibration });
        var profileMagZero = photometricProfileMagnitudeZero ?? magnitudeZeroElectronsPerSecond ?? 1_000;
        var regionalOptions = new RegionalStellarVisibilityProcessingStepOptions
        {
            GridColumns = 4,
            GridRows = 4,
            MinimumExpectedStarsPerTile = 3,
            MinimumMissingStarsForDeficit = 2,
            EdgeMarginPixels = 4.0,
            PhotometricProfile = omitExplicitPhotometricProfile
                ? null
                : new RegionalVisibilityPhotometricProfileV1(
                    ProfileName: "virtual-regional-v1",
                    MagnitudeZeroElectronsPerSecond: profileMagZero,
                    NominalPsfSigmaPixels: 1.1,
                    DarkCurrentElectronsPerSecond: syntheticCalibration is null ? 0.05 : 0.0,
                    VignettingStrength: 0.0,
                    ElectronsPerAduAtZeroGain: 1.0,
                    GainControlDivisor: 100.0,
                    ReadNoiseElectrons: 2.0,
                    FullWellElectrons: 20_000,
                    SensorResponse: simResponse is not null && photometricProfileColorResponse is not null
                        ? simResponse with { ColorResponse = photometricProfileColorResponse }
                        : simResponse)
        };

        var steps = new List<CaptureProcessingStepConfig>
        {
            new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                Options: calibrationOptions),
            new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional-visibility", DependsOn: ["$raw"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(regionalOptions)),
            new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                new FileStorageCaptureProcessingStepOptions
                {
                    StorageRoot = Path.Combine(root, "storage"),
                    QueueForUpload = false,
                    UpdateLatestFrame = false
                }))
        };
        if (includeClearReferenceCloudAssessment)
        {
            steps.Add(new("CloudAssessment", "cloud", DependsOn: ["$raw"], Required: false, Publication: durable));
        }

        var moduleOptions = new Dictionary<string, object?>
        {
            ["seed"] = 1925,
            ["fixedSceneUtc"] = startUtc,
            ["illuminationMode"] = illuminationMode,
            ["backgroundElectronsPerSecond"] = 25.0,
            ["psfSigmaPixels"] = psfSigmaPixels,
            ["psfRadiusPixels"] = Math.Max(4.5, psfSigmaPixels * 4.5),
            ["vignettingStrength"] = 0.0,
            ["darkCurrentElectronsPerSecond"] = syntheticCalibration is null ? 0.05 : 0.0,
            ["asi178Sensor"] = new { enabled = false },
            ["cloudScenario"] = cloudScenario,
            ["syntheticCalibration"] = syntheticCalibration
        };
        if (magnitudeZeroElectronsPerSecond is { } magZero)
        {
            moduleOptions["magnitudeZeroElectronsPerSecond"] = magZero;
        }

        return camera with
        {
            AgentId = baseline.AgentId,
            Schedule = baseline.Schedule,
            Rig = camera.Rig with
            {
                ControlPolicy = baseline.Rig.ControlPolicy,
                Readout = readout,
                Sensor = camera.Rig.Sensor with { SimulationResponse = simResponse }
            },
            DeploymentLocation = DeploymentLocationSnapshot.Create(
                "regional-vis-test", 1, "virtual", null, DateTimeOffset.UnixEpoch, null,
                35.347, -113.878, 1200, "America/Phoenix"),
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(moduleOptions)),
            CenteredExposure = new("regional-vis-cam", CenteredExposureProfile.CurrentExecutionVersion),
            Pipeline = baseline.Pipeline with { Steps = steps }
        };
    }

    private static (VirtualCloudScenarioDefinition Scenario, (int Column, int Row)[] Clear, (int Column, int Row)[] Opaque)
        FindHeldOutPatchyCloudScenario(
        CameraModuleConfig profile, DateTimeOffset utc)
    {
        var projection = RigProjectionContextFactory.CreateNative(profile.Rig);
        var projector = ProjectorFactory.Create(projection);
        var tileW = (double)profile.Rig.Sensor.WidthPixels / 4;
        var tileH = (double)profile.Rig.Sensor.HeightPixels / 4;
        var innerTiles = new[] { (1, 1), (2, 1), (1, 2), (2, 2) };

        for (var seed = 1; seed <= 250; seed++)
        {
            var candidate = new VirtualCloudScenarioDefinition
            {
                ScenarioId = $"heldout-patchy-{seed}",
                Seed = seed,
                EpochUtc = utc,
                SpatialFrequency = 2.0,
                Octaves = 2,
                EdgeSoftness = 0.04,
                HorizonFadeDegrees = 0,
                TemporalSampleCount = 1,
                Keyframes = [new VirtualCloudKeyframe { OffsetSeconds = 0, Coverage = 0.50, MaximumOpacity = 0.99, ScatterFraction = 0.95 }]
            };
            var field = new VirtualCloudField(candidate);
            var clearTiles = new List<(int, int)>();
            var opaqueTiles = new List<(int, int)>();
            foreach (var (col, row) in innerTiles)
            {
                var allClear = true;
                var allOpaque = true;
                for (var s = 0; s < 4; s++)
                {
                    var x = col * tileW + (s % 2 + 0.5) * tileW / 2;
                    var y = row * tileH + (s / 2 + 0.5) * tileH / 2;
                    var dir = projector.Unproject(new(x, y))!.Value;
                    var tx = field.Integrate(dir, utc, TimeSpan.FromSeconds(1)).Transmission;
                    if (tx < 0.95) allClear = false;
                    if (tx > 0.05) allOpaque = false;
                }
                if (allClear) clearTiles.Add((col, row));
                if (allOpaque) opaqueTiles.Add((col, row));
            }
            if (clearTiles.Count >= 1 && opaqueTiles.Count >= 1)
            {
                return (candidate, clearTiles.ToArray(), opaqueTiles.ToArray());
            }
        }

        throw new InvalidOperationException("Could not find a patchy cloud seed with both clear and opaque inner tiles.");
    }

    private static RegionalVisibilityTestCatalog CreateRegionalVisibilityCatalog(
        DateTimeOffset utc, CameraModuleConfig profile, int starsPerInnerTile, double starMagnitude = 1.4,
        HashSet<(int Column, int Row)>? tiles = null)
    {
        var projection = RigProjectionContextFactory.CreateNative(profile.Rig);
        var projector = ProjectorFactory.Create(projection);
        var stars = new List<CelestialCatalogObject>();
        const int cols = 4;
        const int rows = 4;
        var tileW = (double)profile.Rig.Sensor.WidthPixels / cols;
        var tileH = (double)profile.Rig.Sensor.HeightPixels / rows;
        var subCols = (int)Math.Ceiling(Math.Sqrt(starsPerInnerTile));
        var subRows = (int)Math.Ceiling((double)starsPerInnerTile / subCols);
        var index = 0;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                if (tiles is not null && !tiles.Contains((col, row)))
                {
                    continue;
                }
                for (var s = 0; s < starsPerInnerTile; s++)
                {
                    var sc = s % subCols;
                    var sr = s / subCols;
                    var x = col * tileW + (sc + 0.5) * tileW / subCols;
                    var y = row * tileH + (sr + 0.5) * tileH / subRows;
                    var dx = x - projection.PrincipalPointX;
                    var dy = y - projection.PrincipalPointY;
                    var radius = projection.ImageCircleRadiusPixels ?? Math.Min(profile.Rig.Sensor.WidthPixels, profile.Rig.Sensor.HeightPixels) * 0.48;
                    if (Math.Sqrt(dx * dx + dy * dy) >= radius - 8)
                    {
                        continue;
                    }

                    var horizontal = projector.Unproject(new(x, y));
                    if (horizontal is null || horizontal.Value.AltitudeDegrees < 10)
                    {
                        continue;
                    }

                    var ofDate = CoordinateTransforms.HorizontalToEquatorial(
                        horizontal.Value, utc, profile.Observatory.LatitudeDegrees, profile.Observatory.LongitudeDegrees);
                    var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, utc);
                    var id = $"regvis-star-{index++}";
                    stars.Add(new CelestialCatalogObject(id, id, j2000.RightAscensionHours, j2000.DeclinationDegrees, starMagnitude, 0.65));
                }
            }
        }

        return new RegionalVisibilityTestCatalog(stars);
    }

    private sealed class RegionalVisibilityTestCatalog(IReadOnlyList<CelestialCatalogObject> stars)
        : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _catalog = new(stars);

        public CatalogMetadata Metadata { get; } = new(
            "regional-visibility-fixture",
            "1",
            new Uri("https://example.invalid/catalog/regional-visibility"),
            new string('A', 64),
            "MIT",
            "1");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default)
            => _catalog.QueryCandidatesAsync(query, cancellationToken);
    }

    private static CameraAgentProcessingGraphUiService RegionalVisibilityOperations(
        ServiceProvider provider, IProcessingGraphOperations operations)
    {
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(service => service.GetAuthenticationStateAsync()).ReturnsAsync(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("test"))));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        return new CameraAgentProcessingGraphUiService(
            authentication.Object,
            authorization.Object,
            operations,
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>(),
            TimeProvider.System,
            NullLogger<CameraAgentProcessingGraphUiService>.Instance,
            provider.GetRequiredService<IExposureMeasurementRunReader>(),
            provider.GetRequiredService<IRegionalStellarVisibilityRunReader>());
    }
}
