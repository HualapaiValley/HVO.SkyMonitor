using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Authorization;
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

    private static Mock<IAuthorizationService> CreateAuthorization(out ClaimsPrincipal principal, bool succeeded)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-id")], "test"));
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
        IAuthorizationService authorization)
        => new(
            new StubAuthenticationStateProvider(principal),
            authorization,
            projection,
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentSkyMapUiService>.Instance);

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
