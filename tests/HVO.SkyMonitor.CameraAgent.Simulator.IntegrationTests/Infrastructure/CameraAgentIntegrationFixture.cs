using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Simulator;
using HVO.SkyMonitor.IntegrationTests;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HVO.SkyMonitor.CameraAgent.Simulator.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the central SkyMonitor host and a simulator camera agent instance for end-to-end testing.
/// </summary>
internal sealed class CameraAgentIntegrationFixture : IDisposable
{
    private readonly IntegrationTestFixture _hostFixture = new();
    private WebApplicationFactory<Program>? _agentFactory;
    private WebApplicationFactory<Program>? _agentBaseFactory;
    private Uri? _centralIdentityBaseUri;
    private Uri? _centralHostBaseUri;
    private JsonWebKeySet? _jwksDocument;

    /// <summary>
    /// Initializes the host and camera agent factories.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _hostFixture.InitializeAsync().ConfigureAwait(false);

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

        _agentBaseFactory = new WebApplicationFactory<Program>();
        _agentFactory = _agentBaseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var overrides = BuildConfigurationOverrides();
                    config.AddInMemoryCollection(overrides!);
                });
                builder.ConfigureTestServices(services =>
                {
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.BackchannelHttpHandler = _hostFixture.Factory.Server.CreateHandler();
                        if (_jwksDocument is not null)
                        {
                            options.TokenValidationParameters.IssuerSigningKeys = _jwksDocument.GetSigningKeys();
                        }
                    });

                    services.AddHttpClient(SkyMonitorClientOptions.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(_ => _hostFixture.Factory.Server.CreateHandler());

                    services.AddHttpClient(CentralAuthenticationService.TokenClientName)
                        .ConfigurePrimaryHttpMessageHandler(_ => _hostFixture.Factory.Server.CreateHandler());
                });
            });

        using var scope = _agentFactory.Services.CreateScope();
        var scopedProvider = scope.ServiceProvider;
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
    /// Creates an HTTP client pointed at the simulator camera agent.
    /// </summary>
    public HttpClient CreateCameraAgentClient()
    {
        EnsureInitialized();
        return _agentFactory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
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

    /// <summary>
    /// Gets the base URI for the central identity service.
    /// </summary>
    public Uri CentralIdentityBaseUri => _centralIdentityBaseUri ?? throw new InvalidOperationException("Fixture has not been initialized.");

    public void Dispose()
    {
        _agentFactory?.Dispose();
        _agentBaseFactory?.Dispose();
        _hostFixture.Dispose();
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
            ["SkyMonitor:BaseUrl"] = apiBase
        };

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
