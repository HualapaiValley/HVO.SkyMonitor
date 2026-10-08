using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// The single expected-identity exemption in <c>TR_CentralDerivativeJobs_GraphIdentityImmutable</c>, on SQL Server: an
/// annotation whose optional measured-association auxiliary was omitted may re-derive its expected identity exactly
/// while it is Waiting, never attempted or leased, and its Missing measured-association requirement is durable. Each
/// rejected boundary runs alone in its own rolled-back transaction against one expanded canonical graph, and the
/// scheduler's omission path runs against both the current trigger and the trigger an earlier baseline created.
/// </summary>
public sealed partial class CentralProcessingGraphMigrationTests
{
    private const string GraphIdentityImmutableMessage = "Derivative graph executable identity is immutable.";

    [TestMethod]
    public async Task SqlServerTriggerAdmitsOnlyTheExactAnnotationExpectedIdentityRederivation()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var graph = await ExpandCanonicalGraphAsync(context, now, "rederivation-boundaries").ConfigureAwait(false);
            var rederived = new string('E', 64);

            foreach (var boundary in RejectedRederivationBoundaries)
            {
                await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                var target = await ArrangeRederivationBoundaryAsync(context, graph, boundary, now).ConfigureAwait(false);

                Func<Task> rederive = () => boundary switch
                {
                    "transitions-to-pending" => context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [CentralDerivativeJobs]
                        SET [ExpectedRecipeIdentitySha256] = {rederived}, [Status] = N'Pending'
                        WHERE [Id] = {target};
                        """),
                    "other-frozen-column" => context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [CentralDerivativeJobs]
                        SET [ExpectedRecipeIdentitySha256] = {rederived},
                            [TraceParent] = N'00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01'
                        WHERE [Id] = {target};
                        """),
                    "requested-identity" => context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [CentralDerivativeJobs]
                        SET [ExpectedRecipeIdentitySha256] = {rederived}, [RequestedRecipeIdentitySha256] = {rederived}
                        WHERE [Id] = {target};
                        """),
                    "request-identity" => context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [CentralDerivativeJobs]
                        SET [ExpectedRecipeIdentitySha256] = {rederived}, [RequestIdentitySha256] = {rederived}
                        WHERE [Id] = {target};
                        """),
                    _ => context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [CentralDerivativeJobs]
                        SET [ExpectedRecipeIdentitySha256] = {rederived}
                        WHERE [Id] = {target};
                        """)
                };

                (await rederive.Should().ThrowAsync<SqlException>($"the {boundary} boundary is outside the exemption")
                        .ConfigureAwait(false))
                    .Where(exception => exception.Number == 51000 &&
                        exception.Message.Contains(GraphIdentityImmutableMessage, StringComparison.Ordinal),
                        $"the {boundary} boundary must be rejected by the graph identity trigger itself");
            }

            await using (var accepted = await context.Database.BeginTransactionAsync().ConfigureAwait(false))
            {
                var target = await ArrangeRederivationBoundaryAsync(context, graph, "exact", now).ConfigureAwait(false);
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs]
                    SET [ExpectedRecipeIdentitySha256] = {rederived}
                    WHERE [Id] = {target};
                    """).ConfigureAwait(false);
                var stored = await context.CentralDerivativeJobs.AsNoTracking()
                    .Where(item => item.Id == target)
                    .Select(item => new
                    {
                        item.ExpectedRecipeIdentitySha256,
                        item.RequestedRecipeIdentitySha256,
                        item.RequestIdentitySha256,
                        item.Status
                    })
                    .SingleAsync().ConfigureAwait(false);
                stored.ExpectedRecipeIdentitySha256.Should().Be(rederived);
                stored.RequestedRecipeIdentitySha256.Should().Be(graph.Annotation.RequestedRecipeIdentitySha256);
                stored.RequestIdentitySha256.Should().Be(graph.Annotation.RequestIdentitySha256);
                stored.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
                await accepted.RollbackAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerOmittedMeasuredAssociationsRederiveTheAnnotationIdentityOnceThroughTheTrigger()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var graph = await ExpandCanonicalGraphAsync(context, now, "rederivation-omitted").ConfigureAwait(false);
            await SkipMeasuredAssociationsAsync(context, graph, now.AddSeconds(1)).ConfigureAwait(false);

            await ConvergeGraphAsync(context, graph.ExecutionId, now.AddSeconds(2)).ConfigureAwait(false);
            var first = await ReadAnnotationAsync(context, graph).ConfigureAwait(false);
            await ConvergeGraphAsync(context, graph.ExecutionId, now.AddSeconds(3)).ConfigureAwait(false);
            var second = await ReadAnnotationAsync(context, graph).ConfigureAwait(false);

            first.Requirement.ResolutionState.Should().Be(CentralDerivativeInputResolutionState.Missing);
            first.Requirement.ResolutionReasonCode.Should().Be("processing.graph.optional-dependency-omitted");
            first.Job.ExpectedRecipeIdentitySha256.Should().NotBe(graph.Annotation.ExpectedRecipeIdentitySha256,
                "the omitted auxiliary leaves the frozen expectation, so the annotation re-derives it");
            first.Job.StateReasonCode.Should().NotBe(CentralProcessingGraphScheduler.AnnotationRederivationFailedReasonCode)
                .And.NotBe(CentralProcessingGraphScheduler.AnnotationRederivationUnsupportedSchemaReasonCode);
            first.Job.RequestedRecipeIdentitySha256.Should().Be(graph.Annotation.RequestedRecipeIdentitySha256);
            first.Job.RequestIdentitySha256.Should().Be(graph.Annotation.RequestIdentitySha256,
                "the request identity stays frozen on the original expectation and only keys the unique index");
            second.Job.ExpectedRecipeIdentitySha256.Should().Be(first.Job.ExpectedRecipeIdentitySha256,
                "re-derivation is idempotent across convergence passes");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerEarlierBaselineTriggerTerminalizesTheAnnotationInsteadOfRetryingConvergence()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var graph = await ExpandCanonicalGraphAsync(context, now, "rederivation-old-trigger").ConfigureAwait(false);
            var current = await ReadGraphIdentityTriggerDefinitionAsync(context).ConfigureAwait(false);
            await SkipMeasuredAssociationsAsync(context, graph, now.AddSeconds(1)).ConfigureAwait(false);
            Func<Task> converge = () => ConvergeGraphAsync(context, graph.ExecutionId, now.AddSeconds(2));

            // This test's own database carries the earlier trigger only inside this block, and gets the current one back
            // whatever happens, so nothing else can observe it.
            await context.Database.ExecuteSqlRawAsync(CreateEarlierBaselineGraphIdentityTrigger(current))
                .ConfigureAwait(false);
            try
            {
                await converge.Should().NotThrowAsync("an earlier baseline must not make convergence retry without bound")
                    .ConfigureAwait(false);
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync(AlterTrigger(current)).ConfigureAwait(false);
            }
            (await ReadGraphIdentityTriggerDefinitionAsync(context).ConfigureAwait(false))
                .Should().Contain(CentralProcessingGraphScheduler.ExpectedIdentityRederivationTriggerMarker,
                    "the current trigger is restored after the earlier-baseline block");
            var annotation = await ReadAnnotationAsync(context, graph).ConfigureAwait(false);
            annotation.Job.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
            annotation.Job.StateReasonCode.Should().Be(
                CentralProcessingGraphScheduler.AnnotationRederivationUnsupportedSchemaReasonCode);
            annotation.Job.ExpectedRecipeIdentitySha256.Should().Be(graph.Annotation.ExpectedRecipeIdentitySha256,
                "the unsupported trigger is never asked to accept the re-derived identity");
            annotation.Requirement.ResolutionState.Should().Be(CentralDerivativeInputResolutionState.Missing);
            var others = await context.CentralDerivativeJobs.AsNoTracking()
                .Where(item => item.GraphExecutionId == graph.ExecutionId && item.Id != graph.Annotation.Id)
                .Select(item => item.StateReasonCode)
                .ToListAsync().ConfigureAwait(false);
            others.Should().NotContain(CentralProcessingGraphScheduler.AnnotationRederivationUnsupportedSchemaReasonCode);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Two convergences of one execution race the re-derivation. The first is held immediately after its identity
    /// statement, so the trigger has accepted the write and its locks are held; the second must then block on the
    /// execution row lock convergence takes first, rather than read the Waiting requirement and re-derive again. Once the
    /// first commits, the second reads the durable Missing requirement and writes nothing more.
    /// </summary>
    [TestMethod]
    public async Task SqlServerConcurrentConvergencesRederiveTheAnnotationIdentityOnce()
    {
        await using var context = await CreateSeedSupersessionDatabaseAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var graph = await ExpandCanonicalGraphAsync(context, now, "rederivation-contention").ConfigureAwait(false);
            await SkipMeasuredAssociationsAsync(context, graph, now.AddSeconds(1)).ConfigureAwait(false);
            var connectionString = context.Database.GetConnectionString()!;
            var park = new ParkAfterIdentityWriteInterceptor();
            await using var first = CreateRederivationContext(connectionString, park);
            await using var second = CreateRederivationContext(connectionString, null);
            await first.Database.OpenConnectionAsync().ConfigureAwait(false);
            var firstSession = await first.Database.SqlQuery<short>($"SELECT @@SPID AS [Value]")
                .SingleAsync().ConfigureAwait(false);
            var timeout = TimeSpan.FromSeconds(30);
            Task? firstConvergence = null;
            Task? secondConvergence = null;
            try
            {
                firstConvergence = Task.Run(() => ConvergeGraphAsync(first, graph.ExecutionId, now.AddSeconds(2)));
                await park.Entered.WaitAsync(timeout).ConfigureAwait(false);
                secondConvergence = Task.Run(() => ConvergeGraphAsync(second, graph.ExecutionId, now.AddSeconds(3)));
                var deadline = DateTimeOffset.UtcNow + timeout;
                while (await context.Database.SqlQuery<int>($"""
                           SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE [blocking_session_id] = {firstSession}
                           """).SingleAsync().ConfigureAwait(false) == 0)
                {
                    secondConvergence.IsCompleted.Should().BeFalse(
                        "the second convergence must wait for the first instead of converging the Waiting requirement");
                    DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the second convergence never blocked");
                    await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                }
            }
            finally
            {
                park.Release();
            }
            await firstConvergence!.WaitAsync(timeout).ConfigureAwait(false);
            await secondConvergence!.WaitAsync(timeout).ConfigureAwait(false);
            park.Count.Should().Be(1, "only one convergence ever issues the identity statement");

            var annotation = await ReadAnnotationAsync(context, graph).ConfigureAwait(false);
            annotation.Requirement.ResolutionState.Should().Be(CentralDerivativeInputResolutionState.Missing);
            annotation.Job.Status.Should().Be(CentralDerivativeJobStatus.Pending, annotation.Job.StateReasonCode);
            // The fixture frame carries no scene provenance, so the no-auxiliary identity is the requested identity.
            annotation.Job.ExpectedRecipeIdentitySha256.Should().Be(annotation.Job.RequestedRecipeIdentitySha256);
            annotation.Job.RequestIdentitySha256.Should().Be(graph.Annotation.RequestIdentitySha256);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static ApplicationDbContext CreateRederivationContext(string connectionString, IInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        return new ApplicationDbContext(builder.Options);
    }

    /// <summary>Holds the session after the statement that writes a job's expected identity has executed.</summary>
    private sealed class ParkAfterIdentityWriteInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;

        public Task Entered => entered.Task;

        public int Count => Volatile.Read(ref count);

        public void Release() => release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await ParkIfMatchedAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            await ParkIfMatchedAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task ParkIfMatchedAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.Contains("UPDATE [CentralDerivativeJobs]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[ExpectedRecipeIdentitySha256]", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static readonly string[] RejectedRederivationBoundaries =
    [
        "persisted-pending",
        "transitions-to-pending",
        "attempted",
        "leased",
        "lease-acquired",
        "other-recipe",
        "requirement-waiting",
        "requirement-incompatible",
        "other-binding",
        "other-frozen-column",
        "requested-identity",
        "request-identity"
    ];

    private sealed record ExpandedCanonicalGraph(
        Guid ExecutionId,
        CentralDerivativeJob Annotation,
        CentralDerivativeJobInputRequirement Associations,
        CentralDerivativeJob MeasuredAssociations,
        CentralDerivativeJobInputRequirement OtherRecipeRequirement);

    /// <summary>Seeds the shipped canonical chain and live-schedules one Raw source, as ingest does.</summary>
    private static async Task<ExpandedCanonicalGraph> ExpandCanonicalGraphAsync(
        ApplicationDbContext context,
        DateTimeOffset now,
        string cameraName)
    {
        _ = await ConvergeAsync(context, CanonicalCentralGraphSeedChain.Revisions,
            DatabaseSeeder.CreateBasicCentralProcessingGraph()).ConfigureAwait(false);
        var camera = await SeedCameraAsync(context, now, cameraName).ConfigureAwait(false);
        var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'C', now.AddMinutes(-1));
        context.Add(raw);
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        await ScheduleLiveAsync(context, raw.Id, now).ConfigureAwait(false);
        var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
            .SingleAsync(item => item.AnchorSourceCentralArtifactId == raw.Id).ConfigureAwait(false);
        execution.ExpandedAtUtc.Should().NotBeNull();
        var jobs = await context.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .Where(item => item.GraphExecutionId == execution.Id)
            .ToListAsync().ConfigureAwait(false);
        var annotation = jobs.Single(item => item.RecipeName == BuiltInProcessingRecipes.Annotation);
        annotation.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        annotation.AttemptCount.Should().Be(0);
        var associations = annotation.InputRequirements.Single(item =>
            item.BindingName == BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName);
        associations.ResolutionState.Should().Be(CentralDerivativeInputResolutionState.Waiting);
        associations.IsRequired.Should().BeFalse();
        var measuredAssociations = jobs.Single(item =>
            item.RecipeName == BuiltInProcessingRecipes.MeasuredStellarAssociations);
        var otherRecipeRequirement = jobs
            .Where(item => item.RecipeName is not BuiltInProcessingRecipes.Annotation)
            .SelectMany(item => item.InputRequirements)
            .OrderBy(item => item.Id)
            .First();
        return new(execution.Id, annotation, associations, measuredAssociations, otherRecipeRequirement);
    }

    /// <summary>
    /// Makes every element of the exemption hold except the one a boundary names, each in its own statement inside the
    /// caller's transaction, and returns the job whose expected identity the boundary then rewrites.
    /// </summary>
    private static async Task<Guid> ArrangeRederivationBoundaryAsync(
        ApplicationDbContext context,
        ExpandedCanonicalGraph graph,
        string boundary,
        DateTimeOffset now)
    {
        var job = graph.Annotation.Id;
        var requirement = graph.Associations.Id;
        if (boundary == "other-recipe")
        {
            // A non-annotation job is given a Missing requirement under the measured-association binding name, so the
            // recipe is the only element that differs. Requirement identity is frozen, so this test-only arrangement
            // bypasses its trigger inside the transaction the caller rolls back.
            job = graph.OtherRecipeRequirement.CentralDerivativeJobId;
            await context.Database.ExecuteSqlRawAsync(
                "DISABLE TRIGGER [TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable] ON [CentralDerivativeJobInputRequirements];")
                .ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobInputRequirements]
                SET [BindingName] = {BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName},
                    [ResolutionState] = N'Missing', [ExpectedCentralArtifactId] = NULL,
                    [ResolutionReasonCode] = N'test.boundary', [ResolvedAtUtc] = {now}
                WHERE [Id] = {graph.OtherRecipeRequirement.Id};
                """).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync(
                "ENABLE TRIGGER [TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable] ON [CentralDerivativeJobInputRequirements];")
                .ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobs]
                SET [Status] = N'Waiting', [AttemptCount] = 0, [LeaseOwner] = NULL, [LeaseToken] = NULL,
                    [LeaseAcquiredAtUtc] = NULL, [LeaseExpiresAtUtc] = NULL
                WHERE [Id] = {job};
                """).ConfigureAwait(false);
            return job;
        }
        if (boundary == "other-binding")
        {
            // The annotation's own optional requirement is renamed, so a Missing requirement exists but under another
            // binding; the same test-only bypass applies.
            await context.Database.ExecuteSqlRawAsync(
                "DISABLE TRIGGER [TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable] ON [CentralDerivativeJobInputRequirements];")
                .ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobInputRequirements]
                SET [BindingName] = N'measured-stellar-associations-other',
                    [ResolutionState] = N'Missing', [ResolutionReasonCode] = N'test.boundary', [ResolvedAtUtc] = {now}
                WHERE [Id] = {requirement};
                """).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync(
                "ENABLE TRIGGER [TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable] ON [CentralDerivativeJobInputRequirements];")
                .ConfigureAwait(false);
            return job;
        }
        if (boundary == "requirement-incompatible")
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobInputRequirements]
                SET [ResolutionState] = N'Incompatible', [ResolutionReasonCode] = N'test.boundary', [ResolvedAtUtc] = {now}
                WHERE [Id] = {requirement};
                """).ConfigureAwait(false);
        }
        else if (boundary != "requirement-waiting")
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobInputRequirements]
                SET [ResolutionState] = N'Missing', [ResolutionReasonCode] = N'test.boundary', [ResolvedAtUtc] = {now}
                WHERE [Id] = {requirement};
                """).ConfigureAwait(false);
        }
        var leaseToken = Guid.NewGuid();
        switch (boundary)
        {
            case "persisted-pending":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs] SET [Status] = N'Pending' WHERE [Id] = {job};
                    """).ConfigureAwait(false);
                break;
            case "attempted":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs] SET [AttemptCount] = 1 WHERE [Id] = {job};
                    """).ConfigureAwait(false);
                break;
            case "leased":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs]
                    SET [LeaseOwner] = N'test-worker', [LeaseToken] = {leaseToken}, [LeaseExpiresAtUtc] = {now.AddMinutes(5)}
                    WHERE [Id] = {job};
                    """).ConfigureAwait(false);
                break;
            case "lease-acquired":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs] SET [LeaseAcquiredAtUtc] = {now} WHERE [Id] = {job};
                    """).ConfigureAwait(false);
                break;
        }
        return job;
    }

    /// <summary>Terminalizes the measured-association node without output, as a Skipped or Failed outcome leaves it.</summary>
    private static async Task SkipMeasuredAssociationsAsync(
        ApplicationDbContext context,
        ExpandedCanonicalGraph graph,
        DateTimeOffset now)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [CentralDerivativeJobs]
            SET [Status] = N'Skipped', [StateReasonCode] = {ProcessingReasonCodes.MissingProjectedScene},
                [CompletedAtUtc] = {now}, [AvailableAtUtc] = NULL, [UpdatedAtUtc] = {now}
            WHERE [Id] = {graph.MeasuredAssociations.Id};
            """).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private static async Task ConvergeGraphAsync(ApplicationDbContext context, Guid executionId, DateTimeOffset now)
    {
        var recipeCatalog = new CentralDerivativeRecipeCatalog();
        var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var catalog = new ProcessingGraphCatalogService(
            context, registry, TimeProvider.System, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
        var graphScheduler = new CentralProcessingGraphScheduler(
            context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
            new CentralDerivativeWorkerTelemetry(), TimeProvider.System);
        await graphScheduler.ConvergeAsync(executionId, now, CancellationToken.None).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private static async Task<(CentralDerivativeJob Job, CentralDerivativeJobInputRequirement Requirement)>
        ReadAnnotationAsync(ApplicationDbContext context, ExpandedCanonicalGraph graph)
    {
        var job = await context.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .SingleAsync(item => item.Id == graph.Annotation.Id).ConfigureAwait(false);
        return (job, job.InputRequirements.Single(item => item.Id == graph.Associations.Id));
    }

    private static Task<string> ReadGraphIdentityTriggerDefinitionAsync(ApplicationDbContext context)
        => context.Database.SqlQuery<string>($"""
                SELECT OBJECT_DEFINITION(OBJECT_ID(N'[TR_CentralDerivativeJobs_GraphIdentityImmutable]')) AS [Value]
                """).SingleAsync();

    /// <summary>
    /// The graph identity trigger as an earlier baseline created it, comparing the expected identity without the
    /// exemption, exactly as a database migrated before this change still carries it.
    /// </summary>
    private static string CreateEarlierBaselineGraphIdentityTrigger(string current)
    {
        var exemption = new Regex(
            @"OR \(i\.\[ExpectedRecipeIdentitySha256\] <> d\.\[ExpectedRecipeIdentitySha256\]\s+AND NOT \(.*?N'Missing'\)\)\)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        exemption.Matches(current).Should().ContainSingle();
        var earlier = AlterTrigger(
            exemption.Replace(current, "OR i.[ExpectedRecipeIdentitySha256] <> d.[ExpectedRecipeIdentitySha256]"));
        earlier.Should().NotContain(CentralProcessingGraphScheduler.ExpectedIdentityRederivationTriggerMarker);
        return earlier;
    }

    private static string AlterTrigger(string definition)
        => new Regex(@"CREATE\s+TRIGGER", RegexOptions.CultureInvariant).Replace(definition, "ALTER TRIGGER", 1);
}
