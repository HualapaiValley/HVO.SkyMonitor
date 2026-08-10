using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Components.Pages.Devices;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Fleet.Contracts;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceBootstrapTests
{
    private static readonly DeviceIdentity Identity = new(
        "device-identity-1",
        "PAIR1234",
        new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

    [TestMethod]
    public void FirstTimePairingRendersCrossHostAccountsCopyActionsAndCurrentProgress()
    {
        using var context = CreateContext(secrets: null, captureAgentId: Identity.DeviceId);

        var cut = context.Render<DeviceBootstrap>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "CameraAgent local owner account", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "observatory owner account", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "https://logic.example/devices/register", StringComparison.Ordinal);
            Assert.AreEqual(2, cut.FindAll("button[aria-label^='Copy']").Count);
            StringAssert.Contains(cut.Markup, "Complete bootstrap first.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Aligned as device-identity-1", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void RegisteredDeviceRendersHeartbeatAndUploadReady()
    {
        var secrets = new DeviceSecrets(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Roof camera",
            "registration-token",
            "/api/heartbeat",
            "/api/upload",
            10,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            "device-key",
            null!);
        using var context = CreateContext(secrets, Identity.DeviceId);
        var heartbeat = context.Services.GetRequiredService<FleetHeartbeatState>();
        heartbeat.Update(
            new FleetStatusOutboxSnapshot(0, 0, 0, 0, 0, 0, 0, null, DateTimeOffset.UtcNow),
            FleetAvailability.Available,
            "ready",
            DateTimeOffset.UtcNow);
        context.Services.GetRequiredService<ArtifactOutboxState>().MarkInitialized();

        var cut = context.Render<DeviceBootstrap>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, ">Ready<", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Acknowledged", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Healthy; 0 pending, 0 retrying.", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void StaleHeartbeatDoesNotCompleteReadiness()
    {
        var secrets = CreateSecrets(DateTimeOffset.UtcNow);
        using var context = CreateContext(secrets, Identity.DeviceId);
        context.Services.GetRequiredService<FleetHeartbeatState>().Update(
            new FleetStatusOutboxSnapshot(0, 0, 0, 0, 0, 0, 0, null, DateTimeOffset.UtcNow),
            FleetAvailability.Available,
            "ready",
            secrets.IssuedAtUtc.AddSeconds(-1));
        context.Services.GetRequiredService<ArtifactOutboxState>().MarkInitialized();

        var cut = context.Render<DeviceBootstrap>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, ">Waiting<", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Waiting for the first acknowledgement.", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ExpiredEnvelopeShowsActionableRetryAndSuccessfulRetryCompletesBootstrap()
    {
        var workflow = new Mock<IDeviceBootstrapWorkflow>();
        workflow.SetupSequence(item => item.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gone", null, HttpStatusCode.Gone))
            .ReturnsAsync(CreateSecrets(DateTimeOffset.UtcNow));
        using var context = CreateContext(secrets: null, Identity.DeviceId, workflow.Object);
        var cut = context.Render<DeviceBootstrap>();

        cut.Find("textarea").Change("expired-envelope");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "It may be expired, already used, or issued for another device.", StringComparison.Ordinal));

        cut.Find("textarea").Change("replacement-envelope");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Markup, "Bootstrap completed.", StringComparison.Ordinal));
        workflow.Verify(item => item.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static BunitContext CreateContext(
        DeviceSecrets? secrets,
        string captureAgentId,
        IDeviceBootstrapWorkflow? workflow = null)
    {
        var context = new BunitContext();
        var identityStore = new Mock<IDeviceIdentityStore>();
        identityStore.Setup(store => store.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Identity);
        var secretStore = new Mock<IDeviceSecretStore>();
        secretStore.Setup(store => store.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);
        var hostOptions = Options.Create(new CameraAgentHostOptions
        {
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled },
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = true }
        });
        var configurationAccessor = new CameraAgentConfigurationAccessor();
        configurationAccessor.SetConfiguration(new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            AgentId: captureAgentId));
        workflow ??= Mock.Of<IDeviceBootstrapWorkflow>();

        context.Services.AddSingleton<IDeviceIdentityStore>(identityStore.Object);
        context.Services.AddSingleton<IDeviceSecretStore>(secretStore.Object);
        context.Services.AddSingleton(workflow);
        context.Services.AddSingleton<IOptions<CameraAgentHostOptions>>(hostOptions);
        context.Services.AddSingleton<ICameraAgentConfigurationAccessor>(configurationAccessor);
        context.Services.AddSingleton<FleetHeartbeatState>();
        context.Services.AddSingleton<ArtifactOutboxState>();
        context.Services.AddSingleton<IOptions<SkyMonitorClientOptions>>(
            Options.Create(new SkyMonitorClientOptions
            {
                BaseUrl = new Uri("http://logichost:8080/"),
                PublicBaseUrl = new Uri("https://logic.example/")
            }));
        context.Services.AddSingleton(NullLogger<DeviceBootstrap>.Instance);
        return context;
    }

    private static DeviceSecrets CreateSecrets(DateTimeOffset issuedAtUtc)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Roof camera",
            "registration-token",
            "/api/heartbeat",
            "/api/upload",
            10,
            issuedAtUtc,
            issuedAtUtc.AddHours(1),
            "device-key",
            null!);
}
