using System.Text.Json;
using HVO.SkyMonitor.CameraAgent;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>
/// Boots one CameraAgent with no LogicHost or external service dependencies.
/// </summary>
internal sealed class CameraAgentIntegrationFixture : IDisposable
{
    private WebApplicationFactory<Program>? _agentFactory;
    private WebApplicationFactory<Program>? _agentBaseFactory;
    private string? _configurationPath;
    private string? _storageRoot;
    private CatalogFixtureInstallation? _catalogFixture;

    public async Task InitializeAsync()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), $"hvo-cameraagent-standalone-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        var provisioningRoot = Path.Combine(_storageRoot, "provisioning");
        Directory.CreateDirectory(provisioningRoot);
        await File.WriteAllTextAsync(
            Path.Combine(provisioningRoot, "device-identity.json"),
            JsonSerializer.Serialize(new
            {
                deviceId = "cameraagent-integration-test",
                verificationCode = "INTEG2TEST",
                createdUtc = DateTimeOffset.UtcNow
            })).ConfigureAwait(false);
        _catalogFixture = CatalogFixtureInstallation.Create(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"));
        _configurationPath = Path.Combine(_storageRoot, "cameraagent.integration.json");
        var transientEpochUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToUniversalTime();
        var template = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.integration.json")).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            _configurationPath,
            template
                .Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(_storageRoot), StringComparison.Ordinal)
                .Replace("__TRANSIENT_EPOCH_UTC__", JsonSerializer.Serialize(transientEpochUtc), StringComparison.Ordinal))
            .ConfigureAwait(false);

        _agentBaseFactory = new WebApplicationFactory<Program>();
        _agentFactory = _agentBaseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            var overrides = BuildConfigurationOverrides(provisioningRoot);
            foreach (var setting in overrides.Where(static setting =>
                         setting.Key.StartsWith("LocalIdentity:", StringComparison.Ordinal)))
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides!));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = IntegrationUserAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = IntegrationUserAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = IntegrationUserAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationUserAuthenticationHandler>(
                        IntegrationUserAuthenticationHandler.SchemeName,
                        _ => { });

                var drainService = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(ArtifactOutboxDrainService));
                if (drainService is not null)
                {
                    services.Remove(drainService);
                }
            });
        });

        using var scope = _agentFactory.Services.CreateScope();
        var ownerManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var owner = await ownerManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false)
            ?? throw new InvalidOperationException("The CameraAgent integration owner was not seeded.");
        owner.PasswordChangeRequired = false;
        var ownerUpdate = await ownerManager.UpdateAsync(owner).ConfigureAwait(false);
        if (!ownerUpdate.Succeeded)
        {
            throw new InvalidOperationException("The CameraAgent integration owner could not be prepared.");
        }
        DeviceId = (await scope.ServiceProvider.GetRequiredService<IDeviceIdentityStore>()
            .GetOrCreateAsync(CancellationToken.None).ConfigureAwait(false)).DeviceId;
    }

    public HttpClient CreateCameraAgentClient()
    {
        EnsureInitialized();
        return _agentFactory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public WebApplicationFactory<Program> CreateCameraAgentFactory(Action<IServiceCollection> configureServices)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        EnsureInitialized();
        return _agentFactory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var hostedService in services
                         .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                         .ToArray())
            {
                services.Remove(hostedService);
            }
            configureServices(services);
        }));
    }

    public IServiceScope CreateCameraAgentScope()
    {
        EnsureInitialized();
        return _agentFactory!.Services.CreateScope();
    }

    public string StorageRoot => _storageRoot
        ?? throw new InvalidOperationException("Fixture has not been initialized.");

    public string DeviceId { get; private set; } = string.Empty;

    public void Dispose()
    {
        _agentFactory?.Dispose();
        _agentBaseFactory?.Dispose();
        _catalogFixture?.Dispose();
        if (_storageRoot is not null && Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private Dictionary<string, string?> BuildConfigurationOverrides(string provisioningRoot) => new(StringComparer.Ordinal)
    {
        ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
        ["CameraAgent:ConfigFilePath"] = _configurationPath,
        ["CameraAgent:RawIngressRoot"] = _storageRoot,
        ["CameraAgent:DiskPressureThresholdPercent"] = "1",
        ["CameraAgent:DiskPressureRecoveryPercent"] = "2",
        ["Catalog:Root"] = _catalogFixture?.Root,
        ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
        ["Catalog:RequiredPackageKind"] = "Fixture",
        ["DeviceProvisioning:StateDirectory"] = provisioningRoot,
        ["LocalIdentity:AdminEmail"] = "owner@cameraagent.integration",
        ["LocalIdentity:AdminPassword"] = "IntegrationOwner!123",
        ["LocalIdentity:AdminPasswordFile"] = string.Empty,
        ["LocalIdentity:AllowMissingAdminPassword"] = "false",
        ["LocalIdentity:DatabasePath"] = Path.Combine(_storageRoot!, "cameraagent_identity.db"),
        ["LocalIdentity:CookieName"] = "CameraAgent.Integration.Auth",
        ["SkyMonitor:BaseUrl"] = "http://127.0.0.1:1"
    };

    private void EnsureInitialized()
    {
        if (_agentFactory is null)
        {
            throw new InvalidOperationException("CameraAgent integration fixture has not been initialized.");
        }
    }
}
