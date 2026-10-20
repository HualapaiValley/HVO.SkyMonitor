using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// A refresh job over a real orbital store, an intercepting store decorator, a scripted provider client and a manual
/// clock. Nothing here touches the network; every provider response is synthetic.
/// </summary>
internal sealed class OrbitalRefreshTestHarness : IDisposable
{
    public const string ScheduledRunKey = "orbital-elements-refresh|0123456789abcdef|20261019T120000Z";

    private OrbitalRefreshTestHarness(string root, bool enabled, IOrbitalStorageFaultInjector? faults, Meter? meter)
    {
        Root = root;
        Clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        Store = OrbitalElementsStoreTestData.Open(root, Clock, faults: faults);
        Intercept = new InterceptingOrbitalStore(Store);
        Client = new ScriptedOrbitalClient();
        Telemetry = meter is null ? new OrbitalElementsTelemetry(Clock) : new OrbitalElementsTelemetry(Clock, meter);
        Logger = new OrbitalTestLogger<OrbitalElementsRefreshJob>();
        Options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
        {
            OrbitalElements = new OrbitalElementsOptions { Enabled = enabled }
        });
        Job = new OrbitalElementsRefreshJob(Intercept, Client, Options, Clock, Telemetry, Logger);
    }

    public string Root { get; }

    public SignalingTimeProvider Clock { get; }

    public SqliteOrbitalElementsStore Store { get; }

    public InterceptingOrbitalStore Intercept { get; }

    public ScriptedOrbitalClient Client { get; }

    public OrbitalElementsTelemetry Telemetry { get; }

    public OrbitalTestLogger<OrbitalElementsRefreshJob> Logger { get; }

    public IOptions<CameraAgentHostOptions> Options { get; }

    public OrbitalElementsRefreshJob Job { get; }

    public static async Task<OrbitalRefreshTestHarness> CreateAsync(
        string root, bool enabled = true, IOrbitalStorageFaultInjector? faults = null, bool initialize = true,
        Meter? meter = null)
    {
        var harness = new OrbitalRefreshTestHarness(root, enabled, faults, meter);
        if (initialize)
        {
            await harness.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return harness;
    }

    public static byte[] ValidPayload(params int[] catalogNumbers)
        => CelesTrakGpTestData.Payload((catalogNumbers.Length == 0 ? [60001, 60002] : catalogNumbers)
            .Select(static number => CelesTrakGpTestData.RecordFor(number)).ToArray());

    public ValueTask<LocalAutomationExecution> RunAsync(
        OrbitalAttemptTrigger trigger = OrbitalAttemptTrigger.Scheduled,
        DateTimeOffset? scheduledForUtc = null,
        CancellationToken cancellationToken = default)
        => Job.ExecuteAsync(ScheduledRunKey, trigger, scheduledForUtc ?? Clock.GetUtcNow(), cancellationToken);

    public OrbitalFetchResult Retrieved(byte[] raw) => CelesTrakGpTestData.Retrieved(raw, Clock.GetUtcNow());

    public OrbitalFetchResult Halt(int status) => Result(OrbitalFetchDisposition.HttpStatusHalt, status,
        "orbital.non-200-response", string.Create(CultureInfo.InvariantCulture, $"status={status}"));

    public OrbitalFetchResult Result(OrbitalFetchDisposition disposition, int? status, string reasonCode, string detail)
    {
        var now = Clock.GetUtcNow();
        return new OrbitalFetchResult(disposition, status, now.AddSeconds(-1), now, null, null, 0, null, null, null,
            reasonCode, detail);
    }

    /// <summary>The newest attempt row as <c>outcome|reason_code|trigger_kind</c>, or null before any attempt.</summary>
    public string? LastAttempt() => (string?)OrbitalElementsStoreTestData.Scalar(Root,
        "SELECT outcome || '|' || coalesce(reason_code, '') || '|' || trigger_kind FROM orbital_attempts ORDER BY attempt_sequence DESC LIMIT 1;");

    public long AttemptCount() => (long)OrbitalElementsStoreTestData.Scalar(Root, "SELECT COUNT(*) FROM orbital_attempts;")!;

    public Task<OrbitalProviderState> StateAsync() => Store.GetStateAsync(CancellationToken.None).AsTask();

    public void Dispose()
    {
        Telemetry.Dispose();
        Store.Dispose();
    }
}

