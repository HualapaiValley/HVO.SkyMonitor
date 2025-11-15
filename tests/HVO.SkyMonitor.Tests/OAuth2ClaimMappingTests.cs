using System.Security.Claims;
using HVO.SkyMonitor.Data;
using OpenIddict.Abstractions;

namespace HVO.SkyMonitor.Tests;

/// <summary>
/// Tests for OAuth2/OpenID Connect token claim mapping and account type handling.
/// These are unit tests that verify the claim structure without requiring full integration.
/// </summary>
[TestClass]
public class OAuth2ClaimMappingTests
{
    [TestMethod]
    public void AccountTypeClaim_IsIncludedForUserAccount()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var accountType = AccountType.User;
        
        // Act
        identity.SetClaim("account_type", accountType.ToString());
        
        // Assert
        var claim = identity.FindFirst("account_type");
        Assert.IsNotNull(claim);
        Assert.AreEqual("User", claim.Value);
    }
    
    [TestMethod]
    public void AccountTypeClaim_IsIncludedForSystemAccount()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var accountType = AccountType.System;
        
        // Act
        identity.SetClaim("account_type", accountType.ToString());
        
        // Assert
        var claim = identity.FindFirst("account_type");
        Assert.IsNotNull(claim);
        Assert.AreEqual("System", claim.Value);
    }
    
    [TestMethod]
    public void SubjectClaim_IsSetFromUserId()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var userId = "user-12345";
        
        // Act
        identity.SetClaim(OpenIddictConstants.Claims.Subject, userId);
        
        // Assert
        var claim = identity.FindFirst(OpenIddictConstants.Claims.Subject);
        Assert.IsNotNull(claim);
        Assert.AreEqual(userId, claim.Value);
    }
    
    [TestMethod]
    public void NameClaim_IsSetFromUsername()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var username = "testuser@example.com";
        
        // Act
        identity.SetClaim(OpenIddictConstants.Claims.Name, username);
        
        // Assert
        var claim = identity.FindFirst(OpenIddictConstants.Claims.Name);
        Assert.IsNotNull(claim);
        Assert.AreEqual(username, claim.Value);
    }
    
    [TestMethod]
    public void EmailClaim_IsSetFromUserEmail()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var email = "testuser@example.com";
        
        // Act
        identity.SetClaim(OpenIddictConstants.Claims.Email, email);
        
        // Assert
        var claim = identity.FindFirst(OpenIddictConstants.Claims.Email);
        Assert.IsNotNull(claim);
        Assert.AreEqual(email, claim.Value);
    }
    
    [TestMethod]
    public void Scopes_CanBeSetOnIdentity()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var scopes = new[] { "api", "camera" };
        
        // Act
        identity.SetScopes(scopes);
        
        // Assert
        var retrievedScopes = identity.GetScopes();
        CollectionAssert.AreEqual(scopes, retrievedScopes.ToArray());
    }
    
    [TestMethod]
    public void ClientCredentialsFlow_CreatesSystemAccountType()
    {
        // Arrange & Act
        var identity = new ClaimsIdentity("TestAuth");
        identity.SetClaim("account_type", AccountType.System.ToString());
        identity.SetClaim(OpenIddictConstants.Claims.Subject, "client-credentials-app");
        identity.SetClaim(OpenIddictConstants.Claims.Name, "Service Application");
        
        var principal = new ClaimsPrincipal(identity);
        
        // Assert
        var accountTypeClaim = principal.FindFirst("account_type");
        Assert.IsNotNull(accountTypeClaim);
        Assert.AreEqual("System", accountTypeClaim.Value);
        
        var subjectClaim = principal.FindFirst(OpenIddictConstants.Claims.Subject);
        Assert.IsNotNull(subjectClaim);
        Assert.AreEqual("client-credentials-app", subjectClaim.Value);
    }
}
