using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.IntegrationTests;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the central SkyMonitor host and a camera agent instance for end-to-end testing.
/// </summary>
internal sealed class CameraAgentIntegrationFixture : IDisposable
{
    private readonly bool _hybridTransientMode;
    private readonly EnvironmentalDeliveryCompletionTracker _environmentalDelivery = new();
    private readonly IntegrationTestFixture _hostFixture = new(new Dictionary<string, string?>
    {
        ["CentralTransient:Mode"] = "Hybrid",
        ["CentralTransient:SourceRole"] = "Raw"
    }, useEphemeralMinioStorage: true);
    private WebApplicationFactory<Program>? _agentFactory;
    private WebApplicationFactory<Program>? _agentBaseFactory;
    private Uri? _centralIdentityBaseUri;
    private Uri? _centralHostBaseUri;
    private JsonWebKeySet? _jwksDocument;
    private string? _configurationPath;
    private string? _storageRoot;
    private CatalogFixtureInstallation? _catalogFixture;

    public CameraAgentIntegrationFixture(bool hybridTransientMode = false)
    {
        _hybridTransientMode = hybridTransientMode;
    }

    /// <summary>
    /// Initializes the host and camera agent factories.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _hostFixture.InitializeAsync().ConfigureAwait(false);
        await _hostFixture.SeedActiveDeviceAsync("cameraagent-integration-test").ConfigureAwait(false);
        _storageRoot = Path.Combine(Path.GetTempPath(), $"hvo-cameraagent-integration-{Guid.NewGuid():N}");
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
        var template = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.integration.json")).ConfigureAwait(false);
        if (_hybridTransientMode)
        {
            template = template
                .Replace("3600.0", "0.5", StringComparison.Ordinal)
                .Replace("\"angularWidthDegrees\": 0.2", "\"angularWidthDegrees\": 2.0", StringComparison.Ordinal)
                .Replace("\"pixelX\": 1.5,\n                \"pixelY\": 1.5", "\"pixelX\": 20.0,\n                \"pixelY\": 24.0", StringComparison.Ordinal)
                .Replace("\"pixelX\": 20.0,\n                \"pixelY\": 24.0,\n                \"electronsPerSecond\": 1000000000.0,\n                \"sigmaPixels\": 0.25\n              }\n            ]", "\"pixelX\": 44.0,\n                \"pixelY\": 24.0,\n                \"electronsPerSecond\": 1000000000.0,\n                \"sigmaPixels\": 2.0\n              }\n            ]", StringComparison.Ordinal)
                .Replace("\"sigmaPixels\": 0.25", "\"sigmaPixels\": 2.0", StringComparison.Ordinal);
        }
        using var hostClient = _hostFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        _centralHostBaseUri = hostClient.BaseAddress ?? new Uri("http://127.0.0.1");

        var metadataEndpoint = new Uri("/.well-known/openid-configuration", UriKind.Relative);
        var metadataResponse = await hostClient.GetStringAsync(metadataEndpoint).ConfigureAwait(false);
        var metadata = ParseMetadata(metadataResponse);
        _centralIdentityBaseUri = metadata.Issuer ?? _centralHostBaseUri;
        if (metadata.JwksUri is not null)
        {
            var jwksJson = await hostClient.GetStringAsync(metadata.JwksUri).ConfigureAwait(false);
            _jwksDocument = new JsonWebKeySet(jwksJson);
        }

