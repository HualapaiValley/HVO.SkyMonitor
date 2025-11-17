using System.Linq;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

#pragma warning disable CA1848 // Database seeding logs run rarely; LoggerMessage delegates add noise
#pragma warning disable CA2007 // ConfigureAwait(false) not required in startup-only seeding helpers

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Seeds the database with initial data for development and production.
/// </summary>
internal static class DatabaseSeeder
{
    /// <summary>
    /// Seeds the database with default accounts, scopes, and OAuth2 clients.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var apiKeyHasher = serviceProvider.GetRequiredService<IApiKeyHasher>();
        var dbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();

        // Seed default interactive accounts
        await SeedDefaultUsersAsync(userManager, logger);

        // Seed system service account
        var systemAccount = await SeedSystemAccountAsync(userManager, logger);

        // Seed API keys for system integrations
        await SeedApiKeysAsync(dbContext, apiKeyHasher, systemAccount, logger);

        // Seed OpenIddict scopes and clients
        await SeedOpenIddictDataAsync(serviceProvider, logger);

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedDefaultUsersAsync(UserManager<ApplicationUser> userManager, ILogger logger)
    {
        foreach (var descriptor in GetTestUsers())
        {
            var user = await userManager.FindByEmailAsync(descriptor.Email);

            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = descriptor.Username,
                    Email = descriptor.Email,
                    EmailConfirmed = true,
                    AccountType = descriptor.AccountType
                };

                var result = await userManager.CreateAsync(user, descriptor.Password);

                if (result.Succeeded)
                {
                    logger.LogInformation("Seeded user {Email}", descriptor.Email);
                }
                else
                {
                    logger.LogError("Failed to create user {Email}: {Errors}", descriptor.Email, string.Join(", ", result.Errors.Select(e => e.Description)));
                }

                continue;
            }

            var needsUpdate = false;
            if (user.AccountType != descriptor.AccountType)
            {
                user.AccountType = descriptor.AccountType;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                await userManager.UpdateAsync(user);
            }

