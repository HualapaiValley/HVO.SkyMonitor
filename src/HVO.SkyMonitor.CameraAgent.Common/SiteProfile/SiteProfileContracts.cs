using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.SiteProfile;

/// <summary>Bounds for the operator-maintained site profile.</summary>
public static class SiteProfileLimits
{
    /// <summary>The longest observatory, camera, or owner name the profile accepts.</summary>
    public const int MaximumNameLength = 80;

    /// <summary>The longest owner contact line (an email address, telephone number, or both).</summary>
    public const int MaximumContactLength = 200;

    public const int MaximumActorLength = 128;

    public const string InvalidCommandReasonCode = "siteProfile.invalidCommand";

    public const string InvalidFieldReasonCode = "siteProfile.invalidField";

    public const string ExpectedVersionConflictReasonCode = "siteProfile.expectedVersionConflict";

    /// <summary>This host loads no operator settings file, so the profile cannot be saved.</summary>
    public const string UnavailableReasonCode = "siteProfile.unavailable";

    /// <summary>The operator settings file is not valid JSON, so it is left for the operator to correct.</summary>
    public const string UnreadableReasonCode = "siteProfile.unreadable";
}

/// <summary>
/// The operator-facing identity of this deployment: what the observatory and camera are called and who to contact
/// about them. It is descriptive only; nothing about capture, geometry, or LogicHost registration reads it.
/// </summary>
public sealed record SiteProfileValues(
    string? ObservatoryName,
    string? CameraName,
    string? OwnerName,
    string? OwnerContact)
{
    public static SiteProfileValues Empty { get; } = new(null, null, null, null);
}

/// <summary>The current profile and the name the workspace shows for this camera.</summary>
/// <param name="Version">The operator settings file version a save echoes, so a hand edit is never overwritten.</param>
/// <param name="EffectiveCameraName">
/// The saved camera name; before the settings file has a site section, the installer-configured display name.
/// </param>
/// <param name="SettingsFilePath">The settings file that holds the profile, or null when this host loads none.</param>
/// <param name="Problem">Why the profile cannot be saved right now, or null when it can.</param>
public sealed record SiteProfileState(
    string Version,
    SiteProfileValues Profile,
    string? EffectiveCameraName,
    string? SettingsFilePath,
    string? Problem);

/// <summary>One operator request to replace the site profile.</summary>
public sealed record SiteProfileRequest(
    SiteProfileValues Profile,
    string ExpectedVersion,
    string Actor);

[JsonConverter(typeof(JsonStringEnumConverter<SiteProfileStatus>))]
public enum SiteProfileStatus
{
    /// <summary>The profile was saved to the operator settings file.</summary>
    Applied,

    /// <summary>The requested profile already matches the saved one; nothing changed.</summary>
    Unchanged,

    /// <summary>The settings file changed since the expected version was read.</summary>
    Conflict,

    /// <summary>The command failed validation, or the file cannot be saved; nothing changed.</summary>
    Invalid
}

public sealed record SiteProfileResult(
    SiteProfileStatus Status,
    string? ReasonCode,
    string? FieldPath,
    SiteProfileState State);
