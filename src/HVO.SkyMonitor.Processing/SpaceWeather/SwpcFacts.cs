using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>The four approved SWPC products.</summary>
public enum SwpcProduct
{
    /// <summary>The alerts token.</summary>
    Alerts,
    /// <summary>The scales token.</summary>
    Scales,
    /// <summary>The kp-forecast token.</summary>
    KpForecast,
    /// <summary>The 3-day-forecast token.</summary>
    ThreeDayForecast
}

/// <summary>The source message family, independent of local relevance.</summary>
public enum SpaceWeatherFamily
{
    /// <summary>The watch token.</summary>
    Watch,
    /// <summary>The warning token.</summary>
    Warning,
    /// <summary>The k-alert token.</summary>
    KAlert,
    /// <summary>The xray-alert token.</summary>
    XrayAlert,
    /// <summary>The radio-sweep token.</summary>
    RadioSweep,
    /// <summary>The flux-alert token.</summary>
    FluxAlert,
    /// <summary>The summary token.</summary>
    Summary,
    /// <summary>The unclassified token.</summary>
    Unclassified
}

/// <summary>A derived notice lifecycle state.</summary>
public enum SwpcNoticeState
{
    /// <summary>The pending token.</summary>
    Pending,
    /// <summary>The active token.</summary>
    Active,
    /// <summary>The recent token.</summary>
    Recent,
    /// <summary>The history token.</summary>
    History
}

/// <summary>The adjustment that ended a notice.</summary>
public enum SwpcNoticeEndReason
{
    /// <summary>The expired token.</summary>
    Expired,
    /// <summary>The superseded token.</summary>
    Superseded,
    /// <summary>The cancelled token.</summary>
    Cancelled,
    /// <summary>The capped token.</summary>
    Capped
}

/// <summary>An HVO estimate for optical observing.</summary>
public enum LocalRelevanceCategory
{
    /// <summary>The InImpactArea token.</summary>
    InImpactArea,
    /// <summary>The PossibleNearHorizon token.</summary>
    PossibleNearHorizon,
    /// <summary>The Unlikely token.</summary>
    Unlikely,
    /// <summary>The NotExpected token.</summary>
    NotExpected,
    /// <summary>The Unknown token.</summary>
    Unknown,
    /// <summary>The NoLocalObservingImpact token.</summary>
    NoLocalObservingImpact
}

/// <summary>The first failed check for one alert record.</summary>
/// <param name="Index">The zero-based record index.</param>
/// <param name="Reason">The contract reason token.</param>
public sealed record SwpcRecordRejection(int Index, string Reason);

/// <summary>A bounded decoded product, or a body rejection.</summary>
/// <param name="Records">The accepted product records.</param>
/// <param name="Rejections">The ordered per-record failures.</param>
/// <param name="RejectionReason">The first body rejection reason, or null.</param>
/// <param name="SnapshotSha256">The uppercase content hash of the decoded JSON or normalized text.</param>
/// <param name="StampUtc">The in-file content stamp, independent of retrieval time.</param>
/// <param name="RecordCount">The source row count, including duplicates.</param>
public sealed record SwpcBodyResult<T>(ImmutableArray<T> Records, ImmutableArray<SwpcRecordRejection> Rejections, string? RejectionReason, string? SnapshotSha256, DateTimeOffset? StampUtc, int RecordCount)
{
    /// <summary>Whether the body passed its product checks.</summary>
    public bool Accepted => RejectionReason is null;
}

/// <summary>A code and serial, including an absent link target.</summary>
/// <param name="Code">The source message code.</param>
/// <param name="Serial">The source serial number.</param>
public sealed record SwpcMessageNode(string Code, int Serial);

/// <summary>A source link to another serial of the same code.</summary>
/// <param name="Kind">The kind value.</param>
/// <param name="TargetSerial">The target serial value.</param>
public sealed record SwpcMessageLink(string Kind, int TargetSerial);

