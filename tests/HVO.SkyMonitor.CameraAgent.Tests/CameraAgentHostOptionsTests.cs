using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentHostOptionsTests
{
    [TestMethod]
    public void Validate_WhenUploadMaximumIsBelowInitial_ReturnsValidationError()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            UploadRetryInitialDelaySeconds = 60,
            UploadRetryMaximumDelaySeconds = 30
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CameraAgentHostOptions.UploadRetryMaximumDelaySeconds))));
    }

    [TestMethod]
    public void Validate_WhenRawIngressRootIsMissing_ReturnsValidationError()
    {
        var options = new CameraAgentHostOptions();
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CameraAgentHostOptions.RawIngressRoot))));
    }
}
