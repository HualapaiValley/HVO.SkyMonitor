using System.Runtime.Versioning;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary>
/// Every command's one deadline, <c>--timeout</c> (default 30 s): it bounds every request, every answer's body and every
/// wait between reads, and its expiry exits 1 ("timed out"), distinct from Ctrl+C (130). Time a person takes to answer a
/// prompt does not count against it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[UnsupportedOSPlatform("windows")]
public sealed class SkyCliDeadlineTests
{
    /// <summary>A real deadlock guard; expiry and assertions use the controllable clock, never elapsed runner time.</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    private static CliHarness CreateHarness() => new() { Time = new ManualTimeProvider(), PollInterval = TimeSpan.Zero };

    private static void Expire(CliHarness harness, int seconds)
    {
        var clock = (ManualTimeProvider)harness.Time;
        var timer = clock.Timers.Single(timer => !timer.Disposed && timer.DueTime == TimeSpan.FromSeconds(seconds));
        timer.Fire();
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task AnAnswerWhoseBodyNeverArrives_TimesOutAtTheDeadlineAndExitsOneAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = CreateHarness();
        using var watchdog = new CancellationTokenSource(Watchdog);
        var args = SkyCliExitCodeTests.Prepare(command, harness, watchdog);
        harness.Api.StallBodies = true;
        harness.Api.OnStalledRead = () => Expire(harness, 1);

        var result = await harness.RunAsync(watchdog.Token, [.. args, "--timeout", "1"]).ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Timed out after 1 s (--timeout)", StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method is "PUT" or "POST"), "Nothing was sent after the stalled read.");
    }

    [TestMethod]
    public async Task ARestartWhoseReadsHang_EndsAtItsDeadlineNotAfterItAsync()
    {
        using var harness = CreateHarness();
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.InfoRoute && harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0)
            {
                harness.Api.Hang = true;
            }
        };
        using var watchdog = new CancellationTokenSource(Watchdog);
        harness.Api.OnHang = () => Expire(harness, 2);

        var result = await harness.RunAsync(watchdog.Token, "restart", "--force", "--timeout", "2").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "CameraAgent accepted the restart, but it could not be verified", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "Timed out after 2 s (--timeout) waiting for CameraAgent to come back", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARestartThatNeverComesBack_TimesOutAtTheDefaultDeadlineUnlessGivenAnotherAsync()
    {
        using var harness = CreateHarness();
        harness.RestartReadsBeforeNewProcess = -1;
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.InfoRoute && harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0)
            {
                Expire(harness, 1);
            }
        };

        var result = await harness.RunAsync("restart", "--force", "--timeout", "1").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "waiting up to 1 s", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "Timed out after 1 s (--timeout) waiting for CameraAgent to come back", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheDefaultDeadline_IsThirtySecondsAsync()
    {
        using var harness = CreateHarness();

        var result = await harness.RunAsync("restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "waiting up to 30 s", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ATimeout_IsReportedInJsonAsTimedOutWithExitOneAsync()
    {
        using var harness = CreateHarness();
        harness.Api.Hang = true;
        using var watchdog = new CancellationTokenSource(Watchdog);

        harness.Api.OnHang = () => Expire(harness, 1);
        var result = await harness.RunAsync(watchdog.Token, "status", "--json", "--timeout", "1").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        var error = result.Json().GetProperty("error");
        Assert.AreEqual(1, error.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("TimedOut", error.GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task CtrlC_BeforeTheDeadline_StillExits130Async()
    {
        using var harness = CreateHarness();
        using var stop = new CancellationTokenSource();
        harness.Api.Hang = true;
        harness.Api.OnRequest = _ => stop.Cancel();

        var result = await harness.RunAsync(stop.Token, "status", "--timeout", "60").ConfigureAwait(false);

        Assert.AreEqual(130, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Interrupted", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("status --timeout 0")]
    [DataRow("status --timeout 3601")]
    [DataRow("info --timeout soon")]
    [DataRow("logout --timeout 0")]
    [DataRow("rig apply --force --timeout 0")]
    public async Task ATimeoutOutOfRange_IsAUsageErrorOnEveryCommandAsync(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        using var harness = CreateHarness();

        var result = await harness.RunAsync(commandLine.Split(' ')).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task TimeSpentAnsweringAPrompt_DoesNotCountAgainstTheDeadlineAsync()
    {
        using var harness = CreateHarness();
        harness.Interactive = true;
        var clock = (ManualTimeProvider)harness.Time;
        var answered = false;
        harness.Answering = () =>
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, clock.Timers.Single(timer => !timer.Disposed).DueTime,
                "The expiry is disabled for the entire time the operator answers.");
            clock.Now += TimeSpan.FromHours(1);
            answered = true;
        };
        harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs(active: "rig-1", pending: null));
        harness.Input.Enqueue("y");

        var result = await harness.RunAsync("rig", "stage", "rig-2", "--timeout", "1").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.RigRoute + "/stage"));
        Assert.IsTrue(answered);
        Assert.AreEqual(TimeSpan.FromSeconds(1), clock.Timers.Single().DueTime, "The exact remaining deadline resumes.");
    }

    [TestMethod]
    public async Task Watch_AReadWhoseBodyNeverArrives_IsReportedAndTheWatchGoesOnAsync()
    {
        using var harness = CreateHarness();
        using var stop = new CancellationTokenSource(Watchdog);
        harness.Api.StallBodies = true;
        harness.Api.OnStalledRead = () => Expire(harness, 1);
        harness.Api.OnRequest = _ =>
        {
            var reads = harness.Api.RequestsTo("GET", Fake.SummaryRoute).Count;
            harness.Api.StallBodies = reads == 1;
            if (reads >= 3)
            {
                stop.Cancel();
            }
        };

        var result = await harness.RunAsync(stop.Token, "status", "--watch", "--timeout", "1").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "NOT READ: Timed out after 1 s (--timeout)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Running", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARequestThatHangs_IsBoundedByTheDeadlineAloneAsync()
    {
        using var harness = CreateHarness();
        harness.Api.Hang = true;
        using var watchdog = new CancellationTokenSource(Watchdog);

        harness.Api.OnHang = () => Expire(harness, 2);
        var result = await harness.RunAsync(watchdog.Token, "info", "--timeout", "2").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Timed out after 2 s (--timeout)", StringComparison.Ordinal);
    }
}
