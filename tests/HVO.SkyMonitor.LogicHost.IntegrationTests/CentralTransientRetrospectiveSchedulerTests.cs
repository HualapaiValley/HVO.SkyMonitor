using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralTransientRetrospectiveSchedulerTests
{
    [TestMethod]
    public async Task ScheduleBatchAsync_IntegrityRejectionIsContainedPerArtifactAndNotReselected()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await using var database = await RetrospectiveDatabase.CreateAsync(now, 3).ConfigureAwait(false);
        var rejectedId = database.ArtifactIds[0];
        var goodIds = database.ArtifactIds.Skip(1).ToArray();
        var scheduler = new IntegrityRejectingScheduler(rejectedId);
        var logger = new RecordingLogger<CentralTransientRetrospectiveScheduler>();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var outcomes = new OperationOutcomeCollector();

        Func<Task> firstPass = () => RunPassAsync(database, scheduler, telemetry, logger, now);
        await firstPass.Should().NotThrowAsync(
            "an integrity rejection for the oldest candidate must not abort the retrospective pass").ConfigureAwait(false);
        var firstCalls = scheduler.TakeCalls();
        await RunPassAsync(database, scheduler, telemetry, logger, now.AddMinutes(1)).ConfigureAwait(false);
        var secondCalls = scheduler.TakeCalls();

        await using var verification = database.CreateContext();
        var artifacts = await verification.CentralArtifacts.AsNoTracking()
            .ToDictionaryAsync(item => item.ArtifactId).ConfigureAwait(false);
        using (new AssertionScope())
        {
            firstCalls.Should().Equal([rejectedId, .. goodIds],
                "the candidates behind the rejected one are scheduled in the same pass");
            secondCalls.Should().Equal(goodIds,
                "the rejected artifact is ineligible until its marker is cleared, so it cannot lead the window again");
            var rejected = artifacts[rejectedId];
            rejected.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            rejected.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            rejected.StateReasonCode.Should().Be("object.derivative-scheduling-rejected");
            goodIds.Select(id => artifacts[id].StateReasonCode).Should().AllSatisfy(code => code.Should().BeNull());
            var rejection = logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 2142).Subject;
            rejection.Level.Should().Be(LogLevel.Error);
            rejection.Message.Should().Contain("projected-scene.source-mismatch")
                .And.NotContain(rejectedId.ToString());
            rejection.Exception.Should().BeNull();
            outcomes.Observations.Should().ContainSingle(item => item.Outcome == "scheduling-rejected")
                .Which.Operation.Should().Be("transient-schedule");
        }

        // Clearing the marker, as the next recovery generation does after it verifies the object again, makes the
        // artifact eligible once more; it is retried exactly once and marked again while its evidence still fails.
        await verification.CentralArtifacts.Where(item => item.ArtifactId == rejectedId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StateReasonCode, (string?)null))
            .ConfigureAwait(false);
        await RunPassAsync(database, scheduler, telemetry, logger, now.AddMinutes(2)).ConfigureAwait(false);

        scheduler.TakeCalls().Should().Equal([rejectedId, .. goodIds]);
        (await verification.CentralArtifacts.AsNoTracking().Where(item => item.ArtifactId == rejectedId)
            .Select(item => item.StateReasonCode).SingleAsync().ConfigureAwait(false))
            .Should().Be("object.derivative-scheduling-rejected");
    }

    private static async Task RunPassAsync(
        RetrospectiveDatabase database,
        ICentralDerivativeJobScheduler scheduler,
        CentralDerivativeWorkerTelemetry telemetry,
        ILogger<CentralTransientRetrospectiveScheduler> logger,
        DateTimeOffset now)
    {
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        await using var context = database.CreateContext();
        var retrospective = new CentralTransientRetrospectiveScheduler(
            context,
            scheduler,
            new CentralDerivativeRecipeCatalog(options),
            Options.Create(options),
            telemetry,
            logger);
        await retrospective.ScheduleBatchAsync(now, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class IntegrityRejectingScheduler(Guid rejectedArtifactId) : ICentralDerivativeJobScheduler
    {
        private readonly List<Guid> calls = [];

        internal Guid[] TakeCalls()
        {
            var taken = calls.ToArray();
            calls.Clear();
            return taken;
        }

        public Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
            => throw new AssertFailedException("Retrospective scheduling must use the durable artifact-identity overload.");

        public Task EnsureRequiredJobsAsync(
            Guid devicePublicId,
            Guid artifactId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            calls.Add(artifactId);
            return artifactId == rejectedArtifactId
                ? throw new CentralArtifactIntegrityException("projected-scene.source-mismatch")
                : Task.CompletedTask;
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class OperationOutcomeCollector : IDisposable
    {
        private readonly MeterListener listener = new();

        internal ConcurrentQueue<(string Operation, string Outcome)> Observations { get; } = new();

        internal OperationOutcomeCollector()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CentralDerivativeWorkerTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.derivative.operations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? operation = null;
                string? outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "operation")
                    {
                        operation = tag.Value as string;
                    }
                    else if (tag.Key == "outcome")
                    {
                        outcome = tag.Value as string;
                    }
                }
                Observations.Enqueue((operation ?? string.Empty, outcome ?? string.Empty));
            });
            listener.Start();
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed class RetrospectiveDatabase(
        ApplicationDbContext context,
        string connectionString,
        IReadOnlyList<Guid> artifactIds) : IAsyncDisposable
    {
        // Edge artifact ids in ReceivedAtUtc order, which is the retrospective candidate order.
        internal IReadOnlyList<Guid> ArtifactIds { get; } = artifactIds;

        internal static async Task<RetrospectiveDatabase> CreateAsync(DateTimeOffset now, int captureCount)
        {
            var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = $"SkyMonitorRetrospective_{Guid.NewGuid():N}"
            };
            var context = CreateContext(builder.ConnectionString);
            await context.Database.MigrateAsync().ConfigureAwait(false);

            var observatory = new Observatory
            {
                OwnerUserId = "retrospective-owner",
                Name = "Retrospective observatory",
                LatitudeDegrees = 35,
                LongitudeDegrees = -113,
                ElevationMeters = 500,
                TimeZoneId = "UTC",
                CreatedAtUtc = now.AddDays(-2),
                IsActive = true
            };
            var registration = new DeviceRegistration
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                DeviceId = $"retrospective-{Guid.NewGuid():N}",
                FriendlyName = "Retrospective camera",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = observatory.OwnerUserId,
                OwnerDisplayName = "Retrospective owner",
                OwnerConfirmationMethod = "SelfAttested",
                VerificationCodeHash = new string('B', 64),
                DevicePublicId = Guid.NewGuid(),
                Status = DeviceRegistrationStatus.Active,
                IssuedAtUtc = now.AddDays(-1),
                ActivatedAtUtc = now.AddDays(-1)
            };
            context.AddRange(observatory, registration);
            var artifactIds = new List<Guid>();
            for (var sequence = 0; sequence < captureCount; sequence++)
            {
                var receivedAtUtc = now.AddMinutes(sequence - captureCount);
                var frame = new CentralFrame
                {
                    RegistrationId = registration.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ObservatoryId = observatory.Id,
                    AgentId = registration.DeviceId,
                    FrameId = Guid.NewGuid(),
                    CaptureSequence = sequence + 1,
                    CapturedAtUtc = receivedAtUtc,
                    FirstReceivedAtUtc = receivedAtUtc,
                    LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved
                };
                var artifactId = Guid.NewGuid();
                frame.Artifacts.Add(new CentralArtifact
                {
                    CentralFrameId = frame.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ArtifactId = artifactId,
                    Role = FrameArtifactRole.Raw,
                    RecipeVersion = "raw-v1",
                    ManifestSchemaVersion = "v2",
                    MediaType = "application/octet-stream",
                    ByteLength = 4,
                    ChecksumSha256 = new string('D', 64),
                    StorageReference = $"object://skymonitor-artifacts/retrospective/{Guid.NewGuid():N}",
                    ReceivedAtUtc = receivedAtUtc,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    ObjectState = CentralArtifactObjectState.Available,
                    ReconstructionState = CentralReconstructionState.Complete
                });
                context.CentralFrames.Add(frame);
                artifactIds.Add(artifactId);
            }
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return new(context, builder.ConnectionString, artifactIds);
        }

        internal ApplicationDbContext CreateContext() => CreateContext(connectionString);

        private static ApplicationDbContext CreateContext(string connectionString)
            => new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);

        public async ValueTask DisposeAsync()
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
