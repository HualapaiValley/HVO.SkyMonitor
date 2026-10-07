using System.Globalization;
using System.Text;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Canonical central graph seed supersession on SQL Server. A superseding chain is the shipped chain plus one more
/// entry generated here, so these tests exercise the supersession mechanism whatever the shipped chain's length.
/// </summary>
public sealed partial class CentralProcessingGraphMigrationTests
{
    [TestMethod]
    public async Task SqlServerSeedSupersessionOfAShippedDatabaseAppendsOnlyTheCurrentEntryAndRestartsAsANoOp()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            await InsertShippedRevisionOneAsync(context).ConfigureAwait(false);
            var shipped = await SnapshotSeedRowsAsync(context).ConfigureAwait(false);
            var (chain, definition) = CreateSupersedingChain();

            var report = await ConvergeAsync(context, chain, definition).ConfigureAwait(false);

            report.Should().BeEquivalentTo(new CanonicalCentralGraphSeedReport(0, 0, []));
            var converged = await SnapshotSeedRowsAsync(context).ConfigureAwait(false);
            converged.Revisions.Should().ContainEquivalentOf(shipped.Revisions.Single(),
                "a historical seed revision is validated, never rewritten");
            converged.Assignments.Should().ContainEquivalentOf(shipped.Assignments.Single());
            converged.Revisions.Select(static item => item.Id).Should().BeEquivalentTo(
                [CanonicalCentralGraphSeedChain.Revisions[0].RevisionId, chain[^1].RevisionId],
                "an intermediate entry the database never received is not inserted on supersession");
            var current = converged.Revisions.Single(item => item.Id == chain[^1].RevisionId);
            current.Revision.Should().Be(chain[^1].Revision);
            current.CentralPlanIdentitySha256.Should().Be(chain[^1].CentralPlanIdentitySha256);
            current.PublishedAtUtc.Should().Be(chain[^1].SeededAtUtc);
            (await CreateCatalog(context).ResolveAsync(
                    CentralProcessingGraphTargetHost.Central, Guid.NewGuid(), null, DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false))!
                .Id.Should().Be(chain[^1].AssignmentId, "the newest global default wins resolution");

            context.ChangeTracker.Clear();
            var restarted = await ConvergeAsync(context, chain, definition).ConfigureAwait(false);

            restarted.Should().BeEquivalentTo(new CanonicalCentralGraphSeedReport(0, 0, []));
            (await SnapshotSeedRowsAsync(context).ConfigureAwait(false)).Should().BeEquivalentTo(converged,
                "a restart on the same chain changes nothing");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerSeedSupersessionFailsClosedOnATamperedHistoricalRevision()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var shippedJson = ReadShippedRevisionOneJson();
            const string original = "\"asinhStrength\":4";
            shippedJson.Should().Contain(original);
            await InsertShippedRevisionOneAsync(
                context, shippedJson.Replace(original, "\"asinhStrength\":5", StringComparison.Ordinal))
                .ConfigureAwait(false);
            var (chain, definition) = CreateSupersedingChain();

            var converge = () => ConvergeAsync(context, chain, definition);

            await converge.Should().ThrowExactlyAsync<InvalidOperationException>()
                .WithMessage("The historical canonical central graph seed revision 1 has conflicting content.")
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var current = chain[^1];
            (await context.CentralProcessingGraphRevisions.AnyAsync(item => item.Id == current.RevisionId)
                .ConfigureAwait(false)).Should().BeFalse("nothing is persisted when a historical row is tampered");
            (await context.CentralProcessingGraphAssignments.AnyAsync(item => item.Id == current.AssignmentId)
                .ConfigureAwait(false)).Should().BeFalse();
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerSeedSupersessionOfAFreshDatabaseInsertsOnlyTheCurrentEntry()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var (chain, definition) = CreateSupersedingChain();

            var report = await ConvergeAsync(context, chain, definition).ConfigureAwait(false);