/// <summary>A year-resolved UTC watch day and its verbatim level.</summary>
/// <param name="DateUtc">The date utc value.</param>
/// <param name="Level">The source level token, or null when absent.</param>
public sealed record SwpcWatchDay(DateOnly DateUtc, string Level);

/// <summary>Parsed source facts; an invalid watch day list is null.</summary>
/// <param name="Headline">The headline value.</param>
/// <param name="Kind">The kind value.</param>
/// <param name="Modifier">The modifier value.</param>
/// <param name="Corrected">The corrected value.</param>
/// <param name="Link">The link value.</param>
/// <param name="Family">The family value.</param>
/// <param name="Level">The source level token, or null when absent.</param>
/// <param name="StatedLatitude">The stated latitude value.</param>
/// <param name="Days">The resolved watch days; null when a listed date is invalid.</param>
/// <param name="Times">The parsed required source times; missing or invalid values are null.</param>
/// <param name="SynopticPeriod">The synoptic period value.</param>
/// <param name="ScaleDescriptor">The original NOAA Scale field.</param>
public sealed record SwpcMessageHeader(string Headline, string? Kind, string? Modifier, bool Corrected, SwpcMessageLink? Link, SpaceWeatherFamily Family, string? Level, double? StatedLatitude, ImmutableArray<SwpcWatchDay>? Days, ImmutableDictionary<string, DateTimeOffset?> Times, string? SynopticPeriod, string? ScaleDescriptor);

/// <summary>An issued version with normalized text and its verbatim original JSON.</summary>
/// <param name="Identity">The stable message or group identity.</param>
/// <param name="Code">The source message code.</param>
/// <param name="Serial">The source serial number.</param>
/// <param name="IssueMinuteUtc">The issue minute parsed from the message header.</param>
/// <param name="IssueDatetimeText">The source attribute checked as text, never parsed as a time.</param>
/// <param name="ContentSha256">The uppercase SHA-256 of normalized message text.</param>
/// <param name="OriginalRecordJson">The exact source JSON span from opening to closing brace.</param>
/// <param name="Header">The parsed message facts.</param>
/// <param name="ProductId">The source product_id attribute.</param>
/// <param name="NormalizedText">The normalized source text.</param>
public sealed record SwpcMessageRecord(string Identity, string Code, int Serial, DateTimeOffset IssueMinuteUtc, string IssueDatetimeText, string ContentSha256, string OriginalRecordJson, SwpcMessageHeader Header, string ProductId, string NormalizedText)
{
    /// <summary>The serial node used for grouping.</summary>
    public SwpcMessageNode Node => new(Code, Serial);
}

/// <summary>An accepted message with its durable ingest ordering.</summary>
/// <param name="Message">The message value.</param>
/// <param name="IngestSequence">The caller-provided durable ingest order.</param>
public sealed record SwpcMessageVersion(SwpcMessageRecord Message, long IngestSequence);

/// <summary>A linked group whose versions are in contract order.</summary>
/// <param name="Identity">The stable message or group identity.</param>
/// <param name="Nodes">All serial nodes, including missing link targets.</param>
/// <param name="Versions">All versions in issue-minute, attribute-text, ingest order.</param>
/// <param name="Orphan">Whether any linked serial is missing.</param>
public sealed record SwpcNoticeGroup(string Identity, ImmutableHashSet<SwpcMessageNode> Nodes, ImmutableArray<SwpcMessageVersion> Versions, bool Orphan)
{
    /// <summary>The earliest non-cancel version, or null for a cancel-only group.</summary>
    public SwpcMessageRecord? Root => Versions.Select(v => v.Message).FirstOrDefault(m => m.Header.Modifier != "CANCEL");
    /// <summary>The latest non-cancel version.</summary>
    public SwpcMessageRecord? LatestNonCancel => Versions.Select(v => v.Message).LastOrDefault(m => m.Header.Modifier != "CANCEL");
    /// <summary>The last version in contract order, including cancellation.</summary>
    public SwpcMessageRecord Latest => Versions[^1].Message;
    /// <summary>The source family of the first non-cancel version, or of the cancel.</summary>
    public SpaceWeatherFamily Family => Root?.Header.Family ?? (Versions[0].Message.Code.StartsWith("WATA", StringComparison.Ordinal) ? SpaceWeatherFamily.Watch : Versions[0].Message.Header.Family);
    /// <summary>The latest non-cancel source level.</summary>
    public string? Level => LatestNonCancel?.Header.Level;
    /// <summary>The lowest serial node, including absent link targets.</summary>
    public SwpcMessageNode CanonicalNode => Nodes.OrderBy(n => n.Code, StringComparer.Ordinal).ThenBy(n => n.Serial).First();
}

