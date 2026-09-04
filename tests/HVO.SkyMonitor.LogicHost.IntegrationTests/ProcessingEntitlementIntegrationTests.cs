using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Fair scheduling and entitlement coverage (#429) against the real claim engine: atomic entitlements under
/// concurrent claims, weighted fair share and starvation ordering, class budgets that do not block compatible work,
/// capacity release on expiry, usage records, runner pools, and the admission health signal.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProcessingEntitlementIntegrationTests
{
    private static readonly byte[] SourcePayload = [1, 0, 2, 0, 3, 0, 4, 0];

    [TestCleanup]
    public Task CleanupAsync() => DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory);

    [TestMethod]
    public async Task ConcurrentClaimsNeverExceedAnObservatoryEntitlement()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var observatory = await SeedObservatoryAsync("entitlement-concurrent", sources: 4).ConfigureAwait(false);
        using var factory = CreateFactory(("ProcessingEntitlements:DefaultActiveJobs", "2"));

        var leases = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync($"entitlement-worker-{index}", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false);
        })).ConfigureAwait(false);

        var held = leases.Where(lease => lease is not null).ToArray();
        held.Length.Should().Be(2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await ActiveLeasesAsync(db, observatory).ConfigureAwait(false)).Should().Be(2);
            // Releasing one lease frees exactly one slot.
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .FailAsync(held[0]!.JobId, held[0]!.LeaseToken, "entitlement.test", retryable: false, CancellationToken.None)
                .ConfigureAwait(false);
            var next = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("entitlement-worker-next", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            next.Should().NotBeNull();
            (await ActiveLeasesAsync(db, observatory).ConfigureAwait(false)).Should().Be(2);
        }
    }

    [TestMethod]
    public async Task OldBacklogOfOneObservatoryDoesNotStarveAnotherAndStarvingWorkGoesFirst()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var busy = await SeedObservatoryAsync("fairness-busy", sources: 6, availableOffset: TimeSpan.FromMinutes(-30)).ConfigureAwait(false);
        var quiet = await SeedObservatoryAsync("fairness-quiet", sources: 1).ConfigureAwait(false);
        using var factory = CreateFactory(("ProcessingEntitlements:StarvationAge", "1.00:00:00"));

        // Share ordering: the busy observatory has the older work, but once it holds a lease the quiet observatory's
        // share (0 active) wins the next claim instead of the busy backlog draining first.
        var first = await ClaimAsync(factory, "fair-1").ConfigureAwait(false);
        var second = await ClaimAsync(factory, "fair-2").ConfigureAwait(false);
        var third = await ClaimAsync(factory, "fair-3").ConfigureAwait(false);
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        third.Should().NotBeNull();
        var observatories = new[] { first!, second!, third! }.Select(lease => ObservatoryOf(factory, lease)).ToArray();
        observatories.Should().Contain(quiet);
        observatories.Take(2).Should().Contain(quiet, "the quiet observatory is served before the busy backlog drains");

        // Starvation: with a short starvation age the oldest work is served first regardless of share.
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var starving = await SeedObservatoryAsync("fairness-starving", sources: 1, availableOffset: TimeSpan.FromMinutes(-30)).ConfigureAwait(false);
        var fresh = await SeedObservatoryAsync("fairness-fresh", sources: 3).ConfigureAwait(false);
        using var starvationFactory = CreateFactory(
            ("ProcessingEntitlements:StarvationAge", "00:05:00"),
            ($"ProcessingEntitlements:Observatories:{fresh:D}:Priority", "-10"));
        var claimed = await ClaimAsync(starvationFactory, "starve-1").ConfigureAwait(false);
        ObservatoryOf(starvationFactory, claimed!).Should().Be(starving, "starving work outranks priority and share");
    }

    [TestMethod]
    public async Task ClassBudgetsDoNotBlockCompatibleWorkAndExpiryReleasesCapacity()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var observatory = await SeedObservatoryAsync("class-budget", sources: 2).ConfigureAwait(false);
        using var factory = CreateFactory(
            ($"ProcessingEntitlements:ResourceClasses:{CentralProcessingEntitlementOptions.EncodingClass}:ActiveJobs", "1"),
            ("ProcessingEntitlements:DefaultActiveJobs", "1"));

        // Entitlement 1: the first claim leases one job; the second claim is refused by the observatory entitlement.
        var first = await ClaimAsync(factory, "class-1").ConfigureAwait(false);
        first.Should().NotBeNull();
        (await ClaimAsync(factory, "class-2").ConfigureAwait(false)).Should().BeNull();

        // Expire the lease (job and attempt) and the capacity is released to the next claim.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.CentralDerivativeJobs.Where(job => job.Id == first!.JobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
            await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == first!.JobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
        }
        var reclaimed = await ClaimAsync(factory, "class-3").ConfigureAwait(false);
        reclaimed.Should().NotBeNull();

        // Class budget: with the encoding class at its budget, the next claim under a larger entitlement is a job of
        // another class rather than nothing.
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await SeedObservatoryAsync("class-budget-mix", sources: 2).ConfigureAwait(false);
        using var mixFactory = CreateFactory(
            ($"ProcessingEntitlements:ResourceClasses:{CentralProcessingEntitlementOptions.EncodingClass}:ActiveJobs", "1"));
        var claims = new List<CentralDerivativeJobLease>();
        while (claims.Count < 4 && await ClaimAsync(mixFactory, $"mix-{claims.Count}").ConfigureAwait(false) is { } lease)
        {
            claims.Add(lease);
        }
        claims.Count(lease => lease.RecipeName == BuiltInProcessingRecipes.EncodedPreview).Should().Be(1);
        claims.Should().Contain(lease => lease.RecipeName != BuiltInProcessingRecipes.EncodedPreview,
            "work in other classes is claimable while the encoding class is at its budget");
        _ = observatory;
    }

    [TestMethod]
    public async Task UsageRecordsAreWrittenForCompletedFailedAndExpiredAttempts()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var observatory = await SeedObservatoryAsync("usage", sources: 2).ConfigureAwait(false);
        using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var failed = (await jobs.ClaimNextAsync("usage-1", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        await jobs.FailAsync(failed.JobId, failed.LeaseToken, "usage.test", retryable: false, CancellationToken.None).ConfigureAwait(false);
        var skipped = (await jobs.ClaimNextAsync("usage-2", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        await jobs.SkipAsync(skipped.JobId, skipped.LeaseToken, "usage.skip", CancellationToken.None).ConfigureAwait(false);
        var expiring = (await jobs.ClaimNextAsync("usage-3", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.CentralDerivativeJobs.Where(job => job.Id == expiring.JobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
        await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == expiring.JobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
        var reclaimed = await jobs.ClaimNextAsync("usage-4", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        reclaimed!.JobId.Should().Be(expiring.JobId);

        var records = await db.CentralProcessingUsageRecords.AsNoTracking()
            .Where(record => record.ObservatoryId == observatory)
            .ToListAsync().ConfigureAwait(false);
        records.Should().Contain(record => record.CentralDerivativeJobId == failed.JobId && record.Outcome == CentralDerivativeAttemptOutcome.TerminalFailure && record.WorkerId == "usage-1");
        records.Should().Contain(record => record.CentralDerivativeJobId == skipped.JobId && record.Outcome == CentralDerivativeAttemptOutcome.Skipped);
        records.Should().Contain(record => record.CentralDerivativeJobId == expiring.JobId && record.AttemptNumber == 1 && record.Outcome == CentralDerivativeAttemptOutcome.LeaseExpired);
        records.Should().OnlyContain(record => record.ResourceClass.Length > 0 && record.EndedAtUtc >= record.LeaseAcquiredAtUtc);
        records.Select(record => (record.CentralDerivativeJobId, record.AttemptNumber)).Should().OnlyHaveUniqueItems();

        // Operator cancellation terminalizes the attempt with a bulk update and records usage in that transaction.
        var canceled = (await jobs.ClaimNextAsync("usage-5", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
            .CancelAsync(canceled.JobId, "usage-test", CancellationToken.None).ConfigureAwait(false);
        (await db.CentralProcessingUsageRecords.AsNoTracking().SingleOrDefaultAsync(record =>
            record.CentralDerivativeJobId == canceled.JobId && record.AttemptNumber == canceled.AttemptCount).ConfigureAwait(false))!
            .Outcome.Should().Be(CentralDerivativeAttemptOutcome.Canceled);

        // An attempt terminalized through a tracked entity is recorded by the SaveChanges interceptor.
        var tracked = (await jobs.ClaimNextAsync("usage-6", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        db.ChangeTracker.Clear();
        var attempt = await db.CentralDerivativeJobAttempts.SingleAsync(item =>
            item.CentralDerivativeJobId == tracked.JobId && item.AttemptNumber == tracked.AttemptCount).ConfigureAwait(false);
        attempt.Outcome = CentralDerivativeAttemptOutcome.Quarantined;
        attempt.ReasonCode = new string('r', 256);
        attempt.EndedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        var interceptorRecord = await db.CentralProcessingUsageRecords.AsNoTracking().SingleOrDefaultAsync(record =>
            record.CentralDerivativeJobId == tracked.JobId && record.AttemptNumber == tracked.AttemptCount).ConfigureAwait(false);
        interceptorRecord.Should().NotBeNull();
        interceptorRecord!.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Quarantined);
        interceptorRecord.ReasonCode.Should().HaveLength(256, "reason codes keep the attempt column's full length");

        // The periodic sweep restores a row that is missing for any reason.
        await db.CentralProcessingUsageRecords.Where(record => record.CentralDerivativeJobId == tracked.JobId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        (await CentralProcessingUsageRecorder.RecordMissingAsync(db, null, 100, TimeSpan.FromHours(1), CancellationToken.None).ConfigureAwait(false))
            .Should().BeGreaterThanOrEqualTo(1);
        (await db.CentralProcessingUsageRecords.AsNoTracking().CountAsync(record => record.CentralDerivativeJobId == tracked.JobId).ConfigureAwait(false))
            .Should().Be(1);
        (await CentralProcessingUsageRecorder.RecordMissingAsync(db, null, 100, TimeSpan.FromHours(1), CancellationToken.None).ConfigureAwait(false))
            .Should().Be(0, "the sweep is idempotent");

        // Metrics consume each usage row exactly once: a second take returns nothing for the rows already signaled.
        var signals = await CentralProcessingUsageRecorder.TakeUnsignaledAsync(db, 1000, CancellationToken.None).ConfigureAwait(false);
        signals.Should().Contain(signal => signal.ObservatoryId == observatory && signal.Outcome == "Skipped");
        (await CentralProcessingUsageRecorder.TakeUnsignaledAsync(db, 1000, CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
        (await db.CentralProcessingUsageRecords.AsNoTracking().CountAsync(record => record.ObservatoryId == observatory && record.SignaledAtUtc == null).ConfigureAwait(false))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task HealthReportsClassByteSaturation()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var observatory = await SeedObservatoryAsync("class-bytes-health", sources: 2, availableOffset: TimeSpan.FromMinutes(-20)).ConfigureAwait(false);
        long singleInputBytes;
        await using (var seedScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            singleInputBytes = await seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts.AsNoTracking()
                .Where(artifact => artifact.Frame!.ObservatoryId == observatory && artifact.Role == FrameArtifactRole.Raw)
                .Select(artifact => artifact.ByteLength)
                .FirstAsync().ConfigureAwait(false);
        }
        // Every class budget equals one single-input job, so the first lease of any class fills that class's budget.
        using var factory = CreateFactory(CentralProcessingEntitlementOptions.KnownClasses
            .Select(cls => ($"ProcessingEntitlements:ResourceClasses:{cls}:ActiveInputBytes", singleInputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Append(("ProcessingEntitlements:BacklogDegradedAfter", "00:01:00"))
            .ToArray());
        var lease = await ClaimAsync(factory, "class-bytes-health").ConfigureAwait(false);
        lease.Should().NotBeNull("a single-input job fits an empty byte budget");
        await using var scope = factory.Services.CreateAsyncScope();
        var check = new CentralProcessingEntitlementHealthCheck(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<IOptions<CentralProcessingEntitlementOptions>>(),
            TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded, "the image class is at its byte budget with 20-minute-old backlog");
        ((string)result.Data["saturatedDimensions"]).Should().Contain("class-bytes");
    }

    [TestMethod]
    public async Task ExhaustedExpiredLeasesAreTerminalizedEvenWhenTheObservatoryIsSaturated()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await SeedObservatoryAsync("cleanup", sources: 2).ConfigureAwait(false);
        using var factory = CreateFactory(("ProcessingEntitlements:DefaultActiveJobs", "1"));
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // One lease fills the entitlement; a second job is left as an expired lease with its attempts exhausted.
        var exhausted = (await jobs.ClaimNextAsync("cleanup-1", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
        var maxAttempts = await db.CentralDerivativeJobs.AsNoTracking().Where(job => job.Id == exhausted.JobId)
            .Select(job => job.MaxAttempts).SingleAsync().ConfigureAwait(false);
        await db.CentralDerivativeJobs.Where(job => job.Id == exhausted.JobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseExpiresAtUtc, expired)
                .SetProperty(job => job.AttemptCount, maxAttempts)).ConfigureAwait(false);
        await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == exhausted.JobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.LeaseExpiresAtUtc, expired)
                .SetProperty(attempt => attempt.AttemptNumber, maxAttempts)).ConfigureAwait(false);
        var active = (await jobs.ClaimNextAsync("cleanup-2", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
        active.JobId.Should().NotBe(exhausted.JobId);

        // The observatory is at its entitlement, yet the next claim still terminalizes the exhausted lease.
        (await jobs.ClaimNextAsync("cleanup-3", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
        var cleaned = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(job => job.Id == exhausted.JobId).ConfigureAwait(false);
        cleaned.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
        cleaned.LeaseToken.Should().BeNull();
    }

    [TestMethod]
    public async Task HealthReportsCameraSaturationWithoutAnObservatoryEntitlement()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var observatory = await SeedObservatoryAsync("camera-health", sources: 2, availableOffset: TimeSpan.FromMinutes(-20)).ConfigureAwait(false);
        using var factory = CreateFactory(
            ("ProcessingEntitlements:DefaultActiveJobs", "0"),
            ("ProcessingEntitlements:DefaultActiveJobsPerCamera", "1"),
            ("ProcessingEntitlements:BacklogDegradedAfter", "00:01:00"));
        (await ClaimAsync(factory, "camera-health").ConfigureAwait(false)).Should().NotBeNull();
        await using var scope = factory.Services.CreateAsyncScope();
        var check = new CentralProcessingEntitlementHealthCheck(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<IOptions<CentralProcessingEntitlementOptions>>(),
            TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded, "the single camera is at its entitlement with 20-minute-old backlog");
        ((string)result.Data["saturatedDimensions"]).Should().Contain("camera");
        _ = observatory;
    }

    [TestMethod]
    public async Task DedicatedAndReservedPoolsRouteObservatoryWork()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        var pooled = await SeedObservatoryAsync("pool-blue", sources: 2).ConfigureAwait(false);
        var shared = await SeedObservatoryAsync("pool-shared", sources: 2).ConfigureAwait(false);
        using var factory = CreateFactory(($"ProcessingEntitlements:Observatories:{pooled:D}:Pool", "blue"));
        var recipes = new[] { BuiltInProcessingRecipes.EncodedPreview, BuiltInProcessingRecipes.ImageQuality, BuiltInProcessingRecipes.RollingMean, BuiltInProcessingRecipes.Annotation, BuiltInProcessingRecipes.CloudAssessment, BuiltInProcessingRecipes.WeatherCloudOverlay };

        // The shared in-process claim never sees the pooled observatory.
        var inProcess = await ClaimAsync(factory, "pool-shared-worker").ConfigureAwait(false);
        ObservatoryOf(factory, inProcess!).Should().Be(shared);

        await using var scope = factory.Services.CreateAsyncScope();
        var leases = scope.ServiceProvider.GetRequiredService<ICentralDerivativeRunnerLeaseService>();
        var dedicated = await leases.ClaimNextAsync("pool-dedicated", TimeSpan.FromMinutes(2),
            CentralDerivativeClaimScope.Only(recipes, null, "blue", CentralDerivativeClaimPoolMode.Dedicated), CancellationToken.None).ConfigureAwait(false);
        ObservatoryOf(factory, dedicated!).Should().Be(pooled);
        var otherPool = await leases.ClaimNextAsync("pool-red", TimeSpan.FromMinutes(2),
            CentralDerivativeClaimScope.Only(recipes, null, "red", CentralDerivativeClaimPoolMode.Dedicated), CancellationToken.None).ConfigureAwait(false);
        otherPool.Should().BeNull("a dedicated runner of another pool serves nothing here");
        var reserved = await leases.ClaimNextAsync("pool-reserved", TimeSpan.FromMinutes(2),
            CentralDerivativeClaimScope.Only(recipes, null, "blue", CentralDerivativeClaimPoolMode.Reserved), CancellationToken.None).ConfigureAwait(false);
        ObservatoryOf(factory, reserved!).Should().Be(pooled, "a reserved runner serves its pool first");
    }

    [TestMethod]
    public async Task HealthDegradesAtTheAdmissionPendingLimit()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await SeedObservatoryAsync("admission", sources: 2).ConfigureAwait(false);
        using var factory = CreateFactory(("ProcessingEntitlements:AdmissionPendingLimit", "1"));
        await using var scope = factory.Services.CreateAsyncScope();
        var check = new CentralProcessingEntitlementHealthCheck(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<IOptions<CentralProcessingEntitlementOptions>>(),
            TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        result.Status.Should().Be(HealthStatus.Degraded);
        ((int)result.Data["overAdmissionLimit"]).Should().BeGreaterThanOrEqualTo(1);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(params (string Key, string Value)[] settings)
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingEntitlements:Enabled", "true");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static async Task<CentralDerivativeJobLease?> ClaimAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string workerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync(workerId, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
    }

    private static Guid ObservatoryOf(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, CentralDerivativeJobLease lease)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Id == lease.JobId)
            .Select(job => job.SourceArtifact!.Frame!.ObservatoryId)
            .Single();
    }

    private static Task<int> ActiveLeasesAsync(ApplicationDbContext db, Guid observatory)
        => db.CentralDerivativeJobs.AsNoTracking().CountAsync(job =>
            job.Status == CentralDerivativeJobStatus.Leased
            && job.LeaseExpiresAtUtc > DateTimeOffset.UtcNow
            && job.SourceArtifact!.Frame!.ObservatoryId == observatory);

    /// <summary>Seeds one observatory (one device) with <paramref name="sources"/> raw captures and their scheduled jobs.</summary>
    private static async Task<Guid> SeedObservatoryAsync(string scenario, int sources, TimeSpan? availableOffset = null)
    {
        var name = $"{scenario}-{Guid.NewGuid():N}"[..Math.Min(40, scenario.Length + 33)];
        var device = Guid.NewGuid();
        Guid sourceId = Guid.Empty;
        for (var sequence = 1; sequence <= sources; sequence++)
        {
            sourceId = await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                name, device, sequence, DateTimeOffset.UtcNow.AddMinutes(-10), SourcePayload, $"{scenario}-profile").ConfigureAwait(false);
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var observatory = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.Id == sourceId)
            .Select(artifact => artifact.Frame!.ObservatoryId)
            .SingleAsync().ConfigureAwait(false);
        if (availableOffset is { } offset)
        {
            var available = DateTimeOffset.UtcNow.Add(offset);
            await db.CentralDerivativeJobs.Where(job => job.SourceArtifact!.Frame!.ObservatoryId == observatory && job.AvailableAtUtc != null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.AvailableAtUtc, available)).ConfigureAwait(false);
        }
        return observatory;
    }

    private static async Task DisableClaimableJobsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
    }
}
