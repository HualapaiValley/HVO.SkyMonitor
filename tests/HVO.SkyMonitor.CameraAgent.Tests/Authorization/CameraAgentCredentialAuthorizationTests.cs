using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

/// <summary>Which credential each CameraAgent policy accepts, beyond the site-owner checks.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCredentialAuthorizationTests
{
    private const string ConfiguredEmail = "owner@cameraagent.test";
    private const string Stamp = "durable-stamp";

    [TestMethod]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, "session", true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, "session", true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.ApiKeyManagementV1, "session", true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1, "session", true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, nameof(ApiKeyAccessLevel.Read), true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, nameof(ApiKeyAccessLevel.Read), false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.ApiKeyManagementV1, nameof(ApiKeyAccessLevel.Read), false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1, nameof(ApiKeyAccessLevel.Read), false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, nameof(ApiKeyAccessLevel.ReadWrite), true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, nameof(ApiKeyAccessLevel.ReadWrite), true)]
    [DataRow(CameraAgentAuthorizationPolicyNames.ApiKeyManagementV1, nameof(ApiKeyAccessLevel.ReadWrite), false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1, nameof(ApiKeyAccessLevel.ReadWrite), false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, "bearer", false)]
    [DataRow(CameraAgentAuthorizationPolicyNames.OperationsReadV1, "unknown-access", false)]
    public async Task Policies_AcceptAKeyOnlyForTheOperationsItsAccessCoversAsync(
        string policy,
        string credential,
        bool expected)
    {
        using var provider = Services(new ApplicationUser
        {
            Id = "owner",
            IsSiteOwner = true,
            NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
            SecurityStamp = Stamp
        });
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(Principal(credential), policy).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Succeeded);
    }

    [TestMethod]
    public async Task ReadWriteKey_OfAnAccountThatIsNoLongerTheOwner_IsRefusedAsync()
    {
        using var provider = Services(new ApplicationUser
        {
            Id = "owner",
            IsSiteOwner = false,
            NormalizedEmail = ConfiguredEmail.ToUpperInvariant(),
            SecurityStamp = Stamp
        });
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(
            Principal(nameof(ApiKeyAccessLevel.ReadWrite)),
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void IsApiKey_RecognisesOnlyAWellFormedKeyIdentity()
    {
        Assert.IsTrue(CameraAgentCredentialAccess.IsApiKey(Principal(nameof(ApiKeyAccessLevel.Read))));
        Assert.IsFalse(CameraAgentCredentialAccess.IsApiKey(Principal("session")));
        Assert.IsFalse(CameraAgentCredentialAccess.IsApiKey(Principal("unknown-access")));
        Assert.IsFalse(CameraAgentCredentialAccess.IsApiKey(new ClaimsPrincipal(new ClaimsIdentity())));
        // A session and a key on one principal are not a key principal: the identity is ambiguous.
        Assert.IsFalse(CameraAgentCredentialAccess.IsApiKey(new ClaimsPrincipal(
            [Principal(nameof(ApiKeyAccessLevel.ReadWrite)).Identities.Single(), Principal("session").Identities.Single()])));
    }

    private static ServiceProvider Services(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.Setup(candidate => candidate.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        manager.SetupGet(candidate => candidate.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(candidate => candidate.GetSecurityStampAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(Stamp);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(manager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ConfiguredEmail }));
        services.AddCameraAgentAuthorization();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(string credential)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "owner"),
            new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
            new(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, Stamp)
        ];
        return credential switch
        {
            "session" => new(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)),
            "bearer" => new(new ClaimsIdentity(
                [
                    new Claim("sub", "owner"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
                    new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, Stamp)
                ],
                CanonicalCredentialClaims.BearerAuthenticationType)),
            _ => new(new ClaimsIdentity(
                claims.Concat(
                [
                    new Claim(ApiKeyClaims.ApiKeyId, "0123456789abcdef"),
                    new Claim(ApiKeyClaims.AccessLevel, credential == "unknown-access" ? "Admin" : credential),
                    new Claim(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme)
                ]),
                ApiKeyAuthenticationOptions.AuthenticationScheme))
        };
    }
}
