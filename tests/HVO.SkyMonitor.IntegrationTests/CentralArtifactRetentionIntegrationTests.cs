using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralArtifactRetentionIntegrationTests
{
    private const string Bucket = "skymonitor-artifacts";

    [TestMethod]
    public async Task Release_ReservesDeletesFinalizesAndDuplicateRemainsReleased()
    {
        await using var database = await CreateDatabaseAsync("Complete").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "complete", [1, 2, 3, 4]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var service = CreateService(
            database.Context, GetFixtureMinio(), telemetry, signals.ProcessorLogger, signals.ServiceLogger);

        (await service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        (await service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        (await service.ReleaseAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.NotFound);

        database.Context.ChangeTracker.Clear();
        var artifact = await database.Context.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        var disposition = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
        artifact.RetentionDeletionToken.Should().NotBeNull();
        artifact.RetentionDeletionRequestedAtUtc.Should().NotBeNull();
        artifact.RetentionDeletionCompletedAtUtc.Should().NotBeNull();
        disposition.OperationToken.Should().Be(artifact.RetentionDeletionToken);
        disposition.State.Should().Be(CentralObjectRecoveryStates.Completed);
        disposition.CompletedAtUtc.Should().NotBeNull();
        (await CentralObjectOwnershipFence.IsRetiredAsync(
            database.Context, seeded.StorageReference, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        (await CentralObjectOwnershipFence.IsRetiredAsync(
            database.Context,
            CentralObjectOwnershipFence.BucketPrefix + seeded.ObjectKey.ToUpperInvariant(),
            CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        signals.Instruments.Should().BeEquivalentTo([
            "skymonitor.central.retention.operations",
            "skymonitor.central.retention.duration",
            "skymonitor.central.retention.stage.duration",
            "skymonitor.central.retention.object_bytes",
            "skymonitor.central.retention.pending",
            "skymonitor.central.retention.pending_bytes",
            "skymonitor.central.retention.pending_oldest_age",
            "skymonitor.central.retention.recovery",
            "skymonitor.central.retention.finalization_conflicts"
        ]);
        signals.Activities.Should().Contain([
            "central-artifact.retention",
            "central-artifact.retention.reserve",
            "central-artifact.retention.delete",
            "central-artifact.retention.finalize"
        ]);
        signals.Logs.Select(item => item.EventId).Should().Contain([2170, 2171, 2172]);
        signals.MetricTagKeys.Should().BeSubsetOf(["outcome", "origin", "stage"]);
        signals.ActivityTagKeys.Should().BeSubsetOf(["retention.origin", "retention.outcome"]);
        signals.Logs.SelectMany(item => item.FieldNames).Distinct().Should().BeSubsetOf(
            ["Outcome", "Origin", "State", "Bytes", "DurationMs", "FailureCategory"]);
        var logText = string.Join('\n', signals.Logs.Select(item => item.Message));
        logText.Should().NotContain(seeded.ObjectKey);
        logText.Should().NotContain(artifact.RetentionDeletionToken!.Value.ToString("D"));
        var health = await new CentralArtifactRetentionHealthCheck(
                database.Context,
                TimeProvider.System,
                Options.Create(new CentralArtifactRetentionOptions()))
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Healthy);
        health.Data.Keys.Should().BeEquivalentTo(
            ["Condition", "PendingCount", "PendingBytes", "PendingOldestAgeSeconds"]);
        var healthText = JsonSerializer.Serialize(health.Data);
        healthText.Should().NotContain(seeded.ObjectKey);
        healthText.Should().NotContain(artifact.RetentionDeletionToken.Value.ToString("D"));
        await AssertMissingAsync(seeded.ObjectKey).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Release_HappyPathUsesSeventeenEfCommandsNineteenTotalAndTwoTransactions()
    {
        await using var database = await CreateDatabaseAsync("ProtocolBudget").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "protocol-budget", [61, 62, 63]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var evidence = new Issue246RetentionEvidenceCollector();
        await using var measured = CreateContext(
            database.ConnectionString, evidence.Commands, evidence.Transactions);
        using var minio = CreateMinio(evidence.Http);
        using var telemetry = new CentralArtifactRetentionTelemetry();

        evidence.Reset();
        (await CreateService(measured, minio, telemetry)
            .ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);

        var snapshot = evidence.Snapshot();
        snapshot.EfCommands.Should().Be(17);
        const int directApplicationLockCommands = 2;
        (snapshot.EfCommands + directApplicationLockCommands).Should().Be(19,
            "the EF interceptor excludes the direct sp_getapplock and sp_releaseapplock commands");
        snapshot.SqlTransactions.StartAttempts.Should().Be(2);
        snapshot.SqlTransactions.SuccessfullyStarted.Should().Be(2);
        snapshot.SqlTransactions.Committed.Should().Be(2);
        snapshot.SqlTransactions.RolledBack.Should().Be(0);
        snapshot.SqlTransactions.Failed.Should().Be(0);
        snapshot.ObjectStore.Deletes.Should().Be(1);
    }

    [TestMethod]
    public async Task RetentionReferenceAndOwnerQueries_UseUnionShapeAndSupportingIndexes()
    {
        await using var database = await CreateDatabaseAsync("QueryShape").ConfigureAwait(false);
        var target = await SeedAsync(database.Context, "query-shape-target", [66]).ConfigureAwait(false);
        var unrelated = await SeedAsync(database.Context, "query-shape-unrelated", [67]).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        _ = AddDerivativeJob(database.Context, unrelated.ArtifactId, now);
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();

        var commands = new CommandShapeInterceptor();
        await using var measured = CreateContext(database.ConnectionString, commands);
        var started = Stopwatch.StartNew();
        (await new CentralArtifactRetentionReferences(measured)
            .IsHeldAsync(target.ArtifactId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        (await CentralObjectOwnershipFence.HasActiveOwnerAsync(
            measured, target.StorageReference, target.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().BeFalse();
        started.Stop();

        var directReferenceSql = commands.Commands.Single(command =>
            command.Contains("[CentralClearReferenceDesignations]", StringComparison.Ordinal)
            && command.Contains("[CentralTransientDerivativeBackgrounds]", StringComparison.Ordinal));
        directReferenceSql.Should().Contain("UNION ALL");
        var ownerSql = commands.Commands.Single(command =>
            command.Contains("[ActiveOwners]", StringComparison.Ordinal));
        ownerSql.Should().Contain("UNION ALL")
            .And.Contain("[IX_CentralArtifacts_StorageReference]")
            .And.Contain("[StorageReferenceSha256]");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        var artifactType = measured.Model.FindEntityType(typeof(CentralArtifact));
        artifactType.Should().NotBeNull();
        artifactType!.GetIndexes().Should().Contain(index =>
            index.Properties.Count == 1
            && index.Properties[0].Name == nameof(CentralArtifact.StorageReference));
        var intentType = measured.Model.FindEntityType(typeof(CentralTransientDerivativeOutputIntent));
        intentType.Should().NotBeNull();
        intentType!.GetIndexes().Should().Contain(index =>
            index.Properties.Count == 1 && index.Properties[0].Name == "StorageReferenceSha256");
        var designationType = measured.Model.FindEntityType(typeof(CentralClearReferenceDesignation));
        designationType.Should().NotBeNull();
        designationType!.GetIndexes().Should().Contain(index =>
            index.Properties.Count == 1
            && index.Properties[0].Name == nameof(CentralClearReferenceDesignation.CentralArtifactId));
    }

    [TestMethod]
    public async Task ReservationDeadlockRetry_ReusesTokenAndCreatesOneTerminalDisposition()
    {
        await using var database = await CreateDatabaseAsync("ReservationDeadlockRetry").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "reservation-deadlock-retry", [53, 54, 55]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var injected = new InvalidOperationException("Injected reservation deadlock.");
        var observedTokens = new List<Guid>();
        var service = CreateService(
            database.Context, GetFixtureMinio(), telemetry, signals.ProcessorLogger, signals.ServiceLogger);
        service.ReservationDeadlockClassifier = exception => ReferenceEquals(exception, injected);
        service.ReservationFaultInjector = (attempt, token) =>
        {
            observedTokens.Add(token);
            return attempt == 0 ? injected : null;
        };

        (await service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        observedTokens.Should().HaveCount(2);
        observedTokens.Distinct().Should().ContainSingle();
        signals.RetryMeasurements.Should().ContainSingle(item =>
            item.Stage == "reserve" && item.Outcome == "retry" && item.Origin == "request");
        database.Context.ChangeTracker.Clear();
        var disposition = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false);
        disposition.OperationToken.Should().Be(observedTokens[0]);
        disposition.State.Should().Be(CentralObjectRecoveryStates.Completed);
        disposition.AttemptCount.Should().Be(1);
        var completedAt = disposition.CompletedAtUtc;

        (await service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        database.Context.ChangeTracker.Clear();
        var replayed = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.CentralArtifactId == seeded.ArtifactId).ToArrayAsync().ConfigureAwait(false);
        replayed.Should().ContainSingle();
        replayed[0].OperationToken.Should().Be(observedTokens[0]);
        replayed[0].CompletedAtUtc.Should().Be(completedAt);
    }

    [TestMethod]
    public async Task ReservationDeadlockRetry_ExhaustionThrowsBoundedFailureWithoutDurableMutation()
    {
        await using var database = await CreateDatabaseAsync("ReservationDeadlockExhaustion").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "reservation-deadlock-exhaustion", [56, 57]).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var injected = new InvalidOperationException("Injected reservation deadlock.");
        var observedTokens = new List<Guid>();
        var service = CreateService(
            database.Context, GetFixtureMinio(), telemetry, signals.ProcessorLogger, signals.ServiceLogger);
        service.ReservationDeadlockClassifier = exception => ReferenceEquals(exception, injected);
        service.ReservationFaultInjector = (_, token) =>
        {
            observedTokens.Add(token);
            return injected;
        };

        CentralArtifactRetentionService.IsSqlServerDeadlock(
            new InvalidOperationException("unrelated", new DbUpdateException("unrelated"))).Should().BeFalse();
        var release = () => service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        await release.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Central artifact retention reservation exhausted SQL conflict retries.")
            .ConfigureAwait(false);
        observedTokens.Should().HaveCount(CentralArtifactRetentionService.MaximumReservationConflictRetries + 1);
        observedTokens.Distinct().Should().ContainSingle();
        signals.RetryMeasurements.Should().HaveCount(CentralArtifactRetentionService.MaximumReservationConflictRetries)
            .And.OnlyContain(item =>
                item.Stage == "reserve" && item.Outcome == "retry" && item.Origin == "request");

        database.Context.ChangeTracker.Clear();
        var artifact = await database.Context.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.RetentionDeletionToken.Should().BeNull();
        artifact.RetentionDeletionRequestedAtUtc.Should().BeNull();
        artifact.RetentionDeletionCompletedAtUtc.Should().BeNull();
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .CountAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false)).Should().Be(0);
    }

    [TestMethod]
    public async Task ReservationUniqueConflictRetry_ReusesTokenAndCompletesOnce()
    {
        await using var database = await CreateDatabaseAsync("ReservationUniqueConflict").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "reservation-unique-conflict", [64, 65]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var injected = new InvalidOperationException("Injected reservation uniqueness conflict.");
        var observedTokens = new List<Guid>();
        var service = CreateService(
            database.Context, GetFixtureMinio(), telemetry, signals.ProcessorLogger, signals.ServiceLogger);
        service.ReservationUniqueConstraintClassifier = exception => ReferenceEquals(exception, injected);
        service.ReservationFaultInjector = (attempt, token) =>
        {
            observedTokens.Add(token);
            return attempt == 0 ? injected : null;
        };

        (await service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        observedTokens.Should().HaveCount(2);
        observedTokens.Distinct().Should().ContainSingle();
        signals.RetryMeasurements.Should().ContainSingle(item =>
            item.Stage == "reserve" && item.Outcome == "retry" && item.Origin == "request");

        database.Context.ChangeTracker.Clear();
        var dispositions = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.CentralArtifactId == seeded.ArtifactId).ToArrayAsync().ConfigureAwait(false);
        dispositions.Should().ContainSingle();
        dispositions[0].State.Should().Be(CentralObjectRecoveryStates.Completed);
        dispositions[0].AttemptCount.Should().Be(1);
    }

    [TestMethod]
    public async Task LegacyDisposition_ConcurrentReconciliationAndRequestAdoptionConvergeOnce()
    {
        await using var database = await CreateDatabaseAsync("LegacyAdoptionRace").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "legacy-adoption-race", [68, 69]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var legacyId = Guid.NewGuid();
        database.Context.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            Id = legacyId,
            SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(seeded.ObjectKey),
            SourceObjectKey = seeded.ObjectKey,
            Kind = CentralObjectRecoveryKinds.ExpiredDelete,
            State = CentralObjectRecoveryStates.PendingDelete,
            ByteLength = seeded.Payload.LongLength,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();

        using var telemetry = new CentralArtifactRetentionTelemetry();
        var reservationEntered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reservationGate = new ManualResetEventSlim();
        var service = CreateService(database.Context, GetFixtureMinio(), telemetry);
        service.ReservationFaultInjector = (_, operationToken) =>
        {
            reservationEntered.TrySetResult(operationToken);
            if (!reservationGate.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("The reconciliation barrier did not release reservation adoption.");
            }
            return null;
        };

        var release = service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        var operationToken = await reservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var candidateObserver = new ReconciliationCandidateObserver();
        await using var reconciliationServices = CreateReconciliationServices(
            database.ConnectionString, candidateObserver);
        var reconciler = new CentralArtifactReconciliationService(
            reconciliationServices.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<CentralIngestTelemetry>(),
            NullLogger<CentralArtifactReconciliationService>.Instance);
        var reconciliation = reconciler.ReconcileAsync(CancellationToken.None);
        await candidateObserver.LegacyCandidateRead.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        reservationGate.Set();

        (await release.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        _ = await reconciliation.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        database.Context.ChangeTracker.Clear();
        var disposition = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == legacyId).ConfigureAwait(false);
        disposition.OperationToken.Should().Be(operationToken);
        disposition.CentralArtifactId.Should().Be(seeded.ArtifactId);
        disposition.State.Should().Be(CentralObjectRecoveryStates.Completed);
        disposition.AttemptCount.Should().Be(1);
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .CountAsync(item => item.SourceObjectIdentitySha256 == disposition.SourceObjectIdentitySha256)
            .ConfigureAwait(false)).Should().Be(1);
        await AssertMissingAsync(seeded.ObjectKey).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ConcurrentDistinctReleases_RepeatedC8CompletesWithoutRetriesOrDuplicates()
    {
        await using var database = await CreateDatabaseAsync("RepeatedC8").ConfigureAwait(false);
        var seeded = new List<SeededArtifact>();
        for (var index = 0; index < 64; index++)
        {
            var artifact = await SeedAsync(
                database.Context, $"repeated-c8-{index}", [(byte)index]).ConfigureAwait(false);
            seeded.Add(artifact);
            await PutAsync(artifact.ObjectKey, artifact.Payload).ConfigureAwait(false);
        }
        database.Context.ChangeTracker.Clear();
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var retries = new RetentionRetryCollector();
        var results = new CentralArtifactRetentionResult[seeded.Count];

        var lanes = Enumerable.Range(0, 8).Select(async lane =>
        {
            await using var context = CreateContext(database.ConnectionString);
            var service = CreateService(context, GetFixtureMinio(), telemetry);
            for (var index = lane; index < seeded.Count; index += 8)
            {
                results[index] = await service.ReleaseAsync(
                    seeded[index].ArtifactId, CancellationToken.None).ConfigureAwait(false);
            }
        });
        await Task.WhenAll(lanes).WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        results.Should().OnlyContain(result => result == CentralArtifactRetentionResult.Released);
        retries.Measurements.Should().BeEmpty();
        database.Context.ChangeTracker.Clear();
        var dispositions = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => seeded.Select(value => value.ArtifactId).Contains(item.CentralArtifactId!.Value))
            .ToArrayAsync().ConfigureAwait(false);
        dispositions.Should().HaveCount(seeded.Count);
        dispositions.Should().OnlyContain(item =>
            item.State == CentralObjectRecoveryStates.Completed && item.AttemptCount == 1);
    }

    [TestMethod]
    public async Task WorkerPrepareDeadlockExhaustion_RecordsOnlyRetriesActuallyAttempted()
    {
        await using var database = await CreateDatabaseAsync("PrepareDeadlockExhaustion").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "prepare-deadlock-exhaustion", [70]).ConfigureAwait(false);
        var operationToken = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var artifact = await database.Context.CentralArtifacts.SingleAsync(item => item.Id == seeded.ArtifactId)
            .ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Expired;
        artifact.RetentionDeletionToken = operationToken;
        artifact.RetentionDeletionRequestedAtUtc = now;
        var dispositionId = Guid.NewGuid();
        database.Context.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            Id = dispositionId,
            SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(seeded.ObjectKey),
            SourceObjectKey = seeded.ObjectKey,
            Kind = CentralObjectRecoveryKinds.ExpiredDelete,
            State = CentralObjectRecoveryStates.PendingDelete,
            CentralArtifactId = seeded.ArtifactId,
            OperationToken = operationToken,
            ByteLength = seeded.Payload.LongLength,
            AttemptCount = 1,
            LastAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        var injected = new InvalidOperationException("Injected worker prepare deadlock.");
        var interceptor = new ThrowMatchingReaderInterceptor(
            "[CentralArtifacts] WITH (UPDLOCK, HOLDLOCK)", injected);
        await using var faultContext = CreateContext(database.ConnectionString, interceptor);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var processor = CreateProcessor(
            faultContext, GetFixtureMinio(), telemetry, logger: signals.ProcessorLogger);
        processor.SqlDeadlockClassifier = exception => ReferenceEquals(exception, injected);

        var process = () => processor.ProcessAsync(dispositionId, "worker", CancellationToken.None);
        await process.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Central artifact retention reconcile exhausted SQL deadlock retries.")
            .ConfigureAwait(false);
        interceptor.ThrowCount.Should().Be(4);
        signals.RetryMeasurements.Should().HaveCount(3).And.OnlyContain(item =>
            item.Stage == "reconcile" && item.Outcome == "retry" && item.Origin == "worker");

        database.Context.ChangeTracker.Clear();
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == dispositionId).ConfigureAwait(false))
            .AttemptCount.Should().Be(1);
    }

    [TestMethod]
    public async Task DelayedDelete_HasNoSqlTransactionAllowsWriterAndRecoversStaleRowVersion()
    {
        var applicationName = $"HVO.Retention.Delay.{Guid.NewGuid():N}";
        await using var database = await CreateDatabaseAsync("Delay", applicationName).ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "delay", [5, 6, 7, 8]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var minio = CreateMinio(handler);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        var service = CreateService(database.Context, minio, telemetry);

        var releaseTask = service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await using var probe = new SqlConnection(database.ConnectionString);
        await probe.OpenAsync().ConfigureAwait(false);
        await using (var command = probe.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM sys.dm_exec_sessions AS sessions
                INNER JOIN sys.dm_exec_requests AS requests ON requests.session_id = sessions.session_id
                WHERE sessions.program_name = @applicationName
                  AND requests.open_transaction_count > 0;
                """;
            _ = command.Parameters.AddWithValue("@applicationName", applicationName);
            Convert.ToInt32(
                await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture).Should().Be(0);
        }
        await using (var writer = CreateContext(database.ConnectionString))
        {
            var update = writer.CentralArtifacts.Where(item => item.Id == seeded.ArtifactId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StateReasonCode, "retention.probe"));
            (await update.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)).Should().Be(1);
        }
        handler.Release();
        (await releaseTask.ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Pending);

        await Task.Delay(TimeSpan.FromMilliseconds(1100)).ConfigureAwait(false);
        await using var recoveryContext = CreateContext(database.ConnectionString);
        using var recoveryTelemetry = new CentralArtifactRetentionTelemetry();
        var dispositionId = await recoveryContext.CentralObjectRecoveryDispositions
            .Where(item => item.CentralArtifactId == seeded.ArtifactId)
            .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
        var processor = CreateProcessor(recoveryContext, GetFixtureMinio(), recoveryTelemetry);
        (await processor.ProcessAsync(dispositionId, "worker", CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionProcessResult.Released);
        recoveryContext.ChangeTracker.Clear();
        (await recoveryContext.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == dispositionId).ConfigureAwait(false))
            .AttemptCount.Should().Be(2, "worker replay must retain the prepare attempt");
    }

    [TestMethod]
    public async Task ObjectStoreFaults_RetryTransientAndPersistTerminalFailure()
    {
        await using var database = await CreateDatabaseAsync("Faults").ConfigureAwait(false);
        var transient = await SeedAsync(database.Context, "transient", [9, 10, 11]).ConfigureAwait(false);
        var timedOut = await SeedAsync(database.Context, "timeout", [12, 13]).ConfigureAwait(false);
        var terminal = await SeedAsync(database.Context, "terminal", [12, 13, 14]).ConfigureAwait(false);
        await PutAsync(transient.ObjectKey, transient.Payload).ConfigureAwait(false);
        await PutAsync(timedOut.ObjectKey, timedOut.Payload).ConfigureAwait(false);
        await PutAsync(terminal.ObjectKey, terminal.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();

        using (var transientMinio = CreateMinio(new StatusDeleteHandler(HttpStatusCode.ServiceUnavailable)))
        {
            var result = await CreateService(database.Context, transientMinio, telemetry)
                .ReleaseAsync(transient.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            result.Should().Be(CentralArtifactRetentionResult.Pending);
        }
        database.Context.ChangeTracker.Clear();
        var retry = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == transient.ArtifactId).ConfigureAwait(false);
        retry.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        retry.AttemptCount.Should().Be(1);
        retry.NextAttemptAtUtc.Should().BeAfter(retry.LastAttemptAtUtc!.Value);

        using (var timeoutMinio = CreateMinio(new ExceptionDeleteHandler(new TaskCanceledException("Injected timeout."))))
        {
            var result = await CreateService(database.Context, timeoutMinio, telemetry)
                .ReleaseAsync(timedOut.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            result.Should().Be(CentralArtifactRetentionResult.Pending);
        }
        database.Context.ChangeTracker.Clear();
        var timeoutRetry = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == timedOut.ArtifactId).ConfigureAwait(false);
        timeoutRetry.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        timeoutRetry.AttemptCount.Should().Be(1);
        timeoutRetry.ReasonCode.Should().BeNull();

        using (var terminalMinio = CreateMinio(new StatusDeleteHandler(HttpStatusCode.Forbidden)))
        {
            var result = await CreateService(
                    database.Context, terminalMinio, telemetry, signals.ProcessorLogger, signals.ServiceLogger)
                .ReleaseAsync(terminal.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            result.Should().Be(CentralArtifactRetentionResult.Pending);
        }
        database.Context.ChangeTracker.Clear();
        var failed = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == terminal.ArtifactId).ConfigureAwait(false);
        failed.State.Should().Be(CentralObjectRecoveryStates.Failed);
        failed.NextAttemptAtUtc.Should().BeNull();
        failed.ReasonCode.Should().Be("retention.authorization");
        signals.Logs.Select(item => item.EventId).Should().Contain(2174);
        string.Join('\n', signals.Logs.Select(item => item.Message)).Should().NotContain(terminal.ObjectKey);
    }

    [TestMethod]
    public async Task CancellationAndExactOwnership_LeaveDurablePendingAndRespectBinaryKeys()
    {
        await using var database = await CreateDatabaseAsync("Ownership").ConfigureAwait(false);
        var preCancelled = await SeedAsync(database.Context, "pre-cancelled", [14]).ConfigureAwait(false);
        var cancelled = await SeedAsync(database.Context, "cancelled", [15, 16]).ConfigureAwait(false);
        await PutAsync(cancelled.ObjectKey, cancelled.Payload).ConfigureAwait(false);
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var minio = CreateMinio(handler);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using (var beforeReservation = new CancellationTokenSource())
        {
            await beforeReservation.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await CreateService(database.Context, minio, telemetry)
                    .ReleaseAsync(preCancelled.ArtifactId, beforeReservation.Token).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        database.Context.ChangeTracker.Clear();
        (await database.Context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == preCancelled.ArtifactId)
            .ConfigureAwait(false)).RetentionDeletionToken.Should().BeNull();
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .AnyAsync(item => item.CentralArtifactId == preCancelled.ArtifactId).ConfigureAwait(false)).Should().BeFalse();

        using var cancellation = new CancellationTokenSource();
        var release = CreateService(database.Context, minio, telemetry)
            .ReleaseAsync(cancelled.ArtifactId, cancellation.Token);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        handler.Release();
        (await release.ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Pending);
        database.Context.ChangeTracker.Clear();
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == cancelled.ArtifactId).ConfigureAwait(false))
            .State.Should().Be(CentralObjectRecoveryStates.PendingDelete);

        var shared = await SeedAsync(database.Context, "shared", [17]).ConfigureAwait(false);
        _ = await SeedAsync(database.Context, "shared-owner", [17], shared.StorageReference).ConfigureAwait(false);
        var caseDistinct = await SeedAsync(
            database.Context,
            "case-distinct",
            [18],
            CentralObjectOwnershipFence.BucketPrefix + shared.ObjectKey.ToUpperInvariant()).ConfigureAwait(false);
        await PutAsync(shared.ObjectKey, shared.Payload).ConfigureAwait(false);
        await PutAsync(caseDistinct.ObjectKey, caseDistinct.Payload).ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();
        var fixtureService = CreateService(database.Context, GetFixtureMinio(), telemetry);
        (await fixtureService.ReleaseAsync(shared.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Held);
        (await fixtureService.ReleaseAsync(caseDistinct.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
    }

    [TestMethod]
    public async Task AppliedDeleteWithLostResponse_ReplaysMissingObjectToCompletion()
    {
        await using var database = await CreateDatabaseAsync("ResponseLost").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "response-lost", [21, 22, 23]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using (var minio = CreateMinio(new ResponseLostDeleteHandler { InnerHandler = new SocketsHttpHandler() }))
        {
            (await CreateService(database.Context, minio, telemetry)
                .ReleaseAsync(seeded.ArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Pending);
        }

        await AssertMissingAsync(seeded.ObjectKey).ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();
        var disposition = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false);
        disposition.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        await database.Context.CentralObjectRecoveryDispositions.Where(item => item.Id == disposition.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);

        (await CreateProcessor(database.Context, GetFixtureMinio(), telemetry)
            .ProcessAsync(disposition.Id, "worker", CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionProcessResult.Released);
        database.Context.ChangeTracker.Clear();
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == disposition.Id).ConfigureAwait(false))
            .State.Should().Be(CentralObjectRecoveryStates.Completed);
    }

    [TestMethod]
    public async Task FinalizationCommitAppliedThenThrows_ProbesAfterUnwindAndReturnsReleased()
    {
        await using var database = await CreateDatabaseAsync("CommitAmbiguity").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "commit-ambiguity", [41, 42, 43]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        var interceptor = new ThrowAfterCommittedInterceptor(commitNumber: 2);
        await using var faultContext = CreateContext(database.ConnectionString, interceptor);
        using var telemetry = new CentralArtifactRetentionTelemetry();

        (await CreateService(faultContext, GetFixtureMinio(), telemetry)
            .ReleaseAsync(seeded.ArtifactId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        interceptor.Triggered.Should().BeTrue();

        await using var verifier = CreateContext(database.ConnectionString);
        var disposition = await verifier.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false);
        var artifact = await verifier.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        disposition.State.Should().Be(CentralObjectRecoveryStates.Completed);
        disposition.CompletedAtUtc.Should().NotBeNull();
        artifact.RetentionDeletionCompletedAtUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task FinalizationCommitDeadlock_RetriesOnlyFinalizationAndDeletesOnce()
    {
        await using var database = await CreateDatabaseAsync("FinalizeCommitDeadlock").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "finalize-commit-deadlock", [58, 59, 60]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        var injected = new InvalidOperationException("Injected finalization commit deadlock.");
        var interceptor = new ThrowSpecificBeforeCommitInterceptor(commitNumber: 2, injected);
        await using var faultContext = CreateContext(database.ConnectionString, interceptor);
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var minio = CreateMinio(handler);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var references = new CentralArtifactRetentionReferences(faultContext);
        var processor = CreateProcessor(faultContext, minio, telemetry, references, signals.ProcessorLogger);
        processor.SqlDeadlockClassifier = exception => ReferenceEquals(exception, injected);
        var service = new CentralArtifactRetentionService(
            faultContext,
            references,
            processor,
            TimeProvider.System,
            telemetry,
            signals.ServiceLogger);

        var release = service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        handler.Release();
        (await release.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Released);
        interceptor.Triggered.Should().BeTrue();
        handler.DeleteCount.Should().Be(1);
        signals.RetryMeasurements.Should().ContainSingle(item =>
            item.Stage == "finalize" && item.Outcome == "retry" && item.Origin == "request");

        await using var verifier = CreateContext(database.ConnectionString);
        var dispositions = await verifier.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.CentralArtifactId == seeded.ArtifactId).ToArrayAsync().ConfigureAwait(false);
        dispositions.Should().ContainSingle();
        dispositions[0].State.Should().Be(CentralObjectRecoveryStates.Completed);
        dispositions[0].AttemptCount.Should().Be(1);
        (await verifier.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false))
            .RetentionDeletionCompletedAtUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task FinalizationDeadlockExhaustion_RecordsOnlyRetriesActuallyAttemptedAndDeletesOnce()
    {
        await using var database = await CreateDatabaseAsync("FinalizeDeadlockExhaustion").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "finalize-deadlock-exhaustion", [71, 72]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        var injected = new InvalidOperationException("Injected finalization deadlock exhaustion.");
        var interceptor = new ThrowBeforeCommitFromInterceptor(firstCommitNumber: 2, injected);
        await using var faultContext = CreateContext(database.ConnectionString, interceptor);
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var minio = CreateMinio(handler);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        var references = new CentralArtifactRetentionReferences(faultContext);
        var processor = CreateProcessor(faultContext, minio, telemetry, references, signals.ProcessorLogger);
        processor.SqlDeadlockClassifier = exception => ReferenceEquals(exception, injected);
        var service = new CentralArtifactRetentionService(
            faultContext,
            references,
            processor,
            TimeProvider.System,
            telemetry,
            signals.ServiceLogger);

        var release = service.ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        handler.Release();
        var awaitRelease = async () => await release.ConfigureAwait(false);
        await awaitRelease.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Central artifact retention finalize exhausted SQL deadlock retries.")
            .ConfigureAwait(false);
        interceptor.ThrowCount.Should().Be(4);
        handler.DeleteCount.Should().Be(1);
        signals.RetryMeasurements.Should().HaveCount(3).And.OnlyContain(item =>
            item.Stage == "finalize" && item.Outcome == "retry" && item.Origin == "request");

        await using var verifier = CreateContext(database.ConnectionString);
        var disposition = await verifier.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false);
        disposition.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        disposition.AttemptCount.Should().Be(1);
        (await verifier.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false))
            .RetentionDeletionCompletedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public async Task FinalizationPreCommitRollback_FreshProcessorRecoversMissingObject()
    {
        await using var database = await CreateDatabaseAsync("FinalizePreCommit").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "finalize-pre-commit", [44, 45, 46]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        var interceptor = new ThrowBeforeCommitInterceptor(commitNumber: 2);
        using var telemetry = new CentralArtifactRetentionTelemetry();

        await using (var faultContext = CreateContext(database.ConnectionString, interceptor))
        {
            (await CreateService(faultContext, GetFixtureMinio(), telemetry)
                .ReleaseAsync(seeded.ArtifactId, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Pending);
        }
        interceptor.Triggered.Should().BeTrue();
        await AssertMissingAsync(seeded.ObjectKey).ConfigureAwait(false);

        await using var recovery = CreateContext(database.ConnectionString);
        var disposition = await recovery.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.CentralArtifactId == seeded.ArtifactId).ConfigureAwait(false);
        disposition.State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        var artifact = await recovery.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        artifact.RetentionDeletionCompletedAtUtc.Should().BeNull();

        (await CreateProcessor(recovery, GetFixtureMinio(), telemetry)
            .ProcessAsync(disposition.Id, "worker", CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionProcessResult.Released);
        recovery.ChangeTracker.Clear();
        (await recovery.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == disposition.Id).ConfigureAwait(false))
            .State.Should().Be(CentralObjectRecoveryStates.Completed);
        (await recovery.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false))
            .RetentionDeletionCompletedAtUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task StaleTokenCannotFinalizeDeleteAndFreshWorkerConverges()
    {
        await using var database = await CreateDatabaseAsync("StaleToken").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "stale-token", [24, 25, 26]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var minio = CreateMinio(handler);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        var release = CreateService(database.Context, minio, telemetry)
            .ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var replacementToken = Guid.NewGuid();
        Guid dispositionId;
        await using (var writer = CreateContext(database.ConnectionString))
        await using (var transaction = await writer.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            dispositionId = await writer.CentralObjectRecoveryDispositions.AsNoTracking()
                .Where(item => item.CentralArtifactId == seeded.ArtifactId)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            (await writer.CentralArtifacts.Where(item => item.Id == seeded.ArtifactId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.RetentionDeletionToken, replacementToken)).ConfigureAwait(false)).Should().Be(1);
            (await writer.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositionId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.OperationToken, replacementToken)).ConfigureAwait(false)).Should().Be(1);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        handler.Release();
        (await release.ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Pending);

        await using var recovery = CreateContext(database.ConnectionString);
        (await CreateProcessor(recovery, GetFixtureMinio(), telemetry)
            .ProcessAsync(dispositionId, "worker", CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionProcessResult.Released);
        var completed = await recovery.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == dispositionId).ConfigureAwait(false);
        completed.OperationToken.Should().Be(replacementToken);
        completed.State.Should().Be(CentralObjectRecoveryStates.Completed);
    }

    [TestMethod]
    public async Task ConcurrentRequestAndWorker_UseOneDeleteAndBothObserveCompletion()
    {
        await using var database = await CreateDatabaseAsync("Concurrent").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "concurrent", [27, 28, 29]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        using var handler = new BlockingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
        using var minio = CreateMinio(handler);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        var request = CreateService(database.Context, minio, telemetry)
            .ReleaseAsync(seeded.ArtifactId, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Guid dispositionId;
        await using (var reader = CreateContext(database.ConnectionString))
        {
            dispositionId = await reader.CentralObjectRecoveryDispositions.AsNoTracking()
                .Where(item => item.CentralArtifactId == seeded.ArtifactId)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
        }
        await using var workerContext = CreateContext(database.ConnectionString);
        var worker = CreateProcessor(workerContext, minio, telemetry)
            .ProcessAsync(dispositionId, "worker", CancellationToken.None);
        await Task.Delay(100).ConfigureAwait(false);
        worker.IsCompleted.Should().BeFalse();
        handler.Release();

        (await request.ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Released);
        (await worker.ConfigureAwait(false)).Should().Be(CentralArtifactRetentionProcessResult.Released);
        handler.DeleteCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ContextDependencyAndUnassociatedValidationSlot_HoldArtifactOutsideEvent()
    {
        await using var database = await CreateDatabaseAsync("ContextReference").ConfigureAwait(false);
        var source = await SeedAsync(database.Context, "context-source", [30]).ConfigureAwait(false);
        var context = await SeedAsync(database.Context, "context-target", [31]).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var job = AddDerivativeJob(database.Context, source.ArtifactId, now);
        var validation = new CentralTransientValidationJob
        {
            CentralDerivativeJobId = job.Id,
            Job = job,
            AgentId = "retention-context-agent",
            SubmissionSchemaVersion = "v1",
            SubmissionIdentitySha256 = Sha256(Guid.NewGuid()),
            CreatedAtUtc = now
        };
        validation.ContextDependencies.Add(new CentralTransientContextDependency
        {
            Ordinal = 0,
            ContextCentralArtifactId = context.ArtifactId,
            RequestedRecipeIdentitySha256 = Sha256(Guid.NewGuid()),
            ExecutionOptionsIdentitySha256 = Sha256(Guid.NewGuid()),
            CreatedAtUtc = now
        });
        validation.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
        {
            AgentId = "retention-context-agent",
            Ordinal = 0,
            State = CentralTransientValidationIdentitySlotState.Reserved,
            SubmittedEventId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid(),
            ObservationId = Guid.NewGuid(),
            AssessmentId = Guid.NewGuid()
        });
        database.Context.CentralTransientValidationJobs.Add(validation);
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();

        var references = new CentralArtifactRetentionReferences(database.Context);
        (await references.IsHeldAsync(context.ArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().BeTrue();
        (await references.IsHeldOutsideTransientEventAsync(
            context.ArtifactId, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
    }

    [TestMethod]
    public async Task DerivativeReservationBeforeIntent_RejectsWithoutPendingOwner()
    {
        await using var database = await CreateDatabaseAsync("DerivativeIntentRace").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "derivative-source", [44, 45]).ConfigureAwait(false);
        var source = await database.Context.CentralArtifacts.Include(item => item.Frame)
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var leaseToken = Guid.NewGuid();
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Ordinal = 0,
            BindingName = "source",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.Exact,
            ExpectedAgentId = source.Frame!.AgentId,
            ExpectedCentralArtifactId = source.Id,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        var input = new CentralDerivativeJobInput
        {
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Requirement = requirement,
            Ordinal = 0,
            CentralArtifactId = source.Id,
            Artifact = source,
            CompatibilityJson = "{}",
            CompatibilitySha256 = Sha256(Guid.NewGuid()),
            ByteLength = source.ByteLength,
            SelectedAtUtc = now
        };
        requirement.Input = input;
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "retention-race-v1",
            TargetVariant = "retention-race",
            RecipeName = "retention-race",
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = Sha256(Guid.NewGuid()),
            RequestIdentitySha256 = Sha256(Guid.NewGuid()),
            Status = CentralDerivativeJobStatus.Leased,
            AttemptCount = 1,
            MaxAttempts = 3,
            LeaseOwner = "retention-race-worker",
            LeaseToken = leaseToken,
            LeaseAcquiredAtUtc = now,
            LeaseExpiresAtUtc = now.AddMinutes(5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        job.InputRequirements.Add(requirement);
        job.Inputs.Add(input);
        database.Context.CentralDerivativeJobs.Add(job);
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();

        using var options = JsonDocument.Parse("{}");
        var recipeIdentity = new ProcessingRecipeIdentity(
            RecipeIdentityDescriptor.Create("retention-race", "v1", "v1", options.RootElement),
            Sha256(Guid.NewGuid()));
        ReadOnlyMemory<byte> payload = new byte[] { 46, 47 };
        var product = new ProcessingProduct(
            FrameArtifactRole.Preview,
            "retention-race",
            Sha256(Guid.NewGuid()),
            "image/png",
            null,
            payload,
            Convert.ToHexString(SHA256.HashData(payload.Span)),
            recipeIdentity,
            [],
            [source.ArtifactId],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"));
        var lease = new CentralDerivativeJobLease(
            job.Id,
            leaseToken,
            job.LeaseOwner,
            job.LeaseExpiresAtUtc!.Value,
            source.Frame.DevicePublicId,
            source.ArtifactId,
            source.Role,
            source.RecipeVersion,
            source.StorageReference,
            source.ChecksumSha256,
            source.MediaType,
            source.Frame.FrameId,
            source.Frame.AgentId,
            source.Frame.CapturedAtUtc,
            source.Frame.RigProfileVersion,
            source.Frame.SceneProvenanceJson,
            job.TargetRole,
            job.TargetRecipeVersion,
            job.TargetVariant!,
            job.RecipeName,
            job.RecipeOptionsJson,
            job.InputSelectorJson,
            job.RequestedRecipeIdentitySha256,
            job.RequestIdentitySha256,
            null,
            null,
            job.AttemptCount,
            job.MaxAttempts);
        var storageReference = CentralDerivativeOutputWriter.CreateStorageReference(lease, product);
        await using var blockerContext = CreateContext(database.ConnectionString);
        var blocker = await CentralObjectApplicationLock.AcquireAsync(
            blockerContext, storageReference, CancellationToken.None).ConfigureAwait(false);
        Guid tombstoneArtifactId = default;
        Guid tombstoneDispositionId = default;
        try
        {
            await using var publisherContext = CreateContext(database.ConnectionString);
            var writer = new CentralDerivativeOutputWriter(
                publisherContext,
                GetFixtureMinio(),
                AssemblyHooks.Fixture.Factory.Services.GetRequiredService<ICentralArtifactObjectReader>(),
                AssemblyHooks.Fixture.Factory.Services.GetRequiredService<CentralDerivativeWorkerTelemetry>(),
                TimeProvider.System,
                NullLogger<CentralDerivativeOutputWriter>.Instance);
            var publication = writer.PersistAsync(lease, product, source.ByteLength, TimeSpan.Zero, CancellationToken.None);
            await Task.Delay(100).ConfigureAwait(false);
            publication.IsCompleted.Should().BeFalse();

            await using (var tombstoneContext = CreateContext(database.ConnectionString))
            {
                var operationToken = Guid.NewGuid();
                var tombstone = new CentralArtifact
                {
                    CentralFrameId = source.CentralFrameId,
                    DevicePublicId = source.DevicePublicId,
                    ArtifactId = Guid.NewGuid(),
                    Role = FrameArtifactRole.Metadata,
                    RecipeVersion = "retention-race-v1",
                    ManifestSchemaVersion = "central-v1",
                    MediaType = "application/octet-stream",
                    ByteLength = 1,
                    ChecksumSha256 = new string('B', 64),
                    StorageReference = storageReference,
                    ReceivedAtUtc = now,
                    IdempotencyKey = Sha256(Guid.NewGuid()),
                    ObjectState = CentralArtifactObjectState.Expired,
                    ReconstructionState = CentralReconstructionState.Complete,
                    RetentionDeletionToken = operationToken,
                    RetentionDeletionRequestedAtUtc = now
                };
                var objectKey = storageReference[CentralObjectOwnershipFence.BucketPrefix.Length..];
                var disposition = new CentralObjectRecoveryDisposition
                {
                    SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey),
                    SourceObjectKey = objectKey,
                    Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                    State = CentralObjectRecoveryStates.PendingDelete,
                    CentralArtifactId = tombstone.Id,
                    OperationToken = operationToken,
                    ByteLength = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    NextAttemptAtUtc = now
                };
                tombstoneContext.AddRange(tombstone, disposition);
                await tombstoneContext.SaveChangesAsync().ConfigureAwait(false);
                tombstoneArtifactId = tombstone.Id;
                tombstoneDispositionId = disposition.Id;
                (await tombstoneContext.CentralArtifactProcessingEvidence.CountAsync(item =>
                    item.CentralDerivativeJobId == job.Id).ConfigureAwait(false)).Should().Be(0);
            }
            await blocker.DisposeAsync().ConfigureAwait(false);
            var rejected = async () => await publication.ConfigureAwait(false);
            await rejected.Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
            (await database.Context.CentralArtifactProcessingEvidence.AsNoTracking().CountAsync(item =>
                item.CentralDerivativeJobId == job.Id).ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await blocker.DisposeAsync().ConfigureAwait(false);
            if (tombstoneDispositionId != Guid.Empty)
            {
                await using var cleanup = CreateContext(database.ConnectionString);
                await cleanup.CentralObjectRecoveryDispositions.Where(item => item.Id == tombstoneDispositionId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await cleanup.CentralArtifacts.Where(item => item.Id == tombstoneArtifactId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    public async Task PublicReleaseAndRetentionRace_ConvergesWithoutReleasedExpiredArtifact()
    {
        await using var database = await CreateDatabaseAsync("PublicRace").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "public-race", [32, 33, 34]).ConfigureAwait(false);
        await PutAsync(seeded.ObjectKey, seeded.Payload).ConfigureAwait(false);
        var owner = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = $"retention-owner-{Guid.NewGuid():N}",
            NormalizedUserName = $"RETENTION-OWNER-{Guid.NewGuid():N}"
        };
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = "Retention publication race",
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        observatory.Memberships.Add(new ObservatoryMembership
        {
            UserId = owner.Id,
            User = owner,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = DateTimeOffset.UtcNow
        });
        var artifact = await database.Context.CentralArtifacts.Include(item => item.Frame)
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        artifact.Role = FrameArtifactRole.Preview;
        artifact.MediaType = "image/png";
        artifact.Frame!.ObservatoryId = observatory.Id;
        database.Context.Observatories.Add(observatory);
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        database.Context.ChangeTracker.Clear();

        await using var publicationContext = CreateContext(database.ConnectionString);
        await using var retentionContext = CreateContext(database.ConnectionString);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        var publicationService = new PublicRecordPublicationService(
            publicationContext,
            TimeProvider.System,
            NullLogger<PublicRecordPublicationService>.Instance);
        var retentionService = CreateService(retentionContext, GetFixtureMinio(), telemetry);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = RunAfterGateAsync(gate.Task, () => publicationService.DecideAsync(
            observatory.Id,
            owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, seeded.ArtifactId),
            PublicationDecisionState.Released,
            "public-image-v1",
            "retention-race"));
        var release = RunAfterGateAsync(gate.Task, () => retentionService.ReleaseAsync(
            seeded.ArtifactId, CancellationToken.None));
        gate.SetResult();
        await Task.WhenAll(publication, release).ConfigureAwait(false);
        var publicationResult = await publication.ConfigureAwait(false);
        var releaseResult = await release.ConfigureAwait(false);

        await using var verifier = CreateContext(database.ConnectionString);
        var finalArtifact = await verifier.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ArtifactId).ConfigureAwait(false);
        var releasedPublicly = await verifier.PublicRecordPublicationDecisions.AsNoTracking().AnyAsync(item =>
            item.CentralArtifactId == seeded.ArtifactId
            && item.State == PublicationDecisionState.Released).ConfigureAwait(false);
        if (publicationResult.Outcome == PublicRecordPublicationOutcome.Applied)
        {
            releaseResult.Should().Be(CentralArtifactRetentionResult.Held);
            releasedPublicly.Should().BeTrue();
            finalArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        }
        else
        {
            publicationResult.Outcome.Should().Be(PublicRecordPublicationOutcome.NotFoundOrDenied);
            releaseResult.Should().Be(CentralArtifactRetentionResult.Released);
            releasedPublicly.Should().BeFalse();
            finalArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
        }
    }

    [TestMethod]
    public async Task Reconciliation_ReacquiresChangedExactStorageReference()
    {
        await using var database = await CreateDatabaseAsync("ReferenceLock").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "reference-lock", [35]).ConfigureAwait(false);
        var replacementReference = $"{CentralObjectOwnershipFence.BucketPrefix}artifacts/retention-tests/{Guid.NewGuid():N}/replacement.bin";
        await using var blockerContext = CreateContext(database.ConnectionString);
        var blocker = await CentralObjectApplicationLock.AcquireAsync(
            blockerContext, seeded.StorageReference, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await using var reconciliationContext = CreateContext(database.ConnectionString);
            var artifact = await reconciliationContext.CentralArtifacts.SingleAsync(item => item.Id == seeded.ArtifactId)
                .ConfigureAwait(false);
            var acquiring = CentralArtifactReconciliationService.AcquireCurrentObjectLockAsync(
                reconciliationContext, artifact, CancellationToken.None);
            await Task.Delay(100).ConfigureAwait(false);
            acquiring.IsCompleted.Should().BeFalse();
            await using (var writer = CreateContext(database.ConnectionString))
            {
                (await writer.CentralArtifacts.Where(item => item.Id == seeded.ArtifactId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        item => item.StorageReference, replacementReference)).ConfigureAwait(false)).Should().Be(1);
            }
            await blocker.DisposeAsync().ConfigureAwait(false);
            await using var currentLock = await acquiring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            artifact.StorageReference.Should().Be(replacementReference);

            await using var contenderContext = CreateContext(database.ConnectionString);
            var contender = CentralObjectApplicationLock.AcquireAsync(
                contenderContext, replacementReference, CancellationToken.None);
            await Task.Delay(100).ConfigureAwait(false);
            contender.IsCompleted.Should().BeFalse("reconciliation must hold the reloaded exact key");
            await currentLock.DisposeAsync().ConfigureAwait(false);
            await using var acquired = await contender.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            await blocker.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task Worker_FreshProvidersDrainOnlyDueRowsAndRecordActualOutcome()
    {
        await using var database = await CreateDatabaseAsync("WorkerDrain").ConfigureAwait(false);
        var first = await SeedAsync(database.Context, "worker-first", [36, 37]).ConfigureAwait(false);
        var second = await SeedAsync(database.Context, "worker-second", [38, 39, 40]).ConfigureAwait(false);
        await PutAsync(first.ObjectKey, first.Payload).ConfigureAwait(false);
        await PutAsync(second.ObjectKey, second.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using var signals = new RetentionSignalCollector();
        using (var unavailable = CreateMinio(new StatusDeleteHandler(HttpStatusCode.ServiceUnavailable)))
        {
            (await CreateService(database.Context, unavailable, telemetry)
                .ReleaseAsync(first.ArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Pending);
            (await CreateService(database.Context, unavailable, telemetry)
                .ReleaseAsync(second.ArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Pending);
        }
        database.Context.ChangeTracker.Clear();
        var dispositions = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.CentralArtifactId == first.ArtifactId || item.CentralArtifactId == second.ArtifactId)
            .ToDictionaryAsync(item => item.CentralArtifactId!.Value).ConfigureAwait(false);
        await database.Context.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositions[first.ArtifactId].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
        await database.Context.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositions[second.ArtifactId].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddHours(1))).ConfigureAwait(false);

        await using (var services = CreateWorkerServices(database.ConnectionString, telemetry))
        {
            var worker = CreateWorker(services, telemetry, signals.WorkerLogger);
            (await worker.ProcessDueAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(1);
        }
        await AssertMissingAsync(first.ObjectKey).ConfigureAwait(false);
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == dispositions[first.ArtifactId].Id).ConfigureAwait(false))
            .State.Should().Be(CentralObjectRecoveryStates.Completed);
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleAsync(item => item.Id == dispositions[second.ArtifactId].Id).ConfigureAwait(false))
            .State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        var firstCompletedAt = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.Id == dispositions[first.ArtifactId].Id)
            .Select(item => item.CompletedAtUtc).SingleAsync().ConfigureAwait(false);

        await using (var services = CreateWorkerServices(database.ConnectionString, telemetry))
        {
            (await CreateWorker(services, telemetry, signals.WorkerLogger)
                .ProcessDueAsync(CancellationToken.None).ConfigureAwait(false))
            .Should().Be(0);
        }

        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.Id == dispositions[first.ArtifactId].Id)
            .Select(item => item.CompletedAtUtc).SingleAsync().ConfigureAwait(false)).Should().Be(firstCompletedAt);
        signals.RecoveryMeasurements.Should().Contain(item => item.Value == 1 && item.Outcome == "deleted");

        await database.Context.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositions[second.ArtifactId].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
        await using (var services = CreateWorkerServices(database.ConnectionString, telemetry))
        {
            (await CreateWorker(services, telemetry, signals.WorkerLogger)
                .ProcessDueAsync(CancellationToken.None).ConfigureAwait(false))
                .Should().Be(1);
        }
        await AssertMissingAsync(second.ObjectKey).ConfigureAwait(false);
        (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .CountAsync(item => item.State == CentralObjectRecoveryStates.PendingDelete).ConfigureAwait(false))
            .Should().Be(0);
        signals.Logs.Should().Contain(item => item.EventId == 2173
            && item.Message.Contains("Count=1", StringComparison.Ordinal)
            && item.Message.Contains("Bytes=2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Worker_FirstItemLockTimeoutDoesNotStarveSecondDueDeletion()
    {
        await using var database = await CreateDatabaseAsync("WorkerIsolation").ConfigureAwait(false);
        var locked = await SeedAsync(database.Context, "worker-locked", [48, 49]).ConfigureAwait(false);
        var drainable = await SeedAsync(database.Context, "worker-drainable", [50, 51, 52]).ConfigureAwait(false);
        await PutAsync(locked.ObjectKey, locked.Payload).ConfigureAwait(false);
        await PutAsync(drainable.ObjectKey, drainable.Payload).ConfigureAwait(false);
        using var telemetry = new CentralArtifactRetentionTelemetry();
        using (var unavailable = CreateMinio(new StatusDeleteHandler(HttpStatusCode.ServiceUnavailable)))
        {
            (await CreateService(database.Context, unavailable, telemetry)
                .ReleaseAsync(locked.ArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Pending);
            (await CreateService(database.Context, unavailable, telemetry)
                .ReleaseAsync(drainable.ArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Pending);
        }
        database.Context.ChangeTracker.Clear();
        var dispositionIds = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.CentralArtifactId == locked.ArtifactId
                || item.CentralArtifactId == drainable.ArtifactId)
            .ToDictionaryAsync(item => item.CentralArtifactId!.Value, item => item.Id).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await database.Context.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositionIds[locked.ArtifactId])
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, now.AddSeconds(-2)))
            .ConfigureAwait(false);
        await database.Context.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositionIds[drainable.ArtifactId])
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, now.AddSeconds(-1)))
            .ConfigureAwait(false);

        await using var blockerContext = CreateContext(database.ConnectionString);
        await using var blocker = await CentralObjectApplicationLock.AcquireAsync(
            blockerContext, locked.StorageReference, CancellationToken.None).ConfigureAwait(false);
        await using (var services = CreateWorkerServices(database.ConnectionString, telemetry))
        {
            (await CreateWorker(
                    services,
                    telemetry,
                    NullLogger<CentralArtifactRetentionWorker>.Instance)
                .ProcessDueAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false)).Should().Be(2);
        }

        await AssertMissingAsync(drainable.ObjectKey).ConfigureAwait(false);
        var states = await database.Context.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.Id == dispositionIds[locked.ArtifactId]
                || item.Id == dispositionIds[drainable.ArtifactId])
            .ToDictionaryAsync(item => item.Id).ConfigureAwait(false);
        states[dispositionIds[locked.ArtifactId]].State.Should().Be(CentralObjectRecoveryStates.PendingDelete);
        states[dispositionIds[locked.ArtifactId]].NextAttemptAtUtc.Should().BeAfter(now);
        states[dispositionIds[drainable.ArtifactId]].State.Should().Be(CentralObjectRecoveryStates.Completed);
    }

    [TestMethod]
    public async Task Health_ReportsFreshStaleFailedAndDrainedRetentionWork()
    {
        await using var database = await CreateDatabaseAsync("Health").ConfigureAwait(false);
        var seeded = await SeedAsync(database.Context, "health", [19, 20]).ConfigureAwait(false);
        var token = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var artifact = await database.Context.CentralArtifacts.SingleAsync(item => item.Id == seeded.ArtifactId)
            .ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Expired;
        artifact.RetentionDeletionToken = token;
        artifact.RetentionDeletionRequestedAtUtc = now;
        database.Context.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(seeded.ObjectKey),
            SourceObjectKey = seeded.ObjectKey,
            Kind = CentralObjectRecoveryKinds.ExpiredDelete,
            State = CentralObjectRecoveryStates.PendingDelete,
            CentralArtifactId = artifact.Id,
            OperationToken = token,
            ByteLength = artifact.ByteLength,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextAttemptAtUtc = now
        });
        await database.Context.SaveChangesAsync().ConfigureAwait(false);
        var health = new CentralArtifactRetentionHealthCheck(
            database.Context,
            TimeProvider.System,
            Options.Create(new CentralArtifactRetentionOptions()));
        var context = new HealthCheckContext();
        (await health.CheckHealthAsync(context).ConfigureAwait(false)).Status.Should().Be(HealthStatus.Healthy);

        await database.Context.CentralArtifacts.Where(item => item.Id == seeded.ArtifactId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RetentionDeletionRequestedAtUtc, now.AddMinutes(-16)))
            .ConfigureAwait(false);
        (await health.CheckHealthAsync(context).ConfigureAwait(false)).Status.Should().Be(HealthStatus.Degraded);

        await database.Context.CentralObjectRecoveryDispositions.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.State, CentralObjectRecoveryStates.Failed)
            .SetProperty(item => item.AttemptCount, 1)
            .SetProperty(item => item.LastAttemptAtUtc, now)
            .SetProperty(item => item.NextAttemptAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.ReasonCode, "retention.authorization")).ConfigureAwait(false);
        (await health.CheckHealthAsync(context).ConfigureAwait(false)).Status.Should().Be(HealthStatus.Unhealthy);

        await database.Context.CentralObjectRecoveryDispositions.ExecuteDeleteAsync().ConfigureAwait(false);
        (await health.CheckHealthAsync(context).ConfigureAwait(false)).Status.Should().Be(HealthStatus.Healthy);
    }

    private static CentralArtifactRetentionService CreateService(
        ApplicationDbContext db,
        IMinioClient minio,
        CentralArtifactRetentionTelemetry telemetry,
        ILogger<CentralArtifactRetentionProcessor>? logger = null,
        ILogger<CentralArtifactRetentionService>? serviceLogger = null)
    {
        var references = new CentralArtifactRetentionReferences(db);
        return new(
            db,
            references,
            CreateProcessor(db, minio, telemetry, references, logger),
            TimeProvider.System,
            telemetry,
            serviceLogger ?? NullLogger<CentralArtifactRetentionService>.Instance);
    }

    private static CentralArtifactRetentionProcessor CreateProcessor(
        ApplicationDbContext db,
        IMinioClient minio,
        CentralArtifactRetentionTelemetry telemetry,
        CentralArtifactRetentionReferences? references = null,
        ILogger<CentralArtifactRetentionProcessor>? logger = null)
        => new(
            db,
            references ?? new CentralArtifactRetentionReferences(db),
            minio,
            TimeProvider.System,
            telemetry,
            logger ?? NullLogger<CentralArtifactRetentionProcessor>.Instance);

    private static async Task<SeededArtifact> SeedAsync(
        ApplicationDbContext db,
        string scenario,
        byte[] payload,
        string? storageReference = null)
    {
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = $"retention-{scenario}-{Guid.NewGuid():N}",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            FirstReceivedAtUtc = DateTimeOffset.UtcNow
        };
        var objectKey = storageReference is null
            ? $"artifacts/retention-tests/{Guid.NewGuid():N}/{scenario}.bin"
            : storageReference[CentralObjectOwnershipFence.BucketPrefix.Length..];
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "retention-test-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = payload.LongLength,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload)),
            StorageReference = storageReference ?? $"{CentralObjectOwnershipFence.BucketPrefix}{objectKey}",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(artifact);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(artifact.Id, artifact.StorageReference, objectKey, payload);
    }

    private static CentralDerivativeJob AddDerivativeJob(
        ApplicationDbContext db,
        Guid sourceCentralArtifactId,
        DateTimeOffset now)
    {
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = sourceCentralArtifactId,
            TargetRole = FrameArtifactRole.Metadata,
            TargetRecipeVersion = "retention-context-v1",
            TargetVariant = "retention-context",
            RecipeName = "retention-context",
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = Sha256(Guid.NewGuid()),
            ExpectedRecipeIdentitySha256 = Sha256(Guid.NewGuid()),
            RequestIdentitySha256 = Sha256(Guid.NewGuid()),
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = 3,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CentralDerivativeJobs.Add(job);
        return job;
    }

    private static string Sha256(Guid value) => Convert.ToHexString(SHA256.HashData(value.ToByteArray()));

    private static ServiceProvider CreateWorkerServices(
        string connectionString,
        CentralArtifactRetentionTelemetry telemetry)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddSingleton<IMinioClient>(GetFixtureMinio());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(telemetry);
        services.AddScoped<ICentralArtifactRetentionReferences, CentralArtifactRetentionReferences>();
        services.AddScoped<CentralArtifactRetentionProcessor>();
        services.AddScoped<ICentralArtifactRetentionProcessor>(provider =>
            provider.GetRequiredService<CentralArtifactRetentionProcessor>());
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateReconciliationServices(
        string connectionString,
        IInterceptor interceptor)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor));
        services.AddSingleton<IMinioClient>(GetFixtureMinio());
        return services.BuildServiceProvider();
    }

    private static CentralArtifactRetentionWorker CreateWorker(
        ServiceProvider services,
        CentralArtifactRetentionTelemetry telemetry,
        ILogger<CentralArtifactRetentionWorker> logger)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Options.Create(new CentralArtifactRetentionOptions()),
            telemetry,
            logger);

    private static async Task<T> RunAfterGateAsync<T>(Task gate, Func<Task<T>> action)
    {
        await gate.ConfigureAwait(false);
        return await action().ConfigureAwait(false);
    }

    private static async Task PutAsync(string objectKey, byte[] payload)
    {
        var minio = GetFixtureMinio();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }
        await using var stream = new MemoryStream(payload, writable: false);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
            .WithStreamData(stream).WithObjectSize(payload.LongLength)).ConfigureAwait(false);
    }

    private static async Task AssertMissingAsync(string objectKey)
    {
        var action = () => GetFixtureMinio().StatObjectAsync(
            new StatObjectArgs().WithBucket(Bucket).WithObject(objectKey));
        await action.Should().ThrowAsync<MinioException>().ConfigureAwait(false);
    }

    private static IMinioClient GetFixtureMinio()
        => AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();

    private static IMinioClient CreateMinio(HttpMessageHandler handler)
        => new MinioClient()
            .WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
            .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
            .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
            .Build();

    private static async Task<RetentionDatabase> CreateDatabaseAsync(string scenario, string? applicationName = null)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorRetention{scenario}_{Guid.NewGuid():N}",
            ApplicationName = applicationName ?? $"HVO.Retention.{scenario}.{Guid.NewGuid():N}"
        };
        var context = CreateContext(builder.ConnectionString);
        await context.Database.MigrateAsync().ConfigureAwait(false);
        return new(context, builder.ConnectionString);
    }

    private static ApplicationDbContext CreateContext(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptors.Length != 0)
        {
            options.AddInterceptors(interceptors);
        }
        return new(options.Options);
    }

    private sealed record SeededArtifact(
        Guid ArtifactId,
        string StorageReference,
        string ObjectKey,
        byte[] Payload);

    private sealed class RetentionDatabase(ApplicationDbContext context, string connectionString) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            await Context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class BlockingDeleteHandler : DelegatingHandler
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;
        public int DeleteCount { get; private set; }

        public void Release() => release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                DeleteCount++;
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ExceptionDeleteHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => request.Method == HttpMethod.Delete
                ? Task.FromException<HttpResponseMessage>(exception)
                : throw new InvalidOperationException("The fault client only supports DELETE.");
    }

    private sealed class ResponseLostDeleteHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method == HttpMethod.Delete)
            {
                response.Dispose();
                throw new HttpRequestException("Injected response loss after DELETE was applied.");
            }
            return response;
        }
    }

    private sealed class ThrowAfterCommittedInterceptor(int commitNumber) : DbTransactionInterceptor
    {
        private int commits;

        public bool Triggered { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == commitNumber)
            {
                Triggered = true;
                throw new InvalidOperationException("Injected response loss after transaction commit.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowBeforeCommitInterceptor(int commitNumber) : DbTransactionInterceptor
    {
        private int commits;

        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == commitNumber)
            {
                Triggered = true;
                throw new InvalidOperationException("Injected retention finalization pre-commit rollback.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowSpecificBeforeCommitInterceptor(int commitNumber, Exception exception)
        : DbTransactionInterceptor
    {
        private int commits;

        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == commitNumber)
            {
                Triggered = true;
                throw exception;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowBeforeCommitFromInterceptor(int firstCommitNumber, Exception exception)
        : DbTransactionInterceptor
    {
        private int commits;
        private int throwCount;

        public int ThrowCount => Volatile.Read(ref throwCount);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) >= firstCommitNumber)
            {
                Interlocked.Increment(ref throwCount);
                throw exception;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowMatchingReaderInterceptor(string marker, Exception exception) : DbCommandInterceptor
    {
        private int throwCount;

        public int ThrowCount => Volatile.Read(ref throwCount);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref throwCount);
                throw exception;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommandShapeInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> commands = new();

        public IReadOnlyCollection<string> Commands => commands.ToArray();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReconciliationCandidateObserver : DbCommandInterceptor
    {
        private readonly TaskCompletionSource legacyCandidateRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LegacyCandidateRead => legacyCandidateRead.Task;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("OperationToken] IS NULL", StringComparison.Ordinal)
                && command.CommandText.Contains("[UpdatedAtUtc]", StringComparison.Ordinal)
                && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal))
            {
                legacyCandidateRead.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }


    private sealed class StatusDeleteHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Delete)
            {
                throw new InvalidOperationException("The fault client only supports DELETE.");
            }
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent($"""
                    <Error>
                      <Code>{(statusCode == HttpStatusCode.Forbidden ? "AccessDenied" : "ServiceUnavailable")}</Code>
                      <Message>Injected retention test failure.</Message>
                      <Resource>/skymonitor-artifacts/test</Resource>
                      <RequestId>bounded-test-request</RequestId>
                      <HostId>bounded-test-host</HostId>
                    </Error>
                    """, System.Text.Encoding.UTF8, "application/xml")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RetentionSignalCollector : IDisposable
    {
        private readonly MeterListener meter = new();
        private readonly ActivityListener activities;

        public RetentionSignalCollector()
        {
            meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralArtifactRetentionTelemetry.MeterName)
                {
                    Instruments.Add(instrument.Name);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                RecordMetricTags(tags);
                if (instrument.Name == "skymonitor.central.retention.recovery")
                {
                    RecoveryMeasurements.Add(new(value, GetTag(tags, "outcome")));
                }
            });
            meter.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            {
                RecordMetricTags(tags);
                if (instrument.Name == "skymonitor.central.retention.stage.duration"
                    && GetTag(tags, "outcome") == "retry")
                {
                    RetryMeasurements.Add(new(
                        GetTag(tags, "stage"), GetTag(tags, "outcome"), GetTag(tags, "origin")));
                }
            });
            meter.Start();
            activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CentralIngestTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    Activities.Add(activity.OperationName);
                    foreach (var tag in activity.Tags)
                    {
                        ActivityTagKeys.Add(tag.Key);
                    }
                }
            };
            ActivitySource.AddActivityListener(activities);
        }

        public HashSet<string> Instruments { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MetricTagKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ActivityTagKeys { get; } = new(StringComparer.Ordinal);
        public List<RecoveryMeasurement> RecoveryMeasurements { get; } = [];
        public List<RetryMeasurement> RetryMeasurements { get; } = [];
        public List<string> Activities { get; } = [];
        public List<RetentionLog> Logs { get; } = [];
        public ILogger<CentralArtifactRetentionProcessor> ProcessorLogger => new CollectingLogger(Logs);
        public ILogger<CentralArtifactRetentionService> ServiceLogger => new CollectingLogger(Logs);
        public ILogger<CentralArtifactRetentionWorker> WorkerLogger => new CollectingLogger(Logs);

        public void Dispose()
        {
            activities.Dispose();
            meter.Dispose();
        }

        private void RecordMetricTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                MetricTagKeys.Add(tag.Key);
            }
        }

        private static string? GetTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == key)
                {
                    return tag.Value?.ToString();
                }
            }
            return null;
        }

        private sealed class CollectingLogger(List<RetentionLog> logs) :
            ILogger<CentralArtifactRetentionProcessor>,
            ILogger<CentralArtifactRetentionService>,
            ILogger<CentralArtifactRetentionWorker>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fieldNames = state is IEnumerable<KeyValuePair<string, object?>> fields
                    ? fields.Select(item => item.Key).Where(item => item != "{OriginalFormat}").ToArray()
                    : [];
                logs.Add(new(eventId.Id, formatter(state, exception), fieldNames));
            }
        }
    }

    private sealed class RetentionRetryCollector : IDisposable
    {
        private readonly MeterListener meter = new();
        private readonly ConcurrentQueue<RetryMeasurement> measurements = new();

        public RetentionRetryCollector()
        {
            meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralArtifactRetentionTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.retention.stage.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                string? stage = null;
                string? outcome = null;
                string? origin = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "stage") stage = tag.Value?.ToString();
                    if (tag.Key == "outcome") outcome = tag.Value?.ToString();
                    if (tag.Key == "origin") origin = tag.Value?.ToString();
                }
                if (outcome == "retry")
                {
                    measurements.Enqueue(new(stage, outcome, origin));
                }
            });
            meter.Start();
        }

        public IReadOnlyCollection<RetryMeasurement> Measurements => measurements.ToArray();

        public void Dispose() => meter.Dispose();
    }

    private sealed record RetentionLog(int EventId, string Message, IReadOnlyList<string> FieldNames);
    private sealed record RecoveryMeasurement(long Value, string? Outcome);
    private sealed record RetryMeasurement(string? Stage, string? Outcome, string? Origin);
}
