using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Scheduling;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOperatorUiServiceTests
{
    [TestMethod]
    [DataRow("no-store", "  North Camera  ", "North Camera")]
    [DataRow("no-store", "   ", null)]
    [DataRow("recorded", "North Camera", "East dome")]
    [DataRow("cleared", "North Camera", null)]
    [DataRow("unreadable", " North Camera ", "North Camera")]
    [DataRow("unreadable", null, null)]
    public async Task ReadDisplayNameAsync_PrefersTheSavedNameAndDegradesToTheConfiguredOneAsync(
        string scenario,
        string? configured,
        string? expected)
    {
        var store = new Mock<ISiteProfileStore>(MockBehavior.Strict);
        if (scenario is "recorded" or "cleared")
        {
            // Once the settings file names the camera, or clears the name, the installer name no longer applies.
            var cameraName = scenario == "recorded" ? "East dome" : null;
            store.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SiteProfileState(
                "version-1",
                new SiteProfileValues("Hualapai Valley Observatory", cameraName, null, null),
                cameraName,
                "/srv/hvo/App_Data/appsettings.local.json",
                null));
        }
        else
        {
            store.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidDataException("fixture failure"));
        }

        var name = await CameraAgentOperatorUiService.ReadDisplayNameAsync(
            scenario == "no-store" ? null : store.Object,
            configured,
            NullLogger.Instance,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(expected, name);
    }

    [TestMethod]
    public async Task ReadDisplayNameAsync_WhenTheCallerCancels_DoesNotSwallowItAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var store = new Mock<ISiteProfileStore>(MockBehavior.Strict);
        store.Setup(item => item.GetAsync(cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await CameraAgentOperatorUiService.ReadDisplayNameAsync(
                store.Object, "North Camera", NullLogger.Instance, cancellation.Token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EveryReadAndMutation_ReReadsPrincipalAndReauthorizesAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-id")],
            "test"));
        var authentication = new CountingAuthenticationStateProvider(principal);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(
                principal,
                null,
                It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Failed());
        var service = CreateService(authentication, authorization.Object);

        var operations = await service.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
        var gallery = await service.GetGalleryPageAsync(new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);
        var current = await service.GetCurrentImagePresentationAsync(CancellationToken.None).ConfigureAwait(false);
        var quarantine = await service.GetQuarantinePageAsync("Artifact", null, null, 25, CancellationToken.None).ConfigureAwait(false);
        var detail = await service.GetGalleryCaptureAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var detailView = await service.GetCaptureDetailViewAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var system = await service.GetSystemStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var capture = await service.SetCapturePausedAsync(true, 1, "operation-key", CancellationToken.None).ConfigureAwait(false);
        var outbox = await service.ResolveOutboxAsync(
            "Artifact",
            OutboxOperationAction.Replay,
            "action-token",
            "evidence-restored",
            "operation-key",
            CancellationToken.None).ConfigureAwait(false);
        var captureLane = await service.ResolveOutboxAsync(
            "TransientRuntime",
            OutboxOperationAction.Abandon,
            "action-token",
            "operator-approved-loss",
            "operation-key-2",
            CancellationToken.None).ConfigureAwait(false);
        var ownership = await service.BindTransientRuntimeOwnershipAsync(
            "reference-token", "d331-0821084607", new string('D', 64), true,
            CancellationToken.None).ConfigureAwait(false);
        var currentSky = await service.GetCurrentSkyViewAsync(CancellationToken.None).ConfigureAwait(false);
        var calendar = await service.GetArchiveCalendarAsync(
            new CameraAgentGalleryCalendarQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3)), CancellationToken.None).ConfigureAwait(false);
        var neighbours = await service.GetGalleryNeighboursAsync(Guid.NewGuid(), new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);
        var products = await service.GetProductPageAsync(new CameraAgentProductQuery(), CancellationToken.None).ConfigureAwait(false);
        var product = await service.GetProductDetailAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var delivery = await service.GetDeliveryRecordsAsync(CancellationToken.None).ConfigureAwait(false);
        var reconciliation = await service.GetStorageReconciliationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(18, authentication.ReadCount);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, delivery.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, reconciliation.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, currentSky.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, calendar.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, neighbours.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, products.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, product.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, operations.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, gallery.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, current.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, quarantine.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, detail.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, detailView.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, system.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, capture.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, outbox.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, captureLane.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, ownership.Kind);
        authorization.Verify(service => service.AuthorizeAsync(
            principal,
            null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Exactly(14));
        authorization.Verify(service => service.AuthorizeAsync(
            principal,
            null,
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Exactly(4));
    }

    [TestMethod]
    public void SystemAliases_ResolveRegisteredAssemblyTypesAndRejectUnknownTypes()
    {
        var implementationType = typeof(CameraAgentOperatorUiServiceTests);
        var assemblyQualified = implementationType.AssemblyQualifiedName!;
        var processing = new CaptureProcessingStepRegistration(
            "SafeProcessing",
            implementationType,
            typeof(object));
        var module = new CameraModuleRegistration("SafeModule", implementationType);

        Assert.AreEqual(
            "SafeProcessing",
            CameraAgentOperatorUiService.ResolveProcessingAlias(assemblyQualified, [processing]));
        Assert.AreEqual(
            "SafeModule",
            CameraAgentOperatorUiService.ResolveModuleAlias(assemblyQualified, [module]));
        Assert.AreEqual(
            "Unavailable",
            CameraAgentOperatorUiService.ResolveProcessingAlias("Unknown.Type, Unknown.Assembly", [processing]));
        Assert.AreEqual(
            "Unavailable",
            CameraAgentOperatorUiService.ResolveModuleAlias("Unknown.Type, Unknown.Assembly", [module]));
    }

    [TestMethod]
    public async Task BindTransientOwnership_RequiresAcknowledgmentAndSealsNormalizedEvidenceAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        var authentication = new CountingAuthenticationStateProvider(principal);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var tokens = new OutboxOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(1));
        var service = CreateService(authentication, authorization.Object, tokens);
        var updated = new DateTimeOffset(2026, 8, 22, 1, 2, 3, TimeSpan.Zero);
        var target = new TransientRuntimeOperationTarget(
            42, 43, 44, "agent-east", 10, Guid.NewGuid(), Guid.NewGuid(),
            new string('A', 64), new string('C', 64), new string('B', 64), "hybrid", true,
            "completed", "quarantined", "quarantined", "transient-runtime.input-levels-invalid",
            updated.AddSeconds(-2), updated.AddSeconds(-1), updated);
        var reference = tokens.ProtectTransientRuntimeReference(target);

        var missingAcknowledgment = await service.BindTransientRuntimeOwnershipAsync(
            reference, "d331-0821084607", new string('d', 64), false, CancellationToken.None).ConfigureAwait(false);
        var bound = await service.BindTransientRuntimeOwnershipAsync(
            reference, "d331-0821084607", new string('d', 64), true, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, missingAcknowledgment.Kind);
        Assert.IsTrue(bound.IsSuccess);
        Assert.AreEqual("d331-0821084607", bound.Value!.DeploymentRunId);
        Assert.AreEqual(new string('D', 64), bound.Value.InventorySha256);
        Assert.IsTrue(tokens.TryReadTransientRuntimeAction(bound.Value.ActionToken, out var parsed));
        Assert.AreEqual(new TransientRuntimeExternalOwnershipEvidence(
            "d331-0821084607", new string('D', 64), true), parsed!.ExternalOwnershipEvidence);
    }

    [TestMethod]
    public void SnapshotIdentityIsCanonicalAndChangesOnlyWithAllowlistedDtoFacts()
    {
        var status = OperatorUiTestData.SystemStatus() with
        {
            SnapshotIdentity = "ignored",
            Pipeline =
            [
                new("z-node", "Preview", true, ["b", "a"]),
                new("a-node", "Calibration", false, [])
            ]
        };
        var reordered = status with
        {
            SnapshotIdentity = "another-ignored-value",
            Pipeline =
            [
                new("a-node", "Calibration", false, []),
                new("z-node", "Preview", true, ["a", "b"])
            ]
        };
        var changed = status with { Capture = status.Capture with { NightGain = status.Capture.NightGain + 1 } };
        var standalone = status with { CentralIntegration = "Disabled" };

        var identity = CameraAgentOperatorUiService.ComputeSnapshotIdentity(status);

        Assert.AreEqual(identity, CameraAgentOperatorUiService.ComputeSnapshotIdentity(reordered));
        Assert.AreNotEqual(identity, CameraAgentOperatorUiService.ComputeSnapshotIdentity(changed));
        Assert.AreNotEqual(identity, CameraAgentOperatorUiService.ComputeSnapshotIdentity(standalone));
        Assert.AreEqual(64, identity.Length);
        Assert.IsTrue(identity.All(Uri.IsHexDigit));
        var serialized = System.Text.Json.JsonSerializer.Serialize(status with { SnapshotIdentity = identity });
        Assert.IsFalse(serialized.Contains("/private/config", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("client-secret", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("options", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(CameraAgentLayeredPresentationStatus.NotRetained, true)]
    [DataRow(CameraAgentLayeredPresentationStatus.Unavailable, false)]
    [DataRow(CameraAgentLayeredPresentationStatus.Malformed, false)]
    public async Task OnlyConfirmedManifestAbsenceMapsToNotFoundAsync(CameraAgentLayeredPresentationStatus status, bool absent)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var layers = new Mock<ICameraAgentLayeredPresentationService>();
        layers.Setup(value => value.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentLayeredPresentationResult(status, Reason: "Retained layer status."));
        var service = CreateService(new CountingAuthenticationStateProvider(principal), authorization.Object, layers: layers.Object);
        var result = await service.GetLayeredPresentationAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(absent ? OperatorUiResultKind.NotFound : OperatorUiResultKind.Unavailable, result.Kind);
    }

    [TestMethod]
    public async Task CaptureDetailUsesExactValidatedPresentationAndCombinedIdentityAsync()
    {
        var capture = OperatorUiTestData.Capture(Guid.NewGuid()) with { RawState = "committed" };
        var raw = capture.Artifacts[0];
        var combined = raw with
        {
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Combined,
            Variant = "a-selected",
            SourceArtifactIds = [raw.ArtifactId],
            CreatedUtc = OperatorUiTestData.Now.AddMinutes(-2)
        };
        var newest = combined with
        {
            ArtifactId = Guid.NewGuid(),
            Variant = "z-newest",
            CreatedUtc = OperatorUiTestData.Now,
            SourceArtifactIds = [Guid.NewGuid(), Guid.NewGuid()]
        };
        var derivative = capture.Artifacts[1] with
        {
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            SourceArtifactIds = [combined.ArtifactId],
            Recipe = new CameraAgentGalleryRecipe("encoded-preview", "1", "encoded-preview-v1", "OPTIONS", "IDENTITY")
        };
        capture = capture with { Artifacts = [raw, combined, newest, derivative] };
        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .ProjectWithRetainedDisplay(capture);
        var gallery = new Mock<ICameraAgentGallery>(MockBehavior.Strict);
        var presentations = new Mock<ICameraAgentCurrentImagePresentationService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        gallery.Setup(value => value.GetCaptureAsync(capture.CaptureId, cancellation.Token)).ReturnsAsync(capture);
        presentations.Setup(value => value.ProjectCaptureAsync(capture, cancellation.Token)).ReturnsAsync(projection);
        var service = CreateCaptureDetailService(gallery.Object, presentations.Object);

        var result = await service.GetCaptureDetailViewAsync(capture.CaptureId, cancellation.Token).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreSame(capture, result.Value!.Capture);
        CollectionAssert.AreEqual(projection.Stages.ToArray(), result.Value.Presentation.Stages.ToArray());
        var slot = result.Value.Presentation.Stages.Single(static value => value.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(combined.ArtifactId, slot.ArtifactId);
        Assert.AreEqual(derivative.ArtifactId, slot.DisplayArtifactId);
        Assert.AreEqual(derivative.ArtifactId, slot.DisplayReferenceId);
        Assert.IsNotNull(result.Value.Facts);
        Assert.AreEqual(capture.CaptureId, result.Value.Facts.CaptureId);
        Assert.AreEqual(capture.ExposureStartedUtc, result.Value.Facts.ExposureStartedUtc);
        Assert.AreEqual(capture.Detail!.Controls!.EffectiveExposureMilliseconds, result.Value.Facts.ExposureMilliseconds);
        Assert.AreEqual(capture.Detail.CloudAssessment!.CoverageMillionths, result.Value.Facts.Cloud.CoverageMillionths);
        Assert.AreEqual(combined.ArtifactId, result.Value.Facts.CombinedLineage!.ArtifactId);
        CollectionAssert.AreEqual(combined.SourceArtifactIds.ToArray(), result.Value.Facts.CombinedLineage.SourceArtifactIds.ToArray());
        gallery.VerifyAll();
        gallery.VerifyNoOtherCalls();
        presentations.VerifyAll();
        presentations.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task CaptureDetailNeverBorrowsCombinedLineageWithoutAValidatedSlotAsync(bool retainedCombined, bool truncated)
    {
        var capture = OperatorUiTestData.Capture(Guid.NewGuid()) with { ArtifactsTruncated = truncated };
        if (retainedCombined)
        {
            capture = capture with
            {
                Artifacts = [.. capture.Artifacts, capture.Artifacts[0] with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Combined }]
            };
        }
        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions())).Project(capture);
        projection = projection with
        {
            Stages = projection.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                ? new CameraAgentPresentationSlot(slot.Stage, slot.Label,
                    retainedCombined || truncated ? CameraAgentPresentationSlotAvailability.Unavailable : CameraAgentPresentationSlotAvailability.Missing,
                    retainedCombined ? "Gone" : truncated ? "ProjectionBoundReached" : "NotProduced")
                : slot).ToArray()
        };
        var gallery = new Mock<ICameraAgentGallery>(MockBehavior.Strict);
        var presentations = new Mock<ICameraAgentCurrentImagePresentationService>(MockBehavior.Strict);
        gallery.Setup(value => value.GetCaptureAsync(capture.CaptureId, CancellationToken.None)).ReturnsAsync(capture);
        presentations.Setup(value => value.ProjectCaptureAsync(capture, CancellationToken.None)).ReturnsAsync(projection);

        var result = await CreateCaptureDetailService(gallery.Object, presentations.Object)
            .GetCaptureDetailViewAsync(capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.IsNotNull(result.Value!.Facts);
        Assert.IsNull(result.Value.Facts.CombinedLineage);
        Assert.AreEqual(retainedCombined || truncated, result.Value.Facts.CombinedLineageUnavailable);
        gallery.VerifyAll();
        gallery.VerifyNoOtherCalls();
        presentations.VerifyAll();
        presentations.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CaptureDetailRejectsMissingOrWrongReturnedCaptureWithoutProjectionAsync(bool wrongId)
    {
        var requestedId = Guid.NewGuid();
        var gallery = new Mock<ICameraAgentGallery>(MockBehavior.Strict);
        var presentations = new Mock<ICameraAgentCurrentImagePresentationService>(MockBehavior.Strict);
        gallery.Setup(value => value.GetCaptureAsync(requestedId, CancellationToken.None))
            .ReturnsAsync(wrongId ? OperatorUiTestData.Capture(Guid.NewGuid()) : null);

        var result = await CreateCaptureDetailService(gallery.Object, presentations.Object)
            .GetCaptureDetailViewAsync(requestedId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.NotFound, result.Kind);
        Assert.IsNull(result.Value);
        gallery.VerifyAll();
        gallery.VerifyNoOtherCalls();
        presentations.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task DeliveryRecords_MergeLocationsWithUnfinishedWorkFirstAsync()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var resolver = new Mock<ICameraAgentStorageResolver>(MockBehavior.Strict);
        resolver.Setup(value => value.GetUploadLocationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CameraAgentStorageLocation("captures", "/data/captures"), new CameraAgentStorageLocation("archive", "/data/archive")]);
        var outbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        outbox.Setup(value => value.ReadRecentDeliveryAsync("/data/captures", SqliteArtifactOutbox.MaximumRecentDeliveryRecords, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                DeliveryRecord(1, ArtifactOutboxStatus.Acknowledged, now.AddMinutes(-30), acknowledgedUtc: now.AddMinutes(-2)),
                DeliveryRecord(2, ArtifactOutboxStatus.Retry, now.AddMinutes(-20)),
            ]);
        outbox.Setup(value => value.ReadRecentDeliveryAsync("/data/archive", SqliteArtifactOutbox.MaximumRecentDeliveryRecords, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                DeliveryRecord(3, ArtifactOutboxStatus.Pending, now.AddMinutes(-5)),
                DeliveryRecord(4, ArtifactOutboxStatus.Abandoned, now.AddMinutes(-40), updatedUtc: now.AddMinutes(-1)),
            ]);
        var service = CreateReadService(storageResolver: resolver.Object, artifactOutbox: outbox.Object);

        var result = await service.GetDeliveryRecordsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        string[] expected = ["captures:2", "archive:3", "archive:4", "captures:1"];
        CollectionAssert.AreEqual(
            expected,
            result.Value!.Select(static item => $"{item.StorageAlias}:{item.Record.CaptureSequence}").ToArray());
    }

    [TestMethod]
    public async Task DeliveryRecords_ShareTheRowsAcrossLocationsWhateverTheCaptureTimesAsync()
    {
        // The archive's single record was queued last but holds the oldest capture. Capture times do not order
        // one queue against another, so the full captures queue must not crowd it out of the bounded list.
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var resolver = new Mock<ICameraAgentStorageResolver>(MockBehavior.Strict);
        resolver.Setup(value => value.GetUploadLocationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CameraAgentStorageLocation("captures", "/data/captures"), new CameraAgentStorageLocation("archive", "/data/archive")]);
        var outbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        outbox.Setup(value => value.ReadRecentDeliveryAsync("/data/captures", SqliteArtifactOutbox.MaximumRecentDeliveryRecords, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, SqliteArtifactOutbox.MaximumRecentDeliveryRecords)
                .Select(index => DeliveryRecord(100 - index, ArtifactOutboxStatus.Pending, now.AddMinutes(-index)))
                .ToArray());
        outbox.Setup(value => value.ReadRecentDeliveryAsync("/data/archive", SqliteArtifactOutbox.MaximumRecentDeliveryRecords, It.IsAny<CancellationToken>()))
            .ReturnsAsync([DeliveryRecord(7, ArtifactOutboxStatus.Pending, now.AddDays(-3))]);
        var service = CreateReadService(storageResolver: resolver.Object, artifactOutbox: outbox.Object);

        var result = await service.GetDeliveryRecordsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var shown = result.Value!.Select(static item => $"{item.StorageAlias}:{item.Record.CaptureSequence}").ToArray();
        Assert.HasCount(SqliteArtifactOutbox.MaximumRecentDeliveryRecords, shown);
        string[] head = ["captures:100", "archive:7", "captures:99", "captures:98"];
        CollectionAssert.AreEqual(head, shown[..4]);
        Assert.AreEqual("captures:77", shown[^1]);
    }

    [TestMethod]
    public async Task DeliveryRecords_WhenStandalone_ReadNoOutboxAsync()
    {
        // Strict mocks with no setups fail the test if the standalone path touches an outbox store.
        var service = CreateReadService(
            storageResolver: new Mock<ICameraAgentStorageResolver>(MockBehavior.Strict).Object,
            artifactOutbox: new Mock<IArtifactOutbox>(MockBehavior.Strict).Object,
            hostOptions: new CameraAgentHostOptions
            {
                RawIngressRoot = "/unused",
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            });

        var result = await service.GetDeliveryRecordsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsEmpty(result.Value!);
    }

    [TestMethod]
    public async Task DeliveryRecords_WhenAStoreFails_ReturnAFixedUnavailableMessageAsync()
    {
        var resolver = new Mock<ICameraAgentStorageResolver>(MockBehavior.Strict);
        resolver.Setup(value => value.GetUploadLocationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CameraAgentStorageLocation("captures", "/data/captures")]);
        var outbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        outbox.Setup(value => value.ReadRecentDeliveryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("/data/captures/outbox.db is locked"));
        var service = CreateReadService(storageResolver: resolver.Object, artifactOutbox: outbox.Object);

        var result = await service.GetDeliveryRecordsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The delivery outbox could not be read.", result.Message);
    }

    [TestMethod]
    public async Task StorageReconciliation_ReturnsTheLatestRecordedPassesAsync()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var rawIngress = new RawIngressState(time);
        var processing = new CaptureProcessingState();
        var service = CreateReadService(rawIngressState: rawIngress, captureProcessingState: processing);

        var before = await service.GetStorageReconciliationAsync(CancellationToken.None).ConfigureAwait(false);
        rawIngress.RecordReconciliation(new RawIngressReconciliationSummary(40, 2, 1, 1, 0, 4096, IndexProjectionFailures: 3));
        processing.RecordReconciliation(time.GetUtcNow(), new DerivedProductReconciliationSummary(120, 110, 5, 3, 1, 1, 2048));
        var after = await service.GetStorageReconciliationAsync(CancellationToken.None).ConfigureAwait(false);
        processing.RecordReconciliation(time.GetUtcNow().AddMinutes(5), null);
        var failed = await service.GetStorageReconciliationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new CameraAgentStorageReconciliation(null, null), before.Value);
        Assert.AreEqual(new RawIngressReconciliationReport(time.GetUtcNow(), 40, 2, 1, 1, 0, 3), after.Value!.RawIngress);
        Assert.AreEqual(new DerivedProductReconciliationReport(time.GetUtcNow(), true, 120, 5, 3, 1, 1), after.Value.DerivedProducts);
        // A failed pass replaces the last success, so the page never shows old counts as current.
        Assert.AreEqual(new DerivedProductReconciliationReport(time.GetUtcNow().AddMinutes(5), false, 0, 0, 0, 0, 0), failed.Value!.DerivedProducts);
        Assert.AreEqual(after.Value.RawIngress, failed.Value.RawIngress);
    }

    private static CameraAgentOperatorUiService CreateReadService(
        ICameraAgentStorageResolver? storageResolver = null,
        IArtifactOutbox? artifactOutbox = null,
        CameraAgentHostOptions? hostOptions = null,
        RawIngressState? rawIngressState = null,
        CaptureProcessingState? captureProcessingState = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        return CreateService(new CountingAuthenticationStateProvider(principal), authorization.Object,
            storageResolver: storageResolver, artifactOutbox: artifactOutbox, hostOptions: hostOptions,
            rawIngressState: rawIngressState, captureProcessingState: captureProcessingState);
    }

    private static ArtifactOutboxDeliveryRecord DeliveryRecord(
        long sequence, ArtifactOutboxStatus status, DateTimeOffset createdUtc,
        DateTimeOffset? acknowledgedUtc = null, DateTimeOffset? updatedUtc = null)
        => new(sequence, FrameArtifactRole.Preview, "image/png", 1024, status, 1,
            createdUtc, updatedUtc ?? createdUtc, createdUtc, acknowledgedUtc, null);

    private static CameraAgentOperatorUiService CreateCaptureDetailService(
        ICameraAgentGallery gallery,
        ICameraAgentCurrentImagePresentationService presentations)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        return CreateService(new CountingAuthenticationStateProvider(principal), authorization.Object,
            gallery: gallery, presentations: presentations);
    }

    private static CameraAgentOperatorUiService CreateService(
        AuthenticationStateProvider authentication,
        IAuthorizationService authorization,
        OutboxOperationsTokenService? tokenService = null,
        ICameraAgentLayeredPresentationService? layers = null,
        ICameraAgentGallery? gallery = null,
        ICameraAgentCurrentImagePresentationService? presentations = null,
        ICameraAgentConfigurationAccessor? configuration = null,
        CaptureScheduleRuntimeCoordinator? scheduleRuntime = null,
        ICameraAgentStorageResolver? storageResolver = null,
        IArtifactOutbox? artifactOutbox = null,
        CameraAgentHostOptions? hostOptions = null,
        RawIngressState? rawIngressState = null,
        CaptureProcessingState? captureProcessingState = null) => new(
            authentication,
            authorization,
            operationsProvider: null!,
            gallery: gallery!,
            archive: null!,
            observingDays: new FixedObservingDayCalendarProvider(ObservingDayCalendar.Create("America/Phoenix")),
            currentImagePresentation: presentations!,
            layeredPresentations: layers!,
            null!,
            null!,
            storageResolver!,
            artifactOutbox!,
            null!,
            null!,
            null!,
            configuration!,
            scheduleRuntime,
            [],
            [],
            Options.Create(hostOptions ?? new CameraAgentHostOptions { RawIngressRoot = "/unused" }),
            tokenService!,
            TimeProvider.System,
            NullLogger<CameraAgentOperatorUiService>.Instance,
            rawIngressState: rawIngressState,
            captureProcessingState: captureProcessingState);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        internal int ReadCount { get; private set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            ReadCount++;
            return Task.FromResult(new AuthenticationState(principal));
        }
    }
}
