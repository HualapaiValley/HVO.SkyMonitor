using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Additional tests for CentralAuthenticationService error handling and edge cases.
/// </summary>
[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "MSTest test classes must be public.")]
[TestClass]
public class CentralAuthenticationServiceErrorTests
{
    [TestMethod]
    public async Task GetAccessTokenAsyncWhenTokenRequestFailsThrowsException()
    {
        // Arrange
        var identityOptions = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:5001", UriKind.Absolute),
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret"
            }
        };
        identityOptions.ClientCredentials!.Scopes.Clear();
        identityOptions.ClientCredentials!.Scopes.Add("api");
        var options = Options.Create(identityOptions);

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

        // Simulate 401 Unauthorized response
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "invalid_client" }));

        using var httpClient = new HttpClient(mockHttpMessageHandler.Object, disposeHandler: false);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var timeProvider = TimeProvider.System;
        var service = new CentralAuthenticationService(options, httpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act & Assert
        HttpRequestException? caughtException = null;
        try
        {
            await service.GetAccessTokenAsync().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            caughtException = ex;
        }

        Assert.IsNotNull(caughtException, "Expected HttpRequestException to be thrown");
    }

    [TestMethod]
    public async Task GetAccessTokenAsyncInApiKeyModeReturnsNull()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:5001", UriKind.Absolute),
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
        var token = await service.GetAccessTokenAsync().ConfigureAwait(false);

        // Assert
        Assert.IsNull(token);
    }

    [TestMethod]
    public async Task ConfigureHttpClientAsyncInApiKeyModeAddsApiKeyHeader()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:5001", UriKind.Absolute),
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
        using var targetClient = new HttpClient();

        // Act
        await service.ConfigureHttpClientAsync(targetClient).ConfigureAwait(false);

        // Assert
        Assert.IsTrue(targetClient.DefaultRequestHeaders.Contains("X-API-Key"));
        var apiKeyValues = targetClient.DefaultRequestHeaders.GetValues("X-API-Key");
        Assert.AreEqual("smk_test_api_key_12345", apiKeyValues.First());
    }

    [TestMethod]
    public void GetApiKeyInClientCredentialsModeReturnsNull()
    {
        // Arrange
        var identityOptionsCredentials = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:5001", UriKind.Absolute),
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret"
            }
        };
        identityOptionsCredentials.ClientCredentials!.Scopes.Clear();
        identityOptionsCredentials.ClientCredentials!.Scopes.Add("api");
        var options = Options.Create(identityOptionsCredentials);

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
    public async Task GetAccessTokenAsyncWithTokenRefreshWindowRefreshesToken()
    {
        // Arrange
        var identityOptionsRefreshing = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:5001", UriKind.Absolute),
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret"
            },
            TokenCacheDurationSeconds = 300,
            TokenRefreshWindowSeconds = 60
        };
        identityOptionsRefreshing.ClientCredentials!.Scopes.Clear();
        identityOptionsRefreshing.ClientCredentials!.Scopes.Add("api");
        var options = Options.Create(identityOptionsRefreshing);

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
            .ReturnsAsync(() => CreateJsonResponse(HttpStatusCode.OK, tokenResponse));

        using var httpClient = new HttpClient(mockHttpMessageHandler.Object, disposeHandler: false);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var timeProvider = TimeProvider.System;
        var service = new CentralAuthenticationService(options, httpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act
        var token = await service.GetAccessTokenAsync().ConfigureAwait(false);

        // Assert
        Assert.IsNotNull(token);
        Assert.AreEqual("test-token-12345", token);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload))
        };
    }
}
