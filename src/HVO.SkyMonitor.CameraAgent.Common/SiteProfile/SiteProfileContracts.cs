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

    public const int MaximumIdempotencyKeyLength = 128;

    public const int MaximumReasonLength = 512;

    /// <summary>
    /// The most recent revisions the record retains. Older revisions are dropped so an authenticated caller
    /// cannot grow the record without bound; the idempotency replay window is the same retained set.
    /// </summary>
    public const int MaximumRetainedRevisions = 200;

    /// <summary>The newest revisions a state projection carries to the operator UI.</summary>
    public const int MaximumProjectedRevisions = 50;

    public const string InvalidCommandReasonCode = "siteProfile.invalidCommand";

    public const string InvalidFieldReasonCode = "siteProfile.invalidField";

    public const string ExpectedVersionConflictReasonCode = "siteProfile.expectedVersionConflict";

    public const string IdempotencyKeyConflictReasonCode = "siteProfile.idempotencyKeyConflict";
}

/// <summary>
/// The operator-facing identity of this deployment: what the observatory and camera are called and who to contact
/// about them. It is descriptive only; nothing about capture, geometry, or LogicHost registration reads it.
/// </summary>
public sealed record SiteProfileValues(
    [property: JsonRequired] string? ObservatoryName,
    [property: JsonRequired] string? CameraName,
    [property: JsonRequired] string? OwnerName,
    [property: JsonRequired] string? OwnerContact)
{
    public static SiteProfileValues Empty { get; } = new(null, null, null, null);
}

/// <summary>One accepted profile change. The stored values are the complete profile after the change.</summary>
public sealed record SiteProfileRevision(
    [property: JsonRequired] long Version,
    [property: JsonRequired] DateTimeOffset RecordedAtUtc,
    [property: JsonRequired] string Actor,
    [property: JsonRequired] string? Reason,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] SiteProfileValues Profile);

/// <summary>The current profile, the name the workspace shows for this camera, and the newest revisions.</summary>
/// <param name="Version">Zero until an operator records a profile; the concurrency token a command echoes.</param>
/// <param name="EffectiveCameraName">
/// The recorded camera name, else the installer-configured display name, else null.
/// </param>
public sealed record SiteProfileState(
    long Version,
    SiteProfileValues Profile,
    string? EffectiveCameraName,
    string? ConfiguredCameraName,
    DateTimeOffset? UpdatedAtUtc,
    string? UpdatedBy,
    IReadOnlyList<SiteProfileRevision> History);

/// <summary>One operator request to replace the site profile.</summary>
public sealed record SiteProfileRequest(
    SiteProfileValues Profile,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string? Reason);

[JsonConverter(typeof(JsonStringEnumConverter<SiteProfileStatus>))]
public enum SiteProfileStatus
{
    /// <summary>A new profile version was recorded.</summary>
    Applied,

    /// <summary>The same idempotency key and profile were already recorded; nothing changed.</summary>
    Replayed,

    /// <summary>The requested profile already matches the recorded one; nothing changed.</summary>
    Unchanged,

    /// <summary>The expected version was stale, or a key was replayed with a different profile.</summary>
    Conflict,

    /// <summary>The command failed validation and nothing durable changed.</summary>
    Invalid
}

public sealed record SiteProfileResult(
    SiteProfileStatus Status,
    string? ReasonCode,
    string? FieldPath,
    SiteProfileState State);
