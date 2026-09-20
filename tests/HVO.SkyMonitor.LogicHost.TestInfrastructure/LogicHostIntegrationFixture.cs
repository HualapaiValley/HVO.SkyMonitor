using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace HVO.SkyMonitor.LogicHost.TestInfrastructure;

using Program = HVO.SkyMonitor.LogicHost.Program;

/// <summary>
/// Integration test fixture that starts Testcontainers for SQL Server, Redis, and Mailpit
/// and uses the production filesystem object-store provider.
/// Provides a WebApplicationFactory for hosting the HVO.SkyMonitor application in-process.
/// </summary>
public sealed class IntegrationTestFixture : IDisposable
{
    public const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04@sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89";
    public const string RedisImage = "redis:7.4.11-alpine@sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf";
    public const string MailpitImage = "axllent/mailpit:v1.31.0@sha256:c96991d9bef73594c246d89ca81411d4e916f03e76a7d2d72fa2ab5dd3c9ce24";
    private const string SqlServerPassword = "SkyMonitor_test_password1!";
    private readonly IReadOnlyDictionary<string, string?> _configurationOverrides;
    private readonly bool _suppressRecurringWorkers;
    private MsSqlContainer? _sqlServerContainer;
    private RedisContainer? _redisContainer;
    private IContainer? _smtpContainer;
    private FilesystemObjectStore? _objectStore;
    private CatalogFixtureInstallation? _catalogFixture;
    private bool _initialized;
    private string? _originalSqlServerConnectionString;
    private string? _originalDefaultConnectionString;
    private string _redisHost = "127.0.0.1";
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
    /// Gets the fixture-owned filesystem object-store root.
    /// </summary>
    public string ObjectStorageRoot { get; private set; } = string.Empty;

    /// <summary>
    /// Environment variables naming an operator-provided S3-compatible endpoint. The supported
    /// LogicHost deployment uses the filesystem provider, so no S3 server is started or managed
    /// here; the retained S3 adapter tests are opt-in against an endpoint the operator supplies.
    /// </summary>
    public const string ExternalS3EndpointVariable = "HVO_SKYMONITOR_S3_TEST_ENDPOINT";
    public const string ExternalS3AccessKeyVariable = "HVO_SKYMONITOR_S3_TEST_ACCESS_KEY";
    public const string ExternalS3SecretKeyVariable = "HVO_SKYMONITOR_S3_TEST_SECRET_KEY";

    public static string? TryGetExternalS3Endpoint()
    {
        var endpoint = Environment.GetEnvironmentVariable(ExternalS3EndpointVariable);
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
    }

    public static string ExternalS3Endpoint
        => TryGetExternalS3Endpoint()
            ?? throw new InvalidOperationException(
                $"These tests require an S3-compatible endpoint in {ExternalS3EndpointVariable}. " +
                "The supported LogicHost deployment uses the filesystem object-store provider.");

    public static string ExternalS3AccessKey
        => Environment.GetEnvironmentVariable(ExternalS3AccessKeyVariable) ?? "minioadmin";

    public static string ExternalS3SecretKey
        => Environment.GetEnvironmentVariable(ExternalS3SecretKeyVariable) ?? "minioadmin";

    /// <summary>
    /// Provenance label for historical evidence documents that recorded the object-storage
    /// server image. No such server is managed by this fixture.
    /// </summary>
    public static string ExternalS3ImageLabel => TryGetExternalS3Endpoint() ?? "external-s3-endpoint";

    /// <summary>
    /// Gets the SMTP HTTP endpoint (Mailpit UI/API).
    /// </summary>
    public string SmtpHttpEndpoint { get; private set; } = string.Empty;

