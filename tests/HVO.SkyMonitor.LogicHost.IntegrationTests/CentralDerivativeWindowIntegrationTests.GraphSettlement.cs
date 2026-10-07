using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Settled graph-owned window requirements and window-resolver ownership. Every test here runs on its own scratch
/// database with no hosted services, because the shared fixture's derivative worker runs the periodic resolver,
/// graph convergence and execution every second against the shared database and would race each assertion.
/// </summary>
public sealed partial class CentralDerivativeWindowIntegrationTests
{
    private const string RollingMeanGraphNode = "RollingMean";

    /// <summary>One more non-owned job than the resolver's periodic batch holds.</summary>
    private const int NonOwnedJobCount = 101;

    private static readonly JsonSerializerOptions SelectorOptions = CreateSelectorOptions();

    private static readonly string[] DependencyGraphNodes = ["Preview", "ImageQuality"];

    /// <summary>
    /// A graph-owned centered window settles each position as its neighbour arrives. Settled graph requirements are
    /// immutable, so a later affected-frame notification must leave them exactly as they were instead of re-stamping
    /// them; before the fix the second neighbour onward threw out of scheduling, which ingest answers with HTTP 500.
    /// Every notification after the first starts from the state that was stuck before the fix, so this also proves
    /// that state is released by the next notification without any persisted-state change.
    /// </summary>
    [TestMethod]
    public async Task GraphOwnedWindow_LaterNeighboursLeaveSettledPositionsUntouchedAndFreeze()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"graph-window-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        var settled = new Dictionary<Guid, SettledRequirement>();
        foreach (var sequence in new long[] { 100, 102, 98, 101, 99 })
        {
            var value = checked((ushort)((sequence - 97) * 10));
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(value), "compatible",
                graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
            var jobId = await ReadJobIdAsync(host.Services, sources[100], BuiltInProcessingRecipes.RollingMean)
                .ConfigureAwait(false);
            var current = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
            foreach (var (id, before) in settled)
            {
                current[id].Should().Be(before, "a settled graph-owned requirement is immutable");
            }
            foreach (var (id, requirement) in current.Where(item =>
                item.Value.State != CentralDerivativeInputResolutionState.Waiting))
            {
                settled.TryAdd(id, requirement);
            }
        }

