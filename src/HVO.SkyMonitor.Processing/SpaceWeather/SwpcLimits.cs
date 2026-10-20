namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Fixed v1 bounds, applied before decoding or proportional allocation.</summary>
public static class SwpcLimits
{
    /// <summary>Maximum decoded alert body bytes.</summary>
    public const int AlertsBytes = 1_048_576;
    /// <summary>Maximum decoded scales or three-day text bytes.</summary>
    public const int SmallProductBytes = 65_536;
    /// <summary>Maximum decoded Kp body bytes.</summary>
    public const int KpBytes = 262_144;
    /// <summary>Maximum alert records in one body.</summary>
    public const int Records = 2_000;
    /// <summary>Maximum UTF-8 message bytes before normalization.</summary>
    public const int MessageBytes = 16_384;
    /// <summary>Maximum object/array depth, counting the outer container as one.</summary>
    public const int JsonDepth = 64;
    /// <summary>First accepted source year.</summary>
    public const int FirstYear = 1900;
    /// <summary>Last accepted source year.</summary>
    public const int LastYear = 9998;
    /// <summary>Short-window new-notice grace in seconds.</summary>
    public const int NewGraceSeconds = 300;
    /// <summary>Maximum lifetime after the latest non-cancel issue.</summary>
    public const int ActiveCapHours = 96;
    /// <summary>Recent history duration in hours.</summary>
    public const int RecentHours = 24;
    /// <summary>Retention after the end of a notice in days.</summary>
    public const int RetentionDays = 35;
    /// <summary>Returns the decoded byte cap for the approved product.</summary>
    public static int BodyBytes(SwpcProduct product) => product switch
    {
        SwpcProduct.Alerts => AlertsBytes,
        SwpcProduct.Scales or SwpcProduct.ThreeDayForecast => SmallProductBytes,
        SwpcProduct.KpForecast => KpBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    };
}
