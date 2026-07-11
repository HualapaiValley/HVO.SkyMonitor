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
            user = new ApplicationUser
            {
                UserName = _options.AdminEmail,
                Email = _options.AdminEmail,
                EmailConfirmed = true,
                IsSiteOwner = true
            };

            var createResult = await userManager.CreateAsync(user, _options.AdminPassword);
            if (!createResult.Succeeded)
            {
                var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                _logger.LogError("Failed to create default admin user: {Errors}", errors);
                throw new InvalidOperationException("Could not seed default admin user");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Seeded default admin account {Email}", _options.AdminEmail);
            }
            return;
        }

        var needsUpdate = false;

        if (!string.Equals(user.UserName, _options.AdminEmail, StringComparison.OrdinalIgnoreCase))
        {
            user.UserName = _options.AdminEmail;
            needsUpdate = true;
        }

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
                _logger.LogInformation("Aligned default admin account to use email {Email} as username", _options.AdminEmail);
            }
        }

        if (!await userManager.CheckPasswordAsync(user, _options.AdminPassword))
        {
            if (await userManager.HasPasswordAsync(user))
            {
                await userManager.RemovePasswordAsync(user);
            }

            var resetResult = await userManager.AddPasswordAsync(user, _options.AdminPassword);
            if (!resetResult.Succeeded)
            {
                var errors = string.Join(", ", resetResult.Errors.Select(e => e.Description));
                _logger.LogError("Failed to reset default admin password: {Errors}", errors);
                throw new InvalidOperationException("Could not update default admin password");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Reset password for default admin account {Email}", _options.AdminEmail);
            }
        }
    }
}
