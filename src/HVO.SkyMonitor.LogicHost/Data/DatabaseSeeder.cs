using System.Linq;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var apiKeyHasher = serviceProvider.GetRequiredService<IApiKeyHasher>();
        var dbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
        var options = serviceProvider.GetRequiredService<IOptions<DatabaseSeedOptions>>().Value;
        var bootstrapOptions = serviceProvider.GetRequiredService<IOptions<DeviceBootstrapSecretsOptions>>().Value;
        var centralIdentityOptions = serviceProvider.GetRequiredService<IOptions<CentralIdentityOptions>>().Value;
        var bootstrapClient = (bootstrapOptions.CentralIdentity ?? centralIdentityOptions).ClientCredentials;

        // Interactive credentials and integration keys are opt-in configuration.
        await EnsurePlatformEditorRoleAsync(roleManager);
        await SeedDefaultUsersAsync(userManager, options.Users, logger);

        // Seed system service account
        var systemAccount = await SeedSystemAccountAsync(userManager, logger);

        // Seed API keys for system integrations
        await SeedApiKeysAsync(dbContext, apiKeyHasher, systemAccount, options.ApiKeys, logger);

        // Seed OpenIddict scopes and clients
        await SeedOpenIddictDataAsync(serviceProvider, options, bootstrapClient, logger);

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedDefaultUsersAsync(
        UserManager<ApplicationUser> userManager,
        IEnumerable<SeedUserOptions> users,
        ILogger logger)
    {
        foreach (var descriptor in users)
        {
            var user = await userManager.FindByEmailAsync(descriptor.Email);

            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = descriptor.Username,
                    Email = descriptor.Email,
                    EmailConfirmed = true,
                    AccountType = AccountType.User
                };

                var result = await userManager.CreateAsync(user, descriptor.Password);

                if (result.Succeeded)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Seeded user {Email}", descriptor.Email);
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Failed to create user {descriptor.Email}: " +
                        string.Join(", ", result.Errors.Select(error => error.Description)));
                }
            }

            var needsUpdate = false;
            if (user.AccountType != AccountType.User)
            {
                user.AccountType = AccountType.User;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                var updateResult = await userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to reconcile user {descriptor.Email}: " +
                        string.Join(", ", updateResult.Errors.Select(error => error.Description)));
                }
            }

            if (!await userManager.CheckPasswordAsync(user, descriptor.Password))
            {
                if (await userManager.HasPasswordAsync(user))
                {
                    var removePasswordResult = await userManager.RemovePasswordAsync(user);
                    if (!removePasswordResult.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Failed to remove the previous password for {descriptor.Email}: " +
                            string.Join(", ", removePasswordResult.Errors.Select(error => error.Description)));
                    }
                }

                var passwordResult = await userManager.AddPasswordAsync(user, descriptor.Password);
                if (!passwordResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to update the password for {descriptor.Email}: " +
                        string.Join(", ", passwordResult.Errors.Select(error => error.Description)));
                }
            }

            var isEditor = await userManager.IsInRoleAsync(user, AuthorizationRoleNames.PlatformEditor);
            if (descriptor.IsPlatformEditor.HasValue && isEditor != descriptor.IsPlatformEditor.Value)
            {
                var roleResult = descriptor.IsPlatformEditor.Value
                    ? await userManager.AddToRoleAsync(user, AuthorizationRoleNames.PlatformEditor)
                    : await userManager.RemoveFromRoleAsync(user, AuthorizationRoleNames.PlatformEditor);
                if (!roleResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to reconcile Platform Editor role for {descriptor.Email}: " +
                        string.Join(", ", roleResult.Errors.Select(error => error.Description)));
                }
                var stampResult = await userManager.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to rotate the security stamp for {descriptor.Email} after role reconciliation.");
                }
            }
        }
    }

    private static async Task EnsurePlatformEditorRoleAsync(RoleManager<IdentityRole> roleManager)
    {
        if (await roleManager.RoleExistsAsync(AuthorizationRoleNames.PlatformEditor))
        {
            return;
        }
        var result = await roleManager.CreateAsync(new IdentityRole(AuthorizationRoleNames.PlatformEditor));
        if (!result.Succeeded && !await roleManager.RoleExistsAsync(AuthorizationRoleNames.PlatformEditor))
        {
            throw new InvalidOperationException(
                "Failed to seed the Platform Editor role: " +
                string.Join(", ", result.Errors.Select(error => error.Description)));
        }
    }

    private static async Task<ApplicationUser> SeedSystemAccountAsync(UserManager<ApplicationUser> userManager, ILogger logger)
    {
        const string systemEmail = "system@skymonitor.local";
        const string systemUsername = "system-service";

        var existingAccount = await userManager.FindByEmailAsync(systemEmail);
        if (existingAccount != null)
        {
            if (await userManager.HasPasswordAsync(existingAccount))
            {
                var passwordResult = await userManager.RemovePasswordAsync(existingAccount);
                if (!passwordResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Failed to remove password authentication from the system account: " +
                        string.Join(", ", passwordResult.Errors.Select(error => error.Description)));
                }
            }
            var needsUpdate = false;
            if (!string.Equals(existingAccount.UserName, systemUsername, StringComparison.Ordinal))
            {
                existingAccount.UserName = systemUsername;
                needsUpdate = true;
            }
            if (!existingAccount.EmailConfirmed)
            {
                existingAccount.EmailConfirmed = true;
                needsUpdate = true;
            }
            if (existingAccount.AccountType != AccountType.System)
            {
                existingAccount.AccountType = AccountType.System;
                needsUpdate = true;
            }
            if (needsUpdate)
            {
                var updateResult = await userManager.UpdateAsync(existingAccount);
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Failed to reconcile the system account: " +
                        string.Join(", ", updateResult.Errors.Select(error => error.Description)));
                }
            }
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("System service account already exists: {Email}", systemEmail);
            }
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
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("System service account created successfully: {Email}", systemEmail);
            }
            logger.LogInformation("System account uses API key authentication only - no password authentication");
            return systemAccount;
        }

        throw new InvalidOperationException(
            "Failed to create the system account: " +
            string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    private static async Task SeedOpenIddictDataAsync(
        IServiceProvider serviceProvider,
        DatabaseSeedOptions options,
        ClientCredentialsOptions? bootstrapClient,
        ILogger logger)
    {
        var scopeManager = serviceProvider.GetRequiredService<IOpenIddictScopeManager>();
        var applicationManager = serviceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        // Seed scopes
        await SeedScopesAsync(scopeManager, logger);

        // Seed OAuth2 applications/clients
        await SeedApplicationsAsync(applicationManager, options, bootstrapClient, logger);
    }

    private static async Task SeedScopesAsync(IOpenIddictScopeManager scopeManager, ILogger logger)
    {
        var scopes = new[]
        {
            new { Name = "api.camera", DisplayName = "Camera Control", Description = "Access to camera control endpoints" },
            new { Name = "api.artifacts.read", DisplayName = "Artifact Retrieval", Description = "Job-bound access to central artifact content" },
            new { Name = "api.frames", DisplayName = "Frame APIs", Description = "Access to frame ingestion endpoints" },
            new { Name = "api.images", DisplayName = "Image APIs", Description = "Access to image processing endpoints" },
            new { Name = "api.admin", DisplayName = "Administrative Access", Description = "Full administrative access" },
            new { Name = "api.viewer", DisplayName = "Viewer Access", Description = "Read-only API access" },
            new { Name = "api.owner.write", DisplayName = "Owner Mutation Access", Description = "Modify resources owned by the authenticated user" },
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

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Created scope: {ScopeName}", scope.Name);
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Scope already exists: {ScopeName}", scope.Name);
                }
            }
        }
    }

    private static async Task SeedApplicationsAsync(
        IOpenIddictApplicationManager applicationManager,
        DatabaseSeedOptions options,
        ClientCredentialsOptions? bootstrapClient,
        ILogger logger)
    {
        if (bootstrapClient is not null &&
            !string.IsNullOrWhiteSpace(bootstrapClient.ClientId) &&
            !string.IsNullOrWhiteSpace(bootstrapClient.ClientSecret))
        {
            await EnsureConfidentialClientAsync(
                applicationManager,
                logger,
                bootstrapClient.ClientId,
                bootstrapClient.ClientSecret,
                "Camera Agent Bootstrap Client",
                bootstrapClient.Scopes.Count > 0 ? bootstrapClient.Scopes : ClientCredentialsOptions.DefaultScopes);
        }

        foreach (var client in options.ConfidentialClients)
        {
            await EnsureConfidentialClientAsync(
                applicationManager,
                logger,
                client.ClientId,
                client.ClientSecret,
                client.DisplayName,
                client.Scopes);
        }

        foreach (var client in options.PublicClients)
        {
            await EnsurePublicClientAsync(
                applicationManager,
                logger,
                client.ClientId,
                client.DisplayName,
                client.Scopes,
                client.RedirectUris.Select(static value => new Uri(value, UriKind.Absolute)).ToArray(),
                client.PostLogoutRedirectUris.Select(static value => new Uri(value, UriKind.Absolute)).ToArray());
        }
    }

    private static async Task EnsureConfidentialClientAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        string clientId,
        string clientSecret,
        string displayName,
        IEnumerable<string> scopes)
    {
        var managedDescriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            DisplayName = displayName,
            ConsentType = ConsentTypes.Implicit,
            ClientType = ClientTypes.Confidential
        };

        managedDescriptor.Permissions.Add(Permissions.Endpoints.Token);
        managedDescriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        foreach (var scope in scopes)
        {
            managedDescriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        var application = await applicationManager.FindByClientIdAsync(clientId);
        if (application is null)
        {
            await applicationManager.CreateAsync(managedDescriptor);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Created OAuth2 client: {ClientId}", clientId);
            }
            return;
        }

        var currentPermissions = await applicationManager.GetPermissionsAsync(application);
        var metadataChanged = !string.Equals(
                await applicationManager.GetDisplayNameAsync(application), displayName, StringComparison.Ordinal) ||
            !string.Equals(
                await applicationManager.GetConsentTypeAsync(application), ConsentTypes.Implicit, StringComparison.Ordinal) ||
            !string.Equals(
                await applicationManager.GetClientTypeAsync(application), ClientTypes.Confidential, StringComparison.Ordinal) ||
            !currentPermissions.ToHashSet(StringComparer.Ordinal).SetEquals(managedDescriptor.Permissions);
        var secretChanged = !await applicationManager.ValidateClientSecretAsync(application, clientSecret);

        if (metadataChanged)
        {
            var persistedDescriptor = new OpenIddictApplicationDescriptor();
            await applicationManager.PopulateAsync(persistedDescriptor, application);
            persistedDescriptor.ClientId = managedDescriptor.ClientId;
            persistedDescriptor.DisplayName = managedDescriptor.DisplayName;
            persistedDescriptor.ConsentType = managedDescriptor.ConsentType;
            persistedDescriptor.ClientType = managedDescriptor.ClientType;
            persistedDescriptor.Permissions.Clear();
            persistedDescriptor.Permissions.UnionWith(managedDescriptor.Permissions);
            if (secretChanged)
            {
                persistedDescriptor.ClientSecret = clientSecret;
            }

            await applicationManager.UpdateAsync(application, persistedDescriptor);
        }
        else if (secretChanged)
        {
            await applicationManager.UpdateAsync(application, clientSecret);
        }
        else
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("OAuth2 client already matches configuration: {ClientId}", clientId);
            }
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Updated OAuth2 client from configuration: {ClientId}", clientId);
        }
    }

    private static async Task EnsurePublicClientAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        string clientId,
        string displayName,
        IEnumerable<string> scopes,
        IReadOnlyCollection<Uri> redirectUris,
        IReadOnlyCollection<Uri> postLogoutUris)
    {
        if (await applicationManager.FindByClientIdAsync(clientId) != null)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("OAuth2 client already exists: {ClientId}", clientId);
            }
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
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Created OAuth2 public client: {ClientId}", clientId);
        }
    }

    private static async Task SeedApiKeysAsync(
        ApplicationDbContext dbContext,
        IApiKeyHasher hasher,
        ApplicationUser systemAccount,
        IEnumerable<SeedApiKeyOptions> apiKeys,
        ILogger logger)
    {
        foreach (var descriptor in apiKeys)
        {
            var hashed = hasher.Hash(descriptor.RawKey);
            var existing = await dbContext.ApiKeys.SingleOrDefaultAsync(key => key.HashedKey == hashed);
            if (existing is not null)
            {
                existing.UserId = systemAccount.Id;
                existing.DisplayName = descriptor.DisplayName;
                existing.AccessLevel = descriptor.AccessLevel;
                existing.ObservatoryId = null;
                existing.IsActive = true;
                existing.ExpiresUtc = null;
                existing.CreatedBy = "DatabaseSeeder";
                continue;
            }

            dbContext.ApiKeys.Add(new ApiKey
            {
                UserId = systemAccount.Id,
                DisplayName = descriptor.DisplayName,
                AccessLevel = descriptor.AccessLevel,
                HashedKey = hashed,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedBy = "DatabaseSeeder"
            });

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Seeded API key {Name}", descriptor.DisplayName);
            }
        }
    }

}

#pragma warning restore CA2007
#pragma warning restore CA1848
