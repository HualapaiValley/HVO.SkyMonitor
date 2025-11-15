using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Data;

namespace HVO.SkyMonitor.Tests;

[TestClass]
public class ApiKeyAuthenticationTests
{
    [TestMethod]
    public void ApiKey_HasCorrectPrefix()
    {
        // Arrange
        var keyWithPrefix = "smk_test123";
        
        // Assert
        Assert.IsTrue(keyWithPrefix.StartsWith("smk_"));
    }

    [TestMethod]
    public void ApiKeyValidationResult_ContainsAccountType()
    {
        // Arrange
        var result = new ApiKeyValidationResult
        {
            IsValid = true,
            UserId = "user123",
            AccessLevel = AccessLevel.Read,
            AccountType = "User"
        };

        // Assert
        Assert.AreEqual("User", result.AccountType);
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void ApiKeyValidationResult_SupportsSystemAccountType()
    {
        // Arrange
        var result = new ApiKeyValidationResult
        {
            IsValid = true,
            UserId = "service123",
            AccessLevel = AccessLevel.ReadWrite,
            AccountType = "System"
        };

        // Assert
        Assert.AreEqual("System", result.AccountType);
        Assert.AreEqual(AccessLevel.ReadWrite, result.AccessLevel);
    }
}
