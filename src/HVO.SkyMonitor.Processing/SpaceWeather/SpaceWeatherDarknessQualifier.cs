using System.Collections.Immutable;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Adds conservative solar restrictions without changing the relevance category.</summary>
public static class SpaceWeatherDarknessQualifier
{
    /// <summary>Collects daylight and twilight boundaries using bounded 24-hour queries. No events means unknown.</summary>
    public static DarknessQualifier Qualify(SwpcNoticeWindow window, SpaceWeatherSiteLocation site, ISolarEventCalculator sun)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(sun);
        if (window.FromUtc.Offset != TimeSpan.Zero || window.UntilUtc.Offset != TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        // This is a bounded site-context query, not a history scan. Long linked-group histories
        // require the caller to pass a relevant subwindow, rather than allocate per historic day.
        if (window.UntilUtc - window.FromUtc > TimeSpan.FromHours(SwpcLimits.ActiveCapHours)) throw new ArgumentOutOfRangeException(nameof(window));
        if (!double.IsFinite(site.LatitudeDeg) || site.LatitudeDeg is < -90 or > 90
            || !double.IsFinite(site.LongitudeDeg) || site.LongitudeDeg is < -180 or > 180 || !double.IsFinite(site.ElevationMeters))
            throw new ArgumentOutOfRangeException(nameof(site));
        var boundaries = ImmutableArray.CreateBuilder<SolarEventResult>();
        for (var from = window.FromUtc; from < window.UntilUtc;)
        {
            var until = window.UntilUtc - from > TimeSpan.FromHours(24) ? from.AddHours(24) : window.UntilUtc;
            foreach (var kind in new[] { SolarEventKind.Sunrise, SolarEventKind.Sunset, SolarEventKind.AstronomicalDawn, SolarEventKind.AstronomicalDusk })
            {
                var result = sun.Find(kind, from, until, site.LatitudeDeg, site.LongitudeDeg, site.ElevationMeters);
                if (result.Utc is { } at && at >= from && at < until) boundaries.Add(result);
            }
            from = until;
        }
        var ordered = boundaries.OrderBy(b => b.Utc).ThenBy(b => b.Kind).ToImmutableArray();
        var restrictions = ImmutableArray.CreateBuilder<SwpcSolarRestriction>();
        Restrict(SolarEventKind.Sunrise, SolarEventKind.Sunset, "daylight");
        Restrict(SolarEventKind.AstronomicalDawn, SolarEventKind.AstronomicalDusk, "daylight-or-twilight");
        return new(ordered, [.. restrictions.OrderBy(r => r.FromUtc).ThenBy(r => r.Reason, StringComparer.Ordinal)]);

        void Restrict(SolarEventKind opening, SolarEventKind closing, string reason)
        {
            var events = ordered.Where(b => b.Kind == opening || b.Kind == closing).ToArray();
            // If the first boundary ends a restriction, it was already in force at the
            // window start. Without either boundary its initial state is unknown.
            DateTimeOffset? start = events.Length > 0 && events[0].Kind == closing ? window.FromUtc : null;
            foreach (var boundary in events)
            {
                if (boundary.Kind == opening) start ??= boundary.Utc;
                else if (start is { } from && boundary.Utc is { } to)
                {
                    if (to > from) restrictions.Add(new(from, to, reason));
                    start = null;
                }
            }
            if (start is { } remaining && remaining < window.UntilUtc) restrictions.Add(new(remaining, window.UntilUtc, reason));
        }
    }
}
