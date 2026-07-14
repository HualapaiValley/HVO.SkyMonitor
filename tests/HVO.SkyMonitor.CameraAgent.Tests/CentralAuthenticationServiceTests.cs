using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "MSTest test classes must be public.")]
[TestClass]
[TestCategory("Unit")]
public class CentralAuthenticationServiceTests
{
    [TestMethod]
    public async Task GetAccessTokenAsyncReturnsTokenFromCache()
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
            },
            TokenCacheDurationSeconds = 300
        };
        identityOptions.ClientCredentials!.Scopes.Clear();
        identityOptions.ClientCredentials!.Scopes.Add("api");
        var options = Options.Create(identityOptions);

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
            .ReturnsAsync(() => CreateJsonResponse(tokenResponse));

        using var httpClient = new HttpClient(mockHttpMessageHandler.Object, disposeHandler: false);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var timeProvider = TimeProvider.System;

        var service = new CentralAuthenticationService(options, httpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act
        var token1 = await service.GetAccessTokenAsync().ConfigureAwait(false);
        var token2 = await service.GetAccessTokenAsync().ConfigureAwait(false); // Should come from cache

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
    public async Task ConfigureHttpClientAsyncAddsAuthorizationHeader()
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
            .ReturnsAsync(() => CreateJsonResponse(tokenResponse));

        using var httpClient = new HttpClient(mockHttpMessageHandler.Object, disposeHandler: false);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var timeProvider = TimeProvider.System;

        var service = new CentralAuthenticationService(options, httpClientFactory.Object, mockLogger.Object, timeProvider);
        using var targetClient = new HttpClient();

        // Act
        await service.ConfigureHttpClientAsync(targetClient).ConfigureAwait(false);

        // Assert
        Assert.IsNotNull(targetClient.DefaultRequestHeaders.Authorization);
        Assert.AreEqual("Bearer", targetClient.DefaultRequestHeaders.Authorization.Scheme);
        Assert.IsNotNull(targetClient.DefaultRequestHeaders.Authorization.Parameter);
    }

    [TestMethod]
    public void GetApiKeyReturnsConfiguredKey()
    {
        // Arrange
        var options = Options.Create(new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:5001", UriKind.Absolute),
            Mode = AuthenticationMode.ApiKey,
            ApiKey = new ApiKeyOptions
            {
                Key = "test-api-key-12345"
            }
        });

        var mockLogger = new Mock<ILogger<CentralAuthenticationService>>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var timeProvider = TimeProvider.System;

        var service = new CentralAuthenticationService(options, mockHttpClientFactory.Object, mockLogger.Object, timeProvider);

        // Act
        var apiKey = service.GetApiKey();

        // Assert
        Assert.AreEqual("test-api-key-12345", apiKey);
    }
    private static HttpResponseMessage CreateJsonResponse(object payload)
    {
        return new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(JsonSerializer.Serialize(payload))
        };
    }
}