/// <summary>A half-open UTC window with its current lifecycle state.</summary>
/// <param name="FromUtc">The inclusive UTC start.</param>
/// <param name="UntilUtc">The exclusive UTC end.</param>
/// <param name="RecentUntilUtc">The recent until utc value.</param>
/// <param name="State">The state value.</param>
/// <param name="EndReason">The end reason when recent or history; otherwise null.</param>
/// <param name="WindowEndReason">The eventual end reason, also retained while pending or active.</param>
/// <param name="Fallback">Whether both bounds use the six-hour fallback.</param>
public sealed record SwpcNoticeWindow(DateTimeOffset FromUtc, DateTimeOffset UntilUtc, DateTimeOffset RecentUntilUtc, SwpcNoticeState State, SwpcNoticeEndReason? EndReason, SwpcNoticeEndReason? WindowEndReason, bool Fallback);

/// <summary>A local attention event that never performs delivery.</summary>
/// <param name="Group">The group identity for local attention.</param>
/// <param name="Reason">The contract reason token.</param>
/// <param name="Level">The source level token, or null when absent.</param>
public sealed record SwpcAttentionEvent(string Group, string Reason, string? Level);

/// <summary>An operator acknowledgement independent of lifecycle and raised ranks.</summary>
/// <param name="Operator">The operator value.</param>
/// <param name="AtUtc">The at utc value.</param>
public sealed record SwpcAcknowledgement(string Operator, DateTimeOffset AtUtc);

/// <summary>The complete immutable input for notice attention after a merge.</summary>
/// <param name="Groups">The groups value.</param>
/// <param name="Touched">The serial nodes to which the caller added a version.</param>
/// <param name="RaisedRanks">The durable rank map keyed by code and serial.</param>
/// <param name="NowUtc">The explicit evaluation time.</param>
public sealed record SwpcAttentionInput(ImmutableArray<SwpcNoticeGroup> Groups, ImmutableHashSet<SwpcMessageNode> Touched, ImmutableDictionary<SwpcMessageNode, int> RaisedRanks, DateTimeOffset NowUtc);

/// <summary>Ordered events and the replacement durable raised-rank map.</summary>
/// <param name="Events">The events value.</param>
/// <param name="RaisedRanks">The durable rank map keyed by code and serial.</param>
public sealed record SwpcAttentionResult(ImmutableArray<SwpcAttentionEvent> Events, ImmutableDictionary<SwpcMessageNode, int> RaisedRanks);

/// <summary>One source scale window, keeping scale and probability strings and nulls.</summary>
/// <param name="Window">The window value.</param>
/// <param name="StampUtc">The in-file content stamp, independent of retrieval time.</param>
/// <param name="RScale">The r scale value.</param>
/// <param name="RMinorProbPct">The r minor prob pct value.</param>
/// <param name="RMajorProbPct">The r major prob pct value.</param>
/// <param name="SScale">The s scale value.</param>
/// <param name="SProbPct">The s prob pct value.</param>
/// <param name="GScale">The g scale value.</param>
public sealed record ScaleReading(string Window, DateTimeOffset StampUtc, string? RScale, string? RMinorProbPct, string? RMajorProbPct, string? SScale, string? SProbPct, string? GScale);

