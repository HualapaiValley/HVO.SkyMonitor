using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The server-side bounds of the space-weather operator UI. Counts are exact and computed before these bounds apply;
/// the components repeat them rather than re-deriving them.
/// </summary>
internal static class SpaceWeatherUiBounds
{
    /// <summary>Active and pending notices shown together, before "N more".</summary>
    internal const int CurrentRows = 50;
    /// <summary>Recent notices (the 24 hours after a notice ends), before "N more".</summary>
    internal const int RecentRows = 50;
    /// <summary>History notices per page.</summary>
    internal const int HistoryPageSize = 25;
    /// <summary>Navigable history pages; older retained notices are counted, not shown.</summary>
    internal const int HistoryPages = 8;
    /// <summary>History notices the projection carries.</summary>
    internal const int HistoryRows = HistoryPageSize * HistoryPages;
    /// <summary>Notice rows the overview summary lists.</summary>
    internal const int SummaryRows = 3;
    /// <summary>Darkness results kept between reads; the cache is cleared when it would exceed this.</summary>
    internal const int DarknessCacheEntries = 256;
    /// <summary>How long one shared projection, or a failure, is reused.</summary>
    internal static readonly TimeSpan ProjectionTtl = TimeSpan.FromSeconds(10);
    /// <summary>The bound on one shared projection read, independent of every caller's token.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    /// <summary>The minimum age before the Operations layout starts another background read.</summary>
    internal static readonly TimeSpan LayoutRefreshAge = TimeSpan.FromSeconds(30);
}

/// <summary>Whether a projection carries data, and why not when it does not.</summary>
public enum SpaceWeatherUiState
{
    /// <summary>Status and notices were read.</summary>
    Ready,
    /// <summary>The local store could not be read; nothing is claimed about activity.</summary>
    StoreUnavailable,
    /// <summary>The read failed for another reason; nothing is claimed about activity.</summary>
    ReadFailed
}

/// <summary>Whether a notice's level rose after it was acknowledged, derived from retained versions only.</summary>
public enum SpaceWeatherEscalation
{
    /// <summary>The notice has no acknowledgement.</summary>
    NotAcknowledged,
    /// <summary>Acknowledged, and the level has not risen since.</summary>
    NotEscalated,
    /// <summary>Acknowledged, and the level has risen since.</summary>
    Escalated,
    /// <summary>Acknowledged, but the retained versions cannot show whether the level rose.</summary>
    Unknown
}

/// <summary>One instant shown in UTC and in site-local time with its offset and zone.</summary>
public sealed record SpaceWeatherUiTime(DateTimeOffset Utc, string UtcText, string LocalText)
{
    internal static SpaceWeatherUiTime Create(DateTimeOffset utc, CameraAgentSiteTime siteTime)
    {
        ArgumentNullException.ThrowIfNull(siteTime);
        var value = utc.ToUniversalTime();
        return new(value, value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            siteTime.Format(value, "yyyy-MM-dd HH:mm"));
    }
}

/// <summary>The site context used for relevance and darkness.</summary>
public sealed record SpaceWeatherUiSite(bool Configured, double? GeomagneticLatitude, string ZoneLabel);

/// <summary>
/// A provider or product fetch halt. <see cref="SinceUtcText"/> is the unformatted UTC round-trip text from the
/// operations status; <see cref="SinceUtc"/> is that text parsed with a zero offset, which a resume echoes exactly.
/// </summary>
public sealed record SpaceWeatherUiHalt(
    string Scope,
    string? Product,
    string ReasonCode,
    string SinceUtcText,
    DateTimeOffset? SinceUtc,
    SpaceWeatherUiTime? Since,
    string Description,
    bool CanResume)
{
    /// <summary>The halt's target as shown to the operator.</summary>
    public string Target => Product ?? "provider";
}

