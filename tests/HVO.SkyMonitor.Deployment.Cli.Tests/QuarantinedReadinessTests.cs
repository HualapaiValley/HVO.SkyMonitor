using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Backup and upgrade readiness when the only unhealthy checks are a processing-lane quarantine. The predicate is the
/// decision <c>--allow-quarantined-readiness</c> adds; archive checksums are not part of it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class QuarantinedReadinessTests
{
    private static readonly ApprovedCatalogContract Catalog = ProductionCatalog.Get(HygV42.CatalogId);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);

    // Bound assertion failures if polling stops scheduling timers.
    private static readonly TimeSpan TestWatchdog = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void TryAccept_LaneQuarantineAndTerminalOutcome_IsReady()
    {
        using var document = JsonDocument.Parse(Health(laneQuarantine: 1, processing: "terminal;processing-quarantine"));

        Assert.IsTrue(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out var reason), reason);
        Assert.AreEqual(string.Empty, reason);
    }

    [TestMethod]
    public void TryAccept_LaneQuarantineWithoutProcessingFailure_IsReady()
    {
        using var document = JsonDocument.Parse(Health(laneQuarantine: 1, processing: null));

        Assert.IsTrue(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out _));
    }

    [TestMethod]
    [DataRow(0, "terminal", null)]
    [DataRow(1, "terminal;retry", null)]
    [DataRow(1, "processing-quarantine", null)]
    [DataRow(1, "terminal", "disk-pressure")]
    public void TryAccept_AnyOtherUnhealthyCheck_IsRefused(int quarantine, string? processing, string? other)
    {
        using var document = JsonDocument.Parse(Health(quarantine, processing, other));

        Assert.IsFalse(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out var reason));
        Assert.IsGreaterThan(0, reason.Length);
    }

    [TestMethod]
    public void TryAccept_CatalogMismatch_IsRefused()
    {
        using var document = JsonDocument.Parse(Health(1, "terminal").Replace("\"4.2\"", "\"4.4\"", StringComparison.Ordinal));

        Assert.IsFalse(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out var reason));
        StringAssert.Contains(reason, "catalog", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task WaitForHealth_QuarantineOnly503_SucceedsWhenAllowed()
    {
        var clock = new HealthPollTimeProvider();
        var timeout = TimeSpan.FromSeconds(5);
        using var handler = Script(Health(1, "terminal"));

        await StartHealthWait(CreateClient(handler, clock), timeout, allowQuarantinedReadiness: true)
            .WaitAsync(TestWatchdog);

        await ExpectTimerAsync(clock, timeout);
        Assert.AreEqual(2, handler.RequestCount);

        using var recovering = Script(Health(1, "terminal"), recoveredHealth: Health(0, null));
        var wait = StartHealthWait(CreateClient(recovering, clock), timeout);
        await ExpectTimerAsync(clock, timeout);
        var poll = await ExpectPollAsync(clock, recovering, requests: 2);
        Assert.IsFalse(wait.IsCompleted);
        clock.Advance(poll, PollInterval);
        await wait.WaitAsync(TestWatchdog);
        Assert.AreEqual(4, recovering.RequestCount);
    }

    [TestMethod]
    public async Task WaitForHealth_QuarantineOnly503_StillFailsWithoutTheFlag()
    {
        var clock = new HealthPollTimeProvider();
        using var handler = Script(Health(1, "terminal"));
        var wait = StartHealthWait(CreateClient(handler, clock), HealthTimeout);

        var deadline = await ExpectTimerAsync(clock, HealthTimeout);
        var poll = await ExpectPollAsync(clock, handler, requests: 2);
        var lastMillisecond = TimeSpan.FromMilliseconds(1);
        clock.Advance(poll, PollInterval - lastMillisecond);
        Assert.IsFalse(wait.IsCompleted);
        Assert.AreEqual(2, handler.RequestCount);
        clock.Advance(poll, lastMillisecond);

        await ExpectPollAsync(clock, handler, requests: 4);
        clock.Advance(deadline, HealthTimeout - PollInterval);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => wait.WaitAsync(TestWatchdog));

        StringAssert.Contains(failure.Message, "did not become ready", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, handler.RequestCount);
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task WaitForHealth_AnotherUnhealthyCheck_FailsWithTheFlag()
    {
        var clock = new HealthPollTimeProvider();
        using var handler = Script(Health(1, "terminal", "disk-pressure"));
        var client = CreateClient(handler, clock);
        var wait = StartHealthWait(client, HealthTimeout, allowQuarantinedReadiness: true);

        var deadline = await ExpectTimerAsync(clock, HealthTimeout);
        await ExpectPollAsync(clock, handler, requests: 2);
        clock.Advance(deadline, HealthTimeout);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => wait.WaitAsync(TestWatchdog));

        StringAssert.Contains(failure.Message, "disk-pressure", StringComparison.Ordinal);
        Assert.AreEqual(2, handler.RequestCount);

        using var cancellation = new CancellationTokenSource();
        var canceledWait = StartHealthWait(client, HealthTimeout, allowQuarantinedReadiness: true, cancellationToken: cancellation.Token);
        await ExpectTimerAsync(clock, HealthTimeout);
        var pending = await ExpectPollAsync(clock, handler, requests: 4);
        await cancellation.CancelAsync();

        var canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => canceledWait.WaitAsync(TestWatchdog));
        Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        clock.Advance(pending, PollInterval);
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task WaitForHealth_DeploymentLocationDeferredThenReady_RequiresSuccessfulSemanticHealth()
    {
        var clock = new HealthPollTimeProvider();
        using var handler = new ScriptedHandler((_, count, _) => Task.FromResult(count switch
        {
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(Health(0, null, "deployment-location"))
            },
            4 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Health(0, null))
            },
            1 or 3 => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new AssertFailedException("Unexpected readiness probe.")
        }));
        var wait = StartHealthWait(CreateClient(handler, clock), HealthTimeout);

        await ExpectTimerAsync(clock, HealthTimeout);
        var poll = await ExpectPollAsync(clock, handler, requests: 2);
        Assert.IsFalse(wait.IsCompleted);
        clock.Advance(poll, PollInterval);

        await wait.WaitAsync(TestWatchdog);
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WaitForHealth_InFlightProbeHonorsDeadlineAndCallerCancellation(bool cancel)
    {
        var clock = new HealthPollTimeProvider();
        var timeout = TimeSpan.FromMilliseconds(200);
        var probeStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHandler(async (_, _, token) =>
        {
            var responseDelay = Task.Delay(Timeout.InfiniteTimeSpan, token);
            probeStarted.SetResult(token);
            await responseDelay;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var caller = new CancellationTokenSource();
        var pending = StartHealthWait(CreateClient(handler, clock), timeout, cancellationToken: caller.Token);
        var deadline = await ExpectTimerAsync(clock, timeout);
        var probeToken = await probeStarted.Task.WaitAsync(TestWatchdog);
        Assert.IsFalse(pending.IsCompleted);

        if (cancel)
        {
            await caller.CancelAsync();
            Assert.IsTrue(probeToken.IsCancellationRequested, "Caller cancellation must cancel the in-flight probe.");
            var canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TestWatchdog));
            Assert.AreEqual(caller.Token, canceled.CancellationToken);
        }
        else
        {
            var lastMillisecond = TimeSpan.FromMilliseconds(1);
            clock.Advance(deadline, timeout - lastMillisecond);
            Assert.IsFalse(probeToken.IsCancellationRequested);
            Assert.IsFalse(pending.IsCompleted);
            clock.Advance(deadline, lastMillisecond);
            Assert.IsTrue(probeToken.IsCancellationRequested, "The readiness deadline must cancel the in-flight probe.");

            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => pending.WaitAsync(TestWatchdog));
            StringAssert.Contains(exception.Message, "did not become ready within", StringComparison.Ordinal);
        }

        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public void Flags_AreAttestationsAndDoNotChangeTheJournaledRequest()
    {
        var id = Guid.Parse("0e4af399-9984-48eb-b119-27ac3e327756");
        var plain = new LifecycleRequest(LifecycleOperationKind.Backup, id, "/var/lib/hvo-sky", false, false, false);
        var flagged = plain with { AllowQuarantinedReadiness = true, Abort = true };

        Assert.AreEqual(plain.ComputeRequestSha256(), flagged.ComputeRequestSha256());
    }

    [TestMethod]
    public void Parse_BackupAdmitsTheFlags_AndOtherCommandsDoNotTakeAbort()
    {
        var id = Guid.NewGuid().ToString("D");
        var parsed = (LifecycleRequest)CommandLine.ParseCommand(
            ["cameraagent", "backup", "--instance-id", id, "--product-root", "/srv/hvo/product", "--abort", "--allow-quarantined-readiness"],
            allowTestProductRoot: true);

        Assert.IsTrue(parsed.Abort);
        Assert.IsTrue(parsed.AllowQuarantinedReadiness);
        var refused = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
            ["cameraagent", "upgrade", "--instance-id", id, "--product-root", "/srv/hvo/product",
                "--image-ref", $"sha256:{new string('a', 64)}", "--abort"],
            allowTestProductRoot: true));
        StringAssert.Contains(refused.Message, "--abort", StringComparison.Ordinal);
        var unknown = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
            ["catalog", "install", "--catalog-bundle", "/srv/hvo/bundle", "--product-root", "/srv/hvo/product", "--allow-quarantined-readiness"],
            allowTestProductRoot: true));
        StringAssert.Contains(unknown.Message, "'--allow-quarantined-readiness'", StringComparison.Ordinal);
    }

    private static OwnerBootstrapClient CreateClient(ScriptedHandler handler, TimeProvider clock)
        => new(new Uri("http://camera.invalid"), handler, protectedReadTimeout: null, timeProvider: clock);

    // A missing poll delay can make the client retry synchronously before returning a task.
    private static Task StartHealthWait(
        OwnerBootstrapClient client, TimeSpan timeout, bool allowQuarantinedReadiness = false,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => client.WaitForHealthAsync(Catalog, cancellationToken, timeout, allowQuarantinedReadiness),
            cancellationToken);

    private static async Task<HealthPollTimeProvider.ScheduledTimer> ExpectPollAsync(
        HealthPollTimeProvider clock, ScriptedHandler handler, int requests)
    {
        var poll = await ExpectTimerAsync(clock, PollInterval);
        Assert.AreEqual(requests, handler.RequestCount);
        return poll;
    }

    private static async Task<HealthPollTimeProvider.ScheduledTimer> ExpectTimerAsync(
        HealthPollTimeProvider clock, TimeSpan delay)
    {
        var timer = await clock.NextTimerAsync();
        Assert.AreEqual(delay, timer.Due - clock.GetUtcNow());
        return timer;
    }

    private static ScriptedHandler Script(string unavailableHealth, string? recoveredHealth = null)
        => new((request, count, _) =>
        {
            // Bound the worker's retries even if it never schedules a poll timer.
            Assert.IsLessThanOrEqualTo(4, count, "Unexpected readiness probe.");
            var isAlive = count % 2 == 1;
            Assert.AreEqual(isAlive ? "/alive" : "/health", request.RequestUri!.AbsolutePath);
            var recovered = count > 2 ? recoveredHealth : null;
            var status = isAlive || recovered is not null ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            var body = isAlive ? """{"status":"Healthy"}""" : recovered ?? unavailableHealth;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

    private sealed class HealthPollTimeProvider : TimeProvider
    {
        private readonly Channel<ScheduledTimer> _timers = Channel.CreateUnbounded<ScheduledTimer>();
        private long _elapsedTicks;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref _elapsedTicks));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            var timer = new ScheduledTimer(GetUtcNow() + dueTime, callback, state);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        public Task<ScheduledTimer> NextTimerAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(TestWatchdog);

        public void Advance(ScheduledTimer timer, TimeSpan elapsed)
        {
            var now = DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Add(ref _elapsedTicks, elapsed.Ticks));
            if (now >= timer.Due)
            {
                timer.Fire();
            }
        }

        public sealed class ScheduledTimer(DateTimeOffset due, TimerCallback callback, object? state) : ITimer
        {
            private int _finished;

            public DateTimeOffset Due { get; } = due;

            public void Fire()
            {
                if (Interlocked.Exchange(ref _finished, 1) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => Interlocked.Exchange(ref _finished, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static string Health(int laneQuarantine, string? processing, string? other = null)
    {
        var laneStatus = laneQuarantine > 0 ? "Unhealthy" : "Healthy";
        var laneDescription = laneQuarantine > 0
            ? QuarantinedReadiness.LaneDescription
            : "Capture lanes are accepting and draining work.";
        var checks = new StringBuilder()
            .Append("{\"name\":\"catalog\",\"status\":\"Healthy\",\"description\":\"Catalog matches.\",\"data\":{\"CatalogId\":\"hyg-v42-production\",\"CatalogVersion\":\"4.2\",\"SchemaVersion\":\"2\",\"PreprocessingVersion\":\"3\",\"RowCount\":119625,\"PackageVersion\":\"hyg-v4.2-p3-s2-r1\"}},")
            .Append("{\"name\":\"capture-lanes\",\"status\":\"").Append(laneStatus)
            .Append("\",\"description\":\"").Append(laneDescription)
            .Append("\",\"data\":{\"Availability\":\"").Append(laneStatus)
            .Append("\",\"LaneCount\":1,\"PendingCount\":0,\"PendingBytes\":0,\"LeasedCount\":0,\"QuarantineCount\":")
            .Append(laneQuarantine.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"Reason\":\"required-quarantine\",\"RequiredQuarantinedLaneCount\":1,\"RequiredPressureLaneCount\":0,\"OperationalFailureCount\":0,\"RequiredProcessingQuarantineCount\":")
            .Append(laneQuarantine.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("}},{\"name\":\"capture-admission\",\"status\":\"Healthy\",\"description\":\"Capture admission is intentionally paused.\",\"data\":{\"State\":\"Paused\",\"Version\":14,\"IsInitialized\":true}}");
        if (processing is null)
        {
            checks.Append(",{\"name\":\"capture-processing\",\"status\":\"Healthy\",\"description\":\"Capture processing is completing durable graphs.\",\"data\":{\"Availability\":\"Healthy\",\"PendingCount\":0,\"RetryCount\":0,\"TerminalCount\":0,\"OldestPendingAgeSeconds\":0,\"Reason\":\"completed\"}}");
        }
        else
        {
            checks.Append(",{\"name\":\"capture-processing\",\"status\":\"Unhealthy\",\"description\":\"")
                .Append(QuarantinedReadiness.ProcessingDescription)
                .Append("\",\"data\":{\"Availability\":\"Unhealthy\",\"PendingCount\":0,\"RetryCount\":0,\"TerminalCount\":1,\"LaneQuarantineTerminalCount\":1,\"ProcessingQuarantineCount\":")
                .Append(processing.Contains("processing-quarantine", StringComparison.Ordinal) ? "1" : "0")
                .Append(",\"MissingProductCount\":0,\"DurableStateUnavailable\":false,\"ReconciliationFailed\":false,\"ReplayTerminalCount\":0,\"ReplayRetryCount\":0,\"OldestPendingAgeSeconds\":0,\"Reason\":\"")
                .Append(processing)
                .Append("\"}}");
        }
        if (other is not null)
        {
            checks.Append(",{\"name\":\"").Append(other)
                .Append("\",\"status\":\"Unhealthy\",\"description\":\"Unrelated failure.\",\"data\":null}");
        }
        var status = laneQuarantine > 0 || processing is not null || other is not null ? "Unhealthy" : "Healthy";
        return "{\"status\":\"" + status + "\",\"checks\":[" + checks + "]}";
    }
}
