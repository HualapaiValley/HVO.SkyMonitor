using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Additional tests for CentralAuthenticationService error handling and edge cases.
/// </summary>
[TestClass]
public class CentralAuthenticationServiceErrorTests
{
    [TestMethod]
    public async Task GetAccessTokenAsync_WhenTokenRequestFails_ThrowsException()
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
        
        // Simulate 401 Unauthorized response
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Content = new StringContent("{\"error\":\"invalid_client\"}")
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var timeProvider = TimeProvider.System;
        var service = new CentralAuthenticationService(options, httpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act & Assert
        HttpRequestException? caughtException = null;
        try
        {
            await service.GetAccessTokenAsync();
        }
        catch (HttpRequestException ex)
        {
            caughtException = ex;
        }
        
        Assert.IsNotNull(caughtException, "Expected HttpRequestException to be thrown");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InApiKeyMode_ReturnsNull()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = "https://localhost:5001",
            Mode = AuthenticationMode.ApiKey,
            ApiKey = new ApiKeyOptions
            {
                Key = "smk_test_api_key"
            }
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var timeProvider = TimeProvider.System;

        var service = new CentralAuthenticationService(options, mockHttpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act
        var token = await service.GetAccessTokenAsync();

        // Assert
        Assert.IsNull(token);
    }

    [TestMethod]
    public async Task ConfigureHttpClientAsync_InApiKeyMode_AddsApiKeyHeader()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = "https://localhost:5001",
            Mode = AuthenticationMode.ApiKey,
            ApiKey = new ApiKeyOptions
            {
                Key = "smk_test_api_key_12345"
            }
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var timeProvider = TimeProvider.System;

        var service = new CentralAuthenticationService(options, mockHttpClientFactory.Object, mockLogger.Object, timeProvider);
        var targetClient = new HttpClient();

        // Act
        await service.ConfigureHttpClientAsync(targetClient);

        // Assert
        Assert.IsTrue(targetClient.DefaultRequestHeaders.Contains("X-API-Key"));
        var apiKeyValues = targetClient.DefaultRequestHeaders.GetValues("X-API-Key");
        Assert.AreEqual("smk_test_api_key_12345", apiKeyValues.First());
    }

    [TestMethod]
    public void GetApiKey_InClientCredentialsMode_ReturnsNull()
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
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var timeProvider = TimeProvider.System;

        var service = new CentralAuthenticationService(options, mockHttpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act
        var apiKey = service.GetApiKey();

        // Assert
        Assert.IsNull(apiKey);
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_WithTokenRefreshWindow_RefreshesToken()
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
            TokenCacheDurationSeconds = 300,
            TokenRefreshWindowSeconds = 60
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        
        var tokenResponse = new
        {
            access_token = "test-token-12345",
            expires_in = 50, // Token expires in 50 seconds - within refresh window
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

        var timeProvider = TimeProvider.System;
        var service = new CentralAuthenticationService(options, httpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act
        var token = await service.GetAccessTokenAsync();

        // Assert
        Assert.IsNotNull(token);
        Assert.AreEqual("test-token-12345", token);
    }
}
