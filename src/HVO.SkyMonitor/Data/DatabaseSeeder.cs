using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Seeds the database with initial data for development and production.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>
    /// Seeds the database with default accounts if they don't exist.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Seed admin user account
        await SeedAdminUserAsync(userManager, logger);

        // Seed system service account
        await SeedSystemAccountAsync(userManager, logger);
    }

    private static async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager, ILogger logger)
    {
        const string adminEmail = "admin@skymonitor.local";
        const string adminUsername = "admin";

        var existingUser = await userManager.FindByEmailAsync(adminEmail);
        if (existingUser != null)
        {
            logger.LogInformation("Admin user already exists: {Email}", adminEmail);
            return;
        }

        var adminUser = new ApplicationUser
        {
            UserName = adminUsername,
            Email = adminEmail,
            EmailConfirmed = true,
            AccountType = AccountType.User
        };

        // In development, use a default password
        // In production, this should be set via environment variables or secure configuration
        var defaultPassword = Environment.GetEnvironmentVariable("ADMIN_DEFAULT_PASSWORD") ?? "Admin@123456";
        
        var result = await userManager.CreateAsync(adminUser, defaultPassword);
        
        if (result.Succeeded)
        {
            logger.LogInformation("Admin user created successfully: {Email}", adminEmail);
            logger.LogWarning("SECURITY: Default admin password is in use. Change it immediately in production!");
        }
        else
        {
            logger.LogError("Failed to create admin user: {Errors}", string.Join(", ", result.Errors.Select(e => e.Description)));
        }
    }

    private static async Task SeedSystemAccountAsync(UserManager<ApplicationUser> userManager, ILogger logger)
    {
        const string systemEmail = "system@skymonitor.local";
        const string systemUsername = "system-service";

        var existingAccount = await userManager.FindByEmailAsync(systemEmail);
        if (existingAccount != null)
        {
            logger.LogInformation("System service account already exists: {Email}", systemEmail);
            return;
        }

        var systemAccount = new ApplicationUser
        {
            UserName = systemUsername,
            Email = systemEmail,
            EmailConfirmed = true,
            AccountType = AccountType.System,
            // System accounts should not have passwords - they use API keys or client credentials
            PasswordHash = null
        };

        // Create without password since system accounts don't use password authentication
        var result = await userManager.CreateAsync(systemAccount);
        
        if (result.Succeeded)
        {
            logger.LogInformation("System service account created successfully: {Email}", systemEmail);
            logger.LogInformation("System account uses API key authentication only - no password authentication");
        }
        else
        {
            logger.LogError("Failed to create system account: {Errors}", string.Join(", ", result.Errors.Select(e => e.Description)));
        }
    }
}
