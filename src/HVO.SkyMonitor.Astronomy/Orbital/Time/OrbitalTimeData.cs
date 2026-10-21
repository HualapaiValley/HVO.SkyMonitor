using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>One evaluated time/EOP point. Polar motion is in arcseconds; UTC and UT1 remain split.</summary>
public readonly record struct OrbitalTimePoint(OrbitalJulianDate Utc, OrbitalJulianDate Ut1,
    double Dut1Seconds, double PolarMotionXArcseconds, double PolarMotionYArcseconds,
    int TaiMinusUtcSeconds, OrbitalEarthOrientationStatus Status);

/// <summary>
/// Materialized immutable inputs, parsed once outside the frame path. No filesystem or network access.
/// Bulletin B is final; Bulletin A P is predicted. Rapid-only I and blank rows remain unavailable gaps.
/// </summary>
public sealed class OrbitalTimeData
{
    public const string ConversionVersion = "teme-gmst82-ut1-pm-no-sp-nominal-rate-v1";
    public const string InterpolationVersion = "linear-dut1-polar-no-extrapolation-v1";
    private const int MaximumEopBytes = 8 * 1024 * 1024;
    private const int MaximumLeapBytes = 64 * 1024;
    private static readonly DateTimeOffset MjdEpoch = new(1858, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly EopRow[] _earthOrientation;
    private readonly LeapRow[] _leaps;

    private readonly record struct EopRow(DateTimeOffset Utc, double Dut1, double X, double Y, OrbitalEarthOrientationStatus Status);
    private readonly record struct LeapRow(DateTimeOffset Utc, int Offset);

    public string? EarthOrientationSha256 { get; }
    public OrbitalUtcInterval? EarthOrientationCoverage { get; }
    public OrbitalEarthOrientationStatus EarthOrientationStatus { get; }
    /// <summary>First to last Bulletin B final row. Rows inside are not guaranteed contiguous; queries still validate.</summary>
    public OrbitalUtcInterval? EarthOrientationFinalCoverage { get; }
    /// <summary>First to last Bulletin A predicted row, under the same caveat as the final span.</summary>
    public OrbitalUtcInterval? EarthOrientationPredictedCoverage { get; }
    public string LeapSecondsSha256 { get; }
    /// <summary>Inclusive supported ticks. The artifact's expiration instant itself is excluded.</summary>
    public OrbitalUtcInterval LeapTableCoverage { get; }
    public DateTimeOffset LeapTableExpiresUtc { get; }
    public bool NominalZeroEarthOrientation => _earthOrientation.Length == 0;

    private OrbitalTimeData(ReadOnlySpan<byte> earthOrientationBytes, ReadOnlySpan<byte> leapBytes, bool zero)
    {
        OrbitalContract.Require(leapBytes.Length is > 0 and <= MaximumLeapBytes &&
            (zero ? earthOrientationBytes.IsEmpty : earthOrientationBytes.Length is > 0 and <= MaximumEopBytes), "time-input-size");
        LeapSecondsSha256 = Convert.ToHexString(SHA256.HashData(leapBytes));
        (_leaps, LeapTableExpiresUtc) = ParseLeaps(leapBytes);
        LeapTableCoverage = new(_leaps[0].Utc, LeapTableExpiresUtc.AddTicks(-1));
        _earthOrientation = zero ? [] : ParseEop(earthOrientationBytes);
        if (zero)
        {
            EarthOrientationStatus = OrbitalEarthOrientationStatus.ApproximateZero;
        }
        else
        {
            EarthOrientationSha256 = Convert.ToHexString(SHA256.HashData(earthOrientationBytes));
            EarthOrientationCoverage = new(_earthOrientation[0].Utc, _earthOrientation[^1].Utc);
            EarthOrientationStatus = _earthOrientation.Select(r => r.Status).Distinct().Count() == 1
                ? _earthOrientation[0].Status : OrbitalEarthOrientationStatus.Mixed;
            EarthOrientationFinalCoverage = Span(OrbitalEarthOrientationStatus.Final);
            EarthOrientationPredictedCoverage = Span(OrbitalEarthOrientationStatus.Predicted);
        }
    }

    private OrbitalUtcInterval? Span(OrbitalEarthOrientationStatus status)
    {
        var rows = _earthOrientation.Where(row => row.Status == status).ToArray();
        return rows.Length == 0 ? null : new(rows[0].Utc, rows[^1].Utc);
    }

    /// <summary>Hashes actual unmodified bytes and parses bounded finals2000A and IERS/NTP leap inputs.</summary>
    public static OrbitalTimeData FromFinals2000A(ReadOnlySpan<byte> earthOrientationBytes, ReadOnlySpan<byte> leapBytes)
        => new(earthOrientationBytes, leapBytes, false);

    /// <summary>Explicit opt-in diagnostic approximation; validation restricts it to a configured rig.</summary>
    public static OrbitalTimeData CreateNominalZeroDiagnostic(ReadOnlySpan<byte> leapBytes) => new([], leapBytes, true);

    /// <summary>Verifies all resolved facts, conventions and continuous query coverage against actual input bytes.</summary>
    public void ValidateModel(OrbitalPredictionModelFacts model, OrbitalUtcInterval interval,
        OrbitalPredictionUse use, OrbitalGeometryBasis geometryBasis)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(interval);
        model.Validate(); interval.Validate();
        OrbitalContract.Require(Enum.IsDefined(use) && Enum.IsDefined(geometryBasis) &&
            model.TemeConversionVersion == ConversionVersion && model.EarthOrientationInterpolation == InterpolationVersion &&
            model.NominalZeroEarthOrientation == NominalZeroEarthOrientation &&
            string.Equals(model.EarthOrientationSha256, EarthOrientationSha256, StringComparison.OrdinalIgnoreCase) &&
            model.EarthOrientationCoverage == EarthOrientationCoverage && model.EarthOrientationStatus == EarthOrientationStatus &&
            string.Equals(model.LeapSecondsSha256, LeapSecondsSha256, StringComparison.OrdinalIgnoreCase) &&
            model.LeapTableCoverage == LeapTableCoverage, "resolved-time-model-mismatch");
        OrbitalContract.Require(!NominalZeroEarthOrientation || use == OrbitalPredictionUse.Diagnostic &&
            geometryBasis == OrbitalGeometryBasis.ConfiguredRig, "zero-eop-diagnostic-only");
        ValidateLeapInterval(interval.StartUtc, interval.EndUtc);
        _ = Evaluate(interval.StartUtc);
        _ = Evaluate(interval.EndUtc);
        if (!NominalZeroEarthOrientation)
        {
            for (var i = FindEop(interval.StartUtc); i < _earthOrientation.Length - 1 && _earthOrientation[i].Utc < interval.EndUtc; i++)
            {
                RequireDailyBracket(i);
            }
        }
    }

