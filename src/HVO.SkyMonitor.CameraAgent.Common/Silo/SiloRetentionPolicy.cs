using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloRoleRetention(string Role, TimeSpan? MaximumAge = null, bool ExpireUnderPressure = false, bool AllowLastCopyExpiry = false)
{
    internal void Validate()
    {
        SiloText.RequireToken(Role, nameof(Role), 128);
        if (MaximumAge is { } age && (age < TimeSpan.Zero || age > TimeSpan.FromDays(36500)))
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
    }
}

internal sealed record SiloRetentionPolicy(bool ExpiryHeld, IReadOnlyList<SiloRoleRetention> Roles)
{
    internal const string PolicyId = "tier-retention-v1";
    internal static SiloRetentionPolicy Held { get; } = new(true, []);

    internal SiloPolicyRecord ToRecord()
    {
        Validate();
        return new(PolicyId, JsonSerializer.SerializeToElement(this, SiloJson.Options));
    }

    internal static SiloRetentionPolicy Read(string json)
    {
        var policy = JsonSerializer.Deserialize<SiloRetentionPolicy>(json, SiloJson.Options)
            ?? throw new InvalidDataException("The Silo retention policy is empty.");
        policy.Validate();
        return policy;
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Roles);
        if (Roles.Count > 128 || Roles.Any(static role => role is null) ||
            Roles.Select(static role => role.Role).Distinct(StringComparer.Ordinal).Count() != Roles.Count)
            throw new ArgumentException("Each Silo role may have only one retention policy.", nameof(Roles));
        foreach (var role in Roles) role.Validate();
    }
}

internal enum SiloHoldKind
{
    ProcessingInput, CalibrationMaster, TimeLapseInput, ManualPin, RecentBuffer, StackContributor, Recovery, RestoreOrReplay, NightlyInput,
    SatelliteArchiveReference, ViewManifest, ProjectedScene
}

/// <summary>Logical holds follow matching published versions. A null revision holds all revisions of the artifact.</summary>
internal sealed record SiloRetentionHold(SiloHoldKind Kind, string ArtifactId, string? Revision = null,
    DateTimeOffset? ExpiresUtc = null, SiloIdentityKind? IdentityKind = null)
{
    internal bool Matches(SiloObjectIdentity identity, DateTimeOffset now) =>
        (IdentityKind is null || IdentityKind == identity.Kind) && identity.Id == ArtifactId && (Revision is null || Revision == identity.Revision) &&
        (ExpiresUtc is null || ExpiresUtc > now);
}

internal sealed record SiloRetentionReference(SiloObjectIdentity Identity, string? Role, DateTimeOffset? PublishedUtc,
    IReadOnlyList<SiloExactLocator> Copies);

internal sealed record SiloRetentionCandidate(SiloExactLocator Locator, IReadOnlyList<SiloRetentionReference> References,
    string Decision, bool AuthorizesRoleExpiry = false);
