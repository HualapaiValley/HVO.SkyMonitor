using System.Text.Json;
using System.Text.Json.Serialization;

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

internal sealed record SiloCaptureCondition(string? WindowId = null, string? SkyCondition = null)
{
    internal void Validate()
    {
        if (WindowId is not null) SiloText.RequireText(WindowId, nameof(WindowId), 256);
        if (SkyCondition is not null && !IsSolarCondition(SkyCondition))
            throw new ArgumentException("Expected day, twilight or night.", nameof(SkyCondition));
    }

    internal static bool IsSolarCondition(string value) =>
        value.Equals("day", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("twilight", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("night", StringComparison.OrdinalIgnoreCase);
}

internal sealed record SiloCaptureVariantRetention(string Role, [property: JsonRequired] TimeSpan MaximumAge,
    string? MediaType = null, string? Variant = null);

internal sealed record SiloCaptureRetentionProfile(string Name, IReadOnlyList<SiloCaptureVariantRetention> Variants,
    IReadOnlyList<string>? WindowIds = null, IReadOnlyList<string>? SkyConditions = null);

internal sealed record SiloRetentionPolicy(bool ExpiryHeld, IReadOnlyList<SiloRoleRetention> Roles,
    IReadOnlyList<SiloCaptureRetentionProfile>? CaptureProfiles = null)
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
        if (CaptureProfiles is null) return;
        if (CaptureProfiles.Count > 32)
            throw new ArgumentException("Too many capture retention profiles.", nameof(CaptureProfiles));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windows = new HashSet<string>(StringComparer.Ordinal);
        var conditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in CaptureProfiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            SiloText.RequireToken(profile.Name, nameof(profile.Name), 64);
            if (!names.Add(profile.Name) || (profile.WindowIds?.Count ?? 0) + (profile.SkyConditions?.Count ?? 0) is < 1 or > 64)
                throw new ArgumentException("A capture profile needs a unique name and at least one selector.", nameof(CaptureProfiles));
            foreach (var window in profile.WindowIds ?? [])
            {
                SiloText.RequireText(window, nameof(profile.WindowIds), 256);
                if (!windows.Add(window))
                    throw new ArgumentException("Each schedule window can select only one capture profile.", nameof(CaptureProfiles));
            }
            foreach (var condition in profile.SkyConditions ?? [])
            {
                if (condition is null || !SiloCaptureCondition.IsSolarCondition(condition) || !conditions.Add(condition))
                    throw new ArgumentException("Each supported sky condition can select only one capture profile.", nameof(CaptureProfiles));
            }
            ArgumentNullException.ThrowIfNull(profile.Variants);
            if (profile.Variants.Count is < 1 or > 32)
                throw new ArgumentException("A capture profile needs between one and 32 variant rules.", nameof(CaptureProfiles));
            var variants = new HashSet<(string Role, string? MediaType, string? Variant)>();
            foreach (var variant in profile.Variants)
            {
                ArgumentNullException.ThrowIfNull(variant);
                new SiloRoleRetention(variant.Role, variant.MaximumAge).Validate();
                if (variant.MediaType is not null) SiloText.RequireText(variant.MediaType, nameof(variant.MediaType), 128);
                if (variant.Variant is not null) SiloText.RequireText(variant.Variant, nameof(variant.Variant), 256);
                if (!variants.Add((variant.Role.ToUpperInvariant(), variant.MediaType?.ToUpperInvariant(), variant.Variant)))
                    throw new ArgumentException("Duplicate capture profile variant.", nameof(CaptureProfiles));
            }
        }
    }

    internal (SiloRoleRetention? Rule, string? Profile) FindCaptureRule(SiloRetentionReference reference)
    {
        if (CaptureProfiles is null || reference.Condition is not { } condition) return default;
        var profile = CaptureProfiles.FirstOrDefault(profile => condition.WindowId is { } window &&
                profile.WindowIds?.Contains(window, StringComparer.Ordinal) == true)
            ?? CaptureProfiles.FirstOrDefault(profile => condition.SkyCondition is { } sky &&
                profile.SkyConditions?.Contains(sky, StringComparer.OrdinalIgnoreCase) == true);
        if (profile is null) return default;

        var role = reference.GroupKind is "centered-view" or "centered-archive" ? reference.GroupKind : reference.Role;
        SiloCaptureVariantRetention? rule = null;
        var specificity = -1;
        foreach (var variant in profile.Variants)
        {
            if (!string.Equals(variant.Role, role, StringComparison.OrdinalIgnoreCase) ||
                variant.MediaType is not null && !string.Equals(variant.MediaType, reference.MediaType, StringComparison.OrdinalIgnoreCase) ||
                variant.Variant is not null && !string.Equals(variant.Variant, reference.Variant, StringComparison.Ordinal))
                continue;
            var rank = (variant.Variant is not null ? 2 : 0) + (variant.MediaType is not null ? 1 : 0);
            if (rank > specificity)
            {
                rule = variant;
                specificity = rank;
            }
        }
        return rule is null ? default : (new(rule.Role, rule.MaximumAge, AllowLastCopyExpiry: true), profile.Name);
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
    IReadOnlyList<SiloExactLocator> Copies, string? MediaType = null, string? GroupKind = null, SiloCaptureCondition? Condition = null,
    string? Variant = null);

internal sealed record SiloRetentionCandidate(SiloExactLocator Locator, IReadOnlyList<SiloRetentionReference> References,
    string Decision, bool AuthorizesRoleExpiry = false)
{
    internal long? OrphanLength { get; init; }
    internal IReadOnlyList<string?>? CaptureProfiles { get; init; }
}
