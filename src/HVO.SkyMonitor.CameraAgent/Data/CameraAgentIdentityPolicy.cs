using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.Data;

/// <summary>
/// The local owner's password and sign-in policy. A new CameraAgent owner is seeded with the documented first
/// sign-in <c>admin</c> / <see cref="DefaultOwnerPassword"/> and must replace it before ordinary use; every
/// password that is set afterwards needs at least <see cref="MinimumPasswordLength"/> characters, has no
/// character-class rules, and may not be the documented default, the user name, or the current password.
/// Repeated failed sign-ins lock the account for <see cref="LockoutDuration"/>.
/// </summary>
internal static class CameraAgentIdentityPolicy
{
    /// <summary>
    /// The documented first-sign-in password. It is accepted only as the seeded temporary credential, which is
    /// always marked for replacement; <see cref="CameraAgentPasswordValidator"/> refuses it everywhere else.
    /// </summary>
    internal const string DefaultOwnerPassword = "skymonitor";

    internal const int MinimumPasswordLength = 8;

    /// <summary>The longest temporary password owner recovery accepts.</summary>
    internal const int MaximumRecoveryPasswordLength = 256;

    internal const int MaximumFailedSignInAttempts = 5;

    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    /// <summary>Applies the relaxed password rules and sign-in lockout and adds the CameraAgent password validator.</summary>
    internal static IdentityBuilder AddCameraAgentPasswordPolicy(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<IdentityOptions>(Configure);
        return builder.AddPasswordValidator<CameraAgentPasswordValidator>();
    }

    internal static void Configure(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Password.RequiredLength = MinimumPasswordLength;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaximumFailedSignInAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
    }

    /// <summary>Whether <paramref name="password"/> is exactly the documented first-sign-in password.</summary>
    internal static bool IsDefaultOwnerPassword(string? password)
        => string.Equals(password, DefaultOwnerPassword, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the account a sign-in names. A value containing <c>@</c> is an email address; any other value is a
    /// user name. Surrounding whitespace is ignored. Returns <see langword="null"/> when no account matches.
    /// </summary>
    internal static async Task<ApplicationUser?> FindSignInUserAsync(
        UserManager<ApplicationUser> users,
        string? userNameOrEmail)
    {
        ArgumentNullException.ThrowIfNull(users);
        var value = userNameOrEmail?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Contains('@', StringComparison.Ordinal)
            ? await users.FindByEmailAsync(value).ConfigureAwait(false)
            : await users.FindByNameAsync(value).ConfigureAwait(false);
    }
}

/// <summary>
/// Refuses a new password that is the documented default (in any letter case), the account's user name (in any
/// letter case), or the account's current password. Length is enforced by the Identity password options.
/// </summary>
internal sealed class CameraAgentPasswordValidator : IPasswordValidator<ApplicationUser>
{
    internal const string DefaultPasswordCode = "PasswordIsDefault";
    internal const string UserNamePasswordCode = "PasswordMatchesUserName";
    internal const string CurrentPasswordCode = "PasswordMustChange";

    public Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string? password)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrEmpty(password))
        {
            // The Identity length rule reports an empty password.
            return Task.FromResult(IdentityResult.Success);
        }

        var errors = new List<IdentityError>();
        if (string.Equals(password, CameraAgentIdentityPolicy.DefaultOwnerPassword, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new IdentityError
            {
                Code = DefaultPasswordCode,
                Description = "The new password cannot be the default password."
            });
        }

        if (!string.IsNullOrEmpty(user.UserName) &&
            string.Equals(password, user.UserName, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new IdentityError
            {
                Code = UserNamePasswordCode,
                Description = "The new password cannot be the user name."
            });
        }

        // Setting, changing, resetting, and recovering a password all validate the new value while the account still
        // holds its current hash, so one check here covers every path that replaces a password.
        if (!string.IsNullOrEmpty(user.PasswordHash) &&
            manager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed)
        {
            errors.Add(new IdentityError
            {
                Code = CurrentPasswordCode,
                Description = "The new password must be different from the current password."
            });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]));
    }
}