    /// <summary>
    /// Must also be called for every selected element epoch. A leap between epoch and query is unsupported
    /// even when both exposure endpoints are after the leap: SGP4 elapsed UTC would omit the inserted second.
    /// EOP coverage is only needed at query evaluation times, not at the element epoch.
    /// </summary>
    public void ValidatePropagationInterval(DateTimeOffset elementEpochUtc, OrbitalUtcInterval query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        OrbitalContract.Require(OrbitalContract.Utc(elementEpochUtc), "time-not-utc");
        ValidateLeapInterval(elementEpochUtc < query.StartUtc ? elementEpochUtc : query.StartUtc,
            elementEpochUtc > query.EndUtc ? elementEpochUtc : query.EndUtc);
    }

    /// <summary>Evaluates by binary search and linear interpolation, with no extrapolation or missing-day bridging.</summary>
    public OrbitalTimePoint Evaluate(DateTimeOffset utc)
    {
        OrbitalContract.Require(OrbitalContract.Utc(utc), "time-not-utc");
        var offset = LeapOffset(utc);
        var split = OrbitalJulianDate.FromUtc(utc);
        if (NominalZeroEarthOrientation) return new(split, split, 0, 0, 0, offset, OrbitalEarthOrientationStatus.ApproximateZero);
        var i = FindEop(utc);
        var row = _earthOrientation[i];
        if (utc == row.Utc) return new(split, split.AddSeconds(row.Dut1), row.Dut1, row.X, row.Y, offset, row.Status);
        RequireDailyBracket(i);
        var next = _earthOrientation[i + 1];
        // Interpolation across a leap would mix a discontinuous DUT1 step; reject even for a point query.
        ValidateLeapInterval(row.Utc, next.Utc);
        var t = (double)(utc.Ticks - row.Utc.Ticks) / (next.Utc.Ticks - row.Utc.Ticks);
        var dut1 = row.Dut1 + t * (next.Dut1 - row.Dut1);
        return new(split, split.AddSeconds(dut1), dut1, row.X + t * (next.X - row.X), row.Y + t * (next.Y - row.Y),
            offset, row.Status == next.Status ? row.Status : OrbitalEarthOrientationStatus.Mixed);
    }

