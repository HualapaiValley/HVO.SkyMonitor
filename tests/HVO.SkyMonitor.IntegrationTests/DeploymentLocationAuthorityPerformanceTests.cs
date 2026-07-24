using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Process and SQL plan observations are intentionally synchronous outside measured production operations.")]
public sealed partial class DeploymentLocationAuthorityPerformanceTests
{
    private const int ObservatoryCount = 100;
    private const int AgentCount = 1_000;
    private const int FramesPerAgent = 10;
    private const int FrameCount = AgentCount * FramesPerAgent;
    private const int Warmups = 5;
    private const int MaximumDeadlockRetries = 3;
    private const string PreviousMigration = "20260721201807_AddCentralTransientReviewAndDerivatives";
    private static readonly int[] ConcurrencyLevels = [1, 8, 32];
    private static readonly DateTimeOffset InitialEffectiveUtc = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ChangedEffectiveUtc = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
    private static long deadlockRetries;

    [TestMethod]
    public async Task MigrationFleetReconciliationAndPaging_RecordPerformanceEvidence()
    {
        Assert.AreEqual("Release", typeof(DeploymentLocationAuthorityPerformanceTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration);
        var evidenceRun = Issue170PerformanceEvidence.Create();
        var fixture = AssemblyHooks.Fixture;
        var counter = new SqlCommandCounter();
        using var factory = CreateFactory(fixture, counter);
        var migration = await MeasureMigrationAsync(fixture.SqlServerConnectionString).ConfigureAwait(false);
        var populationBefore = await ReadDatabaseSizeAsync(factory).ConfigureAwait(false);
        var populationStarted = Stopwatch.GetTimestamp();
        var scale = await SeedScaleAsync(factory).ConfigureAwait(false);
        var initialization = await MeasurePhaseAsync(
            "initialization",
            32,
            scale.Agents,
            counter,
            (agent, cancellationToken) => ProposeAsync(factory, agent, agent.Initial, cancellationToken),
            warmups: 0)
            .ConfigureAwait(false);
        await AssertStateAsync(factory, scale.OwnerUserId, 0, 0, FrameCount).ConfigureAwait(false);
        var populationElapsed = Stopwatch.GetElapsedTime(populationStarted);
        var populationAfter = await ReadDatabaseSizeAsync(factory).ConfigureAwait(false);
        var population = new PopulationEvidence(
            ObservatoryCount,
            AgentCount,
            FrameCount,
            AgentCount,
            AgentCount,
            populationElapsed.TotalMilliseconds,
            populationBefore,
            populationAfter,
            populationAfter.DataUsedBytes - populationBefore.DataUsedBytes,
            populationAfter.LogUsedBytes - populationBefore.LogUsedBytes);

        var matched = new List<PhaseEvidence>();
        var changed = new List<PhaseEvidence>();
        var resolution = new List<PhaseEvidence>();
        var backlogs = new List<FleetBacklogEvidence>();
        PagingEvidence? paging = null;
        QueryEvidence? query = null;
        foreach (var concurrency in ConcurrencyLevels)
        {
            await WarmChangedAndResolutionAsync(factory, scale, concurrency).ConfigureAwait(false);

            foreach (var agent in scale.Agents.Take(Warmups))
            {
                await ProposeAsync(factory, agent, agent.Initial, CancellationToken.None).ConfigureAwait(false);
            }
            matched.Add(await MeasurePhaseAsync(
                "matched",
                concurrency,
                scale.Agents,
                counter,
                (agent, cancellationToken) => ProposeAsync(factory, agent, agent.Initial, cancellationToken))
                .ConfigureAwait(false));

            await PrepareChangedCapturesAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
            changed.Add(await MeasurePhaseAsync(
                "changed",
                concurrency,
                scale.Agents,
                counter,
                (agent, cancellationToken) => ProposeAsync(factory, agent, agent.Changed, cancellationToken))
                .ConfigureAwait(false));
            var backlog = await ReadBacklogAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
            Assert.AreEqual(AgentCount, backlog.PendingDeployments);
            Assert.AreEqual(FrameCount, backlog.MismatchFrames);
            backlogs.Add(new FleetBacklogEvidence(
                concurrency,
                backlog.PendingDeployments,
                backlog.MismatchFrames,
                backlog.OldestAgeSeconds));

            if (concurrency == 32)
            {
                paging = await MeasurePagingAsync(factory, scale.OwnerUserId, counter).ConfigureAwait(false);
                query = await MeasureQueryAsync(factory, scale.OwnerUserId, fixture.SqlServerConnectionString)
                    .ConfigureAwait(false);
            }

            var proposals = await ReadChangedProposalsAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
            resolution.Add(await MeasurePhaseAsync(
                "resolution",
                concurrency,
                proposals,
                counter,
                (proposal, cancellationToken) => ResolveAsync(factory, scale.OwnerUserId, proposal, cancellationToken))
                .ConfigureAwait(false));
            await AssertStateAsync(factory, scale.OwnerUserId, 0, 0, FrameCount).ConfigureAwait(false);
            await ResetChangedAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
        }

        var finalState = await ReadFinalStateAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
        Assert.AreEqual(ObservatoryCount, finalState.Observatories);
        Assert.AreEqual(AgentCount, finalState.Registrations);
        Assert.AreEqual(FrameCount, finalState.Frames);
        Assert.AreEqual(AgentCount, finalState.Deployments);
        Assert.AreEqual(AgentCount, finalState.Audits);
        Assert.AreEqual(FrameCount, finalState.ResolvedFrames);
        Assert.AreEqual(FrameCount, finalState.ExactBindingFrames);

        var evidence = new
        {
            Schema = "hvo-deployment-location-authority-performance-v1",
            Issue = 170,
            Revision = evidenceRun.Revision,
            Environment = new
            {
                Observed = evidenceRun.Environment,
                Topology = "in-process production services and SQL Server 2022 Testcontainer"
            },
            BuildCommand = Issue170PerformanceEvidence.BuildCommand,
            Command = evidenceRun.CreateTestCommand(
                "DeploymentLocationAuthorityPerformanceTests.MigrationFleetReconciliationAndPaging_RecordPerformanceEvidence"),
            Workload = new
            {
                ObservatoryCount,
                AgentCount,
                FramesPerAgent,
                FrameCount,
                Warmups,
                ConcurrencyLevels,
                ArrivalModel = "closed-loop Parallel.ForEachAsync; one production DI scope and DbContext per logical operation",
                Migration = "predecessor schema with 100 Observatories, 1,000 active registrations, and 10,000 legacy frames",
                Fleet = "1,000 acknowledged deployments with ten exact capture-location rows each; matched replay, changed outside-boundary proposal, owner resolution",
                Paging = "30 first-page samples at take=100 plus complete keyset traversal"
            },
            Method = new
            {
                Trials = "Run five separate Release test processes from the same immutable evidence revision with trial ordinals 1 through 5.",
                Percentiles = "nearest-rank over 1,000 measured logical operations after five warmups; paging uses 30 samples",
                Resources = "CPU, monotonic allocation delta, and sampled process working set cover fleet phases; migration/population cover CPU/allocation and database growth; paging covers latency and SQL commands.",
                Sql = "EF command interceptor plus SQL Server STATISTICS IO and SHOWPLAN_XML on the owner/status paging query",
                Retries = "SQL Server deadlock 1205 retries use a fresh production scope, are limited to three, remain inside logical-operation latency, and are reported per phase.",
                Reset = "Changed deployment rows and audits are removed between concurrency levels; initial acknowledged rows and 10,000 links are restored."
            },
            Measurements = new
            {
                Migration = migration,
                PostMigrationPopulation = population,
                Initialization = initialization,
                Matched = matched,
                Changed = changed,
                Resolution = resolution,
                Backlog = backlogs,
                Paging = paging,
                Query = query
            },
            IO = new
            {
                MinioRequests = 0,
                MinioBytes = 0,
                FileSystemBytes = "N/A: SQL Server container filesystem I/O is not instrumented.",
                SqlWireBytes = "Unavailable from Microsoft.Data.SqlClient diagnostics.",
                QueueCountAndBytes = "N/A: synchronous metadata path has no durable queue.",
                LohAndFullFrameCopies = "N/A: metadata-only path; the 10,000 frames contain no payload bytes."
            },
            Correctness = finalState,
            Result = new
            {
                Before = "N/A for new schema; this file is an absolute candidate baseline.",
                Acceptance = "All durable counts, exact capture bindings, pending/mismatch convergence, keyset uniqueness, restart backfill idempotence, and required indexes are pass/fail.",
                ResidualRisk = "Process counters exclude SQL Server. EF command count is not physical row count. SHOWPLAN and logical reads are environment/statistics specific."
            },
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        await evidenceRun.WriteTrialAsync(
            "deployment-location-authority-performance.json", evidence).ConfigureAwait(false);
    }

    private static async Task<ScaleFixture> SeedScaleAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        var owner = $"location-performance-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow;
        var observatories = Enumerable.Range(0, ObservatoryCount).Select(index => new Observatory
        {
            OwnerUserId = owner,
            Name = $"Location performance {index:D3}",
            LatitudeDegrees = 35 + index / 10_000d,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            AllowedDeploymentRadiusMeters = 1000,
            CreatedAtUtc = now,
            IsActive = true
        }).ToArray();
        db.Observatories.AddRange(observatories);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var backfilled = await ObservatoryLocationBackfill.RunAsync(db, TimeProvider.System).ConfigureAwait(false);
        Assert.AreEqual(ObservatoryCount, backfilled);
        var captureStartUtc = DateTimeOffset.UtcNow;

        var agents = new List<ScaleAgent>(AgentCount);
        var registrations = new List<DeviceRegistration>(AgentCount);
        for (var index = 0; index < AgentCount; index++)
        {
            var observatory = observatories[index % observatories.Length];
            var locationId = $"location-performance-{index:D4}";
            var registration = new DeviceRegistration
            {
                DeviceId = $"location-performance-device-{Guid.NewGuid():N}",
                ObservatoryId = observatory.Id,
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                FriendlyName = $"Location performance {index:D4}",
                OwnerUserId = owner,
                OwnerDisplayName = "Location Performance",
                OwnerConfirmationMethod = "SelfAttested",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = new string('A', 64),
                DevicePublicId = Guid.NewGuid(),
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            registrations.Add(registration);
            agents.Add(new ScaleAgent(
                registration.Id,
                DeploymentLocationSnapshot.Create(
                    locationId, 1, "observatory-fallback", null, InitialEffectiveUtc, null,
                    observatory.LatitudeDegrees, observatory.LongitudeDegrees, observatory.ElevationMeters, "UTC"),
                DeploymentLocationSnapshot.Create(
                    locationId, 2, "operator-survey", 2, ChangedEffectiveUtc, null,
                    observatory.LatitudeDegrees + 1, observatory.LongitudeDegrees, observatory.ElevationMeters, "UTC")));
        }
        db.DeviceRegistrations.AddRange(registrations);
        await db.SaveChangesAsync().ConfigureAwait(false);

        for (var offset = 0; offset < AgentCount; offset += 100)
        {
            var frames = new List<CentralFrame>(100 * FramesPerAgent);
            for (var index = offset; index < Math.Min(offset + 100, AgentCount); index++)
            {
                var registration = registrations[index];
                var deployment = agents[index].Initial;
                for (var frameIndex = 0; frameIndex < FramesPerAgent; frameIndex++)
                {
                    var frame = new CentralFrame
                    {
                        RegistrationId = registration.Id,
                        DevicePublicId = registration.DevicePublicId!.Value,
                        ObservatoryId = registration.ObservatoryId,
                        AgentId = registration.DeviceId,
                        FrameId = Guid.NewGuid(),
                        CapturedAtUtc = captureStartUtc.AddSeconds(frameIndex),
                        FirstReceivedAtUtc = captureStartUtc.AddSeconds(frameIndex),
                        LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved
                    };
                    frame.Location = new CentralCaptureLocation
                    {
                        CentralFrame = frame,
                        CentralFrameId = frame.Id,
                        LocationId = deployment.LocationId,
                        Version = deployment.Version,
                        Source = deployment.Source,
                        HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
                        EffectiveFromUtc = deployment.EffectiveFromUtc,
                        EffectiveUntilUtc = deployment.EffectiveUntilUtc
                    };
                    frames.Add(frame);
                }
            }
            db.CentralFrames.AddRange(frames);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        return new ScaleFixture(owner, agents);
    }

    private static async Task WarmChangedAndResolutionAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        ScaleFixture scale,
        int concurrency)
    {
        await PrepareChangedCapturesAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
        var warmups = scale.Agents.Take(Warmups).ToArray();
        await ExecuteAsync(warmups, concurrency,
            (agent, cancellationToken) => ProposeAsync(factory, agent, agent.Changed, cancellationToken), null)
            .ConfigureAwait(false);
        var proposals = (await ReadChangedProposalsAsync(factory, scale.OwnerUserId).ConfigureAwait(false))
            .Take(Warmups).ToArray();
        await ExecuteAsync(proposals, concurrency,
            (proposal, cancellationToken) => ResolveAsync(factory, scale.OwnerUserId, proposal, cancellationToken), null)
            .ConfigureAwait(false);
        await ResetChangedAsync(factory, scale.OwnerUserId).ConfigureAwait(false);
    }

    private static async Task<PhaseEvidence> MeasurePhaseAsync<T>(
        string scenario,
        int concurrency,
        IReadOnlyList<T> operations,
        SqlCommandCounter counter,
        Func<T, CancellationToken, Task> action,
        int warmups = Warmups)
    {
        StabilizeGc();
        counter.Reset();
        Interlocked.Exchange(ref deadlockRetries, 0);
        var latencies = new ConcurrentBag<double>();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var rssBefore = process.WorkingSet64;
        await using var rssSampler = new RssSampler(process, rssBefore);
        var started = Stopwatch.GetTimestamp();
        await ExecuteAsync(operations, concurrency, action, latencies).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var rssAfter = process.WorkingSet64;
        var samples = latencies.Order().ToArray();
        var rssPeak = Math.Max(
            Math.Max(rssBefore, rssAfter),
            await rssSampler.StopAsync().ConfigureAwait(false));
        Assert.AreEqual(operations.Count, samples.Length);
        return new PhaseEvidence(
            scenario,
            concurrency,
            warmups,
            operations.Count,
            elapsed.TotalMilliseconds,
            operations.Count / elapsed.TotalSeconds,
            Distribution(samples),
            samples,
            counter.Count,
            Interlocked.Read(ref deadlockRetries),
            cpuMilliseconds,
            allocatedBytes,
            rssBefore,
            rssAfter,
            rssPeak);
    }

    private static Task ExecuteAsync<T>(
        IReadOnlyList<T> operations,
        int concurrency,
        Func<T, CancellationToken, Task> action,
        ConcurrentBag<double>? latencies)
        => Parallel.ForEachAsync(
            operations,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (operation, cancellationToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                await action(operation, cancellationToken).ConfigureAwait(false);
                latencies?.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            });

    private static async Task ProposeAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        ScaleAgent agent,
        DeploymentLocationSnapshot deployment,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var registration = await db.DeviceRegistrations.SingleAsync(
                    item => item.Id == agent.RegistrationId, cancellationToken).ConfigureAwait(false);
                var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
                _ = await authority.ProposeAsync(
                    registration,
                    deployment,
                    deployment.Version == 1 ? DeploymentLocationSourceKind.Inherited : DeploymentLocationSourceKind.Manual,
                    "performance-harness",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < MaximumDeadlockRetries && IsDeadlock(exception))
            {
                Interlocked.Increment(ref deadlockRetries);
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task ResolveAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId,
        ResolutionTarget proposal,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
                var result = await authority.ResolveAsync(
                    proposal.Id,
                    ownerUserId,
                    DeploymentLocationResolutionStatus.Acknowledged,
                    "performance-approved",
                    proposal.ConcurrencyToken,
                    cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(DeploymentLocationMutationStatus.Applied, result.Status);
                return;
            }
            catch (Exception exception) when (attempt < MaximumDeadlockRetries && IsDeadlock(exception))
            {
                Interlocked.Increment(ref deadlockRetries);
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsDeadlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: 1205 })
            {
                return true;
            }
        }
        return false;
    }

