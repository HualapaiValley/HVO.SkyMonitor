using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralRecoveryIntegrationTests
{
    private const string Bucket = "skymonitor-artifacts";
    private const string StoragePrefix = "minio://skymonitor-artifacts/";
    private readonly HashSet<string> objectKeys = new(StringComparer.Ordinal);
    private readonly string agentMarker = $"central-recovery-test-{Guid.NewGuid():N}";

    [TestInitialize]
    public async Task InitializeAsync()
    {
        var minio = GetMinio();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }
        await ResetCheckpointAsync().ConfigureAwait(false);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dispositionKeys = await db.CentralObjectRecoveryDispositions.AsNoTracking()
                .Select(item => new { item.SourceObjectKey, item.TargetObjectKey })
                .ToListAsync().ConfigureAwait(false);
            foreach (var item in dispositionKeys)
            {
                objectKeys.Add(item.SourceObjectKey);
                if (item.TargetObjectKey is not null)
                {
                    objectKeys.Add(item.TargetObjectKey);
                }
            }
            await db.CentralObjectRecoveryDispositions.ExecuteDeleteAsync().ConfigureAwait(false);
            var derivativeJobIds = db.CentralDerivativeJobs
                .Where(item => item.SourceArtifact!.Frame!.AgentId == agentMarker)
                .Select(item => item.Id);
            var eventIds = db.CentralTransientEvents.Where(item => item.AgentId == agentMarker).Select(item => item.Id);
            await using (var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DISABLE TRIGGER [TR_CentralTransientDerivativeOutputIntents_TerminalImmutable] ON [CentralTransientDerivativeOutputIntents]")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "DISABLE TRIGGER [TR_CentralTransientDerivativeJobs_CommittedImmutable] ON [CentralTransientDerivativeJobs]")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "DISABLE TRIGGER [TR_CentralTransientEventVersions_Immutable] ON [CentralTransientEventVersions]")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "DISABLE TRIGGER [TR_CentralTransientEvents_Immutable] ON [CentralTransientEvents]")
                    .ConfigureAwait(false);
                await db.CentralTransientDerivativeOutputIntents
                    .Where(item => derivativeJobIds.Contains(item.CentralDerivativeJobId))
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await db.CentralTransientDerivativeJobs
                    .Where(item => derivativeJobIds.Contains(item.CentralDerivativeJobId))
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await db.CentralDerivativeJobs.Where(item => derivativeJobIds.Contains(item.Id))
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await db.CentralTransientEventVersions.Where(item => eventIds.Contains(item.CentralTransientEventId))
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await db.CentralTransientEvents.Where(item => eventIds.Contains(item.Id)).ExecuteDeleteAsync()
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "ENABLE TRIGGER [TR_CentralTransientEvents_Immutable] ON [CentralTransientEvents]")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "ENABLE TRIGGER [TR_CentralTransientEventVersions_Immutable] ON [CentralTransientEventVersions]")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "ENABLE TRIGGER [TR_CentralTransientDerivativeJobs_CommittedImmutable] ON [CentralTransientDerivativeJobs]")
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "ENABLE TRIGGER [TR_CentralTransientDerivativeOutputIntents_TerminalImmutable] ON [CentralTransientDerivativeOutputIntents]")
                    .ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            await db.CentralFrames.Where(frame => frame.AgentId == agentMarker).ExecuteDeleteAsync().ConfigureAwait(false);
            var registrationIds = await db.DeviceRegistrations.Where(item => item.DeviceId == agentMarker)
                .Select(item => item.Id).ToListAsync().ConfigureAwait(false);
            var observatoryIds = await db.DeviceRegistrations.Where(item => item.DeviceId == agentMarker)
                .Select(item => item.ObservatoryId).ToListAsync().ConfigureAwait(false);
            await db.DeviceRigProfiles.Where(item => registrationIds.Contains(item.RegistrationId)).ExecuteDeleteAsync()
                .ConfigureAwait(false);
            await db.DeviceRegistrations.Where(item => registrationIds.Contains(item.Id)).ExecuteDeleteAsync()
                .ConfigureAwait(false);
            await db.Observatories.Where(item => observatoryIds.Contains(item.Id)).ExecuteDeleteAsync().ConfigureAwait(false);
        }
        var minio = GetMinio();
        await Parallel.ForEachAsync(objectKeys, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (key, cancellationToken) =>
            {
                try
                {
                    await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(key), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
                {
                }
            }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SqlInventory_VerifiesEachOwnedObjectOnceAndNeverSchedulesMissingOrCorruptContent()
    {
        const long generation = 7;
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var scheduler = new RecordingScheduler();
        await ExcludeUnownedFromGenerationAsync(generation).ConfigureAwait(false);
        var availableMissing = await AddArtifactAsync("artifacts/10/missing.bin", [1, 2, 3, 4],
            CentralArtifactObjectState.Available, CentralReconstructionState.Complete, putObject: false).ConfigureAwait(false);
        var availableCorrupt = await AddArtifactAsync("artifacts/10/corrupt.bin", [1, 2, 3, 4],
            CentralArtifactObjectState.Available, CentralReconstructionState.Complete, objectPayload: [4, 3, 2, 1]).ConfigureAwait(false);
        var pendingMissing = await AddArtifactAsync("artifacts/10/pending.bin", [1, 2, 3, 4],
            CentralArtifactObjectState.Pending, CentralReconstructionState.Complete, putObject: false).ConfigureAwait(false);
        var referenceMissing = await AddArtifactAsync("derivatives/10/reference.bin", [1, 2, 3, 4],
            CentralArtifactObjectState.Available, CentralReconstructionState.PendingReference, putObject: false).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.SqlArtifacts, generation: generation).ConfigureAwait(false);
        using var services = CreateServices(scheduler);

        await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.SingleAsync(item => item.Id == availableMissing).ConfigureAwait(false)).Should()
                .Match<CentralArtifact>(item => item.ObjectState == CentralArtifactObjectState.Pending
                    && item.StateReasonCode == "object.missing" && item.RecoveryGeneration == 7);
            (await db.CentralArtifacts.SingleAsync(item => item.Id == availableCorrupt).ConfigureAwait(false)).Should()
                .Match<CentralArtifact>(item => item.ObjectState == CentralArtifactObjectState.Quarantined
                    && item.ReconstructionState == CentralReconstructionState.Quarantined
                    && item.StateReasonCode == "object.checksum-mismatch" && item.RecoveryGeneration == 7);
            (await db.CentralArtifacts.SingleAsync(item => item.Id == pendingMissing).ConfigureAwait(false)).StateReasonCode
                .Should().Be("object.missing");
            (await db.CentralArtifacts.SingleAsync(item => item.Id == referenceMissing).ConfigureAwait(false)).ObjectState
                .Should().Be(CentralArtifactObjectState.Pending);
        }
        scheduler.ArtifactIds.Should().NotContain([availableMissing, availableCorrupt, pendingMissing, referenceMissing],
            "object verification must precede derivative scheduling for every owned test artifact");

        await PutObjectAsync("artifacts/10/pending.bin", [1, 2, 3, 4]).ConfigureAwait(false);
        await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.SingleAsync(item => item.Id == pendingMissing).ConfigureAwait(false)).ObjectState
                .Should().Be(CentralArtifactObjectState.Pending, "a known missing object is not rehashed every 30 seconds");
        }
        clock.UtcNow += CentralArtifactReconciliationService.VerificationInterval + TimeSpan.FromSeconds(1);
        await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.SingleAsync(item => item.Id == pendingMissing).ConfigureAwait(false)).ObjectState
                .Should().Be(CentralArtifactObjectState.Available);
        }
        scheduler.ArtifactIds.Should().Contain(pendingMissing);
    }

    [TestMethod]
    public async Task SqlInventory_ExactBatchAdvancesGenerationAndLeavesLegacyReferencesCompatible()
    {
        const long generation = 19;
        await ExcludeUnownedFromGenerationAsync(generation).ConfigureAwait(false);
        for (var index = 0; index < CentralArtifactReconciliationService.MaximumSqlInventoryArtifactsPerCycle; index++)
        {
            var key = $"artifacts/20/{index:D3}.bin";
            await AddArtifactAsync(key, [(byte)index], CentralArtifactObjectState.Available,
                CentralReconstructionState.LegacyIncomplete).ConfigureAwait(false);
        }
        var legacyId = await AddArtifactAsync("legacy/not-owned.bin", [7], CentralArtifactObjectState.Available,
            CentralReconstructionState.LegacyIncomplete, storageReference: "minio://legacy-bucket/not-owned.bin").ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.SqlArtifacts, generation: generation).ConfigureAwait(false);

        await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, TimeProvider.System)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checkpoint = await db.CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
        checkpoint.Phase.Should().Be(CentralRecoveryPhases.MinioArtifacts,
            "an exact SQL page is complete without an empty follow-up query");
        (await db.CentralArtifacts.CountAsync(item => item.Frame!.AgentId == agentMarker
                && item.RecoveryGeneration == generation).ConfigureAwait(false))
            .Should().Be(CentralArtifactReconciliationService.MaximumSqlInventoryArtifactsPerCycle);
        (await db.CentralArtifacts.SingleAsync(item => item.Id == legacyId).ConfigureAwait(false)).RecoveryGeneration
            .Should().Be(0);
    }

    [TestMethod]
    public async Task SqlInventory_RestartContinuesGenerationBeyondOneBatch()
    {
        const long generation = 23;
        await ExcludeUnownedFromGenerationAsync(generation).ConfigureAwait(false);
        var ids = new List<Guid>();
        for (var index = 0; index <= CentralArtifactReconciliationService.MaximumSqlInventoryArtifactsPerCycle; index++)
        {
            ids.Add(await AddArtifactAsync($"artifacts/25/{index:D3}.bin", [(byte)index],
                CentralArtifactObjectState.Available, CentralReconstructionState.LegacyIncomplete).ConfigureAwait(false));
        }
        await SetCheckpointAsync(CentralRecoveryPhases.SqlArtifacts, generation: generation).ConfigureAwait(false);

        var started = Stopwatch.GetTimestamp();
        var firstImmediate = await RunFreshCycleAsync().ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.CountAsync(item => ids.Contains(item.Id) && item.RecoveryGeneration == generation)
                .ConfigureAwait(false)).Should().Be(CentralArtifactReconciliationService.MaximumSqlInventoryArtifactsPerCycle);
            (await db.CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false)).Phase
                .Should().Be(CentralRecoveryPhases.SqlArtifacts);
        }

        var secondImmediate = await RunFreshCycleAsync().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.CountAsync(item => ids.Contains(item.Id) && item.RecoveryGeneration == generation)
                .ConfigureAwait(false)).Should().Be(ids.Count);
            (await db.CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false)).Phase
                .Should().Be(CentralRecoveryPhases.MinioArtifacts);
        }
        firstImmediate.Should().BeTrue();
        secondImmediate.Should().BeTrue();
        (ids.Count / elapsed.TotalSeconds).Should().BeGreaterThan(1);
    }

    [TestMethod]
    [DataRow("artifacts")]
    [DataRow("derivatives")]
    public async Task MinioOnlyObject_QuarantineIsChecksumVerifiedCopyThenDeleteAcrossRestarts(string objectNamespace)
    {
        var payload = new byte[] { 9, 8, 7, 6 };
        var key = $"{objectNamespace}/30/{Guid.NewGuid():N}.bin";
        await PutObjectAsync(key, payload).ConfigureAwait(false);
        await SetCheckpointAsync(objectNamespace == "artifacts"
            ? CentralRecoveryPhases.MinioArtifacts
            : CentralRecoveryPhases.MinioDerivatives, partition: 0x30).ConfigureAwait(false);

        await RunFreshCycleAsync().ConfigureAwait(false);
        var pendingCopy = await ReadDispositionAsync(key).ConfigureAwait(false);
        pendingCopy.State.Should().Be(CentralObjectRecoveryStates.PendingCopy);
        pendingCopy.TargetObjectKey.Should().NotContain(key);

        await RunFreshCycleAsync().ConfigureAwait(false);
        var pendingDelete = await ReadDispositionAsync(key).ConfigureAwait(false);
        pendingDelete.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        pendingDelete.ContentChecksumSha256.Should().Be(Convert.ToHexString(SHA256.HashData(payload)));
        (await ReadObjectAsync(pendingDelete.TargetObjectKey!).ConfigureAwait(false)).Should().Equal(payload);
        (await ReadObjectAsync(key).ConfigureAwait(false)).Should().Equal(payload);

        await RunFreshCycleAsync().ConfigureAwait(false);
        (await ReadDispositionAsync(key).ConfigureAwait(false)).State.Should().Be(CentralObjectRecoveryStates.Completed);
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeFalse();
        (await ReadObjectAsync(pendingDelete.TargetObjectKey!).ConfigureAwait(false)).Should().Equal(payload);
    }

    [TestMethod]
    public async Task OrphanChangedBetweenCopyAndDelete_IsCopiedToANewTargetBeforeDeletion()
    {
        var key = $"artifacts/35/{Guid.NewGuid():N}.bin";
        await PutObjectAsync(key, [1, 2, 3]).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x35).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        var firstCopy = await ReadDispositionAsync(key).ConfigureAwait(false);
        firstCopy.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        objectKeys.Add(firstCopy.TargetObjectKey!);

        await PutObjectAsync(key, [3, 2, 1]).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        var recopy = await ReadDispositionAsync(key).ConfigureAwait(false);

        recopy.State.Should().Be(CentralObjectRecoveryStates.PendingCopy);
        recopy.TargetObjectKey.Should().NotBe(firstCopy.TargetObjectKey);
        (await ReadObjectAsync(firstCopy.TargetObjectKey!).ConfigureAwait(false)).Should().Equal(1, 2, 3);
        (await ReadObjectAsync(key).ConfigureAwait(false)).Should().Equal(3, 2, 1);
    }

    [TestMethod]
    public async Task OrphanDisposition_IsCancelledWhenIngestClaimsExactKeyBeforeCopy()
    {
        var payload = new byte[] { 4, 2 };
        var key = $"artifacts/36/{Guid.NewGuid():N}.bin";
        await PutObjectAsync(key, payload).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x36).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        var pending = await ReadDispositionAsync(key).ConfigureAwait(false);
        pending.State.Should().Be(CentralObjectRecoveryStates.PendingCopy);

        _ = await AddArtifactAsync(key, payload, CentralArtifactObjectState.Available,
            CentralReconstructionState.Complete, putObject: false).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);

        var cancelled = await ReadDispositionAsync(key).ConfigureAwait(false);
        cancelled.State.Should().Be(CentralObjectRecoveryStates.Cancelled);
        cancelled.ReasonCode.Should().Be("ownership.active");
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeTrue();
        (await ObjectExistsAsync(cancelled.TargetObjectKey!).ConfigureAwait(false)).Should().BeFalse();
    }

    [TestMethod]
    public async Task OrphanDisposition_IsCancelledWhenIngestClaimsExactKeyAfterCopyBeforeDelete()
    {
        var payload = new byte[] { 4, 3 };
        var key = $"artifacts/39/{Guid.NewGuid():N}.bin";
        await PutObjectAsync(key, payload).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x39).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        var copied = await ReadDispositionAsync(key).ConfigureAwait(false);
        copied.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);

        _ = await AddArtifactAsync(key, payload, CentralArtifactObjectState.Available,
            CentralReconstructionState.Complete, putObject: false).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);

        var cancelled = await ReadDispositionAsync(key).ConfigureAwait(false);
        cancelled.State.Should().Be(CentralObjectRecoveryStates.Cancelled);
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeTrue();
        (await ReadObjectAsync(copied.TargetObjectKey!).ConfigureAwait(false)).Should().Equal(payload);
    }

    [TestMethod]
    public async Task ExpiredDisposition_IsCancelledWhenArtifactReactivatesBeforeDelete()
    {
        var key = $"artifacts/37/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(key, [3, 7], CentralArtifactObjectState.Expired,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x37).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        (await ReadDispositionAsync(key).ConfigureAwait(false)).State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
                .Where(item => item.Id == artifactId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ObjectState,
                    CentralArtifactObjectState.Available)).ConfigureAwait(false);
        }

        await RunFreshCycleAsync().ConfigureAwait(false);

        var cancelled = await ReadDispositionAsync(key).ConfigureAwait(false);
        cancelled.State.Should().Be(CentralObjectRecoveryStates.Cancelled);
        cancelled.Kind.Should().Be(CentralObjectRecoveryKinds.ExpiredDelete);
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeTrue();
    }

    [TestMethod]
    public async Task ExpiredDisposition_BecomesOrphanQuarantineWhenSqlOwnerDisappears()
    {
        var key = $"artifacts/38/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(key, [3, 8], CentralArtifactObjectState.Expired,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x38).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralArtifacts.Where(item => item.Id == artifactId).ExecuteDeleteAsync().ConfigureAwait(false);
        }

        await RunFreshCycleAsync().ConfigureAwait(false);

        var reclassified = await ReadDispositionAsync(key).ConfigureAwait(false);
        reclassified.Kind.Should().Be(CentralObjectRecoveryKinds.OrphanQuarantine);
        reclassified.State.Should().Be(CentralObjectRecoveryStates.PendingCopy);
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeTrue();
    }

    [TestMethod]
    public async Task CompletedOrphanThatReappears_ReopensWithNewTargetWithoutDuplicateDisposition()
    {
        var key = $"artifacts/40/{Guid.NewGuid():N}.bin";
        await PutObjectAsync(key, [1]).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x40).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        var first = await ReadDispositionAsync(key).ConfigureAwait(false);
        first.State.Should().Be(CentralObjectRecoveryStates.Completed);
        var firstTarget = first.TargetObjectKey;

        await PutObjectAsync(key, [2]).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x40).ConfigureAwait(false);
        await RunFreshCycleAsync().ConfigureAwait(false);
        var reopened = await ReadDispositionAsync(key).ConfigureAwait(false);

        reopened.Id.Should().Be(first.Id);
        reopened.State.Should().Be(CentralObjectRecoveryStates.PendingCopy);
        reopened.TargetObjectKey.Should().NotBe(firstTarget);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralObjectRecoveryDispositions.CountAsync(item => item.SourceObjectKey == key).ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task ExpiredSqlObject_IsDeletedThroughDurableDisposition()
    {
        var key = $"artifacts/50/{Guid.NewGuid():N}.bin";
        await AddArtifactAsync(key, [5, 4, 3], CentralArtifactObjectState.Expired,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x50).ConfigureAwait(false);

        await RunFreshCycleAsync().ConfigureAwait(false);
        (await ReadDispositionAsync(key).ConfigureAwait(false)).Kind.Should().Be(CentralObjectRecoveryKinds.ExpiredDelete);
        await RunFreshCycleAsync().ConfigureAwait(false);

        var adopted = await ReadDispositionAsync(key).ConfigureAwait(false);
        adopted.OperationToken.Should().NotBeNull();
        adopted.CentralArtifactId.Should().NotBeNull();
        adopted.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeTrue(
            "legacy recovery adopts durable retention state before performing object I/O");
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionProcessor>()
                .ProcessAsync(adopted.Id, "worker", CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionProcessResult.Released);
        }

        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeFalse();
        (await ReadDispositionAsync(key).ConfigureAwait(false)).State.Should().Be(CentralObjectRecoveryStates.Completed);
    }

    [TestMethod]
    public async Task CompletedTokenizedTombstone_ReappearedBytesReopenAndDeleteWithSameToken()
    {
        var key = $"artifacts/51/{Guid.NewGuid():N}.bin";
        byte[] payload = [5, 1, 5, 1];
        var artifactId = await AddArtifactAsync(
            key, payload, CentralArtifactObjectState.Available, CentralReconstructionState.Complete)
            .ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                .ReleaseAsync(artifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Released);
        }
        var completed = await ReadDispositionAsync(key).ConfigureAwait(false);
        completed.OperationToken.Should().NotBeNull();
        completed.State.Should().Be(CentralObjectRecoveryStates.Completed);
        await PutObjectAsync(key, payload).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x51).ConfigureAwait(false);

        await RunFreshCycleAsync().ConfigureAwait(false);

        var reopened = await ReadDispositionAsync(key).ConfigureAwait(false);
        reopened.Id.Should().Be(completed.Id);
        reopened.OperationToken.Should().Be(completed.OperationToken);
        reopened.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        reopened.CompletedAtUtc.Should().BeNull();
        var reopenedArtifact = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        reopenedArtifact.RetentionDeletionToken.Should().Be(completed.OperationToken);
        reopenedArtifact.RetentionDeletionCompletedAtUtc.Should().BeNull();
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeTrue();

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionProcessor>()
                .ProcessAsync(reopened.Id, "worker", CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionProcessResult.Released);
        }
        (await ObjectExistsAsync(key).ConfigureAwait(false)).Should().BeFalse();
        var finalized = await ReadDispositionAsync(key).ConfigureAwait(false);
        finalized.Id.Should().Be(completed.Id);
        finalized.OperationToken.Should().Be(completed.OperationToken);
        finalized.State.Should().Be(CentralObjectRecoveryStates.Completed);
        finalized.AttemptCount.Should().BeGreaterThan(completed.AttemptCount);
    }

    [TestMethod]
    public async Task ExpiredTransientIntentOnlyOwner_DeletesLegacyObjectWithoutReopenLoop()
    {
        var sourceId = await AddArtifactAsync(
            $"artifacts/52/{Guid.NewGuid():N}-source.bin",
            [5, 2],
            CentralArtifactObjectState.Available,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        var objectKey = $"derivatives/52/{Guid.NewGuid():N}.bin";
        byte[] payload = [5, 2, 5, 2];
        await PutObjectAsync(objectKey, payload).ConfigureAwait(false);
        Guid dispositionId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTimeOffset.UtcNow;
            var eventRecord = new CentralTransientEventRecord
            {
                AgentId = agentMarker,
                EventId = Guid.NewGuid(),
                EventCreatedUtc = now
            };
            var eventVersion = new CentralTransientEventVersionRecord
            {
                EventVersionId = Guid.NewGuid(),
                CentralTransientEventId = eventRecord.Id,
                Version = 1,
                State = TransientEventState.Validated,
                VersionCreatedUtc = now,
                FirstObservedUtc = now.AddSeconds(-1),
                LastObservedUtc = now,
                SchemaVersion = TransientEventV1.CurrentSchemaVersion,
                CanonicalEventJson = "{}",
                CanonicalEventSha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                CanonicalEventByteLength = 2
            };
            eventRecord.Versions.Add(eventVersion);
            var job = new CentralDerivativeJob
            {
                SourceCentralArtifactId = sourceId,
                TargetRole = FrameArtifactRole.Metadata,
                TargetRecipeVersion = "expired-transient-v1",
                TargetVariant = "expired-transient",
                RecipeName = "expired-transient",
                RecipeOptionsJson = "{}",
                InputSelectorJson = "{}",
                RequestedRecipeIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                RequestIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                Status = CentralDerivativeJobStatus.Completed,
                AttemptCount = 1,
                MaxAttempts = 3,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = now
            };
            var derivativeJob = new CentralTransientDerivativeJob
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Event = eventRecord,
                CentralTransientEventId = eventRecord.Id,
                SourceEventVersion = eventVersion,
                SourceEventVersionId = eventVersion.EventVersionId,
                RequestIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ProducerSchemaVersion = "expired-transient-v1",
                ProducerName = "integration",
                ProducerVersion = "v1",
                RecipeIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                OptionsIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                CanonicalRequestJson = "{}",
                CanonicalRequestSha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                CanonicalRequestByteLength = 2,
                ExpectedOutputCount = 5,
                CreatedAtUtc = now
            };
            derivativeJob.OutputIntents.Add(new CentralTransientDerivativeOutputIntent
            {
                CentralDerivativeJobId = job.Id,
                CentralTransientEventId = eventRecord.Id,
                Kind = TransientDerivativeKind.Preview,
                DerivativeId = Guid.NewGuid(),
                ArtifactId = Guid.NewGuid(),
                ArtifactRole = FrameArtifactRole.Preview,
                ArtifactVariant = "partial-preview",
                MediaType = "image/png",
                ByteLength = payload.LongLength,
                ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload)),
                OutputIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                StorageReference = StoragePrefix + objectKey,
                ObjectState = CentralArtifactObjectState.Expired,
                StateReasonCode = "retention.publication-rejected",
                CreatedAtUtc = now
            });
            var disposition = new CentralObjectRecoveryDisposition
            {
                SourceObjectIdentitySha256 = CentralArtifactReconciliationService.CreateObjectKeyIdentity(objectKey),
                SourceObjectKey = objectKey,
                Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                State = CentralObjectRecoveryStates.PendingDelete,
                ByteLength = payload.LongLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.AddRange(eventRecord, job, derivativeJob, disposition);
            await db.SaveChangesAsync().ConfigureAwait(false);
            dispositionId = disposition.Id;
        }
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: DateTimeOffset.UtcNow.AddDays(1))
            .ConfigureAwait(false);

        await RunFreshCycleAsync().ConfigureAwait(false);

        (await ObjectExistsAsync(objectKey).ConfigureAwait(false)).Should().BeFalse();
        var completed = await ReadDispositionAsync(objectKey).ConfigureAwait(false);
        completed.Id.Should().Be(dispositionId);
        completed.State.Should().Be(CentralObjectRecoveryStates.Completed);
        completed.OperationToken.Should().BeNull();
        var completedAt = completed.UpdatedAtUtc;
        await RunFreshCycleAsync().ConfigureAwait(false);
        var replay = await ReadDispositionAsync(objectKey).ConfigureAwait(false);
        replay.State.Should().Be(CentralObjectRecoveryStates.Completed);
        replay.UpdatedAtUtc.Should().Be(completedAt);
    }

    [TestMethod]
    public async Task MinioCursor_ResumesInsideBoundedPrefixAndExactPageAdvancesPartition()
    {
        const int partition = 0x60;
        var payload = new byte[] { 6 };
        var count = CentralArtifactReconciliationService.MaximumMinioInventoryObjectsPerCycle + 1;
        for (var index = 0; index < count; index++)
        {
            var key = $"artifacts/60/{index:D3}.bin";
            await AddArtifactAsync(key, payload, CentralArtifactObjectState.Available,
                CentralReconstructionState.LegacyIncomplete).ConfigureAwait(false);
        }
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: partition).ConfigureAwait(false);

        await RunFreshCycleAsync().ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var checkpoint = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
            checkpoint.ObjectPartition.Should().Be(partition);
            checkpoint.ObjectCursor.Should().Be("artifacts/60/099.bin");
        }
        await RunFreshCycleAsync().ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var checkpoint = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
            checkpoint.ObjectPartition.Should().Be(partition + 1);
            checkpoint.ObjectCursor.Should().BeNull();
        }
    }

    [TestMethod]
    public async Task CatchAllInventory_PreservesCaseDistinctOwnershipAndSupportsLongMalformedKeys()
    {
        var device = $"ab{new string('1', 30)}";
        var lowerKey = $"artifacts/{device}/owned.bin";
        var upperKey = $"artifacts/{device.ToUpperInvariant()}/owned.bin";
        var malformedKey = $"artifacts/not-hex/{Guid.NewGuid():N}.bin";
        var longKey = $"artifacts/not-hex/{new string('x', 500)}.bin";
        await AddArtifactAsync(lowerKey, [1], CentralArtifactObjectState.Available,
            CentralReconstructionState.LegacyIncomplete).ConfigureAwait(false);
        await PutObjectAsync(upperKey, [2]).ConfigureAwait(false);
        await PutObjectAsync(malformedKey, [3]).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifactsCatchAll).ConfigureAwait(false);

        var immediate = await RunFreshCycleAsync().ConfigureAwait(false);

        immediate.Should().BeTrue("the completed linear catch-all pass advances directly to partitioned derivative work");
        var upper = await ReadDispositionAsync(upperKey).ConfigureAwait(false);
        var malformed = await ReadDispositionAsync(malformedKey).ConfigureAwait(false);
        upper.SourceObjectIdentitySha256.Should().NotBe(malformed.SourceObjectIdentitySha256);
        upper.SourceObjectKey.Should().Be(upperKey);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checkpoint = await db.CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
        checkpoint.Phase.Should().Be(CentralRecoveryPhases.MinioDerivatives);
        checkpoint.ObjectCursor.Should().BeNull();
        (await db
            .CentralObjectRecoveryDispositions.CountAsync(item =>
                item.SourceObjectIdentitySha256 == CentralArtifactReconciliationService.CreateObjectKeyIdentity(lowerKey))
            .ConfigureAwait(false)).Should().Be(0, "case-distinct SQL ownership must not own the uppercase key");
        db.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            SourceObjectIdentitySha256 = CentralArtifactReconciliationService.CreateObjectKeyIdentity(longKey),
            SourceObjectKey = longKey,
            Kind = CentralObjectRecoveryKinds.OrphanQuarantine,
            State = CentralObjectRecoveryStates.Cancelled,
            ByteLength = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        (await db.CentralObjectRecoveryDispositions.SingleAsync(item =>
            item.SourceObjectIdentitySha256 == CentralArtifactReconciliationService.CreateObjectKeyIdentity(longKey))
            .ConfigureAwait(false)).SourceObjectKey.Should().Be(longKey).And.HaveLength(longKey.Length);
        longKey.Length.Should().BeGreaterThan(512);
        await db.CentralObjectRecoveryDispositions.Where(item =>
                item.SourceObjectIdentitySha256 == CentralArtifactReconciliationService.CreateObjectKeyIdentity(longKey))
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CatchAllInventory_StreamsThousandsOfCanonicalObjectsAndCompletesPhaseOnce()
    {
        const int objectCount = 2_001;
        var deviceKey = Guid.NewGuid().ToString("N");
        var keys = Enumerable.Range(0, objectCount)
            .Select(index => $"artifacts/{deviceKey}/{index:D5}.bin")
            .ToArray();
        objectKeys.UnionWith(keys);
        var minio = GetMinio();
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (key, cancellationToken) =>
            {
                await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(key)
                    .WithStreamData(new MemoryStream([1], writable: false)).WithObjectSize(1), cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifactsCatchAll).ConfigureAwait(false);
        using var observations = new RecoveryObservationCollector();

        var immediate = await RunFreshCycleAsync().ConfigureAwait(false);

        immediate.Should().BeTrue();
        observations.GetMetricTotal("skymonitor.central.recovery.inventory", "catch-all-examined")
            .Should().BeGreaterThanOrEqualTo(objectCount);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checkpoint = await db.CentralRecoveryCheckpoints.AsNoTracking().SingleAsync().ConfigureAwait(false);
        checkpoint.Phase.Should().Be(CentralRecoveryPhases.MinioDerivatives);
        checkpoint.ObjectCursor.Should().BeNull("linear catch-all traversal has no client replay cursor");
        (await db.CentralObjectRecoveryDispositions.CountAsync(item => item.SourceObjectKey.StartsWith(
            $"artifacts/{deviceKey}/")).ConfigureAwait(false)).Should().Be(0,
            "canonical generated keys are observed but only their bounded partition scan can act on them");
    }

    [TestMethod]
    public async Task EmptyMinioPartitions_RequestImmediateContinuation()
    {
        var partition = await FindEmptyObjectPartitionPairAsync("artifacts/").ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: partition).ConfigureAwait(false);

        var first = await RunFreshCycleAsync().ConfigureAwait(false);
        var second = await RunFreshCycleAsync().ConfigureAwait(false);

        first.Should().BeTrue();
        second.Should().BeTrue();
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var checkpoint = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
        checkpoint.Phase.Should().Be(CentralRecoveryPhases.MinioArtifacts);
        checkpoint.ObjectPartition.Should().Be(partition + 2);
    }

    [TestMethod]
    public async Task StagingCursor_ContinuesPriorBatchThenAdvancesPartition()
    {
        const int partition = 7;
        var retainedKey = "staging/7000-prior-cursor";
        var resumedKey = "staging/70ff-resumed";
        await PutObjectAsync(retainedKey, [1]).ConfigureAwait(false);
        await PutObjectAsync(resumedKey, [2]).ConfigureAwait(false);
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow + CentralArtifactReconciliationService.StagingObjectGracePeriod + TimeSpan.FromMinutes(1));
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, stagingPartition: partition,
            stagingCursor: retainedKey, nextInventoryAtUtc: clock.UtcNow.AddDays(1)).ConfigureAwait(false);

        await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        (await ObjectExistsAsync(retainedKey).ConfigureAwait(false)).Should().BeTrue();
        (await ObjectExistsAsync(resumedKey).ConfigureAwait(false)).Should().BeFalse();
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var checkpoint = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
        checkpoint.StagingPartition.Should().Be(partition + 1);
        checkpoint.StagingCursor.Should().BeNull();
    }

    [TestMethod]
    public async Task LeaseContentionAndRecoveryUseSqlUtcDespiteApplicationClockSkew()
    {
        DateTimeOffset databaseNow;
        await using (var timeScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            databaseNow = await timeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]")
                .SingleAsync().ConfigureAwait(false);
        }
        var clock = new MutableTimeProvider(databaseNow.AddYears(10));
        var competingToken = Guid.NewGuid();
        await SetCheckpointAsync(CentralRecoveryPhases.Idle,
            nextInventoryAtUtc: clock.UtcNow.AddDays(1)).ConfigureAwait(false);
        await using (var contentionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await contentionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralRecoveryCheckpoints]
                    SET [LeaseToken] = {competingToken},
                        [LeaseExpiresAtUtc] = DATEADD(minute, 1, SYSUTCDATETIME())
                    WHERE [Id] = {CentralRecoveryCheckpoint.SingletonId};
                    """).ConfigureAwait(false);
        }
        using var metrics = new RecoveryMetricCollector();

        await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        metrics.CycleOutcomes.Should().ContainSingle().Which.Should().Be("lease-unavailable");

        clock.UtcNow = databaseNow.AddYears(-10);
        await using (var expirationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await expirationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralRecoveryCheckpoints]
                    SET [LeaseExpiresAtUtc] = DATEADD(second, -1, SYSUTCDATETIME())
                    WHERE [Id] = {CentralRecoveryCheckpoint.SingletonId};
                    """).ConfigureAwait(false);
        }
        await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var checkpoint = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false);
        checkpoint.LeaseToken.Should().BeNull();
        checkpoint.LastCycleAtUtc.Should().Be(clock.UtcNow);
    }

    [TestMethod]
    public async Task ConcurrentCycles_CreateOneDispositionWithoutRaceDuplicates()
    {
        var key = $"artifacts/a0/{Guid.NewGuid():N}.bin";
        await PutObjectAsync(key, [1, 0]).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0xa0).ConfigureAwait(false);
        using var firstTelemetry = new CentralIngestTelemetry();
        using var secondTelemetry = new CentralIngestTelemetry();
        var scopeFactory = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var first = new CentralArtifactReconciliationService(scopeFactory, TimeProvider.System, firstTelemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        var second = new CentralArtifactReconciliationService(scopeFactory, TimeProvider.System, secondTelemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);

        await Task.WhenAll(first.ReconcileAsync(CancellationToken.None), second.ReconcileAsync(CancellationToken.None))
            .ConfigureAwait(false);

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var dispositions = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralObjectRecoveryDispositions.Where(item => item.SourceObjectKey == key).ToListAsync().ConfigureAwait(false);
        dispositions.Should().ContainSingle();
        dispositions[0].State.Should().Be(CentralObjectRecoveryStates.PendingCopy);
    }

    [TestMethod]
    public async Task LeaseLostDuringObjectRead_PreventsArtifactStateCommit()
    {
        var key = $"artifacts/b0/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(key, [1, 2, 3], CentralArtifactObjectState.Pending,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        var stolenToken = Guid.NewGuid();
        var handler = new LeaseStealingHandler(async () =>
        {
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralRecoveryCheckpoints
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LeaseToken, stolenToken)
                    .SetProperty(item => item.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(5)))
                .ConfigureAwait(false);
        })
        {
            InnerHandler = new SocketsHttpHandler()
        };
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var minio = new MinioClient().WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
            .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
            .WithHttpClient(httpClient, disposeHttpClient: false).Build();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString));
        services.AddSingleton<IMinioClient>(minio);
        AddObjectReader(services);
        services.AddScoped<ICentralDerivativeJobScheduler>(_ => new RecordingScheduler());
        await using var provider = services.BuildServiceProvider();
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: DateTimeOffset.UtcNow.AddDays(1))
            .ConfigureAwait(false);
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, telemetry, NullLogger<CentralArtifactReconciliationService>.Instance);

        var reconcile = () => reconciler.ReconcileAsync(CancellationToken.None);
        await reconcile.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lease was lost*");

        await using var assertionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
        artifact.ObjectVerifiedAtUtc.Should().BeNull();
        (await db.CentralRecoveryCheckpoints.SingleAsync().ConfigureAwait(false)).LeaseToken.Should().Be(stolenToken);
    }

    [TestMethod]
    public async Task LeaseLostDuringScheduler_RequeuesCommittedVerification()
    {
        var key = $"artifacts/b1/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(key, [1, 2, 4], CentralArtifactObjectState.Pending,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        var state = new SchedulerLeaseLossState(Guid.NewGuid());
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString));
        services.AddSingleton(GetMinio());
        AddObjectReader(services);
        services.AddScoped<ICentralDerivativeJobScheduler>(provider => new CommittingLeaseStealingScheduler(
            provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<IServiceScopeFactory>(), state));
        await using var provider = services.BuildServiceProvider();
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: DateTimeOffset.UtcNow.AddDays(1))
            .ConfigureAwait(false);
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, telemetry, NullLogger<CentralArtifactReconciliationService>.Instance);

        var reconcile = () => reconciler.ReconcileAsync(CancellationToken.None);
        await reconcile.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lease was lost*");

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
            artifact.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            artifact.ObjectVerifiedAtUtc.Should().NotBeNull();
            artifact.ObjectVerificationToken.Should().NotBeNull();
            artifact.ObjectVerificationRetryCount.Should().Be(1);
            artifact.ObjectVerificationRetryAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
            (await db.CentralDerivativeJobs.CountAsync(job => job.SourceCentralArtifactId == artifactId)
                .ConfigureAwait(false)).Should().Be(0);
        }
        state.Invocations.Should().Be(1);
    }

    [TestMethod]
    public async Task HealthReportsIncompleteStalledFindingsAndRecoveredWithoutLeakingKeys()
    {
        await using var database = await IsolatedRecoveryDatabase.CreateAsync().ConfigureAwait(false);
        var db = database.Context;
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        await SetCheckpointAsync(db, CentralRecoveryPhases.SqlArtifacts).ConfigureAwait(false);
        (await CheckHealthAsync(db, clock).ConfigureAwait(false)).Should().Match<HealthCheckResult>(result =>
            result.Status == HealthStatus.Unhealthy && (string)result.Data["Condition"] == "inventory-stalled");

        await SetCheckpointAsync(db, CentralRecoveryPhases.SqlArtifacts, lastProgressAtUtc: now).ConfigureAwait(false);
        (await CheckHealthAsync(db, clock).ConfigureAwait(false)).Should().Match<HealthCheckResult>(result =>
            result.Status == HealthStatus.Degraded && (string)result.Data["Condition"] == "inventory-incomplete");

        await SetCheckpointAsync(db, CentralRecoveryPhases.SqlArtifacts,
            lastProgressAtUtc: now - CentralArtifactConsistencyHealthCheck.StaleAfter - TimeSpan.FromSeconds(1),
            leaseToken: Guid.NewGuid(), leaseExpiresAtUtc: now.AddMinutes(1)).ConfigureAwait(false);
        (await CheckHealthAsync(db, clock).ConfigureAwait(false)).Should().Match<HealthCheckResult>(result =>
            result.Status == HealthStatus.Degraded && (string)result.Data["Condition"] == "inventory-incomplete");

        await SetCheckpointAsync(db, CentralRecoveryPhases.SqlArtifacts,
            lastProgressAtUtc: now - CentralArtifactConsistencyHealthCheck.StaleAfter - TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        (await CheckHealthAsync(db, clock).ConfigureAwait(false)).Should().Match<HealthCheckResult>(result =>
            result.Status == HealthStatus.Unhealthy && (string)result.Data["Condition"] == "inventory-stalled");

        var secretKey = "artifacts/80/operator-secret.bin";
        db.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            SourceObjectIdentitySha256 = CentralArtifactReconciliationService.CreateObjectKeyIdentity(secretKey),
            SourceObjectKey = secretKey,
            TargetObjectKey = "quarantine/orphans/SAFE/object.bin",
            Kind = CentralObjectRecoveryKinds.OrphanQuarantine,
            State = CentralObjectRecoveryStates.Completed,
            ByteLength = 1,
            ContentChecksumSha256 = new string('A', 64),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        await SetCheckpointAsync(db, CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddDays(1)).ConfigureAwait(false);
        var finding = await CheckHealthAsync(db, clock).ConfigureAwait(false);
        finding.Status.Should().Be(HealthStatus.Degraded);
        finding.Data["Condition"].Should().Be("inventory-findings");
        finding.Description.Should().NotContain(secretKey);
        finding.Data.Values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
            .Should().NotContain(value => value!.Contains(secretKey, StringComparison.Ordinal));

        await db.CentralObjectRecoveryDispositions.ExecuteDeleteAsync().ConfigureAwait(false);
        db.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            SourceObjectIdentitySha256 = CentralArtifactReconciliationService.CreateObjectKeyIdentity(secretKey),
            SourceObjectKey = secretKey,
            Kind = CentralObjectRecoveryKinds.OrphanQuarantine,
            State = CentralObjectRecoveryStates.Cancelled,
            ByteLength = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ReasonCode = "ownership.active"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var recovered = await CheckHealthAsync(db, clock).ConfigureAwait(false);
        recovered.Status.Should().Be(HealthStatus.Healthy);
    }

    [TestMethod]
    public async Task SeededOverdueInventory_DegradesThenStallsAfterStartupGrace()
    {
        await using var database = await IsolatedRecoveryDatabase.CreateAsync().ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 17, 14, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var startup = new CentralRecoveryStartupState(now);
        await SetCheckpointAsync(database.Context, CentralRecoveryPhases.Idle,
            nextInventoryAtUtc: DateTimeOffset.UnixEpoch).ConfigureAwait(false);
        var health = new CentralArtifactConsistencyHealthCheck(database.Context, clock, startup);

        var starting = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        starting.Should().Match<HealthCheckResult>(result => result.Status == HealthStatus.Degraded
            && (string)result.Data["Condition"] == "inventory-starting");

        clock.UtcNow += CentralArtifactConsistencyHealthCheck.StartupGrace + TimeSpan.FromSeconds(1);
        var stalled = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        stalled.Should().Match<HealthCheckResult>(result => result.Status == HealthStatus.Unhealthy
            && (string)result.Data["Condition"] == "inventory-stalled");
    }

    [TestMethod]
    public async Task DelayedFirstHealthProbe_UsesHostStartupTimestampAndIsAlreadyStalled()
    {
        await using var database = await IsolatedRecoveryDatabase.CreateAsync().ConfigureAwait(false);
        var hostStartedAt = new DateTimeOffset(2026, 7, 17, 14, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(
            hostStartedAt + CentralArtifactConsistencyHealthCheck.StartupGrace + TimeSpan.FromSeconds(1));
        var startup = new CentralRecoveryStartupState(hostStartedAt);
        await SetCheckpointAsync(database.Context, CentralRecoveryPhases.Idle,
            nextInventoryAtUtc: DateTimeOffset.UnixEpoch).ConfigureAwait(false);

        var firstProbe = await new CentralArtifactConsistencyHealthCheck(database.Context, clock, startup)
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        firstProbe.Should().Match<HealthCheckResult>(result => result.Status == HealthStatus.Unhealthy
            && (string)result.Data["Condition"] == "inventory-stalled");
    }

    [TestMethod]
    public async Task HealthFindingClearsWhenMissingObjectSafelyReturnsButFreshQuarantineRemainsDegraded()
    {
        await using var database = await IsolatedRecoveryDatabase.CreateAsync().ConfigureAwait(false);
        var db = database.Context;
        var now = new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var key = $"artifacts/85/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(db, key, [8, 5], CentralArtifactObjectState.Pending,
            CentralReconstructionState.Complete, putObject: false).ConfigureAwait(false);
        await db.CentralArtifacts.Where(item => item.Id == artifactId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.StateReasonCode, "object.missing")
            .SetProperty(item => item.ReceivedAtUtc, now)
            .SetProperty(item => item.ObjectVerifiedAtUtc,
                now - CentralArtifactReconciliationService.VerificationInterval - TimeSpan.FromSeconds(1)))
            .ConfigureAwait(false);
        await SetCheckpointAsync(db, CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddDays(1)).ConfigureAwait(false);
        var finding = await CheckHealthAsync(db, clock).ConfigureAwait(false);
        finding.Status.Should().Be(HealthStatus.Degraded);
        finding.Data["Condition"].Should().Be("inventory-findings");

        await PutObjectAsync(key, [8, 5]).ConfigureAwait(false);
        await CreateReconciler(database.Services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        (await CheckHealthAsync(db, clock).ConfigureAwait(false)).Status.Should().Be(HealthStatus.Healthy);

        await db.CentralArtifacts.Where(item => item.Id == artifactId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Quarantined)
            .SetProperty(item => item.ReconstructionState, CentralReconstructionState.Quarantined)
            .SetProperty(item => item.StateReasonCode, "object.checksum-mismatch"))
            .ConfigureAwait(false);
        var quarantine = await CheckHealthAsync(db, clock).ConfigureAwait(false);
        quarantine.Status.Should().Be(HealthStatus.Degraded);
        quarantine.Data["Condition"].Should().Be("inventory-findings");
    }

    [TestMethod]
    public async Task ObjectApplicationLock_HashesResourceAndSerializesSqlSessions()
    {
        var storageReference = $"{StoragePrefix}artifacts/private/{Guid.NewGuid():N}-operator-secret.bin";
        var resource = CentralObjectApplicationLock.CreateResource(storageReference);
        resource.Should().Be("hvo-central-object:"
            + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storageReference))));
        resource.Should().NotContain(storageReference);
        resource.Should().NotContain("operator-secret");

        await using var firstScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var first = await CentralObjectApplicationLock.AcquireAsync(
            firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), storageReference, CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            var waiting = CentralObjectApplicationLock.AcquireAsync(
                secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), storageReference, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            waiting.IsCompleted.Should().BeFalse("a different SQL session must not enter the same object operation");

            await first.DisposeAsync().ConfigureAwait(false);
            await using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            await first.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ExpiredReactivation_HoldsObjectLockAndCancelsRecoveryDelete()
    {
        var key = $"artifacts/86/{Guid.NewGuid():N}.bin";
        byte[] payload = [8, 6, 2];
        var artifactId = await AddArtifactAsync(key, payload, CentralArtifactObjectState.Expired,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        await using (var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
            {
                SourceObjectIdentitySha256 = CentralArtifactReconciliationService.CreateObjectKeyIdentity(key),
                SourceObjectKey = key,
                Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                State = CentralObjectRecoveryStates.PendingDelete,
                ByteLength = payload.LongLength,
                ContentChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload)),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: DateTimeOffset.UtcNow.AddDays(1))
            .ConfigureAwait(false);

        await using var publisherScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var publisherDb = publisherScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var publisherLock = await CentralObjectApplicationLock.AcquireAsync(
            publisherDb, StoragePrefix + key, CancellationToken.None).ConfigureAwait(false);
        try
        {
            var recovery = RunFreshCycleAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            recovery.IsCompleted.Should().BeFalse("recovery deletion must wait for an active publisher");

            await publisherDb.CentralArtifacts.Where(item => item.Id == artifactId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Available)
                    .SetProperty(item => item.StateReasonCode, (string?)null))
                .ConfigureAwait(false);
            await publisherLock.DisposeAsync().ConfigureAwait(false);
            _ = await recovery.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            await publisherLock.DisposeAsync().ConfigureAwait(false);
        }

        (await ReadDispositionAsync(key).ConfigureAwait(false)).Should().Match<CentralObjectRecoveryDisposition>(item =>
            item.State == CentralObjectRecoveryStates.Cancelled && item.ReasonCode == "ownership.ambiguous");
        _ = await GetMinio().StatObjectAsync(new StatObjectArgs().WithBucket(Bucket).WithObject(key)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PendingReferenceRetry_ExceedsOldLimitThenRigReturnConvergesAndSchedulesOnce()
    {
        var now = new DateTimeOffset(2026, 7, 17, 18, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var key = $"artifacts/87/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(key, [8, 7], CentralArtifactObjectState.Available,
            CentralReconstructionState.PendingReference, withMissingRigReference: true).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddYears(1)).ConfigureAwait(false);
        var scheduler = new DurableSchedulerState();
        using var services = CreateServices(scheduler);
        var reconciler = CreateReconciler(services, clock);
        (await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        var retry = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        retry.ReferenceRetryCount.Should().Be(1);
        retry.ReferenceRetryAtUtc.Should().Be(now + CentralArtifactReconciliationService.InitialReferenceRetryDelay);

        (await CreateReconciler(services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        (await ReadArtifactAsync(artifactId).ConfigureAwait(false)).ReferenceRetryCount.Should().Be(1,
            "a restart before the durable due time must not retry or spin");

        const int attemptsBeyondOldLimit = 12;
        for (var expectedAttempt = 2; expectedAttempt <= attemptsBeyondOldLimit; expectedAttempt++)
        {
            clock.UtcNow = retry.ReferenceRetryAtUtc!.Value + TimeSpan.FromTicks(1);
            _ = await CreateReconciler(services, clock)
                .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            retry = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
            retry.ReferenceRetryCount.Should().Be(expectedAttempt);
            retry.ReferenceRetryAtUtc.Should().NotBeNull();
        }
        CentralArtifactReconciliationService.CalculateReferenceRetryDelay(retry.ReferenceRetryCount)
            .Should().Be(CentralArtifactReconciliationService.MaximumReferenceRetryDelay);

        await AddMatchingRigProfileAsync(artifactId).ConfigureAwait(false);
        clock.UtcNow = retry.ReferenceRetryAtUtc!.Value + TimeSpan.FromTicks(1);
        _ = await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        var completed = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        completed.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        completed.ReferenceRetryCount.Should().Be(0);
        completed.ReferenceRetryAtUtc.Should().BeNull();
        scheduler.InvocationsFor(artifactId).Should().Be(1);
        (await CountRecoveryJobsAsync(artifactId).ConfigureAwait(false)).Should().Be(1);

        _ = await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        scheduler.InvocationsFor(artifactId).Should().Be(1, "completed retry must not schedule duplicate work");
        (await CountRecoveryJobsAsync(artifactId).ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task PendingReferenceRetry_MissingSourceReturnsAfterOldLimitAndSchedulesOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var key = $"artifacts/88/{Guid.NewGuid():N}.bin";
        var artifactId = await AddArtifactAsync(key, [8, 8], CentralArtifactObjectState.Available,
            CentralReconstructionState.PendingReference, withMissingRigReference: true).ConfigureAwait(false);
        var missingSourceId = Guid.NewGuid();
        await using (var sourceScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = sourceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CentralArtifactSources.Add(new CentralArtifactSource
            {
                CentralArtifactId = artifactId,
                Ordinal = 0,
                SourceArtifactId = missingSourceId
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await AddMatchingRigProfileAsync(artifactId).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddYears(1)).ConfigureAwait(false);
        var scheduler = new DurableSchedulerState();
        using var services = CreateServices(scheduler);
        CentralArtifact retry = null!;
        for (var expectedAttempt = 1; expectedAttempt <= 10; expectedAttempt++)
        {
            if (retry is not null)
            {
                clock.UtcNow = retry.ReferenceRetryAtUtc!.Value + TimeSpan.FromTicks(1);
            }
            _ = await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            retry = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
            retry.ReferenceRetryCount.Should().Be(expectedAttempt);
            retry.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        }

        var returnedId = await AddArtifactAsync($"artifacts/89/{Guid.NewGuid():N}.bin", [8, 9],
            CentralArtifactObjectState.Available, CentralReconstructionState.Complete).ConfigureAwait(false);
        await using (var returnScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = returnScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dependentDevice = await db.CentralArtifacts.Where(item => item.Id == artifactId)
                .Select(item => item.DevicePublicId).SingleAsync().ConfigureAwait(false);
            await db.CentralArtifacts.Where(item => item.Id == returnedId).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ArtifactId, missingSourceId)
                .SetProperty(item => item.DevicePublicId, dependentDevice)).ConfigureAwait(false);
        }
        clock.UtcNow = retry.ReferenceRetryAtUtc!.Value + TimeSpan.FromTicks(1);
        _ = await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        var completed = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        completed.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        completed.ReferenceRetryCount.Should().Be(0);
        scheduler.InvocationsFor(artifactId).Should().Be(1);
        (await CountRecoveryJobsAsync(artifactId).ConfigureAwait(false)).Should().Be(1);
        _ = await CreateReconciler(services, clock).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        scheduler.InvocationsFor(artifactId).Should().Be(1);
        (await CountRecoveryJobsAsync(artifactId).ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task PendingReferenceRetry_SaturatesPersistedCounterWithoutOverflow()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var artifactId = await AddArtifactAsync($"unsupported/{Guid.NewGuid():N}.bin", [8],
            CentralArtifactObjectState.Available, CentralReconstructionState.PendingReference, putObject: false,
            storageReference: $"minio://legacy-overflow/{Guid.NewGuid():N}.bin").ConfigureAwait(false);
        await using (var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
                .Where(item => item.Id == artifactId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ReferenceRetryCount, int.MaxValue)
                    .SetProperty(item => item.ReferenceRetryAtUtc, now.AddSeconds(-1)))
                .ConfigureAwait(false);
        }
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddYears(1)).ConfigureAwait(false);

        _ = await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        var retry = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        retry.ReferenceRetryCount.Should().Be(int.MaxValue);
        retry.ReferenceRetryAtUtc.Should().Be(now + CentralArtifactReconciliationService.MaximumReferenceRetryDelay);
    }

    [TestMethod]
    public async Task UnsupportedReference_UsesRecurringCappedBackoffWithoutSpinning()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var artifactId = await AddArtifactAsync(
            $"unsupported/{Guid.NewGuid():N}.bin", [8, 8], CentralArtifactObjectState.Pending,
            CentralReconstructionState.Complete, putObject: false,
            storageReference: $"minio://legacy-private/{Guid.NewGuid():N}.bin").ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: clock.UtcNow.AddDays(1))
            .ConfigureAwait(false);
        var reconciler = CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock);

        (await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        var artifact = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        artifact.StateReasonCode.Should().Be("object.reference-unsupported");
        artifact.ReferenceRetryCount.Should().Be(1);
        artifact.ReferenceRetryAtUtc.Should().NotBeNull();

        (await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        (await ReadArtifactAsync(artifactId).ConfigureAwait(false)).ReferenceRetryCount.Should().Be(1);
        clock.UtcNow = artifact.ReferenceRetryAtUtc!.Value + TimeSpan.FromTicks(1);
        _ = await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        (await ReadArtifactAsync(artifactId).ConfigureAwait(false)).ReferenceRetryCount.Should().Be(2);
    }

    [TestMethod]
    public async Task PendingReferenceRetry_SelectsOldestDueRowsFairlyAtCycleLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = agentMarker,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now
        };
        Guid oldestDueId = default;
        for (var index = 0; index <= 100; index++)
        {
            var artifact = new CentralArtifact
            {
                Frame = frame,
                CentralFrameId = frame.Id,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = frame.DevicePublicId,
                Role = FrameArtifactRole.Preview,
                RecipeVersion = $"fairness-{index:D3}",
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = "application/octet-stream",
                ByteLength = 1,
                ChecksumSha256 = new string('A', 64),
                StorageReference = $"minio://legacy-fairness/{Guid.NewGuid():N}",
                ReceivedAtUtc = now.AddMinutes(-index),
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.PendingReference,
                ReferenceRetryAtUtc = index == 100 ? now.AddHours(-2) : now.AddHours(-1)
            };
            if (index == 100)
            {
                oldestDueId = artifact.Id;
            }
            frame.Artifacts.Add(artifact);
        }
        await using (var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CentralFrames.Add(frame);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddDays(1)).ConfigureAwait(false);

        _ = await CreateReconciler(AssemblyHooks.Fixture.Factory.Services, clock)
            .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        await using var assertionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertionDb.CentralArtifacts.SingleAsync(item => item.Id == oldestDueId).ConfigureAwait(false))
            .ReferenceRetryCount.Should().Be(1, "the oldest eligible due time must not starve at the cycle cap");
        (await assertionDb.CentralArtifacts.CountAsync(item => item.CentralFrameId == frame.Id
            && item.ReferenceRetryCount == 1).ConfigureAwait(false)).Should().Be(100);
        (await assertionDb.CentralArtifacts.CountAsync(item => item.CentralFrameId == frame.Id
            && item.ReferenceRetryCount == 0).ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task RecoveryTelemetryLogsAndSpanUseOnlyBoundedFields()
    {
        var key = $"artifacts/90/{Guid.NewGuid():N}-private.bin";
        var pendingKey = $"artifacts/91/{Guid.NewGuid():N}-private.bin";
        await PutObjectAsync(key, [9]).ConfigureAwait(false);
        _ = await AddArtifactAsync(pendingKey, [9, 1], CentralArtifactObjectState.Pending,
            CentralReconstructionState.Complete).ConfigureAwait(false);
        await SetCheckpointAsync(CentralRecoveryPhases.MinioArtifacts, partition: 0x90).ConfigureAwait(false);
        using var observations = new RecoveryObservationCollector();
        using var telemetry = new CentralIngestTelemetry();
        var logger = new RecordingLogger<CentralArtifactReconciliationService>();
        using var services = CreateServices(new ThrowingScheduler());
        var reconciler = new CentralArtifactReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            logger);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        observations.TagKeys.Should().BeSubsetOf([
            "outcome", "recovery.outcome", "recovery.scanned", "recovery.matched", "recovery.missing",
            "recovery.corrupt", "recovery.orphans", "recovery.quarantined", "recovery.deleted"
        ]);
        observations.GetMetricTotal("skymonitor.central.recovery.cycles", "completed").Should().Be(1);
        observations.GetMetricTotal("skymonitor.central.recovery.inventory", "scanned").Should().BeGreaterThan(0);
        observations.GetMetricTotal("skymonitor.central.recovery.inventory", "matched").Should().BeGreaterThan(0);
        observations.Metrics.Should().NotBeEmpty().And.OnlyContain(observation => observation.Tags.Count > 0
            && observation.Tags.All(tag => !string.IsNullOrWhiteSpace(tag.Value) && tag.Value.Length <= 64));
        observations.Activities.Should().ContainSingle().Which.Should().Match<ActivityObservation>(activity =>
            activity.Name == "central-artifact.reconcile"
            && activity.Status == ActivityStatusCode.Ok
            && activity.Tags.Count == 8
            && activity.Tags.All(tag => !string.IsNullOrWhiteSpace(tag.Value) && tag.Value.Length <= 64));
        observations.TagValues.Should().NotContain(value => value.Contains(key, StringComparison.Ordinal));
        observations.TagValues.Should().NotContain(value => value.Contains(pendingKey, StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 2120);
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 2121);
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 2124
            && entry.Message == "Central artifact reconciliation failed for one durable record"
            && entry.Exception is InvalidOperationException);
        logger.Entries.Select(entry => entry.Message).Should().NotContain(message =>
            message.Contains(key, StringComparison.Ordinal) || message.Contains(pendingKey, StringComparison.Ordinal));
        logger.Entries.Where(entry => entry.Exception is not null).Select(entry => entry.Exception!.ToString())
            .Should().NotContain(value => value.Contains(key, StringComparison.Ordinal)
                || value.Contains(pendingKey, StringComparison.Ordinal)
                || value.Contains(StoragePrefix, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RecoveryVerification_StorageFailureUsesDurableBoundedRetry()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(40);
        var clock = new MutableTimeProvider(now);
        var artifactId = await AddArtifactAsync(
            $"artifacts/92/{Guid.NewGuid():N}.bin",
            [9, 2],
            CentralArtifactObjectState.Available,
            CentralReconstructionState.PendingReference).ConfigureAwait(false);
        await using (var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
            artifact.ObjectState = CentralArtifactObjectState.Pending;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddDays(1)).ConfigureAwait(false);
        var objectReader = new ThrowingObjectReader();
        using var services = CreateServices(new RecordingScheduler(), objectReader);
        var reconciler = CreateReconciler(services, clock);

        var runImmediately = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        var firstRetry = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        runImmediately.Should().BeFalse("a deferred verification must not spin through recovery cycles");
        firstRetry.ObjectVerificationToken.Should().NotBeNull();
        firstRetry.ObjectVerificationRequestedAtUtc.Should().Be(now);
        firstRetry.ObjectVerificationRetryCount.Should().Be(1);
        firstRetry.ObjectVerificationRetryAtUtc.Should().Be(now
            + CentralArtifactReconciliationService.InitialVerificationRetryDelay);

        _ = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        objectReader.Attempts.Should().Be(1, "the verification is not due again before its durable retry time");

        clock.UtcNow = firstRetry.ObjectVerificationRetryAtUtc!.Value + TimeSpan.FromTicks(1);
        _ = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        var secondRetry = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        objectReader.Attempts.Should().Be(2);
        secondRetry.ObjectVerificationRetryCount.Should().Be(2);
        secondRetry.ObjectVerificationRetryAtUtc.Should().Be(clock.UtcNow
            + CentralArtifactReconciliationService.CalculateVerificationRetryDelay(2));
    }

    [TestMethod]
    public async Task StrandedVerification_NoncanonicalReferenceConvergesToQuarantine()
    {
        var now = DateTimeOffset.UtcNow;
        var artifactId = await AddArtifactAsync(
            $"legacy/{Guid.NewGuid():N}.bin",
            [9, 3],
            CentralArtifactObjectState.Pending,
            CentralReconstructionState.Complete,
            putObject: false,
            storageReference: $"minio://legacy/{Guid.NewGuid():N}.bin").ConfigureAwait(false);
        await using (var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
            artifact.ObjectVerificationToken = Guid.NewGuid();
            artifact.ObjectVerificationRequestedAtUtc = now.AddMinutes(-1);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await SetCheckpointAsync(CentralRecoveryPhases.Idle, nextInventoryAtUtc: now.AddDays(1)).ConfigureAwait(false);

        _ = await RunFreshCycleAsync().ConfigureAwait(false);

        var reconciled = await ReadArtifactAsync(artifactId).ConfigureAwait(false);
        reconciled.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
        reconciled.StateReasonCode.Should().Be("object.reference-invalid");
        reconciled.ObjectVerificationToken.Should().BeNull();
        reconciled.ObjectVerificationRequestedAtUtc.Should().BeNull();
    }

    private async Task<Guid> AddArtifactAsync(
        string objectKey,
        byte[] expectedPayload,
        CentralArtifactObjectState objectState,
        CentralReconstructionState reconstructionState,
        bool putObject = true,
        byte[]? objectPayload = null,
        string? storageReference = null,
        bool withMissingRigReference = false)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await AddArtifactAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), objectKey,
            expectedPayload, objectState, reconstructionState, putObject, objectPayload, storageReference,
            withMissingRigReference)
            .ConfigureAwait(false);
    }

    private async Task<Guid> AddArtifactAsync(
        ApplicationDbContext db,
        string objectKey,
        byte[] expectedPayload,
        CentralArtifactObjectState objectState,
        CentralReconstructionState reconstructionState,
        bool putObject = true,
        byte[]? objectPayload = null,
        string? storageReference = null,
        bool withMissingRigReference = false)
    {
        if (putObject)
        {
            await PutObjectAsync(objectKey, objectPayload ?? expectedPayload).ConfigureAwait(false);
        }
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = agentMarker,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            FirstReceivedAtUtc = DateTimeOffset.UnixEpoch
        };
        var artifact = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = Guid.NewGuid(),
            DevicePublicId = frame.DevicePublicId,
            Role = FrameArtifactRole.Preview,
            RecipeVersion = "recovery-test-v1",
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "application/octet-stream",
            ByteLength = expectedPayload.LongLength,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(expectedPayload)),
            StorageReference = storageReference ?? StoragePrefix + objectKey,
            ReceivedAtUtc = DateTimeOffset.UnixEpoch,
            IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ObjectState = objectState,
            ReconstructionState = reconstructionState
        };
        if (withMissingRigReference)
        {
            frame.Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = frame.CapturedAtUtc,
                ExposureStartedUtc = frame.CapturedAtUtc,
                ExposureEndedUtc = frame.CapturedAtUtc,
                ReadoutCompletedUtc = frame.CapturedAtUtc,
                DurableIngressUtc = frame.CapturedAtUtc
            };
            frame.Profiles.Add(new CentralCaptureProfile
            {
                Kind = CentralProfileKind.Rig,
                Name = "missing-recovery-rig",
                Version = "1",
                Sha256 = new string('A', 64)
            });
        }
        frame.Artifacts.Add(artifact);
        db.CentralFrames.Add(frame);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return artifact.Id;
    }

    private async Task PutObjectAsync(string key, byte[] payload)
    {
        objectKeys.Add(key);
        await GetMinio().PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(key)
            .WithStreamData(new MemoryStream(payload)).WithObjectSize(payload.LongLength)).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadObjectAsync(string key)
    {
        using var stream = new MemoryStream();
        await GetMinio().GetObjectAsync(new GetObjectArgs().WithBucket(Bucket).WithObject(key)
            .WithCallbackStream(source => source.CopyTo(stream))).ConfigureAwait(false);
        return stream.ToArray();
    }

    private static async Task<bool> ObjectExistsAsync(string key)
    {
        try
        {
            await GetMinio().StatObjectAsync(new StatObjectArgs().WithBucket(Bucket).WithObject(key)).ConfigureAwait(false);
            return true;
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return false;
        }
    }

    private static IMinioClient GetMinio()
        => AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();

    private static async Task<int> FindEmptyObjectPartitionPairAsync(string prefix)
    {
        var occupied = new HashSet<int>();
        await foreach (var item in GetMinio().ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true)))
        {
            var relative = item.Key.AsSpan(prefix.Length);
            if (relative.Length >= 2
                && int.TryParse(relative[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var partition))
            {
                occupied.Add(partition);
            }
        }
        return Enumerable.Range(0, 255).First(partition =>
            !occupied.Contains(partition) && !occupied.Contains(partition + 1));
    }

    private static CentralArtifactReconciliationService CreateReconciler(IServiceProvider services, TimeProvider timeProvider)
        => new(services.GetRequiredService<IServiceScopeFactory>(), timeProvider, new CentralIngestTelemetry(),
            NullLogger<CentralArtifactReconciliationService>.Instance);

    private static ServiceProvider CreateServices(
        ICentralDerivativeJobScheduler scheduler,
        ICentralArtifactObjectReader? objectReader = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString));
        services.AddSingleton(GetMinio());
        if (objectReader is null)
        {
            AddObjectReader(services);
        }
        else
        {
            services.AddSingleton(objectReader);
        }
        services.AddScoped(_ => scheduler);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateServices(DurableSchedulerState state)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString));
        services.AddSingleton(GetMinio());
        AddObjectReader(services);
        services.AddScoped<ICentralDerivativeJobScheduler>(provider => new DurableRecordingScheduler(
            provider.GetRequiredService<ApplicationDbContext>(), state));
        return services.BuildServiceProvider();
    }

    private static void AddObjectReader(IServiceCollection services)
    {
        services.AddSingleton<CentralArtifactRetrievalTelemetry>();
        services.AddScoped<ICentralArtifactObjectReader>(provider => new CentralArtifactObjectReader(
            provider.GetRequiredService<IMinioClient>(),
            provider.GetRequiredService<CentralArtifactRetrievalTelemetry>(),
            TimeProvider.System,
            NullLogger<CentralArtifactObjectReader>.Instance));
    }

    private static async Task<int> CountRecoveryJobsAsync(Guid artifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
            .CountAsync(item => item.SourceCentralArtifactId == artifactId
                && item.RecipeName == "recovery-convergence-test").ConfigureAwait(false);
    }

    private static async Task<bool> RunFreshCycleAsync()
    {
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        return await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<CentralObjectRecoveryDisposition> ReadDispositionAsync(string sourceObjectKey)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.SourceObjectKey == sourceObjectKey).ConfigureAwait(false);
    }

    private static async Task<CentralArtifact> ReadArtifactAsync(Guid artifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
    }

    private async Task AddMatchingRigProfileAsync(Guid artifactId)
    {
        await AssemblyHooks.Fixture.SeedActiveDeviceAsync(agentMarker).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.AsNoTracking()
            .SingleAsync(item => item.DeviceId == agentMarker).ConfigureAwait(false);
        var nextVersion = await db.DeviceRigProfiles.Where(item => item.RegistrationId == registration.Id)
            .Select(item => (int?)item.Version).MaxAsync().ConfigureAwait(false) ?? 0;
        var artifact = await db.CentralArtifacts.Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
        artifact.DevicePublicId = registration.DevicePublicId;
        artifact.Frame!.RegistrationId = registration.Id;
        artifact.Frame.DevicePublicId = registration.DevicePublicId!.Value;
        artifact.Frame.ObservatoryId = registration.ObservatoryId;
        var identity = artifact.Frame.Profiles.Single(item => item.Kind == CentralProfileKind.Rig);
        db.DeviceRigProfiles.Add(new DeviceRigProfile
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = registration.ObservatoryId,
            Version = nextVersion + 1,
            ConfigHash = identity.Sha256,
            ConfigJson = "{}",
            ProfileName = identity.Name,
            ProfileVersion = identity.Version,
            ProfileSha256 = identity.Sha256,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            EffectiveFromUtc = DateTimeOffset.UnixEpoch
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static Task<HealthCheckResult> CheckHealthAsync(ApplicationDbContext db, TimeProvider clock)
    {
        var health = new CentralArtifactConsistencyHealthCheck(
            db, clock, new CentralRecoveryStartupState(clock.GetUtcNow()));
        return health.CheckHealthAsync(new HealthCheckContext());
    }

    private static async Task ResetCheckpointAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralObjectRecoveryDispositions.ExecuteDeleteAsync().ConfigureAwait(false);
        await SetCheckpointCoreAsync(db, CentralRecoveryPhases.Idle, 1, 0, 0, null,
            DateTimeOffset.UtcNow.AddDays(1), null, null, null).ConfigureAwait(false);
    }

    private async Task ExcludeUnownedFromGenerationAsync(long generation)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .Where(artifact => artifact.Frame!.AgentId != agentMarker)
            .ExecuteUpdateAsync(setters => setters.SetProperty(artifact => artifact.RecoveryGeneration, generation))
            .ConfigureAwait(false);
    }

    private static async Task SetCheckpointAsync(
        string phase,
        long generation = 1,
        int partition = 0,
        int stagingPartition = 0,
        string? stagingCursor = null,
        DateTimeOffset? nextInventoryAtUtc = null,
        DateTimeOffset? lastProgressAtUtc = null,
        Guid? leaseToken = null,
        DateTimeOffset? leaseExpiresAtUtc = null)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await SetCheckpointCoreAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), phase, generation,
            partition, stagingPartition, stagingCursor, nextInventoryAtUtc ?? DateTimeOffset.UnixEpoch,
            lastProgressAtUtc, leaseToken, leaseExpiresAtUtc).ConfigureAwait(false);
    }

    private static Task<int> SetCheckpointAsync(
        ApplicationDbContext db,
        string phase,
        long generation = 1,
        int partition = 0,
        int stagingPartition = 0,
        string? stagingCursor = null,
        DateTimeOffset? nextInventoryAtUtc = null,
        DateTimeOffset? lastProgressAtUtc = null,
        Guid? leaseToken = null,
        DateTimeOffset? leaseExpiresAtUtc = null)
        => SetCheckpointCoreAsync(db, phase, generation, partition, stagingPartition, stagingCursor,
            nextInventoryAtUtc ?? DateTimeOffset.UnixEpoch, lastProgressAtUtc, leaseToken, leaseExpiresAtUtc);

    private static Task<int> SetCheckpointCoreAsync(
        ApplicationDbContext db,
        string phase,
        long generation,
        int partition,
        int stagingPartition,
        string? stagingCursor,
        DateTimeOffset nextInventoryAtUtc,
        DateTimeOffset? lastProgressAtUtc,
        Guid? leaseToken,
        DateTimeOffset? leaseExpiresAtUtc)
        => db.CentralRecoveryCheckpoints.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Generation, generation)
            .SetProperty(item => item.Phase, phase)
            .SetProperty(item => item.ObjectPartition, partition)
            .SetProperty(item => item.ObjectCursor, (string?)null)
            .SetProperty(item => item.StagingPartition, stagingPartition)
            .SetProperty(item => item.StagingCursor, stagingCursor)
            .SetProperty(item => item.NextInventoryAtUtc, nextInventoryAtUtc)
            .SetProperty(item => item.InventoryStartedAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.LastProgressAtUtc, lastProgressAtUtc)
            .SetProperty(item => item.LastCompletedAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.LastCycleAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.LastFailureAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.LeaseToken, leaseToken)
            .SetProperty(item => item.LeaseExpiresAtUtc, leaseExpiresAtUtc)
            .SetProperty(item => item.FindingCount, 0L)
            .SetProperty(item => item.FindingBytes, 0L));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RecordingScheduler : ICentralDerivativeJobScheduler
    {
        public List<Guid> ArtifactIds { get; } = [];
        public Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
        {
            ArtifactIds.Add(artifact.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingScheduler : ICentralDerivativeJobScheduler
    {
        public Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated scheduler failure");
    }

    private sealed class DurableSchedulerState
    {
        private readonly ConcurrentDictionary<Guid, int> invocations = new();

        public void Record(Guid artifactId) => invocations.AddOrUpdate(artifactId, 1, static (_, count) => count + 1);

        public int InvocationsFor(Guid artifactId) => invocations.GetValueOrDefault(artifactId);
    }

    private sealed class DurableRecordingScheduler(ApplicationDbContext db, DurableSchedulerState state)
        : ICentralDerivativeJobScheduler
    {
        public async Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            state.Record(artifact.Id);
            var identity = Convert.ToHexString(SHA256.HashData(artifact.Id.ToByteArray()));
            if (await db.CentralDerivativeJobs.AnyAsync(item => item.RequestIdentitySha256 == identity, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }
            db.CentralDerivativeJobs.Add(new CentralDerivativeJob
            {
                SourceCentralArtifactId = artifact.Id,
                SourceArtifact = artifact,
                TargetRole = FrameArtifactRole.Metadata,
                TargetRecipeVersion = "recovery-convergence-v1",
                TargetVariant = "test",
                RecipeName = "recovery-convergence-test",
                RecipeOptionsJson = "{}",
                InputSelectorJson = "{}",
                RequestedRecipeIdentitySha256 = identity,
                RequestIdentitySha256 = identity,
                Status = CentralDerivativeJobStatus.Pending,
                MaxAttempts = 1,
                AvailableAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class LeaseStealingHandler(Func<Task> stealLease) : DelegatingHandler
    {
        private int armed = 1;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.Query.Contains("list-type", StringComparison.Ordinal) != true
                && Interlocked.Exchange(ref armed, 0) == 1)
            {
                await stealLease().ConfigureAwait(false);
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SchedulerLeaseLossState(Guid stolenToken)
    {
        public Guid StolenToken { get; } = stolenToken;
        public int Invocations { get; set; }
    }

    private sealed class CommittingLeaseStealingScheduler(
        ApplicationDbContext db,
        IServiceScopeFactory scopeFactory,
        SchedulerLeaseLossState state) : ICentralDerivativeJobScheduler
    {
        public async Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            state.Invocations++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralRecoveryCheckpoints
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LeaseToken, state.StolenToken)
                    .SetProperty(item => item.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(5)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class RecoveryMetricCollector : IDisposable
    {
        private readonly MeterListener listener = new();
        public List<string> CycleOutcomes { get; } = [];

        public RecoveryMetricCollector()
        {
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == CentralIngestTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.recovery.cycles")
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome" && tag.Value is string outcome)
                    {
                        CycleOutcomes.Add(outcome);
                    }
                }
            });
            listener.Start();
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed class RecoveryObservationCollector : IDisposable
    {
        private readonly MeterListener meterListener = new();
        private readonly ActivityListener activityListener;
        public HashSet<string> TagKeys { get; } = new(StringComparer.Ordinal);
        public List<string> TagValues { get; } = [];
        public List<MetricObservation> Metrics { get; } = [];
        public List<ActivityObservation> Activities { get; } = [];

        public RecoveryObservationCollector()
        {
            meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralIngestTelemetry.MeterName
                    && instrument.Name.StartsWith("skymonitor.central.recovery.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                Record(instrument.Name, measurement, tags));
            meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                Record(instrument.Name, measurement, tags));
            meterListener.Start();
            activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CentralIngestTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var tag in activity.TagObjects)
                    {
                        TagKeys.Add(tag.Key);
                        var value = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                        TagValues.Add(value);
                        tags[tag.Key] = value;
                    }
                    Activities.Add(new ActivityObservation(activity.OperationName, activity.Status, tags));
                }
            };
            ActivitySource.AddActivityListener(activityListener);
        }

        public long GetMetricTotal(string instrumentName, string outcome)
            => checked((long)Metrics.Where(observation => observation.InstrumentName == instrumentName
                    && observation.Tags.GetValueOrDefault("outcome") == outcome)
                .Sum(observation => observation.Value));

        private void Record(
            string instrumentName,
            double measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                TagKeys.Add(tag.Key);
                var value = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                TagValues.Add(value);
                values[tag.Key] = value;
            }
            Metrics.Add(new MetricObservation(instrumentName, measurement, values));
        }

        public void Dispose()
        {
            meterListener.Dispose();
            activityListener.Dispose();
        }
    }

    private sealed class IsolatedRecoveryDatabase(
        ApplicationDbContext context,
        ServiceProvider services) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public ServiceProvider Services { get; } = services;

        public static async Task<IsolatedRecoveryDatabase> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = $"SkyMonitorRecoveryHealth_{Guid.NewGuid():N}"
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            var context = new ApplicationDbContext(options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(builder => builder.UseSqlServer(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
            services.AddSingleton(GetMinio());
            AddObjectReader(services);
            services.AddScoped<ICentralDerivativeJobScheduler>(_ => new RecordingScheduler());
            return new IsolatedRecoveryDatabase(context, services.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync().ConfigureAwait(false);
            await Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            await Context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class ThrowingObjectReader : ICentralArtifactObjectReader
    {
        public int Attempts { get; private set; }

        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new CentralArtifactStorageException("Injected object-store failure.");
        }

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Generation checks are not reached after a failed verification.");

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Copies are not used by reconciliation verification.");
    }

    private sealed record MetricObservation(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, string> Tags);
    private sealed record ActivityObservation(
        string Name,
        ActivityStatusCode Status,
        IReadOnlyDictionary<string, string> Tags);
    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
}
