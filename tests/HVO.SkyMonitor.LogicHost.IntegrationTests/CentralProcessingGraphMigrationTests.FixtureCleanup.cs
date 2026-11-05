using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralProcessingGraphMigrationTests
{
    [TestMethod]
    [DataRow("window-active")]
    [DataRow("window-foreign")]
    [DataRow("derivative")]
    [DataRow("elastic")]
    [DataRow("runner")]
    [DataRow("hybrid")]
    [DataRow("entitlement")]
    [DataRow("window-all-active")]
    [DataRow("window-delay")]
    [DataRow("derivative-preserve")]
    [DataRow("hybrid-downstream")]
    public async Task FixtureCleanup_PreservesUnsealedGraphAndRetiresEligibleJobs(string route)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorFixtureCleanup_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (raw, sealedJob, unsealedJob, unsealedExecution) = await SeedUnsealedFixtureGraphAsync(db, now)
                .ConfigureAwait(false);

            CentralDerivativeJobStatus[] statuses =
            [
                CentralDerivativeJobStatus.Waiting, CentralDerivativeJobStatus.Pending,
                CentralDerivativeJobStatus.RetryableFailure, CentralDerivativeJobStatus.Leased,
                CentralDerivativeJobStatus.CancelRequested, CentralDerivativeJobStatus.TerminalFailure,
                CentralDerivativeJobStatus.Canceled, CentralDerivativeJobStatus.Completed
            ];
            var legacyJobs = statuses.Select(status => CreateCleanupLegacyJob(raw, now, status)).ToArray();
            db.AddRange(legacyJobs);
            var ownedCamera = await SeedCameraAsync(db, now, "cleanup-owned").ConfigureAwait(false);
            var ownedRaw = CreateSourceArtifact(ownedCamera, FrameArtifactRole.Raw, 'B', now);
            var preservedJob = CreateCleanupLegacyJob(ownedRaw, now, CentralDerivativeJobStatus.Pending);
            db.AddRange(ownedRaw, preservedJob);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
            var unsealedBefore = await CleanupJobSnapshotAsync(db, unsealedJob.Id).ConfigureAwait(false);
            var executionBefore = JsonSerializer.Serialize(await db.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == unsealedExecution.Id).ConfigureAwait(false));
            var completedId = legacyJobs.Single(item => item.Status == CentralDerivativeJobStatus.Completed).Id;
            var completedBefore = await CleanupJobSnapshotAsync(db, completedId).ConfigureAwait(false);
            var preservedBefore = await CleanupJobSnapshotAsync(db, preservedJob.Id).ConfigureAwait(false);

            await RunFixtureCleanupAsync(db, route, preservedJob.Id, ownedRaw.DevicePublicId, now.AddHours(1)).ConfigureAwait(false);

            (await CleanupJobSnapshotAsync(db, unsealedJob.Id).ConfigureAwait(false)).Should().Be(unsealedBefore);
            JsonSerializer.Serialize(await db.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == unsealedExecution.Id).ConfigureAwait(false)).Should().Be(executionBefore);
            (await CleanupJobSnapshotAsync(db, completedId).ConfigureAwait(false)).Should().Be(completedBefore);
            if (route is "window-active" or "window-foreign" or "hybrid" or "window-delay" or "derivative-preserve" or "hybrid-downstream")
            {
                (await CleanupJobSnapshotAsync(db, preservedJob.Id).ConfigureAwait(false)).Should().Be(preservedBefore);
            }
            Func<Task> advanceUnsealed = () => db.CentralDerivativeJobs.Where(item => item.Id == unsealedJob.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure));
            var rejected = await advanceUnsealed.Should().ThrowAsync<SqlException>()
                .WithMessage("*cannot advance before expansion is sealed*").ConfigureAwait(false);
            rejected.Which.Number.Should().Be(51000, "fixture cleanup must preserve the production seal guard");
            foreach (var before in legacyJobs.Append(sealedJob))
            {
                var after = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == before.Id)
                    .ConfigureAwait(false);
                var retired = route switch
                {
                    "window-active" or "window-all-active" => before.Status is CentralDerivativeJobStatus.Waiting
                        or CentralDerivativeJobStatus.Pending or CentralDerivativeJobStatus.RetryableFailure,
                    "window-foreign" or "runner" or "entitlement" => before.Status is CentralDerivativeJobStatus.Waiting
                        or CentralDerivativeJobStatus.Pending or CentralDerivativeJobStatus.RetryableFailure
                        or CentralDerivativeJobStatus.Leased or CentralDerivativeJobStatus.CancelRequested,
                    "window-delay" or "hybrid-downstream" => before.Status == CentralDerivativeJobStatus.Pending,
                    "hybrid" => before.Status is CentralDerivativeJobStatus.Waiting or CentralDerivativeJobStatus.Pending,
                    _ => before.Status != CentralDerivativeJobStatus.Completed
                };
                var expectedStatus = !retired || route == "window-delay"
                    ? before.Status
                    : route is "hybrid" or "hybrid-downstream"
                        ? CentralDerivativeJobStatus.Canceled
                        : CentralDerivativeJobStatus.TerminalFailure;
                after.Status.Should().Be(expectedStatus);
                var clearsLease = retired && route is not ("window-active" or "window-foreign" or "hybrid" or "window-delay" or "derivative-preserve" or "hybrid-downstream" or "window-all-active");
                after.LeaseToken.Should().Be(clearsLease ? null : before.LeaseToken);
                after.LeaseExpiresAtUtc.Should().Be(clearsLease ? null : before.LeaseExpiresAtUtc);
                after.LeaseOwner.Should().Be(before.LeaseOwner);
                after.LeaseAcquiredAtUtc.Should().Be(before.LeaseAcquiredAtUtc);
                after.AvailableAtUtc.Should().Be(retired && route == "window-delay" ? now.AddHours(1) : retired && route is not ("hybrid" or "hybrid-downstream") ? null : before.AvailableAtUtc);
                after.StateReasonCode.Should().Be(retired && route == "hybrid-downstream" ? "integration-test-downstream-isolation" : retired && route == "hybrid" ? "integration-test-isolation" : before.StateReasonCode);
            }
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("runner")]
    [DataRow("entitlement")]
    public async Task ActiveFixtureReset_PreservesTerminalHistoryAndIsIdempotent(string route)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorActiveFixtureReset_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (raw, sealedJob, unsealedJob, _) = await SeedUnsealedFixtureGraphAsync(db, now).ConfigureAwait(false);
            var statuses = Enum.GetValues<CentralDerivativeJobStatus>();
            statuses.Should().HaveCount(11, "every current job status must be classified by this reset contract");
            var legacy = statuses.Select(status => CreateCleanupLegacyJob(raw, now, status)).ToArray();
            db.AddRange(legacy);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
            var terminalBefore = new Dictionary<Guid, string>();
            foreach (var job in legacy.Where(job => !IsActiveFixtureResetStatus(job.Status)))
            {
                terminalBefore.Add(job.Id, await CleanupJobSnapshotAsync(db, job.Id).ConfigureAwait(false));
            }
            var unsealedBefore = await CleanupJobSnapshotAsync(db, unsealedJob.Id).ConfigureAwait(false);

            await RunFixtureCleanupAsync(db, route, Guid.Empty, Guid.Empty, now).ConfigureAwait(false);
            foreach (var original in legacy)
            {
                var after = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(job => job.Id == original.Id).ConfigureAwait(false);
                if (IsActiveFixtureResetStatus(original.Status))
                {
                    after.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
                    after.AvailableAtUtc.Should().BeNull();
                    after.LeaseToken.Should().BeNull();
                    after.LeaseExpiresAtUtc.Should().BeNull();
                }
                else
                {
                    // Full snapshots include RowVersion: an UPDATE with unchanged values is still forbidden.
                    (await CleanupJobSnapshotAsync(db, original.Id).ConfigureAwait(false)).Should().Be(terminalBefore[original.Id]);
                }
            }
            var afterFirst = JsonSerializer.Serialize(await db.CentralDerivativeJobs.AsNoTracking().OrderBy(job => job.Id).ToArrayAsync().ConfigureAwait(false));
            await RunFixtureCleanupAsync(db, route, Guid.Empty, Guid.Empty, now).ConfigureAwait(false);
            JsonSerializer.Serialize(await db.CentralDerivativeJobs.AsNoTracking().OrderBy(job => job.Id).ToArrayAsync().ConfigureAwait(false))
                .Should().Be(afterFirst, "a second reset must not rewrite retired history or its row versions");

            // Exercise all statuses on a genuinely sealed graph too. The execution remains open;
            // only its child outcome varies, so all eleven states are valid fixture inputs.
            foreach (var status in statuses)
            {
                var graphJob = await db.CentralDerivativeJobs.SingleAsync(job => job.Id == sealedJob.Id).ConfigureAwait(false);
                graphJob.Status = status;
                graphJob.CompletedAtUtc = status == CentralDerivativeJobStatus.Completed ? now : null;
                graphJob.AvailableAtUtc = status == CentralDerivativeJobStatus.Completed ? null : now;
                graphJob.LeaseToken = status is CentralDerivativeJobStatus.Leased or CentralDerivativeJobStatus.CancelRequested ? Guid.NewGuid() : null;
                graphJob.LeaseExpiresAtUtc = graphJob.LeaseToken is null ? null : now.AddMinutes(1);
                await db.SaveChangesAsync().ConfigureAwait(false);
                db.ChangeTracker.Clear();
                var before = await CleanupJobSnapshotAsync(db, sealedJob.Id).ConfigureAwait(false);
                await RunFixtureCleanupAsync(db, route, Guid.Empty, Guid.Empty, now).ConfigureAwait(false);
                var after = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(job => job.Id == sealedJob.Id).ConfigureAwait(false);
                if (IsActiveFixtureResetStatus(status))
                {
                    after.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
                    after.AvailableAtUtc.Should().BeNull();
                    after.LeaseToken.Should().BeNull();
                    after.LeaseExpiresAtUtc.Should().BeNull();
                }
                else
                {
                    (await CleanupJobSnapshotAsync(db, sealedJob.Id).ConfigureAwait(false)).Should().Be(before);
                }
            }
            foreach (var (id, before) in terminalBefore)
            {
                (await CleanupJobSnapshotAsync(db, id).ConfigureAwait(false)).Should().Be(before);
            }
            (await CleanupJobSnapshotAsync(db, unsealedJob.Id).ConfigureAwait(false)).Should().Be(unsealedBefore);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static bool IsActiveFixtureResetStatus(CentralDerivativeJobStatus status)
        => status is CentralDerivativeJobStatus.Waiting or CentralDerivativeJobStatus.Pending
            or CentralDerivativeJobStatus.RetryableFailure or CentralDerivativeJobStatus.Leased
            or CentralDerivativeJobStatus.CancelRequested;

    internal static async Task<(CentralArtifact Raw, CentralDerivativeJob SealedJob, CentralDerivativeJob UnsealedJob,
        CentralProcessingGraphExecution UnsealedExecution)> SeedUnsealedFixtureGraphAsync(ApplicationDbContext db, DateTimeOffset now)
    {
        var (camera, raw, _, _, telemetry) = await ScheduleSingleNodePreviewGraphAsync(db, now, "cleanup")
            .ConfigureAwait(false);
        using var ownedTelemetry = telemetry;
        var sealedJob = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(job => job.SourceCentralArtifactId == raw.Id).ConfigureAwait(false);
        var (unsealedJob, unsealedExecution) = await AddUnsealedFixtureGraphAsync(db, sealedJob, now, camera.Owner.Id)
            .ConfigureAwait(false);
        return (raw, sealedJob, unsealedJob, unsealedExecution);
    }

    internal static async Task<(CentralDerivativeJob Job, CentralProcessingGraphExecution Execution)> AddUnsealedFixtureGraphAsync(
        ApplicationDbContext db, CentralDerivativeJob sealedJob, DateTimeOffset now, string actorId)
    {
        var sealedExecution = await db.CentralProcessingGraphExecutions.AsNoTracking()
            .SingleAsync(execution => execution.Id == sealedJob.GraphExecutionId).ConfigureAwait(false);
        sealedExecution.ExpandedAtUtc.Should().NotBeNull();
        // A valid, unfinished expansion: the revision and plans come from the real graph fixture. Its children
        // can be inserted, but no UPDATE of those children is legal until the frozen expansion is sealed.
        var unsealedExecution = new CentralProcessingGraphExecution
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = NewCleanupIdentity(),
            RevisionId = sealedExecution.RevisionId,
            DefinitionIdentitySha256 = sealedExecution.DefinitionIdentitySha256,
            FrozenDefinitionJson = sealedExecution.FrozenDefinitionJson,
            CentralPlanIdentitySha256 = sealedExecution.CentralPlanIdentitySha256,
            FrozenCentralPlanJson = sealedExecution.FrozenCentralPlanJson,
            ExpectedSourceCount = sealedExecution.ExpectedSourceCount,
            ExpectedNodeCount = sealedExecution.ExpectedNodeCount,
            ExpectedDependencyCount = sealedExecution.ExpectedDependencyCount,
            ExpectedOutputCount = sealedExecution.ExpectedOutputCount,
            ObservatoryId = sealedExecution.ObservatoryId,
            LogicalCameraId = sealedExecution.LogicalCameraId,
            LogicalCameraInstallationId = sealedExecution.LogicalCameraInstallationId,
            InstallationPublicId = sealedExecution.InstallationPublicId,
            AnchorSourceCentralArtifactId = sealedExecution.AnchorSourceCentralArtifactId,
            AnchorSourceArtifactId = sealedExecution.AnchorSourceArtifactId,
            AnchorSourceChecksumSha256 = sealedExecution.AnchorSourceChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = actorId,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "fixture-cleanup-regression",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(unsealedExecution);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var unsealedJob = new CentralDerivativeJob
        {
            SourceCentralArtifactId = sealedJob.SourceCentralArtifactId,
            TargetRole = sealedJob.TargetRole,
            TargetRecipeVersion = sealedJob.TargetRecipeVersion,
            TargetVariant = sealedJob.TargetVariant,
            RecipeName = sealedJob.RecipeName,
            RecipeOptionsJson = sealedJob.RecipeOptionsJson,
            InputSelectorJson = sealedJob.InputSelectorJson,
            RequestedRecipeIdentitySha256 = sealedJob.RequestedRecipeIdentitySha256,
            ExpectedRecipeIdentitySha256 = sealedJob.ExpectedRecipeIdentitySha256,
            RequestIdentitySha256 = NewCleanupIdentity(),
            GraphExecutionId = unsealedExecution.Id,
            GraphNodeId = sealedJob.GraphNodeId,
            GraphNodeOrdinal = sealedJob.GraphNodeOrdinal,
            SharedNodePlanIdentitySha256 = sealedJob.SharedNodePlanIdentitySha256,
            FrozenNodePlanJson = sealedJob.FrozenNodePlanJson,
            GraphFailurePolicy = sealedJob.GraphFailurePolicy,
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = sealedJob.MaxAttempts,
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(unsealedJob);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (unsealedJob, unsealedExecution);
    }

    private static Task RunFixtureCleanupAsync(ApplicationDbContext db, string route, Guid preservedId, Guid ownedDeviceId, DateTimeOffset availableAtUtc)
        => route switch
        {
            "window-active" => CentralDerivativeWindowIntegrationTests.DisableOtherActiveJobsAsync(db, preservedId),
            "window-foreign" => CentralDerivativeWindowIntegrationTests.RetireForeignActiveStateAsync(db, ownedDeviceId),
            "derivative" => DerivativeJobIntegrationTests.DisableClaimableJobsAsync(db),
            "elastic" => ElasticProviderIntegrationTests.DisableClaimableJobsAsync(db),
            "runner" => ProcessingRunnerProtocolIntegrationTests.DisableClaimableJobsAsync(db),
            "hybrid" => HybridTransientSubmissionIntegrationTests.DisableOtherClaimableJobsAsync(db, [preservedId]),
            "window-all-active" => CentralDerivativeWindowIntegrationTests.DisableActiveJobsAsync(db),
            "window-delay" => CentralDerivativeWindowIntegrationTests.DelayOtherPendingJobsAsync(db, preservedId, availableAtUtc),
            "derivative-preserve" => DerivativeJobIntegrationTests.DisableOtherIncompleteJobsAsync(db, preservedId),
            "hybrid-downstream" => HybridTransientSubmissionIntegrationTests.CancelOtherPendingJobsAsync(db, [preservedId]),
            "entitlement" => ProcessingEntitlementIntegrationTests.DisableClaimableJobsAsync(db),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };

    private static CentralDerivativeJob CreateCleanupLegacyJob(CentralArtifact raw, DateTimeOffset now, CentralDerivativeJobStatus status)
    {
        var job = CreateLegacyCompletedImageQualityJob(raw, now);
        job.RequestIdentitySha256 = NewCleanupIdentity();
        job.TargetVariant = job.RequestIdentitySha256;
        job.Status = status;
        job.CompletedAtUtc = status == CentralDerivativeJobStatus.Completed ? now : null;
        job.AvailableAtUtc = status == CentralDerivativeJobStatus.Completed ? null : now;
        if (status is CentralDerivativeJobStatus.Leased or CentralDerivativeJobStatus.CancelRequested)
        {
            job.LeaseToken = Guid.NewGuid();
            job.LeaseOwner = "fixture-cleanup-owner";
            job.LeaseAcquiredAtUtc = now;
            job.LeaseExpiresAtUtc = now.AddMinutes(1);
        }
        return job;
    }

    private static string NewCleanupIdentity() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static async Task<string> CleanupJobSnapshotAsync(ApplicationDbContext db, Guid id)
        => JsonSerializer.Serialize(await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == id)
            .ConfigureAwait(false));
}