    private static async Task PrepareChangedCapturesAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE location
            SET [DeviceDeploymentLocationVersionId] = NULL,
                [Version] = 2,
                [Source] = N'operator-survey',
                [HorizontalAccuracyMeters] = 2,
                [EffectiveFromUtc] = {ChangedEffectiveUtc},
                [EffectiveUntilUtc] = NULL
            FROM [CentralCaptureLocations] AS location
            INNER JOIN [CentralFrames] AS frame ON frame.[Id] = location.[CentralFrameId]
            INNER JOIN [DeviceRegistrations] AS registration ON registration.[Id] = frame.[RegistrationId]
            WHERE registration.[OwnerUserId] = {ownerUserId};

            UPDATE frame
            SET [LocationEvidenceState] = N'ReportedUnresolved'
            FROM [CentralFrames] AS frame
            INNER JOIN [DeviceRegistrations] AS registration ON registration.[Id] = frame.[RegistrationId]
            WHERE registration.[OwnerUserId] = {ownerUserId};
            """).ConfigureAwait(false);
    }

    private static async Task ResetChangedAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE location
            SET [DeviceDeploymentLocationVersionId] = deployment.[Id],
                [Version] = 1,
                [Source] = N'observatory-fallback',
                [HorizontalAccuracyMeters] = NULL,
                [EffectiveFromUtc] = {InitialEffectiveUtc},
                [EffectiveUntilUtc] = NULL
            FROM [CentralCaptureLocations] AS location
            INNER JOIN [CentralFrames] AS frame ON frame.[Id] = location.[CentralFrameId]
            INNER JOIN [DeviceRegistrations] AS registration ON registration.[Id] = frame.[RegistrationId]
            INNER JOIN [DeviceDeploymentLocationVersions] AS deployment
                ON deployment.[RegistrationId] = registration.[Id]
                AND deployment.[LocationId] = location.[LocationId]
                AND deployment.[Version] = 1
            WHERE registration.[OwnerUserId] = {ownerUserId};

            UPDATE frame
            SET [LocationEvidenceState] = N'ReportedResolved'
            FROM [CentralFrames] AS frame
            INNER JOIN [DeviceRegistrations] AS registration ON registration.[Id] = frame.[RegistrationId]
            WHERE registration.[OwnerUserId] = {ownerUserId};

            DELETE audit
            FROM [DeploymentLocationResolutionAudits] AS audit
            INNER JOIN [DeviceDeploymentLocationVersions] AS deployment
                ON deployment.[Id] = audit.[DeviceDeploymentLocationVersionId]
            INNER JOIN [DeviceRegistrations] AS registration
                ON registration.[Id] = deployment.[RegistrationId]
            WHERE registration.[OwnerUserId] = {ownerUserId} AND deployment.[Version] = 2;

            DELETE deployment
            FROM [DeviceDeploymentLocationVersions] AS deployment
            INNER JOIN [DeviceRegistrations] AS registration
                ON registration.[Id] = deployment.[RegistrationId]
            WHERE registration.[OwnerUserId] = {ownerUserId} AND deployment.[Version] = 2;

            UPDATE [DeviceRegistrations]
            SET [LocationEvidenceState] = N'DeploymentAcknowledged'
            WHERE [OwnerUserId] = {ownerUserId};
            """).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ResolutionTarget>> ReadChangedProposalsAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DeviceDeploymentLocationVersions.AsNoTracking()
            .Where(item => item.Registration!.OwnerUserId == ownerUserId && item.Version == 2)
            .OrderBy(item => item.Id)
            .Select(item => new ResolutionTarget(item.Id, item.ConcurrencyToken))
            .ToArrayAsync().ConfigureAwait(false);
    }

    private static async Task<BacklogSnapshot> ReadBacklogAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pending = await db.DeviceDeploymentLocationVersions.AsNoTracking()
            .Where(item => item.Registration!.OwnerUserId == ownerUserId
                && item.Status == DeploymentLocationResolutionStatus.Pending)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Oldest = group.Min(item => item.ProposedAtUtc) })
            .SingleAsync().ConfigureAwait(false);
        var mismatch = await db.CentralFrames.AsNoTracking().CountAsync(item =>
            item.LocationEvidenceState == CentralCaptureLocationEvidenceState.Mismatch
            && item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false);
        return new BacklogSnapshot(pending.Count, mismatch,
            Math.Max(0, (DateTimeOffset.UtcNow - pending.Oldest).TotalSeconds));
    }

    private static async Task AssertStateAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId,
        int pending,
        int mismatch,
        int resolved)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.AreEqual(pending, await db.DeviceDeploymentLocationVersions.AsNoTracking().CountAsync(item =>
            item.Registration!.OwnerUserId == ownerUserId
            && item.Status == DeploymentLocationResolutionStatus.Pending).ConfigureAwait(false));
        Assert.AreEqual(mismatch, await db.CentralFrames.AsNoTracking().CountAsync(item =>
            item.LocationEvidenceState == CentralCaptureLocationEvidenceState.Mismatch
            && item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false));
        Assert.AreEqual(resolved, await db.CentralFrames.AsNoTracking().CountAsync(item =>
            item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved
            && item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false));
    }

    private static async Task<PagingEvidence> MeasurePagingAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId,
        SqlCommandCounter counter)
    {
        for (var index = 0; index < Warmups; index++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
            _ = await authority.ListAsync(ownerUserId, DeploymentLocationResolutionStatus.Pending, 100)
                .ConfigureAwait(false);
        }
        StabilizeGc();
        counter.Reset();
        var samples = new double[30];
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < samples.Length; index++)
        {
            var sampleStarted = Stopwatch.GetTimestamp();
            await using var scope = factory.Services.CreateAsyncScope();
            var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
            var page = await authority.ListAsync(ownerUserId, DeploymentLocationResolutionStatus.Pending, 100)
                .ConfigureAwait(false);
            Assert.AreEqual(100, page.Proposals.Count);
            samples[index] = Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds;
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var firstPageCommands = counter.Count;
        counter.Reset();
        var ids = new HashSet<Guid>();
        DeploymentLocationProposalCursor? cursor = null;
        var pages = 0;
        do
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
            var page = await authority.ListAsync(
                ownerUserId, DeploymentLocationResolutionStatus.Pending, 100, cursor).ConfigureAwait(false);
            Assert.IsTrue(page.Proposals.All(item => ids.Add(item.Id)));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);
        Assert.AreEqual(AgentCount, ids.Count);
        Assert.AreEqual(10, pages);
        var traversalCommands = counter.Count;
        Array.Sort(samples);
        return new PagingEvidence(Warmups, samples.Length, 100, pages, ids.Count,
            elapsed.TotalMilliseconds, samples.Length / elapsed.TotalSeconds, Distribution(samples), samples,
            firstPageCommands, traversalCommands);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command is SQL generated by EF Core from the fixed production paging query; owner input remains encoded in EF's generated declaration.")]
    private static async Task<QueryEvidence> MeasureQueryAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId,
        string connectionString)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var queryText = db.DeviceDeploymentLocationVersions.AsNoTracking()
            .Include(item => item.Registration)
            .Include(item => item.ObservatoryLocationVersion)
            .Where(item => item.Registration!.OwnerUserId == ownerUserId
                && item.Status == DeploymentLocationResolutionStatus.Pending)
            .OrderByDescending(item => item.ProposedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(101)
            .ToQueryString();
        var indexes = await db.Database.SqlQuery<string>($"""
            SELECT [name] AS [Value]
            FROM [sys].[indexes]
            WHERE [object_id] = OBJECT_ID(N'[DeviceDeploymentLocationVersions]')
              AND [name] LIKE N'IX_DeviceDeploymentLocationVersions_RegistrationId%ProposedAtUtc%'
            """).ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(2, indexes.Length);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        var plans = new List<string>();
        await using (var planCommand = connection.CreateCommand())
        {
            planCommand.CommandText = "SET SHOWPLAN_XML ON;";
            await planCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            planCommand.CommandText = queryText;
            await using (var planReader = await planCommand.ExecuteReaderAsync().ConfigureAwait(false))
            {
                do
                {
                    while (await planReader.ReadAsync().ConfigureAwait(false))
                    {
                        for (var ordinal = 0; ordinal < planReader.FieldCount; ordinal++)
                        {
                            if (planReader.IsDBNull(ordinal))
                            {
                                continue;
                            }
                            var value = Convert.ToString(
                                planReader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
                            if (value?.Contains("ShowPlanXML", StringComparison.Ordinal) == true)
                            {
                                plans.Add(value);
                            }
                        }
                    }
                }
                while (await planReader.NextResultAsync().ConfigureAwait(false));
            }
            planCommand.CommandText = "SET SHOWPLAN_XML OFF;";
            await planCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        if (plans.Count == 0)
        {
            throw new InvalidOperationException("SQL Server did not return a query plan.");
        }
        await using (var ioCommand = connection.CreateCommand())
        {
            ioCommand.CommandText = $"SET STATISTICS IO ON; {queryText} SET STATISTICS IO OFF;";
            await using var reader = await ioCommand.ExecuteReaderAsync().ConfigureAwait(false);
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                }
            }
            while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        var logicalReads = LogicalReadsRegex().Matches(messages.ToString())
            .Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Sum();
        var plan = string.Join('\n', plans);
        var normalizedQuery = queryText.Replace(ownerUserId, "<owner>", StringComparison.Ordinal);
        var operators = plans.SelectMany(static value =>
        {
            var document = XDocument.Parse(value);
            return document.Descendants().Where(element => element.Name.LocalName == "RelOp")
                .SelectMany(relOp => relOp.Descendants().Where(element => element.Name.LocalName == "Object")
                    .Select(item => new PlanIndexUse(
                        (string?)item.Attribute("Index") ?? "unknown",
                        (string?)relOp.Attribute("PhysicalOp") ?? "unknown")));
        }).Distinct().OrderBy(item => item.Index, StringComparer.Ordinal).ThenBy(item => item.PhysicalOperator, StringComparer.Ordinal)
            .ToArray();
        Assert.IsTrue(operators.Any(item => indexes.Contains(item.Index.Trim('[', ']'), StringComparer.Ordinal)));
        return new QueryEvidence(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuery))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan))),
            logicalReads,
            operators);
    }

    private static async Task<MigrationEvidence> MeasureMigrationAsync(string baseConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = $"SkyMonitorIssue170Performance_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            await SeedPredecessorScaleAsync(db).ConfigureAwait(false);
            var before = await ReadDatabaseSizeAsync(db).ConfigureAwait(false);
            StabilizeGc();
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            await migrator.MigrateAsync().ConfigureAwait(false);
            var migrationElapsed = Stopwatch.GetElapsedTime(started);
            var migrationCpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var migrationAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var after = await ReadDatabaseSizeAsync(db).ConfigureAwait(false);
            var backfillStarted = Stopwatch.GetTimestamp();
            var backfilled = await ObservatoryLocationBackfill.RunAsync(db, TimeProvider.System).ConfigureAwait(false);
            var backfillElapsed = Stopwatch.GetElapsedTime(backfillStarted);
            db.ChangeTracker.Clear();
            var restartStarted = Stopwatch.GetTimestamp();
            var restartBackfilled = await ObservatoryLocationBackfill.RunAsync(db, TimeProvider.System)
                .ConfigureAwait(false);
            var restartElapsed = Stopwatch.GetElapsedTime(restartStarted);
            Assert.AreEqual(ObservatoryCount, backfilled);
            Assert.AreEqual(0, restartBackfilled);
            Assert.AreEqual(ObservatoryCount, await db.ObservatoryLocationVersions.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(AgentCount, await db.DeviceRegistrations.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(FrameCount, await db.CentralFrames.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(0, await db.DeviceDeploymentLocationVersions.CountAsync().ConfigureAwait(false));
            return new MigrationEvidence(
                ObservatoryCount,
                AgentCount,
                FrameCount,
                migrationElapsed.TotalMilliseconds,
                migrationCpu,
                migrationAllocated,
                before,
                after,
                after.DataAllocatedBytes - before.DataAllocatedBytes,
                after.LogAllocatedBytes - before.LogAllocatedBytes,
                after.DataUsedBytes - before.DataUsedBytes,
                after.LogUsedBytes - before.LogUsedBytes,
                backfilled,
                backfillElapsed.TotalMilliseconds,
                restartBackfilled,
                restartElapsed.TotalMilliseconds);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task SeedPredecessorScaleAsync(ApplicationDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            ;WITH numbers AS
            (
                SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY first.object_id, second.object_id) - 1 AS n
                FROM sys.all_objects AS first CROSS JOIN sys.all_objects AS second
            )
            INSERT INTO [Observatories]
                ([Id], [OwnerUserId], [Name], [LatitudeDegrees], [LongitudeDegrees], [ElevationMeters],
                 [TimeZoneId], [CreatedAtUtc], [UpdatedAtUtc], [IsActive])
            SELECT NEWID(), N'issue170-migration-performance', CONCAT(N'Migration Observatory ', n),
                   35 + n / 10000.0, -113, 500, N'UTC', SYSUTCDATETIME(), NULL, 1
            FROM numbers;

            ;WITH ordered_observatories AS
            (
                SELECT [Id], [Name], ROW_NUMBER() OVER (ORDER BY [Id]) AS rn
                FROM [Observatories] WHERE [OwnerUserId] = N'issue170-migration-performance'
            ),
            numbers AS
            (
                SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY first.object_id, second.object_id) - 1 AS n
                FROM sys.all_objects AS first CROSS JOIN sys.all_objects AS second
            )
            INSERT INTO [DeviceRegistrations]
                ([Id], [DeviceId], [ObservatoryId], [FriendlyName], [ObservatoryName],
                 [ObservatoryLatitudeDegrees], [ObservatoryLongitudeDegrees], [ObservatoryElevationMeters],
                 [ObservatoryTimeZoneId], [OwnerUserId], [OwnerDisplayName], [OwnerConfirmationMethod],
                 [Status], [VerificationCodeHash], [DevicePublicId], [IssuedAtUtc])
            SELECT NEWID(), CONCAT(N'migration-device-', n), ordered_observatories.[Id], CONCAT(N'Migration Device ', n),
                   ordered_observatories.[Name], 35, -113, 500, N'UTC', N'issue170-migration-performance',
                   N'Migration Performance', N'SelfAttested', N'Active', REPLICATE(N'A', 64), NEWID(), SYSUTCDATETIME()
            FROM numbers
            INNER JOIN ordered_observatories ON ordered_observatories.rn = (numbers.n % 100) + 1;

            ;WITH registrations AS
            (
                SELECT [Id], [DeviceId], [DevicePublicId], [ObservatoryId],
                       ROW_NUMBER() OVER (ORDER BY [Id]) AS rn
                FROM [DeviceRegistrations] WHERE [OwnerUserId] = N'issue170-migration-performance'
            ),
            numbers AS
            (
                SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY first.object_id, second.object_id) - 1 AS n
                FROM sys.all_objects AS first CROSS JOIN sys.all_objects AS second
            )
            INSERT INTO [CentralFrames]
                ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                 [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [SceneProvenanceJson])
            SELECT NEWID(), registrations.[Id], registrations.[DevicePublicId], registrations.[ObservatoryId],
                   registrations.[DeviceId], NEWID(), SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, NULL
            FROM numbers
            INNER JOIN registrations ON registrations.rn = (numbers.n % 1000) + 1;
            """).ConfigureAwait(false);
    }

    private static async Task<DatabaseSize> ReadDatabaseSizeAsync(ApplicationDbContext db)
    {
        var rows = await db.Database.SqlQuery<DatabaseFileSize>($"""
            SELECT [type_desc] AS [Type], SUM(CAST([size] AS bigint) * 8192) AS [AllocatedBytes],
                   SUM(CAST(FILEPROPERTY([name], 'SpaceUsed') AS bigint) * 8192) AS [UsedBytes]
            FROM [sys].[database_files]
            WHERE [type_desc] = N'ROWS'
            GROUP BY [type_desc]
            """).ToArrayAsync().ConfigureAwait(false);
        var data = rows.Single(item => item.Type == "ROWS");
        var logAllocated = await db.Database.SqlQuery<long>($"""
            SELECT SUM(CAST([size] AS bigint) * 8192) AS [Value]
            FROM [sys].[database_files]
            WHERE [type_desc] = N'LOG'
            """).SingleAsync().ConfigureAwait(false);
        var logUsed = await db.Database.SqlQuery<long>($"""
            SELECT CAST([used_log_space_in_bytes] AS bigint) AS [Value]
            FROM [sys].[dm_db_log_space_usage]
            """).SingleAsync().ConfigureAwait(false);
        return new DatabaseSize(data.AllocatedBytes, data.UsedBytes, logAllocated, logUsed);
    }

    private static async Task<DatabaseSize> ReadDatabaseSizeAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await ReadDatabaseSizeAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .ConfigureAwait(false);
    }

    private static async Task<FinalState> ReadFinalStateAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string ownerUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var exactBindingFrames = await db.CentralFrames.CountAsync(item =>
            item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved
            && item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId
            && item.Location.DeploymentLocation.Status == DeploymentLocationResolutionStatus.Acknowledged
            && item.Location.LocationId == item.Location.DeploymentLocation.LocationId
            && item.Location.Version == item.Location.DeploymentLocation.Version
            && item.Location.Source == item.Location.DeploymentLocation.Source
            && item.Location.HorizontalAccuracyMeters == item.Location.DeploymentLocation.HorizontalAccuracyMeters
            && item.Location.EffectiveFromUtc == item.Location.DeploymentLocation.EffectiveFromUtc
            && item.Location.EffectiveUntilUtc == item.Location.DeploymentLocation.EffectiveUntilUtc)
            .ConfigureAwait(false);
        return new FinalState(
            await db.Observatories.CountAsync(item => item.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.DeviceRegistrations.CountAsync(item => item.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.CentralFrames.CountAsync(item => item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.DeviceDeploymentLocationVersions.CountAsync(item => item.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.DeploymentLocationResolutionAudits.CountAsync(item => item.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.CentralFrames.CountAsync(item => item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved
                && item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.DeviceDeploymentLocationVersions.CountAsync(item => item.Status == DeploymentLocationResolutionStatus.Pending
                && item.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false),
            await db.CentralFrames.CountAsync(item => item.LocationEvidenceState == CentralCaptureLocationEvidenceState.Mismatch
                && item.Location!.DeploymentLocation!.Registration!.OwnerUserId == ownerUserId).ConfigureAwait(false),
            exactBindingFrames);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(
        IntegrationTestFixture fixture,
        SqlCommandCounter counter)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(fixture.SqlServerConnectionString);
                options.AddInterceptors(counter);
            });
        }));

    private static LatencyDistribution Distribution(double[] ordered)
        => new(ordered[0], Percentile(ordered, 0.50), Percentile(ordered, 0.95),
            Percentile(ordered, 0.99), ordered[^1]);

    private static double Percentile(double[] ordered, double percentile)
        => ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string RunGit(string root, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        return process.StandardOutput.ReadToEnd().Trim();
    }

    [GeneratedRegex(@"logical reads (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogicalReadsRegex();

    private sealed class SqlCommandCounter : DbCommandInterceptor
    {
        private long count;
        public long Count => Interlocked.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RssSampler : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task sampling;
        private long peak;

        public RssSampler(Process process, long initial)
        {
            this.process = process;
            peak = initial;
            sampling = SampleAsync();
        }

        public async Task<long> StopAsync()
        {
            cancellation.Cancel();
            await sampling.ConfigureAwait(false);
            return Interlocked.Read(ref peak);
        }

        public async ValueTask DisposeAsync()
        {
            if (!cancellation.IsCancellationRequested)
            {
                _ = await StopAsync().ConfigureAwait(false);
            }
            cancellation.Dispose();
        }

        private async Task SampleAsync()
        {
            try
            {
                while (true)
                {
                    process.Refresh();
                    var observed = process.WorkingSet64;
                    long current;
                    do
                    {
                        current = Interlocked.Read(ref peak);
                    }
                    while (observed > current && Interlocked.CompareExchange(ref peak, observed, current) != current);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private sealed record ScaleFixture(string OwnerUserId, IReadOnlyList<ScaleAgent> Agents);
    private sealed record ScaleAgent(Guid RegistrationId, DeploymentLocationSnapshot Initial, DeploymentLocationSnapshot Changed);
    private sealed record ResolutionTarget(Guid Id, Guid ConcurrencyToken);
    private sealed record BacklogSnapshot(int PendingDeployments, int MismatchFrames, double OldestAgeSeconds);
    private sealed record FleetBacklogEvidence(
        int Concurrency, int PendingDeployments, int MismatchFrames, double OldestAgeSeconds);
    private sealed record LatencyDistribution(double Minimum, double Median, double P95, double P99, double Maximum);
    private sealed record PhaseEvidence(
        string Scenario, int Concurrency, int Warmups, int MeasuredOperations, double ElapsedMilliseconds,
        double OperationsPerSecond, LatencyDistribution LatencyMilliseconds,
        IReadOnlyList<double> LatencySamplesMilliseconds, long SqlCommands, long DeadlockRetries,
        double CpuMilliseconds, long AllocatedBytes, long WorkingSetBeforeBytes, long WorkingSetAfterBytes,
        long WorkingSetObservedPeakBytes);
    private sealed record PagingEvidence(
        int Warmups, int MeasuredQueries, int PageSize, int TraversalPages, int UniqueRows,
        double ElapsedMilliseconds, double QueriesPerSecond, LatencyDistribution LatencyMilliseconds,
        IReadOnlyList<double> LatencySamplesMilliseconds, long FirstPageSqlCommands, long TraversalSqlCommands);
    private sealed record QueryEvidence(
        string NormalizedProductionQuerySha256, string PlanSha256, long LogicalReads,
        IReadOnlyList<PlanIndexUse> PlanIndexOperators);
    private sealed record PlanIndexUse(string Index, string PhysicalOperator);
    private sealed record DatabaseFileSize(string Type, long AllocatedBytes, long UsedBytes);
    private sealed record DatabaseSize(long DataAllocatedBytes, long DataUsedBytes, long LogAllocatedBytes, long LogUsedBytes);
    private sealed record MigrationEvidence(
        int Observatories, int Registrations, int Frames, double MigrationMilliseconds, double CpuMilliseconds,
        long AllocatedBytes, DatabaseSize Before, DatabaseSize After, long DataAllocatedGrowthBytes,
        long LogAllocatedGrowthBytes, long DataUsedGrowthBytes, long LogUsedGrowthBytes,
        int BackfilledObservatories, double BackfillMilliseconds,
        int RestartBackfilledObservatories, double RestartConvergenceMilliseconds);
    private sealed record PopulationEvidence(
        int Observatories,
        int Registrations,
        int Frames,
        int Deployments,
        int Audits,
        double ElapsedMilliseconds,
        DatabaseSize Before,
        DatabaseSize After,
        long DataUsedGrowthBytes,
        long LogUsedGrowthBytes);
    private sealed record FinalState(
        int Observatories, int Registrations, int Frames, int Deployments, int Audits, int ResolvedFrames,
        int PendingDeployments, int MismatchFrames, int ExactBindingFrames);
}
