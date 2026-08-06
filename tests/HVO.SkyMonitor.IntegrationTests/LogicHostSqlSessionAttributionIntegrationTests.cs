using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class LogicHostSqlSessionAttributionIntegrationTests
{
    [TestMethod]
    public async Task ProgramRegisteredEfAndDedicatedObjectLockSessions_UseRuntimeApplicationName()
    {
        var fixture = AssemblyHooks.Fixture;
        var fixtureConfiguration = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        var configured = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            ApplicationName = "conflicting-client"
        };
        using var factory = new WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program>()
            .WithWebHostBuilder(builder =>
            {
                // Staging exercises the runtime profile and read-only startup validation without rerunning migrations.
                builder.UseEnvironment(Environments.Staging);
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(fixtureConfiguration.AsEnumerable());
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:skymonitordb"] = configured.ConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = configured.ConnectionString
                    });
                });
                builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            });
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.OpenConnectionAsync().ConfigureAwait(false);
        try
        {
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT APP_NAME();";
                (await command.ExecuteScalarAsync().ConfigureAwait(false)).Should()
                    .Be(LogicHostSqlConnectionProfiles.RuntimeApplicationName);
            }

            await using var applicationLock = await CentralObjectApplicationLock.AcquireAsync(
                db,
                $"issue-254-attribution/{Guid.NewGuid():N}",
                CancellationToken.None).ConfigureAwait(false);
            await using var sampler = new SqlConnection(fixture.SqlServerConnectionString);
            await sampler.OpenAsync().ConfigureAwait(false);
            await using var sample = new SqlCommand("""
                SELECT [session].[program_name]
                FROM [sys].[dm_tran_locks] AS [application_lock]
                INNER JOIN [sys].[dm_exec_sessions] AS [session]
                    ON [session].[session_id] = [application_lock].[request_session_id]
                WHERE [application_lock].[resource_type] = N'APPLICATION'
                    AND [application_lock].[request_owner_type] = N'SESSION'
                    AND [application_lock].[request_status] = N'GRANT'
                    AND [application_lock].[resource_database_id] = DB_ID();
                """, sampler);
            var applicationNames = new List<string>();
            await using var reader = await sample.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                applicationNames.Add(reader.GetString(0));
            }

            applicationNames.Should().ContainSingle()
                .Which.Should().Be(LogicHostSqlConnectionProfiles.RuntimeApplicationName);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
