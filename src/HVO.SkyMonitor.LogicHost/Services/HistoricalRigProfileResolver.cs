using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class HistoricalRigProfileResolver
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task<DeviceRigProfile?> ResolveAsync(
        ApplicationDbContext db,
        Guid devicePublicId,
        ProfileIdentityDescriptor identity,
        DateTimeOffset exposureStartedUtc,
        CancellationToken cancellationToken)
    {
        var sha256 = identity.Sha256.ToUpperInvariant();
        var candidates = await db.DeviceRigProfiles.Where(profile =>
                profile.DevicePublicId == devicePublicId
                && profile.ProfileName == identity.Name
                && profile.ProfileVersion == identity.Version
                && profile.ProfileSha256 == sha256)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            var legacyProfiles = await db.DeviceRigProfiles.Where(profile =>
                    profile.DevicePublicId == devicePublicId && profile.ProfileSha256 == null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var profile in legacyProfiles)
            {
                var resolved = TryResolveIdentity(profile.ConfigJson);
                if (resolved is null)
                {
                    continue;
                }
                profile.ProfileName = resolved.Name;
                profile.ProfileVersion = resolved.Version;
                profile.ProfileSha256 = resolved.Sha256;
                if (resolved.Name == identity.Name && resolved.Version == identity.Version
                    && string.Equals(resolved.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(profile);
                }
            }
        }
        return candidates
            .Where(profile => profile.EffectiveFromUtc <= exposureStartedUtc)
            .OrderByDescending(profile => profile.EffectiveFromUtc)
            .FirstOrDefault()
            ?? candidates.OrderBy(profile => profile.EffectiveFromUtc).FirstOrDefault();
    }

    internal static ProfileIdentityDescriptor? TryResolveIdentity(string json)
    {
        try
        {
            var rig = JsonSerializer.Deserialize<CameraRigConfig>(json, SerializerOptions);
            return rig is null
                ? null
                : new ProfileIdentityDescriptor("rig", rig.ProfileVersion, CameraRigProfileIdentity.ComputeSha256(rig));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
