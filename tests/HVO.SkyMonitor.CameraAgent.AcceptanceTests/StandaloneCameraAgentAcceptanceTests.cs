using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Astronomy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed standalone fixture used throughout the acceptance scope.")]
public sealed class StandaloneCameraAgentAcceptanceTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly FrameArtifactRole[] ExpectedRoles =
    [
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview
    ];

    [TestMethod]
    public async Task VirtualSkyCapturesAndRecoversWithoutCentralIntegrationAsync()
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        var firstCapture = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        var firstOwner = await ReadOwnerAsync(fixture.Services).ConfigureAwait(false);
        var firstLocation = ReadActiveLocation(fixture.Services);
        AssertProtectedLocationState(fixture.Root);
        AssertLaneJournalHasNoCoordinates(fixture.Root, "35.5599378", "-113.9119818", "America/Phoenix");

        using (var ownerClient = await fixture.CreateOwnerClientAsync().ConfigureAwait(false))
        {
            var summary = await WaitForStandaloneSummaryAsync(ownerClient).ConfigureAwait(false);
            AssertStandaloneSummary(summary);
            await AssertDisabledHealthAsync(ownerClient).ConfigureAwait(false);
            await AssertCaptureHttpEvidenceAsync(ownerClient, fixture.Root, firstCapture).ConfigureAwait(false);
        }
        await AssertCentralStateIsEmptyAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        AssertNoProvisioningIdentity(fixture.Root);
        AssertNoOutboundAttempts(fixture);

        var coordinator = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var paused = await coordinator.PauseAsync(
            $"standalone-pause-{Guid.NewGuid():N}",
            coordinator.Snapshot.Version,
            firstOwner.Id,
            "standalone restart acceptance",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);
        Assert.IsTrue(paused.Changed);
        await WaitForDurableConvergenceAsync(fixture.Root).ConfigureAwait(false);
        var preRestartMaximumSequence = await ReadMaximumSequenceAsync(fixture.Services).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(1_200)).ConfigureAwait(false);
        Assert.AreEqual(preRestartMaximumSequence, await ReadMaximumSequenceAsync(fixture.Services).ConfigureAwait(false));
        await AssertCentralStateIsEmptyAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        AssertNoOutboundAttempts(fixture);

        await fixture.RestartHostAsync().ConfigureAwait(false);
        var recoveredCoordinator = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        await WaitForConditionAsync(
            () => recoveredCoordinator.Snapshot is { IsInitialized: true, State: CaptureAdmissionState.Paused },
            "the paused capture state to recover").ConfigureAwait(false);
        Assert.AreEqual(paused.Version, recoveredCoordinator.Snapshot.Version);
        var recoveredOwner = await ReadOwnerAsync(fixture.Services).ConfigureAwait(false);
        var recoveredLocation = ReadActiveLocation(fixture.Services);
        Assert.AreEqual(firstOwner.Id, recoveredOwner.Id);
        Assert.AreEqual(firstOwner.NormalizedEmail, recoveredOwner.NormalizedEmail);
        Assert.IsTrue(recoveredOwner.IsSiteOwner);
        Assert.AreEqual(firstLocation.ToProvenance(), recoveredLocation.ToProvenance());
        AssertProtectedLocationState(fixture.Root);

        using (var recoveredOwnerClient = await fixture.CreateOwnerClientAsync().ConfigureAwait(false))
        {
            var recoveredSummary = await WaitForStandaloneSummaryAsync(recoveredOwnerClient).ConfigureAwait(false);
            AssertStandaloneSummary(recoveredSummary);
            Assert.AreEqual("Paused", recoveredSummary.CaptureControl.Value.State);
            Assert.AreEqual(paused.Version, recoveredSummary.CaptureControl.Value.Version);
            await AssertCaptureHttpEvidenceAsync(recoveredOwnerClient, fixture.Root, firstCapture).ConfigureAwait(false);
        }
        Assert.AreEqual(preRestartMaximumSequence, await ReadMaximumSequenceAsync(fixture.Services).ConfigureAwait(false));
        await AssertCentralStateIsEmptyAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        AssertNoProvisioningIdentity(fixture.Root);
        AssertNoOutboundAttempts(fixture);

        var resumed = await recoveredCoordinator.ResumeAsync(
            $"standalone-resume-{Guid.NewGuid():N}",
            recoveredCoordinator.Snapshot.Version,
            recoveredOwner.Id,
            "standalone restart acceptance",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, resumed.State);
        Assert.IsTrue(resumed.Changed);
        var resumedCapture = await WaitForCompleteCaptureAsync(
            fixture.Services,
            fixture.Root,
            preRestartMaximumSequence).ConfigureAwait(false);
        Assert.IsGreaterThan(preRestartMaximumSequence, resumedCapture.CaptureSequence);
        Assert.AreEqual(StandaloneCameraAgentKestrelFixture.AgentId, resumedCapture.AgentId);
        Assert.AreEqual(firstLocation.ToProvenance(), ReadActiveLocation(fixture.Services).ToProvenance());
        await AssertCentralStateIsEmptyAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        AssertNoOutboundAttempts(fixture);

        await fixture.StopHostAsync().ConfigureAwait(false);
        AssertNoOutboundAttempts(fixture);
    }

    [TestMethod]
    public async Task EnvironmentalHistoryAndOwnerSurfaceRecoverWithoutAnyCentralServiceAsync()
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync(
            useEnvironmentalAcquisition: true).ConfigureAwait(false);
        await WaitForConditionAsync(async () =>
        {
            var states = await fixture.Services.GetRequiredService<IEnvironmentalAcquisitionStateStore>()
                .ReadSourceStatesAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
            return states.Count == 12 && states.All(static state => state.LastDisposition.HasValue);
        }, "all standalone environmental sources to acquire", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var associatedCapture = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        await WaitForConditionAsync(async () =>
        {
            var associations = await fixture.Services.GetRequiredService<ILocalEnvironmentalAssociationStore>()
                .ReadAssociationsAsync(fixture.Root, associatedCapture.CaptureId, CancellationToken.None)
                .ConfigureAwait(false);
            return associations.Count == 12;
        }, "the required environmental association lane to complete", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var store = fixture.Services.GetRequiredService<ILocalEnvironmentalObservationStore>();
        var beforeRestart = await store.GetLocalSnapshotAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(12L, beforeRestart.StoredCount);

        using (var ownerClient = await fixture.CreateOwnerClientAsync().ConfigureAwait(false))
        {
            using var sources = await ownerClient.GetAsync(
                new Uri("/api/v1/operations/environmental/sources", UriKind.Relative)).ConfigureAwait(false);
            sources.EnsureSuccessStatusCode();
            using var sourcePayload = JsonDocument.Parse(
                await sources.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.AreEqual(12, sourcePayload.RootElement.GetArrayLength());
            using var history = await ownerClient.GetAsync(
                new Uri("/api/v1/operations/environmental/history?pageSize=50", UriKind.Relative)).ConfigureAwait(false);
            history.EnsureSuccessStatusCode();
            using var page = await ownerClient.GetAsync(new Uri("/environmental", UriKind.Relative)).ConfigureAwait(false);
            page.EnsureSuccessStatusCode();
            StringAssert.Contains(
                await page.Content.ReadAsStringAsync().ConfigureAwait(false),
                "Environmental",
                StringComparison.Ordinal);
        }
        AssertNoOutboundAttempts(fixture);

        await fixture.RestartHostAsync().ConfigureAwait(false);
        await WaitForConditionAsync(async () =>
        {
            var states = await fixture.Services.GetRequiredService<IEnvironmentalAcquisitionStateStore>()
                .ReadSourceStatesAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
            return states.Count == 12;
        }, "standalone environmental state to recover").ConfigureAwait(false);
        var afterRestart = await fixture.Services.GetRequiredService<ILocalEnvironmentalObservationStore>()
            .GetLocalSnapshotAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(beforeRestart.StoredCount, afterRestart.StoredCount);
        using (var recoveredOwnerClient = await fixture.CreateOwnerClientAsync().ConfigureAwait(false))
        using (var recoveredSources = await recoveredOwnerClient.GetAsync(
            new Uri("/api/v1/operations/environmental/sources", UriKind.Relative)).ConfigureAwait(false))
        {
            recoveredSources.EnsureSuccessStatusCode();
        }
        AssertNoProvisioningIdentity(fixture.Root);
        AssertNoOutboundAttempts(fixture);
    }

    [TestMethod]
    public async Task SidingSpringLocationRemainsAuthoritativeAcrossStandaloneRestartAsync()
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync(
            useSidingSpringLocation: true).ConfigureAwait(false);
        var firstCapture = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        var first = fixture.Services.GetRequiredService<IDeploymentLocationStore>().Active;
        var firstConfig = await fixture.Services.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.Configuration.ICameraAgentConfigurationAccessor>()
            .WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(first);
        Assert.AreEqual("siding-spring-synthetic", first.LocationId);
        Assert.AreEqual(-31.2733, first.LatitudeDegrees, 1e-12);
        Assert.AreEqual(149.0700, first.LongitudeDegrees, 1e-12);
        Assert.AreEqual(1165, first.ElevationMeters, 1e-12);
        Assert.AreEqual("Australia/Sydney", first.TimeZoneId);
        Assert.AreEqual(1L, first.Version);
        AssertLaneJournalHasNoCoordinates(fixture.Root, "-31.2733", "149.07", "Australia/Sydney");
        AssertNoOutboundAttempts(fixture);

        await fixture.RestartHostAsync().ConfigureAwait(false);
        var restartedCapture = await WaitForCompleteCaptureAsync(
            fixture.Services, fixture.Root, firstCapture.CaptureSequence).ConfigureAwait(false);
        var restarted = fixture.Services.GetRequiredService<IDeploymentLocationStore>().Active;
        var restartedConfig = await fixture.Services.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.Configuration.ICameraAgentConfigurationAccessor>()
            .WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(restarted);
        Assert.AreEqual(first.ToProvenance(), restarted.ToProvenance());
        Assert.AreEqual(first.ToObservatoryLocation(), restarted.ToObservatoryLocation());
        Assert.AreEqual(firstConfig.ResolveObservatory(), restartedConfig.ResolveObservatory());
        Assert.AreEqual(firstConfig.Rig, restartedConfig.Rig);
        var restartedRawManifest = ReadManifest(
            fixture.Root,
            restartedCapture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw).ArtifactId);
        Assert.AreEqual(restarted.ToProvenance(), restartedRawManifest.Descriptor.Location);
        Assert.IsNotNull(restartedRawManifest.Scene);
        Assert.IsFalse(string.IsNullOrWhiteSpace(restartedRawManifest.Scene.ProjectionModel));
        Assert.IsFalse(string.IsNullOrWhiteSpace(restartedRawManifest.Scene.ProjectionCalibrationVersion));
        Assert.IsNotNull(restartedRawManifest.Scene.Objects);
        Assert.IsNotEmpty(restartedRawManifest.Scene.Objects);
        AssertNoOutboundAttempts(fixture);
    }

    [TestMethod]
    public async Task SyntheticCalibrationReferencesCorrectStandaloneCaptureAndSurviveRestartAsync()
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync(
            useSyntheticCalibration: true).ConfigureAwait(false);
        var first = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        var raw = first.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
        var calibrated = first.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Calibrated);
        var rawManifest = ReadManifest(fixture.Root, raw.ArtifactId);
        var calibratedManifest = ReadManifest(fixture.Root, calibrated.ArtifactId);

        Assert.AreEqual(BuiltInProcessingRecipes.ReferenceCalibration, calibratedManifest.Descriptor.Artifact.Recipe.Name);
        Assert.AreEqual("synthetic-corrected", calibratedManifest.Descriptor.Artifact.Variant);
        Assert.HasCount(5, calibratedManifest.Descriptor.Artifact.SourceArtifactIds);
        Assert.AreEqual(raw.ArtifactId, calibratedManifest.Descriptor.Artifact.SourceArtifactIds[0]);
        Assert.AreEqual(rawManifest.Descriptor.Artifact.ChecksumSha256, raw.ChecksumSha256);
        Assert.AreNotEqual(
            rawManifest.Descriptor.Profiles.Calibration.Sha256,
            calibratedManifest.Descriptor.Profiles.Calibration.Sha256);
        Assert.AreNotEqual(
            rawManifest.Descriptor.Profiles.Mask.Sha256,
            calibratedManifest.Descriptor.Profiles.Mask.Sha256);
        Assert.AreEqual("reference-calibration-profile", calibratedManifest.Descriptor.Profiles.Calibration.Name);
        Assert.AreEqual(
            ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            calibratedManifest.Descriptor.Profiles.Calibration.Version);
        Assert.AreEqual("calibration-defect-mask", calibratedManifest.Descriptor.Profiles.Mask.Name);
        foreach (var referenceId in calibratedManifest.Descriptor.Artifact.SourceArtifactIds.Skip(1))
        {
            var reference = ReadManifest(fixture.Root, referenceId);
            Assert.AreEqual(FrameArtifactRole.Raw, reference.Descriptor.Artifact.Role);
            CollectionAssert.Contains(CalibrationReferenceKinds.All.ToArray(), reference.Descriptor.Artifact.Variant);
        }
        Assert.AreNotEqual(raw.ChecksumSha256, calibrated.ChecksumSha256);
        Assert.HasCount(4, Directory.EnumerateFiles(
            Path.Combine(fixture.Root, "calibration", "synthetic"), "*.bin", SearchOption.AllDirectories).ToArray());
        AssertNoOutboundAttempts(fixture);

        await fixture.RestartHostAsync().ConfigureAwait(false);
        var restarted = await WaitForCompleteCaptureAsync(
            fixture.Services, fixture.Root, first.CaptureSequence).ConfigureAwait(false);
        var restartedCalibrated = restarted.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Calibrated);
        Assert.AreEqual(
            BuiltInProcessingRecipes.ReferenceCalibration,
            ReadManifest(fixture.Root, restartedCalibrated.ArtifactId).Descriptor.Artifact.Recipe.Name);
        Assert.HasCount(4, Directory.EnumerateFiles(
            Path.Combine(fixture.Root, "calibration", "synthetic"), "*.bin", SearchOption.AllDirectories).ToArray());
        AssertNoOutboundAttempts(fixture);
    }

    [TestMethod]
    public async Task VirtualSkyProjectedScenePersistsAndRecoversStandaloneAsync()
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync(
            useProjectedScene: true).ConfigureAwait(false);
        var capture = await WaitForProjectedSceneCaptureAsync(fixture.Services).ConfigureAwait(false);
        var raw = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
        var product = capture.Artifacts.Single(static artifact =>
            artifact.ProductSchemaVersion == ProjectedSceneV1.CurrentSchemaVersion);
        var rawManifest = ReadManifest(fixture.Root, raw.ArtifactId);
        var provenance = rawManifest.Scene;
        Assert.IsNotNull(provenance);
        Assert.AreNotEqual(provenance.SceneId, provenance.ProjectedSceneStageKey);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, product.ProductSchemaVersion);
        Assert.AreEqual("Available", capture.CanonicalSceneAvailability);
        Assert.AreEqual(DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
            ReadDurableProductManifest(fixture.Root, product.ArtifactId).SchemaVersion);
        CollectionAssert.AreEqual(new[] { raw.ArtifactId }, product.SourceArtifactIds.ToArray());

        var beforeRestart = await ReadArtifactBytesAsync(fixture.Services, product.ArtifactId).ConfigureAwait(false);
        var parsed = ProjectedSceneJson.Parse(beforeRestart);
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        var scene = parsed.Scene!;
        Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, scene.Kind);
        Assert.AreNotEqual(provenance.SceneId, scene.SceneIdentitySha256);
        Assert.AreNotEqual(provenance.ProjectedSceneStageKey, scene.SceneIdentitySha256);
        Assert.AreEqual(product.ContentIdentitySha256, scene.SceneIdentitySha256);
        Assert.AreEqual(product.ChecksumSha256, Convert.ToHexString(SHA256.HashData(beforeRestart)), ignoreCase: true);
        Assert.AreEqual(product.ByteLength, beforeRestart.LongLength);
        Assert.AreEqual(capture.CaptureId, scene.Source.CaptureId);
        Assert.AreEqual(raw.ArtifactId, scene.Source.ArtifactId);
        Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(rawManifest.Descriptor),
            scene.Source.ArtifactIdentitySha256, ignoreCase: true);
        CollectionAssert.AreEqual(
            provenance.Objects!.Select(static item => (item.Id, item.DisplayName, item.PixelX, item.PixelY, item.Magnitude)).ToArray(),
            scene.Objects.Select(static item => (item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray());
        CollectionAssert.AreEqual(
            provenance.Segments!.Select(static item => (item.ConstellationId, item.FromObjectId, item.ToObjectId,
                item.FromPixelX, item.FromPixelY, item.ToPixelX, item.ToPixelY, item.PartIndex)).ToArray(),
            scene.Segments.Select(static item => (item.ConstellationId, item.FromObjectId, item.ToObjectId,
                item.FromPixel.X, item.FromPixel.Y, item.ToPixel.X, item.ToPixel.Y, item.PartIndex)).ToArray());
        Assert.IsGreaterThan(0, fixture.ProjectedSceneMemoryCacheCount);

        var coordinator = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var owner = await ReadOwnerAsync(fixture.Services).ConfigureAwait(false);
        await coordinator.PauseAsync($"issue-435-{Guid.NewGuid():N}", coordinator.Snapshot.Version, owner.Id,
            "restart projected-scene evidence", CancellationToken.None).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        await fixture.ChangeCurrentProjectedSceneConfigurationAsync().ConfigureAwait(false);
        await fixture.StartStoppedHostAsync().ConfigureAwait(false);
        Assert.AreEqual(0, fixture.ProjectedSceneMemoryCacheCount);
        Assert.AreEqual(5.75, await fixture.ReadLoadedMaximumMagnitudeAsync().ConfigureAwait(false), 1e-12);

        var afterRestart = await ReadArtifactBytesAsync(fixture.Services, product.ArtifactId).ConfigureAwait(false);
        CollectionAssert.AreEqual(beforeRestart, afterRestart);
        var recovered = await fixture.Services.GetRequiredService<ICameraAgentGallery>()
            .GetCaptureAsync(capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(recovered);
        Assert.AreEqual("Available", recovered.CanonicalSceneAvailability);
        Assert.AreEqual(product.ArtifactId, recovered.Artifacts.Single(static artifact =>
            artifact.ProductSchemaVersion == ProjectedSceneV1.CurrentSchemaVersion).ArtifactId);
        Assert.AreEqual(1L, ReadJournalCount(fixture.Root,
            $"SELECT COUNT(*) FROM processing_outputs WHERE capture_id = '{capture.CaptureId:N}' AND product_schema_version = 'projected-scene-v1';"));
        Assert.IsEmpty(Directory.Exists(Path.Combine(fixture.Root, "staging", "projected-scenes"))
            ? Directory.EnumerateFiles(Path.Combine(fixture.Root, "staging", "projected-scenes")).ToArray()
            : []);
        await fixture.StopAsync().ConfigureAwait(false);
        AssertNoOutboundAttempts(fixture);
    }

    private static async Task<CameraAgentGalleryCapture> WaitForProjectedSceneCaptureAsync(IServiceProvider services)
    {
        CameraAgentGalleryCapture? selected = null;
        await WaitForConditionAsync(async () =>
        {
            var page = await services.GetRequiredService<ICameraAgentGallery>().GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: 20, EvidenceOrigin: GalleryEvidenceOrigin.Simulated),
                CancellationToken.None).ConfigureAwait(false);
            selected = page.Items.FirstOrDefault(static item => item.CanonicalSceneAvailability == "Available" &&
                item.Artifacts.Any(static artifact => artifact.ProductSchemaVersion == ProjectedSceneV1.CurrentSchemaVersion));
            if (selected is null) return false;
            selected = await services.GetRequiredService<ICameraAgentGallery>()
                .GetCaptureAsync(selected.CaptureId, CancellationToken.None).ConfigureAwait(false);
            return selected is not null;
        }, "a standalone V3 projected scene", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return selected!;
    }

    private static IDurableProcessingProductManifest ReadDurableProductManifest(string root, Guid artifactId)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = DurableProcessingProductManifestJson.Parse(File.ReadAllBytes(path));
                if (manifest.Artifact.ArtifactId == artifactId) return manifest;
            }
            catch (InvalidDataException)
            {
            }
        }
        throw new AssertFailedException($"Artifact {artifactId:D} has no durable product manifest.");
    }

    private static async Task<byte[]> ReadArtifactBytesAsync(IServiceProvider services, Guid artifactId)
    {
        var result = await services.GetRequiredService<ICameraAgentArtifactService>()
            .OpenContentAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
        await using var content = result.Content!;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<CameraAgentGalleryCapture> WaitForCompleteCaptureAsync(
        IServiceProvider services,
        string root,
        long minimumSequence = 0)
    {
        CameraAgentGalleryCapture? selected = null;
        await WaitForConditionAsync(async () =>
        {
            var gallery = services.GetRequiredService<ICameraAgentGallery>();
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: 100, MinimumSequence: minimumSequence + 1, EvidenceOrigin: GalleryEvidenceOrigin.Simulated),
                CancellationToken.None).ConfigureAwait(false);
            foreach (var candidate in page.Items.OrderByDescending(static capture => capture.CaptureSequence))
            {
                var roles = candidate.Artifacts.Select(static artifact => artifact.Role).Distinct().ToArray();
                if (!ExpectedRoles.All(roles.Contains) ||
                    candidate.ProcessingNodes.Any(static node => node.Status != "Completed"))
                {
                    continue;
                }

                var detail = await gallery.GetCaptureAsync(candidate.CaptureId, CancellationToken.None).ConfigureAwait(false);
                if (detail?.Detail is not { EvidenceAvailability: "Available", RawRetentionHold: false } ||
                    detail.Detail.ProcessingNodes.Any(static node => node.FailureCategory is not null))
                {
                    continue;
                }

                selected = detail;
                return true;
            }
            return false;
        }, "a complete standalone VirtualSky capture", TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.IsNotNull(selected);
        Assert.AreEqual(StandaloneCameraAgentKestrelFixture.AgentId, selected.AgentId);
        Assert.AreEqual(GalleryEvidenceOrigin.Simulated, selected.EvidenceOrigin);
        CollectionAssert.IsSubsetOf(ExpectedRoles, selected.Artifacts.Select(static artifact => artifact.Role).Distinct().ToArray());
        Assert.IsTrue(selected.Artifacts.All(static artifact =>
            artifact.ArtifactId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(artifact.ChecksumSha256) &&
            artifact.ByteLength > 0));
        Assert.IsTrue(selected.Artifacts.Where(static artifact => artifact.Role != FrameArtifactRole.Raw).All(static artifact =>
            artifact.Recipe is not null &&
            !string.IsNullOrWhiteSpace(artifact.Recipe.SemanticVersion) &&
            !string.IsNullOrWhiteSpace(artifact.Recipe.ImplementationVersion) &&
            !string.IsNullOrWhiteSpace(artifact.Recipe.OptionsSha256) &&
            !string.IsNullOrWhiteSpace(artifact.Recipe.IdentitySha256)));
        AssertLocalArtifactContracts(root, selected);
        foreach (var artifact in selected.Artifacts.Where(static artifact => artifact.Role != FrameArtifactRole.Raw))
        {
            AssertLineageReachesRaw(artifact, selected.Artifacts);
        }
        Assert.IsTrue(selected.Detail!.ArtifactStates.All(static state =>
            state.DeliveryAvailability == "Disabled" && state.DeliveryStatuses.Count == 0));
        return selected;
    }

    private static void AssertLocalArtifactContracts(
        string root,
        CameraAgentGalleryCapture capture)
    {
        var manifests = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(static path => CaptureContractJson.ParseManifest(File.ReadAllBytes(path)))
            .Where(static parsed => parsed.IsValid && parsed.Document?.Manifest is not null)
            .Select(static parsed => parsed.Document!.Manifest!)
            .GroupBy(static manifest => manifest.Descriptor.Artifact.ArtifactId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var captureLocations = manifests.Values
            .Where(manifest => manifest.Descriptor.Capture.CaptureId == capture.CaptureId)
            .Select(static manifest => manifest.Descriptor.Location)
            .ToArray();
        Assert.IsTrue(captureLocations.All(static location => location is not null));
        Assert.AreEqual(1, captureLocations.Distinct().Count());
        foreach (var artifact in capture.Artifacts)
        {
            Assert.IsTrue(manifests.TryGetValue(artifact.ArtifactId, out var manifest), artifact.ArtifactId.ToString("D"));
            var descriptor = manifest.Descriptor;
            Assert.IsTrue(descriptor.Validate().IsValid);
            Assert.AreEqual(capture.CaptureId, descriptor.Capture.CaptureId);
            Assert.AreEqual(capture.AgentId, descriptor.Capture.AgentId);
            Assert.AreEqual(artifact.Role, descriptor.Artifact.Role);
            Assert.AreEqual(artifact.ChecksumSha256, descriptor.Artifact.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(
                descriptor.Artifact.Recipe.OptionsSha256,
                CaptureContractJson.ComputeCanonicalJsonSha256(descriptor.Artifact.Recipe.Options),
                ignoreCase: true);
            Assert.AreEqual(
                artifact.Recipe?.IdentitySha256,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                ignoreCase: true);
            Assert.IsTrue(descriptor.Artifact.SourceArtifactIds.All(manifests.ContainsKey),
                $"Artifact {artifact.ArtifactId:D} has dangling source identities.");
            var manifestText = System.Text.Encoding.UTF8.GetString(CaptureContractJson.Serialize(manifest));
            Assert.IsFalse(manifestText.Contains("35.5599378", StringComparison.Ordinal));
            Assert.IsFalse(manifestText.Contains("-113.9119818", StringComparison.Ordinal));
            Assert.IsFalse(manifestText.Contains("America/Phoenix", StringComparison.Ordinal));
        }

        var calibrated = manifests[capture.Artifacts.Single(static artifact =>
            artifact.Role == FrameArtifactRole.Calibrated).ArtifactId].Descriptor.Artifact;
        if (string.Equals(calibrated.Recipe.Name, BuiltInProcessingRecipes.LinearNormalization, StringComparison.Ordinal))
        {
            Assert.AreEqual("none", calibrated.Variant);
            Assert.AreEqual(
                "None",
                calibrated.Recipe.Options.Deserialize<LinearNormalizationOptions>(
                    WebJson)!.Mode);
        }
        else
        {
            Assert.AreEqual(BuiltInProcessingRecipes.ReferenceCalibration, calibrated.Recipe.Name);
            Assert.AreEqual("synthetic-corrected", calibrated.Variant);
            Assert.HasCount(5, calibrated.SourceArtifactIds);
        }
    }

    private static DeploymentLocationSnapshot ReadActiveLocation(IServiceProvider services)
    {
        var active = services.GetRequiredService<IDeploymentLocationStore>().Active;
        Assert.IsNotNull(active);
        Assert.IsTrue(active.Validate().IsValid);
        Assert.AreEqual(1L, active.Version);
        Assert.AreEqual("hualapai-cameraagent", active.LocationId);
        return active;
    }

    private static void AssertProtectedLocationState(string root)
    {
        var path = Path.Combine(root, ".location", "deployment-location.v1.protected");
        Assert.IsTrue(File.Exists(path));
        var protectedText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.IsFalse(protectedText.Contains("35.5599378", StringComparison.Ordinal));
        Assert.IsFalse(protectedText.Contains("-113.9119818", StringComparison.Ordinal));
        Assert.IsFalse(protectedText.Contains("America/Phoenix", StringComparison.Ordinal));
    }

    private static void AssertLaneJournalHasNoCoordinates(string root, params string[] forbidden)
    {
        var journalRoot = Path.Combine(root, "journal");
        foreach (var path in new[]
        {
            Path.Combine(journalRoot, "raw-ingress.db"),
            Path.Combine(journalRoot, "raw-ingress.db-wal"),
            Path.Combine(journalRoot, "raw-ingress.db-shm")
        }.Where(File.Exists))
        {
            var content = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
            foreach (var value in forbidden)
            {
                Assert.IsFalse(content.Contains(value, StringComparison.Ordinal), path);
            }
        }
    }

    private static async Task<CameraAgentOperationsSummary> WaitForStandaloneSummaryAsync(HttpClient client)
    {
        CameraAgentOperationsSummary? summary = null;
        await WaitForConditionAsync(async () =>
        {
            summary = await client.GetFromJsonAsync<CameraAgentOperationsSummary>(
                new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
            return summary is not null && summary.Storage.Value.Count > 0;
        }, "the standalone operations summary").ConfigureAwait(false);
        return summary!;
    }

    private static void AssertStandaloneSummary(CameraAgentOperationsSummary summary)
    {
        Assert.IsTrue(summary.Configuration.Value.IsCurrent);
        Assert.AreEqual("validated", summary.Configuration.Value.ValidationStatus);
        Assert.AreEqual(StandaloneCameraAgentKestrelFixture.AgentId, summary.Configuration.Value.AgentId);
        Assert.AreEqual("VirtualSky", summary.Configuration.Value.ModuleType);
        Assert.AreEqual("Disabled", summary.Configuration.Value.CentralIntegration);
        Assert.AreEqual("Disabled", summary.ArtifactOutbox.Value.Availability);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.PendingCount);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.PendingBytes);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.LeasedCount);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.RetryCount);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.QuarantineCount);
        Assert.IsNull(summary.ArtifactOutbox.Value.OldestPendingUtc);
        Assert.AreEqual("Disabled", summary.Heartbeat.Value.Availability);
        Assert.AreEqual(0L, summary.Heartbeat.Value.PendingCount);
        Assert.AreEqual(0L, summary.Heartbeat.Value.PendingBytes);
        Assert.AreEqual(0L, summary.Heartbeat.Value.LeasedCount);
        Assert.AreEqual(0L, summary.Heartbeat.Value.RetryCount);
        Assert.AreEqual(0L, summary.Heartbeat.Value.QuarantineCount);
        Assert.AreEqual(0L, summary.Heartbeat.Value.OverflowCount);
        Assert.AreEqual(0L, summary.Heartbeat.Value.BlockedCount);
        Assert.IsNull(summary.Heartbeat.Value.OldestPendingUtc);
        Assert.AreEqual("Disabled", summary.EnvironmentalDelivery.Value.Availability);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.StoredCount);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.StoredBytes);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.PendingCount);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.PendingBytes);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.LeasedCount);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.RetryCount);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.QuarantineCount);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.TerminalCount);
        Assert.AreEqual(0L, summary.EnvironmentalDelivery.Value.OverflowCount);
        Assert.IsNull(summary.EnvironmentalDelivery.Value.OldestPendingUtc);
        var storage = summary.Storage.Value.Single(static location => location.Alias == "raw-ingress");
        Assert.IsTrue(storage.ProbeSucceeded);
        Assert.IsGreaterThan(0L, storage.TotalBytes);
    }

    private static async Task AssertDisabledHealthAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var health = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(health, "Artifact delivery is disabled/not configured.", StringComparison.Ordinal);
        StringAssert.Contains(health, "Fleet heartbeat is disabled/not configured.", StringComparison.Ordinal);
        StringAssert.Contains(health, "Environmental delivery is disabled/not configured.", StringComparison.Ordinal);
        StringAssert.Contains(health, "Protected deployment-location history is available.", StringComparison.Ordinal);
        Assert.IsFalse(health.Contains("35.5599378", StringComparison.Ordinal));
        Assert.IsFalse(health.Contains("-113.9119818", StringComparison.Ordinal));
        Assert.IsFalse(health.Contains("America/Phoenix", StringComparison.Ordinal));
    }

    private static async Task AssertCaptureHttpEvidenceAsync(
        HttpClient client,
        string root,
        CameraAgentGalleryCapture expected)
    {
        var capture = await client.GetFromJsonAsync<CameraAgentGalleryCapture>(
            new Uri($"/api/v1/operations/gallery/{expected.CaptureId:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        Assert.AreEqual(expected.CaptureId, capture.CaptureId);
        Assert.AreEqual(expected.CaptureSequence, capture.CaptureSequence);
        Assert.AreEqual(expected.AgentId, capture.AgentId);

        foreach (var artifact in expected.Artifacts)
        {
            using var response = await client.GetAsync(new Uri(
                $"/api/v1/operations/artifacts/{artifact.ArtifactId:D}/content",
                UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(
                HttpStatusCode.OK,
                response.StatusCode,
                DescribeArtifactEvidence(root, artifact.ArtifactId));
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Assert.AreEqual(artifact.ByteLength, payload.LongLength);
            Assert.AreEqual(
                artifact.ChecksumSha256,
                Convert.ToHexString(SHA256.HashData(payload)),
                ignoreCase: true);
            Assert.AreEqual(
                artifact.ChecksumSha256,
                response.Headers.GetValues("X-Artifact-SHA256").Single(),
                ignoreCase: true);
        }

        var previewArtifact = expected.Artifacts.Single(static artifact =>
            artifact.Role == FrameArtifactRole.Preview && artifact.Variant == "default");
        using var preview = await client.GetAsync(new Uri(
            $"/api/v1/operations/artifacts/{previewArtifact.ArtifactId:D}/preview",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(
            HttpStatusCode.OK,
            preview.StatusCode,
            DescribeArtifactEvidence(root, previewArtifact.ArtifactId));
        Assert.AreEqual("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.IsGreaterThan(0, (await preview.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length);
    }

    private static void AssertLineageReachesRaw(
        CameraAgentGalleryArtifact artifact,
        IReadOnlyCollection<CameraAgentGalleryArtifact> artifacts)
    {
        var byId = artifacts.ToDictionary(static candidate => candidate.ArtifactId);
        var pending = new Queue<Guid>(artifact.SourceArtifactIds);
        var visited = new HashSet<Guid>();
        while (pending.TryDequeue(out var artifactId))
        {
            if (!visited.Add(artifactId) || !byId.TryGetValue(artifactId, out var source))
            {
                continue;
            }
            if (source.Role == FrameArtifactRole.Raw)
            {
                return;
            }
            foreach (var parent in source.SourceArtifactIds)
            {
                pending.Enqueue(parent);
            }
        }
        Assert.Fail($"Artifact {artifact.ArtifactId:D} lineage did not reach the capture's raw artifact.");
    }

    private static string DescribeArtifactEvidence(string root, Guid artifactId)
    {
        foreach (var sidecarPath in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            ArtifactManifestParseResult parsed;
            try
            {
                parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(sidecarPath));
            }
            catch (IOException)
            {
                continue;
            }
            var manifest = parsed.Document?.Manifest;
            if (manifest?.Descriptor.Artifact.ArtifactId != artifactId)
            {
                continue;
            }
            var payloadPath = Path.Combine(root, manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(payloadPath))
            {
                return $"Artifact sidecar exists but payload is missing: {manifest.RelativeArtifactPath}";
            }
            var reconstruction = FrameReconstructor.TryReconstruct(
                manifest.Descriptor,
                File.ReadAllBytes(payloadPath),
                out _);
            return $"Artifact reconstruction: {reconstruction.ReasonCode ?? "valid"} at {manifest.RelativeArtifactPath}";
        }
        return $"Artifact {artifactId:D} has no local manifest.";
    }

    private static ArtifactManifestV2 ReadManifest(string root, Guid artifactId)
    {
        foreach (var sidecarPath in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(sidecarPath));
            if (parsed.Document?.Manifest is { } manifest &&
                manifest.Descriptor.Artifact.ArtifactId == artifactId)
            {
                return manifest;
            }
        }
        throw new AssertFailedException($"Artifact {artifactId:D} has no local manifest.");
    }

    private static async Task AssertCentralStateIsEmptyAsync(IServiceProvider services, string root)
    {
        var artifactOutbox = services.GetRequiredService<IArtifactOutbox>();
        var artifact = await artifactOutbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, artifact.HeldCount);
        Assert.AreEqual(0L, artifact.HeldBytes);
        Assert.IsNull(artifact.OldestHeldUtc);
        Assert.AreEqual(0L, artifact.PendingCount);
        Assert.AreEqual(0L, artifact.LeasedCount);
        Assert.AreEqual(0L, artifact.RetryCount);
        Assert.AreEqual(0L, artifact.AcknowledgedCount);
        Assert.AreEqual(0L, artifact.QuarantinedCount);
        Assert.AreEqual(0L, artifact.AbandonedCount);
        Assert.IsEmpty(await artifactOutbox.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(await artifactOutbox.HasUnknownRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
        Assert.IsEmpty(artifactOutbox.List(root, 100));

        var fleet = await services.GetRequiredService<IFleetStatusOutbox>()
            .GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, fleet.PendingCount);
        Assert.AreEqual(0L, fleet.PendingBytes);
        Assert.AreEqual(0L, fleet.LeasedCount);
        Assert.AreEqual(0L, fleet.RetryCount);
        Assert.AreEqual(0L, fleet.QuarantineCount);
        Assert.AreEqual(0L, fleet.OverflowCount);
        Assert.AreEqual(0L, fleet.BlockedCount);
        Assert.IsNull(fleet.OldestPendingUtc);

        var environmental = await services.GetRequiredService<IEnvironmentalObservationOutbox>()
            .GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, environmental.StoredCount);
        Assert.AreEqual(0L, environmental.StoredBytes);
        Assert.AreEqual(0L, environmental.PendingCount);
        Assert.AreEqual(0L, environmental.PendingBytes);
        Assert.AreEqual(0L, environmental.LeasedCount);
        Assert.AreEqual(0L, environmental.RetryCount);
        Assert.AreEqual(0L, environmental.QuarantineCount);
        Assert.AreEqual(0L, environmental.TerminalCount);
        Assert.AreEqual(0L, environmental.OverflowCount);
        Assert.IsNull(environmental.OldestPendingUtc);
    }

    private static async Task WaitForDurableConvergenceAsync(string root)
    {
        await WaitForConditionAsync(
            () => ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed';") == 0 &&
                ReadJournalCount(root, "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';") == 0 &&
                ReadJournalCount(root, "SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed';") == 0,
            "standalone durable work to converge").ConfigureAwait(false);
        Assert.AreEqual(0L, ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;"));
        Assert.AreEqual(0L, ReadJournalCount(root, "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'upload';"));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Queries are fixed private test constants and contain no external input.")]
    private static long ReadJournalCount(string root, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadMaximumSequenceAsync(IServiceProvider services)
    {
        var page = await services.GetRequiredService<ICameraAgentGallery>().GetPageAsync(
            new CameraAgentGalleryQuery(PageSize: 100),
            CancellationToken.None).ConfigureAwait(false);
        return page.Items.Max(static capture => capture.CaptureSequence);
    }

    private static async Task<OwnerSnapshot> ReadOwnerAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var owners = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .Users
            .Where(static user => user.IsSiteOwner)
            .Select(static user => new OwnerSnapshot(user.Id, user.NormalizedEmail, user.IsSiteOwner))
            .ToArrayAsync()
            .ConfigureAwait(false);
        Assert.HasCount(1, owners);
        return owners[0];
    }

    private static void AssertNoProvisioningIdentity(string root)
    {
        var provisioning = Path.Combine(root, "provisioning");
        Assert.IsFalse(File.Exists(Path.Combine(provisioning, "device-identity.json")));
        Assert.IsFalse(File.Exists(Path.Combine(provisioning, "device-secrets.dat")));
    }

    private static void AssertNoOutboundAttempts(StandaloneCameraAgentKestrelFixture fixture)
    {
        Assert.IsEmpty(
            fixture.OutboundAttempts,
            string.Join(Environment.NewLine, fixture.OutboundAttempts.Select(static attempt =>
                $"{attempt.AttemptedUtc:O} {attempt.Method} {attempt.RequestUri}")));
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        string description,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for {description}.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        string description,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for {description}.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private sealed record OwnerSnapshot(string Id, string? NormalizedEmail, bool IsSiteOwner);
}
