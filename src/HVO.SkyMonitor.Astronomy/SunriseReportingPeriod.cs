using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// One resolved source period. Its date names the starting sunrise, not the publication or capture civil date.
/// Persist the complete value for retries; an active site's later configuration must never resolve it again.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SunriseReportingPeriod(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] DateOnly ReportDate,
    [property: JsonRequired] DateTimeOffset StartUtc,
    [property: JsonRequired] DateTimeOffset EndUtc,
    [property: JsonRequired] DeploymentLocationSnapshot Site,
    [property: JsonRequired] string TimeZoneRulesSha256,
    [property: JsonRequired] string StartSolarAlgorithmVersion,
    [property: JsonRequired] string EndSolarAlgorithmVersion,
    [property: JsonRequired] string IdentitySha256)
{
    public const string CurrentVersion = "hvo-sunrise-reporting-period-v1";

    [JsonIgnore]
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>The first stored millisecond inside this exact solar interval.</summary>
    [JsonIgnore]
    public long StartUnixMillisecondsInclusive => StoredMillisecondAtOrAfter(StartUtc);

    /// <summary>The exclusive stored-millisecond boundary. Adjacent periods use exactly this same value.</summary>
    [JsonIgnore]
    public long EndUnixMillisecondsExclusive => StoredMillisecondAtOrAfter(EndUtc);

    public bool Contains(DateTimeOffset sourceUtc) => sourceUtc >= StartUtc && sourceUtc < EndUtc;

    /// <summary>
    /// Binds publication eligibility once for a new occurrence. Persist this complete value; retries consume its
    /// retained allowance and eligibility rather than binding the current configuration again.
    /// </summary>
    public SunriseReportingFinality BindFinality(TimeSpan settleAllowance)
        => SunriseReportingFinality.Create(this, settleAllowance);

    /// <summary>Checks a retained value without consulting current coordinates, time-zone rules or wall time.</summary>
    public bool IsValid()
        => ContractVersion == CurrentVersion && Site is not null && Site.Validate().IsValid &&
           StartUtc.Offset == TimeSpan.Zero && EndUtc.Offset == TimeSpan.Zero && EndUtc > StartUtc &&
           EndUtc - StartUtc < TimeSpan.FromHours(48) &&
           TimeZoneRulesSha256 is { Length: 64 } && TimeZoneRulesSha256.All(char.IsAsciiHexDigit) &&
           !string.IsNullOrWhiteSpace(StartSolarAlgorithmVersion) && StartSolarAlgorithmVersion.Length <= 128 &&
           !string.IsNullOrWhiteSpace(EndSolarAlgorithmVersion) && EndSolarAlgorithmVersion.Length <= 128 &&
           string.Equals(IdentitySha256, ComputeIdentity(this), StringComparison.OrdinalIgnoreCase);

    internal static SunriseReportingPeriod Create(DateOnly date, DateTimeOffset start, DateTimeOffset end,
        DeploymentLocationSnapshot site, string rulesSha256, string startAlgorithm, string endAlgorithm)
    {
        var period = new SunriseReportingPeriod(CurrentVersion, date, start, end, site, rulesSha256,
            startAlgorithm, endAlgorithm, string.Empty);
        return period with { IdentitySha256 = ComputeIdentity(period) };
    }

    /// <summary>
    /// Raw ingress timestamps are stored at millisecond precision. Ceiling an exact boundary, rather than truncating
    /// it, makes indexed queries agree with <see cref="Contains"/> for every retained timestamp.
    /// </summary>
    public static long StoredMillisecondAtOrAfter(DateTimeOffset utc)
    {
        var floor = utc.ToUnixTimeMilliseconds();
        return utc == DateTimeOffset.FromUnixTimeMilliseconds(floor) ? floor : checked(floor + 1);
    }

    private static string ComputeIdentity(SunriseReportingPeriod period)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            period.ContractVersion,
            period.ReportDate,
            period.StartUtc,
            period.EndUtc,
            SiteIdentity = period.Site.CanonicalSha256,
            period.TimeZoneRulesSha256,
            period.StartSolarAlgorithmVersion,
            period.EndSolarAlgorithmVersion
        });
}

/// <summary>Unavailable periods carry no substitute noon, midnight, fixed sunrise or invented sky coverage.</summary>
public sealed record SunriseReportingPeriodResolution(
    DateOnly ReportDate,
    SunriseReportingPeriod? Period,
    string? UnavailableReasonCode)
{
    public bool Available => Period is not null;
}
