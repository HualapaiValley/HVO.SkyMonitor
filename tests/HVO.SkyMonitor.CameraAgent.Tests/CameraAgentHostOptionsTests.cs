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

    [TestMethod]
    public void Validate_WhenLaneLeaseOrNameIsInvalid_ReturnsValidationErrors()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            CaptureDistribution = new CaptureDistributionOptions
            {
                LeaseSeconds = 10,
                LeaseRenewalSeconds = 6,
                SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "Invalid_Name", Enabled = true }]
            }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CaptureDistributionOptions.LeaseRenewalSeconds))));
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CaptureDistributionOptions.SecondaryLanes))));
    }

    [TestMethod]
    public void Validate_WhenDisabledLanesAreInvalidDuplicateOrRequired_ReturnsValidationErrors()
    {
        var options = new CaptureDistributionOptions
        {
            SecondaryLanes =
            [
                new SecondaryCaptureLaneOptions { Name = "standard" },
                new SecondaryCaptureLaneOptions { Name = "secondary" },
                new SecondaryCaptureLaneOptions { Name = "secondary" },
                new SecondaryCaptureLaneOptions { Name = "required", Required = true }
            ]
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsGreaterThanOrEqualTo(3, results.Count);
    }
}
