using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class WeatherCloudOverlayRecipeTests
{
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");

    [TestMethod]
    public async Task ExplicitPreviewAssessmentAndEnvironmentProduceAnnotatedPreview()
    {
        var environmentInput = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(environmentInput)));
        var environmentIdentity = ProcessingIdentity.ComputePayloadSha256(environmentPayload);
        var preview = CreatePreview();
        var assessmentArtifact = CreateAssessment(environmentInput with
        {
            InputIdentitySha256 = environmentIdentity
        });
        ProcessingAuxiliaryInput[] auxiliary =
        [
            new(
                "assessment",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.RecipeResult(
                    FrameArtifactRole.Metadata,
                    assessmentArtifact.Variant,
                    assessmentArtifact.RecipeIdentitySha256),
                ArtifactId: assessmentArtifact.ArtifactId),
            new(
                "environment",
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                IdentitySha256: environmentIdentity,
                Payload: environmentPayload)
        ];
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.WeatherCloudOverlay,
            JsonSerializer.SerializeToElement(new WeatherCloudOverlayOptions(DrawLabels: false)),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                preview.Variant,
                preview.RecipeIdentitySha256),
            [preview, assessmentArtifact],
            "weather-cloud-overlay-v1",
            AuxiliaryInputs: auxiliary,
            InputArtifactId: preview.ArtifactId);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, product.Role);
        Assert.AreEqual(CameraPixelFormat.Mono8, product.Layout!.PixelFormat);
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 },
            product.Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { preview.ArtifactId, assessmentArtifact.ArtifactId },
            product.SourceArtifactIds.ToArray());
    }

    private static ProcessingArtifact CreatePreview()
        => new(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            FrameArtifactRole.Preview,
            "preview",
            new string('A', 64),
            "application/x-hvo-packed-image",
            new FrameLayoutDescriptor(
                4, 2, 4, CameraPixelFormat.Mono8,
                FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, null, byte.MaxValue, 8),
            new byte[8],
            DateTimeOffset.Parse("2026-01-15T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(1),
            Compatibility);

    private static ProcessingArtifact CreateAssessment(CloudAssessmentEnvironmentV1 environment)
    {
        var artifactId = Guid.Parse("30000000-0000-0000-0000-000000000002");
        var payload = CloudAssessmentJson.Serialize(new CloudAssessmentV1(
            CloudAssessmentV1.CurrentSchemaVersion,
            CloudAssessmentStatus.Quantified,
            CloudAssessmentQuality.Degraded,
            [CloudAssessmentReasonCodes.EnvironmentMissing],
            500_000,
            500_000,
            new CloudAssessmentGridV1(2, 1, 750_000, 2, 1, 8, 4),
            [
                new CloudAssessmentRegionV1(0, 0, 0, 0, 2, 2, 4, 4, 0, 1_000_000, false),
                new CloudAssessmentRegionV1(1, 0, 2, 0, 2, 2, 4, 4, 0, 500_000, true)
            ],
            new CloudAssessmentMaskV1(
                CloudAssessmentMaskV1.RowMajorLsbFirst,
                2,
                1,
                new byte[] { 0b0000_0010 }),
            new CloudAssessmentSourceV1(
                Guid.Parse("30000000-0000-0000-0000-000000000010"),
                FrameArtifactRole.Calibrated,
                "current",
                new string('C', 64)),
            new CloudAssessmentSourceV1(
                Guid.Parse("30000000-0000-0000-0000-000000000011"),
                FrameArtifactRole.Calibrated,
                "clear",
                new string('D', 64)),
            new CloudAssessmentCalibrationV1(0, ushort.MaxValue, ushort.MaxValue, "cal", "mask", "sensor", "processing"),
            environment,
            new string('E', 64),
            [new ProcessingAlgorithmIdentity("cloud-transmission", "v1")]));
        return new ProcessingArtifact(
            artifactId,
            FrameArtifactRole.Metadata,
            "cloud-assessment-v1",
            new string('E', 64),
            "application/vnd.hvo.cloud-assessment+json",
            null,
            payload,
            DateTimeOffset.Parse("2026-01-15T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(1),
            Compatibility);
    }
}
