using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>
/// A whole CameraAgent host started with a Silo-layout first-boot pause (#1966; cutover contract 8.1 and 8.4): the
/// seeded pause holds through owner bootstrap and restart while the camera module initializes and no exposure or
/// capture sequence is admitted, and after a same-version operator pause neither installer resume route can resume.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class FirstBootPauseHostTests
{
    private const string LifecycleToken = "first-boot-host-lifecycle-token";

    [TestMethod]
    public async Task SeededPauseHoldsThroughBootstrapAndRestartAndRefusesInstallerResumesAfterOperatorPause()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"hvo-first-boot-host-{Guid.NewGuid():N}");
        var firstBootId = Guid.NewGuid();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CameraAgent:FirstBootPause:Id"] = firstBootId.ToString("D"),
            ["LifecycleControl:Token"] = LifecycleToken
        };
        try
        {
            var modules = new RecordingModules();
            using (var host = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true })
            {
                await host.InitializeAsync(services => modules.Install(services), settings, storageRoot).ConfigureAwait(false);
                using var client = host.CreateCameraAgentClient();

                var status = await ReadFirstBootAsync(client).ConfigureAwait(false);
                Assert.IsTrue(status.GetProperty("seeded").GetBoolean());
                Assert.IsTrue(status.GetProperty("inForce").GetBoolean());
                Assert.AreEqual("paused", status.GetProperty("state").GetString());
                Assert.AreEqual(1L, status.GetProperty("version").GetInt64());

                await modules.WaitForInitializationAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                Assert.AreEqual(0, modules.Captures, "No exposure may be taken while the first-boot pause is in force.");
                await AssertNothingAdmittedAsync(storageRoot).ConfigureAwait(false);

                // Same-version operator pause through the authenticated operator API: an accepted no-op.
                using var owner = host.CreateCameraAgentClient();
                owner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, host.OwnerId);
                var antiforgery = await GetAntiforgeryTokenAsync(owner).ConfigureAwait(false);
                using (var pause = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/operations/capture/pause", UriKind.Relative)))
                {
                    pause.Headers.Add("Idempotency-Key", "operator-hold-before-first-resume");
                    pause.Headers.Add("RequestVerificationToken", antiforgery);
                    pause.Content = JsonContent.Create(new { expectedVersion = 1, reason = "Hold for inspection" });
                    using var paused = await owner.SendAsync(pause).ConfigureAwait(false);
                    Assert.AreEqual(HttpStatusCode.OK, paused.StatusCode, await paused.Content.ReadAsStringAsync().ConfigureAwait(false));
                    using var body = JsonDocument.Parse(await paused.Content.ReadAsStringAsync().ConfigureAwait(false));
                    Assert.IsFalse(body.RootElement.GetProperty("changed").GetBoolean());
                    Assert.AreEqual(1L, body.RootElement.GetProperty("version").GetInt64());
                }

                // Both installer resume routes refuse, with and without an expected version.
                Assert.AreEqual(HttpStatusCode.Conflict, await PostLifecycleAsync(client, "first-resume",
                    new { firstBootId, expectedVersion = 1 }).ConfigureAwait(false));
                Assert.AreEqual(HttpStatusCode.Conflict, await PostLifecycleAsync(client, "resume",
                    new { operationId = Guid.NewGuid(), expectedVersion = 1 }).ConfigureAwait(false));
                Assert.AreEqual(HttpStatusCode.Conflict, await PostLifecycleAsync(client, "resume",
                    new { operationId = Guid.NewGuid() }).ConfigureAwait(false));

                status = await ReadFirstBootAsync(client).ConfigureAwait(false);
                Assert.IsFalse(status.GetProperty("inForce").GetBoolean());
                Assert.IsFalse(status.GetProperty("resumed").GetBoolean());
                Assert.AreEqual("paused", status.GetProperty("state").GetString());
                Assert.AreEqual(1L, status.GetProperty("version").GetInt64());
                Assert.AreEqual(0, modules.Captures);
            }

            // Restart on the same state: still paused, nothing admitted, the camera module initializes again.
            var restartedModules = new RecordingModules();
            using (var restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true })
            {
                await restarted.InitializeAsync(services => restartedModules.Install(services), settings, storageRoot).ConfigureAwait(false);
                using var client = restarted.CreateCameraAgentClient();
                var status = await ReadFirstBootAsync(client).ConfigureAwait(false);
                Assert.AreEqual("paused", status.GetProperty("state").GetString());
                Assert.AreEqual(1L, status.GetProperty("version").GetInt64());
                await restartedModules.WaitForInitializationAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                Assert.AreEqual(0, restartedModules.Captures);
                Assert.AreEqual(HttpStatusCode.Conflict, await PostLifecycleAsync(client, "first-resume",
                    new { firstBootId, expectedVersion = 1 }).ConfigureAwait(false));
                await AssertNothingAdmittedAsync(storageRoot).ConfigureAwait(false);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OwnerBootstrapThenAuthorizedFirstResumeStartsCaptureAndInstallerOwnsOnlyItsPause()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"hvo-first-boot-host-{Guid.NewGuid():N}");
        var firstBootId = Guid.NewGuid();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CameraAgent:FirstBootPause:Id"] = firstBootId.ToString("D"),
            ["LifecycleControl:Token"] = LifecycleToken
        };
        try
        {
            // First start with installer password authority: the owner holds the temporary password.
            var modules = new RecordingModules();
            using (var host = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true, PrepareOwner = false })
            {
                await host.InitializeAsync(services => modules.Install(services), settings, storageRoot).ConfigureAwait(false);
                using var owner = OwnerClient(host);
                Assert.AreEqual("owner-temporary-password", await ReadOwnerStateAsync(owner).ConfigureAwait(false));
                using var client = host.CreateCameraAgentClient();
                Assert.IsTrue((await ReadFirstBootAsync(client).ConfigureAwait(false)).GetProperty("inForce").GetBoolean());
                await modules.WaitForInitializationAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Assert.AreEqual(0, modules.Captures);
                await AssertNothingAdmittedAsync(storageRoot).ConfigureAwait(false);
            }

            // The installer's owner bootstrap restarts without password authority; the owner then replaces the
            // temporary password through the application itself. The pause holds throughout.
            var bootstrapped = new Dictionary<string, string?>(settings, StringComparer.Ordinal)
            {
                ["LocalIdentity:AdminPassword"] = string.Empty,
                ["LocalIdentity:AllowMissingAdminPassword"] = "true"
            };
            var restartedModules = new RecordingModules();
            using var restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true, PrepareOwner = false };
            await restarted.InitializeAsync(services => restartedModules.Install(services), bootstrapped, storageRoot).ConfigureAwait(false);
            using var ownerAfterRestart = OwnerClient(restarted);
            Assert.AreEqual("owner-password-change-required", await ReadOwnerStateAsync(ownerAfterRestart).ConfigureAwait(false));
            await ReplaceTemporaryPasswordAsync(ownerAfterRestart, "IntegrationOwner!123", "BootstrappedOwner!4567").ConfigureAwait(false);
            Assert.AreEqual("owner-ready", await ReadOwnerStateAsync(ownerAfterRestart).ConfigureAwait(false));
            using var lifecycle = restarted.CreateCameraAgentClient();
            var status = await ReadFirstBootAsync(lifecycle).ConfigureAwait(false);
            Assert.IsTrue(status.GetProperty("inForce").GetBoolean());
            await restartedModules.WaitForInitializationAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(0, restartedModules.Captures);
            await AssertNothingAdmittedAsync(storageRoot).ConfigureAwait(false);

            // The authorized release: capture starts and allocates sequences in the same runner.
            Assert.AreEqual(HttpStatusCode.OK, await PostLifecycleAsync(lifecycle, "first-resume",
                new { firstBootId, expectedVersion = 1 }).ConfigureAwait(false));
            await WaitForAdmissionAsync(storageRoot, restartedModules, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            // An installer operation's pause and resume carry distinct command IDs and one stable owner.
            var operation = Guid.NewGuid();
            Assert.AreEqual(HttpStatusCode.OK, await PostLifecycleAsync(lifecycle, "pause",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }).ConfigureAwait(false));
            Assert.AreEqual(HttpStatusCode.OK, await PostLifecycleAsync(lifecycle, "resume",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }).ConfigureAwait(false));

            // After the installer pauses again and the operator pauses later, the installer cannot resume.
            Assert.AreEqual(HttpStatusCode.OK, await PostLifecycleAsync(lifecycle, "pause",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }).ConfigureAwait(false));
            var antiforgery = await GetAntiforgeryTokenAsync(ownerAfterRestart).ConfigureAwait(false);
            using (var pause = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/operations/capture/pause", UriKind.Relative)))
            {
                pause.Headers.Add("Idempotency-Key", "operator-hold-after-installer-pause");
                pause.Headers.Add("RequestVerificationToken", antiforgery);
                pause.Content = JsonContent.Create(new { reason = "Hold for inspection" });
                using var paused = await ownerAfterRestart.SendAsync(pause).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, paused.StatusCode, await paused.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            Assert.AreEqual(HttpStatusCode.Conflict, await PostLifecycleAsync(lifecycle, "resume",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }).ConfigureAwait(false));
            Assert.AreEqual("paused", (await ReadFirstBootAsync(lifecycle).ConfigureAwait(false)).GetProperty("state").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    private static HttpClient OwnerClient(CameraAgentIntegrationFixture host)
    {
        var client = host.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, host.OwnerId);
        return client;
    }

    private static async Task<string?> ReadOwnerStateAsync(HttpClient owner)
    {
        using var response = await owner.GetAsync(new Uri("/api/internal/owner-bootstrap/status", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("state").GetString();
    }

    private static async Task ReplaceTemporaryPasswordAsync(HttpClient owner, string current, string replacement)
    {
        using var page = await owner.GetAsync(new Uri("/Account/ReplaceTemporaryPassword", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "replace-temporary-password",
            ["__RequestVerificationToken"] = HiddenValue(html, "__RequestVerificationToken"),
            ["Input.CurrentPassword"] = current,
            ["Input.NewPassword"] = replacement,
            ["Input.ConfirmPassword"] = replacement
        });
        using var response = await owner.PostAsync(new Uri("/Account/ReplaceTemporaryPassword", UriKind.Relative), form).ConfigureAwait(false);
        Assert.IsTrue(response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.Redirect,
            $"Password replacement returned {(int)response.StatusCode}.");
    }

    private static string HiddenValue(string html, string name)
    {
        var nameIndex = html.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, nameIndex);
        const string value = "value=\"";
        var valueIndex = html.IndexOf(value, nameIndex, StringComparison.Ordinal) + value.Length;
        return WebUtility.HtmlDecode(html[valueIndex..html.IndexOf('"', valueIndex)]);
    }

    private static async Task WaitForAdmissionAsync(string storageRoot, RecordingModules modules, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(storageRoot, "journal", "raw-ingress.db")};Mode=ReadOnly"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM raw_capture_sequences) * (SELECT COUNT(*) FROM raw_captures);";
                if (modules.Captures > 0 &&
                    Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0)
                    return;
            }
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"Capture did not start after the authorized release (exposures: {modules.Captures}).");
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> ReadFirstBootAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/internal/deployment/lifecycle/first-boot", UriKind.Relative));
        request.Headers.Add("X-HVO-Installation-Token", LifecycleToken);
        for (var attempt = 0; ; attempt++)
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            copy.Headers.Add("X-HVO-Installation-Token", LifecycleToken);
            using var response = await client.SendAsync(copy).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 60)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                continue;
            }
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return document.RootElement.Clone();
        }
    }

    private static async Task<HttpStatusCode> PostLifecycleAsync(HttpClient client, string action, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/internal/deployment/lifecycle/{action}", UriKind.Relative))
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-HVO-Installation-Token", LifecycleToken);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }

    private static async Task AssertNothingAdmittedAsync(string storageRoot)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(storageRoot, "journal", "raw-ingress.db")};Mode=ReadOnly");
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var table in new[] { "raw_captures", "raw_capture_sequences" })
        {
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Constant table names from this method only.
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
#pragma warning restore CA2100
            Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture), table);
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        const string name = "name=\"__RequestVerificationToken\"";
        var nameIndex = html.IndexOf(name, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, nameIndex);
        const string value = "value=\"";
        var valueIndex = html.IndexOf(value, nameIndex, StringComparison.Ordinal) + value.Length;
        var endIndex = html.IndexOf('"', valueIndex);
        return WebUtility.HtmlDecode(html[valueIndex..endIndex]);
    }

    /// <summary>Counts module initializations and exposures of the configured fake camera, delegating to the real factory.</summary>
    private sealed class RecordingModules
    {
        private int _initialized;
        private int _captures;
        private readonly TaskCompletionSource _firstInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Captures => Volatile.Read(ref _captures);

        public void Install(IServiceCollection services)
        {
            var existing = services.Last(static descriptor => descriptor.ServiceType == typeof(ICameraModuleFactory));
            services.Remove(existing);
            services.AddSingleton<ICameraModuleFactory>(provider =>
                new Factory(provider.GetRequiredService<CameraModuleFactory>(), this));
        }

        public Task WaitForInitializationAsync(TimeSpan timeout) => _firstInitialization.Task.WaitAsync(timeout);

        private void Initialized()
        {
            Interlocked.Increment(ref _initialized);
            _firstInitialization.TrySetResult();
        }

        private void Captured() => Interlocked.Increment(ref _captures);

        private sealed class Factory(ICameraModuleFactory inner, RecordingModules recorder) : ICameraModuleFactory
        {
            public ICameraModule Create(string moduleType) => new Module(inner.Create(moduleType), recorder);
        }

        private sealed class Module(ICameraModule inner, RecordingModules recorder) : ICameraModule
        {
            public string Id => inner.Id;
            public string DisplayName => inner.DisplayName;
            public string ModuleType => inner.ModuleType;
            public CameraModuleCapabilities Capabilities => inner.Capabilities;

            public async Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            {
                await inner.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
                recorder.Initialized();
            }

            public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            {
                recorder.Captured();
                return inner.CaptureAsync(request, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
