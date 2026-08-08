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
