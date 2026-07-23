using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Services.Models;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceBootstrapWorkflowTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task BootstrapAsync_WhenSuccessful_PersistsSecrets_AndSeedsRigProfile()
    {
        var deviceId = "device-123";
        var identity = new DeviceIdentity(deviceId, "ABCDEF1234", DateTimeOffset.UtcNow);

        var secretsPayload = new DeviceBootstrapSecretsPayload(
            DevicePublicId: Guid.NewGuid(),
            ObservatoryId: Guid.NewGuid(),
            FriendlyName: "Rig A",
            RegistrationToken: "reg-token",
            HeartbeatEndpoint: "/api/device/heartbeat",
            UploadEndpoint: "/api/device/upload",
            HeartbeatIntervalSeconds: 60,
            IssuedAtUtc: new DateTimeOffset(2025, 12, 18, 0, 0, 0, TimeSpan.Zero),
            ExpiresAtUtc: new DateTimeOffset(2025, 12, 18, 1, 0, 0, TimeSpan.Zero),
            CentralIdentity: new CentralIdentityOptions
            {
                ServiceUrl = new Uri("https://identity.example", UriKind.Absolute)
            },
            RigProfileEndpoint: "/api/device/profile/rig");

        var deviceKey = CreateDeviceKeyBase64();
        var responseDto = CreateBootstrapResponse(deviceKey, secretsPayload);

        HttpRequestMessage? capturedRequest = null;

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(responseDto)
            });

        var authenticationService = new Mock<ICentralAuthenticationService>(MockBehavior.Strict);
        var authenticationHandler = new CentralIdentityDelegatingHandler(
            authenticationService.Object,
            NullLogger<CentralIdentityDelegatingHandler>.Instance)
        {
            InnerHandler = mockHttpMessageHandler.Object
        };
        using var httpClient = new HttpClient(authenticationHandler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://logichost.example/")
        };

        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var mockIdentityStore = new Mock<IDeviceIdentityStore>();
        mockIdentityStore
            .Setup(store => store.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(identity);

        DeviceSecrets? savedSecrets = null;
        var mockSecretStore = new Mock<IDeviceSecretStore>();
        mockSecretStore
            .Setup(store => store.SaveAsync(It.IsAny<DeviceSecrets>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceSecrets, CancellationToken>((secrets, _) => savedSecrets = secrets)
            .Returns(Task.CompletedTask);

        var mockSeeder = new Mock<IDeviceRigProfileSeeder>();
        mockSeeder
            .Setup(seeder => seeder.SeedAsync(It.IsAny<DeviceIdentity>(), It.IsAny<DeviceSecrets>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workflow = new DeviceBootstrapWorkflow(
            mockHttpClientFactory.Object,
            mockIdentityStore.Object,
            mockSecretStore.Object,
            mockSeeder.Object,
            Options.Create(new CameraAgentHostOptions()),
            NullLogger<DeviceBootstrapWorkflow>.Instance);

        var result = await workflow.BootstrapAsync(" envelope ", CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual(new Uri("https://logichost.example/api/device/bootstrap"), capturedRequest!.RequestUri);
        Assert.IsFalse(capturedRequest.Headers.Contains(CentralIdentityDelegatingHandler.SkipAuthHeader));
        Assert.IsNull(capturedRequest.Headers.Authorization);
        Assert.IsNotNull(savedSecrets);

        Assert.AreEqual(secretsPayload.DevicePublicId, result.DevicePublicId);
        Assert.AreEqual(secretsPayload.RigProfileEndpoint, result.RigProfileEndpoint);
        Assert.AreEqual(deviceKey, result.DeviceKey);

        mockSeeder.Verify(
            seeder => seeder.SeedAsync(identity, It.Is<DeviceSecrets>(s => s.RigProfileEndpoint == secretsPayload.RigProfileEndpoint), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task BootstrapAsync_WhenRejected_DoesNotLogResponseBody()
    {
        const string sensitiveDetail = "sensitive-bootstrap-response";
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(sensitiveDetail)
            });

        using var httpClient = new HttpClient(mockHttpMessageHandler.Object, disposeHandler: false)
        {
            BaseAddress = new Uri("https://logichost.example/")
        };
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(httpClient);
        var mockIdentityStore = new Mock<IDeviceIdentityStore>();
        mockIdentityStore
            .Setup(store => store.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceIdentity("device-123", "SELFATTEST", DateTimeOffset.UtcNow));
        var logger = new Mock<ILogger<DeviceBootstrapWorkflow>>();

        var workflow = new DeviceBootstrapWorkflow(
            mockHttpClientFactory.Object,
            mockIdentityStore.Object,
            Mock.Of<IDeviceSecretStore>(),
            Mock.Of<IDeviceRigProfileSeeder>(),
            Options.Create(new CameraAgentHostOptions()),
            logger.Object);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => workflow.BootstrapAsync("envelope", CancellationToken.None)).ConfigureAwait(false);

        Assert.IsFalse(logger.Invocations.Any(invocation =>
            invocation.Arguments.Any(argument =>
                argument?.ToString()?.Contains(sensitiveDetail, StringComparison.Ordinal) == true)));
    }

    [TestMethod]
    public async Task BootstrapAsync_WhenCentralIntegrationIsDisabled_DoesNotResolveCentralDependencies()
    {
        var workflow = new DeviceBootstrapWorkflow(
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            Mock.Of<IDeviceIdentityStore>(MockBehavior.Strict),
            Mock.Of<IDeviceSecretStore>(MockBehavior.Strict),
            Mock.Of<IDeviceRigProfileSeeder>(MockBehavior.Strict),
            Options.Create(new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            }),
            NullLogger<DeviceBootstrapWorkflow>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.BootstrapAsync("envelope", CancellationToken.None)).ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateDeviceKeyBase64()
    {
        Span<byte> key = stackalloc byte[32];
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }

    private static DeviceBootstrapResponseDto CreateBootstrapResponse(string deviceKeyBase64, DeviceBootstrapSecretsPayload secrets)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, SerializerOptions);

        var keyBytes = Convert.FromBase64String(deviceKeyBase64);
        Span<byte> nonce = stackalloc byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(keyBytes, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var payload = new DeviceBootstrapPayloadDto(
            Ciphertext: Convert.ToBase64String(ciphertext),
            Nonce: Convert.ToBase64String(nonce),
            Tag: Convert.ToBase64String(tag),
            Algorithm: "AES-256-GCM");

        return new DeviceBootstrapResponseDto(
            RegistrationId: Guid.NewGuid(),
            DevicePublicId: secrets.DevicePublicId,
            EnvelopeVersion: "v1",
            DeviceKey: deviceKeyBase64,
            Payload: payload);
    }
}
