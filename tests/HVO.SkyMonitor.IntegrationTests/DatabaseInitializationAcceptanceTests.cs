using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.RegularExpressions;

#pragma warning disable CA2100 // Test-only SQL is assembled from generated safe identifiers and fixed statements.

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DatabaseInitializationAcceptanceTests
{
    [TestMethod]
    public async Task CleanAndCurrentDatabase_InitializationConverges()
    {
        await using var database = await InitializedDatabase.CreateAsync("Converge").ConfigureAwait(false);
        await using var scope = database.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var initial = await db.DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false);

        (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        initial.Status.Should().Be(DatabaseInitializationStatus.Completed);
        initial.InitializationVersion.Should().Be(DatabaseInitializer.CurrentInitializationVersion);
        initial.TargetMigrationId.Should().Be(db.Database.GetMigrations().Last());
        initial.CompletedAtUtc.Should().NotBeNull();
        initial.FailureStage.Should().BeNull();

        initial.Status = DatabaseInitializationStatus.Running;
        initial.AttemptId = Guid.NewGuid();
        var interruptedAttemptId = initial.AttemptId;
        initial.CompletedAtUtc = null;
        db.DatabaseInitializationState.Update(initial);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var validator = new DatabaseRuntimeValidator(db, new ProductionEnvironment());
        var validation = () => validator.ValidateAsync(CancellationToken.None);
        await validation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*initialization state is incomplete*").ConfigureAwait(false);

        var result = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
            .RunAsync(CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var current = await db.DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false);
        current.AttemptId.Should().Be(result.AttemptId).And.NotBe(interruptedAttemptId);
        current.Status.Should().Be(DatabaseInitializationStatus.Completed);
        (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        Console.WriteLine(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                database.InitializationElapsed,
                InitialDataBytes = database.InitialStorage.DataBytes,
                FinalDataBytes = database.FinalStorage.DataBytes,
                DataGrowthBytes = database.FinalStorage.DataBytes - database.InitialStorage.DataBytes,
                InitialLogBytes = database.InitialStorage.LogBytes,
                FinalLogBytes = database.FinalStorage.LogBytes,
                LogGrowthBytes = database.FinalStorage.LogBytes - database.InitialStorage.LogBytes,
                database.FinalStorage.DataGrowthSetting,
                database.FinalStorage.DataGrowthIsPercent,
                database.FinalStorage.LogGrowthSetting,
                database.FinalStorage.LogGrowthIsPercent,
                database.FinalStorage.VolumeAvailableBytes
            }));
    }

    [TestMethod]
    public async Task HeldInitializationLock_FailsWithoutMutationAndRetryCompletes()
    {
        await using var database = await InitializedDatabase.CreateAsync("Lock").ConfigureAwait(false);
        DatabaseInitializationState before;
        await using (var readScope = database.Factory.Services.CreateAsyncScope())
        {
            before = await readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false);
        }

        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync().ConfigureAwait(false);
        var lockStatus = await ExecuteScalarAsync<int>(blocker, $"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'{DatabaseInitializer.LockResource}',
                @LockMode = N'Exclusive',
                @LockOwner = N'Session',
                @LockTimeout = 0;
            SELECT @result;
            """).ConfigureAwait(false);
        lockStatus.Should().BeGreaterThanOrEqualTo(0);

        await using (var blockedScope = database.Factory.Services.CreateAsyncScope())
        {
            var action = () => blockedScope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
                .RunAsync(CancellationToken.None);
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*another LogicHost database initialization*").ConfigureAwait(false);
        }
        _ = await ExecuteScalarAsync<int>(blocker, $"""
            DECLARE @result int;
            EXEC @result = sys.sp_releaseapplock
                @Resource = N'{DatabaseInitializer.LockResource}',
                @LockOwner = N'Session';
            SELECT @result;
            """).ConfigureAwait(false);
        await blocker.CloseAsync().ConfigureAwait(false);

        await using var retryScope = database.Factory.Services.CreateAsyncScope();
        var db = retryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ChangeTracker.Clear();
        (await db.DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false))
            .AttemptId.Should().Be(before.AttemptId);
        var retry = await retryScope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
            .RunAsync(CancellationToken.None).ConfigureAwait(false);
        retry.AttemptId.Should().NotBe(before.AttemptId);
    }

    [TestMethod]
    public async Task LegacyDatabase_ControlledInitializationConverges()
    {
        await using var database = await InitializedDatabase.CreateAsync(
            "Legacy",
            async connectionString =>
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(connectionString).Options;
                await using var db = new ApplicationDbContext(options);
                var migrations = db.Database.GetMigrations().ToArray();
                await db.GetService<IMigrator>().MigrateAsync(migrations[^2]).ConfigureAwait(false);
            }).ConfigureAwait(false);
        await using var scope = database.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        var state = await db.DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false);
        state.Status.Should().Be(DatabaseInitializationStatus.Completed);
        state.TargetMigrationId.Should().Be(db.Database.GetMigrations().Last());
    }

    [TestMethod]
    public async Task MigrationPrincipal_CanInitializeCleanDatabaseThroughShippedRole()
    {
        var login = $"Issue256Migrator_{Guid.NewGuid():N}";
        const string password = "Issue256_Migration_Only!42";
        string? migrationConnectionString = null;
        try
        {
            await using var database = await InitializedDatabase.CreateAsync(
                "MigratorGrant",
                async adminConnectionString =>
                {
                    await InitializedDatabase.CreateLoginAndUserAsync(adminConnectionString, login, password)
                        .ConfigureAwait(false);
                    await InitializedDatabase.ApplyRoleScriptAsync(
                        adminConnectionString,
                        "logichost-migration-role.sql",
                        "MigrationUser",
                        login).ConfigureAwait(false);
                    var builder = new SqlConnectionStringBuilder(adminConnectionString)
                    {
                        UserID = login,
                        Password = password,
                        IntegratedSecurity = false,
                        ApplicationName = LogicHostSqlConnectionProfiles.InitializationApplicationName
                    };
                    migrationConnectionString = builder.ConnectionString;
                },
                _ => migrationConnectionString
                    ?? throw new InvalidOperationException("The migration connection was not prepared."))
                .ConfigureAwait(false);
            await using var scope = database.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            (await db.DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .Status.Should().Be(DatabaseInitializationStatus.Completed);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await InitializedDatabase.DropLoginAsync(login).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task GeneratedIdempotentSql_CleanAndCurrentDatabaseConverges()
    {
        await using var database = await InitializedDatabase.CreateAsync(
            "IdempotentSql",
            async connectionString =>
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(connectionString).Options;
                await using var db = new ApplicationDbContext(options);
                var script = db.GetService<IMigrator>().GenerateScript(
                    options: MigrationsSqlGenerationOptions.Idempotent);
                await InitializedDatabase.ExecuteBatchesAsync(connectionString, script).ConfigureAwait(false);
                await InitializedDatabase.ExecuteBatchesAsync(connectionString, script).ConfigureAwait(false);
            }).ConfigureAwait(false);
        await using var scope = database.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        (await db.DatabaseInitializationState.AsNoTracking().SingleAsync().ConfigureAwait(false))
            .Status.Should().Be(DatabaseInitializationStatus.Completed);
    }

    [TestMethod]
    public async Task ProductionInitializationHostMode_UsesDedicatedConnectionAndExits()
    {
        await using var database = await InitializedDatabase.CreateAsync("HostMode").ConfigureAwait(false);
        var logicHostAssembly = typeof(HVO.SkyMonitor.LogicHost.Program).Assembly.Location;
        var outputDirectory = Path.GetDirectoryName(logicHostAssembly)!;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = outputDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(logicHostAssembly);
        process.StartInfo.ArgumentList.Add("--host-mode=database-initialize");
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = Environments.Production;
        process.StartInfo.Environment["ConnectionStrings__skymonitordb-migrations"] = database.ConnectionString;
        process.StartInfo.Environment["DeviceBootstrap__CentralIdentity__ServiceUrl"] = "https://issue256.invalid";
        process.StartInfo.Environment["DeviceBootstrap__CentralIdentity__Mode"] = "ClientCredentials";
        process.StartInfo.Environment["DeviceBootstrap__CentralIdentity__ClientCredentials__ClientId"] =
            "issue256-host-mode";
        process.StartInfo.Environment["DeviceBootstrap__CentralIdentity__ClientCredentials__ClientSecret"] =
            "Issue256_Host_Mode_Secret!42";
        process.StartInfo.Environment["DeviceBootstrap__CentralIdentity__ClientCredentials__Scopes__0"] =
            "api.camera";
        process.Start().Should().BeTrue();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        process.ExitCode.Should().Be(0, error);
        output.Should().Contain("Database initialization completed");
    }

    [TestMethod]
    public async Task RuntimePrincipal_CanUseDataAndApplicationLocksButCannotMutateSchemaOrControlState()
    {
        await using var database = await InitializedDatabase.CreateAsync("RuntimeGrant").ConfigureAwait(false);
        var login = $"Issue256Runtime_{Guid.NewGuid():N}";
        const string password = "Issue256_Runtime_Only!42";
        try
        {
            await database.CreateRuntimeLoginAsync(login, password).ConfigureAwait(false);
            var runtimeBuilder = new SqlConnectionStringBuilder(database.ConnectionString)
            {
                UserID = login,
                Password = password,
                IntegratedSecurity = false,
                ApplicationName = LogicHostSqlConnectionProfiles.RuntimeApplicationName
            };
            await using var runtime = new SqlConnection(runtimeBuilder.ConnectionString);
            await runtime.OpenAsync().ConfigureAwait(false);

            (await ExecuteScalarAsync<int>(runtime,
                "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory];").ConfigureAwait(false)).Should().BePositive();
            (await ExecuteScalarAsync<int>(runtime, """
                UPDATE TOP (1) [dbo].[AspNetUsers]
                SET [AccessFailedCount] = [AccessFailedCount];
                SELECT @@ROWCOUNT;
                """).ConfigureAwait(false)).Should().BePositive();
            (await ExecuteScalarAsync<int>(runtime, """
                UPDATE TOP (1) [dbo].[OpenIddictApplications]
                SET [DisplayName] = [DisplayName];
                SELECT @@ROWCOUNT;
                """).ConfigureAwait(false)).Should().BePositive();
            (await ExecuteScalarAsync<int>(runtime, """
                SELECT COUNT(*)
                FROM [sys].[tables]
                WHERE [schema_id] = SCHEMA_ID(N'dbo')
                  AND [name] NOT IN (N'__EFMigrationsHistory', N'DatabaseInitializationState')
                  AND (
                      HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME([name]), N'OBJECT', N'SELECT') <> 1 OR
                      HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME([name]), N'OBJECT', N'INSERT') <> 1 OR
                      HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME([name]), N'OBJECT', N'UPDATE') <> 1 OR
                      HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME([name]), N'OBJECT', N'DELETE') <> 1);
                """).ConfigureAwait(false)).Should().Be(0);
            (await ExecuteScalarAsync<int>(runtime, """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = N'Issue256.Runtime.PermissionProbe',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Session',
                    @LockTimeout = 0;
                IF @result >= 0
                    EXEC sys.sp_releaseapplock
                        @Resource = N'Issue256.Runtime.PermissionProbe',
                        @LockOwner = N'Session';
                SELECT @result;
                """).ConfigureAwait(false)).Should().BeGreaterThanOrEqualTo(0);

            await AssertPermissionDeniedAsync(runtime,
                "CREATE TABLE [dbo].[Issue256Forbidden] ([Id] int NOT NULL);").ConfigureAwait(false);
            await AssertPermissionDeniedAsync(runtime,
                "CREATE SEQUENCE [dbo].[Issue256ForbiddenSequence] AS bigint START WITH 1;").ConfigureAwait(false);
            await AssertPermissionDeniedAsync(runtime,
                "CREATE VIEW [dbo].[Issue256ForbiddenView] AS SELECT 1 AS [Value];").ConfigureAwait(false);
            await AssertPermissionDeniedAsync(runtime,
                "UPDATE [dbo].[DatabaseInitializationState] SET [UpdatedAtUtc] = [UpdatedAtUtc];")
                .ConfigureAwait(false);
            await AssertPermissionDeniedAsync(runtime,
                "UPDATE [dbo].[__EFMigrationsHistory] SET [MigrationId] = [MigrationId];")
                .ConfigureAwait(false);
            await runtime.CloseAsync().ConfigureAwait(false);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(runtimeBuilder.ConnectionString).Options;
            await using var runtimeDb = new ApplicationDbContext(options);
            var validator = new DatabaseRuntimeValidator(runtimeDb, new ProductionEnvironment());
            await validator.ValidateAsync(CancellationToken.None).ConfigureAwait(false);

            using var certificates = new TestOpenIddictCertificates();
            await using (var runtimeFactory = database.Factory.WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment(Environments.Production);
                webHost.UseSetting("OpenIddictCertificates:SigningPath", certificates.SigningPath);
                webHost.UseSetting("OpenIddictCertificates:EncryptionPath", certificates.EncryptionPath);
                webHost.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:skymonitordb"] = runtimeBuilder.ConnectionString,
                        ["OpenIddictCertificates:SigningPath"] = certificates.SigningPath,
                        ["OpenIddictCertificates:EncryptionPath"] = certificates.EncryptionPath
                    }));
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(runtimeBuilder.ConnectionString));
                    services.RemoveAll<IHostedService>();
                });
            }))
            {
                await using var runtimeScope = runtimeFactory.Services.CreateAsyncScope();
                var serviceDb = runtimeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var site = new Observatory
                {
                    OwnerUserId = $"issue256-runtime-owner-{Guid.NewGuid():N}",
                    Name = "Issue 256 runtime site",
                    TimeZoneId = "UTC",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                var agentId = Guid.NewGuid();
                var registration = new DeviceRegistration
                {
                    DeviceId = $"issue256-runtime-{Guid.NewGuid():N}",
                    Observatory = site,
                    ObservatoryId = site.Id,
                    FriendlyName = "Issue 256 runtime agent",
                    ObservatoryName = site.Name,
                    OwnerUserId = site.OwnerUserId,
                    OwnerDisplayName = "Issue 256 Owner",
                    Status = DeviceRegistrationStatus.Active,
                    VerificationCodeHash = new string('A', 64),
                    DevicePublicId = agentId,
                    DeviceKeyHash = new string('B', 64),
                    IssuedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
                };
                serviceDb.DeviceRegistrations.Add(registration);
                serviceDb.CentralFrames.Add(new CentralFrame
                {
                    RegistrationId = registration.Id,
                    DevicePublicId = agentId,
                    ObservatoryId = site.Id,
                    AgentId = agentId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                    FrameId = Guid.NewGuid(),
                    CapturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    FirstReceivedAtUtc = DateTimeOffset.UtcNow,
                    RigId = "rig-1"
                });
                await serviceDb.SaveChangesAsync().ConfigureAwait(false);
                using var parametersDocument = JsonDocument.Parse("""{"source":"issue-256"}""");
                var parameters = parametersDocument.RootElement.Clone();
                var observedAt = DateTimeOffset.UtcNow;
                var observation = new EnvironmentalObservationV1(
                    EnvironmentalObservationV1.CurrentSchemaVersion,
                    Guid.NewGuid(),
                    new EnvironmentalObservationTarget(site.Id, agentId, "rig-1"),
                    new EnvironmentalObservationSource(
                        "issue-256",
                        "runtime-permission-probe",
                        "1.0.0",
                        EnvironmentalObservationSourceKind.Measured,
                        new EnvironmentalObservationProvenance(
                            new ProcessingAlgorithmIdentity("issue-256-probe", "1.0.0"),
                            parameters,
                            CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
                    observedAt,
                    null,
                    null,
                    observedAt.AddMinutes(-1),
                    observedAt.AddMinutes(5),
                    observedAt.AddMinutes(3),
                    new EnvironmentalObservationValue(
                        EnvironmentalObservationKind.RelativeHumidity,
                        EnvironmentalObservationUnit.Percent,
                        45,
                        null,
                        EnvironmentalObservationQuality.Good,
                        0.5),
                    []);
                (await runtimeScope.ServiceProvider.GetRequiredService<IEnvironmentalObservationIngestService>()
                    .IngestAsync(observation).ConfigureAwait(false)).Disposition
                    .Should().Be(EnvironmentalObservationIngestDisposition.Accepted);
                (await runtimeScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync("issue-256-runtime", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false)).Should().BeNull();
                (await runtimeScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                    .IsHeldAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
                _ = await runtimeScope.ServiceProvider.GetRequiredService<IPublicNetworkReadService>()
                    .ListObservatoriesAsync(10, null, CancellationToken.None).ConfigureAwait(false);
            }

            await InitializedDatabase.ExecuteDatabaseSqlAsync(
                database.ConnectionString,
                $"ALTER ROLE [db_datawriter] ADD MEMBER [{login}];").ConfigureAwait(false);
            SqlConnection.ClearAllPools();
            await using var overprivilegedDb = new ApplicationDbContext(options);
            var overprivilegedValidator = new DatabaseRuntimeValidator(
                overprivilegedDb,
                new ProductionEnvironment());
            var validation = () => overprivilegedValidator.ValidateAsync(CancellationToken.None);
            await validation.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*prohibited schema, security, or initialization-control authority*")
                .ConfigureAwait(false);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await InitializedDatabase.DropLoginAsync(login).ConfigureAwait(false);
        }
    }

    private static async Task AssertPermissionDeniedAsync(SqlConnection connection, string sql)
    {
        var action = async () =>
        {
            await using var command = new SqlCommand(sql, connection);
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        };
        await action.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The SQL scalar probe returned null.");
        return (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class InitializedDatabase(
        string databaseName,
        string connectionString,
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        DatabaseStorageSnapshot initialStorage,
        DatabaseStorageSnapshot finalStorage,
        TimeSpan initializationElapsed) : IAsyncDisposable
    {
        internal string ConnectionString { get; } = connectionString;
        internal WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> Factory { get; } = factory;
        internal DatabaseStorageSnapshot InitialStorage { get; } = initialStorage;
        internal DatabaseStorageSnapshot FinalStorage { get; } = finalStorage;
        internal TimeSpan InitializationElapsed { get; } = initializationElapsed;

        internal static async Task<InitializedDatabase> CreateAsync(
            string scenario,
            Func<string, Task>? prepareDatabase = null,
            Func<string, string>? applicationConnection = null)
        {
            var databaseName = $"SkyMonitorIssue256{scenario}_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = databaseName
            };
            await ExecuteAdminAsync($"CREATE DATABASE [{databaseName}];").ConfigureAwait(false);
            var initialStorage = await ReadStorageAsync(builder.ConnectionString).ConfigureAwait(false);
            if (prepareDatabase is not null)
            {
                try
                {
                    await prepareDatabase(builder.ConnectionString).ConfigureAwait(false);
                }
                catch
                {
                    SqlConnection.ClearAllPools();
                    await ExecuteAdminAsync(
                        $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];")
                        .ConfigureAwait(false);
                    throw;
                }
            }
            var applicationConnectionString = applicationConnection?.Invoke(builder.ConnectionString)
                ?? builder.ConnectionString;
            var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(webHost =>
            {
                webHost.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:skymonitordb"] = applicationConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = applicationConnectionString
                    }));
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(applicationConnectionString));
                });
            });
            try
            {
                var started = Stopwatch.GetTimestamp();
                _ = factory.Services;
                var elapsed = Stopwatch.GetElapsedTime(started);
                var finalStorage = await ReadStorageAsync(builder.ConnectionString).ConfigureAwait(false);
                return new(
                    databaseName,
                    builder.ConnectionString,
                    factory,
                    initialStorage,
                    finalStorage,
                    elapsed);
            }
            catch
            {
                await factory.DisposeAsync().ConfigureAwait(false);
                await ExecuteAdminAsync(
                    $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];")
                    .ConfigureAwait(false);
                throw;
            }
        }

        internal async Task CreateRuntimeLoginAsync(string login, string password)
        {
            await CreateLoginAndUserAsync(ConnectionString, login, password).ConfigureAwait(false);
            await ApplyRoleScriptAsync(
                ConnectionString,
                "logichost-runtime-role.sql",
                "RuntimeUser",
                login).ConfigureAwait(false);
        }

        internal static async Task CreateLoginAndUserAsync(
            string databaseConnectionString,
            string login,
            string password)
        {
            await ExecuteAdminAsync($"CREATE LOGIN [{login}] WITH PASSWORD=N'{password}';")
                .ConfigureAwait(false);
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new SqlCommand($"CREATE USER [{login}] FOR LOGIN [{login}];", connection);
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        internal static async Task ApplyRoleScriptAsync(
            string connectionString,
            string fileName,
            string userVariable,
            string user)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var path = Path.Combine(FindRepositoryRoot(), "deploy", "sql", fileName);
            var script = (await File.ReadAllTextAsync(path).ConfigureAwait(false))
                .Replace("$(DatabaseName)", builder.InitialCatalog, StringComparison.Ordinal)
                .Replace($"$({userVariable})", user, StringComparison.Ordinal);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new SqlCommand(script, connection);
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        internal static async Task ExecuteBatchesAsync(string connectionString, string script)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using (var options = new SqlCommand("""
                SET ANSI_NULLS ON;
                SET ANSI_PADDING ON;
                SET ANSI_WARNINGS ON;
                SET ARITHABORT ON;
                SET CONCAT_NULL_YIELDS_NULL ON;
                SET QUOTED_IDENTIFIER ON;
                SET NUMERIC_ROUNDABORT OFF;
                """, connection))
            {
                _ = await options.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            foreach (var batch in Regex.Split(
                         script,
                         @"^\s*GO\s*$",
                         RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch))
                {
                    continue;
                }
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        internal static async Task ExecuteDatabaseSqlAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection);
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task<DatabaseStorageSnapshot> ReadStorageAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new SqlCommand("""
                SELECT
                    COALESCE(SUM(CASE WHEN files.[type] = 0 THEN files.[size] * CAST(8192 AS bigint) ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN files.[type] = 1 THEN files.[size] * CAST(8192 AS bigint) ELSE 0 END), 0),
                    COALESCE(MIN(volume.[available_bytes]), 0),
                    COALESCE(MAX(CASE WHEN files.[type] = 0 THEN files.[growth] END), 0),
                    COALESCE(MAX(CASE WHEN files.[type] = 0 THEN CONVERT(int, files.[is_percent_growth]) END), 0),
                    COALESCE(MAX(CASE WHEN files.[type] = 1 THEN files.[growth] END), 0),
                    COALESCE(MAX(CASE WHEN files.[type] = 1 THEN CONVERT(int, files.[is_percent_growth]) END), 0)
                FROM [sys].[database_files] AS files
                CROSS APPLY [sys].[dm_os_volume_stats](DB_ID(), files.[file_id]) AS volume;
                """, connection);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            _ = await reader.ReadAsync().ConfigureAwait(false);
            return new(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5),
                reader.GetInt32(6) == 1);
        }

        internal static Task DropLoginAsync(string login) => ExecuteAdminAsync($"""
            IF SUSER_ID(N'{login}') IS NOT NULL
                DROP LOGIN [{login}];
            """);

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync().ConfigureAwait(false);
            SqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END
                """).ConfigureAwait(false);
        }

        private static async Task ExecuteAdminAsync(string sql)
        {
            var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = "master"
            };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection);
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        internal static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
                {
                    return directory.FullName;
                }
            }
            throw new InvalidOperationException("The repository root could not be found.");
        }
    }

    private sealed record DatabaseStorageSnapshot(
        long DataBytes,
        long LogBytes,
        long VolumeAvailableBytes,
        int DataGrowthSetting,
        bool DataGrowthIsPercent,
        int LogGrowthSetting,
        bool LogGrowthIsPercent);

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
