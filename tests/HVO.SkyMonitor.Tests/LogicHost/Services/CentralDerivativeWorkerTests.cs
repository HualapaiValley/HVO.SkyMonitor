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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned async-disposable harness owns the provider, worker, and telemetry instances.")]
    private static WorkerHarness CreateHarness(
        ScriptedJobService jobs,
        Func<IServiceProvider, ICentralDerivativeJobExecutor> executor,
        TimeSpan? renewalInterval = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ICentralDerivativeJobService>(_ => jobs);
        services.AddScoped<ICentralDerivativeWindowResolver>(_ => new NoopWindowResolver());
        services.AddScoped(executor);
        var provider = services.BuildServiceProvider();
        var telemetry = new CentralDerivativeWorkerTelemetry();
        var options = Options.Create(new CentralDerivativeWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            QueueSampleInterval = TimeSpan.FromHours(1),
            LeaseDuration = TimeSpan.FromSeconds(1),
            RenewalInterval = renewalInterval ?? TimeSpan.FromMilliseconds(250)
        });
        var worker = new CentralDerivativeWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            telemetry,
            TimeProvider.System,
            NullLogger<CentralDerivativeWorker>.Instance);
        return new WorkerHarness(provider, telemetry, worker);
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
        "minio://test/source.raw",
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
                throw;
            }
            throw new InvalidOperationException("The gated executor unexpectedly resumed.");
        }

        public void Dispose() => gate.Disposed = true;
    }

    private sealed class ExecutorGate(bool waitAfterCancellation)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WaitAfterCancellation { get; } = waitAfterCancellation;

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
