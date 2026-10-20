namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Stable spellings from the SWPC v1 contract.</summary>
public static class SwpcTokens
{
    /// <summary>Returns the contract token for a SwpcProduct.</summary>
    public static string Token(this SwpcProduct value) => value switch
    {
        SwpcProduct.Alerts => "alerts",
        SwpcProduct.Scales => "scales",
        SwpcProduct.KpForecast => "kp-forecast",
        SwpcProduct.ThreeDayForecast => "3-day-forecast",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /// <summary>Returns the contract token for a SpaceWeatherFamily.</summary>
    public static string Token(this SpaceWeatherFamily value) => value switch
    {
        SpaceWeatherFamily.Watch => "watch",
        SpaceWeatherFamily.Warning => "warning",
        SpaceWeatherFamily.KAlert => "k-alert",
        SpaceWeatherFamily.XrayAlert => "xray-alert",
        SpaceWeatherFamily.RadioSweep => "radio-sweep",
        SpaceWeatherFamily.FluxAlert => "flux-alert",
        SpaceWeatherFamily.Summary => "summary",
        SpaceWeatherFamily.Unclassified => "unclassified",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /// <summary>Returns the contract token for a SwpcNoticeState.</summary>
    public static string Token(this SwpcNoticeState value) => value switch
    {
        SwpcNoticeState.Pending => "pending",
        SwpcNoticeState.Active => "active",
        SwpcNoticeState.Recent => "recent",
        SwpcNoticeState.History => "history",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /// <summary>Returns the contract token for a SwpcNoticeEndReason.</summary>
    public static string Token(this SwpcNoticeEndReason value) => value switch
    {
        SwpcNoticeEndReason.Expired => "expired",
        SwpcNoticeEndReason.Superseded => "superseded",
        SwpcNoticeEndReason.Cancelled => "cancelled",
        SwpcNoticeEndReason.Capped => "capped",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /// <summary>Returns the contract token for a LocalRelevanceCategory.</summary>
    public static string Token(this LocalRelevanceCategory value) => value switch
    {
        LocalRelevanceCategory.InImpactArea => "InImpactArea",
        LocalRelevanceCategory.PossibleNearHorizon => "PossibleNearHorizon",
        LocalRelevanceCategory.Unlikely => "Unlikely",
        LocalRelevanceCategory.NotExpected => "NotExpected",
        LocalRelevanceCategory.Unknown => "Unknown",
        LocalRelevanceCategory.NoLocalObservingImpact => "NoLocalObservingImpact",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /// <summary>The body rejection vocabulary in check order.</summary>
    public static System.Collections.Immutable.ImmutableArray<string> BodyRejectionReasons =>
        ["oversize", "content-type", "encoding", "malformed-json", "schema", "suspect-empty", "too-many-records", "no-valid-records"];
    /// <summary>The per-record rejection vocabulary in check order.</summary>
    public static System.Collections.Immutable.ImmutableArray<string> RecordRejectionReasons =>
        ["record-schema", "issue-datetime", "record-oversize", "message-header"];
}
