using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Defines the supported local or solar event used by a schedule boundary.</summary>
public enum CaptureScheduleBoundaryKind
{
    FixedLocalTime,
    Sunrise,
    Sunset,
    CivilDawn,
    CivilDusk,
    NauticalDawn,
    NauticalDusk,
    AstronomicalDawn,
    AstronomicalDusk
}

/// <summary>Identifies the source that determined an expanded schedule interval.</summary>
public enum CaptureScheduleIntervalSource
{
    Blackout,
    ForceClosedOverride,
    ForceOpenOverride,
    DateExceptionWindow,
    DateExceptionClosed,
    WeeklyWindow,
    LegacyCompatibility
}

/// <summary>Identifies why schedule admission was allowed or denied.</summary>
public enum CaptureScheduleAdmissionReason
{
    SafetyUnavailable,
    ManualPause,
    Blackout,
    ForceClosedOverride,
    ForceOpenOverride,
    DateException,
    WeeklyWindow,
    LegacyCompatibility,
    NoSolarEvent,
    DefaultClosed
}

/// <summary>Defines a fixed or solar-relative local schedule boundary.</summary>
public sealed record CaptureScheduleBoundary(
    CaptureScheduleBoundaryKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeOnly? LocalTime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TimeSpan Offset = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int DayOffset = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeOnly? NoEventFallbackLocalTime = null)
{
    [JsonConstructor]
    public CaptureScheduleBoundary()
        : this(CaptureScheduleBoundaryKind.FixedLocalTime)
    {
    }
}

/// <summary>Defines exposure, gain, and minimum-start cadence defaults selected by a schedule window.</summary>
public sealed record CaptureScheduleSetpointProfile(
    string Id,
    TimeSpan Exposure,
    double Gain,
    TimeSpan CaptureInterval,
    CaptureCadenceMode CadenceMode = CaptureCadenceMode.MinimumStartInterval,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TargetFps = null);

/// <summary>Defines one recurring weekly window anchored to its start day.</summary>
public sealed record CaptureWeeklyScheduleWindow(
    string Id,
    DayOfWeek Day,
    CaptureScheduleBoundary Start,
    CaptureScheduleBoundary End,
    string SetpointProfileId);

/// <summary>Defines one open window on a specific local date.</summary>
public sealed record CaptureDateScheduleWindow(
    string Id,
    CaptureScheduleBoundary Start,
    CaptureScheduleBoundary End,
    string SetpointProfileId);

/// <summary>Replaces recurring windows for one local date, optionally closing the whole date.</summary>
public sealed record CaptureScheduleDateRule(
    string Id,
    DateOnly Date,
    bool ForceClosed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CaptureDateScheduleWindow>? Windows = null);

