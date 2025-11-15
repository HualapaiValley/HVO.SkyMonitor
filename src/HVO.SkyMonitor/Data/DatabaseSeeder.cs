using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Seeds the database with initial data for development and production.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>
    /// Seeds the database with default accounts, scopes, and OAuth2 clients.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Seed admin user account
        await SeedAdminUserAsync(userManager, logger);

        // Seed system service account
        await SeedSystemAccountAsync(userManager, logger);

        // Seed OpenIddict scopes and clients
        await SeedOpenIddictDataAsync(serviceProvider, logger);
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

    private static async Task SeedOpenIddictDataAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var scopeManager = serviceProvider.GetRequiredService<IOpenIddictScopeManager>();
        var applicationManager = serviceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        // Seed scopes
        await SeedScopesAsync(scopeManager, logger);

        // Seed OAuth2 applications/clients
        await SeedApplicationsAsync(applicationManager, logger);
    }

    private static async Task SeedScopesAsync(IOpenIddictScopeManager scopeManager, ILogger logger)
    {
        // Define scopes for the application
        var scopes = new[]
        {
            new { Name = "api", DisplayName = "API Access", Description = "Access to the HVO.SkyMonitor API" },
            new { Name = "camera", DisplayName = "Camera Control", Description = "Access to camera control endpoints" },
            new { Name = "admin", DisplayName = "Administrative Access", Description = "Full administrative access to all resources" }
        };

        foreach (var scope in scopes)
        {
            if (await scopeManager.FindByNameAsync(scope.Name) == null)
            {
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = scope.Name,
                    DisplayName = scope.DisplayName,
                    Description = scope.Description,
                    Resources = { "skymonitor_api" }
                });

                logger.LogInformation("Created scope: {ScopeName}", scope.Name);
            }
            else
            {
                logger.LogInformation("Scope already exists: {ScopeName}", scope.Name);
            }
        }
    }

    private static async Task SeedApplicationsAsync(IOpenIddictApplicationManager applicationManager, ILogger logger)
    {
        // Seed a test client for development
        const string testClientId = "test-client";
        
        if (await applicationManager.FindByClientIdAsync(testClientId) == null)
        {
            await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = testClientId,
                ClientSecret = "test-secret-change-in-production",
                DisplayName = "Test Client Application",
                ConsentType = ConsentTypes.Implicit, // Auto-approve for development
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email,
                    Permissions.Scopes.Profile,
                    Permissions.Prefixes.Scope + "api",
                    Permissions.Prefixes.Scope + "camera"
                },
                RedirectUris = 
                {
                    new Uri("https://localhost:5001/signin-oidc"),
                    new Uri("http://localhost:5000/signin-oidc"),
                    new Uri("https://oauth.pstmn.io/v1/callback") // For Postman testing
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://localhost:5001/signout-callback-oidc"),
                    new Uri("http://localhost:5000/signout-callback-oidc")
                }
            });

            logger.LogInformation("Created OAuth2 client: {ClientId}", testClientId);
            logger.LogWarning("SECURITY: Test client uses default secret. Change in production!");
        }
        else
        {
            logger.LogInformation("OAuth2 client already exists: {ClientId}", testClientId);
        }

        // Seed camera agent client for system-to-system communication
        const string cameraAgentClientId = "camera-agent";
        
        if (await applicationManager.FindByClientIdAsync(cameraAgentClientId) == null)
        {
            await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = cameraAgentClientId,
                ClientSecret = "camera-agent-secret-change-in-production",
                DisplayName = "Camera Agent Service",
                ConsentType = ConsentTypes.Implicit,
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + "api",
                    Permissions.Prefixes.Scope + "camera"
                }
            });

            logger.LogInformation("Created OAuth2 client: {ClientId}", cameraAgentClientId);
            logger.LogWarning("SECURITY: Camera agent client uses default secret. Change in production!");
        }
        else
        {
            logger.LogInformation("OAuth2 client already exists: {ClientId}", cameraAgentClientId);
        }
    }
}