    public IntegrationTestFixture(
        IReadOnlyDictionary<string, string?>? configurationOverrides = null,
        bool suppressRecurringWorkers = false)
    {
        _configurationOverrides = configurationOverrides ?? new Dictionary<string, string?>();
        _suppressRecurringWorkers = suppressRecurringWorkers;
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
        var registration = new DeviceRegistration
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
        };
        db.DeviceRegistrations.Add(registration);
        _ = await ObservatoryLocationAuthority.ApplyAsync(
            db,
            observatory,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId,
            observatory.AllowedDeploymentRadiusMeters,
            DateTimeOffset.UnixEpoch.AddDays(-2),
            "integration-test",
            CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var deployment = DeploymentLocationSnapshot.Create(
            "inherited-observatory",
            1,
            "observatory-fallback",
            null,
            DateTimeOffset.UnixEpoch.AddDays(-1),
            null,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId);
        _ = await scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>()
            .ProposeAsync(
                registration,
                deployment,
                DeploymentLocationSourceKind.Inherited,
                "integration-test",
                CancellationToken.None).ConfigureAwait(false);
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

        _sqlServerContainer = new MsSqlBuilder(SqlServerImage)
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
        _redisContainer = new RedisBuilder(RedisImage)
            .Build();

        await _redisContainer.StartAsync().ConfigureAwait(false);
        _redisHost = _redisContainer.Hostname;
        var redisPort = _redisContainer.GetMappedPublicPort(6379);
        RedisConnectionString = $"{_redisHost}:{redisPort}";

        ObjectStorageRoot = Path.Combine(Path.GetTempPath(), "hvo-logichost-object-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ObjectStorageRoot);
        foreach (var bucket in new[] { "skymonitor-artifacts", "skymonitor-diagnostics" })
        {
            Directory.CreateDirectory(Path.Combine(ObjectStorageRoot, bucket));
        }
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(ObjectStorageRoot, mode);
            File.SetUnixFileMode(Path.Combine(ObjectStorageRoot, "skymonitor-artifacts"), mode);
            File.SetUnixFileMode(Path.Combine(ObjectStorageRoot, "skymonitor-diagnostics"), mode);
        }
        var objectStorageOptions = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        objectStorageOptions.Filesystem.Root = ObjectStorageRoot;
        var wrappedObjectStorageOptions = Options.Create(objectStorageOptions);
        _objectStore = new FilesystemObjectStore(
            wrappedObjectStorageOptions,
            new ObjectStoreTelemetry(wrappedObjectStorageOptions),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        // Start SMTP (Mailpit) container
        _smtpContainer = new ContainerBuilder(MailpitImage)
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
        Factory = CreateFactory(smtpPort);

        _initialized = true;
    }

    public WebApplicationFactory<Program> CreateKestrelFactory(
        Action<DbContextOptionsBuilder>? configureDatabase = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var smtpPort = _smtpContainer?.GetMappedPublicPort(1025)
            ?? throw new InvalidOperationException("The SMTP fixture is not initialized.");
        var factory = CreateFactory(smtpPort, "Development", configureDatabase, configureServices);
        factory.UseKestrel(0);
        return factory;
    }

    public IContainer GetDependencyContainer(IntegrationDependency dependency) => dependency switch
    {
        IntegrationDependency.SqlServer => _sqlServerContainer
            ?? throw new InvalidOperationException("The SQL Server fixture is not initialized."),
        IntegrationDependency.Redis => _redisContainer
            ?? throw new InvalidOperationException("The Redis fixture is not initialized."),
        IntegrationDependency.Minio => throw new InvalidOperationException("MinIO is not part of the shared LogicHost integration fixture."),
        IntegrationDependency.Smtp => _smtpContainer
            ?? throw new InvalidOperationException("The SMTP fixture is not initialized."),
        _ => throw new ArgumentOutOfRangeException(nameof(dependency))
    };

    private WebApplicationFactory<Program> CreateFactory(
        int smtpPort,
        string environment = "Testing",
        Action<DbContextOptionsBuilder>? configureDatabase = null,
        Action<IServiceCollection>? configureServices = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);

                builder.ConfigureAppConfiguration((context, config) =>
                {
                    var overrides = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:skymonitordb"] = SqlServerConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = SqlServerConnectionString,
                        ["Redis:Configuration"] = RedisConnectionString,
                        ["Redis:InstanceName"] = "integration-tests",
                        ["ObjectStorage:Provider"] = "Filesystem",
                        ["ObjectStorage:Filesystem:Root"] = ObjectStorageRoot,
                        ["ObjectStorage:ArtifactBucket"] = "skymonitor-artifacts",
                        ["ObjectStorage:DiagnosticsBucket"] = "skymonitor-diagnostics",
                        ["Smtp:Host"] = _smtpHost,
                        ["Smtp:Port"] = smtpPort.ToString(CultureInfo.InvariantCulture),
                        ["Smtp:From"] = TestEmail.FromAddress,
                        ["Smtp:FromDisplayName"] = TestEmail.FromDisplayName,
                        ["Catalog:Root"] = CatalogRoot,
                        ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
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
                    services.RemoveAll<IObjectStore>();
                    services.AddSingleton<IObjectStore>(_objectStore
                        ?? throw new InvalidOperationException("The filesystem object-store fixture is not initialized."));
                    foreach (var descriptor in services.Where(static descriptor =>
                             descriptor.ServiceType == typeof(IHostedService)
                              && (descriptor.ImplementationType == typeof(CentralArtifactReconciliationService)
                                   || descriptor.ImplementationType == typeof(CentralDerivativeWorker)
                                   || descriptor.ImplementationType == typeof(EnvironmentalObservationRetentionWorker))).ToArray())
                    {
                        services.Remove(descriptor);
                    }
                    if (_suppressRecurringWorkers)
                    {
                        foreach (var descriptor in services.Where(static descriptor =>
                                     descriptor.ServiceType == typeof(IHostedService)).ToArray())
                        {
                            services.Remove(descriptor);
                        }
                    }

                    // Remove the existing DbContext registration
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();

                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        options.UseSqlServer(SqlServerConnectionString);
                        options.EnableSensitiveDataLogging();
                        options.EnableDetailedErrors();
                        configureDatabase?.Invoke(options);
                    });

                    configureServices?.Invoke(services);

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

    private string CatalogRoot => _catalogFixture?.Root
        ?? throw new InvalidOperationException("The catalog fixture is not initialized.");

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
        AddConfidentialClient(1, TestClients.SystemProcessingRunner.ClientId, TestClients.SystemProcessingRunner.ClientSecret,
            TestClients.SystemProcessingRunner.DisplayName, TestClients.SystemProcessingRunner.Scopes);

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

        if (_smtpContainer != null)
        {
            _smtpContainer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _objectStore?.Dispose();
        if (Directory.Exists(ObjectStorageRoot))
        {
            Directory.Delete(ObjectStorageRoot, recursive: true);
        }
    }
}

public enum IntegrationDependency
{
    SqlServer,
    Redis,
    Minio,
    Smtp
}

public sealed record ActiveDeviceFixture(
    Guid RegistrationId,
    Guid DevicePublicId,
    Guid ObservatoryId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
