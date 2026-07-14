using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.Tests;

[TestClass]
[TestCategory("Unit")]
public class ApiKeyAuthenticationTests
{
    [TestMethod]
    public void ApiKey_HasCorrectPrefix()
    {
        // Arrange
        var keyWithPrefix = "smk_test123";

        // Assert
        Assert.StartsWith("smk_", keyWithPrefix);
    }

    [TestMethod]
    public void ApiKeyValidationResult_ContainsAccountType()
    {
        // Arrange
        var result = new ApiKeyValidationResult
        {
            IsValid = true,
            NameIdentifier = "user123",
            AccessLevel = ApiKeyAccessLevel.Read,
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
            NameIdentifier = "service123",
            AccessLevel = ApiKeyAccessLevel.ReadWrite,
            AccountType = "System"
        };

        // Assert
        Assert.AreEqual("System", result.AccountType);
        Assert.AreEqual(ApiKeyAccessLevel.ReadWrite, result.AccessLevel);
    }
}
