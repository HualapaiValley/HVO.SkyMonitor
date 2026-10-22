using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>An owner API key against the real CameraAgent host: its middleware order, services and settings routes.</summary>
[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OperatorSettingsApiKeyTests
{
    private const string SettingsPath = "/api/v1/operations/settings";
    private static readonly string[] GroupOrder = ["site", "location", "skyMap", "camera", "schedule", "currentSky", "orbitalElements", "satellitePlanning", "timeDataRefresh", "spaceWeather"];

    [TestMethod]
    [DoNotParallelize]
    public async Task OwnerApiKeyReadsTheSettingsAndIsRefusedEverywhereItsAccessDoesNotReachAsync()
    {
        string read;
        string readWrite;
        string ownerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!.Id;
            var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
            read = (await keys.CreateAsync(ownerId, "integration read", ApiKeyAccessLevel.Read, CancellationToken.None)
                .ConfigureAwait(false)).Token!;
            readWrite = (await keys.CreateAsync(ownerId, "integration change", ApiKeyAccessLevel.ReadWrite,
                CancellationToken.None).ConfigureAwait(false)).Token!;
        }

        try
        {
            using var reader = Client(read);
            using var catalogue = await reader.GetAsync(new Uri($"{SettingsPath}/catalogue", UriKind.Relative))
                .ConfigureAwait(false);
            using var values = await reader.GetAsync(new Uri(SettingsPath, UriKind.Relative)).ConfigureAwait(false);
            var catalogueJson = await catalogue.Content.ReadAsStringAsync().ConfigureAwait(false);
            var valuesJson = await values.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, catalogue.StatusCode, catalogueJson);
            Assert.AreEqual(HttpStatusCode.OK, values.StatusCode, valuesJson);
            using (var document = JsonDocument.Parse(valuesJson))
            {
                var groups = document.RootElement.GetProperty("groups").EnumerateArray().ToArray();
                CollectionAssert.AreEqual(
                    GroupOrder,
                    groups.Select(group => group.GetProperty("group").GetString()).ToArray());
                // This host loads no operator settings file, so its settings-file groups are read-only.
                var skyMap = groups.Single(group => group.GetProperty("group").GetString() == "skyMap");
                Assert.IsFalse(skyMap.GetProperty("canWrite").GetBoolean());
            }

            var change = new { expectedVersion = "unavailable", values = new Dictionary<string, int> { ["skyMap.maximumObjects"] = 400 } };
            using var readChange = await reader.PutAsJsonAsync(new Uri($"{SettingsPath}/skyMap", UriKind.Relative), change)
                .ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, readChange.StatusCode);

            // A read-write key passes the mutate policy without an antiforgery token; the store then refuses the
            // read-only group.
            using var writer = Client(readWrite);
            using var writeChange = await writer.PutAsJsonAsync(new Uri($"{SettingsPath}/skyMap", UriKind.Relative), change)
                .ConfigureAwait(false);
            var writeJson = await writeChange.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Conflict, writeChange.StatusCode, writeJson);
            StringAssert.Contains(writeJson, "settings.readOnly", StringComparison.Ordinal);

            using var bootstrap = await writer.GetAsync(new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative))
                .ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, bootstrap.StatusCode);

            using var anonymous = AssemblyHooks.Fixture.CreateCameraAgentClient();
            using var inQuery = await anonymous.GetAsync(
                new Uri($"{SettingsPath}?access_token={Uri.EscapeDataString(readWrite)}", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, inQuery.StatusCode);

            Assert.IsTrue(CameraAgentApiKeyToken.TryReadId(read, out var readId));
            using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
            {
                Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>()
                    .RevokeAsync(ownerId, readId, CancellationToken.None).ConfigureAwait(false));
            }
            using var revoked = await reader.GetAsync(new Uri(SettingsPath, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.AreEqual("Bearer error=\"invalid_token\"", revoked.Headers.WwwAuthenticate.Single().ToString());
        }
        finally
        {
            using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _ = await CameraAgentApiKeyStore.RemoveAllAsync(context, ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static HttpClient Client(string key)
    {
        var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
