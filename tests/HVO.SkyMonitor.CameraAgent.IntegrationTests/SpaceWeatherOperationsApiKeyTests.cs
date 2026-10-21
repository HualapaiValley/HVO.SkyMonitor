using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>
/// An owner API key against the real host's space-weather commands: a read-write key passes the antiforgery gate
/// without a token (a browser never adds the <c>Authorization</c> header to a cross-site request), and a read key is
/// refused by the mutate policy.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SpaceWeatherOperationsApiKeyTests
{
    private const string RefreshPath = "/api/v1/operations/space-weather/refresh";
    private const string ResumePath = "/api/v1/operations/space-weather/resume";

    [TestMethod]
    [DoNotParallelize]
    public async Task AReadWriteApiKeyReachesTheSpaceWeatherCommandsWithoutAnAntiforgeryTokenAsync()
    {
        string read;
        string readWrite;
        string ownerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await users.FindByEmailAsync(scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["LocalIdentity:AdminEmail"]!).ConfigureAwait(false))!.Id;
            var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
            read = (await keys.CreateAsync(ownerId, "space-weather read", ApiKeyAccessLevel.Read, CancellationToken.None)
                .ConfigureAwait(false)).Token!;
            readWrite = (await keys.CreateAsync(ownerId, "space-weather change", ApiKeyAccessLevel.ReadWrite,
                CancellationToken.None).ConfigureAwait(false)).Token!;
        }

        try
        {
            // Refresh is disabled in this host, so a request that clears the antiforgery, owner and key checks is
            // refused by policy with space-weather.disabled, and no provider request is queued.
            using var writer = Client(readWrite);
            using (var refresh = Command(RefreshPath, new { reason = "api key refresh" }))
            {
                using var response = await writer.SendAsync(refresh).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, json);
                StringAssert.Contains(json, "space-weather.disabled", StringComparison.Ordinal);
                Assert.IsFalse(json.Contains("space-weather.antiforgery-invalid", StringComparison.Ordinal), json);
            }
            using (var resume = Command(ResumePath, new { scope = "provider", expectedHaltSinceUtc = DateTimeOffset.UnixEpoch }))
            {
                using var response = await writer.SendAsync(resume).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
                StringAssert.Contains(json, "not-halted", StringComparison.Ordinal);
                Assert.IsFalse(json.Contains("space-weather.antiforgery-invalid", StringComparison.Ordinal), json);
            }

            using var reader = Client(read);
            using (var refresh = Command(RefreshPath, new { reason = "read key refresh" }))
            {
                using var response = await reader.SendAsync(refresh).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
            using (var resume = Command(ResumePath, new { scope = "provider", expectedHaltSinceUtc = DateTimeOffset.UnixEpoch }))
            {
                using var response = await reader.SendAsync(resume).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        finally
        {
            using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _ = await CameraAgentApiKeyStore.RemoveAllAsync(context, ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage Command(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("Idempotency-Key", $"orbital-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpClient Client(string key)
    {
        var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
