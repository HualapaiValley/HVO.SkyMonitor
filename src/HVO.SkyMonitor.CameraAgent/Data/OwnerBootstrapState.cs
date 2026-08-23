using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal static class OwnerBootstrapStates
{
    internal const string Uninitialized = "owner-uninitialized";
    internal const string TemporaryPassword = "owner-temporary-password";
    internal const string PasswordChangeRequired = "owner-password-change-required";
    internal const string Ready = "owner-ready";
    internal const string RecoveryRequired = "owner-recovery-required";
}

internal sealed class OwnerBootstrapStateReader(
    ApplicationDbContext dbContext,
    ILookupNormalizer normalizer,
    IOptions<LocalIdentityOptions> options)
{
    internal async Task<string> GetStateAsync(CancellationToken cancellationToken)
    {
        var owners = await dbContext.Users
            .AsNoTracking()
            .Where(static user => user.IsSiteOwner)
            .OrderBy(static user => user.Id)
            .Select(static user => new { user.NormalizedEmail, user.PasswordChangeRequired })
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (owners.Count == 0)
        {
            return OwnerBootstrapStates.Uninitialized;
        }

        var configuredEmail = normalizer.NormalizeEmail(options.Value.AdminEmail);
        if (owners.Count != 1 || !string.Equals(owners[0].NormalizedEmail, configuredEmail, StringComparison.Ordinal))
        {
            return OwnerBootstrapStates.RecoveryRequired;
        }

        if (!owners[0].PasswordChangeRequired)
        {
            return OwnerBootstrapStates.Ready;
        }

        return string.IsNullOrEmpty(options.Value.AdminPassword)
            ? OwnerBootstrapStates.PasswordChangeRequired
            : OwnerBootstrapStates.TemporaryPassword;
    }
}
