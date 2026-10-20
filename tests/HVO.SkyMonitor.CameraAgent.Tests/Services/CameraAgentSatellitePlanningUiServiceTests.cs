using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSatellitePlanningUiServiceTests
{
    private static readonly CameraAgentSatellitePassSearchRequest Request = new(
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(6), "25544", 10, new('A', 64), new('B', 64));

    [TestMethod]
    public async Task Reads_AuthorizeEverySearchAndArcWithoutCallingPlannerWhenDeniedAsync()
    {
        var planner = new RecordingPlanner();
        var authorization = Authorization(false);
        var service = Create(authorization.Object, planner);
        var search = await service.SearchAsync(Request, CancellationToken.None).ConfigureAwait(false);
        var arc = await service.GetArcAsync("original-session", "original-pass", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, search.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, arc.Kind);
        Assert.AreEqual(0, planner.Calls);
        authorization.Verify(auth => auth.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Exactly(2));
    }

    [TestMethod]
    public async Task Reads_PreserveExactRequestsAndCancellationTokensAsync()
    {
        var planner = new RecordingPlanner();
        var authorization = Authorization(true);
        var service = Create(authorization.Object, planner);
        using var cancellation = new CancellationTokenSource();
        var result = await service.SearchAsync(Request, cancellation.Token).ConfigureAwait(false);
        await service.GetArcAsync("original-session", "original-pass", cancellation.Token).ConfigureAwait(false);
        Assert.AreSame(Request, planner.Request);
        Assert.AreEqual(("original-session", "original-pass"), planner.ArcIdentity);
        Assert.AreEqual(cancellation.Token, planner.Token);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.NoSnapshot, result.Value!.Outcome);
        Assert.DoesNotContain("/private", result.Value.Message, StringComparison.Ordinal);
        authorization.Verify(auth => auth.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Exactly(2));
    }

    [TestMethod]
    public async Task Read_WithoutPlannerIsUnavailableAfterAuthorizationAsync()
    {
        var authorization = Authorization(true);
        var result = await Create(authorization.Object, null).SearchAsync(Request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        authorization.Verify(auth => auth.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Once);
    }

    [TestMethod]
    public async Task Reads_SanitizeFailuresAndRethrowCancellationAsync()
    {
        var planner = new RecordingPlanner { Failure = new IOException("/private/snapshot.json: secret") };
        var service = Create(Authorization(true).Object, planner);
        var search = await service.SearchAsync(Request, CancellationToken.None).ConfigureAwait(false);
        var arc = await service.GetArcAsync("session", "pass", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, search.Kind);
        Assert.AreEqual(search.Message, arc.Message);
        Assert.DoesNotContain("/private", search.Message!, StringComparison.Ordinal);
        planner.Failure = new OperationCanceledException();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.SearchAsync(Request, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.GetArcAsync("session", "pass", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Read_WithPreCancelledTokenDoesNotCallPlannerAsync()
    {
        var planner = new RecordingPlanner();
        var service = Create(Authorization(true).Object, planner);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.SearchAsync(Request, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(0, planner.Calls);
    }

    [TestMethod]
    public void Registration_ComposesOnlyTheUiAdapterAndIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddCameraAgentSatellitePlanningUi().AddCameraAgentSatellitePlanningUi();
        Assert.HasCount(1, services.Where(item => item.ServiceType == typeof(ICameraAgentSatellitePlanningUiService)).ToArray());
        Assert.IsFalse(services.Any(item => item.ServiceType == typeof(ICameraAgentSatellitePlanner)));
    }

    private static Mock<IAuthorizationService> Authorization(bool allowed)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(auth => auth.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1)).ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization;
    }

    private static CameraAgentSatellitePlanningUiService Create(IAuthorizationService authorization, ICameraAgentSatellitePlanner? planner)
        => new(new Authentication(), authorization, NullLogger<CameraAgentSatellitePlanningUiService>.Instance, planner);

    private sealed class Authentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "operator")], "test"))));
    }

    private sealed class RecordingPlanner : ICameraAgentSatellitePlanner
    {
        internal int Calls { get; private set; }
        internal CameraAgentSatellitePassSearchRequest? Request { get; private set; }
        internal (string, string) ArcIdentity { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal Exception? Failure { get; set; }

        public ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>> SearchAsync(
            CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Token = cancellationToken;
            if (Failure is { } failure) return ValueTask.FromException<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>(failure);
            return ValueTask.FromResult(new CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>(
                CameraAgentSatellitePlanningOutcome.NoSnapshot, "/private/must-not-reach-operator", null));
        }

        public ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>> GetArcAsync(
            string searchIdentitySha256, string parentPassSha256, CancellationToken cancellationToken)
        {
            Calls++;
            ArcIdentity = (searchIdentitySha256, parentPassSha256);
            Token = cancellationToken;
            if (Failure is { } failure) return ValueTask.FromException<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>(failure);
            return ValueTask.FromResult(new CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>(
                CameraAgentSatellitePlanningOutcome.NoSnapshot, "/private/must-not-reach-operator", null));
        }
    }
}
