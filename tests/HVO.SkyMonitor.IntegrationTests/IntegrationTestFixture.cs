using DotNet.Testcontainers.Builders;
using HVO.SkyMonitor.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Integration test fixture that starts Testcontainers for PostgreSQL, Redis, and MinIO.
/// Provides a WebApplicationFactory for hosting the HVO.SkyMonitor application in-process.
/// </summary>
public sealed class IntegrationTestFixture : IDisposable
{
    private PostgreSqlContainer? _postgresContainer;
    private RedisContainer? _redisContainer;
    private MinioContainer? _minioContainer;
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
    /// Gets the MinIO access key.
    /// </summary>
    public string MinioAccessKey => "minioadmin";

    /// <summary>
    /// Gets the MinIO secret key.
    /// </summary>
    public string MinioSecretKey => "minioadmin";

    /// <summary>
    /// Initializes Testcontainers and the application factory.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        // Start PostgreSQL container
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("skymonitordb")
            .WithUsername("skymonitor")
            .WithPassword("skymonitor_test")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
            .Build();

        await _postgresContainer.StartAsync();
        PostgresConnectionString = _postgresContainer.GetConnectionString();

        // Start Redis container
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();

        await _redisContainer.StartAsync();
        RedisConnectionString = _redisContainer.GetConnectionString();

        // Start MinIO container
        _minioContainer = new MinioBuilder()
            .WithImage("minio/minio:latest")
            .WithUsername(MinioAccessKey)
            .WithPassword(MinioSecretKey)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();

        await _minioContainer.StartAsync();
        MinioEndpoint = $"localhost:{_minioContainer.GetMappedPublicPort(9000)}";

        // Create the web application factory
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                builder.ConfigureTestServices(services =>
                {
                    // Remove the existing DbContext registration
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();

                    // Use SQLite for now (until PostgreSQL migration is complete)
                    // TODO: Switch to PostgreSQL when EF 10-compatible Npgsql is released
                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        options.UseSqlite("DataSource=:memory:");
                        options.EnableSensitiveDataLogging();
                        options.EnableDetailedErrors();
                    });

                    // Configure Redis connection
                    services.Configure<StackExchange.Redis.ConfigurationOptions>(options =>
                    {
                        options.EndPoints.Clear();
                        options.EndPoints.Add(RedisConnectionString);
                    });

                    // Configure MinIO connection
                    // TODO: Configure MinIO client with test container endpoint
                    // This will be needed when MinIO integration is fully implemented
                });

                builder.ConfigureServices(services =>
                {
                    // Ensure database is created and migrations are applied
                    var serviceProvider = services.BuildServiceProvider();
                    using var scope = serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    dbContext.Database.EnsureCreated();
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
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // TODO: Seed test users, clients, API keys using HVO.SkyMonitor.TestSupport constants
        // This will be implemented as integration tests are added

        await dbContext.SaveChangesAsync();
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
    }
}
