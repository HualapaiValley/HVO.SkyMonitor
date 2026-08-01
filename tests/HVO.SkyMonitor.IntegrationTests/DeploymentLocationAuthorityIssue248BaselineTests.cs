using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only a captured parameterized production SELECT is replayed.")]
public sealed partial class DeploymentLocationAuthorityIssue248BaselineTests
{
    private const string ProductionRevision = "7db3becafd37ec2b0b8a61dbbcc3a9a070819f43";
    private const string EvidenceFile = "deployment-location-authority-baseline.json";
    private const string ManifestFile = "manifest.json";
    private const string ResolutionReason = "issue-248-baseline";
    private static readonly int[] Scales = [10, 100, 1_000, 10_000];
    private static readonly string[] SchedulerPlanMarkers =
        ["CentralProcessingOverrideVersions", "CentralDerivativeJobs", "CentralClearReferenceDesignations"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static FileStream? processLock;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RealScheduler_OneDeploymentScaling_RecordsBaselineEvidence()
    {
        var baseline = Environment.GetEnvironmentVariable("HVO_ISSUE_248_BASELINE_EVIDENCE") == "1";
        var smoke = Environment.GetEnvironmentVariable("HVO_ISSUE_248_SMOKE") == "1";
        if (!baseline && !smoke)
        {
            Assert.Inconclusive("Issue #248 is opt-in; set HVO_ISSUE_248_BASELINE_EVIDENCE=1 or HVO_ISSUE_248_SMOKE=1.");
        }
        if (baseline && smoke)
        {
            throw new InvalidOperationException("Smoke mode is non-claimable and cannot be combined with baseline mode.");
        }

        Assert.AreEqual("Release", typeof(DeploymentLocationAuthorityIssue248BaselineTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);
        var root = FindRepositoryRoot();
        AcquireLock();
        if (smoke)
        {
            var warmupSmoke = await RunScaleAsync(1, null, measured: false).ConfigureAwait(false);
            _ = await RunScaleAsync(10, warmupSmoke.Expectations, measured: true).ConfigureAwait(false);
            _ = await RunContentionAsync(warmupSmoke.Expectations, 10).ConfigureAwait(false);
            _ = await RunFailureRestartAsync(warmupSmoke.Expectations).ConfigureAwait(false);
            TestContext.WriteLine("Issue #248 non-claimable smoke completed: warmup + scale-10 natural/contention/failure paths; no evidence published.");
            return;
        }
        var source = await CaptureSourceAsync(root).ConfigureAwait(false);
        await ValidateSourceAsync(root, source).ConfigureAwait(false);
        var environment = await CaptureEnvironmentAsync(root).ConfigureAwait(false);
        var harnessHash = HarnessHash(root);

        var warmup = await RunScaleAsync(1, null, measured: false).ConfigureAwait(false);
        var expectations = warmup.Expectations;
        var workloadHash = Hash(JsonSerializer.Serialize(new
        {
            id = "W3M/issue-248-one-deployment-v1",
            Scales,
            concurrency = 1,
            arrivals = 0,
            eligibleRawArtifactsPerCapture = 1,
            expectations
        }));

        var runs = new List<ScaleRun>(Scales.Length);
        foreach (var scale in Scales)
        {
            runs.Add(await RunScaleAsync(scale, expectations, measured: true).ConfigureAwait(false));
        }
        var contention = await RunContentionAsync(expectations, 10_000).ConfigureAwait(false);
        var failureRestart = await RunFailureRestartAsync(expectations).ConfigureAwait(false);

        var evidence = new
        {
            Schema = "hvo-issue-248-deployment-location-baseline-v1",
            Issue = 248,
            Phase = "baseline",
            Source = ProjectSource(source),
            ProductionRevision,
            HarnessSha256 = harnessHash,
            Environment = environment,
            Workload = new
            {
                Id = "W3M/issue-248-one-deployment-v1",
                WorkloadSha256 = workloadHash,
                DeploymentsPerScale = 1,
                CaptureScales = Scales,
                AvailableReconstructableRawArtifactsPerCapture = 1,
                Concurrency = 1,
                ArrivalRatePerSecond = 0,
                InitialBacklogEqualsCaptureCount = true,
                UnmeasuredProductionGraphWarmups = 1,
                Trials = "one trial per process; trial ordinal 1..5",
                RecipeExpectations = expectations,
                Boundary = "ResolveAsync through serializable commit, including real CentralDerivativeJobScheduler persistence",
                Excluded = "host startup, migration, seeding, warmup, plan replay, retry verification and database cleanup"
            },
            Measurements = runs.Select(run => run.Public).ToArray(),
            Contention = contention.Public,
            FailureRestart = failureRestart.Public,
            Correctness = new
            {
                AuthorityAuditToken = "status/reason, one Pending-to-Acknowledged audit, registration state and changed concurrency token asserted",
                Bindings = "exact 10/100/1000/10000 ReportedResolved bindings and location facts asserted",
                Eligibility = "one Raw/Available/Complete artifact per capture asserted",
                SchedulerIdentities = "every active policy recipe is independently classified from fixture prerequisites; applicable request identities are computed with production identity code",
                Duplicates = "zero duplicate RequestIdentitySha256 groups asserted",
                FreshScopeRetry = "all artifacts are rescheduled in fresh scopes; rolling windows may add deterministic inputs/status transitions but no jobs or request identities"
            },
            MeasurementMethod = new
            {
                Endpoints = "elapsed, CPU, exact allocation and RSS end are captured immediately after ResolveAsync returns and before sampler shutdown or evidence queries",
                AllocationSampler = "Issue170AllocationSampler construction/setup delay occurs before GC stabilization and all operation start endpoints; sampled allocation remains process/harness-inclusive",
                AlignedStarts = "SQL observer and RSS sampler process setup/first-read readiness complete first; after a final Process.Refresh, the common boundary order is RSS reset/start, CPU baseline, exact-allocation baseline, sampled-allocation reset/start, then elapsed start. Boundary skew is recorded and must remain below 10 ms.",
                SchedulerPlans = "Exact production SELECT shapes and deterministic parameters are captured and replayed only in a separately configured, equivalently seeded same-scale database that is deleted before the natural measured database starts.",
                ProcessScope = "CPU, exact allocations, sampled allocations and RSS include the in-process test harness and direct production service graph; recurring hosted workers are suppressed",
                SqlObserver = "uses a distinct .Observer ApplicationName and filters only the measured ApplicationName"
            },
            Command = "DOTNET_gcServer=1 HVO_ISSUE_248_BASELINE_EVIDENCE=1 HVO_EVIDENCE_PHASE=baseline HVO_EVIDENCE_REVISION=<SOURCE_HEAD> HVO_EVIDENCE_PRODUCTION_REVISION=7db3becafd37ec2b0b8a61dbbcc3a9a070819f43 HVO_EVIDENCE_TRIAL=1..5 dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~DeploymentLocationAuthorityIssue248BaselineTests.RealScheduler_OneDeploymentScaling_RecordsBaselineEvidence",
            Limitations = new
            {
                DurableWorker = "N/A: baseline production has no durable deployment reconciliation work record or worker.",
                WorkerSignals = "N/A: pending/oldest-age/drain/retry/failure worker signals are candidate-only.",
                CandidateWorkerBoundaries = "N/A: durable work discovery, bounded state/scheduling batches and worker completion do not exist in baseline production.",
                RawPlanXml = "Not retained because plan XML may contain database names and generated parameter values; SHA-256 and bounded direct-operator row facts are retained.",
                SqlPhysicalBytes = "N/A: SqlClient does not expose attributable wire or physical storage bytes.",
                PayloadIo = "N/A: W3M is metadata-only and this path does not read MinIO payloads.",
                FileSystemIo = "N/A: SQL container filesystem bytes are not attributable to one operation.",
                ActiveLogPeak = "10 ms sampling can understate a shorter transaction-log peak."
            },
            WorkerSuppression = "AssemblyHooks recognizes the neutral issue #248 baseline/smoke switches before fixture startup; all child-factory IHostedService registrations are also removed.",
            SmokeCommand = "DOTNET_gcServer=1 HVO_ISSUE_248_SMOKE=1 dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~DeploymentLocationAuthorityIssue248BaselineTests.RealScheduler_OneDeploymentScaling_RecordsBaselineEvidence",
            Privacy = "Serialized JSON is bounded and scanned for configured/generated values plus generic credential, connection assignment, path, GUID and payload patterns before publication.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        var generated = runs.SelectMany(run => run.GeneratedValues)
            .Concat(contention.GeneratedValues).Concat(failureRestart.GeneratedValues).ToArray();
        var output = await PublishTrialAsync(root, source, evidence, generated).ConfigureAwait(false);
        await TryAggregateAsync(root, source, harnessHash, environment.FingerprintSha256, workloadHash)
            .ConfigureAwait(false);
        TestContext.WriteLine("Issue #248 baseline trial: {0}", Path.GetRelativePath(root, output));
    }

    private static async Task<ScaleRun> RunScaleAsync(int scale, IReadOnlyList<RecipeExpectation>? expectedRecipes, bool measured)
    {
        var fixture = AssemblyHooks.Fixture;
        var database = $"SkyMonitorIssue248_{scale}_{Guid.NewGuid():N}";
        var application = $"HVO.SkyMonitor.Issue248.{scale}.{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = database,
            ApplicationName = application
        }.ConnectionString;
        var probe = new Probe();
        var calls = new CallCounter();
        var factory = CreateFactory(fixture, connection, probe, calls);
        try
        {
            IReadOnlyList<PlanEvidence>? schedulerPlans = null;
            IReadOnlyDictionary<string, CommandSnapshot>? preResolutionPlanCommands = null;
            if (measured)
            {
                (schedulerPlans, preResolutionPlanCommands) = await CaptureEquivalentScaleSchedulerPlansAsync(scale)
                    .ConfigureAwait(false);
            }
            _ = factory.Services;
            var seeded = await SeedAsync(factory, scale).ConfigureAwait(false);
            var recipes = await ActiveRecipesAsync(factory, seeded.ObservatoryId).ConfigureAwait(false);
            var computedExpectations = BuildExpectations(recipes);
            if (expectedRecipes is not null)
            {
                CollectionAssert.AreEqual(expectedRecipes.ToArray(), computedExpectations);
            }
            var effectiveRecipes = recipes.Where(recipe => computedExpectations.Single(item =>
                item.RequestedRecipeIdentitySha256 == recipe.RequestedRecipeIdentitySha256).Applicable).ToArray();
            var initial = await BacklogAsync(factory, seeded.RegistrationId).ConfigureAwait(false);
            Assert.AreEqual(scale, initial.Count);
            var dbBefore = await DatabaseSizeAsync(connection).ConfigureAwait(false);
            var writesBefore = await ReadIndexOperationsAsync(connection).ConfigureAwait(false);
            calls.Reset();
            probe.Reset();
            Measurement? measurement = null;
            DeploymentLocationResolutionResult result;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                probe.Start(db.ContextId);
                if (measured)
                {
                    using var allocation = new Issue170AllocationSampler();
                    StabilizeGc();
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    _ = process.TotalProcessorTime;
                    _ = process.WorkingSet64;
                    using var cancel = new CancellationTokenSource();
                    var sqlReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var sqlTask = SampleSqlAsync(connection, application, sqlReady, cancel.Token);
                    var rssSampler = new RssSampler(cancel.Token);
                    await sqlReady.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    await rssSampler.Ready.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    process.Refresh();
                    var boundaryStarted = Stopwatch.GetTimestamp();
                    var rss = process.WorkingSet64;
                    rssSampler.Reset(rss);
                    var cpu = process.TotalProcessorTime;
                    var exactAllocation = GC.GetTotalAllocatedBytes(true);
                    allocation.Start();
                    var started = Stopwatch.GetTimestamp();
                    var resourceStartBoundarySkew = Stopwatch.GetElapsedTime(boundaryStarted, started);
                    Assert.IsLessThan(TimeSpan.FromMilliseconds(10), resourceStartBoundarySkew,
                        "Resource start boundary setup exceeded the documented negligible skew bound.");
                    TimeSpan elapsed;
                    TimeSpan cpuElapsed;
                    long exactAllocated;
                    long rssEnd;
                    Issue170AllocationMeasurement sampled;
                    try
                    {
                        result = await ResolveAsync(scope, seeded).ConfigureAwait(false);
                        elapsed = Stopwatch.GetElapsedTime(started);
                        process.Refresh();
                        cpuElapsed = process.TotalProcessorTime - cpu;
                        exactAllocated = GC.GetTotalAllocatedBytes(true) - exactAllocation;
                        rssEnd = process.WorkingSet64;
                        sampled = await allocation.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        probe.Stop();
                        await cancel.CancelAsync().ConfigureAwait(false);
                    }
                    var dbAfter = await DatabaseSizeAsync(connection).ConfigureAwait(false);
                    var sql = await sqlTask.ConfigureAwait(false);
                    Assert.IsNotEmpty(sql);
                    Assert.IsTrue(sql.Any(item => item.OpenTransactions > 0 && item.MaximumIsolationLevel == 4),
                        "SQL sampling must observe the attributed serializable authority transaction.");
                    var transactions = probe.Transactions.Snapshot();
                    measurement = new Measurement(
                        elapsed.TotalMilliseconds, transactions.Durations.Single(),
                        transactions.Starts, transactions.Commits, transactions.Rollbacks, transactions.Failures,
                        probe.Commands.Count, calls.Count, scale / elapsed.TotalSeconds,
                        calls.Count / elapsed.TotalSeconds, 0,
                        cpuElapsed.TotalMilliseconds,
                        exactAllocated,
                        sampled.SampledBytes, sampled.Samples, sampled.IntervalMilliseconds,
                        rss, rssEnd, await rssSampler.Completion.ConfigureAwait(false),
                        resourceStartBoundarySkew.TotalMicroseconds,
                        dbBefore, dbAfter,
                        dbAfter.DataAllocated - dbBefore.DataAllocated,
                        dbAfter.DataUsed - dbBefore.DataUsed,
                        dbAfter.LogAllocated - dbBefore.LogAllocated,
                        dbAfter.LogUsed - dbBefore.LogUsed,
                        sql.Count, SamplingMedian(sql), sql.Count == 0 ? 0 : sql.Max(item => item.OpenTransactions),
                        sql.Count == 0 ? 0 : sql.Max(item => item.ActiveLogBytes));
                }
                else
                {
                    try { result = await ResolveAsync(scope, seeded).ConfigureAwait(false); }
                    finally { probe.Stop(); }
                }
            }
            Assert.AreEqual(DeploymentLocationMutationStatus.Applied, result.Status);
            Assert.AreEqual(scale, calls.Count);
            var preRetryState = await StateAsync(factory, seeded).ConfigureAwait(false);
            AssertState(scale, seeded, preRetryState, effectiveRecipes, afterRetry: false);
            PlanEvidence? plan = null;
            IndexWriteEvidence? writeProxy = null;
            CommandEvidence? commands = null;
            if (measured)
            {
                measurement = measurement! with
                {
                    JobsPerSecond = preRetryState.Jobs / (measurement!.ElapsedMilliseconds / 1000d)
                };
                plan = await PlanAsync(connection, probe.Commands.Reconciliation
                    ?? throw new AssertFailedException("Reconciliation SELECT was not captured.")).ConfigureAwait(false);
                Assert.AreEqual(scale, plan.RowsReturned);
                commands = probe.Commands.Evidence();
                AssertRuntimePlanCommands(preResolutionPlanCommands!, probe.Commands);
                if (scale == 10) Assert.IsTrue(schedulerPlans!.Any(item => item.ExactParameterizedSelect.Contains("CentralDerivativeJobs", StringComparison.Ordinal)));
                var writesAfter = await ReadIndexOperationsAsync(connection).ConfigureAwait(false);
                writeProxy = IndexWriteEvidence.Create(writesBefore, writesAfter);
                Assert.AreEqual(scale, commands.Shapes.Where(shape =>
                    shape.ExactParameterizedText.Contains("CentralClearReferenceDesignations", StringComparison.Ordinal))
                    .Sum(shape => shape.Count),
                    "The non-applicable cloud recipe must retain its production clear-reference decision query cost.");
                await RetryAllAsync(factory, seeded).ConfigureAwait(false);
            }
            var convergedState = await StateAsync(factory, seeded).ConfigureAwait(false);
            AssertState(scale, seeded, convergedState, effectiveRecipes, afterRetry: measured);
            var retry = new RetryConvergence(
                preRetryState.Jobs == convergedState.Jobs,
                preRetryState.OrderedIdentitySetSha256 == convergedState.OrderedIdentitySetSha256,
                convergedState.Inputs - preRetryState.Inputs,
                "Allowed: standalone scheduling owns a transaction and invokes ResolveAffectedAsync; rolling-window inputs/status may converge without creating jobs.");
            var publicRun = new PublicScaleRun(scale, initial, measurement, preRetryState,
                new PostRetryConvergence(convergedState, retry), plan, schedulerPlans, writeProxy, commands);
            return new ScaleRun(publicRun, convergedState, computedExpectations,
                seeded.GeneratedValues.Append(database).Append(application).ToArray());
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            await DeleteDatabaseAsync(connection).ConfigureAwait(false);
        }
    }

    private static Task<DeploymentLocationResolutionResult> ResolveAsync(AsyncServiceScope scope, Seed seeded)
        => scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>().ResolveAsync(
            seeded.DeploymentId, seeded.OwnerId, DeploymentLocationResolutionStatus.Acknowledged,
            ResolutionReason, seeded.Token, cancellationToken: CancellationToken.None);

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(
        IntegrationTestFixture fixture, string connection, Probe probe, CallCounter calls)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(
                connection, sql => sql.CommandTimeout(300)).AddInterceptors(probe.Commands, probe.Transactions));
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.RemoveAll<IHostedService>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new CountingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(), calls));
        }));

    private static async Task<ContentionRun> RunContentionAsync(IReadOnlyList<RecipeExpectation> expectations, int count)
    {
        var fixture = AssemblyHooks.Fixture;
        var database = $"SkyMonitorIssue248_Contention_{Guid.NewGuid():N}";
        var application = $"HVO.SkyMonitor.Issue248.Contention.{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = database,
            ApplicationName = application
        }.ConnectionString;
        var probe = new Probe();
        var barrier = new MaterializationBarrier();
        probe.Commands.Barrier = barrier;
        var calls = new CallCounter();
        var factory = CreateFactory(fixture, connection, probe, calls);
        try
        {
            _ = factory.Services;
            var seed = await SeedAsync(factory, count).ConfigureAwait(false);
            CollectionAssert.AreEqual(expectations.ToArray(), BuildExpectations(
                await ActiveRecipesAsync(factory, seed.ObservatoryId).ConfigureAwait(false)));
            var authorityStarted = Stopwatch.GetTimestamp();
            var resolution = ResolveWithProbeAsync(factory, seed, probe);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var writerApplication = application + ".Writer";
            var writerOffered = Stopwatch.GetTimestamp();
            var writer = RunConflictingWriterAsync(connection, writerApplication, seed.RegistrationId);
            var observation = await ObserveWriterAsync(
                connection, application + ".Observer", application, writerApplication,
                writerOffered, writer.SessionId, writer.Completion)
                .ConfigureAwait(false);
            barrier.Release.TrySetResult();
            var result = await resolution.WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            var authorityDuration = Stopwatch.GetElapsedTime(authorityStarted).TotalMilliseconds;
            var affected = await writer.Completion.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var writerCompleted = Stopwatch.GetElapsedTime(writerOffered).TotalMilliseconds;
            Assert.AreEqual(1, affected);
            Assert.AreEqual(DeploymentLocationMutationStatus.Applied, result.Status);
            Assert.IsTrue(observation.BlockerIsAuthority);
            Assert.IsGreaterThan((short)0, observation.BlockingSessionId);
            Assert.IsTrue(observation.WaitType.StartsWith("LCK_M_", StringComparison.Ordinal));
            Assert.AreEqual((short)2, observation.SessionIsolationLevel);
            Assert.AreEqual((short)4, observation.BlockerIsolationLevel);
            Assert.AreNotEqual("unknown", observation.ResourceType);
            Assert.AreNotEqual("unknown", observation.RequestMode);
            Assert.AreEqual("WAIT", observation.RequestStatus);
            Assert.AreEqual("TRANSACTION", observation.RequestOwnerType);
            var state = await StateAsync(factory, seed).ConfigureAwait(false);
            AssertState(count, seed, state,
                (await ActiveRecipesAsync(factory, seed.ObservatoryId).ConfigureAwait(false)).Where(recipe =>
                    expectations.Single(item => item.RequestedRecipeIdentitySha256 == recipe.RequestedRecipeIdentitySha256).Applicable).ToArray(),
                afterRetry: false);
            return new ContentionRun(new(
                count, "one-row DeviceRegistrations LastSeenUtc UPDATE", true,
                observation.BlockingSessionId, true, observation.BlockerApplicationSha256,
                observation.WaitType, observation.SessionIsolationLevel,
                observation.BlockerIsolationLevel,
                observation.ResourceType, observation.RequestMode, observation.RequestStatus,
                observation.RequestOwnerType, observation.OfferToObservationMilliseconds,
                writerCompleted, authorityDuration, 1, affected,
                "Barrier released immediately after exact SQL attribution; excluded from natural timings."),
                seed.GeneratedValues.Append(database).Append(application).Append(writerApplication).ToArray());
        }
        finally
        {
            barrier.Release.TrySetResult();
            await factory.DisposeAsync().ConfigureAwait(false);
            await DeleteDatabaseAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task<FailureRestartRun> RunFailureRestartAsync(IReadOnlyList<RecipeExpectation> expectations)
    {
        const int count = 10;
        var fixture = AssemblyHooks.Fixture;
        var database = $"SkyMonitorIssue248_Failure_{Guid.NewGuid():N}";
        var application = $"HVO.SkyMonitor.Issue248.Failure.{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = database,
            ApplicationName = application
        }.ConnectionString;
        Seed seed;
        var failedCalls = new CallCounter { FailAfterDelegatedCall = 2 };
        var failedFactory = CreateFactory(fixture, connection, new Probe(), failedCalls);
        try
        {
            _ = failedFactory.Services;
            seed = await SeedAsync(failedFactory, count).ConfigureAwait(false);
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                await using var scope = failedFactory.Services.CreateAsyncScope();
                _ = await ResolveAsync(scope, seed).ConfigureAwait(false);
            }).ConfigureAwait(false);
            Assert.AreEqual("issue-248-injected-scheduler-failure", failure.Message);
            Assert.AreEqual(failedCalls.FailAfterDelegatedCall, failedCalls.Count);
            var rollback = await ReadRollbackStateAsync(failedFactory, seed).ConfigureAwait(false);
            Assert.AreEqual(new RollbackState("Pending", false, "DeploymentPending", count, 0, 0, 0, 0, 0, 0), rollback);
        }
        finally
        {
            await failedFactory.DisposeAsync().ConfigureAwait(false);
        }
        var retryFactory = CreateFactory(fixture, connection, new Probe(), new CallCounter());
        try
        {
            _ = retryFactory.Services;
            await using (var scope = retryFactory.Services.CreateAsyncScope())
            {
                var result = await ResolveAsync(scope, seed).ConfigureAwait(false);
                Assert.AreEqual(DeploymentLocationMutationStatus.Applied, result.Status);
            }
            await RetryAllAsync(retryFactory, seed).ConfigureAwait(false);
            var recipes = await ActiveRecipesAsync(retryFactory, seed.ObservatoryId).ConfigureAwait(false);
            CollectionAssert.AreEqual(expectations.ToArray(), BuildExpectations(recipes));
            var state = await StateAsync(retryFactory, seed).ConfigureAwait(false);
            AssertState(count, seed, state, recipes.Where(recipe => expectations.Single(item =>
                item.RequestedRecipeIdentitySha256 == recipe.RequestedRecipeIdentitySha256).Applicable).ToArray(), afterRetry: true);
            return new FailureRestartRun(new(2, true, true, true, state.Jobs, state.Requirements, state.Inputs,
                state.OrderedIdentitySetSha256, "Fresh WebApplicationFactory and DI scopes reused the same isolated database."),
                seed.GeneratedValues.Append(database).Append(application).ToArray());
        }
        finally
        {
            await retryFactory.DisposeAsync().ConfigureAwait(false);
            await DeleteDatabaseAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task<DeploymentLocationResolutionResult> ResolveWithProbeAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        Seed seed,
        Probe probe)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        probe.Start(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ContextId);
        try { return await ResolveAsync(scope, seed).ConfigureAwait(false); }
        finally { probe.Stop(); }
    }

    private static WriterRun RunConflictingWriterAsync(string connection, string application, Guid registrationId)
    {
        var session = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> ExecuteAsync()
        {
            var writerConnection = new SqlConnectionStringBuilder(connection) { ApplicationName = application }.ConnectionString;
            await using var sql = new SqlConnection(writerConnection);
            await sql.OpenAsync().ConfigureAwait(false);
            await using (var spid = sql.CreateCommand())
            {
                spid.CommandText = "SELECT @@SPID;";
                session.TrySetResult(Convert.ToInt32(await spid.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
            }
            await using var command = sql.CreateCommand();
            command.CommandText = "UPDATE [DeviceRegistrations] SET [LastSeenUtc] = @value WHERE [Id] = @id;";
            command.CommandTimeout = 660;
            command.Parameters.Add(new SqlParameter("@value", System.Data.SqlDbType.DateTimeOffset) { Value = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero) });
            command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = registrationId });
            return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return new WriterRun(session.Task, ExecuteAsync());
    }

    private static async Task<WriterObservation> ObserveWriterAsync(
        string connection, string observerApplication, string authorityApplication, string writerApplication,
        long writerOffered, Task<int> sessionTask, Task completion)
    {
        var sessionId = await sessionTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var observerConnection = new SqlConnectionStringBuilder(connection) { ApplicationName = observerApplication }.ConnectionString;
        await using var sql = new SqlConnection(observerConnection); await sql.OpenAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        while (!completion.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            await using var command = sql.CreateCommand();
            command.CommandText = """
                SELECT TOP(1) request.[blocking_session_id], request.[wait_type], session.[transaction_isolation_level],
                    blocker.[program_name], blocker.[transaction_isolation_level], resource.[resource_type], resource.[request_mode],
                    resource.[request_status], resource.[request_owner_type]
                FROM [sys].[dm_exec_requests] AS request
                INNER JOIN [sys].[dm_exec_sessions] AS session ON session.[session_id]=request.[session_id]
                LEFT JOIN [sys].[dm_exec_sessions] AS blocker ON blocker.[session_id]=request.[blocking_session_id]
                LEFT JOIN [sys].[dm_tran_locks] AS resource ON resource.[request_session_id]=request.[session_id]
                    AND resource.[request_status]=N'WAIT'
                WHERE request.[session_id]=@session AND session.[program_name]=@writer AND request.[blocking_session_id]<>0;
                """;
            command.Parameters.Add(new SqlParameter("@session", System.Data.SqlDbType.Int) { Value = sessionId });
            command.Parameters.Add(new SqlParameter("@writer", System.Data.SqlDbType.NVarChar, 128) { Value = writerApplication });
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var blockerApplication = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? string.Empty : reader.GetString(3);
                return new WriterObservation(reader.GetInt16(0), reader.GetString(1), reader.GetInt16(2),
                    blockerApplication == authorityApplication, Hash(blockerApplication),
                    reader.GetInt16(4),
                    await reader.IsDBNullAsync(5).ConfigureAwait(false) ? "unknown" : reader.GetString(5),
                    await reader.IsDBNullAsync(6).ConfigureAwait(false) ? "unknown" : reader.GetString(6),
                    await reader.IsDBNullAsync(7).ConfigureAwait(false) ? "unknown" : reader.GetString(7),
                    await reader.IsDBNullAsync(8).ConfigureAwait(false) ? "unknown" : reader.GetString(8),
                    Stopwatch.GetElapsedTime(writerOffered).TotalMilliseconds);
            }
            await Task.Delay(1).ConfigureAwait(false);
        }
        throw new AssertFailedException("Conflicting writer was not observed blocked by the authority transaction.");
    }

    private static async Task<RollbackState> ReadRollbackStateAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Seed seed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deployment = await db.DeviceDeploymentLocationVersions.AsNoTracking().SingleAsync(item => item.Id == seed.DeploymentId).ConfigureAwait(false);
        var registration = await db.DeviceRegistrations.AsNoTracking().SingleAsync(item => item.Id == seed.RegistrationId).ConfigureAwait(false);
        return new(deployment.Status.ToString(), deployment.ConcurrencyToken != seed.Token, registration.LocationEvidenceState.ToString(),
            await db.CentralFrames.CountAsync(item => item.RegistrationId == seed.RegistrationId
                && item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedUnresolved).ConfigureAwait(false),
            await db.DeploymentLocationResolutionAudits.CountAsync(item => item.DeviceDeploymentLocationVersionId == seed.DeploymentId).ConfigureAwait(false),
            await db.CentralCaptureLocations.CountAsync(item => item.DeviceDeploymentLocationVersionId == seed.DeploymentId).ConfigureAwait(false),
            await db.CentralDerivativeJobs.CountAsync(item => item.SourceArtifact!.Frame!.RegistrationId == seed.RegistrationId).ConfigureAwait(false),
            await db.CentralDerivativeJobInputRequirements.CountAsync(item => item.Job!.SourceArtifact!.Frame!.RegistrationId == seed.RegistrationId).ConfigureAwait(false),
            await db.CentralDerivativeJobInputs.CountAsync(item => item.Job!.SourceArtifact!.Frame!.RegistrationId == seed.RegistrationId).ConfigureAwait(false),
            await db.CentralDerivativeJobCanonicalInputs.CountAsync(item => item.Job!.SourceArtifact!.Frame!.RegistrationId == seed.RegistrationId).ConfigureAwait(false));
    }
}

