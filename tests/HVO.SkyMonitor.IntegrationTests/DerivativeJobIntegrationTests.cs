using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class DerivativeJobIntegrationTests
{
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
        leases.Single(lease => lease is not null)!.JobId.Should().Be(jobId);
        await using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var job = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Leased);
        job.AttemptCount.Should().Be(1);
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
            RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
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
        source.ReconstructionState = CentralReconstructionState.Complete;
        var scheduler = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();
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
            RecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
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
    public async Task OperationalQuery_FiltersJobsAndDoesNotExposeLeaseToken()
    {
        var (_, _, agentId) = await SeedJobAsync().ConfigureAwait(false);
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
        using var invalid = await client.GetAsync(new Uri(
            "/api/v1.0/derivative-jobs?status=999", UriKind.Relative)).ConfigureAwait(false);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client, administrative: false).ConfigureAwait(false));
        using var forbidden = await client.GetAsync(new Uri(
            "/api/v1.0/derivative-jobs", UriKind.Relative)).ConfigureAwait(false);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            Status = CentralDerivativeJobStatus.Pending,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(source);
        db.CentralDerivativeJobs.Add(job);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (job.Id, frame.Id, frame.AgentId);
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
