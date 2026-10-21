using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Builds one <see cref="SpaceWeatherUiProjection"/> from a paired status and store read in a single pass over the
/// retained groups. It is pure apart from the darkness cache, never writes, and never emits attention events.
/// </summary>
internal static class SpaceWeatherUiProjectionBuilder
{
    internal const string ModelWording =
        "centred-dipole approximation (pole 80.8° N, 72.6° W); model version unspecified";
    internal const string EstimateSuffix = "HVO SkyMonitor estimate, not a NOAA statement.";
    internal const string NoSiteDarkness = "darkness not assessed: no site location";
    internal const string LongWindowDarkness = "darkness not assessed for windows longer than 96 hours";
    internal const string UndeterminedDarkness = "site daylight/twilight could not be determined for this window";
    internal const string NoRestrictionDarkness = "no site daylight or twilight restriction found for this window";

    /// <summary>The attention evaluator's rank rule: K4 or no level is 0, otherwise the scale digit.</summary>
    internal static int Rank(string? level) => level is null or "K4" ? 0 : level[1] - '0';

    internal static SpaceWeatherUiProjection Build(
        SpaceWeatherOperations.Status status,
        SwpcStoreView view,
        DeploymentLocationSnapshot? location,
        DateTimeOffset now,
        bool inconsistent,
        SpaceWeatherDarknessCache darkness,
        ISolarEventCalculator sun)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(darkness);
        ArgumentNullException.ThrowIfNull(sun);
        var context = SiteContext.Create(location);
        var alertsFresh = view.Products.TryGetValue(SwpcProduct.Alerts, out var alerts) && alerts.Freshness == SwpcFreshness.Fresh;
        var retrieved = new Dictionary<(string Identity, long Sequence), DateTimeOffset>();
        foreach (var stored in view.Notices.Versions)
        {
            retrieved.TryAdd((stored.Version.Message.Identity, stored.Version.IngestSequence), stored.RetrievedUtc);
        }

        IReadOnlyList<SwpcNoticeGroup> groups = view.Notices.Groups;
        var current = new List<Entry>();
        var recent = new List<Entry>();
        var history = new List<Entry>();
        int active = 0, pending = 0, reach = 0, review = 0;
        foreach (var group in groups)
        {
            var window = SwpcNoticeLifecycle.Evaluate(group, groups, now);
            var entry = new Entry(group, window, (group.LatestNonCancel ?? group.Latest).IssueMinuteUtc);
            switch (window.State)
            {
                case SwpcNoticeState.Active or SwpcNoticeState.Pending:
                    current.Add(entry);
                    if (window.State == SwpcNoticeState.Active) active++; else pending++;
                    var ack = SwpcGroupBuilder.Acknowledgement(group, view.Notices.Acknowledgements);
                    var escalation = Escalation(group, ack, retrieved);
                    var category = Relevance(group, context).Relevance.Category;
                    if (category is LocalRelevanceCategory.InImpactArea or LocalRelevanceCategory.PossibleNearHorizon &&
                        escalation is not SpaceWeatherEscalation.NotEscalated)
                    {
                        reach++;
                    }
                    else if (escalation is SpaceWeatherEscalation.NotAcknowledged or SpaceWeatherEscalation.Escalated)
                    {
                        review++;
                    }
                    break;
                case SwpcNoticeState.Recent:
                    recent.Add(entry);
                    break;
                default:
                    history.Add(entry);
                    break;
            }
        }

        var currentRows = current
            .OrderBy(static e => e.Window.State == SwpcNoticeState.Active ? 0 : 1)
            .ThenByDescending(static e => e.Issued)
            .ThenBy(static e => e.Group.Identity, StringComparer.Ordinal)
            .Take(SpaceWeatherUiBounds.CurrentRows)
            .Select(e => Row(e, view, context, retrieved, alertsFresh, darkness, sun))
            .ToArray();
        var recentRows = ByUntil(recent).Take(SpaceWeatherUiBounds.RecentRows)
            .Select(e => Row(e, view, context, retrieved, alertsFresh, darkness, sun)).ToArray();
        var historyRows = ByUntil(history).Take(SpaceWeatherUiBounds.HistoryRows)
            .Select(e => Row(e, view, context, retrieved, alertsFresh, darkness, sun)).ToArray();

