using System;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
public sealed class CentralIdentityOptionsValidatorTests
{
    private readonly CentralIdentityOptionsValidator _validator = new();

    [TestMethod]
    public void Validate_ReturnsSuccess_ForValidOptions()
    {
        var result = _validator.Validate(Options.DefaultName, CreateValidOptions());
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Validate_Fails_WhenServiceUrlIsRelative()
    {
        var options = CreateValidOptions();
        options.ServiceUrl = new Uri("/relative", UriKind.Relative);

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "ServiceUrl", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_Fails_WhenClientCredentialsMissing()
    {
        var options = CreateValidOptions();
        options.ClientCredentials = null;

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "ClientCredentials", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_Fails_WhenApiKeyMissingInApiKeyMode()
    {
        var options = CreateValidOptions();
        options.Mode = AuthenticationMode.ApiKey;
        options.ApiKey = null;

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "ApiKey:Key", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_Fails_WhenInteractivePathsMissingSlash()
    {
        var options = CreateValidOptions();
        options.InteractiveClient!.CallbackPath = "signin";

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "CallbackPath", StringComparison.Ordinal);
    }

    private static CentralIdentityOptions CreateValidOptions()
    {
        var options = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://identity.example.com", UriKind.Absolute),
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "camera-agent",
                ClientSecret = "secret-value"
            },
            InteractiveClient = new InteractiveClientOptions
            {
                ClientId = "interactive-client",
                ClientSecret = "interactive-secret",
                CallbackPath = "/signin-central",
                SignedOutCallbackPath = "/signout-callback-central",
                RemoteSignOutPath = "/signout-central"
            }
        };

        options.ClientCredentials!.Scopes.Add("api.camera");
        options.InteractiveClient!.Scopes.Add("api.camera");

        return options;
    }
}
