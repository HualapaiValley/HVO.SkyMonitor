using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.LogicHost.Data;
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
    private readonly int _minioHostPort = GetFreeTcpPort();
    private MsSqlContainer? _sqlServerContainer;
    private RedisContainer? _redisContainer;
    private IContainer? _minioContainer;
    private IContainer? _smtpContainer;
    private bool _initialized;

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

    private const string RedisHost = "127.0.0.1";
    private const string MinioHost = "127.0.0.1";
    private const string SmtpHost = "127.0.0.1";

    /// <summary>
    /// Initializes Testcontainers and the application factory.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _sqlServerContainer = new MsSqlBuilder()
            .WithPassword(SqlServerPassword)
            .Build();

        await _sqlServerContainer.StartAsync().ConfigureAwait(false);
        SqlServerConnectionString = _sqlServerContainer.GetConnectionString();

        // Start Redis container (RedisBuilder provides a wait strategy that verifies
        // the server responds to commands, not just that the TCP port is open)
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();

        await _redisContainer.StartAsync().ConfigureAwait(false);
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

        await _minioContainer.StartAsync().ConfigureAwait(false);
        var minioPort = _minioHostPort;
        MinioEndpoint = $"{MinioHost}:{minioPort}";

        // Start SMTP (Mailpit) container
        _smtpContainer = new ContainerBuilder()
            .WithImage("axllent/mailpit:latest")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1025))
            .Build();

        await _smtpContainer.StartAsync().ConfigureAwait(false);
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
                        ["ConnectionStrings:skymonitordb"] = SqlServerConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = SqlServerConnectionString,
                        ["Redis:Configuration"] = RedisConnectionString,
                        ["Redis:InstanceName"] = "integration-tests",
                        ["Minio:Endpoint"] = MinioHost,
                        ["Minio:Port"] = minioPort.ToString(CultureInfo.InvariantCulture),
                        ["Minio:AccessKey"] = MinioAccessKey,
                        ["Minio:SecretKey"] = MinioSecretKey,
                        ["Minio:DefaultBucket"] = "skymonitor-diagnostics",
                        ["Smtp:Host"] = SmtpHost,
                        ["Smtp:Port"] = smtpPort.ToString(CultureInfo.InvariantCulture),
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
            .WithEndpoint(MinioHost, _minioHostPort)
            .WithCredentials(MinioAccessKey, MinioSecretKey)
            .Build();

        var bucketExists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket("skymonitor-diagnostics")).ConfigureAwait(false);
        if (!bucketExists)
        {
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket("skymonitor-diagnostics")).ConfigureAwait(false);
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
