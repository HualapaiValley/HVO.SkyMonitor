using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentEnvironmentalUiServiceTests
{
    [TestMethod]
    public async Task AcquireAsync_ReauthorizesMutationAndRejectsMissingActor()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var service = CreateService(principal, authorization.Object, EnabledSource(onDemand: true));

        var result = await service.AcquireAsync(
            "source-1", "command-1", "operator check", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        authorization.Verify(service => service.AuthorizeAsync(
            principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Once);
    }

    [TestMethod]
    public async Task AcquireAsync_RejectsDisabledAndIneligibleSourcesBeforeCommandExecution()
    {
        var disabled = CreateAuthorizedService(EnabledSource(onDemand: true), enabled: false);
        var disabledResult = await disabled.AcquireAsync(
            "source-1", "command-1", "operator check", CancellationToken.None).ConfigureAwait(false);
        var ineligible = CreateAuthorizedService(EnabledSource(onDemand: false));
        var ineligibleResult = await ineligible.AcquireAsync(
            "source-1", "command-2", "operator check", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, disabledResult.Kind);
        Assert.AreEqual("Environmental acquisition is disabled.", disabledResult.Message);
        Assert.AreEqual(OperatorUiResultKind.Invalid, ineligibleResult.Kind);
        Assert.AreEqual("This source does not support on-demand acquisition.", ineligibleResult.Message);
    }

    [TestMethod]
    public async Task AcquireAsync_RejectsInvalidReasonAtServiceBoundary()
    {
        var service = CreateAuthorizedService(EnabledSource(onDemand: true));

        var result = await service.AcquireAsync(
            "source-1", "command-1", " invalid ", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual("The on-demand request is invalid.", result.Message);
    }

    private static CameraAgentEnvironmentalUiService CreateAuthorizedService(
        EnvironmentalSourceConfiguration source,
        bool enabled = true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-1")], "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());
        return CreateService(principal, authorization.Object, source, enabled);
    }

    private static CameraAgentEnvironmentalUiService CreateService(
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        EnvironmentalSourceConfiguration source,
        bool enabled = true)
    {
        var stateStore = new Mock<IEnvironmentalAcquisitionStateStore>(MockBehavior.Strict);
        var observationStore = new Mock<ILocalEnvironmentalObservationStore>(MockBehavior.Strict);
        return new CameraAgentEnvironmentalUiService(
            new FixedAuthenticationStateProvider(principal),
            authorization,
            stateStore.Object,
            observationStore.Object,
            null!,
            Options.Create(new CameraAgentHostOptions
            {
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = enabled,
                    Sources = [source]
                }
            }),
            null!,
            TimeProvider.System,
            NullLogger<CameraAgentEnvironmentalUiService>.Instance);
    }

    private static EnvironmentalSourceConfiguration EnabledSource(bool onDemand) => new()
    {
        Id = "source-1",
        Type = "VirtualEnvironment",
        Kind = EnvironmentalObservationKind.RelativeHumidity,
        Triggers = onDemand
            ? [EnvironmentalAcquisitionTrigger.OnDemand]
            : [EnvironmentalAcquisitionTrigger.Periodic]
    };

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }
}
