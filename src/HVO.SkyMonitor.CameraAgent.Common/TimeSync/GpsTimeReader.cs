using System.Globalization;
using System.Runtime.CompilerServices;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

public enum GpsSignalState
{
    Missing,
    Available,
    NoFix,
    Stale,
    Unavailable,
}

/// <summary>
/// GPS receiver diagnostics. ArrivalOffset ages NMEA UTC monotonically against the current host wall clock,
/// including unknown serial latency; it is not a precision clock measurement. A qualified PPS offset has a separate uncertainty bound.
/// </summary>
public sealed record GpsTimeStatus(
    GpsSignalState State,
    DateTimeOffset? Utc,
    DateTimeOffset? LastSentenceUtc,
    TimeSpan? ArrivalOffset,
    int? FixQuality,
    int? Satellites,
    GpsSignalState PpsState,
    DateTimeOffset? LastPpsUtc)
{
    public DateTimeOffset? PpsMeasuredUtc { get; internal init; }
    public TimeSpan? PpsOffset { get; internal init; }
    public TimeSpan? PpsUncertainty { get; internal init; }
    public bool CanVerifyClock => State == GpsSignalState.Available && PpsOffset.HasValue && PpsUncertainty.HasValue;

    internal static GpsTimeStatus Empty(GpsSignalState state) => new(state, null, null, null, null, null, GpsSignalState.Missing, null);
}

/// <summary>
/// Inclusive plausibility bounds for receiver dates, supplied independently of the host clock.
/// </summary>
public sealed class GpsUtcWindow
{
    public GpsUtcWindow(DateTimeOffset earliestUtc, DateTimeOffset latestUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latestUtc, earliestUtc);
        EarliestUtc = earliestUtc;
        LatestUtc = latestUtc;
    }

    public DateTimeOffset EarliestUtc { get; }
    public DateTimeOffset LatestUtc { get; }
}

/// <summary>
/// Reads checksummed GP/GN RMC and GGA from a caller-owned, cancellation-aware stream.
/// Serializes reads and expires evidence using monotonic time. A receiver-specific DCD edge
/// can be associated with a following whole-second RMC. An unassociated edge is diagnostic only.
/// </summary>
public sealed class GpsTimeReader(TimeProvider timeProvider, GpsUtcWindow? utcWindow = null) : IDisposable
{
    public const int MaximumSentenceLength = 82;
    public const int MaximumBytesPerRead = 4096;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    // Receiver profile: DCD at this tty reflects the UTC boundary within 5 ms (including adapter latency).
    internal static readonly TimeSpan MaximumSignalLatency = TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed, _cancellationComplete;
    private int _outstanding;
    private readonly Lock _gate = new();
    private readonly ConditionalWeakTable<Stream, LineFraming> _framing = new();
    private readonly LineFraming _serialFraming = new();
    private DateTimeOffset? _utc, _lastSentence, _lastPps;
    private long? _rmcTimestamp, _ggaTimestamp, _ppsTimestamp;
    private int? _quality, _satellites;
    private bool _validFix, _unavailable;
    private long? _pendingEdge;
    private TimeSpan? _edgeWidth;
    private long? _pairedEdge;
    private TimeSpan? _pairedWidth;
    private DateTimeOffset? _pairedHostUtc;