/// <summary>One product's redacted fetch state.</summary>
public sealed record SpaceWeatherUiSource(
    string Product,
    bool Configured,
    string? LastOutcome,
    string Freshness,
    SpaceWeatherUiTime? LastSuccess,
    SpaceWeatherUiTime? InFileStamp,
    SpaceWeatherUiTime? NextAttempt,
    SpaceWeatherUiTime? RetryNotBefore,
    int ConsecutiveTransientFailures,
    int ConsecutiveRateLimited,
    int ConsecutiveRejected,
    SpaceWeatherUiHalt? Halt,
    bool FormatDrift,
    SpaceWeatherUiTime? RecordDrift);

/// <summary>
/// One retained notice. Acknowledgement never hides, ends or reorders a notice: <see cref="State"/> comes from the
/// lifecycle alone, and an ended notice keeps its <see cref="EndReason"/> whether or not it was acknowledged.
/// </summary>
public sealed record SpaceWeatherUiNotice
{
    public required string Group { get; init; }
    public required string FamilyToken { get; init; }
    public required string Label { get; init; }
    public string? Level { get; init; }
    public required SwpcNoticeState State { get; init; }
    public required SpaceWeatherUiTime Issued { get; init; }
    /// <summary>The latest version's issue time, only when it differs from <see cref="Issued"/>.</summary>
    public SpaceWeatherUiTime? LastUpdate { get; init; }
    public bool LastUpdateIsCancellation { get; init; }
    public required SpaceWeatherUiTime From { get; init; }
    public required SpaceWeatherUiTime Until { get; init; }
    public string? EndReason { get; init; }
    public bool Corrected { get; init; }
    public bool Orphan { get; init; }
    public int VersionCount { get; init; }
    public SpaceWeatherUiTime? AcknowledgedAt { get; init; }
    public required SpaceWeatherEscalation Escalation { get; init; }
    public bool MayBeOutOfDate { get; init; }
    /// <summary>Source activity, as SWPC stated it.</summary>
    public required string GlobalLine { get; init; }
    public required LocalRelevanceCategory Relevance { get; init; }
    /// <summary>The HVO SkyMonitor estimate for this site, kept separate from <see cref="GlobalLine"/>.</summary>
    public required string SiteLine { get; init; }
    /// <summary>Site daylight and twilight restrictions; pending and active notices only.</summary>
    public IReadOnlyList<string> Darkness { get; init; } = [];

    public bool IsCurrent => State is SwpcNoticeState.Pending or SwpcNoticeState.Active;

    /// <summary>
    /// Unacknowledged, escalated since it was acknowledged, or of undeterminable escalation; a fresh acknowledgement
    /// records a new acknowledgement time.
    /// </summary>
    public bool NeedsAcknowledgement => Escalation is SpaceWeatherEscalation.NotAcknowledged or
        SpaceWeatherEscalation.Escalated or SpaceWeatherEscalation.Unknown;

    /// <summary>Within or near the site's geomagnetic latitude by the HVO estimate.</summary>
    public bool ReachesSite => Relevance is LocalRelevanceCategory.InImpactArea or LocalRelevanceCategory.PossibleNearHorizon;

    public string StateText => State switch
    {
        SwpcNoticeState.Active => "active",
        SwpcNoticeState.Pending => "pending",
        SwpcNoticeState.Recent => "recently ended",
        _ => "history"
    };

    public string? EscalationText => Escalation switch
    {
        SpaceWeatherEscalation.Escalated => "escalated since acknowledged",
        SpaceWeatherEscalation.Unknown => "acknowledged; escalation since then cannot be determined",
        SpaceWeatherEscalation.NotEscalated => "acknowledged",
        _ => null
    };
}

