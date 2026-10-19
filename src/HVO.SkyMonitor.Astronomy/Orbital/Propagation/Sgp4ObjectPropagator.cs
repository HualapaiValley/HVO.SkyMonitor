using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Astronomy.Orbital.Propagation;

internal enum Sgp4FailureKind
{
    None,
    InvalidRecord,
    Initialization,
    Propagation,
    NonFiniteTime,
    NonFiniteCoefficients,
    NonFiniteState,
    WorkLimit
}

/// <summary>Core codes are retained verbatim: 1, 2, 3, 4 or 6; zero means an adapter failure.</summary>
internal readonly record struct Sgp4Failure(Sgp4FailureKind Kind, int CoreErrorCode);

/// <summary>
/// Pure WGS72 / AFSPC-a propagation of one normalized record. The initialized template is never
/// propagated after construction; every evaluation clones its mutable near/deep-space state.
/// UTC tick differences assume the caller has rejected unsupported leap-crossing intervals.
/// </summary>
internal sealed class Sgp4ObjectPropagator
{
    private const double RadiansPerRevolution = 2 * Math.PI;
    private const double MinutesPerDay = 1440;
    private readonly ElsetRec template;
    private readonly DateTimeOffset epochUtc;

    private Sgp4ObjectPropagator(ElsetRec initialized, DateTimeOffset epoch)
    {
        template = initialized;
        epochUtc = epoch;
    }

    internal string CatalogNumber => template.satid;
    internal double EpochJulianDay => template.jdsatepoch;
    internal double EpochJulianDayFraction => template.jdsatepochF;
    internal double MeanMotionDotRadiansPerMinuteSquared => template.ndot;
    internal double MeanMotionDdotRadiansPerMinuteCubed => template.nddot;

    internal static bool TryCreate(OrbitalRecordV1 record,
        [NotNullWhen(true)] out Sgp4ObjectPropagator? propagator, out Sgp4Failure failure,
        Func<bool>? beforeCoreEvaluation = null)
    {
        propagator = null;
        failure = default;
        try
        {
            OrbitalContract.ValidateRecord(record);
        }
        catch (ArgumentException)
        {
            failure = new(Sgp4FailureKind.InvalidRecord, 0);
            return false;
        }

        var e = record.Elements;
        var utc = record.EpochUtc.UtcDateTime;
        // Gregorian day counts are exact integers throughout DateTime's range. Keep midnight (.5)
        // separate from the subday ticks; neither Unix milliseconds nor a rounded large JD is used.
        var initialized = new ElsetRec
        {
            whichconst = SGP4.wgs72,
            satid = record.ObjectIdentity.CatalogNumber,
            jdsatepoch = utc.Date.Ticks / TimeSpan.TicksPerDay + 1721425.5,
            jdsatepochF = utc.TimeOfDay.Ticks / (double)TimeSpan.TicksPerDay,
            no_kozai = e.MeanMotionRevolutionsPerDay * RadiansPerRevolution / MinutesPerDay,
            ndot = e.MeanMotionDot * RadiansPerRevolution / (MinutesPerDay * MinutesPerDay),
            nddot = e.MeanMotionDdot * RadiansPerRevolution / (MinutesPerDay * MinutesPerDay * MinutesPerDay),
            ecco = e.Eccentricity,
            inclo = e.InclinationDegrees * Math.PI / 180,
            nodeo = e.AscendingNodeDegrees * Math.PI / 180,
            argpo = e.ArgumentOfPericenterDegrees * Math.PI / 180,
            mo = e.MeanAnomalyDegrees * Math.PI / 180,
            bstar = e.BStarInverseEarthRadii
        };

        if (!double.IsFinite(initialized.no_kozai) || !double.IsFinite(initialized.ndot) ||
            !double.IsFinite(initialized.nddot))
        {
            failure = new(Sgp4FailureKind.NonFiniteCoefficients, 0);
            return false;
        }

        // The pinned initializer always returns true. Its epoch sgp4 call can still fail.
        if (!Reserve(beforeCoreEvaluation, out failure)) return false;
        if (!SGP4.sgp4init('a', initialized) || initialized.error != 0)
        {
            failure = new(Sgp4FailureKind.Initialization, initialized.error);
            return false;
        }

        // Initialization can overflow for otherwise finite extreme elements without a core code.
        // Validate an actual epoch state before publishing the private template.
        if (!TryEvaluate(initialized.Copy(), 0, out _, out failure, beforeCoreEvaluation)) return false;
        propagator = new(initialized, record.EpochUtc);
        return true;
    }

    internal bool TryPropagate(DateTimeOffset utc, [NotNullWhen(true)] out OrbitalTemeState? state,
        out Sgp4Failure failure, Func<bool>? beforeCoreEvaluation = null)
        => TryPropagateMinutes((utc.UtcTicks - epochUtc.UtcTicks) / (double)TimeSpan.TicksPerMinute,
            out state, out failure, beforeCoreEvaluation);

    internal bool TryPropagateMinutes(double minutesSinceEpoch,
        [NotNullWhen(true)] out OrbitalTemeState? state, out Sgp4Failure failure,
        Func<bool>? beforeCoreEvaluation = null)
    {
        if (!double.IsFinite(minutesSinceEpoch))
        {
            state = null;
            failure = new(Sgp4FailureKind.NonFiniteTime, 0);
            return false;
        }
        return TryEvaluate(template.Copy(), minutesSinceEpoch, out state, out failure, beforeCoreEvaluation);
    }

    private static bool TryEvaluate(ElsetRec elements, double minutesSinceEpoch,
        [NotNullWhen(true)] out OrbitalTemeState? state, out Sgp4Failure failure, Func<bool>? beforeCoreEvaluation)
    {
        state = null;
        failure = default;
        if (!Reserve(beforeCoreEvaluation, out failure)) return false;
        var position = new double[3];
        var velocity = new double[3];
        if (!SGP4.sgp4(elements, minutesSinceEpoch, position, velocity) || elements.error != 0)
        {
            // Error 6 can leave finite vectors in the buffers. Every failed state is discarded.
            failure = new(Sgp4FailureKind.Propagation, elements.error);
            return false;
        }
        if (!position.All(double.IsFinite) || !velocity.All(double.IsFinite))
        {
            failure = new(Sgp4FailureKind.NonFiniteState, 0);
            return false;
        }
        state = new(new(position[0], position[1], position[2]), new(velocity[0], velocity[1], velocity[2]));
        return true;
    }

    private static bool Reserve(Func<bool>? beforeCoreEvaluation, out Sgp4Failure failure)
    {
        if (beforeCoreEvaluation is null || beforeCoreEvaluation())
        {
            failure = default;
            return true;
        }
        failure = new(Sgp4FailureKind.WorkLimit, 0);
        return false;
    }
}
