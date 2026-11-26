using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.Common.Configuration;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private const string LifecyclePrefix = "/api/internal/deployment/lifecycle";
    private static readonly Uri VerifyHistoryRoute = new(LifecyclePrefix + "/silo/verify-history", UriKind.Relative);
    private static readonly Uri VerifyInstallationRoute = new("/api/internal/owner-bootstrap/installation-verification", UriKind.Relative);

    [TestMethod]
    [TestCategory("Integration")]
    public Task InstalledMonoCameraRecoversRawAdmissionsAndReopensLocalProducts() =>
        VerifyInstalledCameraAsync(color: false, centered: false);

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public Task InstalledCameraAdmitsAndReopensItsExactRawVersionAfterInterruptedPublication(bool color, bool centered) =>
        VerifyInstalledCameraAsync(color, centered);

    private async Task VerifyInstalledCameraAsync(bool color, bool centered)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var rootAccess = RandomHex(12);
        var rootSecret = RandomHex(24);
        var instance = Guid.NewGuid();
        var pause = Guid.NewGuid();
        var lifecycleToken = RandomHex(24);
        var bucket = CameraAgentSiloBinding.BucketFor(instance);
        var storeId = CameraAgentSiloBinding.StoreIdFor(instance);
        var binding = new SiloStorageBinding(instance.ToString("D"), storeId, bucket);
        var container = BuildStockContainer(rootAccess, rootSecret, "on");
        await using var lease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        using var admin = SiloS3ObjectStore.CreateClient(Endpoint(container), rootAccess, rootSecret);
        await CreateVersionedBucketAsync(admin, bucket, token).ConfigureAwait(false);
        var writer = await CreateWriterAsync(container, binding, token).ConfigureAwait(false);
        await InstallBindingAsync(admin, binding, writer.Access, token).ConfigureAwait(false);
        var settings = InstalledSettings(binding, Endpoint(container), writer, pause, lifecycleToken);
        var exerciseRecovery = !centered && !color;
        if (!centered)
            settings["CameraAgent:Silo:MaximumStagedFiles"] = "2";
        if (exerciseRecovery)
            settings["CameraAgent:TransientDetection:Mode"] = "Edge";
        var root = Directory.CreateTempSubdirectory("silo-capture-").FullName;
        var fault = new InterruptManifestPublication();
        var priorKeyDirectory = Environment.GetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable);
        var priorSettingsPath = Environment.GetEnvironmentVariable(DeploymentLocalSettings.PathVariable);
        Func<string, string>? configureCamera = centered ? json => CenteredConfiguration(json, color) : color ? ColorConfiguration : null;
        CameraAgentIntegrationFixture? restarted = null;

        try
        {
            var keyDirectory = Directory.CreateDirectory(Path.Combine(root, "settings")).FullName;
            foreach (var (key, value) in settings)
                await File.WriteAllTextAsync(Path.Combine(keyDirectory, key.Replace(":", "__", StringComparison.Ordinal)), value, token)
                    .ConfigureAwait(false);
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, keyDirectory);
            Environment.SetEnvironmentVariable(DeploymentLocalSettings.PathVariable, Path.Combine(root, DeploymentLocalSettings.FileName));
            var pending = await CaptureUntilInterruptedAsync(root, settings, lifecycleToken, pause, configureCamera, fault, token).ConfigureAwait(false);
            restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
            var readFault = new ExactReadFault();
            var recoveryFault = new InterruptRawRecovery();
            await restarted.InitializeAsync(services =>
            {
                if (!centered) RemoveCaptureWorkers(services, stopCamera: true);
                services.AddSingleton<Func<ISiloObjectStore, ISiloObjectStore>>(store => new FaultingReadStore(store, readFault));
                if (exerciseRecovery)
                {
                    services.AddSingleton<ISiloFaults>(recoveryFault);
                    services.AddSingleton<IRawIngressFaultInjector>(recoveryFault);
                }
            }, settings, root, configureCamera).ConfigureAwait(false);
            using var scope = restarted.CreateCameraAgentScope();
            var storage = scope.ServiceProvider.GetRequiredService<SiloInstalledStorage>();
            var reopened = await WaitForReopenAsync(storage, pending.GroupId, pending.RevisionId, token).ConfigureAwait(false);
            Assert.IsTrue(reopened.Reopenable);
            if (!centered)
            {
                if (scope.ServiceProvider.GetRequiredService<RawCaptureIngress>().SiloPressureRelease is { } pressure)
                    await pressure.WaitAsync(token).ConfigureAwait(false);
                var admissions = scope.ServiceProvider.GetRequiredService<CaptureAdmissionCoordinator>();
                await admissions.InitializeAsync(token).ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Paused, admissions.Snapshot.State);
                await AssertExposureWaitsAsync(admissions, token).ConfigureAwait(false);
                await admissions.ResumeAsync("operator-resume", admissions.Snapshot.Version, "operator", null, token)
                    .ConfigureAwait(false);
                using var admission = await admissions.EnterAsync(token).ConfigureAwait(false);
                admission.MarkNoPublicationRequired();
            }
            var raw = reopened.Items.Single(static item => item.Role.Kind == SiloIdentityKind.Member &&
                item.Role.Revision != SiloArtifactStorage.RawEvidenceRevision);
            Assert.IsNotNull(raw.ServedFrom);
            var artifactId = Guid.ParseExact(raw.Role.Id, "D");
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5, usesSilo: true);
            var rawEntry = await journal.ReadSiloAdmissionByArtifactAsync(artifactId, token).ConfigureAwait(false);
            Assert.IsNotNull(rawEntry);
            while (await journal.HasPendingSiloAdmissionAsync(rawEntry.CaptureId, token).ConfigureAwait(false))
                await Task.Delay(50, token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(Path.Combine(root, rawEntry.PayloadRelativePath)));
            Assert.IsFalse(File.Exists(Path.Combine(root, rawEntry.SidecarRelativePath)));

            var artifacts = scope.ServiceProvider.GetRequiredService<ICameraAgentArtifactService>();
            var result = await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            Assert.IsNotNull(result.Content);
            await using (result.Content.ConfigureAwait(false))
            {
                Assert.AreEqual(color ? "BayerRggb16" : "Mono16", result.Content.Descriptor?.Layout.PixelFormat.ToString());
                Assert.AreEqual(raw.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(result.Content, token).ConfigureAwait(false)));
            }
            using var verifier = restarted.CreateCameraAgentClient();
            using (var denied = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false))
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            verifier.DefaultRequestHeaders.Add("X-HVO-Installation-Token", lifecycleToken);
            using (var verification = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false))
            {
                verification.EnsureSuccessStatusCode();
                var history = await verification.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
                Assert.IsTrue(history?.VerifiedRoles >= 3);
            }
            if (centered)
            {
                var views = scope.ServiceProvider.GetRequiredService<ICenteredViewReadIndex>();
                var camera = new CameraKey(restarted.DeviceId, "installed-virtual");
                CenteredViewCurrent? current;
                while ((current = await views.ReadLastLiveAsync(camera, token).ConfigureAwait(false)) is null)
                    await Task.Delay(100, token).ConfigureAwait(false);
                var view = current.View;
                Assert.IsNotNull(await views.ReadRevisionAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false));
                var exact = await storage.Catalog.ReopenAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false);
                Assert.IsTrue(exact.Reopenable);
                Assert.IsTrue(exact.Items.Where(static item => item.Required).All(item => item.ServedFrom?.StoreId == storeId));
                var archiveId = SiloCenteredViews.LowerHex(CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
                {
                    executionId = view.Base.Definition.Reference.ExecutionId,
                    nodeId = "LocalStorage"
                })));
                Assert.IsTrue((await WaitForReopenAsync(storage, archiveId, "1", token).ConfigureAwait(false)).Reopenable);
                if (color)
                {
                    await StopSiloCaptureWorkersAsync(scope.ServiceProvider, token).ConfigureAwait(false);
                }
                else
                {
                    var config = await scope.ServiceProvider.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token)
                        .ConfigureAwait(false);
                    using var productDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    productDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var productToken = productDeadline.Token;
                    var gallery = scope.ServiceProvider.GetRequiredService<ICameraAgentGallery>();
                    var reference = view.Base.Definition.Reference.Capture.CaptureId;
                    while ((await gallery.GetCaptureAsync(reference, productToken).ConfigureAwait(false))?.Detail?.CloudAssessment.Availability != "Available")
                        await Task.Delay(50, productToken).ConfigureAwait(false);
                    var satellite = scope.ServiceProvider.GetRequiredService<CaptureSatelliteWorkStore>();
                    var processingStore = scope.ServiceProvider.GetRequiredService<SqliteCaptureProcessingStore>();
                    while (await processingStore.ReadNodeAsync(reference, "CaptureSatelliteTracks", productToken).ConfigureAwait(false) is not
                        { Status: DurableProcessingNodeStatus.Completed })
                        await Task.Delay(50, productToken).ConfigureAwait(false);
                    var satelliteHolds = await satellite.GetRetentionHoldsAsync(root, token).ConfigureAwait(false);
                    Assert.IsTrue(satelliteHolds.Any(hold => hold.ArtifactId == view.Base.Definition.Reference.Input.Raw.Artifact.ArtifactId &&
                        hold.PayloadRelativePath.StartsWith("silo/raw/", StringComparison.Ordinal)));
                    await satellite.ReconcileAsync(root, token).ConfigureAwait(false);
                    var processing = scope.ServiceProvider.GetRequiredService<CaptureProcessingPersistence>();
                    var holds = await processing.GetRetentionHoldsAsync(root, token).ConfigureAwait(false);
                    Assert.IsTrue(holds.Any(static hold => hold.PayloadRelativePath.StartsWith("silo/products/", StringComparison.Ordinal)));
                    var retention = scope.ServiceProvider.GetServices<IHostedService>().OfType<RetentionBackgroundService>().Single();
                    await retention.ApplyRetentionAsync(config, token).ConfigureAwait(false);
                    var required = exact.Items.First(static item => item.Required && item.Role.Kind == SiloIdentityKind.Member);
                    Assert.IsNotNull(required.ServedFrom);
                    await AssertInstalledViewReadOutcomesAsync(restarted, verifier, readFault, view, required.ServedFrom, token)
                        .ConfigureAwait(false);
                    await AssertCenteredVisibilityAsync(scope.ServiceProvider, view, token).ConfigureAwait(false);
                    await AssertSiloReplayPinsAsync(scope.ServiceProvider, reference, root, token).ConfigureAwait(false);
                    await AssertSiloNightlyPublicationAsync(scope.ServiceProvider, root, token).ConfigureAwait(false);
                }
            }
            if (exerciseRecovery)
            {
                await AssertSiloTransientReservationAsync(scope.ServiceProvider, rawEntry, readFault, raw.ServedFrom, root, token)
                    .ConfigureAwait(false);
                await scope.ServiceProvider.GetRequiredService<SiloStorageWorker>().StopAsync(token).ConfigureAwait(false);
                await AssertSiloTimeLapseRetentionAndPublicationAsync(scope.ServiceProvider, root, token).ConfigureAwait(false);
            }
            readFault.Set(raw.ServedFrom, ReadFault.Damaged);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict,
                (await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false)).Status);
            readFault.Set(raw.ServedFrom, ReadFault.Unavailable);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable,
                (await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false)).Status);
            readFault.Clear();
            await admin.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = raw.ServedFrom.Key,
                VersionId = raw.ServedFrom.VersionId
            }, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Gone,
                (await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false)).Status);
            using var failed = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Gone, failed.StatusCode);
            var failedHistory = await failed.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
            Assert.IsTrue(failedHistory?.MissingRoles > 0);
            if (exerciseRecovery)
            {
                var rawManifest = CaptureContractJson.ParseManifest(rawEntry.ManifestJson).Document!.Manifest;
                await AssertRawWriteOffDrainsAsync(scope.ServiceProvider, storage, journal, rawManifest.Descriptor, token)
                    .ConfigureAwait(false);
                await AssertSiloCalibrationAsync(scope.ServiceProvider, rawManifest.Descriptor, root, token).ConfigureAwait(false);
                var recovery = await InterruptRawReclamationAsync(scope.ServiceProvider, storage, journal, rawManifest.Descriptor,
                    recoveryFault, token).ConfigureAwait(false);
                scope.Dispose();
                var stopped = restarted;
                restarted = null;
                stopped.Dispose();
                settings["CameraAgent:Silo:MaximumStagedFiles"] = "4";
                await File.WriteAllTextAsync(Path.Combine(keyDirectory, "CameraAgent__Silo__MaximumStagedFiles"), "4", token)
                    .ConfigureAwait(false);
                await AssertStartupReclaimsRawAsync(settings, root, recovery, journal, rawManifest.Descriptor, token).ConfigureAwait(false);
            }
        }
        finally
        {
            restarted?.Dispose();
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, priorKeyDirectory);
            Environment.SetEnvironmentVariable(DeploymentLocalSettings.PathVariable, priorSettingsPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertInstalledViewReadOutcomesAsync(CameraAgentIntegrationFixture fixture, HttpClient verifier,
        ExactReadFault fault, CenteredView view, SiloExactLocator locator, CancellationToken token)
    {
        using var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, fixture.OwnerId);
        var capture = view.Base.Definition.Reference.Capture.CaptureId;
        var viewRoute = $"/api/v1/operations/gallery/{capture:D}/views/{view.Base.BaseId}/{view.Revision.RevisionId}";
        foreach (var (mode, expected) in new[]
        {
            (ReadFault.Damaged, HttpStatusCode.Conflict),
            (ReadFault.Absent, HttpStatusCode.Gone),
            (ReadFault.Unavailable, HttpStatusCode.ServiceUnavailable)
        })
        {
            fault.Set(locator, mode);
            using var response = await client.GetAsync(new Uri(viewRoute, UriKind.Relative), token).ConfigureAwait(false);
            Assert.AreEqual(expected, response.StatusCode, "Exact centered view metadata");
            using var preview = await client.GetAsync(new Uri(viewRoute + "/preview", UriKind.Relative), token).ConfigureAwait(false);
            Assert.AreEqual(expected, preview.StatusCode, "Exact centered view preview");
            using var history = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false);
            Assert.AreEqual(expected, history.StatusCode, "Full verification status");
            var result = await history.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
            Assert.IsNotNull(result);
            Assert.IsTrue((mode switch
            {
                ReadFault.Damaged => result.DamagedRoles,
                ReadFault.Absent => result.MissingRoles,
                _ => result.UnavailableRoles
            }) > 0);
        }
        fault.Clear();
    }

    private static async Task AssertCenteredVisibilityAsync(IServiceProvider services, CenteredView view, CancellationToken token)
    {
        await StopSiloCaptureWorkersAsync(services, token).ConfigureAwait(false);
        var views = services.GetRequiredService<SqliteCenteredViews>();
        while ((await views.PublishSiloChangesAsync(8, token).ConfigureAwait(false)).More) { }

        var baseId = view.Base.BaseId;
        var reference = view.Base.Definition.Reference.Capture.CaptureId;
        var stream = view.Base.Definition.Stream;
        var key = new CenteredViewLayerKey("test-silo-publication", CenteredViewLayerScope.Reference, reference);
        var healthy = new CenteredViewLayer(key, 1, null, DateTimeOffset.UtcNow, CenteredViewReadiness.Waiting, null, null);
        var second = await views.PublishLayerAsync(baseId, healthy, token).ConfigureAwait(false);
        Assert.IsNotNull(second);
        Assert.IsNull(await views.ReadRevisionAsync(baseId, second.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.HasCount(0, await views.ReadRevisionsAsync(baseId, second.Revision.Order - 1, 16, token).ConfigureAwait(false));
        Assert.IsFalse((await views.ReadChangesAsync(stream, second.Revision.Order - 1, 16, token).ConfigureAwait(false))
            .Any(change => change.RevisionId == second.Revision.RevisionId));
        await views.PublishSiloChangesAsync(8, token).ConfigureAwait(false);
        Assert.IsNotNull(await views.ReadRevisionAsync(baseId, second.Revision.RevisionId, token).ConfigureAwait(false));

        var blocked = await views.PublishLayerAsync(baseId, healthy with
        {
            EvidenceOrder = 2,
            Evidence = new(Guid.NewGuid(), new string('A', 64), new string('B', 64)),
            Readiness = CenteredViewReadiness.Ready
        }, token).ConfigureAwait(false);
        Assert.IsNotNull(blocked);
        var archive = await views.ReadArchiveAsync(stream, reference, token).ConfigureAwait(false);
        Assert.AreEqual(second.Revision.RevisionId, archive.Single(entry => entry.Base.BaseId == baseId).LatestRevisionId);

        var later = await views.PublishLayerAsync(baseId, healthy with { EvidenceOrder = 3 }, token).ConfigureAwait(false);
        Assert.IsNotNull(later);
        var result = await views.PublishSiloChangesAsync(8, token).ConfigureAwait(false);
        Assert.AreEqual("silo-centered-view-unpublishable", result.Status);
        Assert.IsNull(await views.ReadRevisionAsync(baseId, blocked.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.IsNotNull(await views.ReadRevisionAsync(baseId, later.Revision.RevisionId, token).ConfigureAwait(false));

        var malformed = await views.PublishLayerAsync(baseId, healthy with { EvidenceOrder = 4 }, token).ConfigureAwait(false);
        var following = await views.PublishLayerAsync(baseId, healthy with { EvidenceOrder = 5 }, token).ConfigureAwait(false);
        Assert.IsNotNull(malformed);
        Assert.IsNotNull(following);
        var processing = services.GetRequiredService<SqliteCaptureProcessingStore>();
        using (var connection = await processing.OpenCenteredViewsAsync(token).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE processing_centered_view_revisions SET manifest = '{' WHERE base_id = $base AND revision_id = $revision;";
            command.Parameters.AddWithValue("$base", baseId);
            command.Parameters.AddWithValue("$revision", malformed.Revision.RevisionId);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
        }
        result = await views.PublishSiloChangesAsync(8, token).ConfigureAwait(false);
        Assert.AreEqual("silo-centered-view-publication-failed", result.Status);
        var index = services.GetRequiredService<SiloInstalledStorage>().Index;
        Assert.IsNull(await index.ReadPublicationAsync(baseId, malformed.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.IsNotNull(await views.ReadRevisionAsync(baseId, following.Revision.RevisionId, token).ConfigureAwait(false));
    }

    private static async Task StopSiloCaptureWorkersAsync(IServiceProvider services, CancellationToken token)
    {
        await services.GetServices<IHostedService>().OfType<CameraCaptureService>().Single().StopAsync(token).ConfigureAwait(false);
        var storageWorker = services.GetRequiredService<SiloStorageWorker>();
        await storageWorker.StopAsync(token).ConfigureAwait(false);
        Assert.IsTrue(storageWorker.ExecuteTask?.IsCompletedSuccessfully,
            "Silo publication must stop before the installed storage is disposed.");
    }

    private static async Task AssertRawWriteOffDrainsAsync(IServiceProvider services, SiloInstalledStorage storage,
        SqliteRawCaptureJournal journal, ReconstructionDescriptor template, CancellationToken token)
    {
        await services.GetRequiredService<SiloStorageWorker>().StopAsync(token).ConfigureAwait(false);
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow.Subtract(template.Controls.EffectiveExposure).AddSeconds(-5);
        var firstSubmission = MakeRawSubmission(template, timestamp);
        var first = await ingress.AcceptAsync(config, firstSubmission, token).ConfigureAwait(false);
        Assert.IsNotNull(first);
        var pressure = ingress.SiloPressureRelease;
        Assert.IsNotNull(pressure);
        var refusedSubmission = MakeRawSubmission(template, timestamp.AddSeconds(1));
        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
            await ingress.AcceptAsync(config, refusedSubmission, token).ConfigureAwait(false)).ConfigureAwait(false);
        var retry = await ingress.AcceptAsync(config, firstSubmission, token).ConfigureAwait(false);
        Assert.AreEqual(RawIngressOutcome.Existing, retry?.Outcome);
        File.Delete(first.StoredFrame.AbsolutePath);
        var refusal = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual("silo-staged-evidence-missing", refusal.Status);
        var target = (await ingress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false)).Items.Single().Target;
        await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "silo-test-write-off", "operator",
            "operator-approved-loss", token).ConfigureAwait(false);
        await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        await pressure.WaitAsync(token).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json")));

        var second = await ingress.AcceptAsync(config, refusedSubmission, token).ConfigureAwait(false);
        Assert.IsNotNull(second);
        Assert.AreEqual(first.Manifest.Descriptor.Capture.CaptureSequence + 1, second.Manifest.Descriptor.Capture.CaptureSequence);
        var capture = second.Manifest.Descriptor.Capture.CaptureId;
        var drained = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.IsNull(drained.Status);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(capture, token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(second.StoredFrame.AbsolutePath));
        var afterCleanup = await ingress.AcceptAsync(config, refusedSubmission, token).ConfigureAwait(false);
        Assert.AreEqual(RawIngressOutcome.Existing, afterCleanup?.Outcome);
        Assert.IsFalse(File.Exists(second.StoredFrame.AbsolutePath));
        Assert.IsTrue((await storage.Catalog.ReopenAsync(capture.ToString("N"), "raw-1", token).ConfigureAwait(false)).Reopenable);
        await AssertRetainedAstrometryAsync(services, config, refusedSubmission, second, token).ConfigureAwait(false);
    }

    private static CaptureLoopSubmission MakeRawSubmission(ReconstructionDescriptor template, DateTimeOffset at)
    {
        var exposure = template.Controls.EffectiveExposure;
        var setpoint = new CaptureSetpoint(exposure, template.Controls.EffectiveGain, null, null);
        var layout = template.Layout with
        {
            SampleDepthBits = 16,
            BlackLevel = 0,
            WhiteLevel = ushort.MaxValue,
            Readout = null,
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
        var frame = new CameraFrame(at, layout.Width, layout.Height, layout.PixelFormat, new byte[checked((int)layout.ByteLength)],
            new FrameMetadata(exposure, setpoint.Gain, double.NaN, "integration-test"), layout.StrideBytes)
        { Layout = layout };
        return new(new CaptureRequest(at, exposure, CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false), at, exposure, TimeSpan.Zero);
    }

    private static async Task AssertSiloCalibrationAsync(IServiceProvider services, ReconstructionDescriptor raw, string root,
        CancellationToken token)
    {
        var model = new SyntheticCalibrationModelV1();
        var light = raw with
        {
            Controls = raw.Controls with { RequestedGain = model.Gain, EffectiveGain = model.Gain, EffectiveTemperatureC = model.TemperatureC },
            Profiles = raw.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor("synthetic-calibration-model", model.SchemaVersion,
                    SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
            }
        };
        var store = services.GetRequiredService<SyntheticCalibrationReferenceStore>();
        var bundle = await store.GetOrCreateAsync(light, model, token).ConfigureAwait(false);
        var loader = services.GetRequiredService<CameraAgentClearReferenceLoader>();
        var flat = bundle.LibraryBundle.Artifacts.Single(static artifact => artifact.Kind == CalibrationReferenceKinds.Flat);
        var loaded = await loader.LoadAsync(flat.ManifestRelativePath, token).ConfigureAwait(false);
        Assert.AreEqual(flat.ArtifactId, loaded.ArtifactId);
        Assert.AreEqual(flat.PayloadSha256, PayloadChecksum.ComputeSha256(loaded.Payload.Span));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "calibration")));
        Assert.HasCount(0, bundle.EvidenceFiles);
        var library = services.GetRequiredService<SqliteCalibrationLibraryStore>();
        var state = await library.GetStateAsync(token).ConfigureAwait(false);
        await library.ActivateAsync(bundle.LibraryBundle.BundleId, "silo-test-calibration", state.Version, "operator", null, token)
            .ConfigureAwait(false);
        Assert.AreEqual("calibration.library.selected", await library.ValidateActiveBundleAsync(token).ConfigureAwait(false));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "calibration")));
    }

    private enum ReadFault { Damaged, Absent, Unavailable }

    private sealed class ExactReadFault
    {
        private sealed record Fault(SiloExactLocator Locator, ReadFault Mode);

        private Fault? _fault;
        internal void Set(SiloExactLocator locator, ReadFault mode) => Volatile.Write(ref _fault, new(locator, mode));
        internal void Clear() => Volatile.Write(ref _fault, null);

        internal ReadFault? For(string key, string version) =>
            Volatile.Read(ref _fault) is { } fault && fault.Locator.Key == key && fault.Locator.VersionId == version ? fault.Mode : null;
    }

    private sealed class FaultingReadStore(ISiloObjectStore store, ExactReadFault fault) : ISiloObjectStore
    {
        public SiloStoreDescriptor Descriptor => store.Descriptor;
        public ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken) =>
            store.PutCreateOnlyAsync(request, cancellationToken);
        public ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken) =>
            store.LocateCurrentVersionAsync(key, cancellationToken);
        public IAsyncEnumerable<string> ListKeysAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            store.ListKeysAsync(prefix, startAfter, cancellationToken);

        public async ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken)
        {
            var mode = fault.For(key, versionId);
            if (mode == ReadFault.Unavailable)
                throw new SiloStoreUnavailableException(Descriptor.StoreId, "test read", writeMayHaveLanded: false);
            if (mode == ReadFault.Absent)
                return new(SiloOpenStatus.NoSuchVersion, null);
            if (mode != ReadFault.Damaged)
                return await store.OpenVersionAsync(key, versionId, cancellationToken).ConfigureAwait(false);
            using var opened = await store.OpenVersionAsync(key, versionId, cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            await opened.Content!.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            var bytes = output.ToArray();
            bytes[0] ^= 0x80;
            return new(SiloOpenStatus.Found, new MemoryStream(bytes, writable: false));
        }
    }

    private static async Task<(string GroupId, string RevisionId)> CaptureUntilInterruptedAsync(string root,
        Dictionary<string, string?> settings, string lifecycleToken, Guid pause, Func<string, string>? configureCamera,
        InterruptManifestPublication fault,
        CancellationToken token)
    {
        using var host = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
        await host.InitializeAsync(services =>
        {
            RemoveCaptureWorkers(services, stopCamera: false);
            services.AddSingleton<ISiloFaults>(fault);
        }, settings, root, configureCamera).ConfigureAwait(false);
        using var scope = host.CreateCameraAgentScope();
        var storage = scope.ServiceProvider.GetRequiredService<SiloInstalledStorage>();
        var loader = scope.ServiceProvider.GetRequiredService<ICameraAgentConfigurationLoader>();
        var configuration = await loader.LoadAsync(token).ConfigureAwait(false);
        scope.ServiceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration).DisposeSteps();
        using var client = host.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", lifecycleToken);
        using (var readiness = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            readiness.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                using var response = await client.GetAsync(VerifyInstallationRoute, readiness.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    response.EnsureSuccessStatusCode();
                    break;
                }
                await Task.Delay(50, readiness.Token).ConfigureAwait(false);
            }
        }
        using (var fresh = await client.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false))
        {
            fresh.EnsureSuccessStatusCode();
            var empty = await fresh.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
            Assert.IsNotNull(empty);
            Assert.AreEqual(1L, empty.Sequence);
            Assert.AreEqual(0L, empty.VerifiedRoles);
        }
        while (true)
        {
            using var response = await client.PostAsJsonAsync(LifecyclePrefix + "/first-resume",
                new { FirstBootId = pause, ExpectedVersion = 1 }, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                response.EnsureSuccessStatusCode();
                break;
            }
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        await fault.Interrupted.Task.WaitAsync(token).ConfigureAwait(false);
        var pending = (await storage.State.ListOpenAsync(8, token).ConfigureAwait(false)).Single();
        Assert.IsNull(await storage.Index.ReadPublicationAsync(pending.GroupId, pending.RevisionId, token).ConfigureAwait(false));
        var staged = await storage.State.ReadGroupAsync(pending.GroupId, pending.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.ManifestQualified, staged?.State);
        if (settings.ContainsKey("CameraAgent:Silo:MaximumStagedFiles"))
        {
            var ingress = scope.ServiceProvider.GetRequiredService<RawCaptureIngress>();
            Assert.IsNotNull(ingress.SiloPressureRelease);
            var admissions = scope.ServiceProvider.GetRequiredService<CaptureAdmissionCoordinator>();
            Assert.AreEqual(CaptureAdmissionState.Running, admissions.Snapshot.State);
            await AssertExposureWaitsAsync(admissions, token).ConfigureAwait(false);
            await admissions.PauseAsync("operator-pause", admissions.Snapshot.Version, "operator", null, token)
                .ConfigureAwait(false);
        }
        return pending;
    }

    private static async Task AssertExposureWaitsAsync(CaptureAdmissionCoordinator admissions, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var blocked = admissions.EnterAsync(wait.Token).AsTask();
        Assert.IsFalse(blocked.IsCompleted);
        await wait.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await blocked.ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<SiloReopenResult> WaitForReopenAsync(SiloInstalledStorage storage, string group, string revision,
        CancellationToken token)
    {
        while (true)
        {
            if (storage.BindingVerified && await storage.Index.ReadPublicationAsync(group, revision, token).ConfigureAwait(false) is { State: "visible" })
                return await storage.Catalog.ReopenAsync(group, revision, token).ConfigureAwait(false);
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }

    private static void RemoveCaptureWorkers(IServiceCollection services, bool stopCamera)
    {
        foreach (var registration in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
            (descriptor.ImplementationType?.GenericTypeArguments.Contains(typeof(CaptureDistributionService)) == true ||
                descriptor.ImplementationFactory is Func<IServiceProvider, TransientWorkerService> ||
                stopCamera && descriptor.ImplementationType == typeof(CameraCaptureService))).ToArray())
            services.Remove(registration);
    }

    private static string ColorConfiguration(string json)
    {
        var configuration = JsonNode.Parse(json)!;
        var sensor = configuration["rig"]!["sensor"]!;
        sensor["colorMode"] = "Color";
        sensor["pixelFormat"] = "BayerRggb16";
        sensor["responseMode"] = "BayerRaw";
        configuration["rig"]!.AsObject().Remove("readout");
        var options = configuration["module"]!["options"]!;
        options["asi174Sensor"]!["enabled"] = false;
        options["asi178Sensor"] = new JsonObject { ["enabled"] = true };
        return configuration.ToJsonString();
    }

    private static string CenteredConfiguration(string json, bool color)
    {
        var configuration = JsonNode.Parse(color ? ColorConfiguration(json) : json)!;
        configuration["centeredExposure"] = new JsonObject
        {
            ["cameraId"] = "installed-virtual",
            ["executionVersion"] = CenteredExposureProfile.CurrentExecutionVersion
        };
        var moduleOptions = configuration["module"]!["options"]!.AsObject();
        moduleOptions.Remove("transientScenario");
        moduleOptions.Remove("cloudScenario");
        if (!color)
        {
            configuration["rig"]!["readout"] = JsonNode.Parse("""
                {
                  "roi": { "x": 0, "y": 0, "width": 64, "height": 48 },
                  "binX": 1, "binY": 1, "binningAlgorithm": "IdentityV1",
                  "pixelFormat": "Mono16", "sampleDepthBits": 12, "containerDepthBits": 16,
                  "packing": "ByteAligned", "storedCodeTransform": "RightAlignedV1", "levelCodeSpace": "NativeSample",
                  "blackLevel": 64, "whiteLevel": 4095, "strideBytes": 128, "byteOrder": "LittleEndian", "cfaPattern": "None"
                }
                """);
        }
        var pipeline = configuration["pipeline"]!;
        var steps = pipeline["steps"]!.AsArray();
        var calibration = steps.Single(step => step!["id"]!.GetValue<string>() == "Calibration")!.DeepClone();
        calibration["publication"] = new JsonObject { ["persistence"] = "durable-local" };
        calibration["options"]!["outputVariant"] = "qualified-linear";
        var scene = steps.Single(step => step!["id"]!.GetValue<string>() == "ProjectedScene")!.DeepClone();
        scene["publication"] = new JsonObject { ["persistence"] = "durable-local" };
        var storage = steps.Single(step => step!["id"]!.GetValue<string>() == "LocalStorage")!.DeepClone();
        storage["dependsOn"] = new JsonArray("Calibration");
        var options = storage["options"]!.AsObject();
        options.Remove("policies");
        options["storageRoot"] = Path.Combine(options["storageRoot"]!.GetValue<string>(), "archive");
        var cloud = JsonNode.Parse("""
            {
              "id": "CloudAssessment", "type": "CloudAssessment", "order": 60,
              "dependsOn": ["$raw"], "required": false,
              "publication": { "persistence": "durable-local" }
            }
            """);
        var satellite = JsonNode.Parse("""
            {
              "id": "CaptureSatelliteTracks", "type": "CaptureSatelliteTracks", "order": 61,
              "options": { "catalogNumbers": ["25544"] },
              "dependsOn": ["$raw"], "required": false,
              "publication": { "persistence": "durable-local" }
            }
            """);
        pipeline["steps"] = new JsonArray(calibration, scene, storage, cloud, satellite);
        return configuration.ToJsonString();
    }

    private static Dictionary<string, string?> InstalledSettings(SiloStorageBinding binding, Uri endpoint,
        (string Access, string Secret) writer, Guid pause, string token) => new(StringComparer.Ordinal)
        {
            ["LifecycleControl:Token"] = token,
            ["InstallationVerification:Token"] = token,
            ["CameraAgent:FirstBootPause:Id"] = pause.ToString("D"),
            ["CameraAgent:Silo:StateContract"] = CameraAgentSiloBinding.StateContract,
            ["CameraAgent:Silo:Endpoint"] = endpoint.AbsoluteUri,
            ["CameraAgent:Silo:Bucket"] = binding.Bucket,
            ["CameraAgent:Silo:StoreId"] = binding.StoreId,
            ["CameraAgent:Silo:StorageBindingId"] = binding.BindingId,
            ["CameraAgent:Silo:WriterGeneration"] = "1",
            ["CameraAgent:Silo:AccessKey"] = writer.Access,
            ["CameraAgent:Silo:SecretKey"] = writer.Secret
        };

    private static async Task<(string Access, string Secret)> CreateWriterAsync(IContainer container, SiloStorageBinding binding,
        CancellationToken token)
    {
        var script = $$"""
            set -eu
            export MC_HOST_fixture="http://${MINIO_ROOT_USER}:${MINIO_ROOT_PASSWORD}@127.0.0.1:9000"
            cat > /tmp/hvo-silo-policy <<'JSON'
            {"Version":"2012-10-17","Statement":[
              {"Effect":"Allow","Action":["s3:GetBucketLocation","s3:GetBucketVersioning","s3:ListBucket","s3:ListBucketVersions"],"Resource":["arn:aws:s3:::{{binding.Bucket}}"]},
              {"Effect":"Allow","Action":["s3:GetObject","s3:GetObjectVersion","s3:PutObject","s3:DeleteObjectVersion"],"Resource":["arn:aws:s3:::{{binding.Bucket}}/*"]},
              {"Effect":"Deny","Action":["s3:PutObject","s3:DeleteObject","s3:DeleteObjectVersion"],"Resource":["arn:aws:s3:::{{binding.Bucket}}/meta/*"]}]}
            JSON
            mcli admin accesskey create fixture/ --policy /tmp/hvo-silo-policy --name hvo-writer-g1 --json
            """;
        var result = await container.ExecAsync(["/bin/sh", "-c", script], token).ConfigureAwait(false);
        Assert.AreEqual(0L, result.ExitCode, "The stock Silo writer credential could not be created.");
        using var document = JsonDocument.Parse(result.Stdout);
        return (document.RootElement.GetProperty("accessKey").GetString()!, document.RootElement.GetProperty("secretKey").GetString()!);
    }

    private static async Task InstallBindingAsync(AmazonS3Client admin, SiloStorageBinding binding, string accessKey, CancellationToken token)
    {
        var store = new SiloS3ObjectStore(admin,
            new(binding.StoreId, binding.Bucket, SiloStoreClass.Local, SiloRetrieval.Online, PersistenceQualified: true), TimeSpan.FromSeconds(30));
        var marker = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.MarkerFormat,
            instanceId = binding.InstanceId,
            storeId = binding.StoreId,
            bucket = binding.Bucket,
            storageBindingId = binding.BindingId,
            stateContract = CameraAgentSiloBinding.StateContract
        });
        var fence = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.FenceFormat,
            instanceId = binding.InstanceId,
            storageBindingId = binding.BindingId,
            writerGeneration = 1,
            writerAccessKeyId = accessKey
        });
        Assert.IsTrue((await store.PutCreateOnlyAsync(Put(CameraAgentSiloBinding.MarkerKey, marker), token).ConfigureAwait(false)).Created);
        Assert.IsTrue((await store.PutCreateOnlyAsync(Put(CameraAgentSiloBinding.FenceKey, fence), token).ConfigureAwait(false)).Created);
    }

    private static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private sealed class InterruptManifestPublication : ISiloFaults
    {
        public TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hit(SiloFaultPoint point)
        {
            if (point is not SiloFaultPoint.AfterManifestQualified &&
                !(Interrupted.Task.IsCompleted && point == SiloFaultPoint.AfterSegmentSpooled)) return;
            Interrupted.TrySetResult();
            throw new IOException("Interrupted after exact manifest qualification.");
        }
    }
}
