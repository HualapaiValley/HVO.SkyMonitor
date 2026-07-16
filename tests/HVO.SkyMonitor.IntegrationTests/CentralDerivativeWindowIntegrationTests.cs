using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class CentralDerivativeWindowIntegrationTests
{
    private const string Bucket = "skymonitor-artifacts";

    [TestMethod]
    public async Task OutOfOrderWindow_ResolvesExecutesAndPersistsExactLineage()
    {
        var scenario = $"window-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 102, 98, 101, 99 })
        {
            var value = checked((ushort)((sequence - 97) * 10));
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(value), "compatible")
                .ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs).ThenInclude(input => input.Artifact)!.ThenInclude(artifact => artifact!.Frame)
                .AsSplitQuery()
                .SingleAsync(item => item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean)
                .ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            job.InputRequirements.OrderBy(item => item.Ordinal).Select(item => item.SequenceOffset)
                .Should().Equal(-2, -1, 0, 1, 2);
            job.Inputs.OrderBy(item => item.Ordinal).Select(item => item.CaptureSequence)
                .Should().Equal(98, 99, 100, 101, 102);
            job.InputSetIdentitySha256.Should().HaveLength(64);
            jobId = job.Id;

        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => NotifySourceAsync(sources[100])))
            .ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralDerivativeJobs.CountAsync(item => item.SourceCentralArtifactId == sources[100]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false)).Should().Be(1);
            (await db.CentralDerivativeJobInputs.CountAsync(item => item.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(5);
        }

        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        CentralDerivativeJobLease lease;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            lease = (await service.ClaimNextAsync(
                "window-integration-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            lease.JobId.Should().Be(jobId);
            lease.Inputs!.Select(input => input.CaptureSequence).Should().Equal(98, 99, 100, 101, 102);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.Attempts)
                .Include(item => item.ResultArtifact)!.ThenInclude(artifact => artifact!.Sources)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            completed.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            completed.ResultArtifact!.Sources.OrderBy(source => source.Ordinal).Select(source => source.ResolvedCentralArtifactId)
                .Should().Equal(new long[] { 98, 99, 100, 101, 102 }.Select(sequence => (Guid?)sources[sequence]));
            completed.ResultArtifact.ChecksumSha256.Should().Be(
                Convert.ToHexString(SHA256.HashData(CreatePayload(30))));
            completed.Attempts.Single().InputBytes.Should().Be(40);

            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            foreach (var sourceId in sources.Values)
            {
                (await retention.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
            }
        }

        Guid replacementJobId;
        Guid previousResultId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            previousResultId = (await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false)).ResultCentralArtifactId!.Value;
            var existingJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .ReprocessAsync(
                    jobId,
                    new CentralDerivativeReprocessRequest(
                        BuiltInProcessingRecipes.RollingMean,
                        CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                        CentralDerivativeRecipeCatalog.RollingMeanVariant,
                        Supersede: false),
                    "window-integration-test",
                    CancellationToken.None)
                .ConfigureAwait(false);
            existingJobId.Should().Be(jobId);
            var retainedJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .ReprocessAsync(
                    jobId,
                    new CentralDerivativeReprocessRequest(
                        BuiltInProcessingRecipes.RollingMean,
                        CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                        "central-rolling-mean-retained-history",
                        Supersede: false),
                    "window-integration-test",
                    CancellationToken.None)
                .ConfigureAwait(false);
            var retainedJob = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == retainedJobId).ConfigureAwait(false);
            retainedJob.PredecessorJobId.Should().BeNull();
            retainedJob.RetainedResultCentralArtifactId.Should().Be(previousResultId);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                .IsHeldAsync(previousResultId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            replacementJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .ReprocessAsync(
                    jobId,
                    new CentralDerivativeReprocessRequest(
                        BuiltInProcessingRecipes.RollingMean,
                        CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                        "central-rolling-mean-reprocessed",
                        Supersede: true),
                    "window-integration-test",
                    CancellationToken.None)
                .ConfigureAwait(false);
            var replacement = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.Inputs)
                .SingleAsync(job => job.Id == replacementJobId).ConfigureAwait(false);
            replacement.PredecessorJobId.Should().Be(jobId);
            replacement.Inputs.OrderBy(input => input.Ordinal).Select(input => input.CaptureSequence)
                .Should().Equal(98, 99, 100, 101, 102);
            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            (await retention.IsHeldAsync(previousResultId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        }

        await DisableOtherActiveJobsAsync(replacementJobId).ConfigureAwait(false);
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var replacementLease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("window-reprocess-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            replacementLease.JobId.Should().Be(replacementJobId);
            await using var executionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var replacement = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(replacementLease, CancellationToken.None).ConfigureAwait(false);
            replacement.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var previous = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.ResultArtifact)
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false);
            var replacement = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == replacementJobId).ConfigureAwait(false);
            previous.Status.Should().Be(CentralDerivativeJobStatus.Superseded);
            previous.SupersededByJobId.Should().Be(replacementJobId);
            previous.ResultArtifact!.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            replacement.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            replacement.ResultCentralArtifactId.Should().NotBe(previousResultId);
        }
    }

    [TestMethod]
    public async Task WaitingWindow_SurvivesScopeRestartAndTimesOutWithReason()
    {
        var scenario = $"window-timeout-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sourceId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 500, capturedBase, CreatePayload(10), "compatible")
            .ConfigureAwait(false);
        var neighborId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 498, capturedBase, CreatePayload(10), "compatible")
            .ConfigureAwait(false);
        Guid jobId;
        DateTimeOffset deadline;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var waiting = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.SourceCentralArtifactId == sourceId
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            waiting.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            waiting.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.WaitingRequiredInput);
            jobId = waiting.Id;
            deadline = waiting.ResolutionDeadlineUtc!.Value;
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        await using (var heldScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = heldScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            (await retention.IsHeldAsync(neighborId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        }

        await using (var restartedScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await restartedScope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveWaitingAsync(deadline.AddTicks(1), CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var terminal = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            terminal.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
            terminal.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
            (await db.CentralDerivativeJobInputs.CountAsync(input => input.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(2);
            terminal.InputRequirements.Count(item => item.ResolutionState == CentralDerivativeInputResolutionState.Missing)
                .Should().Be(3);
            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            (await retention.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
            (await retention.IsHeldAsync(neighborId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .RequeueAsync(jobId, "window-integration-test", CancellationToken.None).ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var requeued = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false);
            requeued.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            requeued.InputSetIdentitySha256.Should().BeNull();
            (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("incomplete-window-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false)).Should().BeNull();
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .CancelAsync(jobId, "window-integration-test", CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task MissingRequiredInput_UsesConfiguredDeadlineOutcome()
    {
        var runJobId = Guid.Empty;
        var cases = new[]
        {
            (Outcome: CentralDerivativeWindowOutcome.Run, Status: CentralDerivativeJobStatus.Pending),
            (Outcome: CentralDerivativeWindowOutcome.Fail, Status: CentralDerivativeJobStatus.TerminalFailure),
            (Outcome: CentralDerivativeWindowOutcome.Quarantine, Status: CentralDerivativeJobStatus.Quarantined)
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var scenario = $"window-outcome-{index}-{Guid.NewGuid():N}";
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario,
                Guid.NewGuid(),
                1_100 + index * 100,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatePayload(10),
                "compatible").ConfigureAwait(false);
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.SingleAsync(item => item.SourceCentralArtifactId == sourceId
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            job.MissingInputOutcome = cases[index].Outcome;
            job.ResolutionDeadlineUtc = now.AddTicks(-1);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(job.Id, now, CancellationToken.None).ConfigureAwait(false);

            db.ChangeTracker.Clear();
            var resolved = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .AsSplitQuery()
                .SingleAsync(item => item.Id == job.Id).ConfigureAwait(false);
            resolved.Status.Should().Be(cases[index].Status);
            resolved.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
            if (cases[index].Outcome == CentralDerivativeWindowOutcome.Run)
            {
                runJobId = resolved.Id;
                resolved.InputSetIdentitySha256.Should().HaveLength(64);
                resolved.Inputs.Should().ContainSingle();
                resolved.InputRequirements.Count(item => item.ResolutionState == CentralDerivativeInputResolutionState.Missing)
                    .Should().Be(4);
            }
        }

        await DisableOtherActiveJobsAsync(runJobId).ConfigureAwait(false);
        await using var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var lease = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("window-outcome-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);
        lease!.JobId.Should().Be(runJobId);
        lease.Inputs.Should().ContainSingle();
        await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .SkipAsync(runJobId, lease.LeaseToken, "test.cleanup", CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IncompatibleRequiredPosition_FinishesWithBoundedReasonWithoutQuarantiningSource()
    {
        var scenario = $"window-incompatible-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 700, 698, 699, 701, 702 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario,
                devicePublicId,
                sequence,
                capturedBase,
                CreatePayload(10),
                sequence == 702 ? "different-profile" : "compatible")
                .ConfigureAwait(false);
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var job = await db.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .SingleAsync(item => item.SourceCentralArtifactId == sources[700]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
        job.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.IncompatibleInput);
        job.InputRequirements.Single(item => item.ExpectedCaptureSequence == 702).ResolutionReasonCode
            .Should().Be(CentralDerivativeWindowReasonCodes.IncompatibleProfile);
        var incompatibleSource = await db.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == sources[702]).ConfigureAwait(false);
        incompatibleSource.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        incompatibleSource.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
    }

    [TestMethod]
    public async Task InvalidatedNeighbor_ReentersResolutionAndRefreezesAfterRepair()
    {
        var scenario = $"window-repair-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 900, 898, 899, 901, 902 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(10), "compatible")
                .ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.SingleAsync(item => item.SourceCentralArtifactId == sources[900]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            jobId = job.Id;
            var neighbor = await db.CentralArtifacts.SingleAsync(item => item.Id == sources[902]).ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Pending;
            neighbor.ReconstructionState = CentralReconstructionState.PendingReference;
            await ArtifactIngestService.InvalidateDependentsAsync(db, neighbor, CancellationToken.None)
                .ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var waiting = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            waiting.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            waiting.InputSetIdentitySha256.Should().BeNull();
            (await db.CentralDerivativeJobInputs.CountAsync(input => input.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(4);

            var neighbor = await LoadSchedulableArtifactAsync(db, sources[902]).ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Available;
            neighbor.ReconstructionState = CentralReconstructionState.Complete;
            neighbor.StateReasonCode = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
                .EnsureRequiredJobsAsync(neighbor, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var repaired = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            repaired.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            repaired.InputSetIdentitySha256.Should().HaveLength(64);
            (await db.CentralDerivativeJobInputs.CountAsync(input => input.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(5);
        }
    }

    [TestMethod]
    public async Task PublishedWindow_InvalidationPreservesFrozenInputsAndRecoversOnlySameLineage()
    {
        var scenario = $"window-published-repair-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 1_200, 1_198, 1_199, 1_201, 1_202 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(10), "compatible")
                .ConfigureAwait(false);
        }

        Guid jobId;
        string inputSetIdentity;
        Guid resultId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item =>
                item.SourceCentralArtifactId == sources[1_200]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            jobId = job.Id;
            inputSetIdentity = job.InputSetIdentitySha256!;
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("published-window-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            await using var executionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            _ = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            resultId = completed.ResultCentralArtifactId!.Value;
            var neighbor = await db.CentralArtifacts.SingleAsync(item => item.Id == sources[1_202])
                .ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Pending;
            neighbor.ReconstructionState = CentralReconstructionState.PendingReference;
            await ArtifactIngestService.InvalidateDependentsAsync(db, neighbor, CancellationToken.None)
                .ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var suspended = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Inputs)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            suspended.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
            suspended.InputSetIdentitySha256.Should().Be(inputSetIdentity);
            suspended.Inputs.Should().HaveCount(5);
            suspended.ResultCentralArtifactId.Should().Be(resultId);

            var neighbor = await db.CentralArtifacts.SingleAsync(item => item.Id == sources[1_202])
                .ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Available;
            neighbor.ReconstructionState = CentralReconstructionState.Complete;
            neighbor.StateReasonCode = null;
            var result = await db.CentralArtifacts.Include(item => item.Sources)
                .SingleAsync(item => item.Id == resultId).ConfigureAwait(false);
            result.Sources.Single(source => source.ResolvedCentralArtifactId == null).ResolvedCentralArtifactId = neighbor.Id;
            result.ReconstructionState = CentralReconstructionState.Complete;
            result.StateReasonCode = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("published-window-recovery", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            lease.JobId.Should().Be(jobId);
            await using var recoveryScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var recoveredArtifactId = await recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeOutputWriter>()
                .TryCompletePendingAsync(lease, CancellationToken.None).ConfigureAwait(false);
            recoveredArtifactId.Should().NotBeNull();
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var recovered = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            recovered.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            recovered.ResultCentralArtifactId.Should().Be(resultId);
            recovered.InputSetIdentitySha256.Should().Be(inputSetIdentity);
        }
    }

    [TestMethod]
    public async Task SourcesReceivedAfterDeadline_CannotMakeWindowRunnable()
    {
        var scenario = $"window-late-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var centerId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 1_000, capturedBase, CreatePayload(10), "compatible")
            .ConfigureAwait(false);
        Guid jobId;
        DateTimeOffset deadline;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.SourceCentralArtifactId == centerId
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            jobId = job.Id;
            deadline = job.ResolutionDeadlineUtc!.Value;
        }
        foreach (var sequence in new long[] { 998, 999, 1001, 1002 })
        {
            _ = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(10), "compatible", deadline.AddSeconds(1))
                .ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(jobId, deadline.AddSeconds(2), CancellationToken.None).ConfigureAwait(false);
            var terminal = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            terminal.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
            terminal.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
        }
    }

    private static async Task<Guid> SeedAndScheduleSourceAsync(
        string scenario,
        Guid devicePublicId,
        long sequence,
        DateTimeOffset capturedBase,
        byte[] payload,
        string profileSeed,
        DateTimeOffset? receivedAtUtc = null)
    {
        var capturedAtUtc = capturedBase.AddSeconds(sequence);
        var objectKey = $"integration/{scenario}/{sequence:D8}.raw";
        var checksum = Convert.ToHexString(SHA256.HashData(payload));
        await using var uploadScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var minio = uploadScope.ServiceProvider.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), CancellationToken.None)
            .ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), CancellationToken.None)
                .ConfigureAwait(false);
        }
        await using (var stream = new MemoryStream(payload, writable: false))
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket)
                .WithObject(objectKey)
                .WithStreamData(stream)
                .WithObjectSize(payload.Length)
                .WithContentType("application/x-hvo-linear-frame"), CancellationToken.None).ConfigureAwait(false);
        }

        var db = uploadScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = devicePublicId,
            ObservatoryId = Guid.NewGuid(),
            AgentId = scenario,
            FrameId = Guid.NewGuid(),
            RigProfileVersion = 1,
            RigId = $"{scenario}-rig",
            CaptureSequence = sequence,
            CapturedAtUtc = capturedAtUtc,
            FirstReceivedAtUtc = DateTimeOffset.UtcNow
        };
        frame.Timing = new CentralCaptureTiming
        {
            RequestedStartUtc = capturedAtUtc.AddSeconds(-2),
            ExposureStartedUtc = capturedAtUtc.AddSeconds(-1),
            ExposureEndedUtc = capturedAtUtc.AddMilliseconds(-100),
            ReadoutCompletedUtc = capturedAtUtc.AddMilliseconds(-50),
            DurableIngressUtc = capturedAtUtc
        };
        frame.Control = new CentralCaptureControl
        {
            RequestedExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
            EffectiveExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
            RequestedGain = 100,
            EffectiveGain = 100,
            RequestedOffset = 1,
            EffectiveOffset = 1,
            TemperatureSetpointC = -5,
            EffectiveTemperatureC = -5
        };
        var profileSha = HashText(profileSeed);
        foreach (var kind in Enum.GetValues<CentralProfileKind>())
        {
            frame.Profiles.Add(new CentralCaptureProfile
            {
                Kind = kind,
                Name = $"{scenario}-{kind}",
                Version = "1",
                Sha256 = profileSha
            });
        }
        var rawOptions = CaptureContractJson.SerializeToElement(new { });
        var source = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = devicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "window-raw-v1",
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
            MediaType = "application/x-hvo-linear-frame",
            ByteLength = payload.Length,
            ChecksumSha256 = checksum,
            StorageReference = $"minio://{Bucket}/{objectKey}",
            ReceivedAtUtc = receivedAtUtc ?? DateTimeOffset.UtcNow,
            IdempotencyKey = HashText($"{scenario}-{sequence}"),
            SourceId = "window-integration",
            Variant = "native",
            CreatedUtc = capturedAtUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            ReconciledAtUtc = receivedAtUtc ?? DateTimeOffset.UtcNow,
            Layout = new CentralArtifactLayout
            {
                Width = 2,
                Height = 2,
                StrideBytes = 4,
                PixelFormat = CameraPixelFormat.Mono16.ToString(),
                ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                SampleDepthBits = 16,
                ContainerDepthBits = 16,
                Packing = FrameSamplePacking.ByteAligned.ToString(),
                CfaPattern = ColorFilterArrayPattern.None.ToString(),
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                ByteLength = payload.Length
            },
            Recipe = new CentralArtifactRecipe
            {
                Name = "raw-capture",
                SemanticVersion = "1.0.0",
                ImplementationVersion = "window-integration-v1",
                OptionsJson = CaptureContractJson.Canonicalize(rawOptions).GetRawText(),
                OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(rawOptions)
            }
        };
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await uploadScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        return source.Id;
    }

    private static async Task<CentralArtifact> LoadSchedulableArtifactAsync(ApplicationDbContext db, Guid sourceId)
        => await db.CentralArtifacts
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(artifact => artifact.Id == sourceId).ConfigureAwait(false);

    private static async Task NotifySourceAsync(Guid sourceId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await LoadSchedulableArtifactAsync(db, sourceId).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task DisableOtherActiveJobsAsync(Guid preservedJobId)
        => WithDbAsync(db => db.CentralDerivativeJobs.Where(job => job.Id != preservedJobId
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)));

    private static async Task WithDbAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()).ConfigureAwait(false);
    }

    private static byte[] CreatePayload(ushort value)
    {
        var payload = new byte[8];
        for (var offset = 0; offset < payload.Length; offset += sizeof(ushort))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, sizeof(ushort)), value);
        }
        return payload;
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