        await WithDbAsync(host.Services, async db =>
        {
            var job = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .AsSplitQuery()
                .SingleAsync(item => item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            job.GraphExecutionId.Should().NotBeNull();
            job.WaitKind.Should().Be(CentralDerivativeWaitKind.Window);
            job.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            job.InputRequirements.Should().OnlyContain(item =>
                item.ResolutionState == CentralDerivativeInputResolutionState.Resolved);
            job.Inputs.OrderBy(item => item.Ordinal).Select(item => item.CaptureSequence)
                .Should().Equal(98, 99, 100, 101, 102);
            settled.Keys.Should().BeEquivalentTo(job.InputRequirements.Select(item => item.Id));
        }).ConfigureAwait(false);

        // The periodic resolver (ResolveWaitingAsync) runs this same per-job path over a still-waiting window.
        var waitingJobId = await ReadJobIdAsync(host.Services, sources[102], BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        var beforePeriodic = await ReadRequirementsAsync(host.Services, waitingJobId).ConfigureAwait(false);
        beforePeriodic.Values.Should().Contain(item => item.State == CentralDerivativeInputResolutionState.Resolved);
        await ResolveJobAsync(host.Services, waitingJobId).ConfigureAwait(false);
        (await ReadRequirementsAsync(host.Services, waitingJobId).ConfigureAwait(false))
            .Should().BeEquivalentTo(beforePeriodic);
    }

    /// <summary>
    /// A Waiting graph window with one Missing graph-owned requirement receives affected-frame notifications. The
    /// Missing requirement keeps its reason and timestamp, and the other positions still resolve.
    /// </summary>
    [TestMethod]
    public async Task GraphOwnedWindow_MissingSettledRequirementSurvivesNotificationsWhileOthersResolve()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"graph-window-missing-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var anchor = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 100, capturedBase, CreatePayload(30), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, anchor, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        var missingAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30);
        const string missingReason = "processing.graph.optional-dependency-omitted";
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobInputRequirements
            .Where(item => item.CentralDerivativeJobId == jobId && item.SequenceOffset == -2)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ResolutionState, CentralDerivativeInputResolutionState.Missing)
                .SetProperty(item => item.ResolutionReasonCode, missingReason)
                .SetProperty(item => item.ResolvedAtUtc, missingAtUtc))).ConfigureAwait(false);
        var missing = (await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false))
            .Single(item => item.Value.State == CentralDerivativeInputResolutionState.Missing);

        foreach (var sequence in new long[] { 101, 99, 102 })
        {
            _ = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(30), "compatible",
                graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        }
        await ResolveJobAsync(host.Services, jobId).ConfigureAwait(false);

        var requirements = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
        requirements[missing.Key].Should().Be(missing.Value);
        requirements.Where(item => item.Key != missing.Key).Select(item => item.Value.State)
            .Should().OnlyContain(state => state == CentralDerivativeInputResolutionState.Resolved);
        (await ReadJobAsync(host.Services, jobId).ConfigureAwait(false)).Status
            .Should().Be(CentralDerivativeJobStatus.Waiting, "a required position is missing until the deadline");
    }

    /// <summary>
    /// A settled graph input that fails the usability fence cannot be reopened or re-selected. The window completes
    /// through its missing-input policy with a distinct reason and the requirement stays exactly as it was settled.
    /// </summary>
    [TestMethod]
    public async Task GraphOwnedWindow_SettledInputThatBecomesUnusableCompletesThroughPolicy()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"graph-window-lost-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var anchor = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 100, capturedBase, CreatePayload(30), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        var neighbour = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 102, capturedBase, CreatePayload(50), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, anchor, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        var before = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
        before.Values.Should().Contain(item => item.State == CentralDerivativeInputResolutionState.Resolved
            && item.ExpectedCentralArtifactId == neighbour);

        // Not an invalidation: that path terminalizes the graph node itself. This is the defensive case of a settled
        // input becoming unusable underneath the resolver.
        await WithDbAsync(host.Services, db => db.CentralArtifacts.Where(item => item.Id == neighbour)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.ObjectState, CentralArtifactObjectState.Pending))).ConfigureAwait(false);
        await ResolveJobAsync(host.Services, jobId).ConfigureAwait(false);

        (await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false)).Should().BeEquivalentTo(before);
        var job = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
        job.StateReasonCode.Should().Be("window.settled-input-unusable");
    }

    /// <summary>
    /// A settled position keeps its selection. A second usable artifact for the same frame makes the position
    /// ambiguous to a fresh selection, but the resolver never re-selects a settled position, so the competing
    /// candidate is ignored: no terminal state, no write to that requirement, and the other positions still resolve.
    /// </summary>
    [TestMethod]
    public async Task GraphOwnedWindow_CompetingCandidateForASettledPositionIsIgnored()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"graph-window-competing-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var anchor = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 100, capturedBase, CreatePayload(30), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        var neighbour = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 102, capturedBase, CreatePayload(50), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, anchor, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        var before = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
        var settled = before.Where(item => item.Value.State != CentralDerivativeInputResolutionState.Waiting)
            .ToDictionary(item => item.Key, item => item.Value);
        settled.Values.Should().Contain(item => item.ExpectedCentralArtifactId == neighbour);

        // The competitor matches every selection predicate, so a fresh selection of that frame would be ambiguous.
        await WithDbAsync(host.Services, async db =>
        {
            var selectors = await db.CentralDerivativeJobInputRequirements.AsNoTracking()
                .Where(item => item.CentralDerivativeJobId == jobId)
                .Select(item => item.SelectorJson)
                .ToListAsync().ConfigureAwait(false);
            selectors.Select(json => JsonSerializer.Deserialize<ProcessingInputSelector>(json, SelectorOptions))
                .Should().OnlyContain(selector => selector != null && selector.RecipeIdentitySha256 == null);
        }).ConfigureAwait(false);
        var competitor = await AddCompetingRawAsync(host.Services, neighbour).ConfigureAwait(false);
        competitor.Should().NotBe(neighbour);
        _ = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 101, capturedBase, CreatePayload(40), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        await ResolveJobAsync(host.Services, jobId).ConfigureAwait(false);

        var after = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
        foreach (var (id, requirement) in settled)
        {
            after[id].Should().Be(requirement, "a settled position is never re-selected");
        }
        after.Values.Should().Contain(item => item.State == CentralDerivativeInputResolutionState.Resolved
            && item.ExpectedCentralArtifactId != neighbour && item.ExpectedCentralArtifactId != anchor,
            "the newly arrived neighbour still resolves");
        after.Values.Should().NotContain(item => item.ExpectedCentralArtifactId == competitor);
        var job = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        job.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.WaitingRequiredInput);
    }

    /// <summary>
    /// Non-window graph jobs belong to graph convergence. The window resolver must not touch them, so a Waiting
    /// Dependencies job's settled requirement and its job row survive every resolver entry unchanged, starting with
    /// the notification its own scheduling sends: before the ownership guard that notification resolved the job to
    /// Pending past an ordering predecessor that had not run.
    /// </summary>
    [TestMethod]
    public async Task GraphDependenciesJob_IsNeverResolvedByTheWindowResolver()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"graph-dependencies-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var source = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 100, DateTimeOffset.UtcNow.AddMinutes(-10), CreatePayload(30), "compatible",
            graphNodeIds: DependencyGraphNodes, services: host.Services, shapeGraphNode: OrderImageQualityAfterPreview)
            .ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, source, BuiltInProcessingRecipes.ImageQuality)
            .ConfigureAwait(false);
        var waiting = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);
        waiting.WaitKind.Should().Be(CentralDerivativeWaitKind.Dependencies);
        waiting.Status.Should().Be(CentralDerivativeJobStatus.Waiting,
            "the scheduling notification must leave a Dependencies job whose ordering predecessor is still Pending " +
            "to convergence; execution: " + await DescribeExecutionAsync(host.Services, jobId).ConfigureAwait(false));
        var before = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
        before.Values.Should().Contain(item => item.State == CentralDerivativeInputResolutionState.Resolved &&
            item.ExpectedCentralArtifactId == source, "convergence settled the raw input while the job waits");
        var beforeJob = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await LoadSchedulableArtifactAsync(db, source).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAffectedAsync(artifact, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await ResolveJobAsync(host.Services, jobId).ConfigureAwait(false);

        (await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false)).Should().BeEquivalentTo(before);
        var after = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);
        after.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        after.StateReasonCode.Should().Be(beforeJob.StateReasonCode);
        after.UpdatedAtUtc.Should().Be(beforeJob.UpdatedAtUtc);
    }

    /// <summary>
    /// The periodic pass selects only jobs the window resolver owns. More Waiting non-window graph jobs than one
    /// batch holds, all older than a resolvable window, must not hold that window out of the pass: they are never
    /// read, so they never rotate, and before the ownership filter they pinned the head of every batch.
    /// </summary>
    [TestMethod]
    public async Task PeriodicPass_NonOwnedGraphJobsNeverHoldTheBatchAheadOfAResolvableWindow()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var graphScenario = $"periodic-non-owned-{Guid.NewGuid():N}";
        var graphDevice = Guid.NewGuid();
        var nonOwned = new List<Guid>(NonOwnedJobCount);
        for (var sequence = 1L; sequence <= NonOwnedJobCount; sequence++)
        {
            var source = await SeedAndScheduleSourceAsync(
                graphScenario, graphDevice, sequence, capturedBase, CreatePayload(30), "compatible",
                graphNodeIds: DependencyGraphNodes, services: host.Services, shapeGraphNode: OrderImageQualityAfterPreview)
            .ConfigureAwait(false);
            nonOwned.Add(await ReadJobIdAsync(host.Services, source, BuiltInProcessingRecipes.ImageQuality)
                .ConfigureAwait(false));
        }
        var windowScenario = $"periodic-owned-{Guid.NewGuid():N}";
        var windowSource = await SeedAndScheduleSourceAsync(
            windowScenario, Guid.NewGuid(), 100, capturedBase, CreatePayload(30), "compatible",
            services: host.Services).ConfigureAwait(false);
        var windowJobId = await ReadLegacyJobIdAsync(host.Services, windowSource, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);

        var nonOwnedUpdatedAtUtc = DateTimeOffset.UnixEpoch;
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(item => nonOwned.Contains(item.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, nonOwnedUpdatedAtUtc)))
            .ConfigureAwait(false);
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(item => item.Id == windowJobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.UpdatedAtUtc, DateTimeOffset.UnixEpoch.AddDays(1)))).ConfigureAwait(false);
        var window = await ReadJobAsync(host.Services, windowJobId).ConfigureAwait(false);
        window.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        await WithDbAsync(host.Services, async db =>
        {
            var jobs = await db.CentralDerivativeJobs.AsNoTracking().Where(item => nonOwned.Contains(item.Id))
                .ToListAsync().ConfigureAwait(false);
            jobs.Should().HaveCount(NonOwnedJobCount).And.OnlyContain(item =>
                item.Status == CentralDerivativeJobStatus.Waiting
                && item.WaitKind == CentralDerivativeWaitKind.Dependencies
                && item.GraphExecutionId != null,
                "the scheduling notification must leave non-window graph jobs Waiting for convergence");
        }).ConfigureAwait(false);

        var now = window.ResolutionDeadlineUtc!.Value.AddTicks(1);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveWaitingAsync(now, CancellationToken.None).ConfigureAwait(false);
        }

        var resolved = await ReadJobAsync(host.Services, windowJobId).ConfigureAwait(false);
        resolved.Status.Should().Be(CentralDerivativeJobStatus.Skipped, "the owned window resolves in one pass");
        resolved.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
        await WithDbAsync(host.Services, async db =>
        {
            var jobs = await db.CentralDerivativeJobs.AsNoTracking().Where(item => nonOwned.Contains(item.Id))
                .ToListAsync().ConfigureAwait(false);
            jobs.Should().OnlyContain(item => item.Status == CentralDerivativeJobStatus.Waiting
                && item.UpdatedAtUtc == nonOwnedUpdatedAtUtc, "the window resolver never reads a job it does not own");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// A frozen graph window, with some positions settled and some still Waiting, changes nothing on a periodic pass
    /// before its deadline. It must still rotate, so it never pins the head of the batch, and its settled positions
    /// must stay exactly as settled.
    /// </summary>
    [TestMethod]
    public async Task PeriodicPass_RotatesAFrozenGraphWindowWithoutRewritingSettledPositions()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var scenario = $"periodic-frozen-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var anchor = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 100, capturedBase, CreatePayload(30), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        _ = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 102, capturedBase, CreatePayload(50), "compatible",
            graphNodeIds: [RollingMeanGraphNode], services: host.Services).ConfigureAwait(false);
        var jobId = await ReadJobIdAsync(host.Services, anchor, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        var before = await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false);
        before.Values.Should().Contain(item => item.State == CentralDerivativeInputResolutionState.Resolved);
        before.Values.Should().Contain(item => item.State == CentralDerivativeInputResolutionState.Waiting);
        var frozenUpdatedAtUtc = DateTimeOffset.UnixEpoch;
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(item => item.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, frozenUpdatedAtUtc)))
            .ConfigureAwait(false);
        var frozen = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);
        frozen.Status.Should().Be(CentralDerivativeJobStatus.Waiting);

        var now = DateTimeOffset.UtcNow;
        now.Should().BeBefore(frozen.ResolutionDeadlineUtc!.Value);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveWaitingAsync(now, CancellationToken.None).ConfigureAwait(false);
        }

        (await ReadRequirementsAsync(host.Services, jobId).ConfigureAwait(false)).Should().BeEquivalentTo(before);
        var rotated = await ReadJobAsync(host.Services, jobId).ConfigureAwait(false);
        rotated.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        rotated.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.WaitingRequiredInput);
        rotated.UpdatedAtUtc.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// One job that faults inside the periodic pass is contained: the pass logs it, rotates it out of the head of the
    /// next batch, and still resolves the rest of the batch. The fault here is a natural one, an unreadable input
    /// selector on a legacy window, which the resolver rejects before any write.
    /// </summary>
    [TestMethod]
    public async Task PeriodicPass_ContainsAFaultedJobAndResolvesTheRestOfTheBatch()
    {
        await using var host = await IsolatedWindowHost.CreateAsync().ConfigureAwait(false);
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var faultedSource = await SeedAndScheduleSourceAsync(
            $"periodic-faulted-{Guid.NewGuid():N}", Guid.NewGuid(), 100, capturedBase, CreatePayload(30), "compatible",
            services: host.Services).ConfigureAwait(false);
        var resolvableSource = await SeedAndScheduleSourceAsync(
            $"periodic-resolvable-{Guid.NewGuid():N}", Guid.NewGuid(), 100, capturedBase, CreatePayload(30),
            "compatible", services: host.Services).ConfigureAwait(false);
        var faultedJobId = await ReadLegacyJobIdAsync(host.Services, faultedSource, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        var resolvableJobId = await ReadLegacyJobIdAsync(
            host.Services, resolvableSource, BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobInputRequirements
            .Where(item => item.CentralDerivativeJobId == faultedJobId && item.SequenceOffset == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SelectorJson, "null")))
            .ConfigureAwait(false);
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(item => item.Id == faultedJobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, DateTimeOffset.UnixEpoch)))
            .ConfigureAwait(false);
        await WithDbAsync(host.Services, db => db.CentralDerivativeJobs.Where(item => item.Id == resolvableJobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.UpdatedAtUtc, DateTimeOffset.UnixEpoch.AddHours(1)))).ConfigureAwait(false);
        var resolvable = await ReadJobAsync(host.Services, resolvableJobId).ConfigureAwait(false);
        var faultedBefore = await ReadRequirementsAsync(host.Services, faultedJobId).ConfigureAwait(false);

        var now = resolvable.ResolutionDeadlineUtc!.Value.AddTicks(1);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveWaitingAsync(now, CancellationToken.None).ConfigureAwait(false);
        }

        var resolved = await ReadJobAsync(host.Services, resolvableJobId).ConfigureAwait(false);
        resolved.Status.Should().Be(CentralDerivativeJobStatus.Skipped, "a later job in the batch still resolves");
        resolved.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
        var faulted = await ReadJobAsync(host.Services, faultedJobId).ConfigureAwait(false);
        faulted.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        faulted.UpdatedAtUtc.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1), "the faulted job rotates");
        (await ReadRequirementsAsync(host.Services, faultedJobId).ConfigureAwait(false))
            .Should().BeEquivalentTo(faultedBefore, "the faulted job's resolution rolled back");

        // A window that arrives after the pass sorts ahead of the faulted job in the next batch.
        var laterSource = await SeedAndScheduleSourceAsync(
            $"periodic-later-{Guid.NewGuid():N}", Guid.NewGuid(), 100, capturedBase, CreatePayload(30), "compatible",
            services: host.Services).ConfigureAwait(false);
        var laterJobId = await ReadLegacyJobIdAsync(host.Services, laterSource, BuiltInProcessingRecipes.RollingMean)
            .ConfigureAwait(false);
        await WithDbAsync(host.Services, async db =>
        {
            var order = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(item => item.Status == CentralDerivativeJobStatus.Waiting
                    && (item.Id == faultedJobId || item.Id == laterJobId))
                .OrderBy(item => item.UpdatedAtUtc)
                .ThenBy(item => item.ResolutionDeadlineUtc)
                .ThenBy(item => item.CreatedAtUtc)
                .Select(item => item.Id)
                .ToListAsync().ConfigureAwait(false);
            order.Should().Equal(laterJobId, faultedJobId);
        }).ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateSelectorOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private sealed record SettledRequirement(
        CentralDerivativeInputResolutionState State,
        string? Reason,
        Guid? ExpectedCentralArtifactId,
        DateTimeOffset? ResolvedAtUtc);

    private static async Task<Guid> ReadJobIdAsync(IServiceProvider services, Guid sourceId, string recipeName)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
            .AsNoTracking()
            .Where(item => item.SourceCentralArtifactId == sourceId && item.RecipeName == recipeName
                && item.GraphExecutionId != null)
            .Select(item => item.Id)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<Guid> ReadLegacyJobIdAsync(IServiceProvider services, Guid sourceId, string recipeName)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
            .AsNoTracking()
            .Where(item => item.SourceCentralArtifactId == sourceId && item.RecipeName == recipeName
                && item.GraphExecutionId == null)
            .Select(item => item.Id)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<CentralDerivativeJob> ReadJobAsync(IServiceProvider services, Guid jobId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
    }

    /// <summary>
    /// ImageQuality, ordered after Preview. Convergence settles its raw input at once, and with no worker in the
    /// isolated host Preview stays Pending, so ImageQuality is a Waiting graph Dependencies job with a settled
    /// requirement. (The basic graph's only data-dependent node, WeatherCloudOverlay, needs a frozen clear-sky
    /// reference and environment, without which convergence skips it.)
    /// </summary>
    private static ProcessingGraphNodeDefinition OrderImageQualityAfterPreview(ProcessingGraphNodeDefinition node)
        => !string.Equals(node.Id, "ImageQuality", StringComparison.Ordinal)
            ? node
            : new ProcessingGraphNodeDefinition(
                node.Id,
                node.StepAlias,
                node.StepVersion,
                node.OperationKind,
                node.Enabled,
                node.FailurePolicy,
                node.Order,
                node.EffectiveOptions,
                [.. node.Dependencies, new ProcessingGraphDependencyDefinition(
                    "Preview", ProcessingGraphDependencyKind.Ordering)],
                node.Inputs,
                node.Outputs,
                node.Window,
                node.CapabilityLabels,
                node.HostApplicability);

    private static async Task<string> DescribeExecutionAsync(IServiceProvider services, Guid jobId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var executionId = await db.CentralDerivativeJobs.AsNoTracking().Where(item => item.Id == jobId)
            .Select(item => item.GraphExecutionId).SingleAsync().ConfigureAwait(false);
        var jobs = await db.CentralDerivativeJobs.AsNoTracking().Where(item => item.GraphExecutionId == executionId)
            .OrderBy(item => item.GraphNodeOrdinal)
            .Select(item => new { item.RecipeName, item.Status, item.WaitKind, item.StateReasonCode, item.LastError })
            .ToListAsync().ConfigureAwait(false);
        return string.Join("; ", jobs.Select(item =>
            $"{item.RecipeName}={item.Status}/{item.WaitKind}/{item.StateReasonCode}/{item.LastError}"));
    }

    private static async Task<Dictionary<Guid, SettledRequirement>> ReadRequirementsAsync(
        IServiceProvider services,
        Guid jobId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralDerivativeJobInputRequirements.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == jobId)
            .ToDictionaryAsync(
                item => item.Id,
                item => new SettledRequirement(
                    item.ResolutionState, item.ResolutionReasonCode, item.ExpectedCentralArtifactId, item.ResolvedAtUtc))
            .ConfigureAwait(false);
    }

    private static async Task ResolveJobAsync(IServiceProvider services, Guid jobId)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
            .ResolveAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WithDbAsync(IServiceProvider services, Func<ApplicationDbContext, Task> action)
    {
        await using var scope = services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a second usable raw artifact to the frame that owns <paramref name="sourceId"/>, with the same role and
    /// variant, so a fresh selection for that frame is ambiguous. It is not scheduled.
    /// </summary>
    private static async Task<Guid> AddCompetingRawAsync(IServiceProvider services, Guid sourceId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var original = await db.CentralArtifacts.AsNoTracking()
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);
        var layout = original.Layout!;
        var recipe = original.Recipe!;
        var competitor = new CentralArtifact
        {
            CentralFrameId = original.CentralFrameId,
            DevicePublicId = original.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = original.Role,
            RecipeVersion = original.RecipeVersion,
            ManifestSchemaVersion = original.ManifestSchemaVersion,
            MediaType = original.MediaType,
            ByteLength = original.ByteLength,
            ChecksumSha256 = original.ChecksumSha256,
            StorageReference = original.StorageReference,
            ReceivedAtUtc = original.ReceivedAtUtc,
            IdempotencyKey = HashText($"competing-{Guid.NewGuid():N}"),
            SourceId = original.SourceId,
            Variant = original.Variant,
            CreatedUtc = original.CreatedUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            ReconciledAtUtc = original.ReconciledAtUtc,
            Layout = new CentralArtifactLayout
            {
                Width = layout.Width,
                Height = layout.Height,
                StrideBytes = layout.StrideBytes,
                PixelFormat = layout.PixelFormat,
                ByteOrder = layout.ByteOrder,
                SampleDepthBits = layout.SampleDepthBits,
                ContainerDepthBits = layout.ContainerDepthBits,
                Packing = layout.Packing,
                CfaPattern = layout.CfaPattern,
                BlackLevel = layout.BlackLevel,
                WhiteLevel = layout.WhiteLevel,
                StoredCodeTransform = layout.StoredCodeTransform,
                LevelCodeSpace = layout.LevelCodeSpace,
                NativeWidth = layout.NativeWidth,
                NativeHeight = layout.NativeHeight,
                RoiX = layout.RoiX,
                RoiY = layout.RoiY,
                RoiWidth = layout.RoiWidth,
                RoiHeight = layout.RoiHeight,
                BinX = layout.BinX,
                BinY = layout.BinY,
                BinningAlgorithm = layout.BinningAlgorithm,
                CfaOriginX = layout.CfaOriginX,
                CfaOriginY = layout.CfaOriginY,
                ByteLength = layout.ByteLength
            },
            Recipe = new CentralArtifactRecipe
            {
                Name = recipe.Name,
                SemanticVersion = recipe.SemanticVersion,
                ImplementationVersion = recipe.ImplementationVersion,
                OptionsJson = recipe.OptionsJson,
                OptionsSha256 = recipe.OptionsSha256
            }
        };
        db.CentralArtifacts.Add(competitor);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return competitor.Id;
    }

    /// <summary>
    /// The full LogicHost service graph on a scratch database that startup initializes, with every hosted service
    /// removed so no worker, convergence pass or periodic resolver runs behind the test.
    /// </summary>
    private sealed class IsolatedWindowHost : IAsyncDisposable
    {
        private readonly WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> _factory;
        private readonly string _connection;

        private IsolatedWindowHost(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, string connection)
        {
            _factory = factory;
            _connection = connection;
        }

        public IServiceProvider Services => _factory.Services;

        public static async Task<IsolatedWindowHost> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                InitialCatalog = $"SkyMonitorWindowSettlement_{Guid.NewGuid():N}"
            }.ConnectionString;
            // Startup initialization takes its lock on the target database before migrating, so create it first.
            await using (var migration = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options))
            {
                await migration.Database.MigrateAsync().ConfigureAwait(false);
            }
            var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
                services =>
                {
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<ApplicationDbContext>();
                    services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connection));
                    services.RemoveAll<IHostedService>();
                }));
            var host = new IsolatedWindowHost(factory, connection);
            try
            {
                _ = factory.Services;
                await using var scope = factory.Services.CreateAsyncScope();
                (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                    .CanConnectAsync().ConfigureAwait(false)).Should().BeTrue();
                return host;
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
            SqlConnection.ClearAllPools();
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(_connection).Options);
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }
}
