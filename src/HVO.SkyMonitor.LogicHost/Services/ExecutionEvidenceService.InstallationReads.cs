using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ExecutionEvidenceService
{
    internal const int InstallationBindingLimit = 16;

    public async Task<ExecutionEvidenceInstallationBindingsView?> ListInstallationBindingsAsync(
        Guid observatoryId, Guid logicalCameraId, Guid installationId, string userId, Guid? observatoryScope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (observatoryScope is { } scope && scope != observatoryId) { return null; }
        // Resolve the retained central installation and authorize the entire route before selecting bindings.
        // Retirement and registration status do not change historical read authority.
        var registrationId = await _db.LogicalCameraInstallations.AsNoTracking()
            .Where(i => i.Id == installationId && i.LogicalCameraId == logicalCameraId &&
                i.LogicalCamera!.ObservatoryId == observatoryId &&
                ObservatoryMembershipAccess.ForUser(_db, userId).Any(m => m.ObservatoryId == observatoryId))
            .Select(i => (Guid?)i.RegistrationId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (registrationId is null) { return null; }
        // RegistrationId leads the existing (RegistrationId, Id) index. One sentinel proves truncation.
        var bindings = await _db.Set<ExecutionEvidenceBinding>().AsNoTracking()
            .Where(b => b.RegistrationId == registrationId.Value && b.ObservatoryId == observatoryId &&
                b.LogicalCameraInstallationId == installationId)
            .OrderBy(b => b.Id).Take(InstallationBindingLimit + 1)
            .Select(b => new ExecutionEvidenceBindingView(b.Id, b.ObservatoryId, b.RegistrationId,
                b.LogicalCameraInstallationId, b.InstallationPublicId, b.OriginInstallationId, b.AgentInstanceId, b.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(bindings.Take(InstallationBindingLimit).ToArray(), bindings.Count > InstallationBindingLimit);
    }
}

internal sealed record ExecutionEvidenceInstallationBindingsView(
    IReadOnlyList<ExecutionEvidenceBindingView> Bindings, bool Truncated);
