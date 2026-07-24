using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Diagnostics;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class DeploymentLocationReconciliationWorkerTests
{
    [TestMethod]
    public async Task ReconcileOnceAsync_SubmitsProtectedVersionAndPersistsAcknowledgment()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var deployment = DeploymentLocationSnapshot.Create(
            "worker-location", 2, "gps-receiver", 3, DateTimeOffset.UnixEpoch, null,
            35.347, -113.878, 520, "America/Phoenix");
        var observatory = ObservatoryLocationSnapshot.Create(
            Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, 35.347, -113.878, 520,
            "America/Phoenix", 1000);
        var acknowledgment = new DeploymentLocationAcknowledgment(
            observatory,
            deployment,
            DeploymentLocationSourceKind.Gps,
            DeploymentLocationResolutionStatus.Acknowledged,
            "within-observatory-boundary",
            now,
            now);
        var secrets = new DeviceSecrets(
            Guid.NewGuid(), observatory.ObservatoryId, "Camera", "token", "/heartbeat", "/upload", 60,
            now.AddHours(-1), now.AddHours(1), "device-key", new CentralIdentityOptions());
        DeviceSecrets? saved = null;
        var secretStore = new Mock<IDeviceSecretStore>();
        secretStore.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(secrets);
        secretStore.Setup(item => item.SaveAsync(It.IsAny<DeviceSecrets>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceSecrets, CancellationToken>((value, _) => saved = value)
            .Returns(Task.CompletedTask);
        var identityStore = new Mock<IDeviceIdentityStore>();
        identityStore.Setup(item => item.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceIdentity("camera-worker", "code", now));
        var locationStore = new Mock<IDeploymentLocationStore>();
        locationStore.SetupGet(item => item.Active).Returns(deployment);
        locationStore.Setup(item => item.ResolveSourceKind(deployment))
            .Returns(DeploymentLocationSourceKind.Gps);
        var handler = new CapturingHandler(acknowledgment);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://logic.example/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var state = new DeploymentLocationReconciliationState();
        using var telemetry = new DeploymentLocationTelemetry();
        var worker = new DeploymentLocationReconciliationWorker(
            identityStore.Object,
            secretStore.Object,
            locationStore.Object,
            factory.Object,
            Options.Create(new CameraAgentHostOptions
            {
                DeploymentLocation = new DeploymentLocationOptions
                {
                    SourceKind = DeploymentLocationSourceKind.Gps
                }
            }),
            state,
            telemetry,
            new FixedTimeProvider(now),
            NullLogger<DeploymentLocationReconciliationWorker>.Instance);

        await worker.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new Uri("https://logic.example/api/device/deployment-location"), handler.RequestUri);
        using var document = JsonDocument.Parse(handler.RequestJson!);
        Assert.AreEqual("Gps", document.RootElement.GetProperty("sourceKind").GetString());
        Assert.AreEqual(
            deployment.CanonicalSha256,
            document.RootElement.GetProperty("deploymentLocation").GetProperty("canonicalSha256").GetString());
        Assert.AreEqual(acknowledgment, saved!.DeploymentLocationAcknowledgment);
        Assert.AreEqual(DeploymentLocationResolutionStatus.Acknowledged, state.Status);
        Assert.AreEqual("acknowledged", state.Outcome);
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_StagesDifferingAcknowledgmentWithoutChangingActiveLocation()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var active = DeploymentLocationSnapshot.Create(
            "worker-location", 1, "local", 3, DateTimeOffset.UnixEpoch, null,
            35.347, -113.878, 520, "America/Phoenix");
        var approved = DeploymentLocationSnapshot.Create(
            active.LocationId, 2, "approved", 2, now, null,
            35.348, -113.878, 521, "America/Phoenix");
        var observatory = ObservatoryLocationSnapshot.Create(
            Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, 35.348, -113.878, 521,
            "America/Phoenix", 1000);
        var acknowledgment = new DeploymentLocationAcknowledgment(
            observatory, approved, DeploymentLocationSourceKind.Gps,
            DeploymentLocationResolutionStatus.Acknowledged, "owner-approved", now, now);
        var secrets = new DeviceSecrets(
            Guid.NewGuid(), observatory.ObservatoryId, "Camera", "token", "/heartbeat", "/upload", 60,
            now.AddHours(-1), now.AddHours(1), "device-key", new CentralIdentityOptions());
        var secretStore = new Mock<IDeviceSecretStore>();
        secretStore.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(secrets);
        var identityStore = new Mock<IDeviceIdentityStore>();
        identityStore.Setup(item => item.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceIdentity("camera-worker", "code", now));
        var locationStore = new Mock<IDeploymentLocationStore>();
        locationStore.SetupGet(item => item.Active).Returns(active);
        locationStore.SetupGet(item => item.Candidate).Returns(approved);
        locationStore.Setup(item => item.ResolveSourceKind(approved))
            .Returns(DeploymentLocationSourceKind.Gps);
        DeploymentLocationSnapshot? staged = null;
        locationStore.Setup(item => item.StageAsync(It.IsAny<DeploymentLocationSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentLocationSnapshot, CancellationToken>((value, _) => staged = value)
            .Returns(ValueTask.CompletedTask);
        using var client = new HttpClient(new CapturingHandler(acknowledgment))
        {
            BaseAddress = new Uri("https://logic.example/")
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var options = Options.Create(new CameraAgentHostOptions
        {
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled },
            DeploymentLocation = new DeploymentLocationOptions { SourceKind = DeploymentLocationSourceKind.Gps }
        });
        var state = new DeploymentLocationReconciliationState();
        var stopped = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DeploymentLocationTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(activityListener);
        using var telemetry = new DeploymentLocationTelemetry();
        var worker = new DeploymentLocationReconciliationWorker(
            identityStore.Object, secretStore.Object, locationStore.Object, factory.Object, options,
            state, telemetry, new FixedTimeProvider(now), NullLogger<DeploymentLocationReconciliationWorker>.Instance);

        await worker.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(approved, staged);
        Assert.AreEqual(active, locationStore.Object.Active);
        Assert.AreEqual("restart-required", state.Outcome);
        var activity = stopped.Single(item => item.OperationName == "deployment-location.reconcile"
            && item.GetTagItem("deployment.outcome")?.ToString() == "restart-required");
        var tags = activity.TagObjects.ToDictionary(item => item.Key, item => item.Value?.ToString());
        Assert.AreEqual("restart-required", tags["deployment.outcome"]);
        Assert.AreEqual("Gps", tags["deployment.source_kind"]);
        Assert.AreEqual("successor", tags["deployment.version_relation"]);
        Assert.IsFalse(string.Join(';', tags.Values).Contains("35.348", StringComparison.Ordinal));
        var health = await new DeploymentLocationReconciliationHealthCheck(state, options)
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Degraded, health.Status);
    }

    [TestMethod]
    public async Task HealthCheck_EnabledBeforeFirstAttemptIsDegraded()
    {
        var options = Options.Create(new CameraAgentHostOptions
        {
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
        });
        var result = await new DeploymentLocationReconciliationHealthCheck(
                new DeploymentLocationReconciliationState(), options)
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Degraded, result.Status);
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_MalformedSuccessIsInvalidAndDoesNotPersist()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var deployment = DeploymentLocationSnapshot.Create(
            "worker-location", 1, "gps", 2, DateTimeOffset.UnixEpoch, null,
            35.347, -113.878, 520, "America/Phoenix");
        var secretStore = new Mock<IDeviceSecretStore>();
        secretStore.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DeviceSecrets(
            Guid.NewGuid(), Guid.NewGuid(), "Camera", "token", "/heartbeat", "/upload", 60,
            now.AddHours(-1), now.AddHours(1), "key", new CentralIdentityOptions()));
        var identityStore = new Mock<IDeviceIdentityStore>();
        identityStore.Setup(item => item.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceIdentity("camera-worker", "code", now));
        var locationStore = new Mock<IDeploymentLocationStore>();
        locationStore.SetupGet(item => item.Active).Returns(deployment);
        locationStore.Setup(item => item.ResolveSourceKind(deployment)).Returns(DeploymentLocationSourceKind.Gps);
        using var client = new HttpClient(new RawResponseHandler(new StringContent("{not-json")))
        {
            BaseAddress = new Uri("https://logic.example/")
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var state = new DeploymentLocationReconciliationState();
        using var telemetry = new DeploymentLocationTelemetry();
        var worker = new DeploymentLocationReconciliationWorker(
            identityStore.Object,
            secretStore.Object,
            locationStore.Object,
            factory.Object,
            Options.Create(new CameraAgentHostOptions()),
            state,
            telemetry,
            new FixedTimeProvider(now),
            NullLogger<DeploymentLocationReconciliationWorker>.Instance);

        await worker.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("invalid-acknowledgment", state.Outcome);
        Assert.IsNull(state.LastSuccessUtc);
        secretStore.Verify(
            item => item.SaveAsync(It.IsAny<DeviceSecrets>(), It.IsAny<CancellationToken>()),
            Times.Never);
        locationStore.Verify(
            item => item.StageAsync(It.IsAny<DeploymentLocationSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    [DataRow("foreign-observatory")]
    [DataRow("wrong-deployment")]
    public async Task ReconcileOnceAsync_UnrelatedAcknowledgmentIsInvalid(string scenario)
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var deployment = DeploymentLocationSnapshot.Create(
            "worker-location", 1, "gps", 2, DateTimeOffset.UnixEpoch, null,
            35.347, -113.878, 520, "America/Phoenix");
        var observatoryId = Guid.NewGuid();
        var acknowledgmentDeployment = scenario == "wrong-deployment"
            ? DeploymentLocationSnapshot.Create(
                deployment.LocationId, 2, deployment.Source, deployment.HorizontalAccuracyMeters,
                deployment.EffectiveFromUtc.AddSeconds(1), null,
                deployment.LatitudeDegrees, deployment.LongitudeDegrees, deployment.ElevationMeters,
                deployment.TimeZoneId)
            : deployment;
        var acknowledgment = new DeploymentLocationAcknowledgment(
            ObservatoryLocationSnapshot.Create(
                scenario == "foreign-observatory" ? Guid.NewGuid() : observatoryId,
                1, DateTimeOffset.UnixEpoch, 35.347, -113.878, 520, "America/Phoenix", 1000),
            acknowledgmentDeployment,
            DeploymentLocationSourceKind.Gps,
            DeploymentLocationResolutionStatus.Pending,
            "owner-review",
            now,
            null);
        var secretStore = new Mock<IDeviceSecretStore>();
        secretStore.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DeviceSecrets(
            Guid.NewGuid(), observatoryId, "Camera", "token", "/heartbeat", "/upload", 60,
            now.AddHours(-1), now.AddHours(1), "key", new CentralIdentityOptions()));
        var identityStore = new Mock<IDeviceIdentityStore>();
        identityStore.Setup(item => item.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceIdentity("camera-worker", "code", now));
        var locationStore = new Mock<IDeploymentLocationStore>();
        locationStore.SetupGet(item => item.Active).Returns(deployment);
        locationStore.Setup(item => item.ResolveSourceKind(deployment)).Returns(DeploymentLocationSourceKind.Gps);
        using var client = new HttpClient(new CapturingHandler(acknowledgment))
        {
            BaseAddress = new Uri("https://logic.example/")
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var state = new DeploymentLocationReconciliationState();
        using var telemetry = new DeploymentLocationTelemetry();
        var worker = new DeploymentLocationReconciliationWorker(
            identityStore.Object, secretStore.Object, locationStore.Object, factory.Object,
            Options.Create(new CameraAgentHostOptions()), state, telemetry, new FixedTimeProvider(now),
            NullLogger<DeploymentLocationReconciliationWorker>.Instance);

        await worker.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("invalid-acknowledgment", state.Outcome);
        Assert.IsNull(state.LastSuccessUtc);
        secretStore.Verify(
            item => item.SaveAsync(It.IsAny<DeviceSecrets>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class CapturingHandler(DeploymentLocationAcknowledgment acknowledgment) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(acknowledgment)
            };
        }
    }

    private sealed class RawResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
