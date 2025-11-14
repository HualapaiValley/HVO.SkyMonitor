using HVO.SkyMonitor.CameraAgent.ZWO.Data;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Components.Account;

internal sealed class IdentityUserAccessor(UserManager<ApplicationUser> userManager, IdentityRedirectManager redirectManager)
{
    public async Task<ApplicationUser> GetRequiredUserAsync(HttpContext context)
    {
        var user = await userManager.GetUserAsync(context.User);

        if (user is null)
        {
            redirectManager.RedirectToWithStatus("Account/InvalidUser", $"Error: Unable to load user with ID '{userManager.GetUserId(context.User)}'.", context);
            throw new InvalidOperationException("Unable to retrieve the signed-in user.");
        }

        return user;
    }
}
