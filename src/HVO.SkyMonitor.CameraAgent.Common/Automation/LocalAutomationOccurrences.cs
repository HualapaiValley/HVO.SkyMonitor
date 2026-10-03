using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

[JsonConverter(typeof(JsonStringEnumConverter<LocalAutomationSourceWindowKind>))]
public enum LocalAutomationSourceWindowKind
{
    CompletedCivilHour,
    SunriseDay
}

[JsonConverter(typeof(JsonStringEnumConverter<LocalAutomationSourceSelection>))]
public enum LocalAutomationSourceSelection
{
    AllActualSources,
    DarkNightActualSources
}

/// <summary>Source period, selection, and fixed publication allowance; none is a sliding wall-clock query.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalAutomationSourceWindowPolicy(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] LocalAutomationSourceWindowKind Kind,
    [property: JsonRequired] LocalAutomationSourceSelection Selection,
    [property: JsonRequired] TimeSpan ProcessingSettleAllowance)
{
    public const string CurrentVersion = "hvo-automation-source-window-policy-v1";

    public bool IsValid() => ContractVersion == CurrentVersion && Enum.IsDefined(Kind) &&
        Enum.IsDefined(Selection) && ProcessingSettleAllowance >= TimeSpan.Zero &&
        ProcessingSettleAllowance <= TimeSpan.FromDays(1);
}

/// <summary>A planned span and its retained sunrise date/site/rules, including partial civil hours.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalAutomationSourceWindow(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] LocalAutomationSourceWindowPolicy Policy,
    [property: JsonRequired] SunriseReportingPeriod ReportingPeriod,
    [property: JsonRequired] DateTimeOffset StartUtc,
    [property: JsonRequired] DateTimeOffset EndUtc,
    [property: JsonRequired] DateTimeOffset EarliestFinalUtc,
    [property: JsonRequired] SunriseReportingFinality? DailyFinality,
    [property: JsonRequired] string IdentitySha256)
{
    public const string CurrentVersion = "hvo-automation-source-window-v1";
    public const string DarkNightSelectionVersion = "geometric-solar-altitude-at-source-utc-lte-minus18-v1";

    public bool IsValid() => ContractVersion == CurrentVersion && Policy is not null && Policy.IsValid() &&
        ReportingPeriod is not null && ReportingPeriod.IsValid() && StartUtc.Offset == TimeSpan.Zero &&
        EndUtc.Offset == TimeSpan.Zero && StartUtc >= ReportingPeriod.StartUtc &&
        EndUtc <= ReportingPeriod.EndUtc && EndUtc > StartUtc &&
        EndUtc.UtcTicks <= DateTimeOffset.MaxValue.UtcTicks - Policy.ProcessingSettleAllowance.Ticks &&
        EarliestFinalUtc.Offset == TimeSpan.Zero && EarliestFinalUtc == EndUtc + Policy.ProcessingSettleAllowance &&
        (Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay
            ? StartUtc == ReportingPeriod.StartUtc && EndUtc == ReportingPeriod.EndUtc &&
              DailyFinality is not null && DailyFinality.IsValid() && DailyFinality.Period == ReportingPeriod &&
              DailyFinality.ProcessingSettleAllowance == Policy.ProcessingSettleAllowance &&
              DailyFinality.EarliestFinalUtc == EarliestFinalUtc
            : DailyFinality is null) &&
        string.Equals(IdentitySha256, ComputeIdentity(this), StringComparison.OrdinalIgnoreCase);

    public bool IsEligibleForFinal(DateTimeOffset utc) => IsValid() && utc >= EarliestFinalUtc;

    /// <summary>Selects actual source observations without shortening the planned span or synthesizing pixels.</summary>
    public bool AcceptsSource(DateTimeOffset sourceUtc, IPlanetEphemeris ephemeris)
    {
        ArgumentNullException.ThrowIfNull(ephemeris);
        if (!IsValid() || sourceUtc < StartUtc || sourceUtc >= EndUtc)
        {
            return false;
        }
        return Policy.Selection == LocalAutomationSourceSelection.AllActualSources ||
            SolarAltitudeClassifier.DirectionAt(ephemeris, sourceUtc, ReportingPeriod.Site.LatitudeDegrees,
                ReportingPeriod.Site.LongitudeDegrees).AltitudeDegrees <= -18;
    }

    internal static LocalAutomationSourceWindow Create(LocalAutomationSourceWindowPolicy policy,
        SunriseReportingPeriod reportingPeriod, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var value = new LocalAutomationSourceWindow(CurrentVersion, policy, reportingPeriod, startUtc, endUtc,
            endUtc + policy.ProcessingSettleAllowance,
            policy.Kind == LocalAutomationSourceWindowKind.SunriseDay
                ? reportingPeriod.BindFinality(policy.ProcessingSettleAllowance) : null, string.Empty);
        value = value with { IdentitySha256 = ComputeIdentity(value) };
        return value.IsValid() ? value : throw new ArgumentException("The planned source window is invalid.");
    }

    private static string ComputeIdentity(LocalAutomationSourceWindow value) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            value.ContractVersion,
            value.Policy,
            ReportingPeriodIdentity = value.ReportingPeriod.IdentitySha256,
            value.StartUtc,
            value.EndUtc,
            value.EarliestFinalUtc,
            DailyFinalityIdentity = value.DailyFinality?.IdentitySha256,
            DarkNightSelectionVersion
        });
}

