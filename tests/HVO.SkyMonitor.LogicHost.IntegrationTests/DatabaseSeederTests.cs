using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseSeederTests
{
    [TestMethod]
    public async Task SeedAsyncPreservesCanonicalGraphAcrossRestartWithTransientConfiguration()
    {
        string definitionJson;
        string definitionIdentity;
        string portablePlanIdentity;
        string? centralPlanIdentity;
        using (var beforeScope = AssemblyHooks.Fixture.Factory.Services.CreateScope())
        {
            var dbContext = beforeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var revision = await dbContext.CentralProcessingGraphRevisions.AsNoTracking()
                .SingleAsync(item => item.Id == DatabaseSeeder.BasicCentralProcessingGraphRevisionId);
            definitionJson = revision.DefinitionJson;
            definitionIdentity = revision.DefinitionIdentitySha256;
            portablePlanIdentity = revision.PortablePlanIdentitySha256;
            centralPlanIdentity = revision.CentralPlanIdentitySha256;
        }
        var configuredRegistry = new CentralProcessingGraphNodeRegistry(
            new CentralDerivativeRecipeCatalog(new CentralTransientOptions
            {
                Mode = TransientDetectorExecutionMode.Central,
                SourceRole = FrameArtifactRole.Calibrated
            }));

        for (var restart = 0; restart < 2; restart++)
        {
            using var scope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
            await DatabaseSeeder.SeedAsync(
                new RegistryOverrideServiceProvider(scope.ServiceProvider, configuredRegistry),
                NullLogger.Instance);
        }

        using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var retained = await verification.CentralProcessingGraphRevisions.AsNoTracking()
            .SingleAsync(item => item.Id == DatabaseSeeder.BasicCentralProcessingGraphRevisionId);
        Assert.AreEqual(definitionJson, retained.DefinitionJson);
        Assert.AreEqual(definitionIdentity, retained.DefinitionIdentitySha256);
        Assert.AreEqual(portablePlanIdentity, retained.PortablePlanIdentitySha256);
        Assert.AreEqual(centralPlanIdentity, retained.CentralPlanIdentitySha256);
        Assert.DoesNotContain("TransientDetection", retained.DefinitionJson, StringComparison.Ordinal);
        Assert.AreEqual(1, await verification.CentralProcessingGraphAssignments.AsNoTracking()
            .CountAsync(item => item.Id == DatabaseSeeder.BasicCentralProcessingGraphAssignmentId));
    }

    [TestMethod]
    public async Task SeedAsyncAssignsConfiguredApiKeyToPasswordlessOwner()
    {
        const string email = "seeded-api-owner@integration.test";
        const string rawKey = "integration-owner-key";
        var descriptor = new SeedApiKeyOptions
        {
            RawKey = rawKey,
            DisplayName = "Integration API owner",
            AccessLevel = ApiKeyAccessLevel.ReadWrite,
            UserEmail = email
        };
        using var scope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseSeedOptions>>().Value;
        options.ApiKeys.Add(descriptor);

        try
        {
            await DatabaseSeeder.SeedAsync(scope.ServiceProvider, NullLogger.Instance);

            using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
            var dbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var hasher = verificationScope.ServiceProvider.GetRequiredService<IApiKeyHasher>();
            var userManager = verificationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.FindByEmailAsync(email)
                ?? throw new InvalidOperationException("API-key owner was not seeded.");
            var apiKey = await dbContext.ApiKeys.SingleAsync(key => key.HashedKey == hasher.Hash(rawKey));

            Assert.AreEqual(owner.Id, apiKey.UserId);
            Assert.AreEqual(AccountType.User, owner.AccountType);
            Assert.IsNull(owner.PasswordHash);
        }
        finally
        {
            options.ApiKeys.Remove(descriptor);
            using var cleanupScope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
            var dbContext = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var hasher = cleanupScope.ServiceProvider.GetRequiredService<IApiKeyHasher>();
            var apiKey = await dbContext.ApiKeys.SingleOrDefaultAsync(key => key.HashedKey == hasher.Hash(rawKey));
            if (apiKey is not null)
            {
                dbContext.ApiKeys.Remove(apiKey);
                await dbContext.SaveChangesAsync();
            }
            var userManager = cleanupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.FindByEmailAsync(email);
            if (owner is not null)
            {
                Assert.IsTrue((await userManager.DeleteAsync(owner)).Succeeded);
            }
        }
    }

    [TestMethod]
    public async Task SeedAsyncCreatesPlatformEditorRoleAndPreservesUnspecifiedGrant()
    {
        using var scope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(TestUsers.Viewer.Email)
            ?? throw new InvalidOperationException("Viewer was not seeded.");
        Assert.IsTrue(await roleManager.RoleExistsAsync(AuthorizationRoleNames.PlatformEditor));
        if (!await userManager.IsInRoleAsync(user, AuthorizationRoleNames.PlatformEditor))
        {
            Assert.IsTrue((await userManager.AddToRoleAsync(user, AuthorizationRoleNames.PlatformEditor)).Succeeded);
        }

        try
        {
            await DatabaseSeeder.SeedAsync(scope.ServiceProvider, NullLogger.Instance);

            Assert.IsTrue(await userManager.IsInRoleAsync(user, AuthorizationRoleNames.PlatformEditor));
        }
        finally
        {
            Assert.IsTrue((await userManager.RemoveFromRoleAsync(user, AuthorizationRoleNames.PlatformEditor)).Succeeded);
        }
    }

    [TestMethod]
    public async Task SeedAsyncReconcilesExistingBootstrapClient()
    {
        const string staleSecret = "stale-camera-agent-secret";
        using var scope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
        var applicationManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applicationManager.FindByClientIdAsync(TestClients.SystemCameraAgent.ClientId)
            ?? throw new InvalidOperationException("Bootstrap client was not seeded.");
        var originalDescriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(originalDescriptor, application);
        originalDescriptor.ClientSecret = TestClients.SystemCameraAgent.ClientSecret;
        var staleDescriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(staleDescriptor, application);
        staleDescriptor.ClientSecret = staleSecret;
        staleDescriptor.DisplayName = "Stale Bootstrap Client";
        staleDescriptor.Permissions.Clear();
        staleDescriptor.Permissions.Add(Permissions.Endpoints.Token);
        using var propertyDocument = JsonDocument.Parse("true");
        staleDescriptor.Properties["preserved-by-reconciliation"] = propertyDocument.RootElement.Clone();
        await applicationManager.UpdateAsync(application, staleDescriptor);

        try
        {
            await DatabaseSeeder.SeedAsync(scope.ServiceProvider, NullLogger.Instance);

            using var verificationScope = AssemblyHooks.Fixture.Factory.Services.CreateScope();
            var verificationManager = verificationScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            application = await verificationManager.FindByClientIdAsync(TestClients.SystemCameraAgent.ClientId)
                ?? throw new InvalidOperationException("Bootstrap client was not retained.");
            Assert.IsTrue(await verificationManager.ValidateClientSecretAsync(
                application,
                TestClients.SystemCameraAgent.ClientSecret));
            Assert.IsFalse(await verificationManager.ValidateClientSecretAsync(application, staleSecret));
            Assert.AreEqual("Camera Agent Bootstrap Client", await verificationManager.GetDisplayNameAsync(application));

            var permissions = await verificationManager.GetPermissionsAsync(application);
            var expectedPermissions = TestClients.SystemCameraAgent.Scopes
                .Select(scopeName => Permissions.Prefixes.Scope + scopeName)
                .Append(Permissions.Endpoints.Token)
                .Append(Permissions.GrantTypes.ClientCredentials);
            CollectionAssert.AreEquivalent(expectedPermissions.ToArray(), permissions.ToArray());

            var properties = await verificationManager.GetPropertiesAsync(application);
            Assert.IsTrue(properties.TryGetValue("preserved-by-reconciliation", out var preserved));
            Assert.IsTrue(preserved.GetBoolean());

            await DatabaseSeeder.SeedAsync(verificationScope.ServiceProvider, NullLogger.Instance);
            application = await verificationManager.FindByClientIdAsync(TestClients.SystemCameraAgent.ClientId)
                ?? throw new InvalidOperationException("Bootstrap client was not retained after idempotent seeding.");
            Assert.IsTrue(await verificationManager.ValidateClientSecretAsync(
                application,
                TestClients.SystemCameraAgent.ClientSecret));
            Assert.IsTrue((await verificationManager.GetPropertiesAsync(application))
                .ContainsKey("preserved-by-reconciliation"));
        }
        finally
        {
            application = await applicationManager.FindByClientIdAsync(TestClients.SystemCameraAgent.ClientId);
            if (application is not null)
            {
                await applicationManager.UpdateAsync(application, originalDescriptor);
            }
        }
    }

    private sealed class RegistryOverrideServiceProvider(
        IServiceProvider inner,
        ICentralProcessingGraphNodeRegistry registry) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ICentralProcessingGraphNodeRegistry)
                ? registry
                : inner.GetService(serviceType);
    }
}