public sealed partial class DeploymentLocationAuthorityIssue248BaselineTests
{
    private sealed class CountingScheduler(CentralDerivativeJobScheduler inner, CallCounter counter) : ICentralDerivativeJobScheduler
    {
        public async Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken token)
        {
            var ordinal = counter.Increment();
            await inner.EnsureRequiredJobsAsync(artifact, now, token).ConfigureAwait(false);
            if (ordinal == counter.FailAfterDelegatedCall)
            {
                throw new InvalidOperationException("issue-248-injected-scheduler-failure");
            }
        }
        public Task EnsureRequiredJobsAsync(Guid device, Guid artifact, DateTimeOffset now, CancellationToken token) => inner.EnsureRequiredJobsAsync(device, artifact, now, token);
        public Task<Guid?> EnsureTransientContextConvergenceAsync(Guid job, DateTimeOffset now, CancellationToken token) => inner.EnsureTransientContextConvergenceAsync(job, now, token);
        public CentralDerivativeJob CreateHybridTransientJob(CentralDerivativeRecipe recipe, IReadOnlyList<CentralArtifact> sources, TransientCandidateSubmissionEnvelopeV1 envelope, string agent, DateTimeOffset now) => inner.CreateHybridTransientJob(recipe, sources, envelope, agent, now);
    }

    private sealed class CallCounter
    {
        private long count;
        internal long Count => Interlocked.Read(ref count);
        internal long FailAfterDelegatedCall { get; set; } = -1;
        internal long Increment() => Interlocked.Increment(ref count);
        internal void Reset() => Interlocked.Exchange(ref count, 0);
    }

    private sealed class Probe
    {
        internal CommandProbe Commands { get; } = new(); internal TransactionProbe Transactions { get; } = new();
        internal void Reset() { Commands.Reset(); Transactions.Reset(); }
        internal void Start(DbContextId id) { Commands.Start(id); Transactions.Start(id); }
        internal void Stop() { Commands.Stop(); Transactions.Stop(); }
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<string, ShapeCounter> shapes = new(); private long count; private int enabled; private DbContextId context; private CommandSnapshot? reconciliation;
        internal long Count => Interlocked.Read(ref count); internal CommandSnapshot? Reconciliation => Volatile.Read(ref reconciliation);
        internal MaterializationBarrier? Barrier { get; set; }
        internal void Reset() { shapes.Clear(); Interlocked.Exchange(ref count, 0); Volatile.Write(ref reconciliation, null); }
        internal void Start(DbContextId id) { context = id; Volatile.Write(ref enabled, 1); }
        internal void Stop() => Volatile.Write(ref enabled, 0);
        internal CommandEvidence Evidence() { var values = shapes.Values.Select(item => item.Evidence()).OrderBy(item => item.Hash).ToArray(); return new(Count, values.Length, Hash(string.Join('\n', values.Select(item => $"{item.Hash}:{item.Count}"))), values); }
        internal IEnumerable<CommandSnapshot> SelectSnapshots(string marker)
            => shapes.Values.Select(item => item.Snapshot)
                .Where(item => item.Text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    && item.Text.Contains(marker, StringComparison.Ordinal))
                .OrderBy(item => item.Hash, StringComparer.Ordinal);
        internal bool ObservedParameters(string shape, string parameterValueSetSha256)
            => shapes.TryGetValue(shape, out var value) && value.ObservedParameters(parameterValueSetSha256);
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result) { Record(command, data.Context); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default) { Record(command, data.Context); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data, InterceptionResult<int> result) { Record(command, data.Context); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken token = default) { Record(command, data.Context); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData data, InterceptionResult<object> result) { Record(command, data.Context); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<object> result, CancellationToken token = default) { Record(command, data.Context); return ValueTask.FromResult(result); }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData data,
            DbDataReader result,
            CancellationToken token = default)
        {
            if (Barrier is { } barrier && Target(data.Context) && IsReconciliation(command.CommandText))
            {
                barrier.Reached.TrySetResult();
                await barrier.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            }
            return result;
        }
        private void Record(DbCommand command, DbContext? db)
        {
            if (!Target(db)) return;
            var snapshot = CommandSnapshot.Create(command); Interlocked.Increment(ref count); shapes.GetOrAdd(snapshot.Shape, _ => new(snapshot)).Increment(snapshot);
            if (IsReconciliation(command.CommandText)) Interlocked.CompareExchange(ref reconciliation, snapshot, null);
        }
        private bool Target(DbContext? db) => Volatile.Read(ref enabled) != 0 && db is not null && db.ContextId.Equals(context);
        private static bool IsReconciliation(string text) => text.Contains("FROM [CentralCaptureLocations]", StringComparison.Ordinal) && text.Contains("[CentralArtifacts]", StringComparison.Ordinal);
    }

    private sealed class TransactionProbe : DbTransactionInterceptor
    {
        private readonly ConcurrentDictionary<DbTransaction, long> starts = new(); private readonly ConcurrentQueue<double> durations = new();
        private int enabled; private DbContextId context; private long started, committed, rolledBack, failed;
        internal void Reset() { starts.Clear(); durations.Clear(); started = committed = rolledBack = failed = 0; }
        internal void Start(DbContextId id) { context = id; Volatile.Write(ref enabled, 1); }
        internal void Stop() => Volatile.Write(ref enabled, 0);
        internal Transactions Snapshot() => new(started, committed, rolledBack, failed, durations.ToArray());
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData data, DbTransaction result, CancellationToken token = default) { if (Target(data.Context)) { starts[result] = Stopwatch.GetTimestamp(); Interlocked.Increment(ref started); } return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default) { if (Target(data.Context)) { if (starts.TryRemove(transaction, out var value)) durations.Enqueue(Stopwatch.GetElapsedTime(value).TotalMilliseconds); Interlocked.Increment(ref committed); } return Task.CompletedTask; }
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default) { if (Target(data.Context)) { starts.TryRemove(transaction, out _); Interlocked.Increment(ref rolledBack); } return Task.CompletedTask; }
        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData data, CancellationToken token = default) { if (Target(data.Context)) { starts.TryRemove(transaction, out _); Interlocked.Increment(ref failed); } return Task.CompletedTask; }
        private bool Target(DbContext? db) => Volatile.Read(ref enabled) != 0 && db is not null && db.ContextId.Equals(context);
    }

    private sealed class ShapeCounter
    {
        private readonly CommandSnapshot snapshot;
        private readonly ConcurrentDictionary<string, byte> parameterValueSets = new(StringComparer.Ordinal);
        private long count;
        internal ShapeCounter(CommandSnapshot snapshot) => this.snapshot = snapshot;
        internal CommandSnapshot Snapshot => snapshot;
        internal void Increment(CommandSnapshot observed)
        {
            parameterValueSets.TryAdd(observed.ParameterValueSetSha256, 0);
            Interlocked.Increment(ref count);
        }
        internal bool ObservedParameters(string sha256) => parameterValueSets.ContainsKey(sha256);
        internal CommandShape Evidence() => new(snapshot.Hash, count, snapshot.Text, snapshot.Parameters.Select(item => item.Metadata).ToArray());
    }
    private sealed record CommandSnapshot(string Text, string Hash, string Shape, CapturedParameter[] Parameters)
    {
        internal string ParameterValueSetSha256 => DeploymentLocationAuthorityIssue248BaselineTests.Hash(
            string.Join('|', Parameters.Select(item => item.ValueSha256)));
        internal static CommandSnapshot Create(DbCommand command) { var text = string.Join(' ', command.CommandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); var parameters = command.Parameters.Cast<SqlParameter>().Select(CapturedParameter.Create).ToArray(); var hash = DeploymentLocationAuthorityIssue248BaselineTests.Hash(text); return new(command.CommandText, hash, DeploymentLocationAuthorityIssue248BaselineTests.Hash(hash + string.Join('|', parameters.Select(item => item.Metadata))), parameters); }
        internal SqlCommand Create(SqlConnection connection) { var command = connection.CreateCommand(); command.CommandText = Text; foreach (var item in Parameters) command.Parameters.Add(item.Create()); return command; }
    }
    private sealed record CapturedParameter(ParameterMetadata Metadata, object Value)
    {
        internal string ValueSha256 => Hash(string.Join('|', Metadata.Name, Metadata.Type,
            Convert.ToString(Value, CultureInfo.InvariantCulture)));
        internal static CapturedParameter Create(SqlParameter value) => new(new(value.ParameterName, value.SqlDbType.ToString(), value.Size, value.Precision, value.Scale, value.Direction.ToString(), value.IsNullable), value.Value);
        internal SqlParameter Create() => new(Metadata.Name, Enum.Parse<System.Data.SqlDbType>(Metadata.Type), Metadata.Size) { Precision = Metadata.Precision, Scale = Metadata.Scale, Direction = Enum.Parse<System.Data.ParameterDirection>(Metadata.Direction), IsNullable = Metadata.Nullable, Value = Value };
    }

    private sealed record Seed(string OwnerId, Guid ObservatoryId, Guid RegistrationId, Guid DevicePublicId, Guid DeploymentId, Guid Token, string LocationId, long LocationVersion, string Source, double? Accuracy, DateTimeOffset EffectiveFrom, IReadOnlyList<Guid> ArtifactIds, IReadOnlyList<string> GeneratedValues);
    private sealed record ScaleRun(PublicScaleRun Public, State State, IReadOnlyList<RecipeExpectation> Expectations, IReadOnlyList<string> GeneratedValues);
    private sealed record PublicScaleRun(int Scale, Backlog InitialBacklog, Measurement? Measurement,
        State PreRetryState, PostRetryConvergence PostRetryConvergence,
        PlanEvidence? ReconciliationSelectAndPlan, IReadOnlyList<PlanEvidence>? SchedulerSelectPlans,
        IndexWriteEvidence? WriteProxy, CommandEvidence? Commands);
    private sealed record PostRetryConvergence(State State, RetryConvergence Transition);
    private sealed record RecipeExpectation(string RecipeName, string TargetRole, string TargetRecipeVersion, string TargetVariant, string RequestedRecipeIdentitySha256, bool Windowed, int RequirementsPerJob, bool Applicable, string ApplicabilityReason);
    private sealed record RetryConvergence(bool JobsUnchanged, bool RequestIdentitySetUnchanged, int AllowedWindowInputsAdded, string Interpretation);
    private sealed record Backlog(int Count, double? OldestAgeSeconds, string? OldestAgeNotApplicableReason);
    private sealed record JobGroup(string RecipeName, string TargetRecipeVersion, string TargetVariant, string RequestedRecipeIdentitySha256, string Status, int Jobs, int Requirements, int Inputs, int CanonicalInputs);
    private sealed record FrameShape(int Frames, int Artifacts, int ArtifactFrames, long? MinimumSequence, long? MaximumSequence, int DistinctSequences);
    private sealed record State(int Deployments, FrameShape FrameShape, string AuthorityStatus, string? AuthorityReason, bool TokenChanged, string RegistrationState, int ExactResolvedBindings, int Audits, bool AuditExact, int EligibleArtifacts, int Jobs, int Requirements, int Inputs, int CanonicalInputs, int DuplicateRequestIdentities, string OrderedIdentitySetSha256, IReadOnlyList<JobGroup> JobsByRecipe, Backlog FinalBacklog);
    private sealed record DatabaseSize(long DataAllocated, long DataUsed, long LogAllocated, long LogUsed, string LogReuseWaitDescription);
    private sealed record SqlSample(double ElapsedMilliseconds, int OpenTransactions, int MaximumIsolationLevel, long ActiveLogBytes);
    private sealed record Measurement(double ElapsedMilliseconds, double TransactionMilliseconds, long TransactionStarts, long TransactionCommits, long TransactionRollbacks, long TransactionFailures, long SqlCommands, long SchedulerCalls, double CapturesPerSecond, double SchedulerCallsPerSecond, double JobsPerSecond, double CpuMilliseconds, long ExactAllocatedBytes, long SampledAllocatedBytes, int AllocationSamples, int AllocationIntervalMilliseconds, long RssStartBytes, long RssEndBytes, long PeakRssBytes, double ResourceStartBoundarySkewMicroseconds, DatabaseSize DatabaseBefore, DatabaseSize DatabaseAfter, long DataAllocatedDelta, long DataUsedDelta, long LogAllocatedDelta, long LogUsedDelta, int SqlSamples, double EffectiveSqlSamplingIntervalMilliseconds, int PeakOpenTransactions, long PeakActiveLogBytes);
    private sealed record ParameterMetadata(string Name, string Type, int Size, byte Precision, byte Scale, string Direction, bool Nullable);
    private sealed record PlanParameterEvidence(ParameterMetadata Metadata, string ValueSha256);
    private sealed record PlanEvidence(string SelectSha256, string ExactParameterizedSelect,
        IReadOnlyList<PlanParameterEvidence> Parameters, string ActualPlanSha256, string BoundedPlanIdentitySha256,
        int RowsReturned, double? RootEstimatedRows, double RootActualRows, double MaximumEstimatedRowsRead,
        double MaximumActualRowsRead, long LogicalReads, IReadOnlyList<string> Operators, string Boundary, string Safety);
    private sealed record IndexOperation(string Table, string Index, long Inserts, long Updates, long Deletes);
    private sealed record IndexWriteDelta(string Table, string Index, long LeafInserts, long LeafUpdates, long LeafDeletes);
    private sealed record IndexWriteEvidence(string ProviderLogicalWrites, IReadOnlyList<IndexWriteDelta> IndexLeafOperationDeltas, string IdentitySha256)
    {
        internal static IndexWriteEvidence Create(IReadOnlyList<IndexOperation> before, IReadOnlyList<IndexOperation> after)
        {
            var deltas = after.Select(item =>
            {
                var prior = before.SingleOrDefault(value => value.Table == item.Table && value.Index == item.Index);
                return new IndexWriteDelta(item.Table, item.Index, item.Inserts - (prior?.Inserts ?? 0),
                    item.Updates - (prior?.Updates ?? 0), item.Deletes - (prior?.Deletes ?? 0));
            }).Where(item => item.LeafInserts != 0 || item.LeafUpdates != 0 || item.LeafDeletes != 0)
                .OrderBy(item => item.Table, StringComparer.Ordinal).ThenBy(item => item.Index, StringComparer.Ordinal).ToArray();
            Assert.IsNotEmpty(deltas);
            return new("N/A: Microsoft.Data.SqlClient/SQL Server STATISTICS IO does not expose literal provider logical-write counts; index leaf insert/update/delete deltas are the named write proxy.",
                deltas, Hash(JsonSerializer.Serialize(deltas)));
        }
    }
    private sealed record CommandShape(string Hash, long Count, string ExactParameterizedText, IReadOnlyList<ParameterMetadata> Parameters);
    private sealed record CommandEvidence(long Count, int UniqueShapes, string ShapeSetSha256, IReadOnlyList<CommandShape> Shapes);
    private sealed record Transactions(long Starts, long Commits, long Rollbacks, long Failures, IReadOnlyList<double> Durations);
    private sealed record EnvironmentVariableEvidence(string Name, string ValueSha256);
    private sealed record SqlEnvironmentEvidence(string ProductVersion, string Edition, byte CompatibilityLevel,
        string RecoveryModel, string PageVerify, bool ReadCommittedSnapshot, bool AutoUpdateStatistics,
        bool AutoCreateStatistics, bool AcceleratedDatabaseRecovery, string DelayedDurability);
    private sealed record EnvironmentEvidence(string OperatingSystem, string Architecture, string Framework,
        int ProcessorCount, string CpuModel, long MemoryBytes, string PinnedSdk, string ExecutingSdk,
        string DockerVersion, string SqlServerImage, string Configuration, bool ServerGc, string TestProcessCpuAffinity,
        string TestProcessCpuLimit, string TestProcessMemoryLimit, SqlContainerConstraints SqlContainerConstraints,
        IReadOnlyList<EnvironmentVariableEvidence> DotnetEnvironment,
        SqlEnvironmentEvidence SqlServer, string Topology, string Storage, string FingerprintSha256);
    private sealed record SqlContainerConstraints(string CpuSet, long CpuQuota, long CpuPeriod,
        long NanoCpus, long MemoryBytes, long MemorySwapBytes, string Interpretation);
    private sealed record AggregateStatistic(double Minimum, double Median, double Maximum);
    private sealed record FileRecord(string Name, long ByteLength, string Sha256);
    private sealed record PrivateAssembly(string Name, string Sha256, string Configuration,
        string InformationalVersion, string SourceSha256, DateTime AssemblyWrittenUtc, DateTime LatestSourceWriteUtc);
    private sealed record PrivateSource(string? RequestedRevision, string Head, string Branch, bool Dirty,
        string Claimability, int? Trial, IReadOnlyList<PrivateAssembly> Assemblies);
    private sealed class MaterializationBarrier
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record WriterRun(Task<int> SessionId, Task<int> Completion);
    private sealed record WriterObservation(short BlockingSessionId, string WaitType, short SessionIsolationLevel,
        bool BlockerIsAuthority, string BlockerApplicationSha256, short BlockerIsolationLevel, string ResourceType, string RequestMode, string RequestStatus,
        string RequestOwnerType, double OfferToObservationMilliseconds);
    private sealed record ContentionEvidence(int CaptureCount, string Writer, bool BlockedAtObservation,
        short BlockingSessionId, bool BlockerAttributed, string BlockerApplicationSha256,
        string WaitType, short SessionIsolationLevel, short BlockerIsolationLevel, string ResourceType,
        string RequestMode, string RequestStatus, string RequestOwnerType, double WriterOfferToObservationMilliseconds,
        double WriterOfferToCompletionMilliseconds, double AuthorityDurationMilliseconds,
        int BlockedObservationCount, int RowsCommitted, string Isolation);
    private sealed record ContentionRun(ContentionEvidence Public, IReadOnlyList<string> GeneratedValues);
    private sealed record RollbackState(string AuthorityStatus, bool TokenChanged, string RegistrationState,
        int UnresolvedFrames, int Audits, int Bindings, int Jobs, int Requirements, int Inputs, int CanonicalInputs);
    private sealed record FailureRestartEvidence(int FailureAfterDelegatedCall, bool AuthorityRolledBack,
        bool FreshFactoryUsed, bool FullConvergence, int Jobs, int Requirements, int Inputs,
        string OrderedIdentitySetSha256, string RestartBoundary);
    private sealed record FailureRestartRun(FailureRestartEvidence Public, IReadOnlyList<string> GeneratedValues);
}

