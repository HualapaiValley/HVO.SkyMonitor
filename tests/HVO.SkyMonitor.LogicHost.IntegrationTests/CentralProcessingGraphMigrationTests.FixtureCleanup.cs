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
            var (camera, raw, _, _, telemetry) = await ScheduleSingleNodePreviewGraphAsync(db, now, "cleanup")
                .ConfigureAwait(false);
            using var ownedTelemetry = telemetry;
            var sealedJob = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync().ConfigureAwait(false);
            var sealedExecution = await db.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
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
                ObservatoryId = camera.Observatory.Id,
                LogicalCameraId = camera.Camera.Id,
                LogicalCameraInstallationId = camera.Installation.Id,
                InstallationPublicId = camera.Installation.InstallationPublicId,
                AnchorSourceCentralArtifactId = raw.Id,
                AnchorSourceArtifactId = raw.ArtifactId,
                AnchorSourceChecksumSha256 = raw.ChecksumSha256,
                Trigger = CentralProcessingGraphTrigger.Replay,
                ActorId = camera.Owner.Id,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ReasonCode = "fixture-cleanup-regression",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Add(unsealedExecution);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var unsealedJob = new CentralDerivativeJob
            {
                SourceCentralArtifactId = raw.Id,
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

            await RunFixtureCleanupAsync(db, route, preservedJob.Id, ownedRaw.DevicePublicId).ConfigureAwait(false);

            (await CleanupJobSnapshotAsync(db, unsealedJob.Id).ConfigureAwait(false)).Should().Be(unsealedBefore);
            JsonSerializer.Serialize(await db.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == unsealedExecution.Id).ConfigureAwait(false)).Should().Be(executionBefore);
            (await CleanupJobSnapshotAsync(db, completedId).ConfigureAwait(false)).Should().Be(completedBefore);
            if (route is "window-active" or "window-foreign" or "hybrid")
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
                    "window-active" => before.Status is CentralDerivativeJobStatus.Waiting
                        or CentralDerivativeJobStatus.Pending or CentralDerivativeJobStatus.RetryableFailure,
                    "window-foreign" => before.Status is CentralDerivativeJobStatus.Waiting
                        or CentralDerivativeJobStatus.Pending or CentralDerivativeJobStatus.RetryableFailure
                        or CentralDerivativeJobStatus.Leased or CentralDerivativeJobStatus.CancelRequested,
                    "hybrid" => before.Status is CentralDerivativeJobStatus.Waiting or CentralDerivativeJobStatus.Pending,
                    _ => before.Status != CentralDerivativeJobStatus.Completed
                };
                after.Status.Should().Be(retired
                    ? route == "hybrid" ? CentralDerivativeJobStatus.Canceled : CentralDerivativeJobStatus.TerminalFailure
                    : before.Status);
                var clearsLease = retired && route is not ("window-active" or "window-foreign" or "hybrid");
                after.LeaseToken.Should().Be(clearsLease ? null : before.LeaseToken);
                after.LeaseExpiresAtUtc.Should().Be(clearsLease ? null : before.LeaseExpiresAtUtc);
                after.LeaseOwner.Should().Be(before.LeaseOwner);
                after.LeaseAcquiredAtUtc.Should().Be(before.LeaseAcquiredAtUtc);
                after.AvailableAtUtc.Should().Be(retired && route != "hybrid" ? null : before.AvailableAtUtc);
                after.StateReasonCode.Should().Be(retired && route == "hybrid" ? "integration-test-isolation" : before.StateReasonCode);
            }
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static Task RunFixtureCleanupAsync(ApplicationDbContext db, string route, Guid preservedId, Guid ownedDeviceId)
        => route switch
        {
            "window-active" => CentralDerivativeWindowIntegrationTests.DisableOtherActiveJobsAsync(db, preservedId),
            "window-foreign" => CentralDerivativeWindowIntegrationTests.RetireForeignActiveStateAsync(db, ownedDeviceId),
            "derivative" => DerivativeJobIntegrationTests.DisableClaimableJobsAsync(db),
            "elastic" => ElasticProviderIntegrationTests.DisableClaimableJobsAsync(db),
            "runner" => ProcessingRunnerProtocolIntegrationTests.DisableClaimableJobsAsync(db),
            "hybrid" => HybridTransientSubmissionIntegrationTests.DisableOtherClaimableJobsAsync(db, [preservedId]),
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