/// <summary>A manual clock that also reports every timer created on it, so a test can advance only once one exists.</summary>
internal sealed class SignalingTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly ManualTimeProvider _inner = new(start);
    private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _inner.CreateTimer(callback, state, dueTime, period);
        Assert.IsTrue(_created.Writer.TryWrite(dueTime));
        return timer;
    }

    public void Advance(TimeSpan delta) => _inner.Advance(delta);

    /// <summary>Discards the record of every timer created so far, so the next wait observes only a new one.</summary>
    public int DrainCreatedTimers()
    {
        var drained = 0;
        while (_created.Reader.TryRead(out _))
        {
            drained++;
        }
        return drained;
    }

    /// <summary>Waits for the next timer created on this clock and returns its due time.</summary>
    public async Task<TimeSpan> WaitForTimerAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _created.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
    }
}

/// <summary>Answers each request from a queue of scripted behaviours; an empty queue is a test defect.</summary>
internal sealed class ScriptedOrbitalClient : IOrbitalElementsProviderClient
{
    private readonly ConcurrentQueue<Func<CancellationToken, ValueTask<OrbitalFetchResult>>> _script = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Enqueue(OrbitalFetchResult result) => _script.Enqueue(_ => ValueTask.FromResult(result));

    public void Enqueue(Func<CancellationToken, ValueTask<OrbitalFetchResult>> behaviour) => _script.Enqueue(behaviour);

    /// <summary>Holds the request until the caller cancels it.</summary>
    public void EnqueueHang() => _script.Enqueue(static async token =>
    {
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    });

    public ValueTask<OrbitalFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        RequestStarted.TrySetResult();
        if (!_script.TryDequeue(out var behaviour))
        {
            throw new AssertFailedException("The provider client received an unscripted request.");
        }
        return behaviour(cancellationToken);
    }
}

/// <summary>Forwards to the real store and can fail chosen calls by their 1-based call number.</summary>
internal sealed class InterceptingOrbitalStore(IOrbitalElementsStore inner) : IOrbitalElementsStore
{
    private int _halts;
    private int _transports;
    private int _quarantines;
    private int _publishes;
    private int _retentions;
    private int _retrievals;
    private int _interruptions;

    public Func<int, Exception?>? HaltFault { get; set; }

    public Func<int, Exception?>? TransportFault { get; set; }

    public Func<int, Exception?>? QuarantineFault { get; set; }

    public Func<int, Exception?>? PublishFault { get; set; }

    public Func<int, Exception?>? RetentionFault { get; set; }

    public Func<int, Exception?>? RetrievalFault { get; set; }

    public Func<int, Exception?>? InterruptFault { get; set; }

    /// <summary>When set, every state read and resume command throws it, as a failing store would.</summary>
    public Exception? ReadFault { get; set; }

    public int Halts => Volatile.Read(ref _halts);

    public int Transports => Volatile.Read(ref _transports);

    public int Quarantines => Volatile.Read(ref _quarantines);

    public int Publishes => Volatile.Read(ref _publishes);

    public int Retrievals => Volatile.Read(ref _retrievals);

    public int Interruptions => Volatile.Read(ref _interruptions);

    public ConcurrentQueue<string> QuarantineCodes { get; } = new();

