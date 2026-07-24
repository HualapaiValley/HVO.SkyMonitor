using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
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

    [TestMethod]
    public async Task ObservatoryReadWithMergedBearerAndApiKeyIdentitiesIsUnauthorizedAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(
            owner.Id,
            owner.Email!,
            "Merged identity test",
            ApiKeyAccessLevel.Read,
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None).ConfigureAwait(false);
        var token = await HttpHelpers.GetPasswordTokenAsync(
            _client!,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            "openid profile api.viewer").ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/internal/observatories", UriKind.Relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Add(ApiKeyAuthenticationOptions.HeaderName, key.PlaintextKey);

        using var response = await _client!.SendAsync(request).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ObservatoryUpdateRequiresCurrentStrongEtagAsync()
    {
        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        var request = new
        {
            id = (Guid?)null,
            name = $"ETag Observatory {Guid.NewGuid():N}",
            latitudeDegrees = 35.347,
            longitudeDegrees = -113.878,
            elevationMeters = 520,
            timeZoneId = "America/Phoenix",
            isActive = true,
            allowedDeploymentRadiusMeters = (double?)1000
        };
        using var created = await ownerClient.PostAsJsonAsync(
            new Uri("/api/internal/observatories", UriKind.Relative), request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
        Assert.IsNotNull(created.Headers.ETag);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        var id = body.EnumerateObject().Single(property =>
            property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).Value.GetGuid();
        var update = new
        {
            id = (Guid?)id,
            request.name,
            latitudeDegrees = 35.348,
            request.longitudeDegrees,
            request.elevationMeters,
            request.timeZoneId,
            request.isActive,
            request.allowedDeploymentRadiusMeters
        };
        using var missing = await ownerClient.PostAsJsonAsync(
            new Uri("/api/internal/observatories", UriKind.Relative), update).ConfigureAwait(false);
        Assert.AreEqual((HttpStatusCode)428, missing.StatusCode);
        using var staleRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/internal/observatories", UriKind.Relative))
        {
            Content = JsonContent.Create(update)
        };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{new string('A', 64)}\"");
        using var stale = await ownerClient.SendAsync(staleRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var currentRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/internal/observatories", UriKind.Relative))
        {
            Content = JsonContent.Create(update)
        };
        currentRequest.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
        using var current = await ownerClient.SendAsync(currentRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, current.StatusCode);
        Assert.AreNotEqual(created.Headers.ETag.Tag, current.Headers.ETag?.Tag);
        using var deleted = await ownerClient.DeleteAsync(
            new Uri($"/api/internal/observatories/{id:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    private async Task<HttpStatusCode> GetDetailedStatusAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1.0/status/detailed", UriKind.Relative));
        request.Headers.Add(ApiKeyAuthenticationOptions.HeaderName, apiKey);
        using var response = await _client!.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }

}