public sealed partial class DeploymentLocationAuthorityIssue248BaselineTests
{
    private static async Task<(IReadOnlyList<PlanEvidence> Plans, IReadOnlyDictionary<string, CommandSnapshot> Commands)>
        CaptureEquivalentScaleSchedulerPlansAsync(int scale)
    {
        var fixture = AssemblyHooks.Fixture;
        var database = $"SkyMonitorIssue248_Plan_{scale}_{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = database,
            ApplicationName = $"HVO.SkyMonitor.Issue248.Plan.{scale}.{Guid.NewGuid():N}"
        }.ConnectionString;
        var probe = new Probe();
        var factory = CreateFactory(fixture, connection, probe, new CallCounter());
        try
        {
            _ = factory.Services;
            var seed = await SeedAsync(factory, scale).ConfigureAwait(false);
            var recipes = await ActiveRecipesAsync(factory, seed.ObservatoryId).ConfigureAwait(false);
            return await CapturePreResolutionSchedulerPlansAsync(factory, connection, probe, seed, recipes)
                .ConfigureAwait(false);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            await DeleteDatabaseAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task<(IReadOnlyList<PlanEvidence> Plans, IReadOnlyDictionary<string, CommandSnapshot> Commands)>
        CapturePreResolutionSchedulerPlansAsync(
            WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
            string connection,
            Probe probe,
            Seed seed,
            IReadOnlyList<CentralDerivativeRecipe> expectedRecipes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        probe.Reset();
        probe.Start(db.ContextId);
        try
        {
            var recipes = await scope.ServiceProvider.GetRequiredService<ICentralProcessingPolicyService>()
                .ResolveRequiredRecipesAsync(seed.ObservatoryId, FrameArtifactRole.Raw, CancellationToken.None)
                .ConfigureAwait(false);
            CollectionAssert.AreEqual(expectedRecipes.Select(item => item.RequestedRecipeIdentitySha256).ToArray(),
                recipes.Select(item => item.RequestedRecipeIdentitySha256).ToArray());
            var artifact = await db.CentralArtifacts.Include(item => item.Frame)
                .SingleAsync(item => item.ArtifactId == seed.ArtifactIds[0]).ConfigureAwait(false);
            var frame = artifact.Frame!;
            var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
                frame.DevicePublicId, artifact.ArtifactId, recipes[0]);
            var existing = await db.CentralDerivativeJobs.SingleOrDefaultAsync(job =>
                job.RequestIdentitySha256 == requestIdentity).ConfigureAwait(false);
            Assert.IsNull(existing, "The deterministic first create-path request must be absent before resolution.");
            var designation = await db.CentralClearReferenceDesignations
                .Include(item => item.Artifact)!.ThenInclude(item => item!.Frame)
                .SingleOrDefaultAsync(item => item.RegistrationId == frame.RegistrationId
                    && item.RigId == frame.RigId
                    && item.Artifact!.ObjectState == CentralArtifactObjectState.Available
                    && item.Artifact.ReconstructionState == CentralReconstructionState.Complete)
                .ConfigureAwait(false);
            Assert.IsNull(designation, "The create-path clear-reference lookup must be empty for this fixture.");
        }
        finally
        {
            probe.Stop();
        }

        var commands = SchedulerPlanMarkers.ToDictionary(
            marker => marker,
            marker => probe.Commands.SelectSnapshots(marker).Single(),
            StringComparer.Ordinal);
        var plans = new List<PlanEvidence>(commands.Count);
        foreach (var marker in SchedulerPlanMarkers)
        {
            var plan = await PlanAsync(connection, commands[marker],
                "Exact-shape equivalent-scale pre-state plan evidence from a separately configured, seeded, isolated database deleted before natural measured database startup; rows are expected absent and replay is outside measured execution.")
                .ConfigureAwait(false);
            Assert.AreEqual(0, plan.RowsReturned, $"Pre-resolution {marker} lookup unexpectedly returned a row.");
            plans.Add(plan);
        }
        probe.Reset();
        return (plans, commands);
    }

