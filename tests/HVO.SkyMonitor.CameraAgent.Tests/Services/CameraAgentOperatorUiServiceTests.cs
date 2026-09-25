using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOperatorUiServiceTests
{
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

        Assert.AreEqual(16, authentication.ReadCount);
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
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Exactly(12));
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
        ICameraAgentCurrentImagePresentationService? presentations = null) => new(
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
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            [],
            [],
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = "/unused" }),
            tokenService!,
            TimeProvider.System,
            NullLogger<CameraAgentOperatorUiService>.Instance);

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
