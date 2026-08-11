using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Integration")]
public sealed class CameraAgentEnvironmentalUiServiceIntegrationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AuthorizedFacadeExecutesRealCommandServiceAndPropagatesOwnerActor()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-1")], "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = "integration-only",
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                MaximumConcurrency = 1,
                SourceTimeoutMilliseconds = 5_000,
                Sources = [Source()]
            }
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new EnvironmentalSourceFactory(
            provider,
            [new EnvironmentalSourceRegistration(
                "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
        var publisher = new Mock<IEnvironmentalObservationPublisher>(MockBehavior.Strict);
        publisher.Setup(service => service.PublishAsync(
                It.IsAny<EnvironmentalObservationFactV1>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentalObservationPublishResult(
                EnvironmentalObservationPublishDisposition.Enqueued, null));
        using var coordinator = new EnvironmentalAcquisitionCoordinator(
            factory,
            publisher.Object,
            new FixedDeploymentLocationStore(Location()),
            options,
            TimeProvider.System);
        var commands = new Mock<IEnvironmentalOnDemandCommandStore>(MockBehavior.Strict);
        commands.Setup(store => store.ClaimOnDemandAsync(
                "integration-only", "command-1", It.IsAny<string>(), "source-1", "owner-1", "operator check",
                It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentalOnDemandCommandClaim(
                EnvironmentalOnDemandClaimDisposition.Claimed, "lease-1", Epoch, null));
        commands.Setup(store => store.CompleteOnDemandAsync(
                "integration-only", "command-1", "lease-1", It.IsAny<EnvironmentalAcquisitionReceipt>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var commandService = new EnvironmentalOnDemandAcquisitionService(
            coordinator, commands.Object, options, TimeProvider.System);
        var service = new CameraAgentEnvironmentalUiService(
            new FixedAuthenticationStateProvider(principal),
            authorization.Object,
            Mock.Of<IEnvironmentalAcquisitionStateStore>(),
            Mock.Of<ILocalEnvironmentalObservationStore>(),
            commandService,
            options,
            null!,
            TimeProvider.System,
            NullLogger<CameraAgentEnvironmentalUiService>.Instance);

        var result = await service.AcquireAsync(
            "source-1", "command-1", "operator check", CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, result.Value.Receipt.Disposition);
        Assert.IsFalse(result.Value.Replayed);
        commands.VerifyAll();
        publisher.VerifyAll();
    }

    private static EnvironmentalSourceConfiguration Source() => new()
    {
        Id = "source-1",
        Type = "VirtualEnvironment",
        Kind = EnvironmentalObservationKind.AirTemperature,
        Triggers = [EnvironmentalAcquisitionTrigger.OnDemand],
        ScheduleEpochUtc = Epoch,
        ValidForSeconds = 120,
        StaleAfterSeconds = 45,
        Options = CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(
            306, Epoch, 12.5, null, 0.1, 0.2))
    };

    private static DeploymentLocationSnapshot Location()
        => DeploymentLocationSnapshot.Create(
            "location", 1, "test", null, Epoch.AddDays(-1), null,
            35.5599378, -113.9119818, 520, "America/Phoenix");

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedDeploymentLocationStore(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; } = active;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => Active!;
    }
}