        TransientEpochUtc = (_hybridTransientMode
            ? DateTimeOffset.UtcNow.AddSeconds(10)
            : DateTimeOffset.UtcNow.AddMinutes(-1)).ToUniversalTime();
        await File.WriteAllTextAsync(_configurationPath,
            template
                .Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(_storageRoot), StringComparison.Ordinal)
                .Replace("__TRANSIENT_EPOCH_UTC__", JsonSerializer.Serialize(TransientEpochUtc), StringComparison.Ordinal))
            .ConfigureAwait(false);

        _agentBaseFactory = new WebApplicationFactory<Program>();
        _agentFactory = _agentBaseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                var overrides = BuildConfigurationOverrides();
                // Program captures local Identity settings before WebApplicationFactory app overrides are applied.
                foreach (var setting in overrides.Where(static setting =>
                             setting.Key.StartsWith("LocalIdentity:", StringComparison.Ordinal)))
                {
                    builder.UseSetting(setting.Key, setting.Value);
                }
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(overrides!));
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

                    if (!_hybridTransientMode)
                    {
                        var drainService = services.Single(descriptor =>
                            descriptor.ServiceType == typeof(IHostedService) &&
                            descriptor.ImplementationType == typeof(ArtifactOutboxDrainService));
                        services.Remove(drainService);
                    }

                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.BackchannelHttpHandler = _hostFixture.Factory.Server.CreateHandler();
                        if (_jwksDocument is not null)
                        {
                            options.TokenValidationParameters.IssuerSigningKeys = _jwksDocument.GetSigningKeys();
                        }
                    });

                    services.AddHttpClient(SkyMonitorClientOptions.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(_ => new EnvironmentalDeliveryTrackingHandler(
                            _environmentalDelivery,
                            _hostFixture.Factory.Server.CreateHandler()));

                    services.AddHttpClient(CentralAuthenticationService.TokenClientName)
                        .ConfigurePrimaryHttpMessageHandler(_ => _hostFixture.Factory.Server.CreateHandler());
                });
            });

        using var scope = _agentFactory.Services.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var ownerManager = scopedProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var owner = await ownerManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false)
            ?? throw new InvalidOperationException("The CameraAgent integration owner was not seeded.");
        owner.PasswordChangeRequired = false;
        var ownerUpdate = await ownerManager.UpdateAsync(owner).ConfigureAwait(false);
        if (!ownerUpdate.Succeeded)
        {
            throw new InvalidOperationException("The CameraAgent integration owner could not be prepared.");
        }
        var identity = await scopedProvider.GetRequiredService<IDeviceIdentityStore>()
            .GetOrCreateAsync(CancellationToken.None).ConfigureAwait(false);
        await _hostFixture.SeedActiveDeviceAsync(identity.DeviceId).ConfigureAwait(false);
        var activeDevice = await _hostFixture.GetActiveDeviceAsync(identity.DeviceId).ConfigureAwait(false);
        var centralIdentity = new CentralIdentityOptions
        {
            ServiceUrl = CentralIdentityBaseUri,
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = TestClients.SystemCameraAgent.ClientId,
                ClientSecret = TestClients.SystemCameraAgent.ClientSecret
            }
        };
        foreach (var requestedScope in TestClients.SystemCameraAgent.Scopes)
        {
            centralIdentity.ClientCredentials.Scopes.Add(requestedScope);
        }
        var cameraConfiguration = await scopedProvider.GetRequiredService<ICameraAgentConfigurationLoader>()
            .LoadAsync(CancellationToken.None).ConfigureAwait(false);
        await _hostFixture.SeedRigProfileAsync("cameraagent-integration-test", cameraConfiguration.Rig).ConfigureAwait(false);
        await scopedProvider.GetRequiredService<IDeviceSecretStore>().SaveAsync(new DeviceSecrets(
            activeDevice.DevicePublicId,
            activeDevice.ObservatoryId,
            "CameraAgent Integration Device",
            "integration-registration-token",
            "/api/device/heartbeat",
            "/api/device/upload",
            60,
            activeDevice.IssuedAtUtc,
            activeDevice.ExpiresAtUtc,
            "cameraagent-integration-key",
            centralIdentity), CancellationToken.None).ConfigureAwait(false);
        DeviceId = identity.DeviceId;
        DevicePublicId = activeDevice.DevicePublicId;
        ObservatoryId = activeDevice.ObservatoryId;
        var configuration = scopedProvider.GetRequiredService<IConfiguration>();
        var configuredServiceUrl = configuration["CentralIdentity:ServiceUrl"];
        var jwtOptions = scopedProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Creates an HTTP client for the central SkyMonitor host.
    /// </summary>
    public HttpClient CreateHostClient()
    {
        EnsureInitialized();
        return _hostFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    /// <summary>
    /// Creates an HTTP client pointed at the camera agent.
    /// </summary>
    public HttpClient CreateCameraAgentClient()
    {
        EnsureInitialized();
        return _agentFactory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public WebApplicationFactory<Program> CreateCameraAgentFactory(
        Action<IServiceCollection> configureServices)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        EnsureInitialized();
        return _agentFactory!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
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

    /// <summary>
    /// Creates a scoped service provider from the camera agent factory.
    /// Caller is responsible for disposing the returned scope.
    /// </summary>
    public IServiceScope CreateCameraAgentScope()
    {
        EnsureInitialized();
        return _agentFactory!.Services.CreateScope();
    }

    public IServiceScope CreateHostScope()
    {
        EnsureInitialized();
        return _hostFixture.Factory.Services.CreateScope();
    }

    /// <summary>
    /// Gets the base URI for the central identity service.
    /// </summary>
    public Uri CentralIdentityBaseUri => _centralIdentityBaseUri ?? throw new InvalidOperationException("Fixture has not been initialized.");

    public string StorageRoot => _storageRoot ?? throw new InvalidOperationException("Fixture has not been initialized.");

    public string DeviceId { get; private set; } = string.Empty;

    public Guid DevicePublicId { get; private set; }

    public Guid ObservatoryId { get; private set; }

    public DateTimeOffset TransientEpochUtc { get; private set; }

    public Task<int> CountEnvironmentalObservationsAsync(Guid observationId)
        => _hostFixture.CountEnvironmentalObservationsAsync(observationId);

    public Task WaitForEnvironmentalDeliveryAsync(Guid observationId, CancellationToken cancellationToken)
        => _environmentalDelivery.WaitAsync(observationId, cancellationToken);

    public void Dispose()
    {
        _agentFactory?.Dispose();
        _agentBaseFactory?.Dispose();
        _catalogFixture?.Dispose();
        _hostFixture.Dispose();
        if (_storageRoot is not null && Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private sealed class EnvironmentalDeliveryCompletionTracker
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _pending = new();

        public async Task WaitAsync(Guid observationId, CancellationToken cancellationToken)
        {
            var completion = _pending.GetOrAdd(
                observationId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            try
            {
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(new KeyValuePair<Guid, TaskCompletionSource>(observationId, completion));
            }
        }

        public void Complete(Guid observationId)
        {
            if (_pending.TryRemove(observationId, out var completion))
            {
                completion.TrySetResult();
            }
        }
    }

    private sealed class EnvironmentalDeliveryTrackingHandler(
        EnvironmentalDeliveryCompletionTracker tracker,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Guid? observationId = null;
            if (request.RequestUri?.AbsolutePath == "/api/device/environmental-observations" && request.Content is not null)
            {
                var body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                observationId = EnvironmentalObservationDeliveryJson.ParseEnvelope(body).Value?.Observation.ObservationId;
            }
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (observationId is { } completed && response.IsSuccessStatusCode)
            {
                tracker.Complete(completed);
            }
            return response;
        }
    }

    private Dictionary<string, string?> BuildConfigurationOverrides()
    {
        var identityBase = CentralIdentityBaseUri.ToString().TrimEnd('/') + "/";
        var apiBase = (_centralHostBaseUri ?? CentralIdentityBaseUri).ToString().TrimEnd('/');
        var overrides = new Dictionary<string, string?>
        {
            ["CentralIdentity:ServiceUrl"] = identityBase,
            ["CentralIdentity:Mode"] = "ClientCredentials",
            ["CentralIdentity:ClientCredentials:ClientId"] = TestClients.SystemCameraAgent.ClientId,
            ["CentralIdentity:ClientCredentials:ClientSecret"] = TestClients.SystemCameraAgent.ClientSecret,
            ["LocalIdentity:AdminEmail"] = "owner@cameraagent.integration",
            ["LocalIdentity:AdminPassword"] = "IntegrationOwner!123",
            ["LocalIdentity:AdminPasswordFile"] = string.Empty,
            ["LocalIdentity:AllowMissingAdminPassword"] = "false",
            ["LocalIdentity:DatabasePath"] = Path.Combine(_storageRoot!, "cameraagent_identity.db"),
            ["LocalIdentity:CookieName"] = "CameraAgent.Integration.Auth",
            ["SkyMonitor:BaseUrl"] = apiBase,
            ["Catalog:Root"] = _catalogFixture?.Root,
            ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
            ["Catalog:RequiredPackageKind"] = "Fixture",
            ["CameraAgent:ConfigFilePath"] = _configurationPath,
            ["CameraAgent:RawIngressRoot"] = _storageRoot,
            ["CameraAgent:DiskPressureThresholdPercent"] = "1",
            ["CameraAgent:DiskPressureRecoveryPercent"] = "2",
            ["DeviceProvisioning:StateDirectory"] = Path.Combine(_storageRoot!, "provisioning")
        };
        if (_hybridTransientMode)
        {
            overrides["CameraAgent:TransientDetection:Mode"] = TransientOperatingMode.Hybrid.ToString();
            overrides["CameraAgent:TransientDetection:Required"] = "true";
            overrides["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100";
            overrides["CameraAgent:TransientDetection:StarMaximumMagnitude"] = "-30";
        }

        var scopePrefix = "CentralIdentity:ClientCredentials:Scopes";
        for (var i = 0; i < TestClients.SystemCameraAgent.Scopes.Length; i++)
        {
            overrides[$"{scopePrefix}:{i}"] = TestClients.SystemCameraAgent.Scopes[i];
        }

        return overrides;
    }

    private void EnsureInitialized()
    {
        if (_agentFactory is null || _centralIdentityBaseUri is null)
        {
            throw new InvalidOperationException("Camera agent integration fixture has not been initialized.");
        }
    }

    private static IdentityMetadata ParseMetadata(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return new IdentityMetadata();
        }

        using var document = JsonDocument.Parse(metadata);
        Uri? issuer = null;
        Uri? jwks = null;

        if (document.RootElement.TryGetProperty("issuer", out var issuerProperty))
        {
            var issuerValue = issuerProperty.GetString();
            if (!string.IsNullOrWhiteSpace(issuerValue))
            {
                issuer = new Uri(issuerValue, UriKind.Absolute);
            }
        }

        if (document.RootElement.TryGetProperty("jwks_uri", out var jwksProperty))
        {
            var jwksValue = jwksProperty.GetString();
            if (!string.IsNullOrWhiteSpace(jwksValue))
            {
                jwks = new Uri(jwksValue, UriKind.Absolute);
            }
        }

        return new IdentityMetadata
        {
            Issuer = issuer,
            JwksUri = jwks
        };
    }

    private sealed class IdentityMetadata
    {
        public Uri? Issuer { get; init; }

        public Uri? JwksUri { get; init; }
    }
}
