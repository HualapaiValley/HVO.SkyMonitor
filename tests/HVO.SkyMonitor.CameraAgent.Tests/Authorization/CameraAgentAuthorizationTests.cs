using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentAuthorizationTests
{
    private const string ConfiguredEmail = "owner@cameraagent.test";

    [TestMethod]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, false, false, false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, true, false, false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, true, true, true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, false, false, false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, true, false, false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, true, true, true)]
    public async Task OperationsPolicies_RequireAuthenticatedSiteOwner(
        string policyName,
        bool authenticated,
        bool isSiteOwner,
        bool expectedSuccess)
    {
        var userManager = CreateUserManager();
        userManager
            .Setup(manager => manager.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new ApplicationUser
            {
                IsSiteOwner = isSiteOwner,
                NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
                SecurityStamp = "durable-stamp"
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var authorizationService = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var identity = authenticated ? CanonicalIdentity("test-user") : new ClaimsIdentity();

        var result = await authorizationService.AuthorizeAsync(new ClaimsPrincipal(identity), policyName)
            .ConfigureAwait(false);

        Assert.AreEqual(expectedSuccess, result.Succeeded);
    }

    [TestMethod]
    [DataRow("stale-owner@cameraagent.test", true, false)]
    [DataRow("owner@cameraagent.test", false, false)]
    [DataRow("replacement-owner@cameraagent.test", true, true)]
    public async Task SiteOwnerFlagMustMatchCurrentConfiguredNormalizedEmail(
        string storedEmail,
        bool isSiteOwner,
        bool useStoredEmailAsConfiguration)
    {
        ArgumentNullException.ThrowIfNull(storedEmail);
        var configuredEmail = useStoredEmailAsConfiguration ? storedEmail : "replacement-owner@cameraagent.test";
        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new ApplicationUser
            {
                IsSiteOwner = isSiteOwner,
                NormalizedEmail = storedEmail.ToUpperInvariant(),
                SecurityStamp = "durable-stamp"
            });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = configuredEmail }));
        services.AddCameraAgentAuthorization();

        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(CanonicalIdentity("test-user"));

        var result = await authorization.AuthorizeAsync(
            principal, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);

        Assert.AreEqual(isSiteOwner && useStoredEmailAsConfiguration, result.Succeeded);
    }

    [TestMethod]
    public async Task OperationsPolicies_DenyOwnerUntilTemporaryPasswordIsReplaced()
    {
        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new ApplicationUser
            {
                IsSiteOwner = true,
                PasswordChangeRequired = true,
                NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
                SecurityStamp = "durable-stamp"
            });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();
        using var provider = services.BuildServiceProvider();
        var principal = new ClaimsPrincipal(CanonicalIdentity("owner"));

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(
            principal,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task BootstrapStatusPolicy_AllowsConfiguredOwnerBeforePasswordReplacement()
    {
        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new ApplicationUser
            {
                IsSiteOwner = true,
                PasswordChangeRequired = true,
                NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
                SecurityStamp = "durable-stamp"
            });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();
        using var provider = services.BuildServiceProvider();
        var principal = new ClaimsPrincipal(CanonicalIdentity("owner"));

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(
            principal,
            CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task SiteOwnerPolicy_RejectsNoncanonicalAndMixedCookieIdentities()
    {
        var userManager = CreateUserManager();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();
        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var noncanonicalPrincipals = new[]
        {
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "owner")],
                IdentityConstants.ApplicationScheme)),
            new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "owner"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
                ],
                IdentityConstants.ApplicationScheme)),
            new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "owner"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
                ],
                IdentityConstants.ApplicationScheme)),
            new ClaimsPrincipal([CanonicalIdentity("owner"), CanonicalIdentity("other-owner")])
        };

        foreach (var principal in noncanonicalPrincipals)
        {
            var result = await authorization.AuthorizeAsync(
                principal,
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);
            Assert.IsFalse(result.Succeeded);
        }

        userManager.Verify(manager => manager.FindByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task SiteOwnerPolicy_RejectsAStaleSecurityStamp()
    {
        var user = new ApplicationUser
        {
            IsSiteOwner = true,
            NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
            SecurityStamp = "durable-stamp"
        };
        var userManager = CreateUserManager();
        userManager.SetupGet(manager => manager.SupportsUserSecurityStamp).Returns(true);
        userManager.Setup(manager => manager.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        userManager.Setup(manager => manager.GetSecurityStampAsync(user)).ReturnsAsync(user.SecurityStamp);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();
        using var provider = services.BuildServiceProvider();
        var identity = CanonicalIdentity("owner");
        identity.RemoveClaim(identity.FindFirst(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType)!);
        identity.AddClaim(new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "stale-stamp"));

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(
            new ClaimsPrincipal(identity),
            CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task SiteOwnerPolicy_RejectsAStoreWithoutSecurityStampSupport()
    {
        var user = new ApplicationUser
        {
            IsSiteOwner = true,
            NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
            SecurityStamp = "durable-stamp"
        };
        var userManager = CreateUserManager();
        userManager.SetupGet(manager => manager.SupportsUserSecurityStamp).Returns(false);
        userManager.Setup(manager => manager.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(
            new ClaimsPrincipal(CanonicalIdentity("owner")),
            CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
    }

    private static Mock<UserManager<ApplicationUser>> CreateUserManager()
    {
        var manager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.SetupGet(candidate => candidate.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(candidate => candidate.GetSecurityStampAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync((ApplicationUser user) => user.SecurityStamp!);
        return manager;
    }

    private static ClaimsIdentity CanonicalIdentity(string ownerId)
        => new(
            [
                new Claim(ClaimTypes.NameIdentifier, ownerId),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
                new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "durable-stamp")
            ],
            IdentityConstants.ApplicationScheme);
}
