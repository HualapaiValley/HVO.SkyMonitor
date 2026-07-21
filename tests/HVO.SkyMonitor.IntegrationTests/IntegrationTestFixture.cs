using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.AgentCore;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace HVO.SkyMonitor.IntegrationTests;

using Program = HVO.SkyMonitor.LogicHost.Program;

/// <summary>
/// Integration test fixture that starts Testcontainers for SQL Server, Redis, and MinIO.
/// Provides a WebApplicationFactory for hosting the HVO.SkyMonitor application in-process.
/// </summary>
public sealed class IntegrationTestFixture : IDisposable
{
    private const string SqlServerPassword = "SkyMonitor_test_password1!";
    private readonly IReadOnlyDictionary<string, string?> _configurationOverrides;
    private readonly int _minioHostPort = GetFreeTcpPort();
    private MsSqlContainer? _sqlServerContainer;
    private RedisContainer? _redisContainer;
    private IContainer? _minioContainer;
    private IContainer? _smtpContainer;
    private CatalogFixtureInstallation? _catalogFixture;
    private bool _initialized;
    private string? _originalSqlServerConnectionString;
    private string? _originalDefaultConnectionString;
    private string _redisHost = "127.0.0.1";
    private string _minioHost = "127.0.0.1";
    private string _smtpHost = "127.0.0.1";

