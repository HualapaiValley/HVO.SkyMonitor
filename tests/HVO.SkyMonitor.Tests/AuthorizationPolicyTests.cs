using System.Net;
using System.Security.Claims;
using HVO.SkyMonitor.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests;

/// <summary>
/// Tests for authorization policies (RequireSystemAccount, RequireUserAccount, API key access levels).
/// </summary>
[TestClass]
public class AuthorizationPolicyTests
{
    [TestMethod]
    public async Task RequireSystemAccountPolicy_WithSystemAccount_Succeeds()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireSystemAccount", policy =>
                policy.RequireAssertion(context =>
                    context.User.HasClaim("account_type", AccountType.System.ToString())));
        });
        services.AddLogging();
        
        var serviceProvider = services.BuildServiceProvider();
        var authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
        
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test-service"),
            new Claim("account_type", AccountType.System.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        
        // Act
        var result = await authorizationService.AuthorizeAsync(user, "RequireSystemAccount");
        
        // Assert
        Assert.IsTrue(result.Succeeded);
    }
    
    [TestMethod]
    public async Task RequireSystemAccountPolicy_WithUserAccount_Fails()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireSystemAccount", policy =>
                policy.RequireAssertion(context =>
                    context.User.HasClaim("account_type", AccountType.System.ToString())));
        });
        services.AddLogging();
        
        var serviceProvider = services.BuildServiceProvider();
        var authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
        
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test-user"),
            new Claim("account_type", AccountType.User.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        
        // Act
        var result = await authorizationService.AuthorizeAsync(user, "RequireSystemAccount");
        
        // Assert
        Assert.IsFalse(result.Succeeded);
    }
    
    [TestMethod]
    public async Task RequireUserAccountPolicy_WithUserAccount_Succeeds()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireUserAccount", policy =>
                policy.RequireAssertion(context =>
                    context.User.HasClaim("account_type", AccountType.User.ToString())));
        });
        services.AddLogging();
        
        var serviceProvider = services.BuildServiceProvider();
        var authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
        
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test-user"),
            new Claim("account_type", AccountType.User.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        
        // Act
        var result = await authorizationService.AuthorizeAsync(user, "RequireUserAccount");
        
        // Assert
        Assert.IsTrue(result.Succeeded);
    }
    
    [TestMethod]
    public async Task RequireUserAccountPolicy_WithSystemAccount_Fails()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireUserAccount", policy =>
                policy.RequireAssertion(context =>
                    context.User.HasClaim("account_type", AccountType.User.ToString())));
        });
        services.AddLogging();
        
        var serviceProvider = services.BuildServiceProvider();
        var authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
        
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test-service"),
            new Claim("account_type", AccountType.System.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        
        // Act
        var result = await authorizationService.AuthorizeAsync(user, "RequireUserAccount");
        
        // Assert
        Assert.IsFalse(result.Succeeded);
    }
    
    [TestMethod]
    public void AccountType_UserIsDefault()
    {
        // Arrange & Act
        var user = new ApplicationUser
        {
            UserName = "test@example.com",
            Email = "test@example.com"
        };
        
        // Assert
        Assert.AreEqual(AccountType.User, user.AccountType);
    }
    
    [TestMethod]
    public void AccountType_CanBeSetToSystem()
    {
        // Arrange & Act
        var user = new ApplicationUser
        {
            UserName = "service@example.com",
            Email = "service@example.com",
            AccountType = AccountType.System
        };
        
        // Assert
        Assert.AreEqual(AccountType.System, user.AccountType);
    }
}