        var configured = status.Products.Where(static p => p.Configured).Select(static p => p.Product).ToHashSet(StringComparer.Ordinal);
        var sources = status.Products.Select(p => Source(p, view, context.Time, configured)).ToArray();
        var providerHalt = status.ProviderHalt is { } halt ? Halt("provider", null, halt, context.Time, canResume: true) : null;
        return new SpaceWeatherUiProjection
        {
            State = SpaceWeatherUiState.Ready,
            ObservedUtc = now,
            Observed = SpaceWeatherUiTime.Create(now, context.Time),
            Enabled = status.Enabled,
            DefinitionExists = status.DefinitionExists,
            DefinitionEnabled = status.DefinitionEnabled,
            Inconsistent = inconsistent,
            Credit = status.Credit,
            AlertsFreshness = status.Products.FirstOrDefault(static p => p.Product == SwpcProduct.Alerts.Token())?.Freshness,
            NoFirstSuccess = sources.Any(static s => s.Configured) && sources.Where(static s => s.Configured).All(static s => s.LastSuccess is null),
            Sources = sources,
            ProviderHalt = providerHalt,
            ProductHalts = sources.Where(static s => s.Halt is not null).Select(static s => s.Halt!).ToArray(),
            FormatDriftProducts = sources.Where(static s => s.FormatDrift).Select(static s => s.Product).ToArray(),
            RecordDriftProducts = sources.Where(static s => s.Configured && s.RecordDrift is not null).Select(static s => s.Product).ToArray(),
            Site = context.Site,
            Current = currentRows,
            CurrentTotal = current.Count,
            ActiveCount = active,
            PendingCount = pending,
            Recent = recentRows,
            RecentTotal = recent.Count,
            History = historyRows,
            HistoryTotal = history.Count,
            ReachCount = reach,
            ReviewCount = review
        };
    }

    /// <summary>A projection without data: no notice, count or activity claim, only the feature state and site.</summary>
    internal static SpaceWeatherUiProjection Unavailable(
        SpaceWeatherUiState state,
        bool enabled,
        DeploymentLocationSnapshot? location,
        DateTimeOffset now,
        SpaceWeatherOperations.Status? status = null)
    {
        var context = SiteContext.Create(location);
        return new SpaceWeatherUiProjection
        {
            State = state,
            ObservedUtc = now,
            Observed = SpaceWeatherUiTime.Create(now, context.Time),
            Enabled = status?.Enabled ?? enabled,
            DefinitionExists = status?.DefinitionExists ?? false,
            DefinitionEnabled = status?.DefinitionEnabled ?? false,
            Credit = status?.Credit ?? SpaceWeatherOperations.Credit,
            Site = context.Site
        };
    }

    /// <summary>
    /// Read-derived escalation: the baseline is the highest rank among non-cancel versions retrieved strictly before
    /// the acknowledgement. A version that cannot be joined to its retrieval time, a version retrieved at the
    /// acknowledgement instant, or no version before it leaves the answer Unknown.
    /// </summary>
    internal static SpaceWeatherEscalation Escalation(
        SwpcNoticeGroup group,
        SwpcAcknowledgement? acknowledgement,
        IReadOnlyDictionary<(string Identity, long Sequence), DateTimeOffset> retrieved)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(retrieved);
        if (acknowledgement is null)
        {
            return SpaceWeatherEscalation.NotAcknowledged;
        }
        int? baseline = null;
        foreach (var version in group.Versions)
        {
            if (!retrieved.TryGetValue((version.Message.Identity, version.IngestSequence), out var at))
            {
                return SpaceWeatherEscalation.Unknown;
            }
            if (version.Message.Header.Modifier == "CANCEL")
            {
                continue;
            }
            if (at == acknowledgement.AtUtc)
            {
                return SpaceWeatherEscalation.Unknown;
            }
            if (at < acknowledgement.AtUtc)
            {
                var rank = Rank(version.Message.Header.Level);
                baseline = baseline is { } prior ? Math.Max(prior, rank) : rank;
            }
        }
        if (baseline is null)
        {
            return SpaceWeatherEscalation.Unknown;
        }
        return Rank(group.Level) > baseline ? SpaceWeatherEscalation.Escalated : SpaceWeatherEscalation.NotEscalated;
    }

    /// <summary>The distinct operator label of a family and source code; a code without evidence names itself.</summary>
    internal static string Label(SpaceWeatherFamily family, string code) => family switch
    {
        SpaceWeatherFamily.Watch => "Geomagnetic storm watch",
        SpaceWeatherFamily.KAlert => "Geomagnetic K-index alert",
        SpaceWeatherFamily.Warning when IsKIndexWarning(code) => "Geomagnetic K-index warning",
        SpaceWeatherFamily.Warning when code == "WARSUD" => "Sudden impulse warning",
        SpaceWeatherFamily.Warning when code.StartsWith("WARPX", StringComparison.Ordinal) => "Proton flux warning",
        SpaceWeatherFamily.Warning => $"Space weather warning ({code})",
        SpaceWeatherFamily.XrayAlert => "X-ray flux alert (radio blackout)",
        SpaceWeatherFamily.RadioSweep => "Solar radio sweep alert",
        SpaceWeatherFamily.FluxAlert when code.StartsWith("ALTEF", StringComparison.Ordinal) => "Electron flux alert",
        SpaceWeatherFamily.FluxAlert when code.StartsWith("ALTPX", StringComparison.Ordinal) ||
            code.StartsWith("ALTPC", StringComparison.Ordinal) => "Proton flux alert",
        SpaceWeatherFamily.FluxAlert => $"Space weather flux alert ({code})",
        SpaceWeatherFamily.Summary => $"Event summary ({code})",
        _ => $"Unclassified SWPC message ({code})"
    };

    /// <summary>Source activity as SWPC stated it, never a site claim.</summary>
    internal static string GlobalLine(SpaceWeatherFamily family, string? level)
    {
        var shortFamily = family switch
        {
            SpaceWeatherFamily.Watch => "watch",
            SpaceWeatherFamily.Warning => "warning",
            SpaceWeatherFamily.KAlert => "K-index alert",
            SpaceWeatherFamily.XrayAlert => "X-ray alert",
            SpaceWeatherFamily.RadioSweep => "radio sweep alert",
            SpaceWeatherFamily.FluxAlert => "flux alert",
            SpaceWeatherFamily.Summary => "event summary",
            _ => "message"
        };
        return level is null ? $"SWPC: {shortFamily}, level not stated" : $"SWPC: {level} {shortFamily}";
    }

    internal static string CategoryText(LocalRelevanceCategory category) => category switch
    {
        LocalRelevanceCategory.InImpactArea => "within the stated impact latitude",
        LocalRelevanceCategory.PossibleNearHorizon => "possibly near the poleward horizon",
        LocalRelevanceCategory.Unlikely => "unlikely at this latitude",
        LocalRelevanceCategory.NotExpected => "not expected at this latitude",
        LocalRelevanceCategory.NoLocalObservingImpact => "no local optical observing impact",
        _ => "unknown"
    };

    private static IEnumerable<Entry> ByUntil(List<Entry> entries)
        => entries.OrderByDescending(static e => e.Window.UntilUtc).ThenBy(static e => e.Group.Identity, StringComparer.Ordinal);

    private static bool IsKIndexWarning(string code)
        => code.Length == 6 && code.StartsWith("WARK0", StringComparison.Ordinal) && code[5] is >= '4' and <= '9';

    private static SpaceWeatherUiNotice Row(
        Entry entry,
        SwpcStoreView view,
        SiteContext context,
        IReadOnlyDictionary<(string Identity, long Sequence), DateTimeOffset> retrieved,
        bool alertsFresh,
        SpaceWeatherDarknessCache darkness,
        ISolarEventCalculator sun)
    {
        var (group, window, issued) = entry;
        var latest = group.Latest;
        var source = group.LatestNonCancel ?? latest;
        var ack = SwpcGroupBuilder.Acknowledgement(group, view.Notices.Acknowledgements);
        var (relevance, siteLine) = Relevance(group, context);
        var isCurrent = window.State is SwpcNoticeState.Pending or SwpcNoticeState.Active;
        return new SpaceWeatherUiNotice
        {
            Group = group.Identity,
            FamilyToken = group.Family.Token(),
            Label = Label(group.Family, source.Code),
            Level = group.Level,
            State = window.State,
            Issued = SpaceWeatherUiTime.Create(issued, context.Time),
            LastUpdate = latest.IssueMinuteUtc != issued ? SpaceWeatherUiTime.Create(latest.IssueMinuteUtc, context.Time) : null,
            LastUpdateIsCancellation = latest.IssueMinuteUtc != issued && latest.Header.Modifier == "CANCEL",
            From = SpaceWeatherUiTime.Create(window.FromUtc, context.Time),
            Until = SpaceWeatherUiTime.Create(window.UntilUtc, context.Time),
            EndReason = window.EndReason?.Token(),
            Corrected = latest.Header.Corrected,
            Orphan = group.Orphan,
            VersionCount = group.Versions.Length,
            AcknowledgedAt = ack is null ? null : SpaceWeatherUiTime.Create(ack.AtUtc, context.Time),
            Escalation = Escalation(group, ack, retrieved),
            MayBeOutOfDate = isCurrent && !alertsFresh,
            GlobalLine = GlobalLine(group.Family, group.Level),
            Relevance = relevance.Category,
            SiteLine = siteLine,
            Darkness = isCurrent ? Darkness(group, window, context, darkness, sun) : []
        };
    }

    private static (LocalRelevance Relevance, string SiteLine) Relevance(SwpcNoticeGroup group, SiteContext context)
    {
        var source = group.LatestNonCancel ?? group.Latest;
        LocalRelevance relevance;
        try
        {
            relevance = SpaceWeatherRelevance.Categorize(group.Family, source.Code, group.Level,
                source.Header.StatedLatitude, context.Site.GeomagneticLatitude);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (new LocalRelevance(LocalRelevanceCategory.Unknown, null, null, context.Site.GeomagneticLatitude, null, null),
                Site("unknown (the stated latitude is out of range)"));
        }
        switch (relevance.Category)
        {
            case LocalRelevanceCategory.NoLocalObservingImpact:
                return (relevance, Site(CategoryText(relevance.Category)));
            case LocalRelevanceCategory.Unknown:
                var reason = group.Family == SpaceWeatherFamily.Unclassified ? "unclassified message"
                    : context.Site.GeomagneticLatitude is null ? "site location not configured"
                    : "no level or latitude stated";
                return (relevance, Site($"unknown ({reason})"));
        }
        var basis = relevance.Basis == "stated" ? "stated" : $"typical for {group.Level}";
        return (relevance, Site(string.Create(CultureInfo.InvariantCulture,
            $"{CategoryText(relevance.Category)} (required geomagnetic latitude {Degrees(relevance.RequiredLatitudeDeg)}°, {basis}; site geomagnetic latitude {Degrees(relevance.SiteGeomagneticLatitudeDeg)}°; d = {Degrees(relevance.DifferenceDeg)}°; {ModelWording})")));

        static string Site(string text) => $"At this site: {text}. {EstimateSuffix}";
    }

    private static string Degrees(double? value)
        => value is { } degrees ? (degrees == 0 ? 0d : degrees).ToString("0.0", CultureInfo.InvariantCulture) : "?";

    private static string[] Darkness(
        SwpcNoticeGroup group,
        SwpcNoticeWindow window,
        SiteContext context,
        SpaceWeatherDarknessCache cache,
        ISolarEventCalculator sun)
    {
        if (context.Location is not { } site)
        {
            return [NoSiteDarkness];
        }
        var from = window.FromUtc.ToUniversalTime();
        var until = window.UntilUtc.ToUniversalTime();
        if (until - from > TimeSpan.FromHours(SwpcLimits.ActiveCapHours))
        {
            return [LongWindowDarkness];
        }
        var qualifier = cache.GetOrAdd(new(group.Identity, from, until, site.LatitudeDeg, site.LongitudeDeg, site.ElevationMeters), () =>
        {
            try
            {
                return SpaceWeatherDarknessQualifier.Qualify(window with { FromUtc = from, UntilUtc = until }, site, sun);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        });
        if (qualifier is null || qualifier.Boundaries.IsDefaultOrEmpty)
        {
            return [UndeterminedDarkness];
        }
        if (qualifier.Restrictions.IsDefaultOrEmpty)
        {
            return [NoRestrictionDarkness];
        }
        return qualifier.Restrictions.Select(r => Restriction(r, context.Time)).ToArray();
    }

    private static string Restriction(SwpcSolarRestriction restriction, CameraAgentSiteTime time)
    {
        var what = restriction.Reason == "daylight" ? "site daylight" : "site daylight or twilight";
        var localFrom = time.Local(restriction.FromUtc);
        var localUntil = time.Local(restriction.UntilUtc);
        var utcFrom = restriction.FromUtc.UtcDateTime;
        var utcUntil = restriction.UntilUtc.UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture,
            $"{what} {localFrom:yyyy-MM-dd HH:mm}–{(localUntil.Date == localFrom.Date ? localUntil.ToString("HH:mm", CultureInfo.InvariantCulture) : localUntil.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} local ({time.Label}); {utcFrom:yyyy-MM-dd HH:mm}–{(utcUntil.Date == utcFrom.Date ? utcUntil.ToString("HH:mm", CultureInfo.InvariantCulture) : utcUntil.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} UTC");
    }

    private static SpaceWeatherUiSource Source(
        SpaceWeatherOperations.ProductStatus status,
        SwpcStoreView view,
        CameraAgentSiteTime time,
        HashSet<string> configured)
    {
        var product = Enum.GetValues<SwpcProduct>().FirstOrDefault(p => p.Token() == status.Product);
        var recordDrift = view.Products.TryGetValue(product, out var stored) ? stored.Checkpoint.RecordDriftStampUtc : null;
        return new SpaceWeatherUiSource(
            status.Product,
            status.Configured,
            status.LastOutcome,
            status.Freshness,
            Time(status.LastSuccessUtc, time),
            Time(status.InFileStampUtc, time),
            Time(status.NextAttemptNotBeforeUtc, time),
            Time(status.RetryNotBeforeUtc, time),
            status.ConsecutiveTransientFailures,
            status.ConsecutiveRateLimited,
            status.ConsecutiveRejected,
            status.Halt is { } halt ? Halt("product", status.Product, halt, time, configured.Contains(status.Product)) : null,
            status.Configured && status.ConsecutiveRejected >= SwpcSource.RejectedAttentionCount,
            Time(recordDrift, time));
    }

    private static SpaceWeatherUiHalt Halt(
        string scope,
        string? product,
        SpaceWeatherOperations.HaltStatus halt,
        CameraAgentSiteTime time,
        bool canResume)
    {
        DateTimeOffset? since = DateTimeOffset.TryParseExact(halt.SinceUtc, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed : null;
        return new SpaceWeatherUiHalt(scope, product, halt.ReasonCode, halt.SinceUtc, since,
            Time(since, time), SpaceWeatherOperatorText.DescribeHalt(halt.ReasonCode), canResume && since is not null);
    }

    private static SpaceWeatherUiTime? Time(DateTimeOffset? instant, CameraAgentSiteTime time)
        => instant is { } value ? SpaceWeatherUiTime.Create(value, time) : null;

    private sealed record Entry(SwpcNoticeGroup Group, SwpcNoticeWindow Window, DateTimeOffset Issued);

    /// <summary>One active-location snapshot, read once, used for relevance, darkness and display time.</summary>
    private sealed record SiteContext(SpaceWeatherSiteLocation? Location, SpaceWeatherUiSite Site, CameraAgentSiteTime Time)
    {
        internal static SiteContext Create(DeploymentLocationSnapshot? location)
        {
            var time = new CameraAgentSiteTime(ObservingDayCalendar.ForDeployment(location));
            if (location is null || !double.IsFinite(location.LatitudeDegrees) || location.LatitudeDegrees is < -90 or > 90 ||
                !double.IsFinite(location.LongitudeDegrees) || location.LongitudeDegrees is < -180 or > 180 ||
                !double.IsFinite(location.ElevationMeters))
            {
                return new(null, new SpaceWeatherUiSite(false, null, time.Label), time);
            }
            return new(new SpaceWeatherSiteLocation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters),
                new SpaceWeatherUiSite(true, GeomagneticLatitude.Compute(location.LatitudeDegrees, location.LongitudeDegrees), time.Label),
                time);
        }
    }
}

/// <summary>
/// Darkness results per (group, window, site), shared across reads. It holds at most
/// <see cref="SpaceWeatherUiBounds.DarknessCacheEntries"/> entries and is cleared completely when a new entry would
/// exceed that bound.
/// </summary>
internal sealed class SpaceWeatherDarknessCache(int capacity = SpaceWeatherUiBounds.DarknessCacheEntries)
{
    private readonly Lock gate = new();
    private readonly Dictionary<Key, DarknessQualifier?> entries = [];

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    internal DarknessQualifier? GetOrAdd(Key key, Func<DarknessQualifier?> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }
        var value = compute();
        lock (gate)
        {
            if (!entries.ContainsKey(key) && entries.Count >= capacity)
            {
                entries.Clear();
            }
            entries[key] = value;
        }
        return value;
    }

    internal readonly record struct Key(
        string Group,
        DateTimeOffset FromUtc,
        DateTimeOffset UntilUtc,
        double LatitudeDeg,
        double LongitudeDeg,
        double ElevationMeters);
}
