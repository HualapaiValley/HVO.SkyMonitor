using System.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed partial class CustomScriptSandboxIntegrationTests
{
    [TestInitialize]
    public void RequireSandbox()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Linux sandbox qualification is required on the Linux Integration lanes.");
        Assert.IsTrue(LinuxCustomScriptSandbox.Probe(),
            "Required sandbox qualification could not run: " + LinuxCustomScriptSandbox.ProbeUnavailableReason());
    }

    [TestMethod]
    public async Task LiteralArgumentsAndBothOutputStreamsNeverBecomeOperatorText()
    {
        var script = CustomScriptTaskAdapterTests.Script("""
            [ "$1" = '$(exit 42); a b' ] || exit 91
            [ "$2" = '-12' ] || exit 92
            [ "$3" = 'true' ] || exit 93
            printf 'synthetic-credential'
            printf 'internal-protected-path' >&2
            """);
        script.Arguments = [new(CustomScriptArgumentKind.Text, "$(exit 42); a b"),
            new(CustomScriptArgumentKind.WholeNumber, "-12"), new(CustomScriptArgumentKind.Boolean, "true")];
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        var result = await adapter.ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, result.Outcome, result.Detail);
        StringAssert.Contains(result.Detail, "stdoutBytes=20", StringComparison.Ordinal);
        StringAssert.Contains(result.Detail, "stderrBytes=23", StringComparison.Ordinal);
        Assert.IsFalse(result.Detail.Contains("synthetic-credential", StringComparison.Ordinal));
        Assert.IsFalse(result.Detail.Contains("internal-protected-path", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HostEnvironmentFilesystemDevicesAndNetworkAreAbsent()
    {
        const string variable = "HVO_SCRIPT_TEST_SENTINEL";
        var previous = Environment.GetEnvironmentVariable(variable);
        var sentinel = Path.Combine(Path.GetTempPath(), "hvo-script-sentinel-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(sentinel, "synthetic-test-only").ConfigureAwait(false);
        Environment.SetEnvironmentVariable(variable, "synthetic-test-only");
        try
        {
            var script = CustomScriptTaskAdapterTests.Script("""
                [ -z "${HVO_SCRIPT_TEST_SENTINEL+x}" ] && [ -z "${XDG_RUNTIME_DIR+x}" ] || exit 81
                [ "$HOME" = /work ] && [ "$PATH" = /bin ] || exit 82
                [ ! -e "$1" ] && [ ! -e /etc/passwd ] && [ ! -e /sys ] && [ ! -e /dev/sda ] || exit 83
                [ ! -e /proc/1/root/home/roys ] || exit 84
                ( : > /dev/escape ) 2>/dev/null && exit 85
                while read -r name rest; do
                    case "$name" in eth*|en*|wl*) exit 86;; esac
                done < /proc/net/dev
                exit 0
                """);
            script.Arguments = [new(CustomScriptArgumentKind.Text, sentinel)];
            var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
            Assert.AreEqual(0, result.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            File.Delete(sentinel);
        }
    }

    [TestMethod]
    public async Task NonzeroExitIsFailedWithNoRawStderr()
    {
        var script = CustomScriptTaskAdapterTests.Script("printf 'sensitive failure' >&2; exit 17");
        var result = await CustomScriptTaskAdapterTests.Adapter(script)
            .ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Detail, "exit=17", StringComparison.Ordinal);
        Assert.IsFalse(result.Detail.Contains("sensitive", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TimeoutKillsAndAwaitsEvenBackgroundChildren()
    {
        var script = CustomScriptTaskAdapterTests.Script("sleep 200 & wait");
        script.TimeoutSeconds = 1;
        var scope = new OwnedSandboxScope();
        var watch = Stopwatch.StartNew();
        var execution = LinuxCustomScriptSandbox.ExecuteAsync(script, default).AsTask();
        await scope.WaitForSleepAsync(execution).ConfigureAwait(false);
        var result = await execution.ConfigureAwait(false);
        Assert.AreEqual("timeout", result.Status);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed);
        scope.AssertDrained();
    }

    [TestMethod]
    public async Task CancellationDrainsBeforeThrowingAndAllowsTheNextExecution()
    {
        var script = CustomScriptTaskAdapterTests.Script("sleep 200 & wait");
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        var scope = new OwnedSandboxScope();
        using var cancellation = new CancellationTokenSource();
        var execution = adapter.ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), cancellation.Token).AsTask();
        try { await scope.WaitForSleepAsync(execution).ConfigureAwait(false); }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await execution.ConfigureAwait(false)).ConfigureAwait(false);
        }
        scope.AssertDrained();
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), alreadyCancelled.Token).ConfigureAwait(false))
            .ConfigureAwait(false);
        using var nextStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), nextStop.Token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("stdout")]
    [DataRow("stderr")]
    public async Task ExcessiveOutputTerminatesWithoutRetainingText(string stream)
    {
        var script = CustomScriptTaskAdapterTests.Script(stream == "stdout"
            ? "while :; do printf 'synthetic-data'; done"
            : "while :; do printf 'synthetic-data' >&2; done");
        script.OutputBytes = 256;
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("output-limit", result.Status);
        Assert.IsGreaterThan(256L, result.StdoutBytes + result.StderrBytes);
    }

    [TestMethod]
    public async Task ScratchCapacityIsSharedAcrossFilesAndNothingIsPublished()
    {
        var script = CustomScriptTaskAdapterTests.Script("""
            block=x
            while [ "${#block}" -lt "$1" ]; do block=$block$block; done
            for file in one two three four; do
                printf '%s' "$block" > "$file" || exit 27
            done
            exit 99
            """);
        script.ScratchBytes = Environment.SystemPageSize * 2;
        script.Arguments = [new(CustomScriptArgumentKind.WholeNumber,
            Environment.SystemPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual(27, result.ExitCode, "The tmpfs aggregate capacity must reject writes across multiple files.");
        var fresh = CustomScriptTaskAdapterTests.Script("[ ! -e one ] && [ ! -e two ]");
        Assert.AreEqual(0, (await LinuxCustomScriptSandbox.ExecuteAsync(fresh, default).ConfigureAwait(false)).ExitCode);
    }

    [TestMethod]
    public async Task OddScratchLimitCannotAllocateAnExtraPageAndSubpageLimitCannotLaunch()
    {
        var script = CustomScriptTaskAdapterTests.Script("""
            block=x
            while [ "${#block}" -lt "$1" ]; do block=$block$block; done
            printf '%s' "$block" > one || exit 81
            printf '%s' "$block" > two || exit 82
            printf '%s' "$block" > three 2>/dev/null && exit 83
            exit 0
            """);
        script.ScratchBytes = Environment.SystemPageSize * 2 + 1;
        script.Arguments = [new(CustomScriptArgumentKind.WholeNumber,
            Environment.SystemPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(0, result.ExitCode, "A third page must not fit in a two-page-plus-one-byte budget.");

        script.ScratchBytes = Environment.SystemPageSize - 1;
        var unavailable = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("isolation-unavailable", unavailable.Status);
        Assert.IsNull(unavailable.ExitCode);
        Assert.AreEqual(0L, unavailable.StdoutBytes + unavailable.StderrBytes);
    }

    [TestMethod]
    public async Task ChangedRegistrationCannotReinterpretAnOldOccurrenceAndConfigurationIsFrozen()
    {
        var script = CustomScriptTaskAdapterTests.Script();
        var original = script.Snapshot();
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        script.Content = "exit 71";
        script.Arguments = [];
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await adapter.ExecuteAsync(
            CustomScriptTaskAdapterTests.Occurrence(original), default).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, (await adapter.ExecuteAsync(
            CustomScriptTaskAdapterTests.Occurrence(script), default).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public async Task ParallelInvocationIsRefusedAndCancellationReleasesCapacity()
    {
        var script = CustomScriptTaskAdapterTests.Script("sleep 200 & wait");
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        using var stop = new CancellationTokenSource();
        var first = adapter.ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), stop.Token).AsTask();
        try
        {
            var second = await adapter.ExecuteAsync(CustomScriptTaskAdapterTests.Occurrence(script), default).ConfigureAwait(false);
            Assert.AreEqual("script.busy", second.Detail);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await first.ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task DurableRunNowRecordsIdentityActorFailureAndRestartWithoutReexecuting()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-script-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var script = CustomScriptTaskAdapterTests.Script("printf 'do-not-record'; exit 17");
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        using var registry = new PeriodicRegistryHarness(clock, [adapter]);
        var captures = new StubCaptureSequenceSource();
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        SqliteLocalAutomationStore NewStore() => new(registry.Registry, captures, options, clock, NullLogger<SqliteLocalAutomationStore>.Instance);
        try
        {
            string runKey;
            using (var store = NewStore())
            using (var worker = new LocalAutomationOffSweepWorker())
            using (var runner = new LocalAutomationRunnerService(store, registry.Registry, captures, options, clock,
                NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: worker))
            {
                await store.InitializeAsync(default).ConfigureAwait(false);
                var saved = await store.SaveAsync(new("script-task", "Script", true, LocalAutomationTaskKind.CustomScript,
                    script.Target, LocalAutomationTriggerKind.Periodic, 60, 0, "save-script", "site-owner", null), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
                var queued = await store.RunNowAsync(new("script-task", 1, "run-script", "site-owner", "explicit test"), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
                runKey = queued.State.Runs.Single().RunKey;
                await runner.SweepAsync(default).ConfigureAwait(false);
                await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var run = (await store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
                Assert.AreEqual(LocalAutomationRunOutcome.Failed, run.Outcome);
                Assert.IsNotNull(run.StartedAtUtc);
                Assert.IsNotNull(run.CompletedAtUtc);
                Assert.IsTrue(run.Occurrence!.IsValid());
                Assert.AreEqual(script.Target, run.Occurrence.Definition.TaskTarget);
                Assert.AreEqual("site-owner", run.Preparation!.Actor);
                Assert.IsFalse(run.Detail.Contains("do-not-record", StringComparison.Ordinal));
                clock.Advance(TimeSpan.FromSeconds(60));
                var entry = (await store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
                Assert.IsTrue(await store.TryBeginRunAsync(entry, "script-interrupted", clock.GetUtcNow(), null, default)
                    .ConfigureAwait(false));
            }
            using var restarted = NewStore();
            await restarted.InitializeAsync(default).ConfigureAwait(false);
            var runs = (await restarted.GetStateAsync(default).ConfigureAwait(false)).Runs;
            var retained = runs.Single(run => run.RunKey == runKey);
            Assert.AreEqual(runKey, retained.RunKey);
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, retained.Outcome);
            Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, runs.Single(run => run.RunKey == "script-interrupted").Outcome);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task AbruptLauncherDeathCannotLeaveAnExecutingChild()
    {
        var script = CustomScriptTaskAdapterTests.Script("printf 'ready\\n'; exec sleep 199.1137");
        using var process = new Process { StartInfo = LinuxCustomScriptSandbox.BuildStartInfo(script) };
        Assert.IsTrue(process.Start());
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(CustomScriptSyscallPolicy.Create()).ConfigureAwait(false);
            process.StandardInput.Close();
            // The ephemeral ready marker proves the sandbox child started before its launcher dies.
            Assert.AreEqual("ready", await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            var output = process.StandardOutput.ReadToEndAsync();
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ProcessFanoutFailsInsideItsOwnBudgetAndTheNextTaskSucceeds()
    {
        var script = CustomScriptTaskAdapterTests.Script("""
            i=0
            while [ "$i" -lt 100 ]; do
                sleep 200 &
                i=$((i + 1))
            done
            wait
            """);
        script.TimeoutSeconds = 5;
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("process-failed", result.Status, "The process ceiling must reject fanout before wall timeout.");
        Assert.AreNotEqual(0, result.ExitCode);
        var next = CustomScriptTaskAdapterTests.Script();
        Assert.AreEqual(0, (await LinuxCustomScriptSandbox.ExecuteAsync(next, default).ConfigureAwait(false)).ExitCode);
    }

    [TestMethod]
    public async Task MemoryPressureIsTerminatedWithinTheSandboxBudget()
    {
        var script = CustomScriptTaskAdapterTests.Script("""
            value=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
            while :; do value=$value$value$value$value; done
            """);
        script.TimeoutSeconds = 5;
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("process-failed", result.Status, "The memory limit must fail allocation before wall timeout.");
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual(0, (await LinuxCustomScriptSandbox.ExecuteAsync(CustomScriptTaskAdapterTests.Script(), default)
            .ConfigureAwait(false)).ExitCode);
    }

    [TestMethod]
    [DataRow("memfd")]
    [DataRow("namespace")]
    [DataRow("socket")]
    [DataRow("executable-memory")]
    public async Task NativePayloadCannotBypassResourceOrNamespacePolicies(string mode)
    {
        var bytes = await CustomScriptNativeFixture.CompileAsync(false).ConfigureAwait(false);
        var content = CustomScriptNativeFixture.WritePayload(bytes, "/work/probe") +
            "chmod +x /work/probe; /work/probe " + mode;
        var script = CustomScriptTaskAdapterTests.Script(content);
        script.ScratchBytes = Math.Max(32768, Environment.SystemPageSize);
        Assert.IsTrue(script.IsValid(), "The native regression must fit the supported script contract.");
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(0, result.ExitCode, mode + " must be rejected by the enforced kernel boundary.");
        Assert.AreEqual(2L, result.StdoutBytes, "The native helper must actually execute its assertions.");
    }

    [TestMethod]
    public async Task NativePreloadCannotRunAndScriptStdinCannotConsumeItsContent()
    {
        var bytes = await CustomScriptNativeFixture.CompileAsync(true).ConfigureAwait(false);
        var content = CustomScriptNativeFixture.WritePayload(bytes, "/work/preload.so") +
            "LD_PRELOAD=/work/preload.so /bin/sleep 0 || exit 41; " +
            "if read value; then exit 42; fi; " + string.Concat(Enumerable.Repeat(": padding;", 1500)) + " exit 0";
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(CustomScriptTaskAdapterTests.Script(content), default)
            .ConfigureAwait(false);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(0L, result.StdoutBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LaunchingThreadMayExitAndThreadPoolRetirementCannotKillTheScript(bool threadPool)
    {
        Task<CustomScriptSandboxResult>? execution = null;
        var script = CustomScriptTaskAdapterTests.Script(threadPool ? "sleep 25" : "sleep 1");
        script.TimeoutSeconds = 40;
        var thread = new Thread(() => execution = threadPool
            ? Task.Run(async () => await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false))
            : LinuxCustomScriptSandbox.ExecuteAsync(script, default).AsTask());
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        var result = await execution!.WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(0, result.ExitCode, "The thread-pool parent may retire after its idle timeout.");
    }

    [TestMethod]
    public async Task OrderlyCancellationPersistsTerminalEvidenceBeforeDelayedRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-script-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var script = CustomScriptTaskAdapterTests.Script("printf abc; sleep 30");
        var adapter = new CustomScriptTaskAdapter(Options.Create(new CustomScriptOptions
        { Enabled = true, OwnerAuthorized = true, Scripts = [script] }), clock);
        using var registry = new PeriodicRegistryHarness(clock, [adapter]);
        var captures = new StubCaptureSequenceSource();
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        SqliteLocalAutomationStore NewStore() => new(registry.Registry, captures, options, clock, NullLogger<SqliteLocalAutomationStore>.Instance);
        LocalAutomationRun terminal;
        try
        {
            using (var store = NewStore())
            using (var worker = new LocalAutomationOffSweepWorker())
            using (var runner = new LocalAutomationRunnerService(store, registry.Registry, captures, options, clock,
                NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: worker))
            {
                await store.InitializeAsync(default).ConfigureAwait(false);
                var saved = await store.SaveAsync(new("script-task", "Script", true, LocalAutomationTaskKind.CustomScript,
                    script.Target, LocalAutomationTriggerKind.Periodic, 60, 0, "save", "site-owner", null), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
                await store.RunNowAsync(new("script-task", 1, "run", "site-owner", "cancel test"), default).ConfigureAwait(false);
                var scope = new OwnedSandboxScope();
                using var cancel = new CancellationTokenSource();
                await runner.SweepAsync(cancel.Token).ConfigureAwait(false);
                await scope.WaitForSleepAsync(worker.WhenIdleAsync()).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(7));
                await cancel.CancelAsync().ConfigureAwait(false);
                await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                terminal = (await store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
                Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, terminal.Outcome);
                Assert.AreEqual(clock.GetUtcNow(), terminal.CompletedAtUtc);
                StringAssert.Contains(terminal.Detail, "script.cancelled; exit=", StringComparison.Ordinal);
                StringAssert.Contains(terminal.Detail, "stdoutBytes=3; stderrBytes=0", StringComparison.Ordinal);
                Assert.IsFalse(terminal.Detail.Contains("exit=;", StringComparison.Ordinal));
                scope.AssertDrained();
            }
            clock.Advance(TimeSpan.FromDays(1));
            using var reopened = NewStore();
            await reopened.InitializeAsync(default).ConfigureAwait(false);
            var retained = (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
            Assert.AreEqual(terminal, retained, "Restart must preserve the actual end time, detail and occurrence identity.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ContinuouslyDueTimeoutsCannotStarveAReservedRefreshTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-script-fairness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var script = CustomScriptTaskAdapterTests.Script("sleep 30");
        script.TimeoutSeconds = 1;
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        var refresh = new FakePeriodicTaskAdapter { MinimumIntervalSeconds = 60, RunsOffSweep = true };
        using var registry = new PeriodicRegistryHarness(clock, [adapter, refresh]);
        var captures = new StubCaptureSequenceSource();
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        try
        {
            using var store = new SqliteLocalAutomationStore(registry.Registry, captures, options, clock, NullLogger<SqliteLocalAutomationStore>.Instance);
            using var worker = new LocalAutomationOffSweepWorker();
            using var runner = new LocalAutomationRunnerService(store, registry.Registry, captures, options, clock,
                NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: worker);
            await store.InitializeAsync(default).ConfigureAwait(false);
            foreach (var (id, kind, target) in new[] { ("aaa-script", LocalAutomationTaskKind.CustomScript, script.Target),
                ("orbital-elements-refresh", refresh.TaskKind, FakePeriodicTaskAdapter.Target) })
            {
                var saved = await store.SaveAsync(new(id, id, true, kind, target, LocalAutomationTriggerKind.Periodic,
                    60, 0, "save-" + id, "site-owner", null), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
            }
            for (var tick = 0; tick < 6; tick++)
            {
                clock.Advance(TimeSpan.FromSeconds(300));
                await runner.SweepAsync(default).ConfigureAwait(false);
                await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            Assert.IsGreaterThanOrEqualTo(3, refresh.Executed.Count, "A due built-in refresh must progress despite every script timeout.");
            var state = await store.GetStateAsync(default).ConfigureAwait(false);
            Assert.IsTrue(state.Runs.Any(static run => run.DefinitionId == "aaa-script" && run.Detail.StartsWith("script.timeout", StringComparison.Ordinal)));
            Assert.IsTrue(state.Runs.Any(static run => run.DefinitionId == "orbital-elements-refresh" && run.Outcome == LocalAutomationRunOutcome.Succeeded));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

}
