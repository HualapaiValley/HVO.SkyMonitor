using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public class CentralAuthenticationServiceTests
{
    [TestMethod]
    public async Task GetAccessTokenAsync_ReturnsTokenFromCache()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = "https://localhost:5001",
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret",
                Scopes = new[] { "api" }
            },
            TokenCacheDurationSeconds = 300
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        
        var tokenResponse = new
        {
            access_token = "test-token-12345",
            expires_in = 3600,
            token_type = "Bearer"
        };

        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new CentralAuthenticationService(options, mockLogger.Object, httpClientFactory.Object);

        // Act
        var token1 = await service.GetAccessTokenAsync();
        var token2 = await service.GetAccessTokenAsync(); // Should come from cache

        // Assert
        Assert.IsNotNull(token1);
        Assert.AreEqual(token1, token2);
        
        // Verify HTTP call was made only once (second call used cache)
        mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [TestMethod]
    public async Task ConfigureHttpClientAsync_AddsAuthorizationHeader()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = "https://localhost:5001",
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret",
                Scopes = new[] { "api" }
            }
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        
        var tokenResponse = new
        {
            access_token = "test-token-12345",
            expires_in = 3600,
            token_type = "Bearer"
        };

        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new CentralAuthenticationService(options, mockLogger.Object, httpClientFactory.Object);
        var targetClient = new HttpClient();

        // Act
        await service.ConfigureHttpClientAsync(targetClient);

        // Assert
        Assert.IsNotNull(targetClient.DefaultRequestHeaders.Authorization);
        Assert.AreEqual("Bearer", targetClient.DefaultRequestHeaders.Authorization.Scheme);
        Assert.IsNotNull(targetClient.DefaultRequestHeaders.Authorization.Parameter);
    }

    [TestMethod]
    public void GetApiKey_ReturnsConfiguredKey()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = "https://localhost:5001",
            Mode = AuthenticationMode.ApiKey,
            ApiKey = new ApiKeyOptions
            {
                Key = "test-api-key-12345"
            }
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();

        var service = new CentralAuthenticationService(options, mockLogger.Object, mockHttpClientFactory.Object);

        // Act
        var apiKey = service.GetApiKey();

        // Assert
        Assert.AreEqual("test-api-key-12345", apiKey);
    }
}
