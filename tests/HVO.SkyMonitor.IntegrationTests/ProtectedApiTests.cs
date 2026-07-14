using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class ProtectedApiTests
{
    private HttpClient? _client;

    [TestInitialize]
    public void SetUp()
    {
        _client = AssemblyHooks.Fixture.Factory.CreateClient();
    }

    [TestCleanup]
    public void TearDown()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task ProtectedStatusWithBearerTokenSucceedsAsync()
    {
        var scope = string.Join(' ', TestClients.WebUI.Scopes);
        var token = await HttpHelpers.GetPasswordTokenAsync(
            _client!,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            scope).ConfigureAwait(false);

        var authedClient = HttpHelpers.WithBearerToken(_client!, token.AccessToken);
        var response = await authedClient.GetAsync(new Uri("/api/v1.0/status/protected", UriKind.Relative)).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task ProtectedStatusMissingTokenReturnsUnauthorizedAsync()
    {
        var response = await _client!.GetAsync(new Uri("/api/v1.0/status/protected", UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task DetailedStatusWithValidApiKeySucceedsAsync()
    {
        var authedClient = HttpHelpers.WithApiKey(_client!, TestApiKeys.InternalService.Key);
        var response = await authedClient.GetAsync(new Uri("/api/v1.0/status/detailed", UriKind.Relative)).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task DetailedStatusWithInvalidApiKeyReturnsUnauthorizedAsync()
    {
        _client!.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, "invalid-key");
        var response = await _client.GetAsync(new Uri("/api/v1.0/status/detailed", UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ApiKeyOverlapDeactivateReactivateAndDeleteEnforcesLifecycleAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var lifecycle = services.GetRequiredService<IApiKeyLifecycleService>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lifecycle.CreateAsync(
            owner.Id,
            owner.Email!,
            "Invalid access key",
            (ApiKeyAccessLevel)999,
            null,
            CancellationToken.None)).ConfigureAwait(false);
        var first = await lifecycle.CreateAsync(
            owner.Id,
            owner.Email!,
            "Rotation old key",
            ApiKeyAccessLevel.Read,
            DateTimeOffset.UtcNow.AddHours(1),
            CancellationToken.None).ConfigureAwait(false);
        var second = await lifecycle.CreateAsync(
            owner.Id,
            owner.Email!,
            "Rotation replacement key",
            ApiKeyAccessLevel.Read,
            DateTimeOffset.UtcNow.AddHours(1),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, await GetDetailedStatusAsync(first.PlaintextKey).ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, await GetDetailedStatusAsync(second.PlaintextKey).ConfigureAwait(false));
        Assert.IsFalse(await lifecycle.SetActiveAsync(
            "different-owner",
            first.Entity.Id,
            active: false,
            CancellationToken.None).ConfigureAwait(false));

        using (var mutationRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/internal/observatories", UriKind.Relative)))
        {
            mutationRequest.Headers.Add(ApiKeyAuthenticationOptions.HeaderName, second.PlaintextKey);
            mutationRequest.Content = JsonContent.Create(new { name = "Denied" });
            using var mutationResponse = await _client!.SendAsync(mutationRequest).ConfigureAwait(false);
            Assert.IsTrue(
                mutationResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"Expected a denied response, received {mutationResponse.StatusCode}.");
        }

        Assert.IsTrue(await lifecycle.SetActiveAsync(
            owner.Id,
            first.Entity.Id,
            active: false,
            CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await GetDetailedStatusAsync(first.PlaintextKey).ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, await GetDetailedStatusAsync(second.PlaintextKey).ConfigureAwait(false));

        Assert.IsTrue(await lifecycle.SetActiveAsync(
            owner.Id,
            first.Entity.Id,
            active: true,
            CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, await GetDetailedStatusAsync(first.PlaintextKey).ConfigureAwait(false));

        Assert.IsTrue(await lifecycle.DeleteAsync(owner.Id, first.Entity.Id, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await GetDetailedStatusAsync(first.PlaintextKey).ConfigureAwait(false));
    }

    private async Task<HttpStatusCode> GetDetailedStatusAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1.0/status/detailed", UriKind.Relative));
        request.Headers.Add(ApiKeyAuthenticationOptions.HeaderName, apiKey);
        using var response = await _client!.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }

}