    /// <summary>
    /// Consumes at most 4096 bytes within two seconds, including waiting for another read. EOF ends this scripted
    /// round. Expected stream failures mark the source unavailable and invalidate prior evidence. If the caller also
    /// requests cancellation, it propagates after invalidation. Caller cancellation and an internal deadline on their
    /// own retain completed evidence, subject to normal expiry.
    /// </summary>
    /// <remarks>
    /// Streams honor cancellation by throwing <see cref="OperationCanceledException"/>. Production adapters must
    /// translate I/O aborted by their own cancellation or deadline handling into that exception. An
    /// <see cref="IOException"/> or <see cref="ObjectDisposedException"/> from the stream read invalidates evidence.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// The caller canceled the read. The exception carries <paramref name="cancellationToken"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public async Task ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _outstanding++;
        }
        try
        {
            await ReadRoundAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _outstanding--;
                ReleaseIfDrained();
            }
        }
    }

    private async Task ReadRoundAsync(Stream source, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _lifetime.Token);
        var entered = false;
        LineFraming? framing = null;
        try
        {
            await _reading.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
            framing = _framing.GetValue(source, static _ => new LineFraming());
            var buffer = new byte[1];
            for (var count = 0; count < MaximumBytesPerRead; count++)
            {
                linked.Token.ThrowIfCancellationRequested();
                int bytesRead;
                try
                {
                    bytesRead = await source.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or
                    ObjectDisposedException || exception is OperationCanceledException &&
                    !cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _unavailable = true;
                        _utc = _lastSentence = _lastPps = null;
                        _rmcTimestamp = _ggaTimestamp = _ppsTimestamp = null;
                        _quality = _satellites = null;
                        _validFix = false;
                        _pendingEdge = _pairedEdge = null;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
                }
                if (bytesRead == 0)
                {
                    break;
                }
                lock (_gate) Consume(framing, (char)buffer[0]);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested &&
            exception.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (entered)
            {
                // A round boundary is not a physical line boundary. Never admit the remainder of a partial line.
                if (framing is { Length: > 0 })
                {
                    framing.DiscardUntilLineFeed = true;
                    framing.Length = 0;
                }
                _reading.Release();
            }
        }
    }

    /// <summary>Records receipt of a simulated edge only. It never invents an edge's UTC second or offset.</summary>
    public void ObserveSimulatedPps()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastPps = _time.GetUtcNow();
            _ppsTimestamp = _time.GetTimestamp();
        }
    }

    // A rising DCD transition lies between the last low observation and the first high one.
    internal void ObserveRisingEdge(long lastLow, long firstHigh)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _ppsTimestamp = firstHigh;
            _lastPps = _time.GetUtcNow();
            _edgeWidth = _time.GetElapsedTime(lastLow, firstHigh);
            _pendingEdge = _edgeWidth <= TimeSpan.FromMilliseconds(100) ? firstHigh : null;
            if (!_pendingEdge.HasValue) _pairedEdge = null;
        }
    }

    internal void LosePps()
    {
        lock (_gate)
        {
            _pendingEdge = _pairedEdge = _ppsTimestamp = null;
            _lastPps = null;
        }
    }

    public GpsTimeStatus GetStatus()
    {
        lock (_gate)
        {
            if (_disposed) return GpsTimeStatus.Empty(GpsSignalState.Unavailable);
            var beforeWall = _time.GetTimestamp();
            var hostUtc = _time.GetUtcNow();
            var now = _time.GetTimestamp();
            var fresh = Fresh(_rmcTimestamp, now);
            var freshGga = Fresh(_ggaTimestamp, now);
            var state = _unavailable ? GpsSignalState.Unavailable
                : _rmcTimestamp is null ? GpsSignalState.Missing
                : !fresh ? GpsSignalState.Stale
                : !_validFix || (freshGga && _quality == 0) ? GpsSignalState.NoFix : GpsSignalState.Available;
            var qualified = state == GpsSignalState.Available && (!freshGga || _quality is >= 1 and <= 5)
                && _pairedEdge is { } paired && _time.GetElapsedTime(paired, now) < StaleAfter;
            return new GpsTimeStatus(state, _utc, _lastSentence,
                state == GpsSignalState.Available
                    ? _utc - hostUtc + _time.GetElapsedTime(_rmcTimestamp!.Value, now) : null,
                freshGga ? _quality : null, freshGga ? _satellites : null,
                _ppsTimestamp is null ? GpsSignalState.Missing
                    : Fresh(_ppsTimestamp, now) ? GpsSignalState.Available : GpsSignalState.Stale, _lastPps)
            {
                PpsMeasuredUtc = qualified ? _pairedHostUtc : null,
                PpsOffset = qualified ? _utc - hostUtc + _time.GetElapsedTime(_pairedEdge!.Value, now) : null,
                PpsUncertainty = qualified ? MaximumSignalLatency + _pairedWidth + _time.GetElapsedTime(beforeWall, now) : null,
            };
        }
    }

    /// <summary>Attaches diagnostics without replacing SNTP selection, kernel evidence, or clock assessment.</summary>
    public ClockSyncSnapshot ReportInto(ClockSyncSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { Gps = GetStatus() };
    }

    /// <summary>Revokes evidence and wakes both the active read and any waiters. Streams remain caller-owned.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try
        {
            _lifetime.Cancel();
        }
        finally
        {
            lock (_gate)
            {
                _cancellationComplete = true;
                ReleaseIfDrained();
            }
        }
    }

    // Called under _gate. No read can enter after disposal starts. The last of cancellation and read cleanup
    // to finish owns teardown, including a read that unwinds synchronously inside Cancel().
    private void ReleaseIfDrained()
    {
        if (_cancellationComplete && _outstanding == 0)
        {
            _reading.Dispose();
            _lifetime.Dispose();
        }
    }

    // The serial poll owns this framing across short, nonblocking device reads. Each poll has a bounded buffer.
    internal void ConsumeSerialBytes(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var value in bytes) Consume(_serialFraming, (char)value);
        }
    }

    private void Consume(LineFraming framing, char value)
    {
        if (value == '\n')
        {
            if (!framing.DiscardUntilLineFeed)
            {
                var contentLength = framing.Length > 0 && framing.Sentence[framing.Length - 1] == '\r' ? framing.Length - 1 : framing.Length;
                Accept(framing.Sentence.AsSpan(0, contentLength), framing.StartEdge);
            }
            framing.Length = 0;
            framing.DiscardUntilLineFeed = false;
        }
        else if (!framing.DiscardUntilLineFeed)
        {
            if (framing.Length == framing.Sentence.Length || value >= 127 || (value < 32 && value != '\r') ||
                (framing.Length > 0 && framing.Sentence[framing.Length - 1] == '\r'))
            {
                framing.DiscardUntilLineFeed = true;
            }
            else
            {
                if (framing.Length == 0) framing.StartEdge = _pendingEdge;
                framing.Sentence[framing.Length++] = value;
            }
        }
    }

    private sealed class LineFraming
    {
        internal char[] Sentence { get; } = new char[MaximumSentenceLength];
        internal int Length { get; set; }
        internal bool DiscardUntilLineFeed { get; set; }
        internal long? StartEdge { get; set; }
    }

    private bool Fresh(long? timestamp, long now) => timestamp is { } value && _time.GetElapsedTime(value, now) < StaleAfter;

    private void Accept(ReadOnlySpan<char> sentence, long? startEdge)
    {
        if (sentence.Length < 10 || sentence[0] != '$' || sentence[^3] != '*' ||
            !byte.TryParse(sentence[^2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var checksum))
        {
            return;
        }
        var actual = 0;
        foreach (var character in sentence[1..^3])
        {
            actual ^= character;
        }
        if (actual != checksum)
        {
            return;
        }
        var fields = sentence[1..^3].ToString().Split(',');
        if (fields[0] is "GPRMC" or "GNRMC" && fields.Length >= 10 &&
            fields[2] is "A" or "V" && TryTime(fields[1], out var time) && TryDate(fields[9], out var date))
        {
            var utc = new DateTimeOffset(date.ToDateTime(time), TimeSpan.Zero);
            _utc = utc;
            _lastSentence = _time.GetUtcNow();
            _rmcTimestamp = _time.GetTimestamp();
            // Host time is the clock under test and cannot supply the UTC era.
            _validFix = fields[2] == "A" && (fields.Length <= 12 || fields[12] is "A" or "D" or "P" or "R" or "F") &&
                utcWindow is not null && utc >= utcWindow.EarliestUtc && utc <= utcWindow.LatestUtc;
            _pairedEdge = _validFix && utc.Ticks % TimeSpan.TicksPerSecond == 0 &&
                _pendingEdge is { } edge && startEdge == edge && _time.GetElapsedTime(edge, _rmcTimestamp.Value) < TimeSpan.FromMilliseconds(800)
                ? _pendingEdge : null;
            _pairedWidth = _edgeWidth;
            _pairedHostUtc = _lastPps;
            _pendingEdge = null;
            _unavailable = false;
        }
        else if (fields[0] is "GPGGA" or "GNGGA" && fields.Length >= 9 && TryTime(fields[1], out _) &&
            int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var quality) && quality is >= 0 and <= 8 &&
            int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var satellites) && satellites is >= 0 and <= 99)
        {
            _quality = quality;
            _satellites = satellites;
            _ggaTimestamp = _time.GetTimestamp();
            _lastSentence = _time.GetUtcNow();
            _unavailable = false;
        }
    }

    private static bool TryTime(string text, out TimeOnly time)
        => TimeOnly.TryParseExact(text, ["HHmmss", "HHmmss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static bool TryDate(string text, out DateOnly date)
    {
        // Decode two-digit RMC years as 2000–2099. Fix validation applies the receiver deployment window.
        date = default;
        return text.Length == 6 && text.All(char.IsAsciiDigit) &&
            DateOnly.TryParseExact(text[..4] + "20" + text[4..], "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
