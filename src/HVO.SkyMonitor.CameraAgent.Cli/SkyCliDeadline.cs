namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>
/// A command's one deadline, <c>--timeout</c>: how long it may spend waiting for CameraAgent. Its <see cref="Token"/>
/// goes to every request, every read of an answer's body and every wait between reads, so nothing outlasts it; it is
/// also cancelled by the interrupt. Time spent waiting for a person to answer a prompt does not count (<see cref="Pause"/>).
/// </summary>
internal sealed class SkyCliDeadline : IDisposable
{
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _expiry;
    private readonly CancellationTokenSource _linked;
    private DateTimeOffset _expiresAt;
    private TimeSpan? _pausedWithRemaining;

    public SkyCliDeadline(TimeProvider time, TimeSpan limit, CancellationToken interrupt)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        Limit = limit;
        _expiry = new CancellationTokenSource(limit, time);
        _expiresAt = time.GetUtcNow() + limit;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(interrupt, _expiry.Token);
    }

    /// <summary>The time the command was given, <c>--timeout</c>.</summary>
    public TimeSpan Limit { get; }

    /// <summary>Cancelled when the deadline passes or the command is interrupted.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>True once the deadline has passed (and not merely because of the interrupt).</summary>
    public bool Expired => _expiry.IsCancellationRequested;

    /// <summary>The time left, for saying how long a wait may last.</summary>
    public TimeSpan Remaining => _pausedWithRemaining ?? (Expired ? TimeSpan.Zero : Max(_expiresAt - _time.GetUtcNow(), TimeSpan.Zero));

    /// <summary>The message a command that ran out of time ends with.</summary>
    public string Describe(string? waitingFor = null)
        => $"Timed out after {Seconds(Limit)} s (--timeout){(waitingFor is null ? ": CameraAgent did not answer in time" : $" {waitingFor}")}. Give it longer with --timeout.";

    /// <summary>Stops the clock while a person answers a prompt; disposing the result starts it again with the time that was left.</summary>
    public IDisposable Pause()
    {
        if (_pausedWithRemaining is not null || Expired)
        {
            return Resumption.None;
        }

        var remaining = _expiresAt - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Resumption.None;
        }

        _expiry.CancelAfter(Timeout.InfiniteTimeSpan);
        _pausedWithRemaining = remaining;
        return new Resumption(this);
    }

    /// <summary>
    /// The deadline no longer runs: for <c>status --watch</c>, which runs until it is interrupted and gives each read a
    /// deadline of its own.
    /// </summary>
    public void Stop()
    {
        if (!Expired)
        {
            _expiry.CancelAfter(Timeout.InfiniteTimeSpan);
            _pausedWithRemaining = Limit;
        }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _expiry.Dispose();
    }

    internal static int Seconds(TimeSpan time) => (int)Math.Ceiling(time.TotalSeconds);

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private void Resume()
    {
        if (_pausedWithRemaining is { } remaining)
        {
            _pausedWithRemaining = null;
            _expiresAt = _time.GetUtcNow() + remaining;
            _expiry.CancelAfter(remaining);
        }
    }

    private sealed class Resumption(SkyCliDeadline? deadline) : IDisposable
    {
        public static readonly Resumption None = new(null);

        public void Dispose() => deadline?.Resume();
    }
}
