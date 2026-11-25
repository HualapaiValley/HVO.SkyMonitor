using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Services")]
public sealed class ProcessingNodeSectionRegistryTests
{
    [TestMethod]
    public void EveryRegisteredOptionalStepHasKnownSectionAndDedicatedDescriptor()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-section-registry");
        try
        {
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var registrations = provider.GetServices<CaptureProcessingStepRegistration>().ToArray();
            Assert.IsNotEmpty(registrations);

            var slugsByNonSceneAlias = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in registrations)
            {
                if (ExposureMeasurementProducts.IsSystemManagedStepAlias(registration.Alias))
                {
                    Assert.IsNull(ProcessingNodeOptionsForm.DescriptorFor(registration.Alias), registration.Alias);
                    var stage = ProcessingNodeSectionRegistry.DescribeBuiltInStage(registration.Alias);
                    Assert.AreEqual(registration.Alias, stage.StepAlias);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(stage.Title));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(stage.RoleSummary));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(stage.FixedSettingsSummary));
                    continue;
                }

                var descriptor = ProcessingNodeOptionsForm.DescriptorFor(registration.Alias);
                Assert.IsNotNull(descriptor, $"Missing descriptor for '{registration.Alias}'.");

                var slug = ProcessingNodeSectionRegistry.SectionForAlias(registration.Alias);
                Assert.IsTrue(ProcessingNodeSectionRegistry.IsKnownSection(slug), $"Section '{slug}' for '{registration.Alias}' must be known.");
                Assert.IsTrue(ProcessingNodeSectionRegistry.MatchesSection(slug, registration.Alias),
                    $"Section '{slug}' must match alias '{registration.Alias}'.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(ProcessingNodeSectionRegistry.FindSection(slug)!.MissingNodeMessage));

                if (registration.Alias is not (ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias or SceneStylePresentation.StepAlias))
                {
                    Assert.AreNotEqual("constellations", slug, $"'{registration.Alias}' must not fall back to the constellations anchor.");
                    Assert.IsTrue(slugsByNonSceneAlias.Add(slug), $"Section slug '{slug}' must be unique (collision on '{registration.Alias}').");
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CenteredOptionalAnalysisAliasesHaveDistinctSectionSlugs()
    {
        Assert.AreEqual("exposure-stellar-associations",
            ProcessingNodeSectionRegistry.SectionForAlias(ExposureAssociationProducts.StepAlias));
        Assert.AreEqual("exposure-measured-astrometry",
            ProcessingNodeSectionRegistry.SectionForAlias(CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias));
        Assert.IsTrue(ProcessingNodeSectionRegistry.IsKnownSection("exposure-stellar-associations"));
        Assert.IsTrue(ProcessingNodeSectionRegistry.IsKnownSection("exposure-measured-astrometry"));
        Assert.IsFalse(ProcessingNodeSectionRegistry.MatchesSection("constellations", ExposureAssociationProducts.StepAlias));
        Assert.IsFalse(ProcessingNodeSectionRegistry.MatchesSection("constellations", CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias));
    }

    [TestMethod]
    [DataRow(null, "linear-normalization-none-v1")]
    [DataRow("""{"strategy":"None"}""", "linear-normalization-none-v1")]
    [DataRow("""{"strategy":"SyntheticReferences"}""", "reference-calibration-linear16-v2")]
    [DataRow("""{"Strategy":"CalibrationLibrary"}""", "reference-calibration-linear16-v2")]
    public void CalibrationDescriptorResolutionMatchesEffectiveStrategyVersion(string? json, string expectedVersion)
    {
        JsonElement? options = json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
        var descriptor = ProcessingNodeOptionsForm.DescriptorFor("Calibration", options);
        Assert.IsNotNull(descriptor);
        Assert.AreEqual(expectedVersion, descriptor.StepVersion);
    }
}
