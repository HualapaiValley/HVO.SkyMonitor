using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
public class LocalIdentityOptionsTests
{
    [TestMethod]
    public void Validate_WithDefaultValues_FailsWithoutExplicitPassword()
    {
        var options = new LocalIdentityOptions();
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(options);

        var isValid = Validator.TryValidateObject(options, context, validationResults, validateAllProperties: true);

        Assert.IsFalse(isValid);
        Assert.IsTrue(validationResults.Count >= 1);
    }

    [TestMethod]
    public void Validate_WithIdentityCompatiblePassword_Succeeds()
    {
        var options = new LocalIdentityOptions
        {
            AdminEmail = "owner@cameraagent.test",
            AdminPassword = "IdentityOwner!123"
        };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            validationResults,
            validateAllProperties: true);

        Assert.IsTrue(isValid, string.Join("; ", validationResults));
    }

    [TestMethod]
    public void Validate_WhenRequiredFieldsMissing_Fails()
    {
        var options = new LocalIdentityOptions
        {
            AdminEmail = string.Empty,
            AdminPassword = "short"
        };

        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(options);

        var isValid = Validator.TryValidateObject(options, context, validationResults, validateAllProperties: true);

        Assert.IsFalse(isValid);
        Assert.IsTrue(validationResults.Any(result => result.MemberNames.Contains(nameof(LocalIdentityOptions.AdminEmail))));
    }

    [TestMethod]
    public void Validate_WithExplicitSeededOwnerOptIn_AllowsMissingPassword()
    {
        var options = new LocalIdentityOptions { AllowMissingAdminPassword = true };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            validationResults,
            validateAllProperties: true);

        Assert.IsTrue(isValid, string.Join("; ", validationResults));
    }

    [TestMethod]
    public void Validate_WithWeakPassword_Fails()
    {
        var options = new LocalIdentityOptions
        {
            AdminEmail = "owner@cameraagent.test",
            AdminPassword = "short"
        };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            validationResults,
            validateAllProperties: true);

        Assert.IsFalse(isValid);
        Assert.IsTrue(validationResults.Any(result => result.MemberNames.Contains(nameof(LocalIdentityOptions.AdminPassword))));
    }

    [TestMethod]
    public void Defaults_SeedTheAdminUserNameWithANonRoutableEmail()
    {
        var options = new LocalIdentityOptions();

        Assert.AreEqual("admin", options.AdminUserName);
        Assert.AreEqual("owner@cameraagent.local", options.AdminEmail);
    }

    [TestMethod]
    [DataRow("skymonitor")]
    [DataRow("abcdefgh")]
    [DataRow("12345678")]
    public void Validate_WithEightOrMoreCharactersAndNoCharacterClasses_Succeeds(string password)
    {
        var options = new LocalIdentityOptions { AdminPassword = password };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsTrue(valid, string.Join("; ", results));
    }

    [TestMethod]
    public void Validate_WithSevenCharacterPassword_Fails()
    {
        var options = new LocalIdentityOptions { AdminPassword = "abcdefg" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        var failure = results.Single(result => result.MemberNames.Contains(nameof(LocalIdentityOptions.AdminPassword)));
        Assert.AreEqual("AdminPassword must be at least 8 characters.", failure.ErrorMessage);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("owner@cameraagent.local")]
    [DataRow("site owner")]
    public void Validate_WithUnusableAdminUserName_Fails(string userName)
    {
        var options = new LocalIdentityOptions
        {
            AdminPassword = "skymonitor",
            AdminUserName = userName
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(LocalIdentityOptions.AdminUserName))));
    }

    [TestMethod]
    public void Validate_WithCustomCookieName_Succeeds()
    {
        var options = new LocalIdentityOptions
        {
            AdminPassword = "IdentityOwner!123",
            CookieName = "CameraAgent.Hualapai.Auth"
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsTrue(valid, string.Join("; ", results));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("CameraAgent Auth")]
    [DataRow("CameraAgent;Auth")]
    public void Validate_WithUnsafeCookieName_Fails(string cookieName)
    {
        var options = new LocalIdentityOptions
        {
            AdminPassword = "IdentityOwner!123",
            CookieName = cookieName
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(LocalIdentityOptions.CookieName))));
    }
}
