using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Cli;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>
/// hvo-sky (#1317) against the real CameraAgent host: its API key middleware, authorization policies, settings catalogue,
/// operations summary, rig and schedule routes, and the new info and restart routes, with keys the owner created.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[UnsupportedOSPlatform("windows")]
public sealed class HvoSkyCliTests
{
    private const string Url = "http://localhost/";
    private static readonly string[] GroupOrder = ["site", "location", "skyMap", "camera", "schedule", "currentSky", "orbitalElements", "satellitePlanning", "timeDataRefresh", "spaceWeather"];

    [TestMethod]
    [DoNotParallelize]
    public async Task HvoSkyRunsItsCommandsAgainstTheRealCameraAgentWithOwnerApiKeysAsync()
    {
        string read;
        string readWrite;
        string ownerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!.Id;
            var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
            read = (await keys.CreateAsync(ownerId, "hvo-sky read", ApiKeyAccessLevel.Read, CancellationToken.None)
                .ConfigureAwait(false)).Token!;
            readWrite = (await keys.CreateAsync(ownerId, "hvo-sky change", ApiKeyAccessLevel.ReadWrite, CancellationToken.None)
                .ConfigureAwait(false)).Token!;
        }

        var home = Path.Combine(Path.GetTempPath(), $"hvo-sky-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var outputs = new List<string>();
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            var input = new Queue<string?>();
            async Task<(int Code, string Out, string Error)> RunAsync(params string[] args)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var host = new SkyCliHost
                {
                    Out = output,
                    Error = error,
                    GetEnvironmentVariable = name => environment.TryGetValue(name, out var value) ? value : null,
                    UserProfile = home,
                    ReadLine = (_, _) => input.Count > 0 ? input.Dequeue() : null,
                    CreateHandler = AssemblyHooks.Fixture.CreateCameraAgentHandler
                };
                var code = await SkyCli.RunAsync(args, host).ConfigureAwait(false);
                outputs.Add(output.ToString() + error.ToString());
                return (code, output.ToString(), error.ToString());
            }

            // login checks the key against the real host before it saves it.
            input.Enqueue(readWrite);
            var login = await RunAsync("login", "--url", Url, "--json").ConfigureAwait(false);
            Assert.AreEqual(0, login.Code, login.Error);
            using (var json = JsonDocument.Parse(login.Out))
            {
                Assert.AreEqual("ReadWrite", json.RootElement.GetProperty("access").GetString());
                Assert.AreEqual(SkyApiKey.IdOf(readWrite), json.RootElement.GetProperty("keyId").GetString());
            }

