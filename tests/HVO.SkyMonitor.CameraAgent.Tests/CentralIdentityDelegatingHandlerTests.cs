using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "MSTest test classes must be public.")]
[TestClass]
public class CentralIdentityDelegatingHandlerTests
{
    [TestMethod]
    public async Task SendAsyncAttachesBearerTokenWhenMissing()
    {
        var auth = new FakeAuthenticationService("token-123", null);
        var recording = new RecordingHandler();
        using var handler = CreateHandler(auth, recording);
        using var client = new HttpClient(handler, disposeHandler: false);

        using var response = await client.GetAsync(new Uri("https://example.com/api/status", UriKind.Absolute)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Bearer", recording.LastRequest?.Headers.Authorization?.Scheme);
        Assert.AreEqual("token-123", recording.LastRequest?.Headers.Authorization?.Parameter);
        Assert.AreEqual(1, auth.AccessTokenCalls);
        Assert.AreEqual(0, auth.ApiKeyCalls);
    }

    [TestMethod]
    public async Task SendAsyncAttachesApiKeyWhenTokenUnavailable()
    {
        var auth = new FakeAuthenticationService(null, "smk_test");
        var recording = new RecordingHandler();
        using var handler = CreateHandler(auth, recording);
        using var client = new HttpClient(handler, disposeHandler: false);

        using var response = await client.GetAsync(new Uri("https://example.com/api/status", UriKind.Absolute)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(recording.LastRequest?.Headers.Contains("X-API-Key"));
        var apiKey = recording.LastRequest?.Headers.GetValues("X-API-Key").Single();
        Assert.AreEqual("smk_test", apiKey);
        Assert.AreEqual(1, auth.AccessTokenCalls);
        Assert.AreEqual(1, auth.ApiKeyCalls);
    }

    [TestMethod]
    public async Task SendAsyncDoesNotOverrideExistingAuthorization()
    {
        var auth = new FakeAuthenticationService("token-ignored", "api-ignored");
        var recording = new RecordingHandler();
        using var handler = CreateHandler(auth, recording);
        using var client = new HttpClient(handler, disposeHandler: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "existing-token");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("existing-token", recording.LastRequest?.Headers.Authorization?.Parameter);
        Assert.AreEqual(0, auth.AccessTokenCalls);
        Assert.AreEqual(0, auth.ApiKeyCalls);
    }

    private static CentralIdentityDelegatingHandler CreateHandler(
        ICentralAuthenticationService authenticationService,
        RecordingHandler recording)
    {
        var handler = new CentralIdentityDelegatingHandler(authenticationService, NullLogger<CentralIdentityDelegatingHandler>.Instance)
        {
            InnerHandler = recording
        };

        return handler;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient callers dispose the returned HttpResponseMessage instances.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeAuthenticationService(string? accessToken, string? apiKey) : ICentralAuthenticationService
    {
        public int AccessTokenCalls { get; private set; }
        public int ApiKeyCalls { get; private set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            AccessTokenCalls++;
            return Task.FromResult(accessToken);
        }

        public string? GetApiKey()
        {
            ApiKeyCalls++;
            return apiKey;
        }

        public Task ConfigureHttpClientAsync(HttpClient client, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