            report.Should().BeEquivalentTo(new CanonicalCentralGraphSeedReport(0, 0, []));
            var rows = await SnapshotSeedRowsAsync(context).ConfigureAwait(false);
            rows.Revisions.Select(static item => item.Id).Should().Equal(chain[^1].RevisionId);
            rows.Assignments.Select(static item => item.Id).Should().Equal(chain[^1].AssignmentId);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerBacklogSourceIngestedBeforeSupersessionExpandsOnTheCurrentSeedRevision()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await InsertShippedRevisionOneAsync(context).ConfigureAwait(false);
            // Committed before the upgrade, never expanded: the camera has no assignment of its own.
            var camera = await SeedCameraAsync(context, now, "seed-backlog").ConfigureAwait(false);
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'B', now.AddMinutes(-1));
            context.Add(raw);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var (chain, definition) = CreateSupersedingChain();
            _ = await ConvergeAsync(context, chain, definition).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            await ScheduleLiveAsync(context, raw.Id, now).ConfigureAwait(false);

            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.AnchorSourceCentralArtifactId == raw.Id).ConfigureAwait(false);
            execution.RevisionId.Should().Be(chain[^1].RevisionId);
            execution.CentralPlanIdentitySha256.Should().Be(chain[^1].CentralPlanIdentitySha256);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerInFlightExecutionStaysFrozenOnItsSeedRevisionAndMismatchedJobsAreCounted()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var shipped = CanonicalCentralGraphSeedChain.Revisions;
            _ = await ConvergeAsync(context, shipped, DatabaseSeeder.CreateBasicCentralProcessingGraph())
                .ConfigureAwait(false);
            var camera = await SeedCameraAsync(context, now, "seed-in-flight").ConfigureAwait(false);
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'C', now.AddMinutes(-1));
            context.Add(raw);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            await ScheduleLiveAsync(context, raw.Id, now).ConfigureAwait(false);
            var frozen = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.AnchorSourceCentralArtifactId == raw.Id).ConfigureAwait(false);
            frozen.RevisionId.Should().Be(shipped[^1].RevisionId);
            frozen.Status.Should().BeOneOf(
                CentralProcessingGraphExecutionStatus.Pending, CentralProcessingGraphExecutionStatus.Running);
            var jobs = await context.CentralDerivativeJobs.AsNoTracking()
                .Where(item => item.GraphExecutionId == frozen.Id)
                .Select(item => new { item.Id, item.RecipeName, item.Status, item.RequestedRecipeIdentitySha256 })
                .ToListAsync().ConfigureAwait(false);
            // One job frozen by an earlier recipe implementation, as a recipe ImplementationVersion change leaves it.
            // A graph job's frozen identity is immutable once expanded, so this test-only setup bypasses the trigger
            // on this test's own database and restores it whatever happens.
            var stale = jobs.First(item => !CentralDerivativeJobExecutor.IsInProcessOnlyRecipe(item.RecipeName));
            var staleIdentity = new string('F', 64);
            await context.Database.ExecuteSqlRawAsync(
                "DISABLE TRIGGER [TR_CentralDerivativeJobs_GraphIdentityImmutable] ON [CentralDerivativeJobs];")
                .ConfigureAwait(false);
            try
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs]
                    SET [RequestedRecipeIdentitySha256] = {staleIdentity}
                    WHERE [Id] = {stale.Id};
                    """).ConfigureAwait(false);
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync(
                    "ENABLE TRIGGER [TR_CentralDerivativeJobs_GraphIdentityImmutable] ON [CentralDerivativeJobs];")
                    .ConfigureAwait(false);
            }
            var (chain, definition) = CreateSupersedingChain();

            var report = await ConvergeAsync(context, chain, definition).ConfigureAwait(false);

            report.SupersededSeedExecutionCount.Should().Be(1);
            report.FrozenIdentityMismatchJobCount.Should().Be(1,
                "only the job whose frozen identity this binary no longer derives fails on its first lease");
            report.StaleAssignments.Should().BeEmpty();
            context.ChangeTracker.Clear();
            var after = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == frozen.Id).ConfigureAwait(false);
            after.RevisionId.Should().Be(frozen.RevisionId, "an expanded execution is never re-planned");
            after.CentralPlanIdentitySha256.Should().Be(frozen.CentralPlanIdentitySha256);
            after.Status.Should().Be(frozen.Status);
            (await context.CentralDerivativeJobs.AsNoTracking()
                    .Where(item => item.GraphExecutionId == frozen.Id)
                    .Select(item => new { item.Id, item.Status })
                    .ToListAsync().ConfigureAwait(false))
                .Should().BeEquivalentTo(jobs.Select(static item => new { item.Id, item.Status }),
                    "seeding counts frozen work and changes none of it");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerStaleOperatorAssignmentIsLoggedAndDegradesHealthWithoutBeingRewritten()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
            _ = await ConvergeAsync(
                    context, CanonicalCentralGraphSeedChain.Revisions, DatabaseSeeder.CreateBasicCentralProcessingGraph())
                .ConfigureAwait(false);
            var camera = await SeedCameraAsync(context, now, "seed-stale-assignment").ConfigureAwait(false);
            var definitionJson = ArtifactIngestTests.WithStaleAnnotationImplementationVersion(
                Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(
                    DatabaseSeeder.CreateBasicCentralProcessingGraph() with { Name = "seed-stale-assignment" })));
            var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(definitionJson));
            var portable = ProcessingGraphCompiler.Compile(parsed.Definition!);
            var central = LogicHostProcessingGraphAdapter.Compile(parsed.Definition!, registry.Capabilities);
            var revision = new CentralProcessingGraphRevision
            {
                Name = parsed.Definition!.Name,
                Revision = parsed.Definition.Revision,
                DefinitionJson = definitionJson,
                DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
                CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
                CreatedAtUtc = now.AddMinutes(-2),
                CreatedByUserId = camera.Owner.Id,
                PublishedAtUtc = now.AddMinutes(-2),
                PublishedByUserId = camera.Owner.Id
            };
            var assignment = new CentralProcessingGraphAssignment
            {
                Revision = revision,
                RevisionId = revision.Id,
                TargetHost = CentralProcessingGraphTargetHost.Central,
                Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
                ObservatoryId = camera.Observatory.Id,
                LogicalCameraId = camera.Camera.Id,
                EffectiveFromUtc = now.AddMinutes(-1),
                CreatedAtUtc = now.AddMinutes(-1),
                ActorUserId = camera.Owner.Id,
                ReasonCode = "seed-stale-assignment"
            };
            revision.Assignments.Add(assignment);
            context.AddRange(revision, assignment);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var logger = new SeedRecordingLogger();

            var report = await DatabaseSeeder.ConvergeCanonicalCentralGraphChainAsync(
                context, registry, CanonicalCentralGraphSeedChain.Revisions,
                DatabaseSeeder.CreateBasicCentralProcessingGraph(), DateTimeOffset.UtcNow, logger).ConfigureAwait(false);

            report.StaleAssignments.Should().ContainSingle().Which.Should().Be(new CanonicalCentralGraphStaleAssignment(
                assignment.Id, revision.Id, CentralProcessingGraphAssignmentScope.LogicalCamera, camera.Observatory.Id,
                camera.Camera.Id, CentralProcessingGraphPlanVerification.UnsupportedReason,
                BuiltInProcessingRecipes.Annotation));
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains(assignment.Id.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(revision.Id.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains($"logical camera {camera.Camera.Id}", StringComparison.Ordinal));
            context.ChangeTracker.Clear();
            var persisted = await context.CentralProcessingGraphAssignments.AsNoTracking()
                .SingleAsync(item => item.Id == assignment.Id).ConfigureAwait(false);
            persisted.RevisionId.Should().Be(revision.Id, "startup never rewrites an operator assignment");
            persisted.EffectiveUntilUtc.Should().BeNull();

            var health = await new ProcessingGraphCatalogHealthCheck(context, registry, TimeProvider.System)
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

            health.Status.Should().Be(HealthStatus.Degraded);
            health.Data["staleAssignmentCount"].Should().Be(1);
            health.Data["staleAssignmentIds"].Should().Be(assignment.Id.ToString());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private sealed record SeedRows(
        IReadOnlyList<CentralProcessingGraphRevision> Revisions,
        IReadOnlyList<CentralProcessingGraphAssignment> Assignments);

    private static async Task<ApplicationDbContext> CreateSeedSupersessionDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorSeedSupersession_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync().ConfigureAwait(false);
        return context;
    }

    /// <summary>
    /// The shipped chain plus one generated entry: the current code's graph under the next revision number, with new
    /// identifiers, one tick after its predecessor.
    /// </summary>
    private static (CanonicalCentralGraphSeedRevision[] Chain, ProcessingGraphDefinition Definition)
        CreateSupersedingChain()
    {
        var shipped = CanonicalCentralGraphSeedChain.Revisions;
        var predecessor = shipped[^1];
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph() with
        {
            Revision = (int.Parse(predecessor.Revision, CultureInfo.InvariantCulture) + 1)
                .ToString(CultureInfo.InvariantCulture)
        };
        var (_, definitionJsonSha256, portable, central) = CanonicalCentralGraphSeedChain.Compile(definition);
        var next = new CanonicalCentralGraphSeedRevision(
            definition.Revision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            predecessor.SeededAtUtc.AddTicks(1),
            definitionJsonSha256,
            portable.Plan!.DefinitionIdentitySha256,
            portable.Plan.PlanIdentitySha256,
            central.Plan!.PlanIdentitySha256);
        CanonicalCentralGraphSeedRevision[] chain = [.. shipped, next];
        CanonicalCentralGraphSeedChain.Validate(chain);
        return (chain, definition);
    }

    private static Task<CanonicalCentralGraphSeedReport> ConvergeAsync(
        ApplicationDbContext context,
        IReadOnlyList<CanonicalCentralGraphSeedRevision> chain,
        ProcessingGraphDefinition definition)
        => DatabaseSeeder.ConvergeCanonicalCentralGraphChainAsync(
            context,
            new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog()),
            chain,
            definition,
            DateTimeOffset.UtcNow,
            NullLogger.Instance);

    private static string ReadShippedRevisionOneJson()
        => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "canonical-central-graph-rev1-b13f0d0e.json"));

    /// <summary>The revision 1 rows exactly as b13f0d0e seeded them, from its byte-exact stored definition.</summary>
    private static async Task InsertShippedRevisionOneAsync(ApplicationDbContext context, string? definitionJson = null)
    {
        var entry = CanonicalCentralGraphSeedChain.Revisions[0];
        context.CentralProcessingGraphRevisions.Add(new CentralProcessingGraphRevision
        {
            Id = entry.RevisionId,
            Name = CanonicalCentralGraphSeedChain.GraphName,
            Revision = entry.Revision,
            DefinitionJson = definitionJson ?? ReadShippedRevisionOneJson(),
            DefinitionIdentitySha256 = entry.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = entry.PortablePlanIdentitySha256,
            CentralPlanIdentitySha256 = entry.CentralPlanIdentitySha256,
            CreatedAtUtc = entry.SeededAtUtc,
            CreatedByUserId = CanonicalCentralGraphSeedChain.SeedActorUserId,
            PublishedAtUtc = entry.SeededAtUtc,
            PublishedByUserId = CanonicalCentralGraphSeedChain.SeedActorUserId
        });
        context.CentralProcessingGraphAssignments.Add(new CentralProcessingGraphAssignment
        {
            Id = entry.AssignmentId,
            RevisionId = entry.RevisionId,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.GlobalDefault,
            EffectiveFromUtc = entry.SeededAtUtc,
            CreatedAtUtc = entry.SeededAtUtc,
            ActorUserId = CanonicalCentralGraphSeedChain.SeedActorUserId,
            ReasonCode = CanonicalCentralGraphSeedChain.AssignmentReasonCode
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private static async Task<SeedRows> SnapshotSeedRowsAsync(ApplicationDbContext context)
        => new(
            await context.CentralProcessingGraphRevisions.AsNoTracking()
                .Where(item => item.Name == CanonicalCentralGraphSeedChain.GraphName)
                .OrderBy(item => item.CreatedAtUtc)
                .ToListAsync().ConfigureAwait(false),
            await context.CentralProcessingGraphAssignments.AsNoTracking()
                .Where(item => item.ActorUserId == CanonicalCentralGraphSeedChain.SeedActorUserId)
                .OrderBy(item => item.EffectiveFromUtc)
                .ToListAsync().ConfigureAwait(false));

    /// <summary>Live-schedules one source through the job scheduler, as ingest does, resolving at the wall clock.</summary>
    private static async Task ScheduleLiveAsync(ApplicationDbContext context, Guid centralArtifactId, DateTimeOffset now)
    {
        var recipeCatalog = new CentralDerivativeRecipeCatalog();
        var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var catalog = new ProcessingGraphCatalogService(
            context, registry, TimeProvider.System, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
        var graphScheduler = new CentralProcessingGraphScheduler(
            context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
            new CentralDerivativeWorkerTelemetry(), TimeProvider.System);
        var jobScheduler = new CentralDerivativeJobScheduler(
            context, recipeCatalog, new NoopWindowResolver(), graphScheduler: graphScheduler);
        var artifact = await context.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(item => item.Id == centralArtifactId).ConfigureAwait(false);
        await jobScheduler.EnsureRequiredJobsAsync(artifact, now, CancellationToken.None).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private sealed class SeedRecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