/// <summary>A three-hour source bin whose label does not determine its relation to now.</summary>
/// <param name="StartUtc">The start utc value.</param>
/// <param name="KpText">The verbatim Kp number text.</param>
/// <param name="ObservedKind">The verbatim observed, estimated or predicted label.</param>
/// <param name="NoaaScale">The source NOAA scale or null.</param>
public sealed record KpBin(DateTimeOffset StartUtc, string KpText, string ObservedKind, string? NoaaScale)
{
    /// <summary>The exclusive end of this three-hour bin.</summary>
    public DateTimeOffset EndUtc => StartUtc.AddHours(3);
    /// <summary>Computes past, current or future using the explicit read time.</summary>
    public string RelationAt(DateTimeOffset nowUtc) => EndUtc <= nowUtc ? "past" : StartUtc <= nowUtc ? "current" : "future";
}

/// <summary>A three-day forecast bin with verbatim Kp and optional G mark.</summary>
/// <param name="StartUtc">The start utc value.</param>
/// <param name="KpText">The verbatim Kp number text.</param>
/// <param name="NoaaScale">The source NOAA scale or null.</param>
public sealed record SwpcForecastKpBin(DateTimeOffset StartUtc, string KpText, string? NoaaScale);

/// <summary>An issued forecast with separate radiation and radio blackout probabilities.</summary>
/// <param name="IssuedUtc">The issued utc value.</param>
/// <param name="KpBreakdown">The kp breakdown value.</param>
/// <param name="RadiationProbabilities">The radiation probabilities value.</param>
/// <param name="RadioBlackoutProbabilities">The radio blackout probabilities value.</param>
/// <param name="NormalizedText">The normalized source text.</param>
public sealed record IssuedForecast(DateTimeOffset IssuedUtc, ImmutableSortedDictionary<DateTimeOffset, SwpcForecastKpBin> KpBreakdown, ImmutableDictionary<string, ImmutableDictionary<DateOnly, string>> RadiationProbabilities, ImmutableDictionary<string, ImmutableDictionary<DateOnly, string>> RadioBlackoutProbabilities, string NormalizedText);

/// <summary>An HVO estimate with the numerical basis retained separately from the global fact.</summary>
/// <param name="Category">The category value.</param>
/// <param name="Basis">The basis value.</param>
/// <param name="RequiredLatitudeDeg">The required latitude deg value.</param>
/// <param name="SiteGeomagneticLatitudeDeg">The site geomagnetic latitude deg value.</param>
/// <param name="DifferenceDeg">The difference deg value.</param>
/// <param name="ModelVersion">The model version token, currently unspecified by the checkpoint.</param>
public sealed record LocalRelevance(LocalRelevanceCategory Category, string? Basis, double? RequiredLatitudeDeg, double? SiteGeomagneticLatitudeDeg, double? DifferenceDeg, string? ModelVersion);

/// <summary>The observer coordinates needed for a darkness query.</summary>
/// <param name="LatitudeDeg">The latitude deg value.</param>
/// <param name="LongitudeDeg">The longitude deg value.</param>
/// <param name="ElevationMeters">The elevation meters value.</param>
public sealed record SpaceWeatherSiteLocation(double LatitudeDeg, double LongitudeDeg, double ElevationMeters);

/// <summary>Conservative solar boundary evidence; absence of events makes no visibility claim.</summary>
/// <param name="Boundaries">The ordered solar boundary evidence.</param>
/// <param name="Restrictions">The conservative daylight and twilight intervals.</param>
public sealed record DarknessQualifier(ImmutableArray<HVO.SkyMonitor.Astronomy.SolarEventResult> Boundaries, ImmutableArray<SwpcSolarRestriction> Restrictions);

/// <summary>A solar interval that only restricts optical observing.</summary>
/// <param name="FromUtc">The inclusive UTC start.</param>
/// <param name="UntilUtc">The exclusive UTC end.</param>
/// <param name="Reason">The contract reason token.</param>
public sealed record SwpcSolarRestriction(DateTimeOffset FromUtc, DateTimeOffset UntilUtc, string Reason);
