using System.Net;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured fixture disposal hides its strongly typed members; all asynchronous calls are configured.")]
public sealed class InstallationVerificationStartupTests
{
    [TestMethod]
    public async Task CompleteHostedServiceGraphStartsBeforeDeferredCaptureConfiguration()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            // Keep every production hosted service. A service waiting inline for
            // deferred configuration must not prevent ApplicationStarted.
            services.Configure<HostOptions>(options => options.StartupTimeout = TimeSpan.FromSeconds(10));
        }, scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        using var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative)).ConfigureAwait(false);
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, alive.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.IsTrue(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);

        await host.RunWithHostStoppedAsync(async root =>
        {
            var raw = Path.Combine(root, "frames", DateTimeOffset.UtcNow.ToString("yyyy/MM/dd",
                System.Globalization.CultureInfo.InvariantCulture), "Raw");
            Directory.CreateDirectory(raw);
            await File.WriteAllTextAsync(Path.Combine(raw, "torn.json"), "{").ConfigureAwait(false);
        }).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loader = host.Services.GetRequiredService<ICameraAgentConfigurationLoader>();
        await loader.DrainRetainedIntegrityScrubAsync(timeout.Token).ConfigureAwait(false);
        Assert.IsTrue(loader.RetainedIntegrityRecoveryRequired);
        using var owner = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        using var restartedAlive = await owner.GetAsync(new Uri("/alive", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        using var recovery = await owner.GetAsync(new Uri("/system", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        using var unhealthy = await owner.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, restartedAlive.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, recovery.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        request.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        using var verification = await owner.SendAsync(request, timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, verification.StatusCode);
    }

    [TestMethod]
    [DataRow("quarantine/torn.json", "{")]
    [DataRow("quarantine/invalid-v2.json", "{\"schemaVersion\":\"v2\"}")]
    [DataRow("frames/2026/11/10/Raw/torn.json", "{")]
    public async Task StartupAndRecoveryUiRemainAvailableWhileProtectedReadRefusesInvalidEvidence(string relative, string json)
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            // Run the real configuration load during host startup, with autonomous acquisition disabled.
            services.RemoveAll<IHostedService>();
            services.AddSingleton(provider => new ConfigurationStartupProbe(provider.GetRequiredService<ICameraAgentConfigurationLoader>()));
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ConfigurationStartupProbe>());
        }, prepareRoot: async root =>
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }, scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        Assert.IsTrue(host.Services.GetRequiredService<ConfigurationStartupProbe>().Loaded);
        using var owner = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        using var page = await owner.GetAsync(new Uri("/operations/quarantine", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        StringAssert.Contains(await page.Content.ReadAsStringAsync().ConfigureAwait(false), "Quarantine and retry browser", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        request.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        using var response = await owner.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(json, await File.ReadAllTextAsync(Path.Combine(host.Root, relative)).ConfigureAwait(false));
        await host.RestartAsync().ConfigureAwait(false);
        Assert.IsTrue(host.Services.GetRequiredService<ConfigurationStartupProbe>().Loaded);
    }

    [TestMethod]
    public async Task OptionalIndexWriteFailureDoesNotPreventHostStartup()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddSingleton(provider => new ConfigurationStartupProbe(provider.GetRequiredService<ICameraAgentConfigurationLoader>()));
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ConfigurationStartupProbe>());
        }, prepareRoot: root =>
        {
            // A directory at the disposable DB name is an injected cache-open/write failure.
            Directory.CreateDirectory(Path.Combine(root, ".location", "retained-validation.v2.db"));
            return Task.CompletedTask;
        }, scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        Assert.IsTrue(host.Services.GetRequiredService<ConfigurationStartupProbe>().Loaded);
        using var owner = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        using var page = await owner.GetAsync(new Uri("/operations/quarantine", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
    }

    private sealed class ConfigurationStartupProbe(ICameraAgentConfigurationLoader loader) : IHostedService
    {
        internal bool Loaded { get; private set; }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _ = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
            Loaded = true;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