/// <summary>
/// One immutable read of the local space-weather state: mapped status, bounded notice lists with exact totals, drift
/// and halt facts, and the site context. It holds no raw body, no message text beyond typed fields, and no path. A
/// projection that is not <see cref="SpaceWeatherUiState.Ready"/> carries no notices and makes no activity claim.
/// </summary>
public sealed record SpaceWeatherUiProjection
{
    public required SpaceWeatherUiState State { get; init; }
    public required DateTimeOffset ObservedUtc { get; init; }
    public required SpaceWeatherUiTime Observed { get; init; }
    public required bool Enabled { get; init; }
    public bool DefinitionExists { get; init; }
    public bool DefinitionEnabled { get; init; }
    /// <summary>Status and notices changed between the paired reads, twice.</summary>
    public bool Inconsistent { get; init; }
    public string Credit { get; init; } = SpaceWeatherOperations.Credit;
    /// <summary>The alerts product's freshness token, or null when no status was read.</summary>
    public string? AlertsFreshness { get; init; }
    /// <summary>At least one product is enabled and none has completed a successful fetch.</summary>
    public bool NoFirstSuccess { get; init; }
    public IReadOnlyList<SpaceWeatherUiSource> Sources { get; init; } = [];
    public SpaceWeatherUiHalt? ProviderHalt { get; init; }
    public IReadOnlyList<SpaceWeatherUiHalt> ProductHalts { get; init; } = [];
    public IReadOnlyList<string> FormatDriftProducts { get; init; } = [];
    public IReadOnlyList<string> RecordDriftProducts { get; init; } = [];
    public required SpaceWeatherUiSite Site { get; init; }

    /// <summary>Active notices, then pending, at most <see cref="SpaceWeatherUiBounds.CurrentRows"/>.</summary>
    public IReadOnlyList<SpaceWeatherUiNotice> Current { get; init; } = [];
    public int CurrentTotal { get; init; }
    public int ActiveCount { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<SpaceWeatherUiNotice> Recent { get; init; } = [];
    public int RecentTotal { get; init; }
    /// <summary>History notices, at most <see cref="SpaceWeatherUiBounds.HistoryRows"/>.</summary>
    public IReadOnlyList<SpaceWeatherUiNotice> History { get; init; } = [];
    public int HistoryTotal { get; init; }

    /// <summary>Pending or active notices reaching or approaching the site, unacknowledged, escalated or unknown.</summary>
    public int ReachCount { get; init; }
    /// <summary>Other pending or active notices that are unacknowledged or escalated.</summary>
    public int ReviewCount { get; init; }

    public bool HasData => State == SpaceWeatherUiState.Ready;
    public int CurrentMore => CurrentTotal - Current.Count;
    public int RecentMore => RecentTotal - Recent.Count;
    public int HistoryNotShown => HistoryTotal - History.Count;
    public int NoticeTotal => CurrentTotal + RecentTotal + HistoryTotal;
    public int HistoryPageCount => Math.Max(1, (History.Count + SpaceWeatherUiBounds.HistoryPageSize - 1) / SpaceWeatherUiBounds.HistoryPageSize);

    /// <summary>A retained halt on a product this host does not fetch; it cannot be resumed here.</summary>
    public bool IsUnconfigured(SpaceWeatherUiHalt halt)
    {
        ArgumentNullException.ThrowIfNull(halt);
        return halt.Product is { } product &&
            !Sources.Any(source => source.Configured && string.Equals(source.Product, product, StringComparison.Ordinal));
    }

    /// <summary>Clamps a requested history page to the navigable range.</summary>
    public int ClampHistoryPage(int index) => Math.Clamp(index, 0, Math.Min(SpaceWeatherUiBounds.HistoryPages, HistoryPageCount) - 1);

    /// <summary>One page of history, after clamping <paramref name="index"/>.</summary>
    public IReadOnlyList<SpaceWeatherUiNotice> HistoryPage(int index)
        => History.Skip(ClampHistoryPage(index) * SpaceWeatherUiBounds.HistoryPageSize).Take(SpaceWeatherUiBounds.HistoryPageSize).ToArray();
}