/// <summary>Immutable adapter inputs. Restart/retry consumes this value rather than current time or settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalAutomationOccurrence(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] LocalAutomationDefinition Definition,
    [property: JsonRequired] long DefinitionVersion,
    [property: JsonRequired] string RevisionSha256,
    [property: JsonRequired] string RunKey,
    [property: JsonRequired] DateTimeOffset ScheduledForUtc,
    [property: JsonRequired] LocalAutomationSourceWindow? SourceWindow,
    [property: JsonRequired] string IdentitySha256)
{
    public const string CurrentVersion = "hvo-local-automation-occurrence-v1";

    public bool IsValid() => ContractVersion == CurrentVersion && Definition is not null &&
        LocalAutomationDefinitionValidator.Validate(Definition) is null && DefinitionVersion > 0 &&
        RevisionSha256 == LocalAutomationContract.ComputeRevisionSha256(Definition) &&
        !string.IsNullOrWhiteSpace(RunKey) && RunKey.Length <= 128 && ScheduledForUtc.Offset == TimeSpan.Zero &&
        (Definition.SourceWindow is null
            ? SourceWindow is null && Definition.TriggerKind != LocalAutomationTriggerKind.SourceWindowClosed
            : SourceWindow is not null && SourceWindow.IsValid() && SourceWindow.Policy == Definition.SourceWindow &&
              ScheduledForUtc == SourceWindow.EarliestFinalUtc) &&
        string.Equals(IdentitySha256, ComputeIdentity(this), StringComparison.OrdinalIgnoreCase);

    internal static LocalAutomationOccurrence Create(LocalAutomationRunnerEntry entry, string runKey,
        DateTimeOffset scheduledForUtc, LocalAutomationSourceWindow? sourceWindow = null)
    {
        var value = new LocalAutomationOccurrence(CurrentVersion, entry.Definition, entry.Version,
            entry.RevisionSha256, runKey, scheduledForUtc, sourceWindow, string.Empty);
        value = value with { IdentitySha256 = ComputeIdentity(value) };
        return value.IsValid() ? value : throw new ArgumentException("The automation occurrence is invalid.");
    }

    internal static string ComputeIdentity(LocalAutomationRunnerEntry entry, string runKey,
        DateTimeOffset scheduledForUtc, LocalAutomationSourceWindow sourceWindow)
        => ComputeIdentity(new LocalAutomationOccurrence(CurrentVersion, entry.Definition, entry.Version,
            entry.RevisionSha256, runKey, scheduledForUtc, sourceWindow, string.Empty));

    private static string ComputeIdentity(LocalAutomationOccurrence value) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            value.ContractVersion,
            value.DefinitionVersion,
            value.RevisionSha256,
            value.Definition.DefinitionId,
            value.RunKey,
            value.ScheduledForUtc,
            SourceWindowIdentity = value.SourceWindow?.IdentitySha256
        });
}
