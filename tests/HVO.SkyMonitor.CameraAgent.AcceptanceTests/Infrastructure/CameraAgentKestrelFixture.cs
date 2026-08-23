using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Data;
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
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Operations;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed class CameraAgentKestrelFixture : IAsyncDisposable
{
    internal const string OwnerEmail = "owner@cameraagent.browser";
    internal const string OwnerPassword = "BrowserOwner!106";
    internal const string NonOwnerEmail = "viewer@cameraagent.browser";
    internal const string NonOwnerPassword = "BrowserViewer!106";

    private readonly string _root;
    private readonly CatalogFixtureInstallation _catalog;
    private readonly Dictionary<string, string?> _overrides;
    private readonly Action<IServiceCollection>? _configureServices;
    private BrowserWebApplicationFactory? _factory;
    private HttpClient? _lifetimeClient;

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

    internal IServiceProvider Services => (_factory
        ?? throw new InvalidOperationException("The Kestrel fixture is not running.")).Services;

    internal static async Task<CameraAgentKestrelFixture> CreateAsync(
        Action<IServiceCollection>? configureServices = null,
        bool useCalibrationLibrary = false,
        bool requireOwnerPasswordReplacement = false)
    {
        var temporaryRoot = useCalibrationLibrary && Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"hvo-cameraagent-browser-{Guid.NewGuid():N}");
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

        var overrides = new Dictionary<string, string?>
        {
            ["LocalIdentity:AdminEmail"] = OwnerEmail,
            ["LocalIdentity:AdminPassword"] = OwnerPassword,
            ["LocalIdentity:AdminPasswordFile"] = string.Empty,
            ["LocalIdentity:AllowMissingAdminPassword"] = "false",
            ["LocalIdentity:DatabasePath"] = Path.Combine(root, "identity", "cameraagent_identity.db"),
            ["LocalIdentity:CookieName"] = "CameraAgent.Browser106.Auth",
            ["Catalog:Root"] = catalog.Root,
            ["Catalog:RequiredPackageKind"] = "Fixture",
            ["CameraAgent:ConfigFilePath"] = configPath,
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
            ["CameraAgent:EnvironmentalDelivery:Enabled"] = "false",
            ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
            ["CameraAgent:TransientDetection:Mode"] = "Off",
            ["DeviceProvisioning:StateDirectory"] = Path.Combine(root, "provisioning"),
            ["CentralIdentity:ServiceUrl"] = "http://127.0.0.1:1/",
            ["SkyMonitor:BaseUrl"] = "http://127.0.0.1:1",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Serilog:MinimumLevel:Default"] = "Warning"
        };

        var fixture = new CameraAgentKestrelFixture(root, catalog, overrides, configureServices);
        try
        {
            await fixture.StartHostAsync().ConfigureAwait(false);
            if (!requireOwnerPasswordReplacement)
            {
                await CompleteOwnerBootstrapForExistingAcceptanceTestsAsync(fixture.Services).ConfigureAwait(false);
            }
            await SeedNonOwnerAsync(fixture.Services).ConfigureAwait(false);
            await SeedArtifactQuarantineAsync(fixture.Services, root).ConfigureAwait(false);
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

    internal async Task RestartAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        await StartHostAsync().ConfigureAwait(false);
    }

    private async Task StartHostAsync()
    {
        var factory = new BrowserWebApplicationFactory(_root, _overrides, _configureServices);
        factory.UseKestrel(0);
        _factory = factory;
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

    private static async Task SeedArtifactQuarantineAsync(IServiceProvider services, string root)
    {
        var outbox = services.GetRequiredService<IArtifactOutbox>();
        var recipe = RecipeIdentityDescriptor.Create(
            "browser-quarantine",
            "1.0.0",
            "issue-106-v1",
            JsonSerializer.SerializeToElement(new { }));
        var profileHash = new string('A', 64);
        for (var index = 0; index < 30; index++)
        {
            var payload = new byte[] { (byte)index, 1, 2, 3 };
            var artifactId = CreateDeterministicGuid(index, 1);
            var captureId = CreateDeterministicGuid(index, 2);
            var relativePath = $"browser-quarantine/{artifactId:N}.bin";
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, payload).ConfigureAwait(false);
            var now = new DateTimeOffset(2026, 7, 23, 0, 0, index, TimeSpan.Zero);
            var descriptor = new ReconstructionDescriptor(
                new CaptureIdentityDescriptor("browser-fixture", "rig-v1", index + 1, captureId),
                new CaptureTimingDescriptor(now, now, now, now, now),
                new CaptureControlDescriptor(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, 1, null, null, null, null),
                new CaptureProfileSet(
                    new("rig", "1", profileHash),
                    new("calibration", "1", profileHash),
                    new("mask", "1", profileHash),
                    new("sensor", "1", profileHash),
                    new("processing", "1", profileHash)),
                new FrameLayoutDescriptor(
                    2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable,
                    8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, payload.LongLength),
                new ArtifactDescriptor(
                    artifactId,
                    FrameArtifactRole.Raw,
                    "browser-fixture",
                    "quarantine",
                    now,
                    [],
                    recipe,
                    "application/octet-stream",
                    PayloadChecksum.ComputeSha256(payload)));
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
            await File.WriteAllBytesAsync(
                Path.ChangeExtension(path, ".json"),
                CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        for (var index = 0; index < 30; index++)
        {
            var lease = await outbox.ClaimAsync(
                root, "browser-fixture", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            if (lease is null)
            {
                throw new InvalidOperationException("Could not seed deterministic artifact quarantine.");
            }
            await outbox.QuarantineAsync(
                root, lease, "browser-fixture", CancellationToken.None).ConfigureAwait(false);
        }
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
    internal async Task<HttpClient> CreateOwnerClientAsync(string password = OwnerPassword)
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
            var html = await login.Content.ReadAsStringAsync().ConfigureAwait(false);
            var match = Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidOperationException("The local login antiforgery token was not rendered.");
            }
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["Input.Email"] = OwnerEmail,
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
        _catalog.Dispose();
        await DeleteWithRetriesAsync(_root).ConfigureAwait(false);
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
        Action<IServiceCollection>? configureServices) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program captures local Identity settings before WebApplicationFactory app overrides are applied.
            foreach (var setting in overrides.Where(static setting =>
                         setting.Key.StartsWith("LocalIdentity:", StringComparison.Ordinal)))
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(overrides));
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

    private sealed class FixedStorageResolver(CameraAgentStorageLocation location) : ICameraAgentStorageResolver
    {
        public ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetUploadLocationsAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CameraAgentStorageLocation>>([location]);

        public ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(
            string storageAlias,
            CancellationToken cancellationToken) => ValueTask.FromResult<CameraAgentStorageLocation?>(
                storageAlias == location.Alias ? location : null);
    }
}