    /// <summary>
    /// Gets the web application factory for creating HTTP clients.
    /// </summary>
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>
    /// Gets the SQL Server connection string.
    /// </summary>
    public string SqlServerConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the Redis connection string.
    /// </summary>
    public string RedisConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the MinIO endpoint.
    /// </summary>
    public string MinioEndpoint { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the SMTP HTTP endpoint (Mailpit UI/API).
    /// </summary>
    public string SmtpHttpEndpoint { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the MinIO access key.
    /// </summary>
    public const string MinioAccessKey = "minioadmin";

    /// <summary>
    /// Gets the MinIO secret key.
    /// </summary>
    public const string MinioSecretKey = "minioadmin";

    public IntegrationTestFixture(IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        _configurationOverrides = configurationOverrides ?? new Dictionary<string, string?>();
    }

    public async Task SeedActiveDeviceAsync(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.DeviceRegistrations.AnyAsync(
            registration => registration.DeviceId == deviceId).ConfigureAwait(false))
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var observatory = new Observatory
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "integration-tests",
            Name = "CameraAgent Integration Observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = now,
            IsActive = true
        };
        db.Observatories.Add(observatory);
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ObservatoryId = observatory.Id,
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            FriendlyName = "CameraAgent Integration Device",
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = "Integration Tests",
            OwnerConfirmationMethod = "SelfAttested",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
            ActivatedAtUtc = now,
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256("cameraagent-integration-key")
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task<ActiveDeviceFixture> GetActiveDeviceAsync(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DeviceRegistrations
            .AsNoTracking()
            .Where(registration => registration.DeviceId == deviceId && registration.Status == DeviceRegistrationStatus.Active)
            .Select(registration => new ActiveDeviceFixture(
                registration.Id,
                registration.DevicePublicId!.Value,
                registration.ObservatoryId,
                registration.IssuedAtUtc,
                registration.ExpiresAtUtc!.Value))
            .SingleAsync().ConfigureAwait(false);
    }

    public async Task<int> CountEnvironmentalObservationsAsync(Guid observationId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.EnvironmentalObservations.CountAsync(observation => observation.ObservationId == observationId)
            .ConfigureAwait(false);
    }

    public async Task SeedRigProfileAsync(string deviceId, CameraRigConfig rig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(rig);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(
            item => item.DeviceId == deviceId).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var profileHash = CameraRigProfileIdentity.ComputeSha256(rig);
        db.DeviceRigProfiles.Add(new DeviceRigProfile
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = registration.ObservatoryId,
            Version = 1,
            ConfigHash = profileHash,
            ConfigJson = JsonSerializer.Serialize(rig),
            ProfileName = "rig",
            ProfileVersion = rig.ProfileVersion,
            ProfileSha256 = profileHash,
            CreatedAtUtc = now,
            EffectiveFromUtc = now
        });
        registration.CurrentRigProfileVersion = 1;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes Testcontainers and the application factory.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _catalogFixture = CatalogFixtureInstallation.Create(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"));

        _sqlServerContainer = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04@sha256:c1aa8afe9b06eab64c9774a4802dcd032205d1be785b1fd51e1c0151e7586b74")
            .WithPassword(SqlServerPassword)
            .Build();

        await _sqlServerContainer.StartAsync().ConfigureAwait(false);
        SqlServerConnectionString = _sqlServerContainer.GetConnectionString();
        _originalSqlServerConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__skymonitordb");
        _originalDefaultConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        Environment.SetEnvironmentVariable("ConnectionStrings__skymonitordb", SqlServerConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", SqlServerConnectionString);

        // Start Redis container (RedisBuilder provides a wait strategy that verifies
        // the server responds to commands, not just that the TCP port is open)
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7.4.9-alpine@sha256:6ab0b6e7381779332f97b8ca76193e45b0756f38d4c0dcda72dbb3c32061ab99")
            .Build();

        await _redisContainer.StartAsync().ConfigureAwait(false);
        _redisHost = _redisContainer.Hostname;
        var redisPort = _redisContainer.GetMappedPublicPort(6379);
        RedisConnectionString = $"{_redisHost}:{redisPort}";

        // Start MinIO container
        _minioContainer = new ContainerBuilder()
            .WithImage("minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e")
            .WithPortBinding(_minioHostPort, 9000)
            .WithEnvironment(new Dictionary<string, string>
            {
                ["MINIO_ROOT_USER"] = MinioAccessKey,
                ["MINIO_ROOT_PASSWORD"] = MinioSecretKey
            })
                .WithCommand("server", "/data", "--console-address", ":9001")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();

        await _minioContainer.StartAsync().ConfigureAwait(false);
        _minioHost = _minioContainer.Hostname;
        var minioPort = _minioHostPort;
        MinioEndpoint = $"{_minioHost}:{minioPort}";

        // Start SMTP (Mailpit) container
        _smtpContainer = new ContainerBuilder()
            .WithImage("axllent/mailpit:v1.30.4@sha256:5a49a77c5bdbe7c5474450b4f46348d09949df3695257729c93a30369382d4f6")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1025))
            .Build();

        await _smtpContainer.StartAsync().ConfigureAwait(false);
        _smtpHost = _smtpContainer.Hostname;
        var smtpPort = _smtpContainer.GetMappedPublicPort(1025);
        var smtpHttpPort = _smtpContainer.GetMappedPublicPort(8025);
        SmtpHttpEndpoint = $"http://{_smtpHost}:{smtpHttpPort}";

        // Create the web application factory
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                builder.ConfigureAppConfiguration((context, config) =>
                {
                    var overrides = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:skymonitordb"] = SqlServerConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = SqlServerConnectionString,
                        ["Redis:Configuration"] = RedisConnectionString,
                        ["Redis:InstanceName"] = "integration-tests",
                        ["Minio:Endpoint"] = _minioHost,
                        ["Minio:Port"] = minioPort.ToString(CultureInfo.InvariantCulture),
                        ["Minio:AccessKey"] = MinioAccessKey,
                        ["Minio:SecretKey"] = MinioSecretKey,
                        ["Minio:DefaultBucket"] = "skymonitor-diagnostics",
                        ["Smtp:Host"] = _smtpHost,
                        ["Smtp:Port"] = smtpPort.ToString(CultureInfo.InvariantCulture),
                        ["Smtp:From"] = TestEmail.FromAddress,
                        ["Smtp:FromDisplayName"] = TestEmail.FromDisplayName,
                        ["Catalog:Root"] = _catalogFixture.Root,
                        ["Catalog:RequiredPackageKind"] = "Fixture",
                        ["DeviceBootstrap:CentralIdentity:ServiceUrl"] = "https://logichost.integration",
                        ["DeviceBootstrap:CentralIdentity:Mode"] = "ClientCredentials",
                        ["DeviceBootstrap:CentralIdentity:ClientCredentials:ClientId"] = TestClients.SystemCameraAgent.ClientId,
                        ["DeviceBootstrap:CentralIdentity:ClientCredentials:ClientSecret"] = TestClients.SystemCameraAgent.ClientSecret,
                        ["CentralDerivativeWorker:Enabled"] = "false"
                    };

                    AddValues(
                        "DeviceBootstrap:CentralIdentity:ClientCredentials:Scopes",
                        TestClients.SystemCameraAgent.Scopes,
                        overrides);

                    AddDatabaseSeedOverrides(overrides);
                    foreach (var pair in _configurationOverrides)
                    {
                        overrides[pair.Key] = pair.Value;
                    }

                    config.AddInMemoryCollection(overrides!);
                });

