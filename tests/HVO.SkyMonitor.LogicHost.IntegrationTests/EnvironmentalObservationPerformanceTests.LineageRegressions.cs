using System.Data.Common;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class EnvironmentalObservationPerformanceTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(1)]
    [DataRow(32)]
    [DataRow(128)]
    [DataRow(256)]
    public async Task BoundedLineagePreservesQualifiedOrdinalsWithoutTrackingInputs(int count)
    {
        await using var fixture = await LineageRegressionFixture.CreateAsync().ConfigureAwait(false);
        var fact = fixture.Derived(count);
        var probe = new LineageProbe(capture: true);
        await using var db = new ApplicationDbContext(LineageOptions(fixture.Connection, probe));
        var receipt = await fixture.Service(db).IngestAsync(fact).ConfigureAwait(false);
        await VerifyLineageAsync(LineageOptions(fixture.Connection), fact, receipt).ConfigureAwait(false);
        var replay = await IngestLineageAsync(LineageOptions(fixture.Connection), fixture.Telemetry, fact, BenchmarkNow.AddDays(1))
            .ConfigureAwait(false);
        Assert.AreEqual(receipt with { Disposition = EnvironmentalObservationIngestDisposition.Duplicate }, replay);
        Assert.AreEqual(2, probe.SourceLookups, "Own identity plus one bounded lineage source projection.");
        Assert.AreEqual(2, probe.ObservationLookups, "Own identity plus one exact qualified lineage observation projection.");
        Assert.AreEqual(1, db.ChangeTracker.Entries<EnvironmentalObservationRecord>().Count(), "Referenced entities must not enter the ingest graph.");
        Assert.IsTrue(probe.Commands.All(command => command.Parameters.Length + 3 < 2100), "Allow three sp_executesql RPC parameters.");
        TestContext.WriteLine("refs={0}; sources={1}; observations={2}; max_parameters={3}; tracked_observations={4}",
            count, probe.SourceLookups, probe.ObservationLookups, probe.Commands.Max(command => command.Parameters.Length),
            db.ChangeTracker.Entries<EnvironmentalObservationRecord>().Count());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task BoundedLineageRejectsCrossProductPairsAndMissingOrDuplicateInputsAtomically()
    {
        await using var fixture = await LineageRegressionFixture.CreateAsync().ConfigureAwait(false);
        var refs = fixture.References;
        var original = fixture.Derived(256);
        var cases = new Dictionary<string, EnvironmentalObservationReference[]>
        {
            ["missing-source"] = [.. refs.Take(255), new(new string('F', 64), refs[0].ObservationId)],
            ["missing-observation"] = [.. refs.Take(255), new(refs[0].SourceIdentitySha256, DeterministicGuid(999_001))],
            // Both the source and ID occur in the request, but this exact pair does not exist.
            ["mismatched-composite"] = [.. refs.Take(255), new(refs[2].SourceIdentitySha256, refs[0].ObservationId)],
            ["duplicate-reference"] = [.. refs.Take(255), refs[0]]
        };
        await using var db = new ApplicationDbContext(LineageOptions(fixture.Connection));
        var before = await RegressionCountsAsync(db).ConfigureAwait(false);
        foreach (var item in cases)
        {
            var fact = original with { Lineage = item.Value };
            await using var attempted = new ApplicationDbContext(LineageOptions(fixture.Connection));
            if (item.Key == "duplicate-reference")
                await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service(attempted).IngestAsync(fact)).ConfigureAwait(false);
            else
                await Assert.ThrowsAsync<EnvironmentalObservationConflictException>(() => fixture.Service(attempted).IngestAsync(fact)).ConfigureAwait(false);
            await attempted.SaveChangesAsync().ConfigureAwait(false);
            Assert.AreEqual(before, await RegressionCountsAsync(db).ConfigureAwait(false), item.Key);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task BoundedLineageMaximumDistinctSourcesStaysBelowSqlParameterLimit()
    {
        await using var fixture = await LineageRegressionFixture.CreateAsync(distinctSources: true).ConfigureAwait(false);
        var probe = new LineageProbe(capture: true);
        var fact = fixture.Derived(256);
        var receipt = await IngestLineageAsync(LineageOptions(fixture.Connection, probe), fixture.Telemetry, fact, BenchmarkNow)
            .ConfigureAwait(false);
        await VerifyLineageAsync(LineageOptions(fixture.Connection), fact, receipt).ConfigureAwait(false);
        Assert.AreEqual(2, probe.SourceLookups);
        Assert.AreEqual(2, probe.ObservationLookups);
        Assert.IsTrue(probe.Commands.All(command => command.Parameters.Length + 3 < 2100));
        TestContext.WriteLine("256 distinct sources; max_parameters={0}; RPC_overhead_allowance=3",
            probe.Commands.Max(command => command.Parameters.Length));
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BoundedLineageRetentionRaceIsAtomicInEitherLockOrder(bool ingestFirst)
    {
        await using var fixture = await LineageRegressionFixture.CreateAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var pause = new LineagePause(ingestFirst ? "INSERT INTO [EnvironmentalObservations]" : "FROM [EnvironmentalObservations]");
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(fixture.Connection)
            .AddInterceptors(pause).Options;
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(builder =>
        {
            builder.UseSqlServer(fixture.Connection);
            if (!ingestFirst) builder.AddInterceptors(pause);
        });
        await using var provider = services.BuildServiceProvider();
        var worker = new EnvironmentalObservationRetentionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EnvironmentalObservationOptions { ReceiptRetentionDays = 30, RetentionBatchSize = 1000 }),
            new FixedTimeProvider(BenchmarkNow), new EnvironmentalRetentionState(), fixture.Telemetry,
            NullLogger<EnvironmentalObservationRetentionWorker>.Instance);
        var fact = fixture.Derived(256);
        Task<EnvironmentalObservationIngestResult>? ingest = null;
        Task<int>? retention = null;
        await using var db = new ApplicationDbContext(ingestFirst ? dbOptions : LineageOptions(fixture.Connection));
        try
        {
            if (ingestFirst) ingest = fixture.Service(db).IngestAsync(fact, cancellation.Token);
            else retention = worker.SweepBatchAsync(cancellation.Token);
            await pause.Reached.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            if (ingestFirst) retention = worker.SweepBatchAsync(cancellation.Token);
            else ingest = fixture.Service(db).IngestAsync(fact, cancellation.Token);
            await AwaitSqlWaitAsync(fixture.Connection, "APPLICATION", cancellation.Token).ConfigureAwait(false);
            Assert.IsFalse(ingestFirst ? retention!.IsCompleted : ingest!.IsCompleted, "The other operation must wait on the retention lock.");
            pause.Release.TrySetResult();
            if (ingestFirst)
            {
                var receipt = await ingest!.ConfigureAwait(false);
                Assert.AreEqual(0, await retention!.ConfigureAwait(false));
                await VerifyLineageAsync(LineageOptions(fixture.Connection), fact, receipt).ConfigureAwait(false);
            }
            else
            {
                Assert.AreEqual(256, await retention!.ConfigureAwait(false));
                await Assert.ThrowsAsync<EnvironmentalObservationConflictException>(() => ingest!).ConfigureAwait(false);
            }
            await using var verify = new ApplicationDbContext(LineageOptions(fixture.Connection));
            Assert.AreEqual(ingestFirst ? 257 : 0, await verify.EnvironmentalObservations.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(ingestFirst ? 256 : 0, await verify.EnvironmentalObservationLineage.CountAsync().ConfigureAwait(false));
        }
        finally
        {
            pause.Release.TrySetResult();
            await EvidenceTaskCleanup.DrainAsync(new Task?[] { ingest, retention }.OfType<Task>().Select(ObserveLineageTaskAsync), cancellation, TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task BoundedLineageSqlDeadlockRollsBackPartialWritesAndFreshContextReplayConverges()
    {
        await using var fixture = await LineageRegressionFixture.CreateAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var blocker = new SqlConnection(fixture.Connection);
        await blocker.OpenAsync(cancellation.Token).ConfigureAwait(false);
        await using (var create = blocker.CreateCommand())
        {
            create.CommandText = "CREATE TABLE [LineageDeadlockProbe] ([Id] int NOT NULL PRIMARY KEY, [Value] int NOT NULL); INSERT INTO [LineageDeadlockProbe] VALUES (1,0);";
            await create.ExecuteNonQueryAsync(cancellation.Token).ConfigureAwait(false);
        }
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(cancellation.Token).ConfigureAwait(false);
        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = "UPDATE [LineageDeadlockProbe] SET [Value]=1 WHERE [Id]=1;";
            await hold.ExecuteNonQueryAsync(cancellation.Token).ConfigureAwait(false);
        }
        var fault = new LineageDeadlockFault();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(fixture.Connection).AddInterceptors(fault).Options;
        var fact = fixture.Derived(256);
        Task<EnvironmentalObservationIngestResult>? ingest = null;
        Task<object?>? competingRead = null;
        try
        {
            ingest = IngestLineageAsync(options, fixture.Telemetry, fact, BenchmarkNow, cancellation.Token);
            var derivedRecordId = await fault.PartialWrites.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            await using var read = blocker.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT [Id] FROM [EnvironmentalObservations] WITH (READCOMMITTEDLOCK) WHERE [Id]=@id;";
            read.Parameters.Add(new SqlParameter("@id", derivedRecordId));
            competingRead = read.ExecuteScalarAsync(cancellation.Token);
            await AwaitSqlWaitAsync(fixture.Connection, "KEY", cancellation.Token).ConfigureAwait(false);
            fault.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<Exception>(() => ingest).ConfigureAwait(false);
            Assert.IsTrue(IsLineageDeadlock(error), error.ToString());
            await competingRead.ConfigureAwait(false);
            await transaction.RollbackAsync(cancellation.Token).ConfigureAwait(false);
            await using (var verify = new ApplicationDbContext(LineageOptions(fixture.Connection)))
            {
                Assert.AreEqual(256, await verify.EnvironmentalObservations.CountAsync().ConfigureAwait(false));
                Assert.AreEqual(0, await verify.EnvironmentalObservationLineage.CountAsync().ConfigureAwait(false));
                Assert.IsFalse(await verify.EnvironmentalObservationSources.AnyAsync(row =>
                    row.IdentitySha256 == EnvironmentalObservationJson.ComputeSourceIdentitySha256(fact)).ConfigureAwait(false));
            }
            // Disposal represents loss of the failed request scope. A durable caller replays the same payload in a new scope.
            var accepted = await IngestLineageAsync(LineageOptions(fixture.Connection), fixture.Telemetry, fact, BenchmarkNow.AddMinutes(1))
                .ConfigureAwait(false);
            var duplicate = await IngestLineageAsync(LineageOptions(fixture.Connection), fixture.Telemetry, fact, BenchmarkNow.AddMinutes(2))
                .ConfigureAwait(false);
            Assert.AreEqual(accepted with { Disposition = EnvironmentalObservationIngestDisposition.Duplicate }, duplicate);
            await VerifyLineageAsync(LineageOptions(fixture.Connection), fact, duplicate).ConfigureAwait(false);
            await using var final = new ApplicationDbContext(LineageOptions(fixture.Connection));
            Assert.AreEqual(257, await final.EnvironmentalObservations.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(256, await final.EnvironmentalObservationLineage.CountAsync().ConfigureAwait(false));
            TestContext.WriteLine("SQL1205 after first EF write batch; zero acknowledged result; complete rollback; new-context replay accepted then duplicate.");
        }
        finally
        {
            fault.Release.TrySetResult();
            await EvidenceTaskCleanup.DrainAsync(new Task?[] { ingest, competingRead }.OfType<Task>().Select(ObserveLineageTaskAsync), cancellation, TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        }
    }

    private static Task ObserveLineageTaskAsync(Task task) => task.ContinueWith(completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task<(int Sources, int Observations, int Lineage)> RegressionCountsAsync(ApplicationDbContext db)
        => (await db.EnvironmentalObservationSources.CountAsync().ConfigureAwait(false),
            await db.EnvironmentalObservations.CountAsync().ConfigureAwait(false),
            await db.EnvironmentalObservationLineage.CountAsync().ConfigureAwait(false));

    private static async Task AwaitSqlWaitAsync(string connectionString, string resource, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id=DB_ID() AND resource_type=@resource AND request_status='WAIT';";
        command.Parameters.Add(new SqlParameter("@resource", resource));
        while ((int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0)
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
    }

    private sealed class LineagePause(string sqlFragment) : DbCommandInterceptor
    {
        private bool fired;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!fired && command.CommandText.Contains(sqlFragment, StringComparison.Ordinal))
            {
                fired = true;
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }

    private sealed class LineageDeadlockFault : DbCommandInterceptor
    {
        private Guid derivedRecordId;
        private bool fired;
        internal TaskCompletionSource<Guid> PartialWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [EnvironmentalObservations]", StringComparison.Ordinal))
                derivedRecordId = eventData.Context!.ChangeTracker.Entries<EnvironmentalObservationRecord>()
                    .Single(entry => entry.State == EntityState.Added).Entity.Id;
            else if (!fired && command.CommandText.Contains("INSERT INTO [EnvironmentalObservationLineage]", StringComparison.Ordinal))
            {
                fired = true;
                PartialWrites.TrySetResult(derivedRecordId);
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await using var fault = command.Connection!.CreateCommand();
                fault.Transaction = command.Transaction;
                fault.CommandText = "SET DEADLOCK_PRIORITY LOW; SELECT [Id] FROM [LineageDeadlockProbe] WITH (UPDLOCK) WHERE [Id]=1;";
                await fault.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }

    private sealed class LineageRegressionFixture(string connection, EnvironmentalObservationV1[] inputs,
        EnvironmentalObservationReference[] references, EnvironmentalObservationV1 derived) : IAsyncDisposable
    {
        internal string Connection => connection;
        internal EnvironmentalObservationV1[] Inputs => inputs;
        internal EnvironmentalObservationReference[] References => references;
        internal EnvironmentalObservationTelemetry Telemetry { get; } = new();
        internal EnvironmentalObservationV1 Derived(int count) => derived with { Lineage = references.Take(count).ToArray() };
        internal EnvironmentalObservationIngestService Service(ApplicationDbContext db) => new(db, new FixedTimeProvider(BenchmarkNow),
            Options.Create(new EnvironmentalObservationOptions()), Telemetry, NullLogger<EnvironmentalObservationIngestService>.Instance);

        internal static async Task<LineageRegressionFixture> CreateAsync(bool distinctSources = false)
        {
            var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = $"SkyMonitorLineage1558_{Guid.NewGuid():N}",
                ApplicationName = "HVO.Issue1558.SyntheticRegression"
            }.ConnectionString;
            await using var db = new ApplicationDbContext(LineageOptions(connection));
            try
            {
                await db.Database.MigrateAsync().ConfigureAwait(false);
                var target = await SeedTargetAsync(db).ConfigureAwait(false);
                var inputs = Enumerable.Range(0, 256).Select(index => CreateObservation(index, target.SiteId, target.AgentId) with
                {
                    ObservationId = DeterministicGuid(600_000 + index / 2),
                    Source = CreateObservation(index % 8, target.SiteId, target.AgentId).Source with
                    {
                        SourceId = distinctSources ? $"input-{index}" : $"input-{index % 8}"
                    }
                }).ToArray();
                using var telemetry = new EnvironmentalObservationTelemetry();
                foreach (var input in inputs)
                    await IngestLineageAsync(LineageOptions(connection), telemetry, input, Epoch).ConfigureAwait(false);
                var references = Enumerable.Range(0, 256).Select(index => inputs[index * 73 % 256])
                    .Select(input => new EnvironmentalObservationReference(
                        EnvironmentalObservationJson.ComputeSourceIdentitySha256(input), input.ObservationId)).ToArray();
                var derived = CreateObservation(700_000, target.SiteId, target.AgentId) with
                {
                    Source = inputs[0].Source with { Provider = "issue-1558-derived", SourceId = "derived", Kind = EnvironmentalObservationSourceKind.Derived }
                };
                return new(connection, inputs, references, derived);
            }
            catch
            {
                await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Telemetry.Dispose();
            await using var db = new ApplicationDbContext(LineageOptions(connection));
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }
}
