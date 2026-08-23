using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal sealed class OwnerPasswordReplacementService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager)
{
    internal async Task<IdentityResult> ReplaceAsync(
        ApplicationUser owner,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordMustChange",
                Description = "The new password must be different from the current password."
            });
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var passwordResult = await userManager.ChangePasswordAsync(owner, currentPassword, newPassword)
            .ConfigureAwait(false);
        if (!passwordResult.Succeeded)
        {
            return passwordResult;
        }

        owner.PasswordChangeRequired = false;
        try
        {
            var updateResult = await userManager.UpdateAsync(owner).ConfigureAwait(false);
            if (!updateResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await dbContext.Entry(owner).ReloadAsync(cancellationToken).ConfigureAwait(false);
                return updateResult;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return IdentityResult.Success;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await dbContext.Entry(owner).ReloadAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
