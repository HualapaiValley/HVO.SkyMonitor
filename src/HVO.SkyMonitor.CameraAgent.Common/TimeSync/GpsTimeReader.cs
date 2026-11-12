using System.Globalization;

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
/// Fake-only GPS diagnostics. ArrivalOffset is NMEA UTC minus sentence receipt time, including unknown serial
/// latency; it is not a precision clock measurement. PPS reports availability only, with no UTC association.
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
    /// <summary>No GPS diagnostic in this preparation slice can establish a known-good host clock.</summary>
    public bool CanVerifyClock { get; }
}

/// <summary>
/// Bounded, cancellable in-process NMEA reader for scripted streams; never registered in production or opens a path.
/// Supports checksummed RMC (dated UTC and validity) and GGA (fix quality and satellite count), GP/GN talkers.
/// Known limits requiring Roy's decision: PPS source and edge; which NMEA UTC second an edge marks;
/// acceptable measurement uncertainty. Simulated PPS edges intentionally have no implicit UTC-second mapping.
/// No settings persistence, clock setting, background polling, or production health authority is provided.
/// Callers own streams, which must honor ReadAsync cancellation; reads are serialized and retain no partial line
/// across rounds. Status/edge observation is safe during a read. Freshness uses monotonic time, not wall-clock age.
/// </summary>
public sealed class GpsTimeReader(TimeProvider timeProvider) : IDisposable
{
    public const int MaximumSentenceLength = 82;
    public const int MaximumBytesPerRead = 4096;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly Lock _gate = new();
    private DateTimeOffset? _utc, _lastSentence, _lastPps;
    private TimeSpan? _arrivalOffset;
    private long? _rmcTimestamp, _ggaTimestamp, _ppsTimestamp;
    private int? _quality, _satellites;
    private bool _validFix, _unavailable;

    /// <summary>
    /// Consumes at most 4096 bytes within two seconds, including waiting for another read. EOF ends this scripted
    /// round; IOException marks the source unavailable and invalidates prior evidence. Caller cancellation propagates.
    /// An internally timed-out round retains only completed valid sentences, subject to normal expiry.
    /// </summary>
    public async Task ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var timeout = new CancellationTokenSource(ReadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var entered = false;
        try
        {
            await _reading.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            var buffer = new byte[1];
            var sentence = new char[MaximumSentenceLength];
            var length = 0;
            var discard = false;
            for (var count = 0; count < MaximumBytesPerRead; count++)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (await source.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false) == 0)
                {
                    break;
                }
                var value = (char)buffer[0];
                if (value == '\n')
                {
                    if (!discard)
                    {
                        Accept(sentence.AsSpan(0, length).TrimEnd('\r'));
                    }
                    length = 0;
                    discard = false;
                }
                else if (length == sentence.Length || value > 127 || (value < 32 && value != '\r'))
                {
                    discard = true;
                }
                else if (!discard)
                {
                    sentence[length++] = value;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            lock (_gate)
            {
                _unavailable = true;
                _utc = _lastSentence = _lastPps = null;
                _rmcTimestamp = _ggaTimestamp = _ppsTimestamp = null;
                _arrivalOffset = null;
                _quality = _satellites = null;
                _validFix = false;
            }
        }
        finally
        {
            if (entered)
            {
                _reading.Release();
            }
        }
    }

    /// <summary>Records receipt of a simulated edge only. It never invents an edge's UTC second or offset.</summary>
    public void ObserveSimulatedPps()
    {
        lock (_gate)
        {
            _lastPps = _time.GetUtcNow();
            _ppsTimestamp = _time.GetTimestamp();
        }
    }

    public GpsTimeStatus GetStatus()
    {
        lock (_gate)
        {
            var fresh = Fresh(_rmcTimestamp);
            var state = _unavailable ? GpsSignalState.Unavailable
                : _rmcTimestamp is null ? GpsSignalState.Missing
                : !fresh ? GpsSignalState.Stale
                : !_validFix || (Fresh(_ggaTimestamp) && _quality == 0) ? GpsSignalState.NoFix : GpsSignalState.Available;
            return new GpsTimeStatus(state, _utc, _lastSentence,
                state == GpsSignalState.Available ? _arrivalOffset : null,
                Fresh(_ggaTimestamp) ? _quality : null, Fresh(_ggaTimestamp) ? _satellites : null,
                _ppsTimestamp is null ? GpsSignalState.Missing
                    : Fresh(_ppsTimestamp) ? GpsSignalState.Available : GpsSignalState.Stale, _lastPps);
        }
    }

    /// <summary>Attaches diagnostics without replacing SNTP selection, kernel evidence, or clock assessment.</summary>
    public ClockSyncSnapshot ReportInto(ClockSyncSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { Gps = GetStatus() };
    }

    /// <summary>Dispose after all reads and observations have completed; the caller retains stream ownership.</summary>
    public void Dispose() => _reading.Dispose();

    private bool Fresh(long? timestamp) => timestamp is { } value && _time.GetElapsedTime(value) < StaleAfter;

    private void Accept(ReadOnlySpan<char> sentence)
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
            lock (_gate)
            {
                _utc = utc;
                _lastSentence = _time.GetUtcNow();
                _rmcTimestamp = _time.GetTimestamp();
                _validFix = fields[2] == "A";
                _arrivalOffset = _validFix ? utc - _lastSentence : null;
                _unavailable = false;
            }
        }
        else if (fields[0] is "GPGGA" or "GNGGA" && fields.Length >= 9 && TryTime(fields[1], out _) &&
            int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var quality) && quality is >= 0 and <= 8 &&
            int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var satellites) && satellites is >= 0 and <= 99)
        {
            lock (_gate)
            {
                _quality = quality;
                _satellites = satellites;
                _ggaTimestamp = _time.GetTimestamp();
                _lastSentence = _time.GetUtcNow();
                _unavailable = false;
            }
        }
    }

    private static bool TryTime(string text, out TimeOnly time)
        => TimeOnly.TryParseExact(text, ["HHmmss", "HHmmss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static bool TryDate(string text, out DateOnly date)
    {
        // Explicit 2000..2099 fake-workload window; no culture/calendar-dependent two-digit-year pivot.
        date = default;
        return text.Length == 6 && text.All(char.IsAsciiDigit) &&
            DateOnly.TryParseExact(text[..4] + "20" + text[4..], "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
