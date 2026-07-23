using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed class StandaloneCameraAgentKestrelFixture : IAsyncDisposable
{
    internal const string AgentId = "cameraagent-standalone-acceptance";
    internal const string OwnerEmail = "standalone-owner@cameraagent.test";
    internal const string OwnerPassword = "StandaloneOwner!194";

    private readonly string _root;
    private readonly CatalogFixtureInstallation _catalog;
    private readonly IReadOnlyDictionary<string, string?> _overrides;
    private readonly ConcurrentQueue<OutboundHttpAttempt> _outboundAttempts = new();
    private HostInstance? _host;

    private StandaloneCameraAgentKestrelFixture(
        string root,
        CatalogFixtureInstallation catalog,
        IReadOnlyDictionary<string, string?> overrides)
    {
        _root = root;
        _catalog = catalog;
        _overrides = overrides;
    }

    internal Uri BaseAddress => Host.LifetimeClient.BaseAddress
        ?? throw new InvalidOperationException("The standalone fixture has no base address.");

    internal IServiceProvider Services => Host.Factory.Services;

    internal string Root => _root;

    internal IReadOnlyCollection<OutboundHttpAttempt> OutboundAttempts => _outboundAttempts.ToArray();

    private HostInstance Host => _host
        ?? throw new InvalidOperationException("The standalone CameraAgent host is not running.");

    internal static async Task<StandaloneCameraAgentKestrelFixture> CreateAsync(bool useSidingSpringLocation = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-cameraagent-standalone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var catalog = CatalogFixtureInstallation.Create(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"),
            Path.Combine(root, "catalog"));

        try
        {
            var configPath = Path.Combine(root, "cameraagent.standalone.json");
            await WriteConfigurationAsync(configPath, root).ConfigureAwait(false);
            var overrides = new Dictionary<string, string?>
            {
                ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true",
                ["CameraAgent:EnvironmentalDelivery:Enabled"] = "true",
                ["CameraAgent:ConfigFilePath"] = configPath,
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:TransientDetection:Mode"] = "Off",
                ["LocalIdentity:AdminEmail"] = OwnerEmail,
                ["LocalIdentity:AdminPassword"] = OwnerPassword,
                ["LocalIdentity:DatabasePath"] = Path.Combine(root, "identity", "cameraagent_identity.db"),
                ["LocalIdentity:CookieName"] = "CameraAgent.Standalone194.Auth",
                ["DeviceProvisioning:StateDirectory"] = Path.Combine(root, "provisioning"),
                ["Catalog:Root"] = catalog.Root,
                ["Catalog:RequiredPackageKind"] = "Fixture",
                ["CentralIdentity:ServiceUrl"] = "http://central-forbidden.invalid/",
                ["SkyMonitor:BaseUrl"] = "http://central-forbidden.invalid",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
                ["Logging:LogLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Default"] = "Warning"
            };
            if (useSidingSpringLocation)
            {
                overrides["CameraAgent:Observatory:LatitudeDegrees"] = "-31.2733";
                overrides["CameraAgent:Observatory:LongitudeDegrees"] = "149.0700";
                overrides["CameraAgent:Observatory:ElevationMeters"] = "1165";
                overrides["CameraAgent:Observatory:TimeZoneId"] = "Australia/Sydney";
                overrides["CameraAgent:DeploymentLocation:LocationId"] = "siding-spring-synthetic";
                overrides["CameraAgent:DeploymentLocation:Source"] =
                    "GitHub issue #196 operator-pinned acceptance coordinates; not a physical survey";
                overrides["CameraAgent:DeploymentLocation:EffectiveFromUtc"] = "2025-01-01T00:00:00Z";
            }
            var fixture = new StandaloneCameraAgentKestrelFixture(root, catalog, overrides);
            await fixture.StartHostAsync().ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            catalog.Dispose();
            await DeleteWithRetriesAsync(root).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RestartHostAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        await StartHostAsync().ConfigureAwait(false);
    }

    internal async Task StopHostAsync()
    {
        if (_host is null)
        {
            return;
        }

        var host = _host;
        _host = null;
        await host.DisposeAsync().ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient takes ownership of its handler and the caller owns the returned client.")]
    internal async Task<HttpClient> CreateOwnerClientAsync()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        })
        {
            BaseAddress = BaseAddress
        };
        using var login = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode();
        var html = await login.Content.ReadAsStringAsync().ConfigureAwait(false);
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            client.Dispose();
            throw new InvalidOperationException("The local login antiforgery token was not rendered.");
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
            ["Input.Email"] = OwnerEmail,
            ["Input.Password"] = OwnerPassword,
            ["Input.RememberMe"] = "false",
            ["_handler"] = "login"
        });
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        _catalog.Dispose();
        await DeleteWithRetriesAsync(_root).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The created factory is transferred to HostInstance ownership or disposed by the failure path.")]
    private async Task StartHostAsync()
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("The standalone CameraAgent host is already running.");
        }

        var factory = new StandaloneWebApplicationFactory(_root, _overrides, _outboundAttempts);
        factory.UseKestrel(0);
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var server = factory.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.");
            client.BaseAddress = new Uri(address, UriKind.Absolute);
            _host = new HostInstance(factory, client);
        }
        catch
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteConfigurationAsync(string configPath, string root)
    {
        var template = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.integration.json")).ConfigureAwait(false);
        var json = template
            .Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(root), StringComparison.Ordinal)
            .Replace(
                "__TRANSIENT_EPOCH_UTC__",
                JsonSerializer.Serialize(DateTimeOffset.UtcNow.AddMinutes(-1)),
                StringComparison.Ordinal);
        var configuration = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("The standalone CameraAgent fixture configuration is invalid.");
        configuration["agentId"] = AgentId;
        var localStorage = configuration["processingSteps"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static step => step["id"]!.GetValue<string>() == "LocalStorage");
        var options = localStorage["options"]!.AsObject();
        options["queueForUpload"] = true;
        foreach (var policy in options["policies"]!.AsArray())
        {
            policy!.AsObject()["queueForUpload"] = true;
        }

        await File.WriteAllTextAsync(
            configPath,
            configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static async Task DeleteWithRetriesAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (
                attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1))).ConfigureAwait(false);
            }
        }
        throw new IOException($"Acceptance fixture cleanup failed; retained path: {path}");
    }

    internal sealed record OutboundHttpAttempt(string Method, Uri? RequestUri, DateTimeOffset AttemptedUtc);

    private sealed class HostInstance(
        WebApplicationFactory<Program> factory,
        HttpClient lifetimeClient) : IAsyncDisposable
    {
        internal WebApplicationFactory<Program> Factory { get; } = factory;

        internal HttpClient LifetimeClient { get; } = lifetimeClient;

        public async ValueTask DisposeAsync()
        {
            LifetimeClient.Dispose();
            await Factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class StandaloneWebApplicationFactory(
        string root,
        IReadOnlyDictionary<string, string?> overrides,
        ConcurrentQueue<OutboundHttpAttempt> outboundAttempts) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(overrides));
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection()
                    .SetApplicationName("HVO.SkyMonitor.CameraAgent.StandaloneAcceptanceTests")
                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "dataprotection")));
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(messageBuilder =>
                        messageBuilder.AdditionalHandlers.Insert(
                            0,
                            new RejectingOutboundHttpHandler(outboundAttempts))));
            });
        }
    }

    private sealed class RejectingOutboundHttpHandler(
        ConcurrentQueue<OutboundHttpAttempt> outboundAttempts) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            outboundAttempts.Enqueue(new OutboundHttpAttempt(
                request.Method.Method,
                request.RequestUri,
                DateTimeOffset.UtcNow));
            throw new HttpRequestException("Standalone acceptance rejected an outbound HTTP request.");
        }
    }
}