    private static void AssertRuntimePlanCommands(
        IReadOnlyDictionary<string, CommandSnapshot> preResolution,
        CommandProbe runtime)
    {
        foreach (var marker in SchedulerPlanMarkers)
        {
            var expected = preResolution[marker];
            var observed = runtime.SelectSnapshots(marker).Single(item => item.Shape == expected.Shape);
            Assert.AreEqual(expected.Hash, observed.Hash, $"Runtime {marker} SELECT text differs from its pre-resolution plan command.");
            Assert.IsTrue(runtime.ObservedParameters(expected.Shape, expected.ParameterValueSetSha256),
                $"Runtime {marker} SELECT did not observe the deterministic pre-resolution create-path parameters.");
        }
    }

    private static async Task<PlanEvidence> PlanAsync(
        string connection,
        CommandSnapshot snapshot,
        string boundary = "Post-resolution read-only reconciliation replay outside measured execution.")
    {
        await using var sql = new SqlConnection(connection); await sql.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder(); sql.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        await SetAsync(sql, "SET STATISTICS XML ON;").ConfigureAwait(false);
        await SetAsync(sql, "SET STATISTICS IO ON;").ConfigureAwait(false);
        var plans = new List<string>(); var rows = 0;
        try
        {
            await using var command = snapshot.Create(sql); await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var result = 0;
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (result == 0) rows++;
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (!await reader.IsDBNullAsync(i).ConfigureAwait(false)
                            && Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) is { } value
                            && value.Contains("ShowPlanXML", StringComparison.Ordinal)) plans.Add(value);
                    }
                }
                result++;
            } while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        finally { await SetAsync(sql, "SET STATISTICS IO OFF;").ConfigureAwait(false); await SetAsync(sql, "SET STATISTICS XML OFF;").ConfigureAwait(false); }
        Assert.IsNotEmpty(plans);
        var operators = XDocument.Parse(plans[0]).Descendants().Where(item => item.Name.LocalName == "RelOp").Select(item =>
        {
            var counters = item.Elements().Where(child => child.Name.LocalName == "RunTimeInformation")
                .SelectMany(runtime => runtime.Elements().Where(child => child.Name.LocalName == "RunTimeCountersPerThread"));
            return new
            {
                Physical = (string?)item.Attribute("PhysicalOp") ?? "unknown",
                Logical = (string?)item.Attribute("LogicalOp") ?? "unknown",
                EstimatedRows = Number(item, "EstimateRows"),
                EstimatedRowsRead = Number(item, "EstimatedRowsRead"),
                ActualRows = counters.Sum(x => Number(x, "ActualRows") ?? 0),
                ActualRowsRead = counters.Sum(x => Number(x, "ActualRowsRead") ?? 0)
            };
        }).ToArray();
        var reads = LogicalReadsRegex().Matches(messages.ToString()).Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Sum();
        var operatorNames = operators.Select(item => $"{item.Physical}/{item.Logical}").Distinct().Order().ToArray();
        var boundedIdentity = Hash(JsonSerializer.Serialize(new
        {
            snapshot.Hash,
            snapshot.ParameterValueSetSha256,
            Rows = rows,
            operators[0].EstimatedRows,
            operators[0].ActualRows,
            MaximumEstimatedRowsRead = operators.Max(item => item.EstimatedRowsRead ?? 0),
            MaximumActualRowsRead = operators.Max(item => item.ActualRowsRead),
            LogicalReads = reads,
            Operators = operatorNames
        }));
        return new PlanEvidence(snapshot.Hash, snapshot.Text,
            snapshot.Parameters.Select(item => new PlanParameterEvidence(item.Metadata, item.ValueSha256)).ToArray(),
            Hash(string.Join('\n', plans)), boundedIdentity, rows, operators[0].EstimatedRows, operators[0].ActualRows,
            operators.Max(item => item.EstimatedRowsRead ?? 0), operators.Max(item => item.ActualRowsRead), reads,
            operatorNames,
            boundary,
            "Read-only parameterized replay outside timing; raw plan XML is hashed, not retained; this is plan evidence, not measured command execution");
    }

    private static double? Number(XElement element, string name) => double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static async Task SetAsync(SqlConnection sql, string text) { await using var command = sql.CreateCommand(); command.CommandText = text; await command.ExecuteNonQueryAsync().ConfigureAwait(false); }

    private static async Task<IReadOnlyList<IndexOperation>> ReadIndexOperationsAsync(string connection)
    {
        await using var sql = new SqlConnection(connection); await sql.OpenAsync().ConfigureAwait(false);
        await using var command = sql.CreateCommand();
        command.CommandText = """
            SELECT OBJECT_NAME(stats.[object_id]), index_row.[name],
                   SUM(stats.[leaf_insert_count]), SUM(stats.[leaf_update_count]), SUM(stats.[leaf_delete_count])
            FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS stats
            INNER JOIN sys.indexes AS index_row ON index_row.[object_id]=stats.[object_id] AND index_row.[index_id]=stats.[index_id]
            WHERE OBJECT_NAME(stats.[object_id]) IN
                (N'DeviceDeploymentLocationVersions',N'DeploymentLocationResolutionAudits',N'DeviceRegistrations',
                 N'CentralFrames',N'CentralCaptureLocations',N'CentralDerivativeJobs',
                 N'CentralDerivativeJobInputRequirements',N'CentralDerivativeJobInputs',N'CentralDerivativeJobCanonicalInputs')
            GROUP BY stats.[object_id], index_row.[name]
            ORDER BY OBJECT_NAME(stats.[object_id]), index_row.[name];
            """;
        var values = new List<IndexOperation>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            values.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return values;
    }

    private static async Task<string> PublishTrialAsync(string root, EvidenceSourceSnapshot source, object evidence, IReadOnlyCollection<string> generated)
    {
        var endSource = await CaptureSourceAsync(root).ConfigureAwait(false);
        await ValidateSourceAsync(root, endSource).ConfigureAwait(false);
        Assert.AreEqual(
            JsonSerializer.Serialize(ProjectSource(source), JsonOptions),
            JsonSerializer.Serialize(ProjectSource(endSource), JsonOptions),
            "Source/assembly identity changed between run start and publication.");
        var trial = source.Trial!.Value; var parent = Path.Combine(root, "TestResults", "issue-248", source.Head); Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, $"trial-{trial}"); if (Directory.Exists(target)) throw new InvalidOperationException("Trial output already exists; publication is create-new.");
        var staging = Path.Combine(parent, $".trial-{trial}-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions); Scan(bytes, root, generated);
            await WriteAsync(Path.Combine(staging, EvidenceFile), bytes).ConfigureAwait(false);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = "hvo-issue-248-trial-manifest-v1",
                Phase = "baseline",
                SourceHead = source.Head,
                SourceBranch = source.Branch,
                Claimability = source.Claimability,
                Trial = trial,
                Files = new[] { new { Name = EvidenceFile, ByteLength = bytes.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } },
                SelfHash = "N/A: a manifest cannot recursively hash its own finalized bytes"
            }, JsonOptions);
            Scan(manifest, root, generated); await WriteAsync(Path.Combine(staging, ManifestFile), manifest).ConfigureAwait(false);
            var stagedEvidence = await ReadBoundedAsync(Path.Combine(staging, EvidenceFile)).ConfigureAwait(false);
            var stagedManifest = await ReadBoundedAsync(Path.Combine(staging, ManifestFile)).ConfigureAwait(false);
            Assert.AreEqual(bytes.LongLength, stagedEvidence.LongLength); Assert.AreEqual(HashBytes(bytes), HashBytes(stagedEvidence));
            Assert.AreEqual(manifest.LongLength, stagedManifest.LongLength); Assert.AreEqual(HashBytes(manifest), HashBytes(stagedManifest));
            using (var document = ParseJson(stagedEvidence))
                Assert.AreEqual("hvo-issue-248-deployment-location-baseline-v1", document.RootElement.GetProperty("schema").GetString());
            using (var document = ParseJson(stagedManifest))
                Assert.AreEqual("hvo-issue-248-trial-manifest-v1", document.RootElement.GetProperty("schema").GetString());
            Directory.Move(staging, target);
            return target;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task TryAggregateAsync(string root, EvidenceSourceSnapshot source, string harness, string environment, string workload)
    {
        var parent = Path.Combine(root, "TestResults", "issue-248", source.Head);
        var directories = Enumerable.Range(1, 5).Select(i => Path.Combine(parent, $"trial-{i}")).ToArray();
        if (directories.Any(path => !Directory.Exists(path))) return;
        var target = Path.Combine(parent, "aggregate-baseline"); if (Directory.Exists(target)) throw new InvalidOperationException("Aggregate output already exists.");
        var trials = new List<JsonDocument>();
        var retained = new List<FileRecord>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var names = Directory.EnumerateFiles(directories[i]).Select(Path.GetFileName).Order().ToArray();
                CollectionAssert.AreEqual(new[] { EvidenceFile, ManifestFile }.Order().ToArray(), names);
                var evidencePath = Path.Combine(directories[i], EvidenceFile);
                var manifestPath = Path.Combine(directories[i], ManifestFile);
                var evidenceBytes = await ReadBoundedAsync(evidencePath).ConfigureAwait(false);
                var manifestBytes = await ReadBoundedAsync(manifestPath).ConfigureAwait(false);
                Scan(evidenceBytes, root, []); Scan(manifestBytes, root, []);
                using (var manifest = ParseJson(manifestBytes))
                {
                    var manifestRoot = manifest.RootElement;
                    Assert.AreEqual("hvo-issue-248-trial-manifest-v1", manifestRoot.GetProperty("schema").GetString());
                    Assert.AreEqual("baseline", manifestRoot.GetProperty("phase").GetString());
                    Assert.AreEqual(source.Head, manifestRoot.GetProperty("sourceHead").GetString());
                    Assert.AreEqual(source.Branch, manifestRoot.GetProperty("sourceBranch").GetString());
                    Assert.AreEqual("clean-source-attributed-review-required", manifestRoot.GetProperty("claimability").GetString());
                    Assert.AreEqual(i + 1, manifestRoot.GetProperty("trial").GetInt32());
                    Assert.IsTrue(manifestRoot.GetProperty("selfHash").GetString()!.StartsWith("N/A:", StringComparison.Ordinal));
                    var file = manifestRoot.GetProperty("files").EnumerateArray().Single();
                    Assert.AreEqual(EvidenceFile, file.GetProperty("name").GetString());
                    Assert.AreEqual(evidenceBytes.LongLength, file.GetProperty("byteLength").GetInt64());
                    Assert.AreEqual(HashBytes(evidenceBytes), file.GetProperty("sha256").GetString());
                }
                trials.Add(ParseJson(evidenceBytes));
                retained.Add(new($"../trial-{i + 1}/{EvidenceFile}", evidenceBytes.LongLength, HashBytes(evidenceBytes)));
                retained.Add(new($"../trial-{i + 1}/{ManifestFile}", manifestBytes.LongLength, HashBytes(manifestBytes)));
                var value = trials[i].RootElement;
                Assert.AreEqual("hvo-issue-248-deployment-location-baseline-v1", value.GetProperty("schema").GetString());
                Assert.AreEqual("baseline", value.GetProperty("phase").GetString());
                Assert.AreEqual(ProductionRevision, value.GetProperty("productionRevision").GetString());
                var evidenceSource = value.GetProperty("source");
                Assert.AreEqual(source.Head, evidenceSource.GetProperty("head").GetString());
                Assert.AreEqual(source.Branch, evidenceSource.GetProperty("branch").GetString());
                Assert.IsFalse(evidenceSource.GetProperty("dirty").GetBoolean());
                Assert.AreEqual("clean-source-attributed-review-required", evidenceSource.GetProperty("claimability").GetString());
                Assert.AreEqual(i + 1, evidenceSource.GetProperty("trial").GetInt32());
                Assert.IsNotEmpty(evidenceSource.GetProperty("assemblies").EnumerateArray().ToArray());
                Assert.AreEqual(harness, value.GetProperty("harnessSha256").GetString());
                Assert.AreEqual(environment, value.GetProperty("environment").GetProperty("fingerprintSha256").GetString());
                Assert.AreEqual(workload, value.GetProperty("workload").GetProperty("workloadSha256").GetString());
                var scales = value.GetProperty("measurements").EnumerateArray().Select(item => item.GetProperty("scale").GetInt32()).ToArray();
                CollectionAssert.AreEqual(Scales, scales);
                foreach (var measurement in value.GetProperty("measurements").EnumerateArray())
                {
                    var schedulerPlans = measurement.GetProperty("schedulerSelectPlans").EnumerateArray().ToArray();
                    Assert.AreEqual(SchedulerPlanMarkers.Length, schedulerPlans.Length);
                    Assert.IsTrue(schedulerPlans.All(plan => plan.GetProperty("rowsReturned").GetInt32() == 0));
                    Assert.IsTrue(schedulerPlans.All(plan => plan.GetProperty("boundary").GetString()!
                        .StartsWith("Exact-shape equivalent-scale pre-state plan evidence", StringComparison.Ordinal)));
                    Assert.IsTrue(schedulerPlans.All(plan => plan.GetProperty("safety").GetString()!
                        .Contains("not measured command execution", StringComparison.Ordinal)));
                    Assert.IsTrue(schedulerPlans.All(plan => plan.GetProperty("parameters").EnumerateArray().Any()
                        && plan.GetProperty("parameters").EnumerateArray().All(parameter =>
                            parameter.GetProperty("valueSha256").GetString()!.Length == 64)));
                }
                var contention = value.GetProperty("contention");
                Assert.AreEqual(10_000, contention.GetProperty("captureCount").GetInt32(),
                    "Every claimable contention trial must use exactly 10,000 captures.");
                Assert.IsTrue(contention.GetProperty("blockedAtObservation").GetBoolean());
                Assert.IsTrue(contention.GetProperty("blockerAttributed").GetBoolean());
                Assert.IsGreaterThan(0, contention.GetProperty("blockingSessionId").GetInt32());
                Assert.IsTrue(contention.GetProperty("waitType").GetString()!.StartsWith("LCK_M_", StringComparison.Ordinal));
                Assert.AreEqual(2, contention.GetProperty("sessionIsolationLevel").GetInt32());
                Assert.AreEqual(4, contention.GetProperty("blockerIsolationLevel").GetInt32());
                Assert.AreNotEqual("unknown", contention.GetProperty("resourceType").GetString());
                Assert.AreNotEqual("unknown", contention.GetProperty("requestMode").GetString());
                Assert.AreEqual("WAIT", contention.GetProperty("requestStatus").GetString());
                Assert.AreEqual("TRANSACTION", contention.GetProperty("requestOwnerType").GetString());
                Assert.AreEqual(1, contention.GetProperty("blockedObservationCount").GetInt32());
                Assert.AreEqual(1, contention.GetProperty("rowsCommitted").GetInt32());
            }
            RequireCrossTrialEquality(trials, value => value.GetProperty("source").GetProperty("assemblies").GetRawText(), "assembly snapshots");
            RequireCrossTrialEquality(trials, value => value.GetProperty("workload").GetProperty("recipeExpectations").GetRawText(), "recipe expectations");
            for (var scale = 0; scale < Scales.Length; scale++)
            {
                var index = scale;
                RequireCrossTrialEquality(trials, value => value.GetProperty("measurements")[index].GetProperty("commands").GetProperty("shapeSetSha256").GetString()!, $"scale {Scales[index]} command shapes");
                RequireCrossTrialEquality(trials, value => value.GetProperty("measurements")[index].GetProperty("preRetryState").GetProperty("orderedIdentitySetSha256").GetString()!, $"scale {Scales[index]} pre-retry identities");
                RequireCrossTrialEquality(trials, value => value.GetProperty("measurements")[index].GetProperty("preRetryState").GetProperty("jobsByRecipe").GetRawText(), $"scale {Scales[index]} pre-retry recipes");
                RequireCrossTrialEquality(trials, value => value.GetProperty("measurements")[index].GetProperty("postRetryConvergence").GetRawText(), $"scale {Scales[index]} post-retry convergence");
                RequireCrossTrialEquality(trials, value => string.Join('|', value.GetProperty("measurements")[index]
                    .GetProperty("schedulerSelectPlans").EnumerateArray().Select(plan => plan.GetProperty("boundedPlanIdentitySha256").GetString())), $"scale {Scales[index]} scheduler plans");
                RequireCrossTrialEquality(trials, value => value.GetProperty("measurements")[index].GetProperty("reconciliationSelectAndPlan").GetProperty("boundedPlanIdentitySha256").GetString()!, $"scale {Scales[index]} reconciliation plan");
                RequireCrossTrialEquality(trials, value => value.GetProperty("measurements")[index].GetProperty("writeProxy").GetProperty("identitySha256").GetString()!, $"scale {Scales[index]} write proxy");
            }
            RequireCrossTrialEquality(trials, value => value.GetProperty("failureRestart").GetRawText(), "functional failure/restart record");
            foreach (var field in new[] { "captureCount", "writer", "blockerAttributed", "waitType", "sessionIsolationLevel", "blockerIsolationLevel", "resourceType", "requestMode", "requestStatus", "requestOwnerType", "rowsCommitted" })
                RequireCrossTrialEquality(trials, value => value.GetProperty("contention").GetProperty(field).GetRawText(), $"contention {field}");
            Assert.IsTrue(trials.All(item => item.RootElement.GetProperty("contention").GetProperty("captureCount").GetInt32() == 10_000));
            var aggregate = new
            {
                Schema = "hvo-issue-248-aggregate-baseline-v1",
                Issue = 248,
                Phase = "baseline",
                SourceHead = source.Head,
                ProductionRevision,
                HarnessSha256 = harness,
                EnvironmentFingerprintSha256 = environment,
                WorkloadSha256 = workload,
                TrialCount = 5,
                Statistics = "minimum/median/maximum over exactly five trials; p95 is not inferred",
                Scales = Scales.Select((scale, index) => AggregateScale(scale, trials.Select(doc => doc.RootElement.GetProperty("measurements")[index]).ToArray())).ToArray(),
                Contention = new
                {
                    CaptureCount = 10_000,
                    OfferToObservationMilliseconds = AggregateMetric(trials.Select(item => item.RootElement.GetProperty("contention").GetProperty("writerOfferToObservationMilliseconds").GetDouble())),
                    WriterLatencyMilliseconds = AggregateMetric(trials.Select(item => item.RootElement.GetProperty("contention").GetProperty("writerOfferToCompletionMilliseconds").GetDouble())),
                    AuthorityDurationMilliseconds = AggregateMetric(trials.Select(item => item.RootElement.GetProperty("contention").GetProperty("authorityDurationMilliseconds").GetDouble())),
                    BlockedObservationCount = AggregateMetric(trials.Select(item => item.RootElement.GetProperty("contention").GetProperty("blockedObservationCount").GetDouble())),
                    AllBlocked = trials.All(item => item.RootElement.GetProperty("contention").GetProperty("blockedAtObservation").GetBoolean()),
                    AllAttributed = trials.All(item => item.RootElement.GetProperty("contention").GetProperty("blockerAttributed").GetBoolean()
                        && item.RootElement.GetProperty("contention").GetProperty("blockingSessionId").GetInt32() > 0)
                },
                FailureRestartFunctional = trials[0].RootElement.GetProperty("failureRestart").Clone(),
                RecordedAtUtc = DateTimeOffset.UtcNow
            };
            var staging = Path.Combine(parent, $".aggregate-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(aggregate, JsonOptions); Scan(bytes, root, []);
                await WriteAsync(Path.Combine(staging, "aggregate-baseline.json"), bytes).ConfigureAwait(false);
                retained.Add(new("aggregate-baseline.json", bytes.LongLength, HashBytes(bytes)));
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Schema = "hvo-issue-248-aggregate-manifest-v1",
                    Phase = "baseline",
                    SourceHead = source.Head,
                    Files = retained,
                    SelfHash = "N/A: a manifest cannot recursively hash its own finalized bytes"
                }, JsonOptions);
                Scan(manifest, root, []);
                foreach (var file in retained) ValidateManifestPath(parent, Path.Combine(staging, ManifestFile), file.Name);
                await WriteAsync(Path.Combine(staging, ManifestFile), manifest).ConfigureAwait(false);
                foreach (var file in retained)
                {
                    var path = file.Name == "aggregate-baseline.json"
                        ? Path.Combine(staging, file.Name)
                        : Path.GetFullPath(Path.Combine(staging, file.Name));
                    var reread = await ReadBoundedAsync(path).ConfigureAwait(false);
                    Assert.AreEqual(file.ByteLength, reread.LongLength);
                    Assert.AreEqual(file.Sha256, HashBytes(reread));
                }
                var stagedManifest = await ReadBoundedAsync(Path.Combine(staging, ManifestFile)).ConfigureAwait(false);
                Assert.AreEqual(manifest.LongLength, stagedManifest.LongLength);
                Assert.AreEqual(HashBytes(manifest), HashBytes(stagedManifest));
                using (var document = ParseJson(await ReadBoundedAsync(Path.Combine(staging, "aggregate-baseline.json")).ConfigureAwait(false)))
                    Assert.AreEqual("hvo-issue-248-aggregate-baseline-v1", document.RootElement.GetProperty("schema").GetString());
                using (var document = ParseJson(stagedManifest))
                    Assert.AreEqual("hvo-issue-248-aggregate-manifest-v1", document.RootElement.GetProperty("schema").GetString());
                var currentSource = await CaptureSourceAsync(root).ConfigureAwait(false);
                await ValidateSourceAsync(root, currentSource).ConfigureAwait(false);
                Assert.AreEqual(source.Head, currentSource.Head, "Harness HEAD changed before aggregate publication.");
                Assert.AreEqual(harness, HarnessHash(root), "Current harness identity changed before aggregate publication.");
                Assert.AreEqual(
                    JsonSerializer.Serialize(ProjectSource(source), JsonOptions),
                    JsonSerializer.Serialize(ProjectSource(currentSource), JsonOptions),
                    "Clean source/assembly identity changed before aggregate publication.");
                var authenticatedAssemblies = JsonSerializer.Deserialize<PrivateAssembly[]>(
                    trials[0].RootElement.GetProperty("source").GetProperty("assemblies").GetRawText(), JsonOptions)!;
                CollectionAssert.AreEqual(authenticatedAssemblies, ProjectSource(currentSource).Assemblies.ToArray(),
                    "Current assembly identity differs from the assembly identity authenticated by all trials.");
                Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        finally { foreach (var trial in trials) trial.Dispose(); }
    }

    private static void RequireCrossTrialEquality(
        IReadOnlyList<JsonDocument> trials,
        Func<JsonElement, string> selector,
        string identity)
        => Assert.AreEqual(1, trials.Select(trial => selector(trial.RootElement)).Distinct(StringComparer.Ordinal).Count(),
            $"Cross-trial {identity} differs.");

    private static object AggregateScale(int scale, JsonElement[] runs)
    {
        AggregateStatistic Metric(Func<JsonElement, double> value) { var ordered = runs.Select(value).Order().ToArray(); return new(ordered[0], ordered[2], ordered[4]); }
        JsonElement M(JsonElement run) => run.GetProperty("measurement");
        JsonElement S(JsonElement run) => run.GetProperty("preRetryState");
        JsonElement P(JsonElement run) => run.GetProperty("postRetryConvergence").GetProperty("state");
        return new
        {
            Scale = scale,
            ElapsedMilliseconds = Metric(run => M(run).GetProperty("elapsedMilliseconds").GetDouble()),
            TransactionMilliseconds = Metric(run => M(run).GetProperty("transactionMilliseconds").GetDouble()),
            SqlCommands = Metric(run => M(run).GetProperty("sqlCommands").GetDouble()),
            SchedulerCalls = Metric(run => M(run).GetProperty("schedulerCalls").GetDouble()),
            CapturesPerSecond = Metric(run => M(run).GetProperty("capturesPerSecond").GetDouble()),
            SchedulerCallsPerSecond = Metric(run => M(run).GetProperty("schedulerCallsPerSecond").GetDouble()),
            JobsPerSecond = Metric(run => M(run).GetProperty("jobsPerSecond").GetDouble()),
            CpuMilliseconds = Metric(run => M(run).GetProperty("cpuMilliseconds").GetDouble()),
            ExactAllocatedBytes = Metric(run => M(run).GetProperty("exactAllocatedBytes").GetDouble()),
            SampledAllocatedBytes = Metric(run => M(run).GetProperty("sampledAllocatedBytes").GetDouble()),
            PeakRssBytes = Metric(run => M(run).GetProperty("peakRssBytes").GetDouble()),
            ResourceStartBoundarySkewMicroseconds = Metric(run => M(run).GetProperty("resourceStartBoundarySkewMicroseconds").GetDouble()),
            DataUsedDelta = Metric(run => M(run).GetProperty("dataUsedDelta").GetDouble()),
            LogUsedDelta = Metric(run => M(run).GetProperty("logUsedDelta").GetDouble()),
            PeakActiveLogBytes = Metric(run => M(run).GetProperty("peakActiveLogBytes").GetDouble()),
            Jobs = Metric(run => S(run).GetProperty("jobs").GetDouble()),
            Requirements = Metric(run => S(run).GetProperty("requirements").GetDouble()),
            Inputs = Metric(run => S(run).GetProperty("inputs").GetDouble()),
            PreRetryIdentitySetSha256 = runs[0].GetProperty("preRetryState").GetProperty("orderedIdentitySetSha256").GetString(),
            PostRetry = new
            {
                Jobs = Metric(run => P(run).GetProperty("jobs").GetDouble()),
                Requirements = Metric(run => P(run).GetProperty("requirements").GetDouble()),
                Inputs = Metric(run => P(run).GetProperty("inputs").GetDouble()),
                CanonicalInputs = Metric(run => P(run).GetProperty("canonicalInputs").GetDouble()),
                IdentitySetSha256 = runs[0].GetProperty("postRetryConvergence").GetProperty("state").GetProperty("orderedIdentitySetSha256").GetString()
            },
            CommandShapeSetSha256 = runs[0].GetProperty("commands").GetProperty("shapeSetSha256").GetString(),
            ReconciliationPlanIdentitySha256 = runs[0].GetProperty("reconciliationSelectAndPlan").GetProperty("boundedPlanIdentitySha256").GetString(),
            SchedulerPlanIdentitySha256 = runs[0].GetProperty("schedulerSelectPlans").EnumerateArray()
                .Select(plan => plan.GetProperty("boundedPlanIdentitySha256").GetString()).ToArray(),
            WriteProxy = runs[0].GetProperty("writeProxy").Clone()
        };
    }

    private static AggregateStatistic AggregateMetric(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        Assert.AreEqual(5, ordered.Length);
        return new(ordered[0], ordered[2], ordered[4]);
    }

    private static async Task ValidateSourceAsync(string root, EvidenceSourceSnapshot source)
    {
        if (Environment.GetEnvironmentVariable("HVO_EVIDENCE_PHASE") != "baseline" || source.Dirty || source.Trial is null
            || source.RequestedRevision is null || source.OutputDirectoryName != source.Head || string.IsNullOrWhiteSpace(source.Branch)
            || source.Claimability != "clean-source-attributed-review-required"
            || source.Assemblies.Any(assembly => assembly.Configuration != "Release"
                || !assembly.InformationalVersion.Contains(source.Head, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Baseline requires phase=baseline, a clean named exact source revision, and trial 1..5.");
        var requested = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION") ?? throw new InvalidOperationException("Production revision is required.");
        var resolved = (await RunAsync(root, "git", "rev-parse", $"{requested}^{{commit}}").ConfigureAwait(false)).Trim();
        if (!resolved.Equals(ProductionRevision, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Production revision is not the pinned issue #248 base.");
        _ = await RunAsync(root, "git", "merge-base", "--is-ancestor", ProductionRevision, source.Head).ConfigureAwait(false);
        var changed = (await RunAsync(root, "git", "diff", "--name-only", $"{ProductionRevision}..{source.Head}", "--").ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] allowed = ["docs/runbooks/ci-pipeline.md", "scripts/test-categories/Program.cs", "tests/HVO.SkyMonitor.IntegrationTests/AssemblyHooks.cs", "tests/HVO.SkyMonitor.IntegrationTests/DeploymentLocationAuthorityIssue248BaselineTests.cs"];
        if (changed.Any(path => !allowed.Contains(path, StringComparer.Ordinal)) || changed.Any(path => path.StartsWith("src/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Baseline source contains a production or non-harness diff.");
    }

    private static Task<EvidenceSourceSnapshot> CaptureSourceAsync(string root)
        => EvidenceSourceIdentity.CaptureAsync(
            root,
            typeof(DeploymentLocationSnapshot),
            typeof(ProcessingIdentity),
            typeof(DeploymentLocationAuthorityService),
            typeof(CentralDerivativeJobScheduler),
            typeof(DeploymentLocationAuthorityIssue248BaselineTests),
            typeof(EvidenceSourceIdentity));

    private static PrivateSource ProjectSource(EvidenceSourceSnapshot source)
        => new(
            source.RequestedRevision,
            source.Head,
            source.Branch,
            source.Dirty,
            source.Claimability,
            source.Trial,
            source.Assemblies.Select(assembly => new PrivateAssembly(
                assembly.Name,
                assembly.Sha256,
                assembly.Configuration,
                assembly.InformationalVersion,
                assembly.SourceSha256,
                assembly.AssemblyWrittenUtc,
                assembly.LatestSourceWriteUtc)).ToArray());

    private static async Task<EnvironmentEvidence> CaptureEnvironmentAsync(string root)
    {
        using var global = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "global.json")).ConfigureAwait(false));
        var pinned = global.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        var executing = (await RunAsync(root, "dotnet", "--version").ConfigureAwait(false)).Trim();
        var docker = (await RunAsync(root, "docker", "version", "--format", "{{.Server.Version}}").ConfigureAwait(false)).Trim();
        if (pinned != executing || !GCSettings.IsServerGC
            || Environment.GetEnvironmentVariable("DOTNET_gcServer") != "1")
            throw new InvalidOperationException("Pinned SDK, server GC, and DOTNET_gcServer=1 are required.");
        var cpu = Proc("/proc/cpuinfo", "model name") ?? "unknown";
        var memory = long.Parse((Proc("/proc/meminfo", "MemTotal") ?? "0").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture) * 1024;
        var dotnet = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(item => Convert.ToString(item.Key, CultureInfo.InvariantCulture)?.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => new EnvironmentVariableEvidence(
                Convert.ToString(item.Key, CultureInfo.InvariantCulture)!,
                Hash(Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var influential = new[] { "DOTNET_PROCESSOR_COUNT", "DOTNET_GCHeapCount", "DOTNET_GCConserveMemory", "DOTNET_TieredPGO", "DOTNET_ReadyToRun" };
        if (influential.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            throw new InvalidOperationException("Unreviewed influential DOTNET_* setting is present.");
        var cpuLimit = ReadBoundedFile("/sys/fs/cgroup/cpu.max");
        var memoryLimit = ReadBoundedFile("/sys/fs/cgroup/memory.max");
        var affinity = Proc("/proc/self/status", "Cpus_allowed_list") ?? "unknown";
        var sql = await ReadSqlEnvironmentAsync().ConfigureAwait(false);
        var sqlContainer = await ReadSqlContainerConstraintsAsync(root).ConfigureAwait(false);
        var fingerprint = Hash(string.Join('|', RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription, Environment.ProcessorCount, cpu, memory, pinned, executing, docker,
            IntegrationTestFixture.SqlServerImage, cpuLimit, memoryLimit, affinity, JsonSerializer.Serialize(dotnet), JsonSerializer.Serialize(sql), JsonSerializer.Serialize(sqlContainer)));
        return new(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount, cpu, memory, pinned, executing, docker, IntegrationTestFixture.SqlServerImage, "Release", true,
            affinity, cpuLimit, memoryLimit, sqlContainer, dotnet, sql,
            "in-process production LogicHost; isolated SQL Server database per scale; all child IHostedService registrations suppressed",
            "N/A: physical bind-mount media is not attributable", fingerprint);
    }

    private static async Task<SqlContainerConstraints> ReadSqlContainerConstraintsAsync(string root)
    {
        var id = AssemblyHooks.Fixture.GetDependencyContainer(IntegrationDependency.SqlServer).Id;
        var json = await RunAsync(root, "docker", "inspect", "--format", "{{json .HostConfig}}", id).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        return new(value.GetProperty("CpusetCpus").GetString() ?? string.Empty,
            value.GetProperty("CpuQuota").GetInt64(), value.GetProperty("CpuPeriod").GetInt64(),
            value.GetProperty("NanoCpus").GetInt64(), value.GetProperty("Memory").GetInt64(),
            value.GetProperty("MemorySwap").GetInt64(),
            "Zero/empty Docker HostConfig values mean default-unlimited constraints.");
    }

    private static async Task<SqlEnvironmentEvidence> ReadSqlEnvironmentAsync()
    {
        await using var connection = new SqlConnection(AssemblyHooks.Fixture.SqlServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   [compatibility_level], [recovery_model_desc], [page_verify_option_desc],
                   [is_read_committed_snapshot_on], [is_auto_update_stats_on], [is_auto_create_stats_on],
                   [is_accelerated_database_recovery_on], [delayed_durability_desc]
            FROM [sys].[databases] WHERE [database_id]=DB_ID();
            """;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new(reader.GetString(0), reader.GetString(1), reader.GetByte(2), reader.GetString(3), reader.GetString(4),
            reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetString(9));
    }

    private static string ReadBoundedFile(string path)
    {
        var value = File.Exists(path) ? File.ReadAllText(path).Trim() : "unavailable";
        return value.Length <= 128 ? value : throw new InvalidOperationException("Container limit value is unexpectedly unbounded.");
    }

    private static string HarnessHash(string root)
    {
        string[] paths = ["tests/HVO.SkyMonitor.IntegrationTests/DeploymentLocationAuthorityIssue248BaselineTests.cs",
            "tests/HVO.SkyMonitor.IntegrationTests/AssemblyHooks.cs",
            "tests/HVO.SkyMonitor.IntegrationTests/Issue170AllocationSampler.cs", "src/HVO.SkyMonitor.TestSupport/EvidenceSourceIdentity.cs",
            "src/HVO.SkyMonitor.LogicHost/Services/DeploymentLocationAuthorityService.cs", "src/HVO.SkyMonitor.LogicHost/Services/CentralDerivativeJobScheduler.cs",
            "src/HVO.SkyMonitor.LogicHost/Services/CentralDerivativeRecipeCatalog.cs", "Directory.Build.props", "Directory.Packages.props", "global.json"];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths) { hash.AppendData(Encoding.UTF8.GetBytes(path)); hash.AppendData(File.ReadAllBytes(Path.Combine(root, path))); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Scan(byte[] bytes, string root, IEnumerable<string> values)
    {
        var json = Encoding.UTF8.GetString(bytes); var fixture = AssemblyHooks.Fixture;
        var builder = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString);
        foreach (var value in values.Append(root).Append(fixture.SqlServerConnectionString)
                     .Append(builder.Password).Append(builder.DataSource).Append(builder.InitialCatalog).Append(builder.UserID)
                     .Append(IntegrationTestFixture.MinioAccessKey).Append(IntegrationTestFixture.MinioSecretKey)
                     .Where(value => !string.IsNullOrEmpty(value)))
        {
            Assert.IsFalse(json.Contains(value, StringComparison.OrdinalIgnoreCase), "Evidence contains a configured/generated private value.");
            if (Guid.TryParse(value, out var id))
            {
                Assert.IsFalse(json.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(json.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase));
            }
        }
        Assert.IsFalse(SecretAssignmentRegex().IsMatch(json), "Evidence contains a connection/credential assignment pattern.");
        Assert.IsFalse(AbsolutePathRegex().IsMatch(json), "Evidence contains an absolute filesystem path.");
        Assert.IsFalse(GuidRegex().IsMatch(json), "Evidence contains a GUID in D or N form.");
        Assert.IsFalse(PayloadAssignmentRegex().IsMatch(json), "Evidence contains a raw payload assignment.");
        Assert.IsLessThan(10 * 1024 * 1024, bytes.LongLength);
    }

    private static async Task WriteAsync(string path, byte[] bytes) { await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough); await stream.WriteAsync(bytes).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false); }
    private static async Task<byte[]> ReadBoundedAsync(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is < 1 or > 10 * 1024 * 1024) throw new InvalidDataException("Evidence input size is outside the bounded range.");
        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }
    private static JsonDocument ParseJson(byte[] bytes) => JsonDocument.Parse(bytes, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    });
    private static void ValidateManifestPath(string sourceRoot, string manifestPath, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\', StringComparison.Ordinal)) throw new InvalidDataException("Manifest path must be normalized relative syntax.");
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, relative));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(sourceRoot), Path.DirectorySeparatorChar);
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal) || resolved == prefix) throw new InvalidDataException("Manifest path escapes the issue source root.");
    }
    private static async Task DeleteDatabaseAsync(string connection) { SqlConnection.ClearAllPools(); await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection).Options); await db.Database.EnsureDeletedAsync().ConfigureAwait(false); }
    private static void StabilizeGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static Guid Id(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashBytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static string HashOrdered(IEnumerable<string> values) => Hash(string.Join('\n', values.Order(StringComparer.Ordinal)));
    private static string? Proc(string path, string key) => File.ReadLines(path).Select(line => line.Split(':', 2)).FirstOrDefault(parts => parts.Length == 2 && parts[0].Trim() == key)?[1].Trim();
    private static async Task<string> RunAsync(string root, string file, params string[] args) { var start = new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var arg in args) start.ArgumentList.Add(arg); using var process = Process.Start(start) ?? throw new InvalidOperationException("Process start failed."); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().ConfigureAwait(false); return process.ExitCode == 0 ? await output.ConfigureAwait(false) : throw new InvalidOperationException($"{file} failed: {(await error.ConfigureAwait(false))[..Math.Min(512, (await error.ConfigureAwait(false)).Length)]}"); }
    private static string FindRepositoryRoot() { for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "HVO.SkyMonitor.v9.slnx"))) return dir.FullName; throw new InvalidOperationException("Repository root not found."); }
    private static void AcquireLock() { try { processLock = new FileStream(Path.Combine(Path.GetTempPath(), "hvo-issue-248-baseline.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); } catch (IOException ex) { throw new InvalidOperationException("Another issue #248 evidence process is running.", ex); } }
    [GeneratedRegex(@"logical reads (?<reads>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LogicalReadsRegex();
    [GeneratedRegex(@"(?i)(?:Data Source|Server|Database|Initial Catalog|User ID|Password|Pwd|ConnectionString|Secret)\s*[=:]\s*[^\s,;\""}]+", RegexOptions.CultureInvariant)] private static partial Regex SecretAssignmentRegex();
    [GeneratedRegex(@"(?<![A-Za-z0-9.])(?:/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)+|[A-Za-z]:\\[^\r\n\""<>|]+)", RegexOptions.CultureInvariant)] private static partial Regex AbsolutePathRegex();
    [GeneratedRegex(@"(?i)(?<![0-9a-f])(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})(?![0-9a-f])", RegexOptions.CultureInvariant)] private static partial Regex GuidRegex();
    [GeneratedRegex(@"(?i)\""(?:payload|payloadBytes|rawBytes|contentBytes)\""\s*:\s*(?:\""|\[)", RegexOptions.CultureInvariant)] private static partial Regex PayloadAssignmentRegex();
}

public sealed partial class DeploymentLocationAuthorityIssue248BaselineTests
{
    private static async Task<Seed> SeedAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var prefix = $"issue248:{count}";
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var observatory = new Observatory
        {
            Id = Id(prefix + ":observatory"),
            OwnerUserId = owner.Id,
            Name = $"Issue 248 {count}",
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            AllowedDeploymentRadiusMeters = 1000,
            CreatedAtUtc = now,
            IsActive = true
        };
        var version = new ObservatoryLocationVersion
        {
            Id = Id(prefix + ":observatory-version"),
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Version = 1,
            CanonicalSha256 = new string('A', 64),
            EffectiveFromUtc = now.AddDays(-1),
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            AllowedDeploymentRadiusMeters = 1000,
            RecordedAtUtc = now,
            RecordedBy = owner.Id
        };
        var registration = new DeviceRegistration
        {
            Id = Id(prefix + ":registration"),
            DeviceId = $"issue-248-device-{count}",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            ObservatoryName = observatory.Name,
            ObservatoryLatitudeDegrees = 35,
            ObservatoryLongitudeDegrees = -113,
            ObservatoryElevationMeters = 500,
            ObservatoryTimeZoneId = "UTC",
            FriendlyName = "Issue 248",
            OwnerUserId = owner.Id,
            OwnerDisplayName = owner.UserName!,
            OwnerConfirmationMethod = "SelfAttested",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('B', 64),
            DevicePublicId = Id(prefix + ":device"),
            IssuedAtUtc = now,
            ActivatedAtUtc = now,
            LocationEvidenceState = RegistrationLocationEvidenceState.DeploymentPending
        };
        var deployment = new DeviceDeploymentLocationVersion
        {
            Id = Id(prefix + ":deployment"),
            Registration = registration,
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId,
            ObservatoryId = observatory.Id,
            ObservatoryLocationVersion = version,
            ObservatoryLocationVersionId = version.Id,
            ObservatoryLocationVersionNumber = 1,
            ObservatoryLocationCanonicalSha256 = version.CanonicalSha256,
            LocationId = "issue-248-deployment",
            Version = 2,
            CanonicalSha256 = new string('C', 64),
            Source = "operator-survey",
            SourceKind = DeploymentLocationSourceKind.Manual,
            HorizontalAccuracyMeters = 2,
            EffectiveFromUtc = now.AddHours(-1),
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            Status = DeploymentLocationResolutionStatus.Pending,
            ReasonCode = "location-changed",
            ProposedAtUtc = now,
            ConcurrencyToken = Id(prefix + ":token")
        };
        db.AddRange(observatory, version, registration, deployment);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = owner.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = now
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var artifacts = new Guid[count];
        var generated = new List<string>();
        for (var offset = 0; offset < count; offset += 250)
        {
            var frames = new List<CentralFrame>();
            for (var index = offset; index < Math.Min(offset + 250, count); index++)
            {
                var captured = now.AddMilliseconds(index);
                var frame = new CentralFrame
                {
                    Id = Id($"{prefix}:frame-row:{index}"),
                    RegistrationId = registration.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ObservatoryId = observatory.Id,
                    AgentId = registration.DeviceId,
                    FrameId = Id($"{prefix}:frame:{index}"),
                    RigId = "issue-248-rig",
                    RigProfileVersion = 1,
                    CaptureSequence = index,
                    CapturedAtUtc = captured,
                    FirstReceivedAtUtc = captured,
                    LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved
                };
                frame.Timing = new CentralCaptureTiming
                {
                    RequestedStartUtc = captured.AddSeconds(-2),
                    ExposureStartedUtc = captured.AddSeconds(-1),
                    ExposureEndedUtc = captured.AddMilliseconds(-100),
                    ReadoutCompletedUtc = captured.AddMilliseconds(-50),
                    DurableIngressUtc = captured
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
                foreach (var kind in Enum.GetValues<CentralProfileKind>())
                {
                    frame.Profiles.Add(new CentralCaptureProfile
                    {
                        Kind = kind,
                        Name = $"issue-248-{kind}",
                        Version = "1",
                        Sha256 = Hash($"issue-248-profile:{kind}")
                    });
                }
                frame.Location = new CentralCaptureLocation
                {
                    CentralFrame = frame,
                    CentralFrameId = frame.Id,
                    LocationId = deployment.LocationId,
                    Version = deployment.Version,
                    Source = deployment.Source,
                    HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
                    EffectiveFromUtc = deployment.EffectiveFromUtc
                };
                artifacts[index] = Id($"{prefix}:artifact:{index}");
                var storage = $"minio://skymonitor-artifacts/issue-248/{count}/{index:D5}";
                var idempotency = $"issue-248-{count}-{index:D5}";
                frame.Artifacts.Add(new CentralArtifact
                {
                    Id = Id($"{prefix}:artifact-row:{index}"),
                    CentralFrameId = frame.Id,
                    DevicePublicId = registration.DevicePublicId,
                    ArtifactId = artifacts[index],
                    Role = FrameArtifactRole.Raw,
                    RecipeVersion = "raw-v1",
                    ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
                    MediaType = "application/octet-stream",
                    ByteLength = 4,
                    ChecksumSha256 = new string('D', 64),
                    StorageReference = storage,
                    ReceivedAtUtc = captured,
                    IdempotencyKey = idempotency,
                    SourceId = "issue-248-source",
                    Variant = "native",
                    CreatedUtc = captured,
                    ObjectState = CentralArtifactObjectState.Available,
                    ReconstructionState = CentralReconstructionState.Complete,
                    Layout = new CentralArtifactLayout
                    {
                        Width = 1,
                        Height = 2,
                        StrideBytes = 2,
                        PixelFormat = CameraPixelFormat.Mono16.ToString(),
                        ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                        SampleDepthBits = 16,
                        ContainerDepthBits = 16,
                        Packing = FrameSamplePacking.ByteAligned.ToString(),
                        CfaPattern = ColorFilterArrayPattern.None.ToString(),
                        BlackLevel = 0,
                        WhiteLevel = ushort.MaxValue,
                        ByteLength = 4
                    },
                    Recipe = new CentralArtifactRecipe
                    {
                        Name = "raw-capture",
                        SemanticVersion = "1.0.0",
                        ImplementationVersion = "issue-248-v1",
                        OptionsJson = "{}",
                        OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(
                            JsonSerializer.SerializeToElement(new { }))
                    }
                });
                generated.AddRange([frame.Id.ToString("D"), frame.FrameId.ToString("D"), artifacts[index].ToString("D"), storage, idempotency]);
                frames.Add(frame);
            }
            db.CentralFrames.AddRange(frames);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        generated.AddRange([observatory.Id.ToString("D"), version.Id.ToString("D"), registration.Id.ToString("D"),
            registration.DevicePublicId.Value.ToString("D"), deployment.Id.ToString("D"), deployment.ConcurrencyToken.ToString("D")]);
        return new Seed(owner.Id, observatory.Id, registration.Id, registration.DevicePublicId.Value,
            deployment.Id, deployment.ConcurrencyToken, deployment.LocationId, deployment.Version,
            deployment.Source, deployment.HorizontalAccuracyMeters, deployment.EffectiveFromUtc, artifacts, generated);
    }

    private static async Task<IReadOnlyList<CentralDerivativeRecipe>> ActiveRecipesAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Guid observatory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICentralProcessingPolicyService>()
            .ResolveRequiredRecipesAsync(observatory, FrameArtifactRole.Raw, CancellationToken.None).ConfigureAwait(false);
    }

    private static RecipeExpectation[] BuildExpectations(IReadOnlyList<CentralDerivativeRecipe> recipes)
        => recipes.OrderBy(item => item.RequestedRecipeIdentitySha256, StringComparer.Ordinal).Select(recipe =>
        {
            var cloud = recipe.RecipeName == BuiltInProcessingRecipes.CloudAssessment;
            return new RecipeExpectation(
                recipe.RecipeName,
                recipe.TargetRole.ToString(),
                recipe.RecipeVersion,
                recipe.TargetVariant,
                recipe.RequestedRecipeIdentitySha256,
                recipe.Window is not null,
                recipe.Window?.Positions.Count ?? 1,
                !cloud,
                cloud
                    ? "Not applicable: seeded reconstructable frames have a deterministic RigId but the registration/rig has no CentralClearReferenceDesignation; production still resolves policy and queries that prerequisite on every scheduler call."
                    : "Applicable to the seeded Raw/Available/Complete artifact and resolved location.");
        }).ToArray();

    private static async Task<Backlog> BacklogAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Guid registration)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var value = await db.CentralFrames.AsNoTracking().Where(frame => frame.RegistrationId == registration
                && (frame.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedUnresolved
                    || frame.LocationEvidenceState == CentralCaptureLocationEvidenceState.Mismatch))
            .GroupBy(_ => 1).Select(group => new { Count = group.Count(), Oldest = group.Min(frame => frame.CapturedAtUtc) })
            .SingleOrDefaultAsync().ConfigureAwait(false);
        return value is null ? new Backlog(0, null, "N/A: no unresolved captures remain")
            : new Backlog(value.Count, Math.Max(0, (DateTimeOffset.UtcNow - value.Oldest).TotalSeconds), null);
    }

    private static async Task<State> StateAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Seed seed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deployment = await db.DeviceDeploymentLocationVersions.AsNoTracking().SingleAsync(item => item.Id == seed.DeploymentId).ConfigureAwait(false);
        var registration = await db.DeviceRegistrations.AsNoTracking().SingleAsync(item => item.Id == seed.RegistrationId).ConfigureAwait(false);
        var exact = await db.CentralFrames.AsNoTracking().CountAsync(frame => frame.RegistrationId == seed.RegistrationId
            && frame.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved
            && frame.Location!.DeviceDeploymentLocationVersionId == seed.DeploymentId
            && frame.Location.LocationId == seed.LocationId && frame.Location.Version == seed.LocationVersion
            && frame.Location.Source == seed.Source && frame.Location.HorizontalAccuracyMeters == seed.Accuracy
            && frame.Location.EffectiveFromUtc == seed.EffectiveFrom).ConfigureAwait(false);
        var eligible = await db.CentralArtifacts.AsNoTracking().CountAsync(artifact => artifact.Frame!.RegistrationId == seed.RegistrationId
            && artifact.Role == FrameArtifactRole.Raw && artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete).ConfigureAwait(false);
        var jobs = await db.CentralDerivativeJobs.AsNoTracking().Where(job => job.SourceArtifact!.Frame!.RegistrationId == seed.RegistrationId)
            .Select(job => new
            {
                job.RecipeName,
                job.TargetRecipeVersion,
                job.TargetVariant,
                job.RequestedRecipeIdentitySha256,
                job.RequestIdentitySha256,
                job.Status,
                Requirements = job.InputRequirements.Count,
                Inputs = job.Inputs.Count,
                Canonical = job.CanonicalInputs.Count
            })
            .ToArrayAsync().ConfigureAwait(false);
        var grouped = jobs.GroupBy(job => new
        {
            job.RecipeName,
            job.TargetRecipeVersion,
            job.TargetVariant,
            job.RequestedRecipeIdentitySha256,
            job.Status
        }).Select(group => new JobGroup(
                group.Key.RecipeName, group.Key.TargetRecipeVersion, group.Key.TargetVariant,
                group.Key.RequestedRecipeIdentitySha256, group.Key.Status.ToString(), group.Count(),
                group.Sum(job => job.Requirements), group.Sum(job => job.Inputs), group.Sum(job => job.Canonical)))
            .OrderBy(item => item.RequestedRecipeIdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.RecipeName, StringComparer.Ordinal)
            .ThenBy(item => item.Status, StringComparer.Ordinal).ToArray();
        var frameShape = await db.CentralFrames.AsNoTracking().Where(frame => frame.RegistrationId == seed.RegistrationId)
            .GroupBy(_ => 1).Select(group => new FrameShape(
                group.Count(),
                group.SelectMany(frame => frame.Artifacts).Count(),
                group.SelectMany(frame => frame.Artifacts).Select(artifact => artifact.CentralFrameId).Distinct().Count(),
                group.Min(frame => frame.CaptureSequence), group.Max(frame => frame.CaptureSequence),
                group.Select(frame => frame.CaptureSequence).Distinct().Count())).SingleAsync().ConfigureAwait(false);
        var audit = await db.DeploymentLocationResolutionAudits.AsNoTracking()
            .SingleAsync(item => item.DeviceDeploymentLocationVersionId == seed.DeploymentId).ConfigureAwait(false);
        return new State(
            await db.DeviceDeploymentLocationVersions.AsNoTracking().CountAsync(item => item.RegistrationId == seed.RegistrationId).ConfigureAwait(false),
            frameShape, deployment.Status.ToString(), deployment.ReasonCode, deployment.ConcurrencyToken != seed.Token,
            registration.LocationEvidenceState.ToString(), exact, 1,
            audit.RegistrationId == seed.RegistrationId && audit.ActorUserId == seed.OwnerId
                && audit.PreviousStatus == DeploymentLocationResolutionStatus.Pending
                && audit.NewStatus == DeploymentLocationResolutionStatus.Acknowledged
                && audit.Reason == ResolutionReason,
            eligible, jobs.Length, jobs.Sum(job => job.Requirements), jobs.Sum(job => job.Inputs), jobs.Sum(job => job.Canonical),
            jobs.GroupBy(job => job.RequestIdentitySha256).Count(group => group.Count() > 1),
            HashOrdered(jobs.Select(job => job.RequestIdentitySha256)), grouped,
            await BacklogAsync(factory, seed.RegistrationId).ConfigureAwait(false));
    }

    private static void AssertState(
        int count,
        Seed seed,
        State state,
        IReadOnlyList<CentralDerivativeRecipe> recipes,
        bool afterRetry)
    {
        Assert.AreEqual(1, state.Deployments);
        Assert.AreEqual(count, state.FrameShape.Frames);
        Assert.AreEqual(count, state.FrameShape.Artifacts);
        Assert.AreEqual(count, state.FrameShape.ArtifactFrames);
        Assert.AreEqual(0L, state.FrameShape.MinimumSequence);
        Assert.AreEqual(count - 1L, state.FrameShape.MaximumSequence);
        Assert.AreEqual(count, state.FrameShape.DistinctSequences);
        Assert.AreEqual("Acknowledged", state.AuthorityStatus); Assert.AreEqual(ResolutionReason, state.AuthorityReason);
        Assert.IsTrue(state.TokenChanged); Assert.AreEqual("DeploymentAcknowledged", state.RegistrationState);
        Assert.AreEqual(count, state.ExactResolvedBindings); Assert.AreEqual(1, state.Audits); Assert.IsTrue(state.AuditExact);
        Assert.AreEqual(count, state.EligibleArtifacts); Assert.AreEqual(0, state.DuplicateRequestIdentities);
        Assert.AreEqual(0, state.FinalBacklog.Count); Assert.IsNull(state.FinalBacklog.OldestAgeSeconds);
        var expected = seed.ArtifactIds.SelectMany(artifact => recipes.Select(recipe =>
            CentralDerivativeJobIdentity.CreateRequestIdentity(seed.DevicePublicId, artifact, recipe))).ToArray();
        Assert.AreEqual(expected.Length, state.Jobs); Assert.AreEqual(expected.Length, expected.Distinct().Count());
        Assert.AreEqual(HashOrdered(expected), state.OrderedIdentitySetSha256);
        var expectations = BuildExpectations(awaitable: recipes);
        foreach (var expectation in expectations)
        {
            var groups = state.JobsByRecipe.Where(item => item.RequestedRecipeIdentitySha256 == expectation.RequestedRecipeIdentitySha256).ToArray();
            if (!expectation.Applicable)
            {
                Assert.IsEmpty(groups);
                continue;
            }
            Assert.AreEqual(count, groups.Sum(item => item.Jobs));
            Assert.AreEqual(count * expectation.RequirementsPerJob, groups.Sum(item => item.Requirements));
            Assert.AreEqual(0, groups.Sum(item => item.CanonicalInputs));
            if (!expectation.Windowed)
            {
                Assert.AreEqual(count, groups.Sum(item => item.Inputs));
                Assert.AreEqual(count, groups.Single(item => item.Status == "Pending").Jobs);
            }
            else if (!afterRetry)
            {
                Assert.AreEqual(0, groups.Sum(item => item.Inputs));
                Assert.AreEqual(count, groups.Single(item => item.Status == "Waiting").Jobs);
            }
            else
            {
                Assert.AreEqual(5 * count - 6, groups.Sum(item => item.Inputs));
                Assert.AreEqual(count - 4, groups.Single(item => item.Status == "Pending").Jobs);
                Assert.AreEqual(4, groups.Single(item => item.Status == "Waiting").Jobs);
            }
        }
    }

    private static RecipeExpectation[] BuildExpectations(IEnumerable<CentralDerivativeRecipe> awaitable)
        => BuildExpectations(awaitable.ToArray());

    private static async Task RetryAllAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Seed seed)
    {
        foreach (var id in seed.ArtifactIds)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location)
                .SingleAsync(item => item.ArtifactId == id).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
                .EnsureRequiredJobsAsync(artifact, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<DatabaseSize> DatabaseSizeAsync(string connection)
    {
        await using var sql = new SqlConnection(connection); await sql.OpenAsync().ConfigureAwait(false);
        await using var command = sql.CreateCommand(); command.CommandText = """
            SELECT SUM(CASE WHEN [type_desc]=N'ROWS' THEN CAST([size] AS bigint)*8192 ELSE 0 END),
              SUM(CASE WHEN [type_desc]=N'ROWS' THEN CAST(FILEPROPERTY([name],'SpaceUsed') AS bigint)*8192 ELSE 0 END),
              SUM(CASE WHEN [type_desc]=N'LOG' THEN CAST([size] AS bigint)*8192 ELSE 0 END),
              (SELECT CAST([used_log_space_in_bytes] AS bigint) FROM [sys].[dm_db_log_space_usage]),
              (SELECT [log_reuse_wait_desc] FROM [sys].[databases] WHERE [database_id]=DB_ID())
            FROM [sys].[database_files];
            """;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false); Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new DatabaseSize(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
    }

    private static async Task<IReadOnlyList<SqlSample>> SampleSqlAsync(
        string connection,
        string application,
        TaskCompletionSource ready,
        CancellationToken cancellation)
    {
        var samples = new List<SqlSample>(); var started = Stopwatch.GetTimestamp();
        try
        {
            var observerConnection = new SqlConnectionStringBuilder(connection)
            {
                ApplicationName = application + ".Observer"
            }.ConnectionString;
            await using var sql = new SqlConnection(observerConnection); await sql.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            while (!cancellation.IsCancellationRequested)
            {
                await using var command = sql.CreateCommand(); command.CommandText = """
                    SELECT COUNT(DISTINCT session_transaction.[session_id]), MAX(session.[transaction_isolation_level]),
                      COALESCE(SUM(database_transaction.[database_transaction_log_bytes_used]),0)
                    FROM [sys].[dm_exec_sessions] AS session
                    LEFT JOIN [sys].[dm_tran_session_transactions] AS session_transaction ON session_transaction.[session_id]=session.[session_id]
                    LEFT JOIN [sys].[dm_tran_database_transactions] AS database_transaction
                      ON database_transaction.[transaction_id]=session_transaction.[transaction_id] AND database_transaction.[database_id]=DB_ID()
                    WHERE session.[program_name]=@application;
                    """;
                command.Parameters.Add(new SqlParameter("@application", System.Data.SqlDbType.NVarChar, 128) { Value = application });
                await using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false));
                samples.Add(new SqlSample(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    reader.GetInt32(0), await reader.IsDBNullAsync(1, cancellation).ConfigureAwait(false) ? 0 : reader.GetInt16(1), reader.GetInt64(2)));
                ready.TrySetResult();
                await Task.Delay(10, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { ready.TrySetResult(); }
        return samples;
    }

    private sealed class RssSampler
    {
        private readonly CancellationToken cancellation;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> reset = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RssSampler(CancellationToken cancellation)
        {
            this.cancellation = cancellation;
            Completion = RunAsync();
        }

        internal Task Ready => ready.Task;
        internal Task<long> Completion { get; }

        internal void Reset(long initial)
        {
            if (!reset.TrySetResult(initial)) throw new InvalidOperationException("RSS sampler boundary was already reset.");
        }

        private async Task<long> RunAsync()
        {
            var peak = 0L;
            using var process = Process.GetCurrentProcess();
            try
            {
                process.Refresh();
                _ = process.WorkingSet64;
                ready.TrySetResult();
                peak = await reset.Task.WaitAsync(cancellation).ConfigureAwait(false);
                while (!cancellation.IsCancellationRequested)
                {
                    process.Refresh();
                    peak = Math.Max(peak, process.WorkingSet64);
                    await Task.Delay(10, cancellation).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { ready.TrySetResult(); }
            return peak;
        }
    }

    private static double SamplingMedian(IReadOnlyList<SqlSample> samples)
    {
        if (samples.Count < 2) return 0;
        var values = samples.Zip(samples.Skip(1), (a, b) => b.ElapsedMilliseconds - a.ElapsedMilliseconds).Order().ToArray();
        return values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
    }
}
