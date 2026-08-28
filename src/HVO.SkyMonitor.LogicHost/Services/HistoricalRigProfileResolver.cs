using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class HistoricalRigProfileResolver
{
    private const string OrdinalCollation = "Latin1_General_100_BIN2";

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
                && EF.Functions.Collate(profile.ProfileName, OrdinalCollation) == identity.Name
                && EF.Functions.Collate(profile.ProfileVersion, OrdinalCollation) == identity.Version
                && EF.Functions.Collate(profile.ProfileSha256, OrdinalCollation) == sha256)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return candidates
            .Where(profile => profile.EffectiveFromUtc <= exposureStartedUtc)
            .OrderByDescending(profile => profile.EffectiveFromUtc)
            .FirstOrDefault()
            ?? candidates.OrderBy(profile => profile.EffectiveFromUtc).FirstOrDefault();
    }
}
