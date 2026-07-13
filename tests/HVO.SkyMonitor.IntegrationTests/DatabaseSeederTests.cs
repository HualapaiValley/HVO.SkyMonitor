using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class DatabaseSeederTests
{
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
}
