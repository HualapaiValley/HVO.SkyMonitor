using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Data.Common;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeWorkerTests
{
    [TestMethod]
    public async Task ClaimFailureRetriesWithoutStoppingSlotAsync()
    {
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => attempt == 1
                ? Task.FromException<CentralDerivativeJobLease?>(new InvalidOperationException("database unavailable"))
                : Task.FromResult<CentralDerivativeJobLease?>(null)
        };
        await using var harness = CreateHarness(jobs, _ => new ImmediateExecutor());

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        jobs.ClaimCount.Should().BeGreaterThanOrEqualTo(2);
        harness.Telemetry.HasRecentDependencyFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task RenewalFailureCancelsAndJoinsExecutorBeforeClaimingAgainAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null),
            Renew = (_, _) => Task.FromException<CentralDerivativeJobLease>(
                new InvalidOperationException("renewal unavailable"))
        };
        var gate = new ExecutorGate(waitAfterCancellation: true);
        await using var harness = CreateHarness(jobs, _ => new GatedExecutor(gate), renewalInterval: TimeSpan.FromMilliseconds(10));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        jobs.ClaimCount.Should().Be(1);
        gate.Disposed.Should().BeFalse();
        gate.Release.TrySetResult();
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        jobs.FailCount.Should().Be(0);
        gate.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task RenewalFailureWinsRaceWithNormalExecutorCompletionAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null),
            Renew = (_, _) => Task.FromException<CentralDerivativeJobLease>(
                new InvalidOperationException("renewal unavailable"))
        };
        var gate = new ExecutorGate(waitAfterCancellation: false, completeAfterCancellation: true);
        await using var harness = CreateHarness(
            jobs,
            _ => new GatedExecutor(gate),
            renewalInterval: TimeSpan.FromMilliseconds(10));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        harness.Telemetry.HasRecentRenewalFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeTrue();
        harness.Telemetry.LastSuccessUtc.Should().BeNull(
            "results produced after lease renewal fails must not be accepted");
        jobs.FailCount.Should().Be(0);
        gate.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task SuccessfulExecutionCancelsRenewalWithoutRecordingFailureAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null)
        };
        await using var harness = CreateHarness(jobs, _ => new ImmediateExecutor());

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        harness.Telemetry.HasRecentRenewalFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task StopCancelsAndJoinsActiveExecutorAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null)
        };
        var gate = new ExecutorGate(waitAfterCancellation: true);
        await using var harness = CreateHarness(jobs, _ => new GatedExecutor(gate));
        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var stop = harness.Worker.StopAsync(CancellationToken.None);
        await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        stop.IsCompleted.Should().BeFalse();
        gate.Disposed.Should().BeFalse();
        gate.Release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        gate.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task DatabaseExecutionAndFailurePersistenceErrorsDoNotStopSlotAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null),
            Fail = (_, _) => Task.FromException(new TestDbException("failure persistence unavailable"))
        };
        await using var harness = CreateHarness(
            jobs,
            _ => new ThrowingExecutor(new TestDbException("execution database unavailable")));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        jobs.FailCount.Should().Be(1);
        jobs.LastRetryable.Should().BeTrue();
        jobs.LastError.Should().Be(nameof(TestDbException));
        jobs.ClaimCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task UnexpectedFailurePersistsExceptionClassificationAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null)
        };
        await using var harness = CreateHarness(
            jobs,
            _ => new ThrowingExecutor(new InvalidOperationException("invalid execution state")));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        jobs.FailCount.Should().Be(1);
        jobs.LastRetryable.Should().BeFalse();
        jobs.LastError.Should().Be("processing.execution-failed.InvalidOperationException");
    }

    [TestMethod]
    public async Task PersistenceFailurePreservesStableReasonCodeAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null)
        };
        await using var harness = CreateHarness(
            jobs,
            _ => new ThrowingExecutor(new CentralTransientPersistenceException(
                CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                "invalid persistence binding")));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        jobs.FailCount.Should().Be(1);
        jobs.LastRetryable.Should().BeFalse();
        jobs.LastError.Should().Be(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding);
    }

    [TestMethod]
    public async Task CanceledRenewalCancelsExecutorWithoutDatabaseFailureOrLeaseLossAsync()
    {
        var lease = CreateLease();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? lease : null),
            Renew = (_, _) => Task.FromException<CentralDerivativeJobLease>(
                new CentralDerivativeLeaseCanceledException())
        };
        var gate = new ExecutorGate(waitAfterCancellation: false, completeAfterCancellation: true);
        await using var harness = CreateHarness(
            jobs,
            _ => new GatedExecutor(gate),
            renewalInterval: TimeSpan.FromMilliseconds(10));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        harness.Telemetry.HasRecentDependencyFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeFalse("a cancellation-refused renewal is not a database outage");
        harness.Telemetry.HasRecentRenewalFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeFalse();
        harness.Telemetry.LastSuccessUtc.Should().BeNull(
            "results produced after cancellation reached the lease must not be accepted");
        jobs.FailCount.Should().Be(0, "the lease authority terminalizes the canceled node through expiry");
        gate.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task SignaledConvergenceClassifiesGraphStateFailuresWithoutDegradingDatabaseHealthAsync()
    {
        var signal = new CentralProcessingGraphConvergenceSignal();
        var scheduler = new ScriptedGraphScheduler
        {
            Converge = id => id == ScriptedGraphScheduler.CorruptExecutionId
                ? Task.FromException(new CentralDerivativeJobStateException("corrupt frozen graph state"))
                : Task.CompletedTask
        };
        var jobs = new ScriptedJobService();
        signal.Signal(ScriptedGraphScheduler.CorruptExecutionId);
        signal.Signal(Guid.NewGuid());
        await using var harness = CreateHarness(jobs, _ => new ImmediateExecutor(), graphScheduler: scheduler, signal: signal);

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await jobs.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitUntilAsync(() => scheduler.ConvergedIds.Count == 2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        scheduler.ConvergedIds.Should().Contain(ScriptedGraphScheduler.CorruptExecutionId);
        scheduler.ConvergedIds.Should().HaveCount(2, "a corrupt signaled execution must not stop draining the signal");
        harness.Telemetry.HasRecentDependencyFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeFalse("graph-state faults are not database dependency failures");

        var databaseSignal = new CentralProcessingGraphConvergenceSignal();
        var databaseScheduler = new ScriptedGraphScheduler
        {
            Converge = _ => Task.FromException(new TestDbException("database unavailable"))
        };
        databaseSignal.Signal(Guid.NewGuid());
        await using var databaseHarness = CreateHarness(
            new ScriptedJobService(), _ => new ImmediateExecutor(), graphScheduler: databaseScheduler, signal: databaseSignal);
        await databaseHarness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        await databaseHarness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        databaseHarness.Telemetry.HasRecentDependencyFailure(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))
            .Should().BeTrue("a real database fault during signaled convergence still degrades database health");
    }

    /// <summary>
    /// The health check reports <c>graph-recovery-stale</c> after <c>2 x QueueSampleInterval</c>. Graph recovery used to
    /// run inline in slot 0's claim loop, so a recipe longer than that on a single-slot worker starved recovery and
    /// signaled convergence. Both must keep running while the only slot is still executing.
    /// </summary>
    [TestMethod]
    public async Task GraphRecoveryAndSignaledConvergenceContinueWhileTheOnlySlotExecutesALongRecipeAsync()
    {
        var gate = new BlockingExecutor();
        var jobs = new ScriptedJobService
        {
            Claim = (attempt, _) => Task.FromResult(attempt == 1 ? CreateLease() : null)
        };
        var signal = new CentralProcessingGraphConvergenceSignal();
        var scheduler = new ScriptedGraphScheduler();
        await using var harness = CreateHarness(
            jobs,
            _ => gate,
            graphScheduler: scheduler,
            signal: signal,
            queueSampleInterval: TimeSpan.FromMilliseconds(50));

        await harness.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var recoveryPollsAtEntry = scheduler.ConvergeBatchCount;
        var executionId = Guid.NewGuid();
        signal.Signal(executionId);
        await WaitUntilAsync(
            () => scheduler.ConvergedIds.Contains(executionId) && scheduler.ConvergeBatchCount >= recoveryPollsAtEntry + 2,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        gate.Released.Should().BeFalse("the recipe is still executing while recovery and convergence progressed");
        gate.Release();
        await harness.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        scheduler.ConvergedIds.Should().Contain(executionId);
        scheduler.ConvergeBatchCount.Should().BeGreaterThanOrEqualTo(recoveryPollsAtEntry + 2,
            "periodic recovery kept polling on its own cadence while the slot was busy");
        harness.Telemetry.ActiveCount.Should().Be(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The worker did not reach the expected state in time.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned async-disposable harness owns the provider, worker, and telemetry instances.")]
    private static WorkerHarness CreateHarness(
        ScriptedJobService jobs,
        Func<IServiceProvider, ICentralDerivativeJobExecutor> executor,
        TimeSpan? renewalInterval = null,
        ICentralProcessingGraphScheduler? graphScheduler = null,
        CentralProcessingGraphConvergenceSignal? signal = null,
        TimeSpan? queueSampleInterval = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ICentralDerivativeJobService>(_ => jobs);
        services.AddScoped<ICentralDerivativeWindowResolver>(_ => new NoopWindowResolver());
        services.AddScoped(executor);
        if (graphScheduler is not null)
        {
            services.AddScoped(_ => graphScheduler);
        }
        var provider = services.BuildServiceProvider();
        var telemetry = new CentralDerivativeWorkerTelemetry();
        var options = Options.Create(new CentralDerivativeWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            QueueSampleInterval = queueSampleInterval ?? TimeSpan.FromHours(1),
            LeaseDuration = TimeSpan.FromSeconds(1),
            RenewalInterval = renewalInterval ?? TimeSpan.FromMilliseconds(250)
        });
        var worker = new CentralDerivativeWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            telemetry,
            TimeProvider.System,
            NullLogger<CentralDerivativeWorker>.Instance,
            signal);
        return new WorkerHarness(provider, telemetry, worker);
    }

    private sealed class ScriptedGraphScheduler : ICentralProcessingGraphScheduler
    {
        public static readonly Guid CorruptExecutionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private readonly System.Collections.Concurrent.ConcurrentBag<Guid> _converged = [];

        public Func<Guid, Task> Converge { get; init; } = static _ => Task.CompletedTask;

        public IReadOnlyCollection<Guid> ConvergedIds => _converged;

        public Task<CentralProcessingGraphScheduleResult> ScheduleLiveAsync(
            Guid centralArtifactId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
            CentralProcessingGraphReplayRequest request, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ConvergeAsync(Guid executionId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            _converged.Add(executionId);
            return Converge(executionId);
        }

        public int ConvergeBatchCount => Volatile.Read(ref _convergeBatchCount);

        private int _convergeBatchCount;

        public Task ConvergeBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _convergeBatchCount);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingExecutor : ICentralDerivativeJobExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released => _release.Task.IsCompleted;

        public void Release() => _release.TrySetResult();

        public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
            CentralDerivativeJobLease lease,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.Produced, Guid.NewGuid(), null);
        }
    }

    private static CentralDerivativeJobLease CreateLease() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "test-worker",
        DateTimeOffset.UtcNow.AddMinutes(1),
        Guid.NewGuid(),
        Guid.NewGuid(),
        FrameArtifactRole.Raw,
        "raw-v1",
        "s3://test/source.raw",
        new string('A', 64),
        "application/octet-stream",
        Guid.NewGuid(),
        "test-agent",
        DateTimeOffset.UtcNow,
        null,
        null,
        FrameArtifactRole.Metadata,
        "quality-v1",
        "quality",
        BuiltInProcessingRecipes.ImageQuality,
        "{}",
        "{}",
        new string('B', 64),
        new string('C', 64),
        null,
        null,
        1,
        3);

    private sealed class WorkerHarness(
        ServiceProvider provider,
        CentralDerivativeWorkerTelemetry telemetry,
        CentralDerivativeWorker worker) : IAsyncDisposable
    {
        public CentralDerivativeWorkerTelemetry Telemetry => telemetry;

        public CentralDerivativeWorker Worker => worker;

        public async ValueTask DisposeAsync()
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            worker.Dispose();
            telemetry.Dispose();
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ImmediateExecutor : ICentralDerivativeJobExecutor
    {
        public Task<CentralDerivativeExecutionResult> ExecuteAsync(
            CentralDerivativeJobLease lease,
            CancellationToken cancellationToken)
            => Task.FromResult(new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.Produced, Guid.NewGuid(), null));
    }

    private sealed class ThrowingExecutor(Exception exception) : ICentralDerivativeJobExecutor
    {
        public Task<CentralDerivativeExecutionResult> ExecuteAsync(
            CentralDerivativeJobLease lease,
            CancellationToken cancellationToken)
            => Task.FromException<CentralDerivativeExecutionResult>(exception);
    }

    private sealed class GatedExecutor(ExecutorGate gate) : ICentralDerivativeJobExecutor, IDisposable
    {
        public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
            CentralDerivativeJobLease lease,
            CancellationToken cancellationToken)
        {
            gate.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                gate.Canceled.TrySetResult();
                if (gate.WaitAfterCancellation)
                {
                    await gate.Release.Task.ConfigureAwait(false);
                }
                if (gate.CompleteAfterCancellation)
                {
                    return new CentralDerivativeExecutionResult(
                        ProcessingOutcomeStatus.Produced,
                        Guid.NewGuid(),
                        null);
                }
                throw;
            }
            throw new InvalidOperationException("The gated executor unexpectedly resumed.");
        }

        public void Dispose() => gate.Disposed = true;
    }

    private sealed class ExecutorGate(bool waitAfterCancellation, bool completeAfterCancellation = false)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WaitAfterCancellation { get; } = waitAfterCancellation;

        public bool CompleteAfterCancellation { get; } = completeAfterCancellation;

        public bool Disposed { get; set; }
    }

    private sealed class ScriptedJobService : ICentralDerivativeJobService
    {
        private int _claimCount;
        private int _failCount;
        private int _lastRetryable;
        private string? _lastError;

        public Func<int, CancellationToken, Task<CentralDerivativeJobLease?>> Claim { get; init; }
            = static (_, _) => Task.FromResult<CentralDerivativeJobLease?>(null);

        public Func<CentralDerivativeJobLease, CancellationToken, Task<CentralDerivativeJobLease>>? Renew { get; init; }

        public Func<bool, CancellationToken, Task>? Fail { get; init; }

        public TaskCompletionSource SecondClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ClaimCount => Volatile.Read(ref _claimCount);

        public int FailCount => Volatile.Read(ref _failCount);

        public bool LastRetryable => Volatile.Read(ref _lastRetryable) != 0;

        public string? LastError => Volatile.Read(ref _lastError);

        public async Task<CentralDerivativeJobLease?> ClaimNextAsync(
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _claimCount);
            if (attempt >= 2)
            {
                SecondClaim.TrySetResult();
            }
            return await Claim(attempt, cancellationToken).ConfigureAwait(false);
        }

        public Task<CentralDerivativeJobLease> RenewLeaseAsync(
            Guid jobId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var lease = CreateLease() with { JobId = jobId, LeaseToken = leaseToken };
            return Renew?.Invoke(lease, cancellationToken) ?? Task.FromResult(lease);
        }

        public Task FailAsync(
            Guid jobId,
            Guid leaseToken,
            string error,
            bool retryable,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _failCount);
            Volatile.Write(ref _lastRetryable, retryable ? 1 : 0);
            Volatile.Write(ref _lastError, error);
            return Fail?.Invoke(retryable, cancellationToken) ?? Task.CompletedTask;
        }

        public Task CompleteAsync(
            Guid jobId,
            Guid leaseToken,
            Guid resultArtifactId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteWithoutArtifactAsync(
            Guid jobId,
            Guid leaseToken,
            string reasonCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SkipAsync(
            Guid jobId,
            Guid leaseToken,
            string reasonCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkInputUnavailableAsync(
            Guid jobId,
            Guid leaseToken,
            Guid centralArtifactId,
            byte[] expectedSourceRowVersion,
            string reasonCode,
            bool quarantine,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoopWindowResolver : ICentralDerivativeWindowResolver
    {
        public Task ResolveAffectedAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1032:Implement standard exception constructors",
        Justification = "This private test double only represents a database provider failure.")]
    private sealed class TestDbException(string message) : DbException(message);
}
