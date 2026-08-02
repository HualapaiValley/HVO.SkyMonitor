using System.Net;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralTransientEventPersistenceIntegrationTests
{
    private static readonly byte[] Issue250ReplacementPayload = [9, 8, 7, 6];
    private static readonly string[] Issue250TelemetryFieldNames = ["Kind", "Outcome", "RetryCount"];

    [TestMethod]
    public async Task PayloadReleaseReservation_CommitsBeforeDeleteAndFreshProcessorReclaimsExpiredLease()
    {
        await using var database = CreateDatabase("Issue250ReservationRestart");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            var fault = new Issue250StageFaultInjector((stage, _, _, _) =>
                stage == CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete
                    ? Task.FromException(new InvalidOperationException("issue-250-process-termination"))
                    : Task.CompletedTask);
            var service = CreateIssue250Service(database.Context, minio, clock, fault);

            Func<Task> interrupted = () => service.ProcessNextAsync(CancellationToken.None);
            await interrupted.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("issue-250-process-termination").ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var reserved = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .SingleAsync(item => item.RecordId == seed.Items[0].RecordId)
                .ConfigureAwait(false);
            reserved.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
            reserved.ReservationToken.Should().NotBeNull();
            reserved.RequestedAtUtc.Should().Be(seed.CreatedUtc);
            reserved.StorageReference.Should().Be(seed.Items[0].StorageReference);
            reserved.TargetRowVersion.Should().HaveCount(8);
            reserved.TargetGeneration.Should().Be(seed.Items[0].Generation);
            reserved.RowVersion.Should().HaveCount(8);
            var reservedHealth = await new CentralTransientLifecycleHealthCheck(
                    database.Context,
                    clock,
                    Options.Create(new CentralTransientNotificationOptions { FenceTimeout = TimeSpan.FromMinutes(1) }),
                    Issue250ReleaseOptions())
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            reservedHealth.Data["ReservedPayloadReleaseItemCount"].Should().Be(1);
            Convert.ToInt64(reservedHealth.Data["ReservedPayloadReleaseLogicalBytes"], CultureInfo.InvariantCulture)
                .Should().BeGreaterThan(0L);
            reservedHealth.Data["OldestReservedPayloadReleaseItemAgeSeconds"].Should().Be(0D);
            await AssertIssue250ObjectExistsAsync(minio, seed.Items[0].ObjectKey).ConfigureAwait(false);

            clock.Advance(TimeSpan.FromMinutes(2));
            await using var recoveryDb = CreateContext(database.ConnectionString);
            var recovery = CreateIssue250Service(recoveryDb, minio, clock);
            (await recovery.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            recoveryDb.ChangeTracker.Clear();
            var completed = await recoveryDb.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
            completed.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
            var terminal = completed.Items.Single(item => item.RecordId == seed.Items[0].RecordId);
            terminal.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Released);
            terminal.ReservationToken.Should().BeNull();
            terminal.RetryAtUtc.Should().BeNull();
            terminal.RequestedAtUtc.Should().Be(clock.GetUtcNow());
            terminal.StorageReference.Should().Be(seed.Items[0].StorageReference);
            await AssertIssue250ObjectAbsentAsync(minio, seed.Items[0].ObjectKey).ConfigureAwait(false);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseReservation_StaleTargetSnapshotRetriesWithoutDeletingStaleReference()
    {
        await using var database = CreateDatabase("Issue250StaleTarget");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        string? replacementKey = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            replacementKey = $"issue-250/{Guid.NewGuid():N}/replacement.bin";
            await PutIssue250ObjectAsync(minio, replacementKey, Issue250ReplacementPayload).ConfigureAwait(false);
            var replacementReference = "minio://skymonitor-artifacts/" + replacementKey;
            var mutated = false;
            var fault = new Issue250StageFaultInjector(async (stage, _, _, token) =>
            {
                if (stage != CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete || mutated)
                {
                    return;
                }
                mutated = true;
                await using var mutationDb = CreateContext(database.ConnectionString);
                _ = await mutationDb.CentralArtifacts.Where(item => item.Id == seed.Items[0].RecordId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.StorageReference, replacementReference)
                        .SetProperty(item => item.RecoveryGeneration, item => item.RecoveryGeneration + 1), token)
                    .ConfigureAwait(false);
            });
            var service = CreateIssue250Service(database.Context, minio, clock, fault);

            (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var retry = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .SingleAsync(item => item.RecordId == seed.Items[0].RecordId)
                .ConfigureAwait(false);
            retry.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
            retry.ReservationToken.Should().BeNull();
            retry.RetryCount.Should().Be(1);
            retry.RetryAtUtc.Should().Be(seed.CreatedUtc.AddSeconds(2));
            retry.StorageReference.Should().Be(seed.Items[0].StorageReference);
            await AssertIssue250ObjectExistsAsync(minio, seed.Items[0].ObjectKey).ConfigureAwait(false);
            await AssertIssue250ObjectExistsAsync(minio, replacementKey).ConfigureAwait(false);

            clock.Advance(TimeSpan.FromSeconds(2));
            await using var recoveryDb = CreateContext(database.ConnectionString);
            var recovery = CreateIssue250Service(recoveryDb, minio, clock);
            (await recovery.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            recoveryDb.ChangeTracker.Clear();
            var terminal = await recoveryDb.CentralTransientPayloadReleaseItems.AsNoTracking()
                .SingleAsync(item => item.RecordId == seed.Items[0].RecordId)
                .ConfigureAwait(false);
            terminal.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Released);
            terminal.RetryCount.Should().Be(1);
            terminal.StorageReference.Should().Be(replacementReference);
            terminal.TargetGeneration.Should().Be(seed.Items[0].Generation + 1);
            terminal.TargetRowVersion.Should().HaveCount(8);
            await AssertIssue250ObjectExistsAsync(minio, seed.Items[0].ObjectKey).ConfigureAwait(false);
            await AssertIssue250ObjectAbsentAsync(minio, replacementKey).ConfigureAwait(false);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            if (replacementKey is not null)
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket("skymonitor-artifacts")
                    .WithObject(replacementKey)).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseLateHolds_PreserveBeforeDeleteAndRetryAfterDelete()
    {
        await using var database = CreateDatabase("Issue250LateHolds");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 2).ConfigureAwait(false);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            var references = new Issue250SwitchableRetentionReferences(
                new CentralArtifactRetentionReferences(database.Context));
            var firstHoldAdded = false;
            var secondHoldAdded = false;
            var fault = new Issue250StageFaultInjector(async (stage, _, ordinal, _) =>
            {
                if (ordinal == 0 && stage == CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete &&
                    !firstHoldAdded)
                {
                    firstHoldAdded = true;
                    references.Hold(seed.Items[0].RecordId);
                }
                if (ordinal == 1 && stage == CentralTransientPayloadReleaseFaultStage.DeleteCompletedBeforeFinalize &&
                    !secondHoldAdded)
                {
                    secondHoldAdded = true;
                    references.Clear();
                    references.Hold(seed.Items[1].RecordId);
                }
                await Task.CompletedTask.ConfigureAwait(false);
            });
            var service = CreateIssue250Service(database.Context, minio, clock, fault, references);

            (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var items = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .OrderBy(item => item.Ordinal).ToArrayAsync().ConfigureAwait(false);
            items[0].Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Failed);
            items[0].ReleasedUtc.Should().NotBeNull();
            items[0].ReservationToken.Should().BeNull();
            items[1].Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Failed);
            items[1].FailureReasonCode.Should().Be("transient-retention.hold-after-delete");
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .State.Should().Be(CentralTransientPayloadReleaseState.Failed);
            await AssertIssue250ObjectExistsAsync(minio, seed.Items[0].ObjectKey).ConfigureAwait(false);
            await AssertIssue250ObjectAbsentAsync(minio, seed.Items[1].ObjectKey).ConfigureAwait(false);

            await AssertIssue250ObjectExistsAsync(minio, seed.Items[0].ObjectKey).ConfigureAwait(false);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseRetryAndOrdinalCrashes_ConvergeWithTwoProcessorsAndNoFalseCompletion()
    {
        await using var database = CreateDatabase("Issue250RetryAndConcurrency");
        using var handler = new Issue250FailFirstDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        var minio = CreateIssue250Minio(handler);
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 2).ConfigureAwait(false);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            var service = CreateIssue250Service(database.Context, minio, clock);

            (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var retry = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .OrderBy(item => item.Ordinal).FirstAsync().ConfigureAwait(false);
            retry.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
            retry.ReservationToken.Should().BeNull();
            retry.RetryCount.Should().Be(1);
            retry.RetryAtUtc.Should().Be(seed.CreatedUtc.AddSeconds(2));
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .State.Should().Be(CentralTransientPayloadReleaseState.Pending);
            var health = new CentralTransientLifecycleHealthCheck(
                database.Context,
                clock,
                Options.Create(new CentralTransientNotificationOptions { FenceTimeout = TimeSpan.FromMinutes(1) }),
                Issue250ReleaseOptions());
            var healthResult = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            healthResult.Status.Should().Be(HealthStatus.Healthy);
            healthResult.Data["PendingPayloadReleaseItemCount"].Should().Be(5);
            healthResult.Data["RetryDuePayloadReleaseItemCount"].Should().Be(0);
            healthResult.Data["StaleReservedPayloadReleaseItemCount"].Should().Be(0);
            Convert.ToInt64(healthResult.Data["PendingPayloadReleaseLogicalBytes"], CultureInfo.InvariantCulture)
                .Should().BeGreaterThan(0L);
            var staleHealth = new CentralTransientLifecycleHealthCheck(
                database.Context,
                new MutableIssue250TimeProvider(seed.CreatedUtc.AddSeconds(31)),
                Options.Create(new CentralTransientNotificationOptions { FenceTimeout = TimeSpan.FromMinutes(1) }),
                Issue250ReleaseOptions());
            var staleHealthResult = await staleHealth.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            staleHealthResult.Status.Should().Be(HealthStatus.Degraded);
            staleHealthResult.Data["RetryDuePayloadReleaseItemCount"].Should().Be(1);
            (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();

            clock.Advance(TimeSpan.FromSeconds(2));
            var ordinalCrash = false;
            var parentCrash = false;
            var fault = new Issue250StageFaultInjector((stage, _, ordinal, _) =>
            {
                if (stage == CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete &&
                    ordinal == 1 && !ordinalCrash)
                {
                    ordinalCrash = true;
                    return Task.FromException(new InvalidOperationException("issue-250-between-ordinals"));
                }
                if (stage == CentralTransientPayloadReleaseFaultStage.FinalItemBeforeParentCompletion && !parentCrash)
                {
                    parentCrash = true;
                    return Task.FromException(new InvalidOperationException("issue-250-before-parent-completion"));
                }
                return Task.CompletedTask;
            });
            await using (var interruptedDb = CreateContext(database.ConnectionString))
            {
                var interruptedService = CreateIssue250Service(interruptedDb, minio, clock, fault);
                Func<Task> betweenOrdinals = () => interruptedService.ProcessNextAsync(CancellationToken.None);
                await betweenOrdinals.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("issue-250-between-ordinals").ConfigureAwait(false);
            }
            database.Context.ChangeTracker.Clear();
            var interruptedItems = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .OrderBy(item => item.Ordinal).ToArrayAsync().ConfigureAwait(false);
            interruptedItems[0].Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Released);
            interruptedItems[1].Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
            interruptedItems[1].ReservationToken.Should().NotBeNull();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .State.Should().Be(CentralTransientPayloadReleaseState.Pending);

            clock.Advance(TimeSpan.FromMinutes(2));
            await using (var finalItemDb = CreateContext(database.ConnectionString))
            {
                var finalItemService = CreateIssue250Service(finalItemDb, minio, clock, fault);
                Func<Task> beforeParent = () => finalItemService.ProcessNextAsync(CancellationToken.None);
                await beforeParent.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("issue-250-before-parent-completion").ConfigureAwait(false);
            }
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .AllAsync(item => item.Outcome != CentralTransientPayloadReleaseItemOutcome.Pending)
                .ConfigureAwait(false)).Should().BeTrue();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .State.Should().Be(CentralTransientPayloadReleaseState.Pending);

            await using var firstDb = CreateContext(database.ConnectionString);
            await using var secondDb = CreateContext(database.ConnectionString);
            var first = CreateIssue250Service(firstDb, minio, clock);
            var second = CreateIssue250Service(secondDb, minio, clock);
            var results = await Task.WhenAll(
                first.ProcessNextAsync(CancellationToken.None),
                second.ProcessNextAsync(CancellationToken.None)).ConfigureAwait(false);
            results.Count(result => result).Should().Be(1);
            database.Context.ChangeTracker.Clear();
            var completed = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
            completed.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
            completed.Items.Should().OnlyContain(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
            handler.DeleteAttempts.Should().Be(6);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseLostDeleteResponse_RetriesAbsentObjectAndConvergesIdempotently()
    {
        await using var database = CreateDatabase("Issue250LostDeleteResponse");
        using var handler = new Issue250LoseFirstDeleteResponseHandler { InnerHandler = new SocketsHttpHandler() };
        var minio = CreateIssue250Minio(handler);
        var cleanupMinio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            var service = CreateIssue250Service(database.Context, minio, clock);

            (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var retry = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .OrderBy(item => item.Ordinal).FirstAsync().ConfigureAwait(false);
            retry.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Released);
            retry.RetryCount.Should().Be(0);
            retry.ReservationToken.Should().BeNull();
            await AssertIssue250ObjectAbsentAsync(cleanupMinio, seed.Items[0].ObjectKey).ConfigureAwait(false);

            var completed = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
            completed.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
            completed.Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
            completed.Items.Should().OnlyContain(item => item.RetryCount == 0);
            handler.DeleteAttempts.Should().Be(5);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(cleanupMinio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseRetryExhaustion_TerminalizesItemsAndFailsParentWithoutPendingWork()
    {
        await using var database = CreateDatabase("Issue250RetryExhaustion");
        using var handler = new Issue250AlwaysFailDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        var minio = CreateIssue250Minio(handler);
        var cleanupMinio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 5).ConfigureAwait(false);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await using var processorDb = CreateContext(database.ConnectionString);
                var processor = CreateIssue250Service(processorDb, minio, clock);
                if (!await processor.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    break;
                }
                clock.Advance(TimeSpan.FromSeconds(8));
            }

            database.Context.ChangeTracker.Clear();
            var release = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
            release.State.Should().Be(CentralTransientPayloadReleaseState.Failed);
            release.ReasonCode.Should().Be("transient-retention.item-failed");
            release.Items.Should().HaveCount(5);
            release.Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Failed &&
                item.RetryCount == 5 && item.RetryAtUtc == null && item.ReservationToken == null &&
                item.ReleasedUtc != null && item.FailureReasonCode == "transient-retention.delete-retry-exhausted");
            handler.DeleteAttempts.Should().Be(25);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(cleanupMinio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseUnknownDeleteOutcome_FailsAmbiguouslyAndPreservesTheObject()
    {
        await using var database = CreateDatabase("Issue250AmbiguousDeleteOutcome");
        using var handler = new Issue250UnknownDeleteOutcomeHandler { InnerHandler = new SocketsHttpHandler() };
        var minio = CreateIssue250Minio(handler);
        var cleanupMinio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            handler.Arm();
            var service = new CentralTransientPayloadReleaseService(
                database.Context,
                new CentralArtifactRetentionReferences(database.Context),
                minio,
                Issue250ReleaseOptions(maximumRetryCount: 1),
                new MutableIssue250TimeProvider(seed.CreatedUtc));

            (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var release = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
            release.State.Should().Be(CentralTransientPayloadReleaseState.Failed);
            release.Items.Should().HaveCount(5);
            release.Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Failed &&
                item.RetryCount == 1 &&
                item.FailureReasonCode == "transient-retention.delete-outcome-ambiguous");
            foreach (var target in seed.Items)
            {
                await AssertIssue250ObjectExistsAsync(cleanupMinio, target.ObjectKey).ConfigureAwait(false);
            }
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(cleanupMinio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseClaim_SkipsLockedOldestParentAndProcessesSecondDueParent()
    {
        await using var database = CreateDatabase("Issue250NonBlockingClaim");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        var seeds = new List<Issue250ReleaseSeed>();
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seeds.Add(await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false));
            seeds.Add(await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false));
            var clock = new MutableIssue250TimeProvider(seeds[0].CreatedUtc);
            await using var firstDb = CreateContext(database.ConnectionString);
            await using var secondDb = CreateContext(database.ConnectionString);
            await using var probeDb = CreateContext(database.ConnectionString);
            var first = CreateIssue250Service(firstDb, minio, clock);
            var second = CreateIssue250Service(secondDb, minio, clock);
            var orderedReleaseIds = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .OrderBy(item => item.CreatedUtc)
                .ThenBy(item => item.ReleaseId)
                .Select(item => item.ReleaseId)
                .ToArrayAsync().ConfigureAwait(false);
            var lockedReleaseId = orderedReleaseIds[0];
            var otherReleaseId = orderedReleaseIds[1];
            var parentResource = $"central-transient-payload-release:{lockedReleaseId:N}";

            await using var firstParentLock = await CentralObjectApplicationLock.AcquireAsync(
                firstDb,
                parentResource,
                CancellationToken.None).ConfigureAwait(false);
            var missStarted = Stopwatch.StartNew();
            await using var missedLock = await CentralObjectApplicationLock.TryAcquireAsync(
                probeDb, parentResource, CancellationToken.None).ConfigureAwait(false);
            missStarted.Stop();
            missedLock.Should().BeNull();
            missStarted.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            var firstItem = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .Where(item => item.ReleaseId == lockedReleaseId)
                .OrderBy(item => item.Ordinal).FirstAsync().ConfigureAwait(false);
            firstItem.RequestedAtUtc.Should().BeNull();
            firstItem.ReservationToken.Should().BeNull();
            var secondResult = await second.ProcessNextAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            secondResult.Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var whileLocked = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items)
                .Where(item => orderedReleaseIds.Contains(item.ReleaseId))
                .ToArrayAsync().ConfigureAwait(false);
            var locked = whileLocked.Single(item => item.ReleaseId == lockedReleaseId);
            locked.State.Should().Be(CentralTransientPayloadReleaseState.Pending);
            locked.Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.RequestedAtUtc == null && item.ReservationToken == null);
            var completedOther = whileLocked.Single(item => item.ReleaseId == otherReleaseId);
            completedOther.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
            completedOther.Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
            await firstParentLock.DisposeAsync().ConfigureAwait(false);
            (await first.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();

            database.Context.ChangeTracker.Clear();
            var releases = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).ToArrayAsync().ConfigureAwait(false);
            releases.Should().HaveCount(2);
            releases.Should().OnlyContain(item => item.State == CentralTransientPayloadReleaseState.Completed);
            releases.SelectMany(item => item.Items).Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
        }
        finally
        {
            foreach (var seed in seeds)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseApiReplay_ReturnsAcceptedWhileSameReleaseDeleteIsLive()
    {
        await using var database = CreateDatabase("Issue250ConcurrentApiReplay");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            var release = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .SingleAsync().ConfigureAwait(false);
            var eventRowVersion = await database.Context.CentralTransientEventCurrent.AsNoTracking()
                .Where(item => item.CentralTransientEventId == release.CentralTransientEventId)
                .Select(item => item.RowVersion).SingleAsync().ConfigureAwait(false);
            var principal = CreateOwnerPrincipal(release.ActorIdentity, admin: true);
            var clock = new MutableIssue250TimeProvider(seed.CreatedUtc);
            await using var firstDb = CreateContext(database.ConnectionString);
            await using var secondDb = CreateContext(database.ConnectionString);
            var fault = new Issue250StageFaultInjector(async (stage, _, ordinal, token) =>
            {
                if (stage == CentralTransientPayloadReleaseFaultStage.BeforeDelete && ordinal == 0)
                {
                    entered.TrySetResult();
                    await resume.Task.WaitAsync(token).ConfigureAwait(false);
                }
            });
            var first = CreateIssue250Service(firstDb, minio, clock, fault);
            var second = CreateIssue250Service(secondDb, minio, clock);

            var firstTask = first.ProcessNextAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var secondResult = await second.ReleaseAsync(
                    principal,
                    release.CentralTransientEventId,
                    eventRowVersion,
                    release.IdempotencyKey,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            secondResult.Status.Should().Be(CentralTransientPayloadReleaseStatus.Accepted);
            secondResult.Response!.State.Should().Be(CentralTransientPayloadReleaseState.Pending);
            secondResult.Response.Replayed.Should().BeTrue();

            resume.TrySetResult();
            var firstResult = await firstTask.ConfigureAwait(false);
            firstResult.Should().BeTrue();
            database.Context.ChangeTracker.Clear();
            var completed = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
            completed.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
            completed.Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
        }
        finally
        {
            resume.TrySetResult();
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadHoldFence_ReverseInputOperationsAcquireOneCompleteSortedSetWithoutDeadlock()
    {
        await using var database = CreateDatabase("Issue250CompleteLockOrder");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            var ids = seed.Items.Take(2).Select(item => item.RecordId).ToArray();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task AcquireAsync(bool reverse)
            {
                await using var context = CreateContext(database.ConnectionString);
                var targets = await CentralTransientPayloadHoldFence.ReadArtifactsAsync(
                    context, reverse ? ids.Reverse() : ids, CancellationToken.None).ConfigureAwait(false);
                await start.Task.ConfigureAwait(false);
                await using var scope = await CentralTransientPayloadHoldFence.AcquireAsync(
                    context,
                    reverse ? targets.Reverse().ToArray() : targets,
                    CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);
            }

            var first = AcquireAsync(reverse: false);
            var second = AcquireAsync(reverse: true);
            start.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadRelease_ProductionPublicationAndClearWritersLoseReservationAndLiveDeleteRaces()
    {
        var rawMinio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        await using (var controlDatabase = CreateDatabase("Issue250PublicationControl"))
        {
            Issue250PublicationSeed? control = null;
            try
            {
                await controlDatabase.Context.Database.MigrateAsync().ConfigureAwait(false);
                control = await SeedIssue250PublicationAsync(controlDatabase.Context, rawMinio)
                    .ConfigureAwait(false);
                var result = await PublishIssue250ArtifactAsync(
                    controlDatabase.ConnectionString, control, "issue-250-control").ConfigureAwait(false);
                result.Outcome.Should().Be(PublicRecordPublicationOutcome.Applied);
                controlDatabase.Context.ChangeTracker.Clear();
                var decision = await controlDatabase.Context.PublicRecordPublicationDecisions.AsNoTracking()
                    .SingleAsync().ConfigureAwait(false);
                decision.AuthorityObservatoryId.Should().Be(control.ObservatoryId);
                decision.ActorUserId.Should().Be(control.OwnerUserId);
                decision.CentralArtifactId.Should().Be(control.Release.Items[0].RecordId);
                decision.State.Should().Be(PublicationDecisionState.Released);
                var releaseItem = await controlDatabase.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                    .SingleAsync(item => item.ReleaseId == control.Release.ReleaseId && item.Ordinal == 0)
                    .ConfigureAwait(false);
                releaseItem.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
                releaseItem.RequestedAtUtc.Should().BeNull();
                releaseItem.ReservationToken.Should().BeNull();
                (await controlDatabase.Context.CentralArtifacts.AsNoTracking()
                    .SingleAsync(item => item.Id == control.Release.Items[0].RecordId).ConfigureAwait(false))
                    .ObjectState.Should().Be(CentralArtifactObjectState.Available);
                await AssertIssue250ObjectExistsAsync(rawMinio, control.Release.Items[0].ObjectKey)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (control is not null)
                {
                    await CleanupIssue250ObjectsAsync(rawMinio, control.Release).ConfigureAwait(false);
                }
                await controlDatabase.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            }
        }

        await using (var publicationDatabase = CreateDatabase("Issue250PublicationRace"))
        {
            Issue250PublicationSeed? race = null;
            try
            {
                await publicationDatabase.Context.Database.MigrateAsync().ConfigureAwait(false);
                race = await SeedIssue250PublicationAsync(publicationDatabase.Context, rawMinio)
                    .ConfigureAwait(false);
                var fault = new Issue250StageFaultInjector((stage, _, ordinal, _) =>
                    stage == CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete && ordinal == 0
                        ? Task.FromException(new InvalidOperationException("issue-250-stop-after-reservation"))
                        : Task.CompletedTask);
                var clock = new MutableIssue250TimeProvider(race.Release.CreatedUtc);
                Func<Task> interrupted = () => CreateIssue250Service(
                    publicationDatabase.Context, rawMinio, clock, fault).ProcessNextAsync(CancellationToken.None);
                await interrupted.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("issue-250-stop-after-reservation").ConfigureAwait(false);
                publicationDatabase.Context.ChangeTracker.Clear();
                var reserved = await publicationDatabase.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                    .SingleAsync(item => item.ReleaseId == race.Release.ReleaseId && item.Ordinal == 0)
                    .ConfigureAwait(false);
                reserved.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
                reserved.RequestedAtUtc.Should().Be(race.Release.CreatedUtc);
                reserved.ReservationToken.Should().NotBeNull();
                reserved.StorageReference.Should().Be(race.Release.Items[0].StorageReference);
                (await publicationDatabase.Context.CentralArtifacts.AsNoTracking()
                    .SingleAsync(item => item.Id == race.Release.Items[0].RecordId).ConfigureAwait(false))
                    .ObjectState.Should().Be(CentralArtifactObjectState.Available);
                await AssertIssue250ObjectExistsAsync(rawMinio, race.Release.Items[0].ObjectKey)
                    .ConfigureAwait(false);

                var publication = await PublishIssue250ArtifactAsync(
                    publicationDatabase.ConnectionString, race, "issue-250-reservation-race")
                    .ConfigureAwait(false);
                publication.Outcome.Should().Be(PublicRecordPublicationOutcome.NotFoundOrDenied);
                (await publicationDatabase.Context.PublicRecordPublicationDecisions.CountAsync().ConfigureAwait(false))
                    .Should().Be(0);

                clock.Advance(TimeSpan.FromMinutes(2));
                await using var recoveryDb = CreateContext(publicationDatabase.ConnectionString);
                (await CreateIssue250Service(recoveryDb, rawMinio, clock).ProcessNextAsync(CancellationToken.None)
                    .ConfigureAwait(false)).Should().BeTrue();
                var recovered = await recoveryDb.CentralTransientPayloadReleases.AsNoTracking()
                    .Include(item => item.Items).SingleAsync().ConfigureAwait(false);
                recovered.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
                recovered.CompletedUtc.Should().Be(clock.GetUtcNow());
                recovered.ReasonCode.Should().BeNull();
                recovered.Items.Should().OnlyContain(item =>
                    item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released &&
                    item.ReservationToken == null && item.RetryAtUtc == null && item.FailureReasonCode == null);
                (await recoveryDb.CentralArtifacts.AsNoTracking()
                    .SingleAsync(item => item.Id == race.Release.Items[0].RecordId).ConfigureAwait(false))
                    .ObjectState.Should().Be(CentralArtifactObjectState.Expired);
                (await recoveryDb.PublicRecordPublicationDecisions.CountAsync().ConfigureAwait(false)).Should().Be(0);
                await AssertIssue250ObjectAbsentAsync(rawMinio, race.Release.Items[0].ObjectKey)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (race is not null)
                {
                    await CleanupIssue250ObjectsAsync(rawMinio, race.Release).ConfigureAwait(false);
                }
                await publicationDatabase.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            }
        }

        await using var clearDatabase = CreateDatabase("Issue250ClearRace");
        using var handler = new Issue250DelayFirstDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        var delayedMinio = CreateIssue250Minio(handler);
        Issue250ReleaseSeed? clearSeed = null;
        ApplicationDbContext? releaseDb = null;
        try
        {
            await clearDatabase.Context.Database.MigrateAsync().ConfigureAwait(false);
            clearSeed = await SeedIssue250ReleaseAsync(clearDatabase.Context, delayedMinio, 1).ConfigureAwait(false);
            var frameIdentity = await clearDatabase.Context.CentralArtifacts.AsNoTracking()
                .Where(artifact => artifact.Id == clearSeed.Items[0].RecordId)
                .Select(artifact => new
                {
                    artifact.Frame!.RegistrationId,
                    DevicePublicId = artifact.DevicePublicId!.Value
                }).SingleAsync().ConfigureAwait(false);
            var observatory = new Observatory
            {
                OwnerUserId = "issue-250-clear-owner",
                Name = "Issue 250 clear-reference race",
                TimeZoneId = "UTC",
                CreatedAtUtc = clearSeed.CreatedUtc,
                IsActive = true
            };
            clearDatabase.Context.DeviceRegistrations.Add(new DeviceRegistration
            {
                Id = frameIdentity.RegistrationId,
                DeviceId = $"issue-250-clear-{Guid.NewGuid():N}",
                ObservatoryId = observatory.Id,
                Observatory = observatory,
                FriendlyName = "Issue 250 clear-reference race",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = observatory.OwnerUserId,
                OwnerDisplayName = observatory.OwnerUserId,
                OwnerConfirmationMethod = "SelfAttested",
                OwnerConfirmedAtUtc = clearSeed.CreatedUtc,
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
                DevicePublicId = frameIdentity.DevicePublicId,
                IssuedAtUtc = clearSeed.CreatedUtc,
                ActivatedAtUtc = clearSeed.CreatedUtc
            });
            await clearDatabase.Context.SaveChangesAsync().ConfigureAwait(false);
            _ = await clearDatabase.Context.CentralFrames
                .Where(frame => frame.Artifacts.Any(artifact => artifact.Id == clearSeed.Items[0].RecordId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(frame => frame.RigId, "issue-250-rig"))
                .ConfigureAwait(false);
            var identity = await clearDatabase.Context.CentralArtifacts.AsNoTracking()
                .Where(item => item.Id == clearSeed.Items[0].RecordId)
                .Select(item => new
                {
                    DevicePublicId = item.DevicePublicId!.Value,
                    item.ArtifactId,
                    RigId = item.Frame!.RigId!
                }).SingleAsync().ConfigureAwait(false);
            releaseDb = CreateContext(clearDatabase.ConnectionString);
            var releaseTask = CreateIssue250Service(
                releaseDb, delayedMinio, new MutableIssue250TimeProvider(clearSeed.CreatedUtc))
                .ProcessNextAsync(CancellationToken.None);
            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await using var clearWriterDb = CreateContext(clearDatabase.ConnectionString);
            var clearTask = new CentralClearReferenceService(clearWriterDb, TimeProvider.System).SetAsync(
                identity.DevicePublicId,
                identity.RigId,
                identity.ArtifactId,
                "issue-250-clear-race",
                CancellationToken.None);
            await Task.Delay(150).ConfigureAwait(false);
            clearTask.IsCompleted.Should().BeFalse(
                clearTask.Exception?.GetBaseException().ToString() ?? "the object lock must serialize the live DELETE");

            handler.Release();
            (await releaseTask.ConfigureAwait(false)).Should().BeTrue();
            await releaseDb.DisposeAsync().ConfigureAwait(false);
            releaseDb = null;
            Func<Task> rejected = () => clearTask;
            await rejected.Should().ThrowAsync<CentralClearReferenceException>()
                .WithMessage("clear-reference.artifact-ineligible").ConfigureAwait(false);
            (await clearDatabase.Context.CentralClearReferenceDesignations.CountAsync().ConfigureAwait(false))
                .Should().Be(0);
            (await clearDatabase.Context.CentralArtifacts.AsNoTracking()
                .SingleAsync(item => item.Id == clearSeed.Items[0].RecordId).ConfigureAwait(false))
                .ObjectState.Should().Be(CentralArtifactObjectState.Expired);
            await AssertIssue250ObjectAbsentAsync(rawMinio, clearSeed.Items[0].ObjectKey).ConfigureAwait(false);
        }
        finally
        {
            handler.Release();
            if (releaseDb is not null)
            {
                await releaseDb.DisposeAsync().ConfigureAwait(false);
            }
            if (clearSeed is not null)
            {
                await CleanupIssue250ObjectsAsync(rawMinio, clearSeed).ConfigureAwait(false);
            }
            await clearDatabase.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public void PayloadReleaseReservationComparison_RejectsEachIndependentMutation()
    {
        var token = Guid.NewGuid();
        var requestedAt = new DateTimeOffset(2026, 8, 2, 21, 0, 0, TimeSpan.Zero);
        byte[] itemRowVersion = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] targetRowVersion = [8, 7, 6, 5, 4, 3, 2, 1];
        const long generation = 7;
        const string storageReference = "minio://skymonitor-artifacts/issue-250/comparison.bin";
        var expected = new CentralTransientPayloadReservationExpected(
            token, requestedAt, itemRowVersion, targetRowVersion, generation, storageReference);
        var current = new CentralTransientPayloadReservationCurrent(
            CentralTransientPayloadReleaseItemOutcome.Pending,
            token,
            requestedAt,
            itemRowVersion,
            targetRowVersion,
            generation,
            storageReference,
            targetRowVersion,
            generation,
            storageReference);
        CentralTransientPayloadReservationComparison.Matches(current, expected).Should().BeTrue();

        var mutations = new (string Fence, CentralTransientPayloadReservationCurrent Current)[]
        {
            ("outcome", current with { Outcome = CentralTransientPayloadReleaseItemOutcome.Released }),
            ("token", current with { ReservationToken = Guid.NewGuid() }),
            ("requested-time", current with { RequestedAtUtc = requestedAt.AddTicks(1) }),
            ("item-rowversion", current with { ItemRowVersion = new byte[] { 0, 2, 3, 4, 5, 6, 7, 8 } }),
            ("reserved-target-rowversion", current with
            {
                ReservedTargetRowVersion = new byte[] { 0, 7, 6, 5, 4, 3, 2, 1 }
            }),
            ("reserved-generation", current with { ReservedTargetGeneration = generation + 1 }),
            ("reserved-storage-reference", current with
            {
                ReservedStorageReference = "minio://skymonitor-artifacts/issue-250/replacement.bin"
            }),
            ("target-rowversion", current with
            {
                CurrentTargetRowVersion = new byte[] { 0, 7, 6, 5, 4, 3, 2, 1 }
            }),
            ("generation", current with { CurrentTargetGeneration = generation + 1 }),
            ("storage-reference", current with
            {
                CurrentStorageReference = "minio://skymonitor-artifacts/issue-250/replacement.bin"
            })
        };
        foreach (var mutation in mutations)
        {
            CentralTransientPayloadReservationComparison.Matches(mutation.Current, expected)
                .Should().BeFalse($"the independent {mutation.Fence} fence changed");
        }
    }

    [TestMethod]
    public async Task PayloadReleaseCasFences_EachIndependentMutationRejectsTheStaleWorker()
    {
        var scenarios = new[]
        {
            (Fence: "token", Derivative: false),
            (Fence: "item-rowversion", Derivative: false),
            (Fence: "target-rowversion", Derivative: false),
            (Fence: "generation", Derivative: false),
            (Fence: "reference", Derivative: false),
            (Fence: "target-rowversion", Derivative: true)
        };
        var rawMinio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        foreach (var scenario in scenarios)
        {
            await using var database = CreateDatabase($"Issue250Cas{scenario.Fence}{scenario.Derivative}");
            Issue250ReleaseSeed? seed = null;
            Issue250ReleaseTarget? derivativeTarget = null;
            string? replacementKey = null;
            try
            {
                await database.Context.Database.MigrateAsync().ConfigureAwait(false);
                seed = await SeedIssue250ReleaseAsync(database.Context, rawMinio, 1).ConfigureAwait(false);
                derivativeTarget = scenario.Derivative
                    ? await AddIssue250DerivativeTargetAsync(database.Context, rawMinio, seed).ConfigureAwait(false)
                    : null;
                var target = derivativeTarget ?? seed.Items[0];
                var ordinal = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                    .Where(item => item.ReleaseId == seed.ReleaseId && item.RecordId == target.RecordId)
                    .Select(item => item.Ordinal).SingleAsync().ConfigureAwait(false);
                var changed = false;
                var fault = new Issue250StageFaultInjector(async (stage, releaseId, itemOrdinal, token) =>
                {
                    if (changed || stage != CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete ||
                        releaseId != seed.ReleaseId || itemOrdinal != ordinal)
                    {
                        return;
                    }
                    changed = true;
                    await using var mutationDb = CreateContext(database.ConnectionString);
                    switch (scenario.Fence)
                    {
                        case "token":
                            _ = await mutationDb.CentralTransientPayloadReleaseItems
                                .Where(item => item.ReleaseId == releaseId && item.Ordinal == itemOrdinal)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(
                                    item => item.ReservationToken, Guid.NewGuid()), token).ConfigureAwait(false);
                            break;
                        case "item-rowversion":
                            _ = await mutationDb.CentralTransientPayloadReleaseItems
                                .Where(item => item.ReleaseId == releaseId && item.Ordinal == itemOrdinal)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(
                                    item => item.RetryCount, item => item.RetryCount + 1), token).ConfigureAwait(false);
                            break;
                        case "target-rowversion" when scenario.Derivative:
                            _ = await mutationDb.CentralTransientDerivativeOutputIntents
                                .Where(item => item.Id == target.RecordId)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(
                                    item => item.StateReasonCode, "issue-250-target-rowversion"), token)
                                .ConfigureAwait(false);
                            break;
                        case "target-rowversion":
                            _ = await mutationDb.CentralArtifacts.Where(item => item.Id == target.RecordId)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(
                                    item => item.StateReasonCode, "issue-250-target-rowversion"), token)
                                .ConfigureAwait(false);
                            break;
                        case "generation":
                            _ = await mutationDb.CentralArtifacts.Where(item => item.Id == target.RecordId)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(
                                    item => item.RecoveryGeneration, item => item.RecoveryGeneration + 1), token)
                                .ConfigureAwait(false);
                            break;
                        case "reference":
                            replacementKey = $"issue-250/{Guid.NewGuid():N}/cas-replacement.bin";
                            await PutIssue250ObjectAsync(rawMinio, replacementKey, Issue250ReplacementPayload)
                                .ConfigureAwait(false);
                            var replacementReference = "minio://skymonitor-artifacts/" + replacementKey;
                            if (scenario.Derivative)
                            {
                                _ = await mutationDb.CentralTransientDerivativeOutputIntents
                                    .Where(item => item.Id == target.RecordId)
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                                        item => item.StorageReference, replacementReference), token)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                _ = await mutationDb.CentralArtifacts.Where(item => item.Id == target.RecordId)
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                                        item => item.StorageReference, replacementReference), token)
                                    .ConfigureAwait(false);
                            }
                            break;
                    }
                });

                var service = CreateIssue250Service(
                    database.Context,
                    rawMinio,
                    new MutableIssue250TimeProvider(seed.CreatedUtc),
                    fault);
                (await service.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
                changed.Should().BeTrue();
                database.Context.ChangeTracker.Clear();
                var stale = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                    .SingleAsync(item => item.ReleaseId == seed.ReleaseId && item.Ordinal == ordinal)
                    .ConfigureAwait(false);
                stale.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
                if (scenario.Fence is "token" or "item-rowversion")
                {
                    stale.ReservationToken.Should().NotBeNull();
                }
                else
                {
                    stale.ReservationToken.Should().BeNull();
                    stale.RetryCount.Should().Be(1);
                    stale.RetryAtUtc.Should().NotBeNull();
                }
                await AssertIssue250ObjectExistsAsync(rawMinio, target.ObjectKey).ConfigureAwait(false);
                if (replacementKey is not null)
                {
                    await AssertIssue250ObjectExistsAsync(rawMinio, replacementKey).ConfigureAwait(false);
                }
            }
            finally
            {
                if (seed is not null)
                {
                    await CleanupIssue250ObjectsAsync(rawMinio, seed).ConfigureAwait(false);
                }
                if (derivativeTarget is not null)
                {
                    await rawMinio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket("skymonitor-artifacts")
                        .WithObject(derivativeTarget.ObjectKey)).ConfigureAwait(false);
                }
                if (replacementKey is not null)
                {
                    await rawMinio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket("skymonitor-artifacts")
                        .WithObject(replacementKey)).ConfigureAwait(false);
                }
                await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    public void PayloadReleaseTelemetry_ItemSignalsAndSpanAreExactlyBounded()
    {
        var logs = new List<Issue250LogEntry>();
        using var logger = new Issue250TelemetryLogger(logs);
        var measurements = new List<Issue250MetricEntry>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CentralTransientLifecycleTelemetry.MeterName &&
                instrument.Name == "skymonitor.central.transient.retention.items")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new(instrument.Name, value, tags.ToArray())));
        meter.Start();
        Activity? stopped = null;
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CentralTransientLifecycleTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity
        };
        ActivitySource.AddActivityListener(activities);
        using var telemetry = new CentralTransientLifecycleTelemetry(logger);

        using (telemetry.Start("retention"))
        {
            telemetry.RecordRetentionItem("SourceArtifact", "reserved", 0);
            telemetry.RecordRetentionItem("Derivative", "retry", 2);
        }

        measurements.Should().HaveCount(2);
        measurements.Select(item => item.Value).Should().OnlyContain(value => value == 1);
        measurements[0].Tags.Should().BeEquivalentTo(new[]
        {
            new KeyValuePair<string, object?>("kind", "source"),
            new KeyValuePair<string, object?>("outcome", "reserved")
        });
        measurements[1].Tags.Should().BeEquivalentTo(new[]
        {
            new KeyValuePair<string, object?>("kind", "derivative"),
            new KeyValuePair<string, object?>("outcome", "retry")
        });
        logs.Should().HaveCount(2);
        logs.Should().OnlyContain(item => item.EventId == 2167 && item.Level == LogLevel.Information &&
            item.Fields.Keys.Order().SequenceEqual(Issue250TelemetryFieldNames));
        logs[0].Fields.Should().Contain(new KeyValuePair<string, object?>("Kind", "source"));
        logs[0].Fields.Should().Contain(new KeyValuePair<string, object?>("Outcome", "reserved"));
        logs[1].Fields.Should().Contain(new KeyValuePair<string, object?>("RetryCount", 2));
        stopped.Should().NotBeNull();
        stopped!.OperationName.Should().Be("central-transient.retention");
        stopped.Kind.Should().Be(ActivityKind.Internal);
        stopped.Tags.Should().BeEmpty();
    }

    private static CentralTransientPayloadReleaseService CreateIssue250Service(
        ApplicationDbContext db,
        IMinioClient minio,
        TimeProvider clock,
        ICentralTransientPayloadReleaseFaultInjector? fault = null,
        ICentralArtifactRetentionReferences? references = null)
        => new(
            db,
            references ?? new CentralArtifactRetentionReferences(db),
            minio,
            Issue250ReleaseOptions(),
            clock,
            faultInjector: fault);

    private static IOptions<CentralTransientPayloadReleaseOptions> Issue250ReleaseOptions(
        int maximumRetryCount = 5)
        => Options.Create(new CentralTransientPayloadReleaseOptions
        {
            Enabled = true,
            ReservationLeaseTimeout = TimeSpan.FromMinutes(1),
            InitialRetryDelay = TimeSpan.FromSeconds(2),
            MaximumRetryDelay = TimeSpan.FromSeconds(8),
            MaximumRetryCount = maximumRetryCount
        });

    private static async Task<Issue250ReleaseSeed> SeedIssue250ReleaseAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        int itemCount)
    {
        var fixture = CentralTransientPersistenceFixture.Create();
        var seeded = await SeedAsync(db, fixture).ConfigureAwait(false);
        _ = await new CentralTransientEventPersistence(db).AppendAsync(fixture.Request with
        {
            CentralDerivativeJobId = seeded.JobId
        }, CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var seededArtifactIds = seeded.Artifacts.Select(item => item.Id).ToArray();
        var eventId = await db.CentralTransientObservationSources.AsNoTracking()
            .Where(item => seededArtifactIds.Contains(item.CentralArtifactId))
            .Select(item => item.Observation!.CentralTransientEventId)
            .Distinct().SingleAsync().ConfigureAwait(false);
        var createdUtc = new DateTimeOffset(2026, 8, 2, 18, 0, 0, TimeSpan.Zero);
        var targets = seeded.Artifacts.OrderBy(item => item.Id).Take(itemCount)
            .Select(item => new Issue250ReleaseTarget(
                item.Id,
                item.StorageReference,
                item.StorageReference["minio://skymonitor-artifacts/".Length..],
                item.RecoveryGeneration,
                fixture.Payloads[item.ArtifactId]))
            .ToArray();
        var release = new CentralTransientPayloadRelease
        {
            CentralTransientEventId = eventId,
            ActorIdentity = "issue-250-test",
            IdempotencyKey = $"issue-250-{Guid.NewGuid():N}",
            CanonicalRequestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
                "central-transient-payload-release-v1",
                eventId.ToString("N"))))),
            State = CentralTransientPayloadReleaseState.Pending,
            CreatedUtc = createdUtc
        };
        for (var ordinal = 0; ordinal < targets.Length; ordinal++)
        {
            release.Items.Add(new CentralTransientPayloadReleaseItem
            {
                ReleaseId = release.ReleaseId,
                Ordinal = ordinal,
                Kind = CentralTransientPayloadReleaseItemKind.SourceArtifact,
                RecordId = targets[ordinal].RecordId
            });
        }
        db.CentralTransientPayloadReleases.Add(release);
        await db.SaveChangesAsync().ConfigureAwait(false);
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket("skymonitor-artifacts"))
                .ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket("skymonitor-artifacts"))
                .ConfigureAwait(false);
        }
        foreach (var target in targets)
        {
            await PutIssue250ObjectAsync(minio, target.ObjectKey, target.Payload).ConfigureAwait(false);
        }
        return new(release.ReleaseId, createdUtc, targets);
    }

    private static async Task PutIssue250ObjectAsync(IMinioClient minio, string key, byte[] payload)
    {
        await using var stream = new MemoryStream(payload, writable: false);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket("skymonitor-artifacts").WithObject(key)
            .WithStreamData(stream).WithObjectSize(payload.LongLength).WithContentType("application/octet-stream"))
            .ConfigureAwait(false);
    }

    private static async Task<Issue250PublicationSeed> SeedIssue250PublicationAsync(
        ApplicationDbContext db,
        IMinioClient minio)
    {
        var release = await SeedIssue250ReleaseAsync(db, minio, 1).ConfigureAwait(false);
        var owner = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = $"issue-250-owner-{Guid.NewGuid():N}",
            NormalizedUserName = $"ISSUE-250-OWNER-{Guid.NewGuid():N}"
        };
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = "Issue 250 publication authority",
            TimeZoneId = "UTC",
            CreatedAtUtc = release.CreatedUtc,
            IsActive = true
        };
        observatory.Memberships.Add(new ObservatoryMembership
        {
            UserId = owner.Id,
            User = owner,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = release.CreatedUtc
        });
        var artifact = await db.CentralArtifacts.Include(item => item.Frame)
            .SingleAsync(item => item.Id == release.Items[0].RecordId).ConfigureAwait(false);
        artifact.Role = FrameArtifactRole.Preview;
        artifact.MediaType = "image/png";
        artifact.Frame!.ObservatoryId = observatory.Id;
        db.Observatories.Add(observatory);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new(release, observatory.Id, owner.Id);
    }

    private static async Task<PublicRecordPublicationResult> PublishIssue250ArtifactAsync(
        string connectionString,
        Issue250PublicationSeed seed,
        string reasonCode)
    {
        await using var publicationDb = CreateContext(connectionString);
        return await new PublicRecordPublicationService(publicationDb, TimeProvider.System).DecideAsync(
            seed.ObservatoryId,
            seed.OwnerUserId,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, seed.Release.Items[0].RecordId),
            PublicationDecisionState.Released,
            "issue-250-v1",
            reasonCode,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<Issue250ReleaseTarget> AddIssue250DerivativeTargetAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        Issue250ReleaseSeed seed)
    {
        var release = await db.CentralTransientPayloadReleases.AsNoTracking()
            .SingleAsync(item => item.ReleaseId == seed.ReleaseId).ConfigureAwait(false);
        var sourceVersionId = await db.CentralTransientEventVersions.AsNoTracking()
            .Where(item => item.CentralTransientEventId == release.CentralTransientEventId)
            .OrderByDescending(item => item.Version).Select(item => item.EventVersionId)
            .FirstAsync().ConfigureAwait(false);
        var now = seed.CreatedUtc;
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = seed.Items[0].RecordId,
            TargetRole = FrameArtifactRole.Combined,
            TargetRecipeVersion = "issue-250-derivative-v1",
            TargetVariant = "issue-250-cas",
            RecipeName = "issue-250-cas",
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = new string('D', 64),
            ExpectedRecipeIdentitySha256 = new string('D', 64),
            RequestIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            Status = CentralDerivativeJobStatus.Completed,
            MaxAttempts = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = now
        };
        var derivativeJob = new CentralTransientDerivativeJob
        {
            CentralDerivativeJobId = job.Id,
            Job = job,
            CentralTransientEventId = release.CentralTransientEventId,
            SourceEventVersionId = sourceVersionId,
            RequestIdentitySha256 = job.RequestIdentitySha256,
            ProducerSchemaVersion = "transient-derivative-producer-v1",
            ProducerName = "issue-250",
            ProducerVersion = "issue-250-v1",
            RecipeIdentitySha256 = job.RequestedRecipeIdentitySha256,
            OptionsIdentitySha256 = new string('E', 64),
            CanonicalRequestJson = "{}",
            CanonicalRequestSha256 = ProcessingIdentity.ComputePayloadSha256("{}"u8.ToArray()),
            CanonicalRequestByteLength = 2,
            ExpectedOutputCount = 5,
            CreatedAtUtc = now
        };
        var payload = new byte[] { 5, 4, 3, 2, 1 };
        var objectKey = $"issue-250/{Guid.NewGuid():N}/cas-derivative.bin";
        var intent = new CentralTransientDerivativeOutputIntent
        {
            CentralDerivativeJobId = job.Id,
            DerivativeJob = derivativeJob,
            CentralTransientEventId = release.CentralTransientEventId,
            Kind = TransientDerivativeKind.Reconstruction,
            DerivativeId = Guid.NewGuid(),
            ArtifactId = Guid.NewGuid(),
            ArtifactRole = FrameArtifactRole.Combined,
            ArtifactVariant = "issue-250-cas",
            MediaType = "application/octet-stream",
            ByteLength = payload.Length,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload)),
            OutputIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            StorageReference = "minio://skymonitor-artifacts/" + objectKey,
            StorageETag = "issue-250",
            ObjectState = CentralArtifactObjectState.Available,
            CreatedAtUtc = now,
            ObjectVerifiedAtUtc = now
        };
        derivativeJob.OutputIntents.Add(intent);
        db.CentralDerivativeJobs.Add(job);
        db.CentralTransientDerivativeJobs.Add(derivativeJob);
        var ordinal = await db.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.ReleaseId == seed.ReleaseId).MaxAsync(item => item.Ordinal).ConfigureAwait(false) + 1;
        db.CentralTransientPayloadReleaseItems.Add(new CentralTransientPayloadReleaseItem
        {
            ReleaseId = seed.ReleaseId,
            Ordinal = ordinal,
            Kind = CentralTransientPayloadReleaseItemKind.Derivative,
            RecordId = intent.Id
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        await PutIssue250ObjectAsync(minio, objectKey, payload).ConfigureAwait(false);
        return new(intent.Id, intent.StorageReference, objectKey, 0, payload);
    }

    private static async Task AssertIssue250ObjectExistsAsync(IMinioClient minio, string key)
        => Assert.IsNotNull(await minio.StatObjectAsync(new StatObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(key)).ConfigureAwait(false));

    private static async Task AssertIssue250ObjectAbsentAsync(IMinioClient minio, string key)
    {
        Func<Task> stat = () => minio.StatObjectAsync(new StatObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(key));
        await stat.Should().ThrowAsync<Minio.Exceptions.ObjectNotFoundException>().ConfigureAwait(false);
    }

    private static async Task CleanupIssue250ObjectsAsync(IMinioClient minio, Issue250ReleaseSeed seed)
    {
        foreach (var target in seed.Items)
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket("skymonitor-artifacts")
                .WithObject(target.ObjectKey)).ConfigureAwait(false);
        }
    }

    private static IMinioClient CreateIssue250Minio(HttpMessageHandler handler)
        => new MinioClient().WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
            .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
            .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
            .Build();

    private sealed class MutableIssue250TimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset utcNow = now;
        public override DateTimeOffset GetUtcNow() => utcNow;
        internal void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class Issue250StageFaultInjector(
        Func<CentralTransientPayloadReleaseFaultStage, Guid, int, CancellationToken, Task> callback)
        : ICentralTransientPayloadReleaseFaultInjector
    {
        public Task OnStageAsync(
            CentralTransientPayloadReleaseFaultStage stage,
            Guid releaseId,
            int ordinal,
            CancellationToken cancellationToken)
            => callback(stage, releaseId, ordinal, cancellationToken);
    }

    private sealed class Issue250SwitchableRetentionReferences(ICentralArtifactRetentionReferences inner)
        : ICentralArtifactRetentionReferences
    {
        private readonly HashSet<Guid> held = [];

        internal void Hold(Guid artifactId) => held.Add(artifactId);
        internal void Clear() => held.Clear();

        public Task<bool> IsHeldAsync(Guid centralArtifactId, CancellationToken cancellationToken)
            => held.Contains(centralArtifactId)
                ? Task.FromResult(true)
                : inner.IsHeldAsync(centralArtifactId, cancellationToken);

        public Task<bool> IsHeldOutsideTransientEventAsync(
            Guid centralArtifactId,
            Guid centralTransientEventId,
            CancellationToken cancellationToken)
            => held.Contains(centralArtifactId)
                ? Task.FromResult(true)
                : inner.IsHeldOutsideTransientEventAsync(
                    centralArtifactId, centralTransientEventId, cancellationToken);
    }

    private sealed class Issue250FailFirstDeleteHandler : DelegatingHandler
    {
        private int deleteAttempts;
        internal int DeleteAttempts => Volatile.Read(ref deleteAttempts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete && Interlocked.Increment(ref deleteAttempts) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("<Error><Code>ServiceUnavailable</Code><Message>retry</Message><RequestId>bounded</RequestId><HostId>bounded</HostId></Error>")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Issue250AlwaysFailDeleteHandler : DelegatingHandler
    {
        private int deletes;
        internal int DeleteAttempts => Volatile.Read(ref deletes);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref deletes);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        "<Error><Code>ServiceUnavailable</Code><Message>retry</Message></Error>")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Issue250UnknownDeleteOutcomeHandler : DelegatingHandler
    {
        private int armed;

        internal void Arm() => Volatile.Write(ref armed, 1);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref armed) == 1 && request.Method is { } method &&
                (method == HttpMethod.Delete || method == HttpMethod.Head))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        "<Error><Code>ServiceUnavailable</Code><Message>outcome unknown</Message></Error>")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Issue250LoseFirstDeleteResponseHandler : DelegatingHandler
    {
        private int deletes;
        internal int DeleteAttempts => Volatile.Read(ref deletes);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Delete)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            Interlocked.Increment(ref deletes);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException("issue-250-delete-response-lost");
        }
    }

    private sealed class Issue250DelayFirstDeleteHandler : DelegatingHandler
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int deletes;
        private int delayed;

        internal Task Entered => entered.Task;
        internal int DeleteAttempts => Volatile.Read(ref deletes);
        internal void Release() => release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref deletes);
                if (Interlocked.CompareExchange(ref delayed, 1, 0) == 0)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Issue250TelemetryLogger(List<Issue250LogEntry> entries)
        : ILogger<CentralTransientLifecycleTelemetry>, IDisposable
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose()
        {
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(item => item.Key != "{OriginalFormat}")
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                : [];
            entries.Add(new(eventId.Id, logLevel, fields));
        }
    }

    private sealed record Issue250MetricEntry(
        string Instrument,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private sealed record Issue250LogEntry(
        int EventId,
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Fields);

    private sealed record Issue250ReleaseSeed(
        Guid ReleaseId,
        DateTimeOffset CreatedUtc,
        IReadOnlyList<Issue250ReleaseTarget> Items);

    private sealed record Issue250PublicationSeed(
        Issue250ReleaseSeed Release,
        Guid ObservatoryId,
        string OwnerUserId);

    private sealed record Issue250ReleaseTarget(
        Guid RecordId,
        string StorageReference,
        string ObjectKey,
        long Generation,
        byte[] Payload);
}