/// <summary>Defines a durable force-closed UTC interval.</summary>
public sealed record CaptureScheduleBlackout(
    string Id,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>Defines the versioned local schedule carried by a CameraAgent configuration revision.</summary>
public sealed record CaptureScheduleDefinition(
    string SchemaVersion,
    IReadOnlyList<CaptureScheduleSetpointProfile> SetpointProfiles,
    IReadOnlyList<CaptureWeeklyScheduleWindow> WeeklyWindows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CaptureScheduleDateRule>? DateExceptions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CaptureScheduleBlackout>? Blackouts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool LegacyAlwaysOpen = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LegacySetpointProfileId = null);

/// <summary>Binds a raw capture to the exact expanded schedule revision and admission interval.</summary>
public sealed record CaptureScheduleAdmissionEvidence(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ScheduleRevisionId,
    [property: JsonRequired] string ScheduleRevisionSha256,
    [property: JsonRequired] string LocalProfileSha256,
    [property: JsonRequired] string SetpointProfileId,
    [property: JsonRequired] CaptureScheduleAdmissionReason Reason,
    [property: JsonRequired] CaptureScheduleIntervalSource Source,
    [property: JsonRequired] DateTimeOffset DecisionUtc,
    [property: JsonRequired] DateTimeOffset EffectiveStartUtc,
    [property: JsonRequired] DateTimeOffset EffectiveEndUtc,
    [property: JsonRequired] string IntervalId,
    [property: JsonRequired] string ExpansionAlgorithmVersion,
    [property: JsonRequired] string ExpansionSha256,
    [property: JsonRequired] string TimeZoneRuleSha256,
    [property: JsonRequired] string DeploymentLocationId,
    [property: JsonRequired] long DeploymentLocationVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SolarAlgorithmVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OverrideId = null)
{
    public const string CurrentSchemaVersion = "capture-schedule-admission-v1";
}

/// <summary>Validates and identifies immutable schedule definitions without edge-runtime dependencies.</summary>
public static class CaptureScheduleContract
{
    public static CaptureContractValidationResult Validate(CaptureScheduleDefinition? definition)
    {
        if (definition is null ||
            !string.Equals(definition.SchemaVersion, "capture-schedule-v1", StringComparison.Ordinal))
        {
            return Failure("schedule.schema");
        }
        if (definition.SetpointProfiles is null || definition.WeeklyWindows is null ||
            definition.SetpointProfiles.Count == 0)
        {
            return Failure("schedule.definition");
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in definition.SetpointProfiles)
        {
            if (profile is null || !ValidId(profile.Id) || !profileIds.Add(profile.Id) ||
                profile.Exposure <= TimeSpan.Zero || !double.IsFinite(profile.Gain) || profile.Gain < 0 ||
                profile.CaptureInterval < TimeSpan.Zero || !Enum.IsDefined(profile.CadenceMode) ||
                profile.CadenceMode == CaptureCadenceMode.MinimumStartInterval && profile.CaptureInterval <= TimeSpan.Zero ||
                profile.TargetFps is { } fps && (!double.IsFinite(fps) || fps <= 0))
            {
                return Failure("schedule.setpointProfiles");
            }
        }
        if (definition.LegacyAlwaysOpen != !string.IsNullOrWhiteSpace(definition.LegacySetpointProfileId) ||
            definition.LegacyAlwaysOpen && !profileIds.Contains(definition.LegacySetpointProfileId!))
        {
            return Failure("schedule.legacySetpointProfileId");
        }

        var intervalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in definition.WeeklyWindows)
        {
            if (window is null ||
                !ValidWindow(window.Id, window.Start, window.End, window.SetpointProfileId, profileIds, intervalIds) ||
                !Enum.IsDefined(window.Day))
            {
                return Failure("schedule.weeklyWindows");
            }
        }
        var exceptionDates = new HashSet<DateOnly>();
        foreach (var rule in definition.DateExceptions ?? [])
        {
            if (rule is null || !ValidId(rule.Id) || !intervalIds.Add(rule.Id) || !exceptionDates.Add(rule.Date) ||
                rule.ForceClosed && (rule.Windows?.Count ?? 0) > 0)
            {
                return Failure("schedule.dateExceptions");
            }
            foreach (var window in rule.Windows ?? [])
            {
                if (window is null ||
                    !ValidWindow(window.Id, window.Start, window.End, window.SetpointProfileId, profileIds, intervalIds))
                {
                    return Failure("schedule.dateExceptions.windows");
                }
            }
        }
        foreach (var blackout in definition.Blackouts ?? [])
        {
            if (blackout is null || !ValidId(blackout.Id) || !intervalIds.Add(blackout.Id) ||
                blackout.StartUtc.Offset != TimeSpan.Zero || blackout.EndUtc.Offset != TimeSpan.Zero ||
                blackout.StartUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                blackout.EndUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                blackout.EndUtc <= blackout.StartUtc)
            {
                return Failure("schedule.blackouts");
            }
        }
        return CaptureContractValidationResult.Success;
    }

    public static CaptureContractValidationResult ValidateAdmissionEvidence(
        CaptureScheduleAdmissionEvidence? evidence)
    {
        if (evidence is null)
        {
            return CaptureContractValidationResult.Success;
        }
        if (!string.Equals(
                evidence.SchemaVersion,
                CaptureScheduleAdmissionEvidence.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure("scheduleAdmission.schemaVersion");
        }
        if (!ValidId(evidence.ScheduleRevisionId) || !ValidId(evidence.SetpointProfileId) ||
            !ValidId(evidence.IntervalId) || !ValidId(evidence.ExpansionAlgorithmVersion) ||
            !ValidId(evidence.DeploymentLocationId) || evidence.DeploymentLocationVersion < 1 ||
            !ValidSha256(evidence.ScheduleRevisionSha256) || !ValidSha256(evidence.LocalProfileSha256) ||
            !ValidSha256(evidence.ExpansionSha256) ||
            !ValidSha256(evidence.TimeZoneRuleSha256) ||
            evidence.SolarAlgorithmVersion is { } solarVersion && !ValidId(solarVersion) ||
            evidence.OverrideId is { } overrideId && !ValidId(overrideId))
        {
            return Failure("scheduleAdmission.identity");
        }
        if (!Enum.IsDefined(evidence.Reason) || !Enum.IsDefined(evidence.Source) ||
            evidence.DecisionUtc.Offset != TimeSpan.Zero || evidence.EffectiveStartUtc.Offset != TimeSpan.Zero ||
            evidence.EffectiveEndUtc.Offset != TimeSpan.Zero ||
            evidence.EffectiveEndUtc <= evidence.EffectiveStartUtc ||
            evidence.DecisionUtc < evidence.EffectiveStartUtc || evidence.DecisionUtc >= evidence.EffectiveEndUtc)
        {
            return Failure("scheduleAdmission.interval");
        }
        var provenanceMatches = evidence.Reason switch
        {
            CaptureScheduleAdmissionReason.ForceOpenOverride =>
                evidence.Source == CaptureScheduleIntervalSource.ForceOpenOverride && evidence.OverrideId is not null,
            CaptureScheduleAdmissionReason.DateException =>
                evidence.Source == CaptureScheduleIntervalSource.DateExceptionWindow && evidence.OverrideId is null,
            CaptureScheduleAdmissionReason.WeeklyWindow =>
                evidence.Source == CaptureScheduleIntervalSource.WeeklyWindow && evidence.OverrideId is null,
            CaptureScheduleAdmissionReason.LegacyCompatibility =>
                evidence.Source == CaptureScheduleIntervalSource.LegacyCompatibility && evidence.OverrideId is null,
            _ => false
        };
        return provenanceMatches
            ? CaptureContractValidationResult.Success
            : Failure("scheduleAdmission.provenance");
    }

    public static string ComputeSha256(CaptureScheduleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validation = Validate(definition);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The capture schedule is invalid ({validation.FieldPath}).",
                nameof(definition));
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            definition.SchemaVersion,
            SetpointProfiles = definition.SetpointProfiles.OrderBy(static item => item.Id, StringComparer.Ordinal),
            WeeklyWindows = definition.WeeklyWindows.OrderBy(static item => item.Id, StringComparer.Ordinal),
            DateExceptions = (definition.DateExceptions ?? []).OrderBy(static item => item.Id, StringComparer.Ordinal)
                .Select(static item => new
                {
                    item.Id,
                    item.Date,
                    item.ForceClosed,
                    Windows = (item.Windows ?? []).OrderBy(static window => window.Id, StringComparer.Ordinal)
                }),
            Blackouts = (definition.Blackouts ?? []).OrderBy(static item => item.Id, StringComparer.Ordinal),
            definition.LegacyAlwaysOpen,
            definition.LegacySetpointProfileId
        });
    }

    private static bool ValidWindow(
        string id,
        CaptureScheduleBoundary start,
        CaptureScheduleBoundary end,
        string profileId,
        HashSet<string> profileIds,
        HashSet<string> intervalIds)
        => ValidWindowId(id) && intervalIds.Add(id) && profileIds.Contains(profileId) &&
            ValidBoundary(start) && ValidBoundary(end);

    private static bool ValidBoundary(CaptureScheduleBoundary? boundary)
        => boundary is not null && Enum.IsDefined(boundary.Kind) && boundary.DayOffset is >= 0 and <= 1 &&
            boundary.Offset >= TimeSpan.FromHours(-24) && boundary.Offset <= TimeSpan.FromHours(24) &&
            boundary.Offset.Ticks % TimeSpan.TicksPerMillisecond == 0 &&
            (boundary.Kind == CaptureScheduleBoundaryKind.FixedLocalTime
                ? boundary.LocalTime.HasValue && !boundary.NoEventFallbackLocalTime.HasValue
                : !boundary.LocalTime.HasValue);

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static bool ValidWindowId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 117;

    private static bool ValidSha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static CaptureContractValidationResult Failure(string fieldPath)
        => CaptureContractValidationResult.Failure(CaptureContractReasonCodes.InvalidSchedule, fieldPath);
}
