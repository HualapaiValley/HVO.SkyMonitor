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
    public void Validate_WithDefaultValues_Succeeds()
    {
        var options = new LocalIdentityOptions();
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(options);

        var isValid = Validator.TryValidateObject(options, context, validationResults, validateAllProperties: true);

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
        Assert.AreEqual(2, validationResults.Count);
    }
}
