using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Configuration;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed class CameraAgentKestrelFixture : IAsyncDisposable
{
    /// <summary>The override key that carries the opt-in environmental settings file to the host.</summary>
    private const string EnvironmentalSettingsKey = "HvoBrowserFixture:EnvironmentalSettingsPath";

    /// <summary>The one on-demand-capable environmental source the opt-in fixture configures.</summary>
    internal const string EnvironmentalSourceId = "browser-air-temperature";

    internal const string AgentId = "cameraagent-browser-acceptance";
    internal const string InstallationVerificationToken = "cameraagent-browser-verification-token";
    internal const string LifecycleControlToken = "cameraagent-browser-lifecycle-token";
    internal const string OwnerEmail = "owner@cameraagent.browser";
    internal const string OwnerPassword = "BrowserOwner!106";
    internal const string NonOwnerEmail = "viewer@cameraagent.browser";
    internal const string NonOwnerPassword = "BrowserViewer!106";

    private readonly string _root;
    private readonly CatalogFixtureInstallation _catalog;
    private readonly Dictionary<string, string?> _overrides;
    private readonly Action<IServiceCollection>? _configureServices;
    private bool _preserveRoot;
    private bool _reloadConfigurationOnChange = true;
    private BrowserWebApplicationFactory? _factory;
    private HttpClient? _lifetimeClient;
    private int _restartPort;

    internal void PreservePortOnRestart() => _restartPort = BaseAddress.Port;

    private CameraAgentKestrelFixture(
        string root,
        CatalogFixtureInstallation catalog,
        Dictionary<string, string?> overrides,
        Action<IServiceCollection>? configureServices)
    {
        _root = root;
        _catalog = catalog;
        _overrides = overrides;
        _configureServices = configureServices;
    }

    internal Uri BaseAddress => _lifetimeClient?.BaseAddress
        ?? throw new InvalidOperationException("The Kestrel fixture has no base address.");

    internal string Root => _root;

    /// <summary>The operator settings file the host loads, as an installer deployment loads its own.</summary>
    internal string SettingsFilePath => Path.Combine(_root, DeploymentLocalSettings.FileName);

    internal string RecoverySocketPath => Path.Combine(
        _root,
        "identity",
        OwnerRecoveryTransport.SocketFileName);

    internal IServiceProvider Services => (_factory
        ?? throw new InvalidOperationException("The Kestrel fixture is not running.")).Services;

    internal static async Task<CameraAgentKestrelFixture> CreateAsync(
        Action<IServiceCollection>? configureServices = null,
        bool useCalibrationLibrary = false,
        bool requireOwnerPasswordReplacement = false,
        bool useEnvironmentalAcquisition = false,
        Func<string, Task>? prepareRoot = null,
        bool useTransientEvidence = false,
        string ownerPassword = OwnerPassword,
        string? scratchRoot = null,
        bool preserveRoot = false,
        string? productionCatalogRoot = null,
        IReadOnlyDictionary<string, string?>? settingsOverrides = null,
        bool reloadConfigurationOnChange = true)
    {
        var temporaryRoot = useCalibrationLibrary && Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        var rootName = scratchRoot is null ? $"hvo-cameraagent-browser-{Guid.NewGuid():N}" : $"iv-{Guid.NewGuid():N}";
        var root = Path.Combine(scratchRoot ?? temporaryRoot, rootName);
        Directory.CreateDirectory(root);
        var catalog = CatalogFixtureInstallation.Create(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"),
            Path.Combine(root, "catalog"));
        var configPath = Path.Combine(root, "cameraagent.browser.json");
        var template = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.browser.json")).ConfigureAwait(false);
        var configured = template.Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(root), StringComparison.Ordinal);
        if (useCalibrationLibrary)
        {
            var document = JsonNode.Parse(configured)?.AsObject()
                ?? throw new InvalidDataException("The browser calibration fixture configuration is invalid.");
            document["module"] = JsonNode.Parse("""
                {
                  "type": "VirtualSky",
                  "options": { "seed": 208, "maximumResults": 10 }
                }
                """);
            var sensor = document["rig"]!["sensor"]!.AsObject();
            sensor["name"] = "BrowserFixtureMono16";
            sensor["pixelFormat"] = "Mono16";
            sensor["strideBytes"] = 320;
            sensor["sensorRecipeVersion"] = "browser-fixture-mono16-v1";
            document["rig"]!["pipeline"]!["captureInterval"] = "00:00:10";
            document["rig"]!["readout"] = JsonNode.Parse("""
                {
                  "roi": { "x": 0, "y": 0, "width": 160, "height": 120 },
                  "binX": 1,
                  "binY": 1,
                  "binningAlgorithm": "IdentityV1",
                  "pixelFormat": "Mono16",
                  "sampleDepthBits": 16,
                  "containerDepthBits": 16,
                  "packing": "ByteAligned",
                  "storedCodeTransform": "RightAlignedV1",
                  "levelCodeSpace": "NativeSample",
                  "blackLevel": 0,
                  "whiteLevel": 65535,
                  "strideBytes": 320,
                  "byteOrder": "LittleEndian",
                  "cfaPattern": "None"
                }
                """);
            configured = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        await File.WriteAllTextAsync(configPath, configured).ConfigureAwait(false);
        var provisioningRoot = Path.Combine(root, "provisioning");

        var overrides = new Dictionary<string, string?>
        {
            ["LocalIdentity:AdminEmail"] = OwnerEmail,
            ["LocalIdentity:AdminPassword"] = ownerPassword,
            ["LocalIdentity:AdminPasswordFile"] = string.Empty,
            ["LocalIdentity:AllowMissingAdminPassword"] = "false",
            ["LocalIdentity:DatabasePath"] = Path.Combine(root, "identity", "cameraagent_identity.db"),
            ["LocalIdentity:CookieName"] = "CameraAgent.Browser106.Auth",
            ["InstallationVerification:Token"] = InstallationVerificationToken,
            ["LifecycleControl:Token"] = LifecycleControlToken,
            ["Catalog:Root"] = catalog.Root,
            ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
            ["Catalog:RequiredPackageKind"] = "Fixture",
            ["CameraAgent:ConfigFilePath"] = configPath,
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:TransientDetection:Mode"] = useTransientEvidence ? "Edge" : "Off",
            ["DeviceProvisioning:StateDirectory"] = provisioningRoot,
            ["Logging:LogLevel:Default"] = "Warning",
            ["Serilog:MinimumLevel:Default"] = "Warning"
        };

        if (settingsOverrides is not null)
            foreach (var setting in settingsOverrides) overrides[setting.Key] = setting.Value;

        if (productionCatalogRoot is not null)
        {
            overrides["Catalog:Root"] = productionCatalogRoot;
            overrides["Catalog:RequiredCatalogId"] = "hyg-v42-production";
            overrides["Catalog:RequiredPackageKind"] = "Production";
        }

        if (useEnvironmentalAcquisition)
        {
            // One on-demand-capable source, so the closed local automation registry publishes a target.
            // Every browser test builds its own root, config, and host, so this affects only its caller.
            var environmentalSettingsPath = Path.Combine(root, "cameraagent.browser.environmental.json");
            await WriteEnvironmentalSettingsAsync(environmentalSettingsPath).ConfigureAwait(false);
            overrides[EnvironmentalSettingsKey] = environmentalSettingsPath;
        }

        var fixture = new CameraAgentKestrelFixture(root, catalog, overrides, configureServices)
        {
            _preserveRoot = preserveRoot,
            _reloadConfigurationOnChange = reloadConfigurationOnChange
        };
        try
        {
            if (prepareRoot is not null) await prepareRoot(root).ConfigureAwait(false);
            await fixture.StartHostAsync().ConfigureAwait(false);
            if (!requireOwnerPasswordReplacement)
            {
                await CompleteOwnerBootstrapForExistingAcceptanceTestsAsync(fixture.Services).ConfigureAwait(false);
            }
            await SeedNonOwnerAsync(fixture.Services).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RestartWithoutPasswordAuthorityAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        _overrides["LocalIdentity:AdminPassword"] = string.Empty;
        _overrides["LocalIdentity:AdminPasswordFile"] = string.Empty;
        _overrides["LocalIdentity:AllowMissingAdminPassword"] = "true";
        await StartHostAsync().ConfigureAwait(false);
    }

    internal async Task RunWithHostStoppedAsync(Func<string, Task> action)
    {
        await StopHostAsync().ConfigureAwait(false);
        await action(_root).ConfigureAwait(false);
        await StartHostAsync().ConfigureAwait(false);
    }

    internal async Task RestartAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        await StartHostAsync().ConfigureAwait(false);
    }

    private async Task StartHostAsync()
    {
        var factory = new BrowserWebApplicationFactory(_root, _overrides, _configureServices, _reloadConfigurationOnChange);
        factory.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _restartPort);
            options.ListenUnixSocket(RecoverySocketPath, listen => listen.Protocols = HttpProtocols.Http1);
        });
        _factory = factory;
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var server = factory.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault(candidate =>
                Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Host == IPAddress.Loopback.ToString())
                ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.");
            client.BaseAddress = new Uri(address, UriKind.Absolute);
            _lifetimeClient = client;
        }
        catch
        {
            await StopHostAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopHostAsync()
    {
        _lifetimeClient?.Dispose();
        _lifetimeClient = null;
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
            _factory = null;
        }
        SqliteConnection.ClearAllPools();
    }

    private static Guid CreateDeterministicGuid(int index, byte kind)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, index + 1);
        bytes[4] = kind;
        bytes[15] = 106;
        return new Guid(bytes);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient takes ownership of its handler and the caller owns the returned client.")]
    internal async Task<HttpClient> CreateOwnerClientAsync(string password = OwnerPassword, string signInName = OwnerEmail)
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
        var succeeded = false;
        try
        {
            using var login = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
            login.EnsureSuccessStatusCode();
            var token = OwnerBootstrapSession.ExtractAntiforgeryToken(
                await login.Content.ReadAsStringAsync().ConfigureAwait(false),
                "in-process fixture owner login form");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Email"] = signInName,
                ["Input.Password"] = password,
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            succeeded = true;
            return client;
        }
        finally
        {
            if (!succeeded)
            {
                client.Dispose();
            }
        }
    }

    private static async Task SeedNonOwnerAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(NonOwnerEmail).ConfigureAwait(false) is not null)
        {
            return;
        }

        var result = await users.CreateAsync(new ApplicationUser
        {
            UserName = NonOwnerEmail,
            Email = NonOwnerEmail,
            EmailConfirmed = true,
            IsSiteOwner = false
        }, NonOwnerPassword).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not seed the browser non-owner: {string.Join(", ", result.Errors.Select(static error => error.Code))}");
        }
    }

    private static async Task CompleteOwnerBootstrapForExistingAcceptanceTestsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByEmailAsync(OwnerEmail).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The browser acceptance owner was not seeded.");
        owner.PasswordChangeRequired = false;
        var result = await users.UpdateAsync(owner).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not prepare the browser acceptance owner: {string.Join(", ", result.Errors.Select(static error => error.Code))}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        if (!_preserveRoot)
        {
            _catalog.Dispose();
            await DeleteWithRetriesAsync(_root).ConfigureAwait(false);
        }
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

    private sealed class BrowserWebApplicationFactory(
        string root,
        IReadOnlyDictionary<string, string?> overrides,
        Action<IServiceCollection>? configureServices,
        bool reloadConfigurationOnChange) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(DeploymentLocalSettings.PathVariable, Path.Combine(root, DeploymentLocalSettings.FileName));
            if (!reloadConfigurationOnChange)
            {
                // The default app configuration's reload-on-change watchers are not released when a test host is
                // disposed; each keeps an inotify instance until the test process exits. A host that never reloads
                // its settings opts out here. Host settings are read before Program builds its configuration, so
                // this arrives as a host setting like the environment. Every path stays in the fixture root.
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            }
            // Program captures local Identity settings before WebApplicationFactory app overrides are applied.
            foreach (var setting in overrides.Where(static setting =>
                         setting.Key.StartsWith("LocalIdentity:", StringComparison.Ordinal) ||
                         string.Equals(setting.Key, "LifecycleControl:Token", StringComparison.Ordinal)))
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(overrides);
                // Environmental sources carry an opaque options element, which flat key-value overrides
                // cannot express, so the opt-in settings arrive as a JSON file.
                if (overrides.TryGetValue(EnvironmentalSettingsKey, out var environmentalSettings) &&
                    !string.IsNullOrEmpty(environmentalSettings))
                {
                    configuration.AddJsonFile(environmentalSettings, optional: false, reloadOnChange: false);
                }
            });
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection()
                    .SetApplicationName("HVO.SkyMonitor.CameraAgent.AcceptanceTests")
                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "dataprotection")));
                services.RemoveAll<ICameraAgentStorageResolver>();
                services.AddSingleton<ICameraAgentStorageResolver>(
                    new FixedStorageResolver(new CameraAgentStorageLocation("raw-ingress", root)));
                configureServices?.Invoke(services);
            });
        }
    }

    private static async Task WriteEnvironmentalSettingsAsync(string path)
    {
        var settings = new JsonObject
        {
            ["CameraAgent"] = new JsonObject
            {
                ["EnvironmentalAcquisition"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["MaximumConcurrency"] = 2,
                    ["QueueCapacity"] = 32,
                    ["Sources"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["Id"] = EnvironmentalSourceId,
                            ["Type"] = "VirtualEnvironment",
                            ["Kind"] = "AirTemperature",
                            ["Required"] = false,
                            ["Triggers"] = new JsonArray("Periodic", "OnDemand"),
                            ["ScheduleEpochUtc"] = "2026-01-15T08:00:00Z",
                            ["PeriodSeconds"] = 3600,
                            ["EveryNthCapture"] = 3,
                            ["ValidForSeconds"] = 120,
                            ["StaleAfterSeconds"] = 45,
                            ["Options"] = new JsonObject
                            {
                                ["Seed"] = 209,
                                ["EpochUtc"] = "2026-01-15T08:00:00Z",
                                ["NumericValue"] = 12.5,
                                ["NoiseAmplitude"] = 0.25,
                                ["Uncertainty"] = 0.1,
                                ["Quality"] = "Good",
                                ["Mode"] = "Normal",
                                ["DelayMilliseconds"] = 0,
                                ["AlgorithmVersion"] = "virtual-environment-source-v1"
                            }
                        }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(
            path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private sealed class FixedStorageResolver(CameraAgentStorageLocation location) : ICameraAgentStorageResolver
    {
        public ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetStorageLocationsAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CameraAgentStorageLocation>>([location]);

        public ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(
            string storageAlias,
            CancellationToken cancellationToken) => ValueTask.FromResult<CameraAgentStorageLocation?>(
                storageAlias == location.Alias ? location : null);
    }
}