            var credentials = Path.Combine(home, ".config", "hvo-sky", "credentials.json");
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credentials));

            var info = await RunAsync("info", "--json").ConfigureAwait(false);
            Assert.AreEqual(0, info.Code, info.Error);
            DateTimeOffset started;
            using (var json = JsonDocument.Parse(info.Out))
            {
                var document = json.RootElement.GetProperty("info");
                Assert.AreEqual("CameraAgent", document.GetProperty("application").GetProperty("name").GetString());
                Assert.AreEqual("ApiKey", document.GetProperty("caller").GetProperty("credential").GetString());
                Assert.AreEqual("ReadWrite", document.GetProperty("caller").GetProperty("access").GetString());
                Assert.AreEqual("hyg-v42-fixture", document.GetProperty("catalog").GetProperty("catalogId").GetString());
                Assert.IsFalse(document.GetProperty("restart").GetProperty("supervised").GetBoolean());
                started = document.GetProperty("host").GetProperty("processStartedUtc").GetDateTimeOffset();
            }

            var status = await RunAsync("status", "--json").ConfigureAwait(false);
            Assert.AreEqual(0, status.Code, status.Error);
            using (var json = JsonDocument.Parse(status.Out))
            {
                Assert.IsTrue(json.RootElement.TryGetProperty("captureControl", out _));
            }

            var statusText = await RunAsync("status").ConfigureAwait(false);
            Assert.AreEqual(0, statusText.Code, statusText.Error);
            StringAssert.Contains(statusText.Out, "Capture:", StringComparison.Ordinal);

            var show = await RunAsync("config", "show", "--json").ConfigureAwait(false);
            Assert.AreEqual(0, show.Code, show.Error);
            using (var json = JsonDocument.Parse(show.Out))
            {
                CollectionAssert.AreEqual(
                    GroupOrder,
                    json.RootElement.GetProperty("groups").EnumerateArray().Select(static group => group.GetProperty("name").GetString()).ToArray());
            }

            var get = await RunAsync("config", "get", "maximumObjects").ConfigureAwait(false);
            Assert.AreEqual(0, get.Code, get.Error);
            StringAssert.Contains(get.Out, "skyMap.maximumObjects", StringComparison.Ordinal);

            // This host loads no operator settings file, so the settings-file groups are read-only: refused before sending.
            var set = await RunAsync("config", "set", "skyMap.maximumObjects=401").ConfigureAwait(false);
            Assert.AreEqual(3, set.Code, set.Out + set.Error);

            // The shared host is not supervised, so a restart is refused and the host keeps running.
            var restart = await RunAsync("restart", "--force").ConfigureAwait(false);
            Assert.AreEqual(3, restart.Code, restart.Error);
            StringAssert.Contains(restart.Error, "not supervised", StringComparison.Ordinal);
            var stillRunning = await RunAsync("info", "--json").ConfigureAwait(false);
            using (var json = JsonDocument.Parse(stillRunning.Out))
            {
                Assert.AreEqual(started, json.RootElement.GetProperty("info").GetProperty("host").GetProperty("processStartedUtc").GetDateTimeOffset());
            }

            var rig = await RunAsync("rig", "show", "--json").ConfigureAwait(false);
            Assert.AreEqual(0, rig.Code, rig.Error);
            var schedule = await RunAsync("schedule", "show", "--json").ConfigureAwait(false);
            Assert.AreEqual(0, schedule.Code, schedule.Error);

            // A revision the schedule does not list is refused before anything is sent: whether it is older or newer than
            // the active one cannot be established.
            var unlisted = await RunAsync("schedule", "apply", "no-such-revision", "--json").ConfigureAwait(false);
            Assert.AreEqual(3, unlisted.Code, unlisted.Out);
            using (var json = JsonDocument.Parse(unlisted.Out))
            {
                Assert.AreEqual("Refused", json.RootElement.GetProperty("error").GetProperty("kind").GetString());
            }

            // A Read key from the environment overrides the saved key; CameraAgent refuses it a change (403). A settings
            // change is refused by the client itself, as the catalogue marks it read-only for that key, so the change
            // sent is a schedule stage, which CameraAgent's authorization refuses before it reads the profile.
            environment[SkyCredentialStore.ApiKeyVariable] = read;
            var profile = Path.Combine(home, "profile.json");
            await File.WriteAllTextAsync(profile, """{"name": "operator"}""").ConfigureAwait(false);
            var forbidden = await RunAsync("schedule", "stage", profile, "--json").ConfigureAwait(false);
            Assert.AreEqual(3, forbidden.Code, forbidden.Out);
            using (var json = JsonDocument.Parse(forbidden.Out))
            {
                Assert.AreEqual("Forbidden", json.RootElement.GetProperty("error").GetProperty("kind").GetString());
            }

            // A revoked key is refused (401), and the command exits 3.
            Assert.IsTrue(CameraAgentApiKeyToken.TryReadId(read, out var readId));
            using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
            {
                Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>()
                    .RevokeAsync(ownerId, readId, CancellationToken.None).ConfigureAwait(false));
            }

            var revoked = await RunAsync("status").ConfigureAwait(false);
            Assert.AreEqual(3, revoked.Code, revoked.Error);
            StringAssert.Contains(revoked.Error, "did not accept the API key", StringComparison.Ordinal);

            environment.Remove(SkyCredentialStore.ApiKeyVariable);
            var logout = await RunAsync("logout").ConfigureAwait(false);
            Assert.AreEqual(0, logout.Code, logout.Error);
            Assert.IsNull(SkyCredentialStore.Load(credentials)!.ApiKey);

            foreach (var text in outputs)
            {
                Assert.DoesNotContain(read, text, StringComparison.Ordinal);
                Assert.DoesNotContain(readWrite, text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _ = await CameraAgentApiKeyStore.RemoveAllAsync(context, ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
