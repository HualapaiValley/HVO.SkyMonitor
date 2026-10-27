using FluentAssertions;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralDerivativeWindowIntegrationTests
{
    [TestMethod]
    public async Task RuntimeSignals_IsolatedFromForeignUnsealedGraph()
    {
        // Migration/guard tests intentionally leave valid unfinished expansions in the shared fixture.
        // Reproduce that state deterministically, regardless of the assembly's test order.
        var sourceId = await SeedAndScheduleSourceAsync(
            $"signals-foreign-{Guid.NewGuid():N}", Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-10),
            CreatePayload(100), "signals-foreign", graphNodeIds: ["Preview"]).ConfigureAwait(false);
        Guid executionId;
        Guid unsealedJobId;
        string executionBefore;
        string jobBefore;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.SourceCentralArtifactId == sourceId).ConfigureAwait(false);
            var (unsealedJob, execution) = await CentralProcessingGraphMigrationTests.AddUnsealedFixtureGraphAsync(
                db, job, DateTimeOffset.UtcNow, "signals-isolation-regression").ConfigureAwait(false);
            executionId = execution.Id;
            unsealedJobId = unsealedJob.Id;
            executionBefore = JsonSerializer.Serialize(await db.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == executionId).ConfigureAwait(false));
            jobBefore = JsonSerializer.Serialize(await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.Id == unsealedJobId).ConfigureAwait(false));
        }
        await CentralTransientRuntime_EmitsBoundedConnectedPrivateOperationalSignals().ConfigureAwait(false);
        await using var assertionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await assertionDb.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync(item => item.Id == executionId)
            .ConfigureAwait(false);
        persisted.ExpandedAtUtc.Should().BeNull("the isolated run must leave the foreign unfinished expansion intact");
        persisted.Status.Should().Be(CentralProcessingGraphExecutionStatus.Pending);
        JsonSerializer.Serialize(persisted).Should().Be(executionBefore);
        JsonSerializer.Serialize(await assertionDb.CentralDerivativeJobs.AsNoTracking()
            .SingleAsync(item => item.Id == unsealedJobId).ConfigureAwait(false)).Should().Be(jobBefore);
    }

    private sealed class IsolatedRuntimeFixture : IAsyncDisposable
    {
        private readonly ApplicationDbContext database;
        internal WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> Factory { get; }

        private IsolatedRuntimeFixture(ApplicationDbContext database, WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
        {
            this.database = database;
            Factory = factory;
        }

        internal static async Task<IsolatedRuntimeFixture> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = $"SkyMonitorRuntimeSignals_{Guid.NewGuid():N}"
            }.ConnectionString;
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
            try
            {
                await database.Database.MigrateAsync().ConfigureAwait(false);
                var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                        services.RemoveAll<ApplicationDbContext>();
                        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connection)
                            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
                    }));
                return new IsolatedRuntimeFixture(database, factory);
            }
            catch
            {
                await database.Database.EnsureDeletedAsync().ConfigureAwait(false);
                await database.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Factory.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await database.Database.EnsureDeletedAsync().ConfigureAwait(false);
                await database.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
