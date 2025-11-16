using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Integration test fixture that starts Testcontainers for PostgreSQL, Redis, and MinIO.
/// Provides a WebApplicationFactory for hosting the HVO.SkyMonitor application in-process.
/// </summary>
public sealed class IntegrationTestFixture : IDisposable
{
    private const string PostgresUsername = "skymonitor";
    private const string PostgresPassword = "skymonitor_test";
    private const string PostgresDatabase = "skymonitordb";
    private readonly int _minioHostPort = GetFreeTcpPort();
    private PostgreSqlContainer? _postgresContainer;
    private RedisContainer? _redisContainer;
    private IContainer? _minioContainer;
    private IContainer? _smtpContainer;
    private bool _initialized;

    /// <summary>
    /// Gets the web application factory for creating HTTP clients.
    /// </summary>
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>
    /// Gets the PostgreSQL connection string.
    /// </summary>
    public string PostgresConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the Redis connection string.
    /// </summary>
    public string RedisConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the MinIO endpoint.
    /// </summary>
    public string MinioEndpoint { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the SMTP HTTP endpoint (MailHog UI/API).
    /// </summary>
    public string SmtpHttpEndpoint { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the MinIO access key.
    /// </summary>
    public string MinioAccessKey => "minioadmin";

    /// <summary>
    /// Gets the MinIO secret key.
    /// </summary>
    public string MinioSecretKey => "minioadmin";

    private string RedisHost => "127.0.0.1";
    private string MinioHost => "127.0.0.1";
    private string SmtpHost => "127.0.0.1";

    /// <summary>
    /// Initializes Testcontainers and the application factory.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase(PostgresDatabase)
            .WithUsername(PostgresUsername)
            .WithPassword(PostgresPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
            .Build();

        await _postgresContainer.StartAsync();
        var postgresPort = _postgresContainer.GetMappedPublicPort(5432);
        PostgresConnectionString =
            $"Host=127.0.0.1;Port={postgresPort};Username={PostgresUsername};Password={PostgresPassword};Database={PostgresDatabase};Include Error Detail=true";

        // Start Redis container
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();

        await _redisContainer.StartAsync();
        var redisPort = _redisContainer.GetMappedPublicPort(6379);
        RedisConnectionString = $"{RedisHost}:{redisPort}";

        // Start MinIO container
        _minioContainer = new ContainerBuilder()
            .WithImage("minio/minio:latest")
            .WithPortBinding(_minioHostPort, 9000)
            .WithEnvironment(new Dictionary<string, string>
            {
                ["MINIO_ROOT_USER"] = MinioAccessKey,
                ["MINIO_ROOT_PASSWORD"] = MinioSecretKey
            })
                .WithCommand("server", "/data", "--console-address", ":9001")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();

        await _minioContainer.StartAsync();
        var minioPort = _minioHostPort;
        MinioEndpoint = $"{MinioHost}:{minioPort}";

        // Start SMTP (MailHog) container
        _smtpContainer = new ContainerBuilder()
            .WithImage("mailhog/mailhog:v1.0.1")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1025))
            .Build();

        await _smtpContainer.StartAsync();
        var smtpPort = _smtpContainer.GetMappedPublicPort(1025);
        var smtpHttpPort = _smtpContainer.GetMappedPublicPort(8025);
        SmtpHttpEndpoint = $"http://{SmtpHost}:{smtpHttpPort}";

        // Create the web application factory
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                builder.ConfigureAppConfiguration((context, config) =>
                {
                    var overrides = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:skymonitordb"] = PostgresConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = PostgresConnectionString,
                        ["Redis:Configuration"] = RedisConnectionString,
                        ["Redis:InstanceName"] = "integration-tests",
                        ["Minio:Endpoint"] = MinioHost,
                        ["Minio:Port"] = minioPort.ToString(),
                        ["Minio:AccessKey"] = MinioAccessKey,
                        ["Minio:SecretKey"] = MinioSecretKey,
                        ["Minio:DefaultBucket"] = "skymonitor-diagnostics",
                        ["Smtp:Host"] = SmtpHost,
                        ["Smtp:Port"] = smtpPort.ToString(),
                        ["Smtp:From"] = TestEmail.FromAddress,
                        ["Smtp:FromDisplayName"] = TestEmail.FromDisplayName
                    };

                    config.AddInMemoryCollection(overrides!);
                });

                builder.ConfigureTestServices(services =>
                {
                    // Remove the existing DbContext registration
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();

                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        options.UseNpgsql(PostgresConnectionString);
                        options.EnableSensitiveDataLogging();
                        options.EnableDetailedErrors();
                    });
                });

            });

        // Seed test data
        await SeedTestDataAsync();

        _initialized = true;
    }

    /// <summary>
    /// Seeds test data into the database.
    /// </summary>
    private async Task SeedTestDataAsync()
    {
        // Ensure default diagnostics bucket exists
        var client = new MinioClient()
            .WithEndpoint(MinioHost, _minioHostPort)
            .WithCredentials(MinioAccessKey, MinioSecretKey)
            .Build();

        var bucketExists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket("skymonitor-diagnostics"));
        if (!bucketExists)
        {
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket("skymonitor-diagnostics"));
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

        if (_postgresContainer != null)
        {
            _postgresContainer.DisposeAsync().GetAwaiter().GetResult();
        }

        if (_redisContainer != null)
        {
            _redisContainer.DisposeAsync().GetAwaiter().GetResult();
        }

        if (_minioContainer != null)
        {
            _minioContainer.DisposeAsync().GetAwaiter().GetResult();
        }

        if (_smtpContainer != null)
        {
            _smtpContainer.DisposeAsync().GetAwaiter().GetResult();
        }

    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
