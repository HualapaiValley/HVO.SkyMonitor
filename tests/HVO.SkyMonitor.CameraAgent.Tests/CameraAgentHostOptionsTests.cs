using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentHostOptionsTests
{
    [TestMethod]
    public void CentralIntegration_DefaultsToEnabled()
    {
        Assert.AreEqual(CentralIntegrationMode.Enabled, new CameraAgentHostOptions().CentralIntegration.Mode);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(121)]
    public void TransientDeliveryRequestTimeoutMustBeBounded(int seconds)
    {
        var options = new TransientDetectionOptions { DeliveryRequestTimeoutSeconds = seconds };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result =>
            result.MemberNames.Contains(nameof(TransientDetectionOptions.DeliveryRequestTimeoutSeconds))));
    }

    [TestMethod]
    public void Validate_WhenCentralIntegrationModeIsInvalid_ReturnsValidationError()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            CentralIntegration = new CentralIntegrationOptions { Mode = (CentralIntegrationMode)99 }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CentralIntegrationOptions.Mode))));
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Central)]
    [DataRow(TransientOperatingMode.Hybrid)]
    public void Validate_WhenCentralTransientModeIsConfiguredStandalone_ReturnsValidationError(
        TransientOperatingMode mode)
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = true },
            TransientDetection = new TransientDetectionOptions { Mode = mode }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CameraAgentHostOptions.CentralIntegration))));
    }

    [TestMethod]
    public void CaptureLanePolicy_DisabledCentralIntegrationSuppressesConfiguredUploadLane()
    {
        var policy = new CaptureLanePolicy(Options.Create(new CameraAgentHostOptions
        {
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = true }
        }));

        var upload = policy.Definitions.Single(static lane => lane.Name == "upload");
        Assert.IsFalse(upload.Enabled);
        Assert.IsTrue(upload.Required);
    }

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

    [TestMethod]
    public void Validate_WhenTransientModeOrPolicyIsInvalid_ReturnsValidationErrors()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            TransientDetection = new TransientDetectionOptions
            {
                Mode = (TransientOperatingMode)99,
                Required = true
            },
            CaptureDistribution = new CaptureDistributionOptions
            {
                SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "transient", Enabled = true }]
            }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(TransientDetectionOptions.Mode))));
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CaptureDistributionOptions.SecondaryLanes))));
    }

    [TestMethod]
    public void Validate_WhenDisabledTransientLaneIsRequired_ReturnsValidationError()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            TransientDetection = new TransientDetectionOptions
            {
                Mode = TransientOperatingMode.Off,
                Required = true
            }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(TransientDetectionOptions.Required))));
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Central)]
    [DataRow(TransientOperatingMode.Hybrid)]
    public void Validate_WhenCentralDeliveryModeHasNoUploadLane_ReturnsValidationError(
        TransientOperatingMode mode)
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            TransientDetection = new TransientDetectionOptions { Mode = mode },
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = false }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(nameof(CameraAgentHostOptions.CaptureDistribution))));
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Off, false)]
    [DataRow(TransientOperatingMode.Central, false)]
    [DataRow(TransientOperatingMode.Edge, true)]
    [DataRow(TransientOperatingMode.Hybrid, true)]
    public void CaptureLanePolicy_RegistersTransientLaneOnlyForEdgeModes(
        TransientOperatingMode mode,
        bool expected)
    {
        var policy = new CaptureLanePolicy(Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            TransientDetection = new TransientDetectionOptions { Mode = mode, Required = expected }
        }));

        var transient = policy.Definitions.SingleOrDefault(static lane => lane.Name == "transient");

        Assert.AreEqual(expected, transient is not null);
        if (expected)
        {
            Assert.IsTrue(transient!.Enabled);
            Assert.IsTrue(transient.Required);
            Assert.IsTrue(transient.Ordered);
        }
    }

    [TestMethod]
    public void AddCameraAgentInfrastructure_EdgeModeRegistersRunnableTransientHandler()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = "raw-ingress",
                ["CameraAgent:TransientDetection:Mode"] = "Edge"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<CaptureLanePolicy>();
        var handlers = provider.GetServices<ICaptureLaneHandler>().ToArray();

        Assert.IsTrue(policy.Definitions.Any(static lane => lane.Name == "transient" && lane.Enabled && lane.Ordered));
        Assert.IsTrue(handlers.Any(static handler => handler.Lane == "transient"));
        Assert.IsNotNull(provider.GetRequiredService<ITransientRuntimeManagement>());
    }

    [TestMethod]
    public void Validate_WhenEnvironmentalRequestCanOutliveSafeLeaseWindow_ReturnsValidationError()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "raw-ingress",
            EnvironmentalDelivery = new EnvironmentalObservationDeliveryOptions
            {
                LeaseSeconds = 10,
                RequestTimeoutSeconds = 6
            }
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(results.Any(result => result.MemberNames.Contains(
            nameof(EnvironmentalObservationDeliveryOptions.RequestTimeoutSeconds))));
    }
}