    private int FindEop(DateTimeOffset utc)
    {
        OrbitalContract.Require(utc >= _earthOrientation[0].Utc && utc <= _earthOrientation[^1].Utc, "eop-outside-coverage");
        var lo = 0; var hi = _earthOrientation.Length - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (_earthOrientation[mid].Utc <= utc) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private void RequireDailyBracket(int i) => OrbitalContract.Require(i + 1 < _earthOrientation.Length &&
        _earthOrientation[i + 1].Utc - _earthOrientation[i].Utc == TimeSpan.FromDays(1), "eop-missing-daily-bracket");

    private int LeapOffset(DateTimeOffset utc)
    {
        OrbitalContract.Require(utc >= LeapTableCoverage.StartUtc && utc < LeapTableExpiresUtc, "leap-outside-coverage");
        var lo = 0; var hi = _leaps.Length - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (_leaps[mid].Utc <= utc) lo = mid; else hi = mid - 1;
        }
        return _leaps[lo].Offset;
    }

    private void ValidateLeapInterval(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var first = LeapOffset(startUtc);
        var last = LeapOffset(endUtc);
        OrbitalContract.Require(first == last, "unsupported-leap-crossing");
        foreach (var row in _leaps)
            OrbitalContract.Require(row.Utc <= startUtc || row.Utc > endUtc, "unsupported-leap-crossing");
    }

    private static (LeapRow[] Rows, DateTimeOffset Expires) ParseLeaps(ReadOnlySpan<byte> bytes)
    {
        var rows = new List<LeapRow>();
        DateTimeOffset? expires = null;
        foreach (var rawLine in Lines(bytes))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#@", StringComparison.Ordinal))
            {
                OrbitalContract.Require(expires is null, "duplicate-leap-expiry");
                expires = ParseNtp(line[2..].Split('#')[0].Trim());
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                OrbitalContract.Require(fields.Length == 2 && rows.Count < 128, "invalid-leap-row");
                var epoch = ParseNtp(fields[0]);
                OrbitalContract.Require(int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) &&
                    offset is >= 0 and <= 128 && epoch.TimeOfDay == TimeSpan.Zero &&
                    (rows.Count == 0 || epoch > rows[^1].Utc && Math.Abs(offset - rows[^1].Offset) == 1), "invalid-leap-row");
                rows.Add(new(epoch, offset));
            }
        }
        OrbitalContract.Require(rows.Count > 0 && expires is not null && expires > rows[^1].Utc, "invalid-leap-coverage");
        return (rows.ToArray(), expires!.Value);
    }

    private static DateTimeOffset ParseNtp(string text)
    {
        OrbitalContract.Require(long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 0 and <= 100_000_000_000, "invalid-ntp-time");
        return NtpEpoch.AddSeconds(seconds);
    }

    private static EopRow[] ParseEop(ReadOnlySpan<byte> bytes)
    {
        var rows = new List<EopRow>();
        DateTimeOffset? previous = null;
        foreach (var line in Lines(bytes))
        {
            OrbitalContract.Require(line.Length >= 68 && rows.Count < 100_000, "invalid-eop-row");
            var mjd = Number(line, 7, 8);
            OrbitalContract.Require(mjd == Math.Floor(mjd) && mjd is >= 0 and < 1_000_000, "invalid-eop-epoch");
            var utc = MjdEpoch.AddDays(mjd);
            OrbitalContract.Require(previous is null || utc > previous, "unordered-eop-epochs");
            previous = utc;
            double x, y, dut1;
            OrbitalEarthOrientationStatus status;
            if (line.Length >= 165 && !string.IsNullOrWhiteSpace(line[134..165]))
            {
                x = Number(line, 134, 10); y = Number(line, 144, 10); dut1 = Number(line, 154, 11);
                status = OrbitalEarthOrientationStatus.Final;
            }
            else if (line[16] == 'P' && line[57] == 'P')
            {
                x = Number(line, 18, 9); y = Number(line, 37, 9); dut1 = Number(line, 58, 10);
                status = OrbitalEarthOrientationStatus.Predicted;
            }
            else
            {
                // Rapid-only observed I is not claimed to be final. Its absent support remains a gap.
                OrbitalContract.Require(line[16] is 'I' or 'P' or ' ' && line[57] is 'I' or 'P' or ' ', "invalid-eop-status");
                continue;
            }
            OrbitalContract.Require(Math.Abs(dut1) <= 1 && Math.Abs(x) <= 10 && Math.Abs(y) <= 10, "invalid-eop-value");
            rows.Add(new(utc, dut1, x, y, status));
        }
        OrbitalContract.Require(rows.Count >= 2, "insufficient-eop-rows");
        return rows.ToArray();
    }

    private static double Number(string line, int start, int length)
    {
        OrbitalContract.Require(double.TryParse(line.AsSpan(start, length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value), "invalid-eop-number");
        return value;
    }

    private static string[] Lines(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) OrbitalContract.Require(b is 9 or 10 or 13 or >= 32 and <= 126, "time-input-not-ascii");
        return Encoding.ASCII.GetString(bytes).Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
