using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DeploymentLocationReconciliationServiceTests
{
    [TestMethod]
    public async Task ProcessNextAsync_DrainsBoundedBatchesAcrossFreshContexts()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var frames = new[]
        {
            new FrameSpec(Guid.Parse("F0000000-0000-0000-0000-000000000003"), now.AddMinutes(-3)),
            new FrameSpec(Guid.Parse("10000000-0000-0000-0000-000000000001"), now.AddMinutes(-2)),
            new FrameSpec(Guid.Parse("A0000000-0000-0000-0000-000000000002"), now.AddMinutes(-2))
        };
        await using var database = await ReconciliationDatabase.CreateAsync("Drain", now, frames).ConfigureAwait(false);
        var scheduler = new RecordingScheduler();
        var clock = new MutableTimeProvider(now);
        var options = CreateOptions(captureBatchSize: 2, schedulingBatchSize: 1);

        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();
        await using (var discovered = database.CreateContext())
        {
            var inProgress = await discovered.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
            inProgress.CaptureCount.Should().BeNull();
            inProgress.DiscoveredCaptureCount.Should().Be(2);
            inProgress.DiscoveryCursorCentralFrameId.Should().NotBeNull();
            (await discovered.DeploymentLocationReconciliationCaptures.CountAsync().ConfigureAwait(false)).Should().Be(2);
        }
        var calls = 1 + await DrainAsync(database, scheduler, clock, options).ConfigureAwait(false);

        calls.Should().Be(11);
        scheduler.Calls.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        scheduler.WindowCalls.Should().BeEquivalentTo(scheduler.Calls);
        await using var verification = database.CreateContext();
        var work = await verification.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
        work.Status.Should().Be(DeploymentLocationReconciliationStatuses.Completed);
        work.CaptureCount.Should().Be(3);
        work.CompletedCaptureCount.Should().Be(3);
        work.ScheduledArtifactCount.Should().Be(3);
        work.LeaseToken.Should().BeNull();
        (await verification.DeploymentLocationReconciliationCaptures.CountAsync().ConfigureAwait(false)).Should().Be(3);
        (await verification.CentralFrames.CountAsync(item =>
            item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved).ConfigureAwait(false))
            .Should().Be(3);
        (await verification.CentralCaptureLocations.CountAsync(item =>
            item.DeviceDeploymentLocationVersionId == database.DeploymentId).ConfigureAwait(false)).Should().Be(3);
    }

    [TestMethod]
    public async Task ProcessNextAsync_BindsOnlyExactOrdinalEvidenceWithinEffectiveInterval()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var until = now.AddMinutes(10);
        var frames = new[]
        {
            new FrameSpec(Guid.NewGuid(), now, CapturedAtUtc: now),
            new FrameSpec(Guid.NewGuid(), now.AddSeconds(1), CapturedAtUtc: until),
            new FrameSpec(Guid.NewGuid(), now.AddSeconds(2), Source: "Operator-Survey", CapturedAtUtc: now.AddMinutes(1)),
            new FrameSpec(Guid.NewGuid(), now.AddSeconds(3), LocationId: "SITE-A", CapturedAtUtc: now.AddMinutes(2)),
            new FrameSpec(Guid.NewGuid(), now.AddSeconds(4), CapturedAtUtc: now.AddDays(-2))
        };
        await using var database = await ReconciliationDatabase.CreateAsync(
            "Exact", now.AddMinutes(5), frames, effectiveUntilUtc: until, createArtifacts: false).ConfigureAwait(false);

        _ = await DrainAsync(
            database, new RecordingScheduler(), new MutableTimeProvider(now.AddMinutes(5)), CreateOptions(2, 1))
            .ConfigureAwait(false);

        await using var verification = database.CreateContext();
        var work = await verification.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
        work.CaptureCount.Should().Be(3);
        var locations = await verification.CentralCaptureLocations.AsNoTracking()
            .Include(item => item.CentralFrame)
            .ToDictionaryAsync(item => item.CentralFrameId).ConfigureAwait(false);
        locations[frames[0].Id].DeviceDeploymentLocationVersionId.Should().Be(database.DeploymentId);
        locations[frames[0].Id].CentralFrame!.LocationEvidenceState.Should()
            .Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        locations[frames[1].Id].DeviceDeploymentLocationVersionId.Should().BeNull();
        locations[frames[1].Id].CentralFrame!.LocationEvidenceState.Should()
            .Be(CentralCaptureLocationEvidenceState.Mismatch);
        locations[frames[2].Id].DeviceDeploymentLocationVersionId.Should().BeNull();
        locations[frames[2].Id].CentralFrame!.LocationEvidenceState.Should()
            .Be(CentralCaptureLocationEvidenceState.Mismatch);
        locations[frames[3].Id].CentralFrame!.LocationEvidenceState.Should()
            .Be(CentralCaptureLocationEvidenceState.ReportedUnresolved);
        locations[frames[4].Id].CentralFrame!.LocationEvidenceState.Should()
            .Be(CentralCaptureLocationEvidenceState.ReportedUnresolved);
    }

    [TestMethod]
    public async Task ProcessNextAsync_SchedulerFailurePersistsRetryAndResumesAtLeastOnce()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var frames = new[]
        {
            new FrameSpec(Guid.NewGuid(), now.AddMinutes(-2)),
            new FrameSpec(Guid.NewGuid(), now.AddMinutes(-1))
        };
        await using var database = await ReconciliationDatabase.CreateAsync("Retry", now, frames).ConfigureAwait(false);
        var scheduler = new RecordingScheduler { FailNext = true };
        var clock = new MutableTimeProvider(now);
        var options = CreateOptions(2, 2);

        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();

        await using (var failed = database.CreateContext())
        {
            var work = await failed.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
            work.Status.Should().Be(DeploymentLocationReconciliationStatuses.Retry);
            work.LastErrorCode.Should().Be("database");
            work.NextAttemptAtUtc.Should().Be(now.AddSeconds(2));
            work.LeaseToken.Should().BeNull();
            work.SchedulingCentralFrameId.Should().BeNull();
        }
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));

        _ = await DrainAsync(database, scheduler, clock, options).ConfigureAwait(false);

        scheduler.Calls.Should().HaveCount(3);
        scheduler.WindowCalls.Should().HaveCount(2);
        scheduler.Calls.GroupBy(item => item).Should().ContainSingle(group => group.Count() == 2);
        await using var verification = database.CreateContext();
        var completed = await verification.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
        completed.Status.Should().Be(DeploymentLocationReconciliationStatuses.Completed);
        completed.AttemptCount.Should().Be(0);
        completed.ScheduledArtifactCount.Should().Be(2);
    }

    [TestMethod]
    public async Task ProcessNextAsync_FencesSchedulingBeforeAuthorityGenerationReset()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await using var database = await ReconciliationDatabase.CreateAsync(
            "Fence", now, [new FrameSpec(Guid.NewGuid(), now.AddMinutes(-1))]).ConfigureAwait(false);
        var scheduler = new BlockingScheduler();
        var clock = new MutableTimeProvider(now);
        var options = CreateOptions(1, 1);
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();

        var scheduling = ProcessOneAsync(database, scheduler, clock, options);
        await scheduler.Entered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var replacementToken = Guid.NewGuid();
        var reset = ResetAuthorityGenerationAsync(database, replacementToken, now.AddSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        reset.IsCompleted.Should().BeFalse("the scheduler transaction holds the token-fence row lock");

        scheduler.Release();
        (await scheduling.ConfigureAwait(false)).Should().BeTrue();
        await reset.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var replacementScheduler = new RecordingScheduler();
        clock.Advance(TimeSpan.FromSeconds(1));
        _ = await DrainAsync(database, replacementScheduler, clock, options).ConfigureAwait(false);

        scheduler.Calls.Should().Be(1);
        replacementScheduler.Calls.Should().ContainSingle();
        await using var verification = database.CreateContext();
        var work = await verification.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
        work.AuthorityConcurrencyToken.Should().Be(replacementToken);
        work.Status.Should().Be(DeploymentLocationReconciliationStatuses.Completed);
        (await verification.DeploymentLocationReconciliationCaptures
            .Select(item => item.AuthorityConcurrencyToken)
            .Distinct()
            .CountAsync().ConfigureAwait(false)).Should().Be(2);
    }

    [TestMethod]
    public async Task ProcessNextAsync_LeavesLiveLeaseAndReclaimsExpiredLease()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await using var database = await ReconciliationDatabase.CreateAsync(
            "Lease", now, [new FrameSpec(Guid.NewGuid(), now.AddMinutes(-1))]).ConfigureAwait(false);
        var originalLease = Guid.NewGuid();
        await using (var setup = database.CreateContext())
        {
            await setup.DeploymentLocationReconciliationWork.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, DeploymentLocationReconciliationStatuses.Processing)
                .SetProperty(item => item.StartedAtUtc, now)
                .SetProperty(item => item.LastAttemptAtUtc, now)
                .SetProperty(item => item.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.AttemptCount, 1)
                .SetProperty(item => item.LeaseToken, originalLease)
                .SetProperty(item => item.LeaseOwner, "failed-worker")
                .SetProperty(item => item.LeaseExpiresAtUtc, now.AddSeconds(30))).ConfigureAwait(false);
        }
        var clock = new MutableTimeProvider(now);
        var scheduler = new RecordingScheduler();
        var options = CreateOptions(2, 1);

        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(30));
        (await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false)).Should().BeTrue();

        await using var verification = database.CreateContext();
        var work = await verification.DeploymentLocationReconciliationWork.SingleAsync().ConfigureAwait(false);
        work.Status.Should().Be(DeploymentLocationReconciliationStatuses.Pending);
        work.CaptureCount.Should().Be(1);
        work.LeaseToken.Should().BeNull();
        work.LeaseOwner.Should().BeNull();
        work.LeaseExpiresAtUtc.Should().BeNull();
    }

    private static DeploymentLocationReconciliationOptions CreateOptions(int captureBatchSize, int schedulingBatchSize)
        => new()
        {
            WorkerId = "integration-reconciliation",
            CaptureBatchSize = captureBatchSize,
            SchedulingBatchSize = schedulingBatchSize,
            LeaseDuration = TimeSpan.FromMinutes(1),
            PollInterval = TimeSpan.FromMilliseconds(10),
            InitialRetryDelay = TimeSpan.FromSeconds(2),
            MaximumRetryDelay = TimeSpan.FromMinutes(1)
        };

    private static async Task<int> DrainAsync(
        ReconciliationDatabase database,
        RecordingScheduler scheduler,
        MutableTimeProvider clock,
        DeploymentLocationReconciliationOptions options)
    {
        for (var calls = 1; calls <= 30; calls++)
        {
            if (!await ProcessOneAsync(database, scheduler, clock, options).ConfigureAwait(false))
            {
                return calls;
            }
        }
        Assert.Fail("Deployment reconciliation did not drain within 30 processor calls.");
        return 0;
    }

    private static async Task<bool> ProcessOneAsync(
        ReconciliationDatabase database,
        ICentralDerivativeJobScheduler scheduler,
        MutableTimeProvider clock,
        DeploymentLocationReconciliationOptions options)
    {
        await using var context = database.CreateContext();
        using var telemetry = new DeploymentLocationTelemetry();
        var processor = new DeploymentLocationReconciliationService(
            context,
            scheduler,
            telemetry,
            Options.Create(options),
            clock,
            NullLogger<DeploymentLocationReconciliationService>.Instance);
        return await processor.ProcessNextAsync().ConfigureAwait(false);
    }

    private static async Task ResetAuthorityGenerationAsync(
        ReconciliationDatabase database,
        Guid replacementToken,
        DateTimeOffset now)
    {
        await using var context = database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
        await context.DeviceDeploymentLocationVersions.Where(item => item.Id == database.DeploymentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConcurrencyToken, replacementToken))
            .ConfigureAwait(false);
        await context.DeploymentLocationReconciliationWork.Where(item =>
                item.DeviceDeploymentLocationVersionId == database.DeploymentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AuthorityConcurrencyToken, replacementToken)
                .SetProperty(item => item.Status, DeploymentLocationReconciliationStatuses.Pending)
                .SetProperty(item => item.UpdatedAtUtc, now)
                .SetProperty(item => item.StartedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.AttemptCount, 0)
                .SetProperty(item => item.LastAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.NextAttemptAtUtc, now)
                .SetProperty(item => item.LastErrorCode, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.CaptureCount, (long?)null)
                .SetProperty(item => item.DiscoveryCutoffUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.DiscoveredCaptureCount, 0)
                .SetProperty(item => item.DiscoveryCursorFirstReceivedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.DiscoveryCursorCentralFrameId, (Guid?)null)
                .SetProperty(item => item.CompletedCaptureCount, 0)
                .SetProperty(item => item.ScheduledArtifactCount, 0)
                .SetProperty(item => item.LastCompletedFirstReceivedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.LastCompletedCentralFrameId, (Guid?)null)
                .SetProperty(item => item.ActiveBatchUpperFirstReceivedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.ActiveBatchUpperCentralFrameId, (Guid?)null)
                .SetProperty(item => item.ActiveBatchCaptureCount, 0)
                .SetProperty(item => item.SchedulingCentralFrameId, (Guid?)null)
                .SetProperty(item => item.SchedulingCentralArtifactId, (Guid?)null))
            .ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private sealed class RecordingScheduler : ICentralDerivativeJobScheduler
    {
        internal List<Guid> Calls { get; } = [];
        internal List<Guid> WindowCalls { get; } = [];
        internal bool FailNext { get; set; }

        public Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
            => throw new AssertFailedException("Reconciliation must use the durable artifact-identity overload.");

        public Task EnsureRequiredJobsAsync(
            Guid devicePublicId,
            Guid artifactId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(artifactId);
            if (FailNext)
            {
                FailNext = false;
                throw new TimeoutException("Injected scheduler failure.");
            }
            return Task.CompletedTask;
        }

        public Task ResolveAffectedWindowsAsync(
            Guid devicePublicId,
            Guid artifactId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            WindowCalls.Add(artifactId);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingScheduler : ICentralDerivativeJobScheduler
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;
        internal int Calls { get; private set; }

        internal void Release() => release.TrySetResult();

        public Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
            => throw new AssertFailedException("Reconciliation must use the durable artifact-identity overload.");

        public async Task EnsureRequiredJobsAsync(
            Guid devicePublicId,
            Guid artifactId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Calls++;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task ResolveAffectedWindowsAsync(
            Guid devicePublicId,
            Guid artifactId,
            DateTimeOffset now,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan duration) => current += duration;
    }

    private sealed record FrameSpec(
        Guid Id,
        DateTimeOffset FirstReceivedAtUtc,
        string LocationId = "site-a",
        string Source = "operator-survey",
        DateTimeOffset? CapturedAtUtc = null);

    private sealed class ReconciliationDatabase(
        ApplicationDbContext context,
        string connectionString,
        Guid deploymentId) : IAsyncDisposable
    {
        internal Guid DeploymentId { get; } = deploymentId;

        internal static async Task<ReconciliationDatabase> CreateAsync(
            string scenario,
            DateTimeOffset now,
            IReadOnlyCollection<FrameSpec> frameSpecs,
            DateTimeOffset? effectiveUntilUtc = null,
            bool createArtifacts = true)
        {
            var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = $"SkyMonitorReconciliation{scenario}_{Guid.NewGuid():N}"
            };
            var context = CreateContext(builder.ConnectionString);
            await context.Database.MigrateAsync().ConfigureAwait(false);

            var observatory = new Observatory
            {
                OwnerUserId = "reconciliation-owner",
                Name = "Reconciliation observatory",
                LatitudeDegrees = 35,
                LongitudeDegrees = -113,
                ElevationMeters = 500,
                TimeZoneId = "UTC",
                AllowedDeploymentRadiusMeters = 1000,
                CurrentLocationVersion = 1,
                CurrentLocationCanonicalSha256 = new string('A', 64),
                CreatedAtUtc = now.AddDays(-2),
                IsActive = true
            };
            var observatoryVersion = new ObservatoryLocationVersion
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                Version = 1,
                CanonicalSha256 = observatory.CurrentLocationCanonicalSha256,
                EffectiveFromUtc = now.AddDays(-1),
                LatitudeDegrees = observatory.LatitudeDegrees,
                LongitudeDegrees = observatory.LongitudeDegrees,
                ElevationMeters = observatory.ElevationMeters,
                TimeZoneId = observatory.TimeZoneId,
                AllowedDeploymentRadiusMeters = observatory.AllowedDeploymentRadiusMeters,
                RecordedAtUtc = now.AddDays(-1),
                RecordedBy = observatory.OwnerUserId
            };
            var registration = new DeviceRegistration
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                DeviceId = $"reconciliation-{Guid.NewGuid():N}",
                FriendlyName = "Reconciliation camera",
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = observatory.OwnerUserId,
                OwnerDisplayName = "Reconciliation owner",
                OwnerConfirmationMethod = "SelfAttested",
                VerificationCodeHash = new string('B', 64),
                DevicePublicId = Guid.NewGuid(),
                Status = DeviceRegistrationStatus.Active,
                IssuedAtUtc = now.AddDays(-1),
                ActivatedAtUtc = now.AddDays(-1)
            };
            var deployment = new DeviceDeploymentLocationVersion
            {
                Registration = registration,
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId,
                ObservatoryId = observatory.Id,
                ObservatoryLocationVersion = observatoryVersion,
                ObservatoryLocationVersionId = observatoryVersion.Id,
                ObservatoryLocationVersionNumber = observatoryVersion.Version,
                ObservatoryLocationCanonicalSha256 = observatoryVersion.CanonicalSha256,
                LocationId = "site-a",
                Version = 2,
                CanonicalSha256 = new string('C', 64),
                Source = "operator-survey",
                SourceKind = DeploymentLocationSourceKind.Manual,
                HorizontalAccuracyMeters = 2,
                EffectiveFromUtc = now.AddHours(-1),
                EffectiveUntilUtc = effectiveUntilUtc,
                LatitudeDegrees = observatory.LatitudeDegrees,
                LongitudeDegrees = observatory.LongitudeDegrees,
                ElevationMeters = observatory.ElevationMeters,
                TimeZoneId = observatory.TimeZoneId,
                Status = DeploymentLocationResolutionStatus.Acknowledged,
                ReasonCode = "owner-acknowledged",
                ProposedAtUtc = now.AddHours(-1),
                ResolvedAtUtc = now.AddMinutes(-30),
                ResolvedByUserId = observatory.OwnerUserId
            };
            var work = new DeploymentLocationReconciliationWork
            {
                DeploymentLocation = deployment,
                DeviceDeploymentLocationVersionId = deployment.Id,
                AuthorityConcurrencyToken = deployment.ConcurrencyToken,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                NextAttemptAtUtc = now
            };
            deployment.ReconciliationWork = work;
            context.AddRange(observatory, observatoryVersion, registration, deployment, work);
            foreach (var spec in frameSpecs)
            {
                var frame = new CentralFrame
                {
                    Id = spec.Id,
                    RegistrationId = registration.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ObservatoryId = observatory.Id,
                    AgentId = registration.DeviceId,
                    FrameId = Guid.NewGuid(),
                    CapturedAtUtc = spec.CapturedAtUtc ?? spec.FirstReceivedAtUtc,
                    FirstReceivedAtUtc = spec.FirstReceivedAtUtc,
                    LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved
                };
                frame.Location = new CentralCaptureLocation
                {
                    CentralFrame = frame,
                    CentralFrameId = frame.Id,
                    LocationId = spec.LocationId,
                    Version = deployment.Version,
                    Source = spec.Source,
                    HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
                    EffectiveFromUtc = deployment.EffectiveFromUtc,
                    EffectiveUntilUtc = deployment.EffectiveUntilUtc
                };
                if (createArtifacts)
                {
                    frame.Artifacts.Add(new CentralArtifact
                    {
                        CentralFrameId = frame.Id,
                        DevicePublicId = registration.DevicePublicId!.Value,
                        ArtifactId = Guid.NewGuid(),
                        Role = FrameArtifactRole.Raw,
                        RecipeVersion = "raw-v1",
                        ManifestSchemaVersion = "v2",
                        MediaType = "application/octet-stream",
                        ByteLength = 4,
                        ChecksumSha256 = new string('D', 64),
                        StorageReference = $"object://skymonitor-artifacts/reconciliation/{Guid.NewGuid():N}",
                        ReceivedAtUtc = spec.FirstReceivedAtUtc,
                        IdempotencyKey = Guid.NewGuid().ToString("N"),
                        ObjectState = CentralArtifactObjectState.Available,
                        ReconstructionState = CentralReconstructionState.Complete
                    });
                }
                context.CentralFrames.Add(frame);
            }
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return new(context, builder.ConnectionString, deployment.Id);
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