    public TimeSpan PhaseOffset => inner.PhaseOffset;

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);

    public ValueTask<OrbitalProviderState> GetStateAsync(CancellationToken cancellationToken)
        => ReadFault is { } fault ? ValueTask.FromException<OrbitalProviderState>(fault) : inner.GetStateAsync(cancellationToken);

    public ValueTask<OrbitalAttemptAdmission> TryBeginAttemptAsync(
        string runKey, OrbitalAttemptTrigger trigger, CancellationToken cancellationToken)
        => inner.TryBeginAttemptAsync(runKey, trigger, cancellationToken);

    public ValueTask RecordHaltAsync(string attemptId, int? httpStatusCode, string reason, CancellationToken cancellationToken)
    {
        Fail(HaltFault, ref _halts);
        return inner.RecordHaltAsync(attemptId, httpStatusCode, reason, cancellationToken);
    }

    public ValueTask RecordRetrievalAsync(string attemptId, DateTimeOffset retrievedUtc, CancellationToken cancellationToken)
    {
        Fail(RetrievalFault, ref _retrievals);
        return inner.RecordRetrievalAsync(attemptId, retrievedUtc, cancellationToken);
    }

    public ValueTask<bool> RecordInterruptedAsync(string attemptId, CancellationToken cancellationToken)
    {
        Fail(InterruptFault, ref _interruptions);
        return inner.RecordInterruptedAsync(attemptId, cancellationToken);
    }

    public ValueTask RecordTransportFailureAsync(string attemptId, string reasonCode, string detail, CancellationToken cancellationToken)
    {
        Fail(TransportFault, ref _transports);
        return inner.RecordTransportFailureAsync(attemptId, reasonCode, detail, cancellationToken);
    }

    public ValueTask RecordQuarantineAsync(string attemptId, ReadOnlyMemory<byte> rawBytes, string? rawSha256,
        string reasonCode, string detail, CancellationToken cancellationToken)
    {
        QuarantineCodes.Enqueue(reasonCode);
        Fail(QuarantineFault, ref _quarantines);
        return inner.RecordQuarantineAsync(attemptId, rawBytes, rawSha256, reasonCode, detail, cancellationToken);
    }

    public ValueTask<OrbitalPublication> PublishAndActivateAsync(string attemptId, OrbitalSnapshot candidate,
        ReadOnlyMemory<byte> rawBytes, CancellationToken cancellationToken)
    {
        Fail(PublishFault, ref _publishes);
        return inner.PublishAndActivateAsync(attemptId, candidate, rawBytes, cancellationToken);
    }

    public ValueTask<OrbitalCommandResult> ResumeProviderAsync(OrbitalResumeRequest request, CancellationToken cancellationToken)
        => ReadFault is { } fault
            ? ValueTask.FromException<OrbitalCommandResult>(fault)
            : inner.ResumeProviderAsync(request, cancellationToken);

    public ValueTask<OrbitalRetentionResult> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        Fail(RetentionFault, ref _retentions);
        return inner.ApplyRetentionAsync(cancellationToken);
    }

    public ValueTask<bool> AddReferenceAsync(string manifestSha256, string holder, CancellationToken cancellationToken)
        => inner.AddReferenceAsync(manifestSha256, holder, cancellationToken);

    public ValueTask<bool> RemoveReferenceAsync(string manifestSha256, string holder, CancellationToken cancellationToken)
        => inner.RemoveReferenceAsync(manifestSha256, holder, cancellationToken);

    private static void Fail(Func<int, Exception?>? fault, ref int counter)
    {
        var call = Interlocked.Increment(ref counter);
        if (fault?.Invoke(call) is { } exception)
        {
            throw exception;
        }
    }
}

/// <summary>Captures level, event id, rendered message and exception of every entry.</summary>
internal sealed class OrbitalTestLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, int EventId, string Message, Exception? Exception)> Entries { get; } = new();

    public IReadOnlyList<int> EventIds => Entries.Select(static entry => entry.EventId).ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, eventId.Id, formatter(state, exception), exception));

    public string Rendered() => new StringBuilder().AppendJoin('\n', Entries.Select(static entry => entry.Message)).ToString();
}
