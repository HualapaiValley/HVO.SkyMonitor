using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class DerivativeJobIntegrationTests
{
    [TestMethod]
    public async Task JobMutation_RejectsFailedTransactionPreconditionWithoutChangingState()
    {
        var seeded = await SeedJobAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>();

        Func<Task> cancel = () => operations.CancelAsync(
            seeded.JobId,
            "user:removed-manager",
            CancellationToken.None,
            _ => Task.FromResult(false));

        await cancel.Should().ThrowAsync<UnauthorizedAccessException>().ConfigureAwait(false);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(job => job.Id == seeded.JobId)
            .ConfigureAwait(false)).Status.Should().Be(CentralDerivativeJobStatus.Pending);
    }

    [TestMethod]
    public async Task ConcurrentClaims_LeaseJobToOneWorker()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var (jobId, _, _) = await SeedJobAsync().ConfigureAwait(false);
        await using var firstScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var second = secondScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();

        var leases = await Task.WhenAll(
            first.ClaimNextAsync("worker-1", TimeSpan.FromMinutes(1), CancellationToken.None),
            second.ClaimNextAsync("worker-2", TimeSpan.FromMinutes(1), CancellationToken.None)).ConfigureAwait(false);

        leases.Count(lease => lease is not null).Should().Be(1);
        var lease = leases.Single(item => item is not null)!;
        lease.JobId.Should().Be(jobId);
        lease.SourceDevicePublicId.Should().NotBeEmpty();
        lease.SourceContentUri.Should().Be(
            $"/api/v1.0/devices/{lease.SourceDevicePublicId:D}/artifacts/{lease.SourceArtifactId:D}/content");
        lease.SourceContentUri.Should().NotContain("minio://");
        await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var job = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Leased);
        job.AttemptCount.Should().Be(1);
        var attempt = await db.CentralDerivativeJobAttempts.SingleAsync(item => item.CentralDerivativeJobId == jobId)
            .ConfigureAwait(false);
        attempt.AttemptNumber.Should().Be(1);
        attempt.WorkerId.Should().Be(lease.WorkerId);
        attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Leased);
        var operations = verificationScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>();
        await operations.CancelAsync(jobId, "test-operator", CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        (await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false))
            .Status.Should().Be(CentralDerivativeJobStatus.Canceled);
        (await db.CentralDerivativeJobAttempts.SingleAsync(item => item.CentralDerivativeJobId == jobId)
            .ConfigureAwait(false)).Outcome.Should().Be(CentralDerivativeAttemptOutcome.Canceled);
    }

    [TestMethod]
    public async Task ExpiredLease_IsReclaimedAfterServiceRestart()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, _, _) = await SeedJobAsync(availableAtUtc: now).ConfigureAwait(false);
        CentralDerivativeJobLease firstLease;
        await using (var firstScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var service = new CentralDerivativeJobService(db, clock);
            firstLease = (await service.ClaimNextAsync("worker-1", TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false))!;
        }
        clock.Advance(TimeSpan.FromSeconds(6));

        CentralDerivativeJobLease secondLease;
        await using (var secondScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var service = new CentralDerivativeJobService(db, clock);
            secondLease = (await service.ClaimNextAsync("worker-2", TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false))!;
            var staleRenewal = () => service.RenewLeaseAsync(
                jobId, firstLease.LeaseToken, TimeSpan.FromSeconds(5), CancellationToken.None);
            await staleRenewal.Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
        }

        secondLease.JobId.Should().Be(jobId);
        secondLease.LeaseToken.Should().NotBe(firstLease.LeaseToken);
        secondLease.AttemptCount.Should().Be(2);
        await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var attempts = await verificationDb.CentralDerivativeJobAttempts
            .Where(item => item.CentralDerivativeJobId == jobId)
            .OrderBy(item => item.AttemptNumber)
            .ToListAsync().ConfigureAwait(false);
        attempts.Select(item => item.Outcome).Should().Equal(
            CentralDerivativeAttemptOutcome.LeaseExpired,
            CentralDerivativeAttemptOutcome.Leased);
        attempts[0].ReasonCode.Should().Be("lease.expired");
    }

    [TestMethod]
    public async Task RetryableFailure_UsesPersistedBackoffAndThenReclaims()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 13, 6, 5, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, _, _) = await SeedJobAsync(availableAtUtc: now).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = new CentralDerivativeJobService(db, clock);
        var lease = (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false))!;

        await service.FailAsync(jobId, lease.LeaseToken, "transient", retryable: true, CancellationToken.None)
            .ConfigureAwait(false);

        (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull();
        clock.Advance(CentralDerivativeJobService.InitialRetryDelay);
        db.ChangeTracker.Clear();
        var sourceId = await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
            .Select(job => job.SourceCentralArtifactId).SingleAsync().ConfigureAwait(false);
        await db.CentralArtifacts.Where(artifact => artifact.Id == sourceId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(artifact => artifact.ObjectState, CentralArtifactObjectState.Pending))
            .ConfigureAwait(false);
        (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull();
        await db.CentralArtifacts.Where(artifact => artifact.Id == sourceId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(artifact => artifact.ObjectState, CentralArtifactObjectState.Available)
                .SetProperty(artifact => artifact.ReconstructionState, CentralReconstructionState.Complete))
            .ConfigureAwait(false);
        var retry = await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);
        retry!.AttemptCount.Should().Be(2);
        var attempts = await db.CentralDerivativeJobAttempts.Where(item => item.CentralDerivativeJobId == jobId)
            .OrderBy(item => item.AttemptNumber).ToListAsync().ConfigureAwait(false);
        attempts.Select(item => item.Outcome).Should().Equal(
            CentralDerivativeAttemptOutcome.RetryableFailure,
            CentralDerivativeAttemptOutcome.Leased);
    }

    [TestMethod]
    public async Task RetryableFailure_AtMaximumAttemptsBecomesTerminal()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 13, 6, 7, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, _, _) = await SeedJobAsync(availableAtUtc: now, maxAttempts: 1).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = new CentralDerivativeJobService(db, clock);
        var lease = (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false))!;

        await service.FailAsync(jobId, lease.LeaseToken, "still broken", retryable: true, CancellationToken.None)
            .ConfigureAwait(false);

        db.ChangeTracker.Clear();
        var job = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
        job.AvailableAtUtc.Should().BeNull();
        var attempt = await db.CentralDerivativeJobAttempts.SingleAsync(item => item.CentralDerivativeJobId == jobId)
            .ConfigureAwait(false);
        attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.TerminalFailure);
        attempt.EndedAtUtc.Should().Be(now);
        (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull();
    }

    [TestMethod]
    public async Task Completion_RequiresMatchingTargetAndIsIdempotent()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 13, 6, 10, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, frameId, _) = await SeedJobAsync(availableAtUtc: now).ConfigureAwait(false);
        Guid resultArtifactId;
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var result = new CentralArtifact
        {
            CentralFrameId = frameId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            Variant = CentralDerivativeRecipeCatalog.PreviewVariant,
            RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            ManifestSchemaVersion = "v1",
            MediaType = "image/png",
            ByteLength = 4,
            ChecksumSha256 = new string('A', 64),
            StorageReference = "minio://result",
            ReceivedAtUtc = now,
            IdempotencyKey = Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(result);
        await db.SaveChangesAsync().ConfigureAwait(false);
        resultArtifactId = result.ArtifactId;
        var service = new CentralDerivativeJobService(db, clock);
        var lease = (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false))!;

        var source = await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
            .Select(job => job.SourceArtifact!).SingleAsync().ConfigureAwait(false);
        source.ObjectState = CentralArtifactObjectState.Pending;
        await db.SaveChangesAsync().ConfigureAwait(false);
        await FluentActions.Awaiting(() => service.CompleteAsync(
                jobId, lease.LeaseToken, resultArtifactId, CancellationToken.None))
            .Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
        source.ObjectState = CentralArtifactObjectState.Available;
        result.ObjectState = CentralArtifactObjectState.Pending;
        await db.SaveChangesAsync().ConfigureAwait(false);
        await FluentActions.Awaiting(() => service.CompleteAsync(
                jobId, lease.LeaseToken, resultArtifactId, CancellationToken.None))
            .Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
        result.ObjectState = CentralArtifactObjectState.Available;
        await db.SaveChangesAsync().ConfigureAwait(false);
        await service.CompleteAsync(jobId, lease.LeaseToken, resultArtifactId, CancellationToken.None).ConfigureAwait(false);
        await service.CompleteAsync(jobId, lease.LeaseToken, resultArtifactId, CancellationToken.None).ConfigureAwait(false);

        db.ChangeTracker.Clear();
        var job = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
        job.ResultCentralArtifactId.Should().Be(result.Id);
        var attempt = await db.CentralDerivativeJobAttempts.SingleAsync(item => item.CentralDerivativeJobId == jobId)
            .ConfigureAwait(false);
        attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
        attempt.EndedAtUtc.Should().Be(now);
    }

    [TestMethod]
    public async Task ArtifactlessCompletion_FinalizesAttemptAndIsIdempotent()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 20, 20, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, _, _) = await SeedJobAsync(availableAtUtc: now).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = new CentralDerivativeJobService(db, clock);
        var lease = (await service.ClaimNextAsync("artifactless-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false))!;

        await service.CompleteWithoutArtifactAsync(
            jobId, lease.LeaseToken, CentralTransientRuntimeReasonCodes.Persisted, CancellationToken.None)
            .ConfigureAwait(false);
        await service.CompleteWithoutArtifactAsync(
            jobId, lease.LeaseToken, CentralTransientRuntimeReasonCodes.Persisted, CancellationToken.None)
            .ConfigureAwait(false);

        var job = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
            .ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
        job.ResultCentralArtifactId.Should().BeNull();
        job.StateReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.Persisted);
        var attempt = await db.CentralDerivativeJobAttempts.AsNoTracking()
            .SingleAsync(item => item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
        attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
        attempt.ReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.Persisted);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalCompletion_IsIdempotent()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var (jobId, frameId, _) = await SeedJobAsync().ConfigureAwait(false);
        Guid resultArtifactId;
        await using (var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var result = new CentralArtifact
            {
                CentralFrameId = frameId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Preview,
                Variant = CentralDerivativeRecipeCatalog.PreviewVariant,
                RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
                ManifestSchemaVersion = "v1",
                MediaType = "image/png",
                ByteLength = 4,
                ChecksumSha256 = new string('B', 64),
                StorageReference = $"minio://result/{Guid.NewGuid():N}",
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            db.CentralArtifacts.Add(result);
            await db.SaveChangesAsync().ConfigureAwait(false);
            resultArtifactId = result.ArtifactId;
        }
        Guid leaseToken;
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var service = claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            leaseToken = (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!.LeaseToken;
        }
        await using var firstScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var second = secondScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();

        await Task.WhenAll(
            first.CompleteAsync(jobId, leaseToken, resultArtifactId, CancellationToken.None),
            second.CompleteAsync(jobId, leaseToken, resultArtifactId, CancellationToken.None)).ConfigureAwait(false);

        await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await verificationDb.CentralDerivativeJobs.SingleAsync(job => job.Id == jobId).ConfigureAwait(false))
            .Status.Should().Be(CentralDerivativeJobStatus.Completed);
        (await verificationDb.CentralDerivativeJobAttempts.SingleAsync(item => item.CentralDerivativeJobId == jobId)
            .ConfigureAwait(false)).Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
    }

    [TestMethod]
    public async Task CompletedJob_SourceInvalidationSuspendsAndRecoveryRequeuesSafely()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, frameId, _) = await SeedJobAsync(availableAtUtc: now).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await db.CentralArtifacts.Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(artifact => artifact.CentralFrameId == frameId && artifact.Role == FrameArtifactRole.Raw)
            .ConfigureAwait(false);
        var result = new CentralArtifact
        {
            CentralFrameId = frameId,
            Frame = source.Frame,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            Variant = CentralDerivativeRecipeCatalog.PreviewVariant,
            RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "image/png",
            ByteLength = 4,
            ChecksumSha256 = new string('C', 64),
            StorageReference = $"minio://result/{Guid.NewGuid():N}",
            ReceivedAtUtc = now,
            IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(result);
        var completedAtUtc = now.AddMinutes(-1);
        var job = await db.CentralDerivativeJobs.SingleAsync(candidate => candidate.Id == jobId).ConfigureAwait(false);
        job.Status = CentralDerivativeJobStatus.Completed;
        job.AttemptCount = 1;
        job.AvailableAtUtc = null;
        job.ResultCentralArtifactId = result.Id;
        job.ResultArtifact = result;
        job.CompletedAtUtc = completedAtUtc;
        source.ObjectState = CentralArtifactObjectState.Pending;
        result.ReconstructionState = CentralReconstructionState.PendingReference;
        await db.SaveChangesAsync().ConfigureAwait(false);

        await ArtifactIngestService.InvalidateDependentsAsync(db, source, CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        job.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
        job.AvailableAtUtc.Should().BeNull();
        job.LastError.Should().Be(CentralDerivativeJobScheduler.SourceInvalidatedReason);
        job.CompletedAtUtc.Should().Be(completedAtUtc);
        job.ResultCentralArtifactId.Should().Be(result.Id);
        var service = new CentralDerivativeJobService(db, clock);
        (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull();
        await FluentActions.Awaiting(() => service.CompleteAsync(
                jobId, Guid.NewGuid(), result.ArtifactId, CancellationToken.None))
            .Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);

        source.ObjectState = CentralArtifactObjectState.Available;
        source.ReconstructionState = CentralReconstructionState.LegacyIncomplete;
        await db.SaveChangesAsync().ConfigureAwait(false);
        var scheduler = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();
        await scheduler.EnsureRequiredJobsAsync(source, now, CancellationToken.None).ConfigureAwait(false);
        var resultId = result.Id;
        db.ChangeTracker.Clear();
        source = await db.CentralArtifacts.Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(artifact => artifact.CentralFrameId == frameId && artifact.Role == FrameArtifactRole.Raw)
            .ConfigureAwait(false);
        result = source.Frame!.Artifacts.Single(artifact => artifact.Id == resultId);
        job = await db.CentralDerivativeJobs.SingleAsync(candidate => candidate.Id == jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
        job.AvailableAtUtc.Should().BeNull();

        source.ReconstructionState = CentralReconstructionState.Complete;
        await db.SaveChangesAsync().ConfigureAwait(false);
        await scheduler.EnsureRequiredJobsAsync(source, now, CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Pending);
        job.AvailableAtUtc.Should().Be(now);
        job.CompletedAtUtc.Should().Be(completedAtUtc);
        await db.CentralDerivativeJobs.Where(candidate => candidate.Id != jobId
                && candidate.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(candidate => candidate.AvailableAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        var lease = await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        lease!.JobId.Should().Be(jobId);

        result.ReconstructionState = CentralReconstructionState.Complete;
        await db.SaveChangesAsync().ConfigureAwait(false);
        await service.CompleteAsync(jobId, lease.LeaseToken, result.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var recovered = await db.CentralDerivativeJobs.SingleAsync(candidate => candidate.Id == jobId).ConfigureAwait(false);
        recovered.Status.Should().Be(CentralDerivativeJobStatus.Completed);
        recovered.CompletedAtUtc.Should().Be(now);
    }

    [TestMethod]
    public async Task CompletedJob_ResultInvalidationSuspendsAndRecoveryRequeuesForRegeneration()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 15, 12, 30, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var (jobId, frameId, _) = await SeedJobAsync(availableAtUtc: now).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await db.CentralArtifacts.Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(artifact => artifact.CentralFrameId == frameId && artifact.Role == FrameArtifactRole.Raw)
            .ConfigureAwait(false);
        var result = new CentralArtifact
        {
            CentralFrameId = frameId,
            Frame = source.Frame,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            Variant = CentralDerivativeRecipeCatalog.PreviewVariant,
            RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "image/png",
            ByteLength = 4,
            ChecksumSha256 = new string('D', 64),
            StorageReference = $"minio://result/{Guid.NewGuid():N}",
            ReceivedAtUtc = now,
            IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(result);
        var completedAtUtc = now.AddMinutes(-1);
        var job = await db.CentralDerivativeJobs.SingleAsync(candidate => candidate.Id == jobId).ConfigureAwait(false);
        job.Status = CentralDerivativeJobStatus.Completed;
        job.AttemptCount = 1;
        job.AvailableAtUtc = null;
        job.ResultCentralArtifactId = result.Id;
        job.ResultArtifact = result;
        job.CompletedAtUtc = completedAtUtc;
        result.ObjectState = CentralArtifactObjectState.Quarantined;
        result.ReconstructionState = CentralReconstructionState.Quarantined;
        await db.SaveChangesAsync().ConfigureAwait(false);

        await ArtifactIngestService.InvalidateDependentsAsync(db, result, CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        job.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
        job.AvailableAtUtc.Should().BeNull();
        job.LastError.Should().Be(CentralDerivativeJobScheduler.ResultInvalidatedReason);
        job.ResultCentralArtifactId.Should().Be(result.Id);
        var service = new CentralDerivativeJobService(db, clock);
        (await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull();

        result.ObjectState = CentralArtifactObjectState.Available;
        result.ReconstructionState = CentralReconstructionState.Complete;
        var scheduler = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();
        await scheduler.EnsureRequiredJobsAsync(result, now, CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        job.Status.Should().Be(CentralDerivativeJobStatus.Pending);
        job.AvailableAtUtc.Should().Be(now);
        job.CompletedAtUtc.Should().Be(completedAtUtc);
        var lease = await service.ClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        lease!.JobId.Should().Be(jobId);
        await service.CompleteAsync(jobId, lease.LeaseToken, result.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var recovered = await db.CentralDerivativeJobs.SingleAsync(candidate => candidate.Id == jobId).ConfigureAwait(false);
        recovered.Status.Should().Be(CentralDerivativeJobStatus.Completed);
        recovered.CompletedAtUtc.Should().Be(now);
    }

    [TestMethod]
    public async Task InputInvalidationWithStaleSourceGenerationIsRejected()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var (jobId, _, _) = await SeedJobAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var lease = await service.ClaimNextAsync(
            "generation-worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        lease!.JobId.Should().Be(jobId);
        var source = await db.CentralArtifacts.AsNoTracking().SingleAsync(item =>
            item.ArtifactId == lease.SourceArtifactId).ConfigureAwait(false);
        var staleRowVersion = source.RowVersion.ToArray();
        await db.CentralArtifacts.Where(item => item.Id == source.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.StateReasonCode, "object.repaired")).ConfigureAwait(false);

        await FluentActions.Awaiting(() => service.MarkInputUnavailableAsync(
                lease.JobId,
                lease.LeaseToken,
                source.Id,
                staleRowVersion,
                "object.checksum-mismatch",
                quarantine: true,
                CancellationToken.None))
            .Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var current = await db.CentralArtifacts.SingleAsync(item => item.Id == source.Id).ConfigureAwait(false);
        current.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        current.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
    }

    [TestMethod]
    public async Task ExpiredTerminalAndMalformedLeasesDoNotBlockPendingWork()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var (_, _, _) = await SeedJobAsync(maxAttempts: 1).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var original = await db.CentralDerivativeJobs.Include(job => job.SourceArtifact)
            .SingleAsync(job => job.MaxAttempts == 1 && job.Status == CentralDerivativeJobStatus.Pending)
            .ConfigureAwait(false);
        var source = original.SourceArtifact!;
        for (var index = 0; index < 100; index++)
        {
            db.CentralDerivativeJobs.Add(CloneJob(original, source, $"expiry-{index:D3}", maxAttempts: 1));
        }
        var malformed = CloneJob(original, source, "expiry-malformed", maxAttempts: 2);
        malformed.Status = CentralDerivativeJobStatus.Leased;
        malformed.AttemptCount = 1;
        malformed.AvailableAtUtc = null;
        malformed.LeaseOwner = "missing-attempt-worker";
        malformed.LeaseToken = Guid.NewGuid();
        malformed.LeaseAcquiredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        malformed.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.CentralDerivativeJobs.Add(malformed);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        for (var index = 0; index < 101; index++)
        {
            var lease = await service.ClaimNextAsync(
                "expiry-seed-worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.CentralDerivativeJobs.Where(job => job.Id == lease!.JobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    job => job.LeaseExpiresAtUtc, expiredAt)).ConfigureAwait(false);
            await db.CentralDerivativeJobAttempts.Where(attempt =>
                    attempt.CentralDerivativeJobId == lease.JobId
                    && attempt.AttemptNumber == lease.AttemptCount)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    attempt => attempt.LeaseExpiresAtUtc, expiredAt)).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        var pending = CloneJob(original, source, "after-expired", maxAttempts: 5);
        db.CentralDerivativeJobs.Add(pending);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var claimed = await service.ClaimNextAsync(
            "after-expiry-worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be(pending.Id);
        db.ChangeTracker.Clear();
        (await db.CentralDerivativeJobs.CountAsync(job =>
            job.TargetVariant.StartsWith("expiry-")
            && job.Status == CentralDerivativeJobStatus.TerminalFailure).ConfigureAwait(false)).Should().Be(101);
    }

    [TestMethod]
    public async Task RecipeIdentityMismatchTerminatesBeforeLoadingInput()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var (jobId, _, _) = await SeedJobAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                job => job.RequestedRecipeIdentitySha256, new string('F', 64))).ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var lease = await service.ClaimNextAsync(
            "identity-worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);

        var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(lease!, CancellationToken.None).ConfigureAwait(false);

        result.Status.Should().Be(ProcessingOutcomeStatus.TerminalFailure);
        result.ReasonCode.Should().Be("processing.recipe-identity-mismatch");
        db.ChangeTracker.Clear();
        var job = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
    }

    [TestMethod]
    public async Task ReconciliationDoesNotCompleteCanceledJob()
    {
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var (jobId, _, _) = await SeedJobAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>();
        await operations.CancelAsync(jobId, "integration-test", CancellationToken.None).ConfigureAwait(false);
        var job = await db.CentralDerivativeJobs.Include(item => item.SourceArtifact)!
            .ThenInclude(source => source!.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        var source = job.SourceArtifact!;
        var target = new CentralArtifact
        {
            CentralFrameId = source.CentralFrameId,
            Frame = source.Frame,
            DevicePublicId = source.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            Variant = CentralDerivativeRecipeCatalog.PreviewVariant,
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "image/jpeg",
            ByteLength = 4,
            ChecksumSha256 = new string('B', 64),
            StorageReference = $"minio://target/{Guid.NewGuid():N}",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(target);
        await db.SaveChangesAsync().ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(target, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.ChangeTracker.Clear();
        var canceled = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        canceled.Status.Should().Be(CentralDerivativeJobStatus.Canceled);
        canceled.ResultCentralArtifactId.Should().BeNull();
    }

    [TestMethod]
    public async Task OperationalQuery_FiltersJobsAndDoesNotExposeLeaseToken()
    {
        var (jobId, _, agentId) = await SeedJobAsync().ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client, administrative: true).ConfigureAwait(false));

        using var response = await client.GetAsync(new Uri(
            $"/api/v1.0/derivative-jobs?agentId={agentId}&status=pending&targetRole=preview&take=10",
            UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().Contain(agentId);
        body.Should().NotContain("LeaseToken");
        body.Should().NotContain("RowVersion");
        using var cancel = await client.PostAsync(
            new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/cancel", UriKind.Relative), content: null)
            .ConfigureAwait(false);
        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var requeue = await client.PostAsync(
            new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/requeue", UriKind.Relative), content: null)
            .ConfigureAwait(false);
        requeue.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var secondCancel = await client.PostAsync(
            new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/cancel", UriKind.Relative), content: null)
            .ConfigureAwait(false);
        secondCancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var reprocessRequest = new
        {
            recipeName = BuiltInProcessingRecipes.ImageQuality,
            options = new { },
            outputVariant = "operator-quality-v2",
            supersede = false
        };
        Guid siblingJobId;
        await using (var siblingScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var siblingDb = siblingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var original = await siblingDb.CentralDerivativeJobs.Include(job => job.SourceArtifact)
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false);
            var sibling = CloneJob(original, original.SourceArtifact!, "sibling-preview", maxAttempts: 5);
            sibling.Status = CentralDerivativeJobStatus.TerminalFailure;
            sibling.AvailableAtUtc = null;
            siblingDb.CentralDerivativeJobs.Add(sibling);
            await siblingDb.SaveChangesAsync().ConfigureAwait(false);
            siblingJobId = sibling.Id;
        }
        var reprocessUri = new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/reprocess", UriKind.Relative);
        var reprocessTasks = new[]
        {
            client.PostAsJsonAsync(reprocessUri, reprocessRequest),
            client.PostAsJsonAsync(
                new Uri($"/api/v1.0/derivative-jobs/{siblingJobId:D}/reprocess", UriKind.Relative),
                reprocessRequest)
        };
        var reprocessResponses = await Task.WhenAll(reprocessTasks).ConfigureAwait(false);
        reprocessResponses.Should().OnlyContain(item => item.StatusCode == HttpStatusCode.OK);
        var reprocessedIds = await Task.WhenAll(reprocessResponses.Select(async item =>
            (await item.Content.ReadFromJsonAsync<DerivativeJobsController.ReprocessResponse>()
                .ConfigureAwait(false))!.JobId)).ConfigureAwait(false);
        reprocessedIds.Distinct().Should().ContainSingle();
        var reprocessedJobId = reprocessedIds[0];
        foreach (var item in reprocessResponses)
        {
            item.Dispose();
        }
        var supersedeRequest = new
        {
            recipeName = BuiltInProcessingRecipes.ImageQuality,
            options = new { },
            outputVariant = "operator-quality-v2",
            supersede = true
        };
        using var supersede = await client.PostAsJsonAsync(
            new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/reprocess", UriKind.Relative), supersedeRequest)
            .ConfigureAwait(false);
        (await supersede.Content.ReadFromJsonAsync<DerivativeJobsController.ReprocessResponse>()
            .ConfigureAwait(false))!.JobId.Should().Be(reprocessedJobId);
        using var duplicateSupersede = await client.PostAsJsonAsync(
            new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/reprocess", UriKind.Relative), supersedeRequest)
            .ConfigureAwait(false);
        (await duplicateSupersede.Content.ReadFromJsonAsync<DerivativeJobsController.ReprocessResponse>()
            .ConfigureAwait(false))!.JobId.Should().Be(reprocessedJobId);
        await using (var operationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var operationDb = operationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var original = await operationDb.CentralDerivativeJobs.SingleAsync(job => job.Id == jobId)
                .ConfigureAwait(false);
            original.Status.Should().Be(CentralDerivativeJobStatus.Canceled);
            original.SupersededByJobId.Should().BeNull();
            var reprocessed = await operationDb.CentralDerivativeJobs.SingleAsync(job => job.Id == reprocessedJobId)
                .ConfigureAwait(false);
            reprocessed.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            reprocessed.PredecessorJobId.Should().Be(jobId);
            reprocessed.TargetRole.Should().Be(FrameArtifactRole.Metadata);
            reprocessed.TargetVariant.Should().Be("operator-quality-v2");
            reprocessed.RequestIdentitySha256.Should().HaveLength(64);
        }
        using var invalid = await client.GetAsync(new Uri(
            "/api/v1.0/derivative-jobs?status=999", UriKind.Relative)).ConfigureAwait(false);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var invalidReprocess = await client.PostAsJsonAsync(
            new Uri($"/api/v1.0/derivative-jobs/{jobId:D}/reprocess", UriKind.Relative),
            new { recipeName = "unknown", options = new { }, outputVariant = "invalid", supersede = false })
            .ConfigureAwait(false);
        invalidReprocess.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client, administrative: false).ConfigureAwait(false));
        using var forbidden = await client.GetAsync(new Uri(
            "/api/v1.0/derivative-jobs", UriKind.Relative)).ConfigureAwait(false);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var forbiddenCancel = await client.PostAsync(
            new Uri($"/api/v1.0/derivative-jobs/{reprocessedJobId:D}/cancel", UriKind.Relative), content: null)
            .ConfigureAwait(false);
        forbiddenCancel.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task DisableClaimableJobsAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
    }

    private static CentralDerivativeJob CloneJob(
        CentralDerivativeJob template,
        CentralArtifact source,
        string variant,
        int maxAttempts)
    {
        var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            TargetRole = template.TargetRole,
            TargetRecipeVersion = template.TargetRecipeVersion,
            TargetVariant = variant,
            RecipeName = template.RecipeName,
            RecipeOptionsJson = template.RecipeOptionsJson,
            InputSelectorJson = template.InputSelectorJson,
            RequestedRecipeIdentitySha256 = template.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = maxAttempts,
            AvailableAtUtc = createdAtUtc,
            ResolutionCompletedAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
        AttachSingleInput(job, source, createdAtUtc);
        return job;
    }

    private static async Task<(Guid JobId, Guid FrameId, string AgentId)> SeedJobAsync(
        DateTimeOffset? availableAtUtc = null,
        int maxAttempts = 5)
    {
        var now = availableAtUtc ?? DateTimeOffset.UtcNow;
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = $"job-agent-{Guid.NewGuid():N}",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now
        };
        var source = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            ArtifactId = Guid.NewGuid(),
            DevicePublicId = frame.DevicePublicId,
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v1",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('A', 64),
            StorageReference = $"minio://source/{Guid.NewGuid():N}",
            ReceivedAtUtc = now,
            IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var recipe = new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => item.TargetRole == FrameArtifactRole.Preview);
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            TargetVariant = recipe.TargetVariant,
            RecipeName = recipe.RecipeName,
            RecipeOptionsJson = CaptureContractJson.Canonicalize(recipe.Options).GetRawText(),
            InputSelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText(),
            RequestedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateRequestIdentity(
                source.DevicePublicId!.Value, source.ArtifactId, recipe),
            Status = CentralDerivativeJobStatus.Pending,
            ResolutionCompletedAtUtc = now,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        AttachSingleInput(job, source, now);
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(source);
        db.CentralDerivativeJobs.Add(job);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (job.Id, frame.Id, frame.AgentId);
    }

    private static void AttachSingleInput(
        CentralDerivativeJob job,
        CentralArtifact source,
        DateTimeOffset selectedAtUtc)
    {
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = 0,
            BindingName = "input",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            SequenceOffset = 0,
            IsRequired = true,
            SelectorJson = job.InputSelectorJson,
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = source.Frame?.AgentId ?? string.Empty,
            ExpectedRigId = source.Frame?.RigId,
            ExpectedCaptureSequence = source.Frame?.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = selectedAtUtc
        };
        job.InputRequirements.Add(requirement);
        job.Inputs.Add(new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = 0,
            CentralArtifactId = source.Id,
            CaptureSequence = source.Frame?.CaptureSequence,
            CompatibilityJson = "{}",
            CompatibilitySha256 = new string('0', 64),
            ByteLength = source.ByteLength,
            SelectedAtUtc = selectedAtUtc
        });
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
    }

    private static async Task<string> GetSystemTokenAsync(HttpClient client, bool administrative)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = administrative ? "system-internal" : "system-camera-agent",
            ["client_secret"] = administrative
                ? "test-internal-secret-do-not-use-in-production"
                : "test-camera-agent-secret-do-not-use-in-production",
            ["scope"] = administrative ? "api.admin" : "api.camera api.frames"
        });
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
