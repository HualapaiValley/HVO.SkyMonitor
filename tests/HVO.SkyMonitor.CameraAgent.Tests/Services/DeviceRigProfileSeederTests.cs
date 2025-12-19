using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
public sealed class DeviceRigProfileSeederTests
{
    [TestMethod]
    public async Task SeedAsync_WhenEndpointMissing_DoesNotSendRequest()
    {
        var mockHttpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var mockLoader = new Mock<ICameraAgentConfigurationLoader>(MockBehavior.Strict);

        var seeder = new DeviceRigProfileSeeder(
            mockHttpClientFactory.Object,
            mockLoader.Object,
            NullLogger<DeviceRigProfileSeeder>.Instance);

        var identity = new DeviceIdentity("device-1", "CODE", DateTimeOffset.UtcNow);
        var secrets = CreateSecrets(rigProfileEndpoint: "");

        await seeder.SeedAsync(identity, secrets, CancellationToken.None);
    }

    [TestMethod]
    public async Task SeedAsync_PostsRigProfile_WithSkipAuthHeader()
    {
        HttpRequestMessage? captured = null;

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new { ok = true })
            });

        using var httpClient = new HttpClient(mockHttpMessageHandler.Object, disposeHandler: false)
        {
            BaseAddress = new Uri("https://logichost.example/")
        };

        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var rig = new CameraRigConfig(
            Sensor: new SensorProfile("Test Sensor", 1920, 1080, 3.45, SensorColorMode.Color, CameraPixelFormat.Rgb24),
            Optics: new OpticsProfile("Rectilinear", 3.6, 120.0, 0.0),
            Orientation: new RigOrientation(45.0, 180.0, 0.0),
            Pipeline: new PipelineExposureProfile(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), 1.0, 2.0),
            ControlPolicy: null);

        var config = new CameraModuleConfig(
            Observatory: new ObservatoryLocation(0, 0, 0, "UTC"),
            Module: new CameraModuleDescriptor("RandomImage"),
            Rig: rig);

        var mockLoader = new Mock<ICameraAgentConfigurationLoader>();
        mockLoader
            .Setup(loader => loader.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var seeder = new DeviceRigProfileSeeder(
            mockHttpClientFactory.Object,
            mockLoader.Object,
            NullLogger<DeviceRigProfileSeeder>.Instance);

        var identity = new DeviceIdentity("device-1", "CODE", DateTimeOffset.UtcNow);
        var secrets = CreateSecrets(rigProfileEndpoint: "/api/device/profile/rig");

        await seeder.SeedAsync(identity, secrets, CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.AreEqual(new Uri("https://logichost.example/api/device/profile/rig"), captured!.RequestUri);
        Assert.IsTrue(captured.Headers.Contains(CentralIdentityDelegatingHandler.SkipAuthHeader));

        var json = await captured.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual("device-1", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.AreEqual(secrets.DeviceKey, doc.RootElement.GetProperty("deviceKey").GetString());

        var rigJson = doc.RootElement.GetProperty("rigConfigJson").GetString();
        Assert.IsNotNull(rigJson);
        Assert.IsTrue(rigJson!.Contains("\"sensor\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(rigJson.Contains("Test Sensor", StringComparison.Ordinal));
    }

    private static DeviceSecrets CreateSecrets(string rigProfileEndpoint)
        => new(
            DevicePublicId: Guid.NewGuid(),
            ObservatoryId: Guid.NewGuid(),
            FriendlyName: "Test",
            RegistrationToken: "token",
            HeartbeatEndpoint: "/api/device/heartbeat",
            UploadEndpoint: "/api/device/upload",
            HeartbeatIntervalSeconds: 60,
            IssuedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            DeviceKey: "device-key",
            CentralIdentity: new CentralIdentityOptions { ServiceUrl = new Uri("https://identity.example") },
            RigProfileEndpoint: rigProfileEndpoint);
}