                builder.ConfigureTestServices(services =>
                {
                    foreach (var descriptor in services.Where(static descriptor =>
                             descriptor.ServiceType == typeof(IHostedService)
                             && (descriptor.ImplementationType == typeof(CentralArtifactReconciliationService)
                                  || descriptor.ImplementationType == typeof(CentralDerivativeWorker)
                                  || descriptor.ImplementationType == typeof(EnvironmentalObservationRetentionWorker))).ToArray())
                    {
                        services.Remove(descriptor);
                    }

                    // Remove the existing DbContext registration
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();

                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        options.UseSqlServer(SqlServerConnectionString);
                        options.EnableSensitiveDataLogging();
                        options.EnableDetailedErrors();
                    });

                    // Override distributed cache to use the testcontainer Redis.
                    // ConfigureAppConfiguration overrides may not be visible when
                    // Program.cs reads Redis:Configuration (captured before Build()),
                    // so we re-register the cache with the correct connection string.
                    services.AddStackExchangeRedisCache(options =>
                    {
                        options.Configuration = RedisConnectionString;
                        options.InstanceName = "integration-tests";
                    });
                });

            });

        // Seed test data
        await SeedTestDataAsync().ConfigureAwait(false);

        _initialized = true;
    }

    /// <summary>
    /// Seeds test data into the database.
    /// </summary>
    private async Task SeedTestDataAsync()
    {
        // Ensure default diagnostics bucket exists
        using var client = new MinioClient()
            .WithEndpoint(_minioHost, _minioHostPort)
            .WithCredentials(MinioAccessKey, MinioSecretKey)
            .Build();

        var bucketExists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket("skymonitor-diagnostics")).ConfigureAwait(false);
        if (!bucketExists)
        {
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket("skymonitor-diagnostics")).ConfigureAwait(false);
        }
    }

    private static void AddDatabaseSeedOverrides(Dictionary<string, string?> overrides)
    {
        AddUser(0, TestUsers.Admin.Email, TestUsers.Admin.Username, TestUsers.Admin.Password);
        AddUser(1, TestUsers.Operator.Email, TestUsers.Operator.Username, TestUsers.Operator.Password);
        AddUser(2, TestUsers.Viewer.Email, TestUsers.Viewer.Username, TestUsers.Viewer.Password);
        AddUser(3, TestUsers.Regular.Email, TestUsers.Regular.Username, TestUsers.Regular.Password);

        AddApiKey(0, TestApiKeys.CameraAgent.Key, TestApiKeys.CameraAgent.Name, ApiKeyAccessLevel.ReadWrite);
        AddApiKey(1, TestApiKeys.InternalService.Key, TestApiKeys.InternalService.Name, ApiKeyAccessLevel.ReadWrite);
        AddApiKey(2, TestApiKeys.Webhook.Key, TestApiKeys.Webhook.Name, ApiKeyAccessLevel.Read);
        AddApiKey(3, TestApiKeys.ReadOnly.Key, TestApiKeys.ReadOnly.Name, ApiKeyAccessLevel.Read);

        AddConfidentialClient(0, TestClients.SystemInternal.ClientId, TestClients.SystemInternal.ClientSecret,
            TestClients.SystemInternal.DisplayName, TestClients.SystemInternal.Scopes);

        AddPublicClient(0, TestClients.WebUI.ClientId, TestClients.WebUI.DisplayName, TestClients.WebUI.Scopes,
            ["https://localhost:5001/signin-oidc", "http://localhost:5000/signin-oidc"],
            ["https://localhost:5001/signout-callback-oidc", "http://localhost:5000/signout-callback-oidc"]);
        AddPublicClient(1, TestClients.MobileApp.ClientId, TestClients.MobileApp.DisplayName, TestClients.MobileApp.Scopes,
            ["com.skymonitor.mobile://auth-callback"], []);

        void AddUser(int index, string email, string username, string password)
        {
            var prefix = $"DatabaseSeed:Users:{index}";
            overrides[$"{prefix}:Email"] = email;
            overrides[$"{prefix}:Username"] = username;
            overrides[$"{prefix}:Password"] = password;
        }

        void AddApiKey(int index, string rawKey, string displayName, ApiKeyAccessLevel accessLevel)
        {
            var prefix = $"DatabaseSeed:ApiKeys:{index}";
            overrides[$"{prefix}:RawKey"] = rawKey;
            overrides[$"{prefix}:DisplayName"] = displayName;
            overrides[$"{prefix}:AccessLevel"] = accessLevel.ToString();
        }

        void AddConfidentialClient(int index, string clientId, string clientSecret, string displayName, IReadOnlyList<string> scopes)
        {
            var prefix = $"DatabaseSeed:ConfidentialClients:{index}";
            overrides[$"{prefix}:ClientId"] = clientId;
            overrides[$"{prefix}:ClientSecret"] = clientSecret;
            overrides[$"{prefix}:DisplayName"] = displayName;
            AddValues($"{prefix}:Scopes", scopes);
        }

        void AddPublicClient(
            int index,
            string clientId,
            string displayName,
            IReadOnlyList<string> scopes,
            IReadOnlyList<string> redirectUris,
            IReadOnlyList<string> postLogoutRedirectUris)
        {
            var prefix = $"DatabaseSeed:PublicClients:{index}";
            overrides[$"{prefix}:ClientId"] = clientId;
            overrides[$"{prefix}:DisplayName"] = displayName;
            AddValues($"{prefix}:Scopes", scopes);
            AddValues($"{prefix}:RedirectUris", redirectUris);
            AddValues($"{prefix}:PostLogoutRedirectUris", postLogoutRedirectUris);
        }

        void AddValues(string prefix, IReadOnlyList<string> values)
        {
            for (var index = 0; index < values.Count; index++)
            {
                overrides[$"{prefix}:{index}"] = values[index];
            }
        }
    }

    private static void AddValues(
        string prefix,
        string[] values,
        Dictionary<string, string?> overrides)
    {
        for (var index = 0; index < values.Length; index++)
        {
            overrides[$"{prefix}:{index}"] = values[index];
        }
    }

    /// <summary>
    /// Disposes Testcontainers and the application factory.
    /// </summary>
    public void Dispose()
    {
        if (Factory != null)
        {
            Factory.Dispose();
        }

        _catalogFixture?.Dispose();

        Environment.SetEnvironmentVariable("ConnectionStrings__skymonitordb", _originalSqlServerConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _originalDefaultConnectionString);

        if (_sqlServerContainer != null)
        {
            _sqlServerContainer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_redisContainer != null)
        {
            _redisContainer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_minioContainer != null)
        {
            _minioContainer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_smtpContainer != null)
        {
            _smtpContainer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed record ActiveDeviceFixture(
    Guid RegistrationId,
    Guid DevicePublicId,
    Guid ObservatoryId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
