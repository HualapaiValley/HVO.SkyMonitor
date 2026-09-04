using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSkyMapUiServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task GetSkyMapAsync_WhenTheOperationsReadPolicyFails_DeniesWithoutProjectingAsync()
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service.GetSkyMapAsync(null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, projection.Calls);
        authorization.Verify(
            service => service.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1),
            Times.Once);
    }

    [TestMethod]
    public async Task GetSkyMapAsync_WhenAuthorized_ProjectsTheRequestedInstantAsync()
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service.GetSkyMapAsync(Now.AddHours(-3), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(Now.AddHours(-3), projection.LastRequested);
        authorization.Verify(
            service => service.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1),
            Times.Once);
    }

    [TestMethod]
    [DataRow(25d, 0d)]
    [DataRow(0d, 63d)]
    public async Task GetSkyMapAsync_ForAnInstantOutsideTheAcceptedWindow_IsRejectedAsync(double futureHours, double pastDays)
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service
            .GetSkyMapAsync(Now.AddHours(futureHours).AddDays(-pastDays), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual(CameraAgentSkyMapInstantBounds.RejectionMessage, result.Message);
        Assert.AreEqual(0, projection.Calls);
    }

    [TestMethod]
    [DataRow(24d, 0d)]
    [DataRow(0d, 62d)]
    public async Task GetSkyMapAsync_AtTheWindowBoundary_IsAcceptedAsync(double futureHours, double pastDays)
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service
            .GetSkyMapAsync(Now.AddHours(futureHours).AddDays(-pastDays), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(1, projection.Calls);
    }

    [TestMethod]
    public async Task GetSkyMapAsync_WhenTheProjectionFails_ReturnsAFixedUnavailableStateAsync()
    {
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new ThrowingProjection(), principal, authorization.Object);

        var result = await service.GetSkyMapAsync(null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The sky map projection is unavailable.", result.Message);
    }

    [TestMethod]
    public async Task GetManualLocationAsync_WhenTheOperationsReadPolicyFails_DeniesWithoutReadingTheStoreAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await service.GetManualLocationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, store.Reads);
    }

    [TestMethod]
    public async Task GetManualLocationAsync_WhenAuthorized_ReturnsTheProtectedManualStateAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await service.GetManualLocationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.IsTrue(result.Value!.Supported);
        Assert.AreEqual(1, store.Reads);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_WhenTheMutatePolicyFails_DeniesWithoutTouchingTheStoreAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsEmpty(store.Requests);
        authorization.Verify(
            service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1),
            Times.Once);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_WhenAuthorized_PassesTheOwnerIdAsTheRecordedActorAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var request = store.Requests.Single();
        Assert.AreEqual("owner-id", request.Actor);
        Assert.AreEqual("key-1", request.IdempotencyKey);
        Assert.AreEqual(4L, request.ExpectedVersion);
        Assert.AreEqual("America/Phoenix", request.TimeZoneId);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_MapsRejectedCommandsToTheirOperatorResultKindAsync()
    {
        var store = new RecordingDeploymentLocationStore
        {
            Status = ManualDeploymentLocationStatus.Conflict,
            ReasonCode = ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode
        };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var conflict = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, conflict.Kind);
        Assert.Contains("Refresh before retrying", conflict.Message!, StringComparison.Ordinal);

        store.Status = ManualDeploymentLocationStatus.Invalid;
        store.ReasonCode = null;
        store.FieldPath = "location.timeZoneId";

        var invalid = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, invalid.Kind);
        Assert.Contains("IANA identifier", invalid.Message!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_WhenTheStoreFails_ReturnsAFixedUnavailableStateAsync()
    {
        var store = new RecordingDeploymentLocationStore { Throw = true };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The coordinate change could not be completed.", result.Message);
    }

    private static async Task<OperatorUiResult<ManualDeploymentLocationResult>> ApplyAsync(
        CameraAgentSkyMapUiService service)
        => await service.ApplyManualLocationAsync(
            31.5,
            -110.25,
            1400,
            "America/Phoenix",
            4,
            "key-1",
            "relocated",
            CancellationToken.None).ConfigureAwait(false);

    private static Mock<IAuthorizationService> CreateAuthorization(out ClaimsPrincipal principal, bool succeeded)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(
                    CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        principal = user;
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, It.IsAny<string>()))
            .ReturnsAsync(succeeded ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization;
    }

    private static CameraAgentSkyMapUiService CreateService(
        ICameraAgentSkyMapProjection projection,
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        IDeploymentLocationStore? store = null)
        => new(
            new StubAuthenticationStateProvider(principal),
            authorization,
            projection,
            store ?? new RecordingDeploymentLocationStore(),
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentSkyMapUiService>.Instance);

    private sealed class RecordingDeploymentLocationStore : IDeploymentLocationStore
    {
        private static readonly ManualDeploymentLocationState State = new(
            Supported: true,
            LocationId: "hvo-observatory",
            KnownVersion: 4,
            NextVersion: 5,
            CentralAcknowledgementRequired: false,
            StagedAcknowledgementPending: false,
            CandidateAwaitingAcknowledgement: false,
            Override: null,
            OverrideSupersededAtUtc: null,
            History: []);

        internal int Reads { get; private set; }

        internal List<ManualDeploymentLocationRequest> Requests { get; } = [];

        internal ManualDeploymentLocationStatus Status { get; set; } = ManualDeploymentLocationStatus.Applied;

        internal string? ReasonCode { get; set; }

        internal string? FieldPath { get; set; }

        internal bool Throw { get; set; }

        public DeploymentLocationSnapshot? Active => null;

        public ManualDeploymentLocationState Manual
        {
            get
            {
                Reads++;
                return State;
            }
        }

        public ValueTask<ManualDeploymentLocationResult> ApplyManualAsync(
            ManualDeploymentLocationRequest request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("fixture failure");
            }
            Requests.Add(request);
            return ValueTask.FromResult(
                new ManualDeploymentLocationResult(Status, ReasonCode, FieldPath, State));
        }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProjection : ICameraAgentSkyMapProjection
    {
        internal int Calls { get; private set; }

        internal DateTimeOffset? LastRequested { get; private set; }

        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequested = atUtc;
            return ValueTask.FromResult(SkyMapTestData.Result(atUtc ?? Now));
        }
    }

    private sealed class ThrowingProjection : ICameraAgentSkyMapProjection
    {
        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("fixture failure");
    }
}
