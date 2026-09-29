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
    public void DerivedProductLifecycleDefaultsAreBoundedAndValid()
    {
        var options = new CameraAgentHostOptions();
        var lifecycle = options.DerivedProductLifecycle;

        Assert.IsInRange(16, 4096, lifecycle.ReconciliationBatchSize);
        Assert.IsInRange(1, 365, lifecycle.DiagnosticRetentionDays);
        Assert.IsInRange(1, 1440, lifecycle.OrphanRecoveryWindowMinutes);
        Assert.IsTrue(Validator.TryValidateObject(lifecycle, new ValidationContext(lifecycle), [], true));
    }

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
    public void SiteDefaults_AreValidAndKeepTheProjectionBoundAndOnlineMap()
    {
        var options = new CameraAgentHostOptions { RawIngressRoot = "raw-ingress" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsTrue(valid, string.Join("; ", results.Select(static result => result.ErrorMessage)));
        Assert.IsNull(options.DisplayName);
        Assert.AreEqual(200, options.SkyMap.MaximumObjects);
        Assert.IsTrue(options.SiteMap.Enabled);
        Assert.AreEqual("https://tile.openstreetmap.org/{z}/{x}/{y}.png", options.SiteMap.TileTemplate);
        Assert.AreEqual(13, options.SiteMap.Zoom);
    }

    [TestMethod]
    [DataRow("display-blank", nameof(CameraAgentHostOptions.DisplayName))]
    [DataRow("display-control", nameof(CameraAgentHostOptions.DisplayName))]
    [DataRow("display-long", nameof(CameraAgentHostOptions.DisplayName))]
    [DataRow("objects-zero", nameof(SkyMapOptions.MaximumObjects))]
    [DataRow("objects-high", nameof(SkyMapOptions.MaximumObjects))]
    [DataRow("tiles-http", nameof(SiteMapOptions.TileTemplate))]
    [DataRow("tiles-placeholder", nameof(SiteMapOptions.TileTemplate))]
    [DataRow("tiles-relative", nameof(SiteMapOptions.TileTemplate))]
    [DataRow("attribution-http", nameof(SiteMapOptions.AttributionLink))]
    [DataRow("attribution-control", nameof(SiteMapOptions.Attribution))]
    [DataRow("zoom-zero", nameof(SiteMapOptions.Zoom))]
    [DataRow("zoom-high", nameof(SiteMapOptions.Zoom))]
    public void Validate_WhenASiteSettingIsOutOfBounds_NamesIt(string scenario, string member)
    {
        var options = scenario switch
        {
            "display-blank" => new CameraAgentHostOptions { RawIngressRoot = "raw-ingress", DisplayName = "  " },
            "display-control" => new CameraAgentHostOptions { RawIngressRoot = "raw-ingress", DisplayName = "East\ndome" },
            "display-long" => new CameraAgentHostOptions { RawIngressRoot = "raw-ingress", DisplayName = new string('n', 81) },
            "objects-zero" => new CameraAgentHostOptions { RawIngressRoot = "raw-ingress", SkyMap = new SkyMapOptions { MaximumObjects = 0 } },
            "objects-high" => new CameraAgentHostOptions { RawIngressRoot = "raw-ingress", SkyMap = new SkyMapOptions { MaximumObjects = 5001 } },
            "tiles-http" => WithSiteMap(new SiteMapOptions { TileTemplate = "http://tiles.example.test/{z}/{x}/{y}.png" }),
            "tiles-placeholder" => WithSiteMap(new SiteMapOptions { TileTemplate = "https://tiles.example.test/{z}/{x}.png" }),
            "tiles-relative" => WithSiteMap(new SiteMapOptions { TileTemplate = "/tiles/{z}/{x}/{y}.png" }),
            "attribution-http" => WithSiteMap(new SiteMapOptions { AttributionLink = new Uri("http://tiles.example.test/terms") }),
            "attribution-control" => WithSiteMap(new SiteMapOptions { Attribution = "Tiles\u0007" }),
            "zoom-zero" => WithSiteMap(new SiteMapOptions { Zoom = 0 }),
            _ => WithSiteMap(new SiteMapOptions { Zoom = 19 })
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.IsFalse(valid);
        Assert.IsTrue(
            results.Any(result => result.MemberNames.Contains(member)),
            string.Join("; ", results.Select(static result => $"{result.ErrorMessage} [{string.Join(",", result.MemberNames)}]")));
    }

    [TestMethod]
    public void Validate_WhenTheOnlineMapIsOff_IgnoresTheTileAddress()
    {
        var options = WithSiteMap(new SiteMapOptions
        {
            Enabled = false,
            TileTemplate = "http://tiles.example.test/unused"
        });

        Assert.IsTrue(Validator.TryValidateObject(
            options, new ValidationContext(options), [], validateAllProperties: true));
    }

    private static CameraAgentHostOptions WithSiteMap(SiteMapOptions siteMap)
        => new() { RawIngressRoot = "raw-ingress", SiteMap = siteMap };

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