            if (!await userManager.CheckPasswordAsync(user, descriptor.Password))
            {
                if (await userManager.HasPasswordAsync(user))
                {
                    await userManager.RemovePasswordAsync(user);
                }

                var passwordResult = await userManager.AddPasswordAsync(user, descriptor.Password);
                if (!passwordResult.Succeeded)
                {
                    logger.LogError("Failed to update password for {Email}: {Errors}", descriptor.Email, string.Join(", ", passwordResult.Errors.Select(e => e.Description)));
                }
            }
        }
    }

    private static async Task<ApplicationUser?> SeedSystemAccountAsync(UserManager<ApplicationUser> userManager, ILogger logger)
    {
        const string systemEmail = "system@skymonitor.local";
        const string systemUsername = "system-service";

        var existingAccount = await userManager.FindByEmailAsync(systemEmail);
        if (existingAccount != null)
        {
            logger.LogInformation("System service account already exists: {Email}", systemEmail);
            return existingAccount;
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
            return systemAccount;
        }

        logger.LogError("Failed to create system account: {Errors}", string.Join(", ", result.Errors.Select(e => e.Description)));
        return null;
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
        var scopes = new[]
        {
            new { Name = "api.camera", DisplayName = "Camera Control", Description = "Access to camera control endpoints" },
            new { Name = "api.frames", DisplayName = "Frame APIs", Description = "Access to frame ingestion endpoints" },
            new { Name = "api.images", DisplayName = "Image APIs", Description = "Access to image processing endpoints" },
            new { Name = "api.admin", DisplayName = "Administrative Access", Description = "Full administrative access" },
            new { Name = "api.viewer", DisplayName = "Viewer Access", Description = "Read-only API access" },
            new { Name = "api.webhooks", DisplayName = "Webhook Access", Description = "Webhook publishing scopes" }
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
        await EnsureConfidentialClientAsync(
            applicationManager,
            logger,
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            TestClients.SystemCameraAgent.DisplayName,
            TestClients.SystemCameraAgent.Scopes);

        await EnsureConfidentialClientAsync(
            applicationManager,
            logger,
            TestClients.SystemInternal.ClientId,
            TestClients.SystemInternal.ClientSecret,
            TestClients.SystemInternal.DisplayName,
            TestClients.SystemInternal.Scopes);

        await EnsurePublicClientAsync(
            applicationManager,
            logger,
            TestClients.WebUI.ClientId,
            TestClients.WebUI.DisplayName,
            TestClients.WebUI.Scopes,
            redirectUris: new[]
            {
                new Uri("https://localhost:5001/signin-oidc"),
                new Uri("http://localhost:5000/signin-oidc")
            },
            postLogoutUris: new[]
            {
                new Uri("https://localhost:5001/signout-callback-oidc"),
                new Uri("http://localhost:5000/signout-callback-oidc")
            });

        await EnsurePublicClientAsync(
            applicationManager,
            logger,
            TestClients.MobileApp.ClientId,
            TestClients.MobileApp.DisplayName,
            TestClients.MobileApp.Scopes,
            redirectUris: new[] { new Uri("com.skymonitor.mobile://auth-callback") },
            postLogoutUris: Array.Empty<Uri>());
    }

    private static async Task EnsureConfidentialClientAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        string clientId,
        string clientSecret,
        string displayName,
        IReadOnlyCollection<string> scopes)
    {
        if (await applicationManager.FindByClientIdAsync(clientId) != null)
        {
            logger.LogInformation("OAuth2 client already exists: {ClientId}", clientId);
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            DisplayName = displayName,
            ConsentType = ConsentTypes.Implicit
        };

        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        foreach (var scope in scopes)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        await applicationManager.CreateAsync(descriptor);
        logger.LogInformation("Created OAuth2 client: {ClientId}", clientId);
        logger.LogWarning("SECURITY: Client {ClientId} uses default secret. Change it in production!", clientId);
    }

    private static async Task EnsurePublicClientAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        string clientId,
        string displayName,
        IReadOnlyCollection<string> scopes,
        IReadOnlyCollection<Uri> redirectUris,
        IReadOnlyCollection<Uri> postLogoutUris)
    {
        if (await applicationManager.FindByClientIdAsync(clientId) != null)
        {
            logger.LogInformation("OAuth2 client already exists: {ClientId}", clientId);
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
            ConsentType = ConsentTypes.Explicit,
            ClientType = ClientTypes.Public
        };

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.GrantTypes.Password);
        descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);

        var requestedScopes = new[] { Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess }
            .Concat(scopes);

        foreach (var scope in requestedScopes)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(uri);
        }

        foreach (var uri in postLogoutUris)
        {
            descriptor.PostLogoutRedirectUris.Add(uri);
        }

        await applicationManager.CreateAsync(descriptor);
        logger.LogInformation("Created OAuth2 public client: {ClientId}", clientId);
    }

    private static IEnumerable<TestUserDescriptor> GetTestUsers()
    {
        yield return new TestUserDescriptor(TestUsers.Admin.Email, TestUsers.Admin.Username, TestUsers.Admin.Password, AccountType.User);
        yield return new TestUserDescriptor(TestUsers.Operator.Email, TestUsers.Operator.Username, TestUsers.Operator.Password, AccountType.User);
        yield return new TestUserDescriptor(TestUsers.Viewer.Email, TestUsers.Viewer.Username, TestUsers.Viewer.Password, AccountType.User);
        yield return new TestUserDescriptor(TestUsers.Regular.Email, TestUsers.Regular.Username, TestUsers.Regular.Password, AccountType.User);
    }

    private static async Task SeedApiKeysAsync(
        ApplicationDbContext dbContext,
        IApiKeyHasher hasher,
        ApplicationUser? systemAccount,
        ILogger logger)
    {
        if (systemAccount is null)
        {
            logger.LogWarning("System account missing. API key seeding skipped.");
            return;
        }

        var descriptors = new[]
        {
            new ApiKeyDescriptor(systemAccount.Id, TestApiKeys.CameraAgent.Key, TestApiKeys.CameraAgent.Name, ApiKeyAccessLevel.ReadWrite),
            new ApiKeyDescriptor(systemAccount.Id, TestApiKeys.InternalService.Key, TestApiKeys.InternalService.Name, ApiKeyAccessLevel.ReadWrite),
            new ApiKeyDescriptor(systemAccount.Id, TestApiKeys.Webhook.Key, TestApiKeys.Webhook.Name, ApiKeyAccessLevel.Read),
            new ApiKeyDescriptor(systemAccount.Id, TestApiKeys.ReadOnly.Key, TestApiKeys.ReadOnly.Name, ApiKeyAccessLevel.Read)
        };

        foreach (var descriptor in descriptors)
        {
            var hashed = hasher.Hash(descriptor.RawKey);
            var exists = await dbContext.ApiKeys.AnyAsync(key => key.HashedKey == hashed);
            if (exists)
            {
                continue;
            }

            dbContext.ApiKeys.Add(new ApiKey
            {
                UserId = descriptor.UserId,
                DisplayName = descriptor.DisplayName,
                AccessLevel = descriptor.AccessLevel,
                HashedKey = hashed,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedBy = "DatabaseSeeder"
            });

            logger.LogInformation("Seeded API key {Name}", descriptor.DisplayName);
        }
    }

    private sealed record TestUserDescriptor(string Email, string Username, string Password, AccountType AccountType);

    private sealed record ApiKeyDescriptor(string UserId, string RawKey, string DisplayName, ApiKeyAccessLevel AccessLevel);
}

#pragma warning restore CA2007
#pragma warning restore CA1848
