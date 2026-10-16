using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal sealed class CameraAgentIdentitySeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<CameraAgentIdentitySeeder> logger,
    IOptions<LocalIdentityOptions> identityOptions)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<CameraAgentIdentitySeeder> _logger = logger;
    private readonly LocalIdentityOptions _options = identityOptions.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var scopedProvider = scope.ServiceProvider;
        var dbContext = scopedProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var userManager = scopedProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(_options.AdminEmail);
        if (user is null)
        {
            if (string.IsNullOrEmpty(_options.AdminPassword))
            {
                throw new InvalidOperationException("The initial site owner password is required until local identity has been seeded.");
            }
            user = new ApplicationUser
            {
                UserName = _options.AdminUserName,
                Email = _options.AdminEmail,
                EmailConfirmed = true,
                IsSiteOwner = true,
                PasswordChangeRequired = true
            };

            var createResult = await CreateOwnerAsync(userManager, user, _options.AdminPassword).ConfigureAwait(false);
            if (!createResult.Succeeded)
            {
                if (createResult.Errors.Any(static error => error.Code == nameof(IdentityErrorDescriber.DuplicateUserName)))
                {
                    _logger.LogError("Failed to create default admin user: the configured owner user name is already in use");
                    throw new InvalidOperationException("The configured owner user name is already used by another local account.");
                }

                var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                _logger.LogError("Failed to create default admin user: {Errors}", errors);
                throw new InvalidOperationException("Could not seed default admin user");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    new EventId(4180, "OwnerTemporaryPasswordSeeded"),
                    "Seeded the local owner with a temporary password; password replacement is required");
            }
        }

        // The email is the durable owner key and is kept aligned with configuration. The user name is not: an owner
        // seeded before user names existed signs in with its email user name, and renaming it on upgrade would
        // silently change how that owner signs in.
        var needsUpdate = false;

        if (!string.Equals(user.Email, _options.AdminEmail, StringComparison.OrdinalIgnoreCase))
        {
            user.Email = _options.AdminEmail;
            needsUpdate = true;
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            needsUpdate = true;
        }

        if (!user.IsSiteOwner)
        {
            user.IsSiteOwner = true;
            needsUpdate = true;
        }

        if (!user.LockoutEnabled)
        {
            user.LockoutEnabled = true;
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            var updateResult = await userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                var errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                _logger.LogError("Failed to update default admin account metadata: {Errors}", errors);
                throw new InvalidOperationException("Could not align default admin account");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Aligned local owner account metadata with configured identity");
            }
        }

        if (user.PasswordChangeRequired &&
            !string.IsNullOrEmpty(_options.AdminPassword) &&
            !await userManager.CheckPasswordAsync(user, _options.AdminPassword))
        {
            _logger.LogError(
                new EventId(4181, "OwnerTemporaryPasswordMismatch"),
                "The configured temporary owner password does not match durable bootstrap state");
            throw new InvalidOperationException("The configured temporary owner password does not match the seeded owner.");
        }

        var staleOwners = await userManager.Users
            .Where(candidate => candidate.IsSiteOwner && candidate.Id != user.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var staleOwner in staleOwners)
        {
            staleOwner.IsSiteOwner = false;
            var demoteResult = await userManager.UpdateAsync(staleOwner);
            if (!demoteResult.Succeeded)
            {
                var errors = string.Join(", ", demoteResult.Errors.Select(e => e.Description));
                _logger.LogError("Failed to demote stale site owner: {Errors}", errors);
                throw new InvalidOperationException("Could not reconcile stale site owner");
            }
        }

        if (staleOwners.Count > 0 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Demoted {StaleOwnerCount} stale site owner accounts while reconciling local ownership",
                staleOwners.Count);
        }
    }

    /// <summary>
    /// Creates the owner with its temporary password. The documented default is refused by the password policy as a
    /// replacement, so it is the one value hashed here without passing through the password validators; the owner
    /// is created with <see cref="ApplicationUser.PasswordChangeRequired"/> set, so the default only ever reaches the
    /// forced replacement. Any other configured password must satisfy the policy.
    /// </summary>
    private static Task<IdentityResult> CreateOwnerAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser user,
        string password)
    {
        if (!CameraAgentIdentityPolicy.IsDefaultOwnerPassword(password))
        {
            return userManager.CreateAsync(user, password);
        }

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, password);
        return userManager.CreateAsync(user);
    }
}
