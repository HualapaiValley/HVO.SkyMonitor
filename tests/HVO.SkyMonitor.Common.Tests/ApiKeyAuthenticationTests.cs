using HVO.SkyMonitor.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

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

    [TestMethod]
    [DataRow(null, "User")]
    [DataRow("subject", null)]
    [DataRow("subject", "Unknown")]
    public async Task AuthenticationRejectsIncompleteCanonicalIdentity(string? subject, string? accountType)
    {
        var result = await AuthenticateAsync(new ApiKeyValidationResult
        {
            IsValid = true,
            NameIdentifier = subject,
            AccountType = accountType,
            AccessLevel = ApiKeyAccessLevel.Read
        }, new StringValues("key")).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure?.Message, "identity is incomplete", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AuthenticationRejectsDuplicateApiKeyHeaders()
    {
        var result = await AuthenticateAsync(new ApiKeyValidationResult
        {
            IsValid = true,
            NameIdentifier = "subject",
            AccountType = "User"
        }, new StringValues(["first", "second"])).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure?.Message, "Exactly one", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AuthenticationEmitsExplicitCanonicalIdentity()
    {
        var result = await AuthenticateAsync(new ApiKeyValidationResult
        {
            IsValid = true,
            NameIdentifier = "subject",
            AccountType = "System",
            AccessLevel = ApiKeyAccessLevel.ReadWrite
        }, new StringValues("key")).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("subject", result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.AreEqual("System", result.Principal.FindFirstValue("account_type"));
        Assert.AreEqual(ApiKeyAuthenticationOptions.AuthenticationScheme, result.Principal.Identity!.AuthenticationType);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        ApiKeyValidationResult validationResult,
        StringValues headerValues)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IApiKeyValidator>(new StubApiKeyValidator(validationResult));
        services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, DefaultApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationOptions.AuthenticationScheme,
                _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers[ApiKeyAuthenticationOptions.HeaderName] = headerValues;
        return await context.AuthenticateAsync(ApiKeyAuthenticationOptions.AuthenticationScheme).ConfigureAwait(false);
    }

    private sealed class StubApiKeyValidator(ApiKeyValidationResult result) : IApiKeyValidator
    {
        public Task<ApiKeyValidationResult> ValidateAsync(string apiKey) => Task.FromResult(result);
    }
}
