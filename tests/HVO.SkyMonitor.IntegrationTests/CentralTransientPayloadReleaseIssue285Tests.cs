using System.Net;
using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralTransientEventPersistenceIntegrationTests
{
    [TestMethod]
    public async Task PayloadReleaseCreation_C4CompletesWithoutCallerRetryOrDuplicateRows()
    {
        await using var database = CreateDatabase("Issue285NaturalC4");
        var contexts = new List<ApplicationDbContext>();
        try
        {
            var seeds = new List<Issue285EventSeed>();
            for (var index = 0; index < 4; index++)
            {
                seeds.Add(await SeedIssue285EventAsync(database.Context).ConfigureAwait(false));
            }
            var services = new List<CentralTransientPayloadReleaseService>();
            var handlers = new List<Issue285RejectingHttpHandler>();
            using var telemetry = new Issue285TelemetryCollector();
            var creationBarrier = new Issue285AsyncBarrier(4);
            for (var index = 0; index < seeds.Count; index++)
            {
                var context = CreateContext(database.ConnectionString);
                contexts.Add(context);
                var handler = new Issue285RejectingHttpHandler();
                handlers.Add(handler);
                var service = CreateIssue285Service(
                    context, handler, seeds[index].ArtifactIds, telemetry.Telemetry);
                service.CreationConcurrencyHook = (_, _, token) => creationBarrier.SignalAndWaitAsync(token);
                services.Add(service);
            }

            var results = await Task.WhenAll(services.Select((service, index) => service.ReleaseAsync(
                CreateOwnerPrincipal($"issue-285-c4-{index}", admin: true),
                seeds[index].EventId,
                seeds[index].RowVersion,
                $"issue-285-c4-{index}",
                CancellationToken.None))).ConfigureAwait(false);

            results.Should().OnlyContain(result => result.Status == CentralTransientPayloadReleaseStatus.Released);
            results.Select(result => result.Response!.ReleaseId).Should().OnlyHaveUniqueItems();
            handlers.Should().OnlyContain(handler => handler.RequestCount == 0);
            telemetry.Conflicts.Should().BeEmpty();
            database.Context.ChangeTracker.Clear();
            var releases = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).ToArrayAsync().ConfigureAwait(false);
            releases.Should().HaveCount(4);
            releases.Select(item => new
            {
                item.CentralTransientEventId,
                item.ActorIdentity,
                item.IdempotencyKey
            }).Should().OnlyHaveUniqueItems();
            releases.Sum(item => item.Items.Count).Should().Be(seeds.Sum(seed => seed.ArtifactIds.Length));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseCreation_ConcurrentSameKeyReturnsOneDurableRelease()
    {
        await using var database = CreateDatabase("Issue285SameKey");
        await using var secondContext = CreateContext(database.ConnectionString);
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var firstHandler = new Issue285RejectingHttpHandler();
            var secondHandler = new Issue285RejectingHttpHandler();
            var first = CreateIssue285Service(database.Context, firstHandler, seed.ArtifactIds);
            var second = CreateIssue285Service(secondContext, secondHandler, seed.ArtifactIds);
            var principal = CreateOwnerPrincipal("issue-285-same-key", admin: true);

            var results = await Task.WhenAll(
                first.ReleaseAsync(
                    principal, seed.EventId, seed.RowVersion, "issue-285-same-key", CancellationToken.None),
                second.ReleaseAsync(
                    principal, seed.EventId, seed.RowVersion, "issue-285-same-key", CancellationToken.None))
                .ConfigureAwait(false);

            results.Should().OnlyContain(result =>
                result.Status == CentralTransientPayloadReleaseStatus.Released ||
                result.Status == CentralTransientPayloadReleaseStatus.Accepted);
            results.Select(result => result.Response!.ReleaseId).Should().OnlyContain(
                releaseId => releaseId == results[0].Response!.ReleaseId);
            results.Count(result => result.Response!.Replayed).Should().Be(1);
            firstHandler.RequestCount.Should().Be(0);
            secondHandler.RequestCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(seed.ArtifactIds.Length);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }


    [TestMethod]
    public async Task PayloadReleaseCreation_DeadlockRetriesInsideSqlBoundary()
    {
        await using var database = CreateDatabase("Issue285CreationRetry");
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var deadlock = await CreateIssue285SqlDeadlockAsync(
                    database.ConnectionString, seed.ArtifactIds.Take(2).ToArray())
                .ConfigureAwait(false);
            deadlock.Number.Should().Be(1205);
            var handler = new Issue285RejectingHttpHandler();
            using var telemetry = new Issue285TelemetryCollector();
            var service = CreateIssue285Service(
                database.Context, handler, seed.ArtifactIds, telemetry.Telemetry);
            var creationAttempts = 0;
            service.CreationFaultInjector = (attempt, _, stage) =>
            {
                if (stage == CentralTransientPayloadReleaseCreationFaultStage.BeforeCommit)
                {
                    creationAttempts++;
                    return attempt == 0 ? deadlock : null;
                }
                return null;
            };

            var result = await service.ReleaseAsync(
                CreateOwnerPrincipal("issue-285-retry", admin: true),
                seed.EventId,
                seed.RowVersion,
                "issue-285-retry",
                CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            creationAttempts.Should().Be(2);
            telemetry.Conflicts.Should().Equal(
                new Issue285ConflictMeasurement("deadlock", "retry"));
            handler.RequestCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            var releases = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).ToArrayAsync().ConfigureAwait(false);
            releases.Should().ContainSingle();
            releases[0].Items.Should().HaveCount(seed.ArtifactIds.Length);
            releases[0].Items.Should().OnlyContain(item =>
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.PreservedHeld);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseCreation_DeadlockExhaustionHasNoDurableOrObjectWork()
    {
        await using var database = CreateDatabase("Issue285CreationExhaustion");
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var handler = new Issue285RejectingHttpHandler();
            using var telemetry = new Issue285TelemetryCollector();
            var service = CreateIssue285Service(
                database.Context, handler, seed.ArtifactIds, telemetry.Telemetry);
            var creationAttempts = 0;
            service.CreationDeadlockClassifier = exception =>
                exception is InvalidOperationException { Message: "issue-285-deadlock" };
            service.CreationFaultInjector = (_, _, stage) =>
            {
                if (stage == CentralTransientPayloadReleaseCreationFaultStage.BeforeCommit)
                {
                    creationAttempts++;
                    return new InvalidOperationException("issue-285-deadlock");
                }
                return null;
            };

            Func<Task> release = async () => _ = await service.ReleaseAsync(
                CreateOwnerPrincipal("issue-285-exhaustion", admin: true),
                seed.EventId,
                seed.RowVersion,
                "issue-285-exhaustion",
                CancellationToken.None).ConfigureAwait(false);

            await release.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*exhausted SQL conflict retries*").ConfigureAwait(false);
            creationAttempts.Should().Be(CentralTransientPayloadReleaseService.MaximumCreationConflictRetries + 1);
            telemetry.Conflicts.Should().Equal(
                new Issue285ConflictMeasurement("deadlock", "retry"),
                new Issue285ConflictMeasurement("deadlock", "retry"),
                new Issue285ConflictMeasurement("deadlock", "retry"),
                new Issue285ConflictMeasurement("deadlock", "exhausted"));
            handler.RequestCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseCreation_NonDeadlockFailureIsNotRetried()
    {
        await using var database = CreateDatabase("Issue285NonDeadlock");
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var handler = new Issue285RejectingHttpHandler();
            var service = CreateIssue285Service(database.Context, handler, seed.ArtifactIds);
            var creationAttempts = 0;
            service.CreationFaultInjector = (_, _, stage) =>
            {
                if (stage == CentralTransientPayloadReleaseCreationFaultStage.BeforeCommit)
                {
                    creationAttempts++;
                    return new InvalidOperationException("issue-285-not-retryable");
                }
                return null;
            };

            Func<Task> release = async () => _ = await service.ReleaseAsync(
                CreateOwnerPrincipal("issue-285-not-retryable", admin: true),
                seed.EventId,
                seed.RowVersion,
                "issue-285-not-retryable",
                CancellationToken.None).ConfigureAwait(false);

            await release.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("issue-285-not-retryable").ConfigureAwait(false);
            creationAttempts.Should().Be(1);
            handler.RequestCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }


    [TestMethod]
    public async Task PayloadReleaseCreation_AmbiguousCommitAdoptsOneDurableRelease()
    {
        await using var database = CreateDatabase("Issue285AmbiguousCommit");
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var handler = new Issue285RejectingHttpHandler();
            using var telemetry = new Issue285TelemetryCollector();
            var service = CreateIssue285Service(
                database.Context, handler, seed.ArtifactIds, telemetry.Telemetry);
            var afterCommitFaults = 0;
            service.CreationAmbiguousOutcomeClassifier = exception =>
                exception is InvalidOperationException { Message: "issue-285-ambiguous" };
            service.CreationFaultInjector = (_, _, stage) =>
            {
                if (stage == CentralTransientPayloadReleaseCreationFaultStage.AfterCommit && afterCommitFaults++ == 0)
                {
                    return new InvalidOperationException("issue-285-ambiguous");
                }
                return null;
            };
            var principal = CreateOwnerPrincipal("issue-285-ambiguous", admin: true);

            var result = await service.ReleaseAsync(
                principal,
                seed.EventId,
                seed.RowVersion,
                "issue-285-ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            var replay = await service.ReleaseAsync(
                principal,
                seed.EventId,
                seed.RowVersion,
                "issue-285-ambiguous",
                CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            replay.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            replay.Response!.Replayed.Should().BeTrue();
            replay.Response.ReleaseId.Should().Be(result.Response!.ReleaseId);
            afterCommitFaults.Should().Be(1);
            telemetry.Conflicts.Should().Equal(
                new Issue285ConflictMeasurement("ambiguous-commit", "recovered"));
            handler.RequestCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(seed.ArtifactIds.Length);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseCreation_AmbiguousProbeFailureRetriesAndAdoptsCommittedRelease()
    {
        await using var database = CreateDatabase("Issue285AmbiguousProbe");
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var handler = new Issue285RejectingHttpHandler();
            using var telemetry = new Issue285TelemetryCollector();
            var service = CreateIssue285Service(
                database.Context, handler, seed.ArtifactIds, telemetry.Telemetry);
            var afterCommitFaults = 0;
            var probeFaults = 0;
            service.CreationAmbiguousOutcomeClassifier = exception =>
                exception is InvalidOperationException { Message: "issue-285-ambiguous" };
            service.CreationFaultInjector = (_, _, stage) =>
                stage == CentralTransientPayloadReleaseCreationFaultStage.AfterCommit && afterCommitFaults++ == 0
                    ? new InvalidOperationException("issue-285-ambiguous")
                    : null;
            service.CreationProbeFaultInjector = () => probeFaults++ == 0
                ? new OperationCanceledException("issue-285-probe-timeout")
                : null;

            var result = await service.ReleaseAsync(
                CreateOwnerPrincipal("issue-285-probe", admin: true),
                seed.EventId,
                seed.RowVersion,
                "issue-285-probe",
                CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            result.Response!.Replayed.Should().BeTrue();
            afterCommitFaults.Should().Be(1);
            probeFaults.Should().Be(1);
            telemetry.Conflicts.Should().Equal(
                new Issue285ConflictMeasurement("ambiguous-commit", "retry"));
            handler.RequestCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientPayloadReleases.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking().CountAsync()
                .ConfigureAwait(false)).Should().Be(seed.ArtifactIds.Length);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Issue285EventSeed> SeedIssue285EventAsync(ApplicationDbContext db)
    {
        await db.Database.MigrateAsync().ConfigureAwait(false);
        var fixture = CentralTransientPersistenceFixture.Create();
        var seeded = await SeedAsync(db, fixture).ConfigureAwait(false);
        _ = await new CentralTransientEventPersistence(db).AppendAsync(fixture.Request with
        {
            CentralDerivativeJobId = seeded.JobId
        }, CancellationToken.None).ConfigureAwait(false);
        _ = await db.CentralDerivativeJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, CentralDerivativeJobStatus.Completed)
            .SetProperty(item => item.CompletedAtUtc, DateTimeOffset.UtcNow)
            .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var artifactIds = seeded.Artifacts.Select(item => item.Id).Order().ToArray();
        var eventId = await db.CentralTransientObservationSources.AsNoTracking()
            .Where(item => artifactIds.Contains(item.CentralArtifactId))
            .Select(item => item.Observation!.CentralTransientEventId)
            .Distinct().SingleAsync().ConfigureAwait(false);
        var current = await db.CentralTransientEventCurrent.AsNoTracking()
            .SingleAsync(item => item.CentralTransientEventId == eventId).ConfigureAwait(false);
        var review = await new CentralTransientReviewService(
                db,
                new CentralTransientEventVersionAppender(db),
                TimeProvider.System)
            .ReviewAsync(
                CreateOwnerPrincipal("issue-285-review", admin: true),
                current.CentralTransientEventId,
                current.RowVersion,
                $"issue-285-review-{Guid.NewGuid():N}",
                new CentralTransientReviewRequest(
                    current.ActiveAssessmentId,
                    TransientReviewDisposition.Confirmed,
                    null,
                    ["human.release-approved"]),
                CancellationToken.None).ConfigureAwait(false);
        review.Status.Should().Be(CentralTransientReviewMutationStatus.Applied);
        _ = await db.CentralDerivativeJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
            .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var reviewed = await db.CentralTransientEventCurrent.AsNoTracking()
            .SingleAsync(item => item.CentralTransientEventId == eventId).ConfigureAwait(false);
        return new(
            reviewed.CentralTransientEventId,
            reviewed.RowVersion,
            artifactIds);
    }

    private static CentralTransientPayloadReleaseService CreateIssue285Service(
        ApplicationDbContext db,
        HttpMessageHandler handler,
        IReadOnlyCollection<Guid> heldArtifactIds,
        CentralTransientLifecycleTelemetry? telemetry = null)
    {
        var references = new Issue250SwitchableRetentionReferences(new CentralArtifactRetentionReferences(db));
        foreach (var artifactId in heldArtifactIds)
        {
            references.Hold(artifactId);
        }
        return new(
            db,
            references,
            CreateIssue250Minio(handler),
            Options.Create(new CentralTransientPayloadReleaseOptions { Enabled = true }),
            TimeProvider.System,
            telemetry);
    }

    private static async Task<SqlException> CreateIssue285SqlDeadlockAsync(
        string connectionString,
        Guid[] artifactIds)
    {
        artifactIds.Should().HaveCount(2);
        await using var firstConnection = new SqlConnection(connectionString);
        await using var secondConnection = new SqlConnection(connectionString);
        await firstConnection.OpenAsync().ConfigureAwait(false);
        await secondConnection.OpenAsync().ConfigureAwait(false);
        await using var firstTransaction = await firstConnection.BeginTransactionAsync().ConfigureAwait(false);
        await using var secondTransaction = await secondConnection.BeginTransactionAsync().ConfigureAwait(false);
        await ExecuteIssue285LockAsync(firstConnection, firstTransaction, artifactIds[0]).ConfigureAwait(false);
        await ExecuteIssue285LockAsync(secondConnection, secondTransaction, artifactIds[1]).ConfigureAwait(false);

        var firstWait = CaptureIssue285SqlExceptionAsync(
            firstConnection, firstTransaction, artifactIds[1]);
        await Task.Delay(100).ConfigureAwait(false);
        var secondWait = CaptureIssue285SqlExceptionAsync(
            secondConnection, secondTransaction, artifactIds[0]);
        var outcomes = await Task.WhenAll(firstWait, secondWait).WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        var deadlocks = outcomes.Where(exception => exception?.Number == 1205).ToArray();
        deadlocks.Should().ContainSingle();
        return deadlocks[0]!;
    }

    private static async Task<SqlException?> CaptureIssue285SqlExceptionAsync(
        SqlConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid artifactId)
    {
        try
        {
            await ExecuteIssue285LockAsync(connection, transaction, artifactId).ConfigureAwait(false);
            return null;
        }
        catch (SqlException exception)
        {
            return exception;
        }
    }

    private static async Task ExecuteIssue285LockAsync(
        SqlConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid artifactId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandTimeout = 10;
        command.CommandText = """
            UPDATE [CentralArtifacts]
            SET [ByteLength] = [ByteLength]
            WHERE [Id] = @artifactId;
            """;
        _ = command.Parameters.Add(new SqlParameter("@artifactId", System.Data.SqlDbType.UniqueIdentifier)
        {
            Value = artifactId
        });
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed record Issue285EventSeed(Guid EventId, byte[] RowVersion, Guid[] ArtifactIds);

    private sealed record Issue285ConflictMeasurement(string Reason, string Outcome);

    private sealed class Issue285TelemetryCollector : IDisposable
    {
        private readonly MeterListener listener = new();

        internal Issue285TelemetryCollector()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CentralTransientLifecycleTelemetry.MeterName &&
                    instrument.Name == "skymonitor.central.transient.retention.creation.conflicts")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                measurement.Should().Be(1);
                var values = tags.ToArray().ToDictionary(item => item.Key, item => item.Value?.ToString());
                values.Keys.Should().BeEquivalentTo(["reason", "outcome"]);
                Conflicts.Add(new(values["reason"]!, values["outcome"]!));
            });
            listener.Start();
            Telemetry = new CentralTransientLifecycleTelemetry(
                NullLogger<CentralTransientLifecycleTelemetry>.Instance);
        }

        internal CentralTransientLifecycleTelemetry Telemetry { get; }

        internal List<Issue285ConflictMeasurement> Conflicts { get; } = [];

        public void Dispose()
        {
            Telemetry.Dispose();
            listener.Dispose();
        }
    }

    private sealed class Issue285AsyncBarrier(int participants)
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int remaining = participants;

        internal async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                completion.TrySetResult();
            }
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Issue285RejectingHttpHandler : HttpMessageHandler
    {
        private int requestCount;
        internal int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

}
