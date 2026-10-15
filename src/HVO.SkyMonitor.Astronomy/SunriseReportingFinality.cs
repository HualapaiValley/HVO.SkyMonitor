using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// A retained source period and its fixed publication eligibility. The settle allowance belongs to this identity,
/// independently of the calendar's reusable solar period. Later settings cannot change an existing occurrence.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SunriseReportingFinality(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] SunriseReportingPeriod Period,
    [property: JsonRequired] TimeSpan ProcessingSettleAllowance,
    [property: JsonRequired] DateTimeOffset EarliestFinalUtc,
    [property: JsonRequired] string IdentitySha256)
{
    public const string CurrentVersion = "hvo-sunrise-reporting-finality-v1";

    /// <summary>Validates retained metadata without current configuration, coordinates or wall time.</summary>
    public bool IsValid()
        => ContractVersion == CurrentVersion && Period is not null && Period.IsValid() &&
           ProcessingSettleAllowance >= TimeSpan.Zero && ProcessingSettleAllowance <= TimeSpan.FromDays(1) &&
           Period.EndUtc.UtcTicks <= DateTimeOffset.MaxValue.UtcTicks - ProcessingSettleAllowance.Ticks &&
           EarliestFinalUtc.Offset == TimeSpan.Zero && EarliestFinalUtc == Period.EndUtc + ProcessingSettleAllowance &&
           string.Equals(IdentitySha256, ComputeIdentity(this), StringComparison.OrdinalIgnoreCase);

    /// <summary>An invalid value or an instant before retained eligibility cannot authorize a final.</summary>
    public bool IsEligibleForFinal(DateTimeOffset instantUtc) => IsValid() && instantUtc >= EarliestFinalUtc;

    internal static SunriseReportingFinality Create(SunriseReportingPeriod period, TimeSpan settleAllowance)
    {
        ArgumentNullException.ThrowIfNull(period);
        if (!period.IsValid())
        {
            throw new ArgumentException("Publication eligibility requires a valid retained period.", nameof(period));
        }
        if (settleAllowance < TimeSpan.Zero || settleAllowance > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(settleAllowance));
        }
        var value = new SunriseReportingFinality(CurrentVersion, period, settleAllowance,
            period.EndUtc + settleAllowance, string.Empty);
        return value with { IdentitySha256 = ComputeIdentity(value) };
    }

    private static string ComputeIdentity(SunriseReportingFinality value)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            value.ContractVersion,
            PeriodIdentitySha256 = value.Period.IdentitySha256,
            ProcessingSettleTicks = value.ProcessingSettleAllowance.Ticks,
            value.EarliestFinalUtc
        });
}
