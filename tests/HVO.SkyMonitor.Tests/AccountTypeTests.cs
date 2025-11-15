using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using HVO.SkyMonitor.Data;

namespace HVO.SkyMonitor.Tests;

[TestClass]
public class AccountTypeTests
{
    [TestMethod]
    public async Task UserAccount_CanBeCreated()
    {
        // Arrange
        var user = new ApplicationUser
        {
            UserName = "test@example.com",
            Email = "test@example.com",
            AccountType = AccountType.User
        };

        // Assert
        Assert.AreEqual(AccountType.User, user.AccountType);
        Assert.IsNotNull(user.ApiKeys);
    }

    [TestMethod]
    public async Task SystemAccount_CanBeCreated()
    {
        // Arrange
        var user = new ApplicationUser
        {
            UserName = "service@example.com",
            Email = "service@example.com",
            AccountType = AccountType.System
        };

        // Assert
        Assert.AreEqual(AccountType.System, user.AccountType);
        Assert.IsNotNull(user.ApiKeys);
    }

    [TestMethod]
    public void AccountType_DefaultsToUser()
    {
        // Arrange & Act
        var user = new ApplicationUser
        {
            UserName = "default@example.com",
            Email = "default@example.com"
        };

        // Assert
        Assert.AreEqual(AccountType.User, user.AccountType);
    }
}
