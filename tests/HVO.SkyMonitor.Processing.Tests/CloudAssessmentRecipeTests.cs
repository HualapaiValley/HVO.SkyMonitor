using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CloudAssessmentRecipeTests
{
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");

    [TestMethod]
    public async Task ComparativeInputsProduceCanonicalQuantifiedMetadata()
    {
        var current = CreateArtifact(
            "current",
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            (x, y) =>
            {
                var reference = 1000 + x * 100 + y * 10;
                return (ushort)(x < 2 ? reference : reference / 2 + 100);
            });
        var clear = CreateArtifact(
            "clear",
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var request = CreateRequest(current, clear);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        Assert.IsNull(product.Layout);
        Assert.AreEqual("application/vnd.hvo.cloud-assessment+json", product.MediaType);
        CollectionAssert.AreEqual(new[] { current.ArtifactId, clear.ArtifactId }, product.SourceArtifactIds.ToArray());
        var parsed = CloudAssessmentJson.Parse(product.Payload);
        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        Assert.AreEqual(CloudAssessmentStatus.Quantified, parsed.Assessment!.Status);
        Assert.AreEqual(CloudAssessmentQuality.Degraded, parsed.Assessment.Quality);
        Assert.AreEqual(500_000, parsed.Assessment.CoverageMillionths);
        Assert.AreEqual(0b0000_0010, parsed.Assessment.Mask!.Bits.Span[0]);
        CollectionAssert.Contains(
            parsed.Assessment.ReasonCodes.ToArray(),
            CloudAssessmentReasonCodes.EnvironmentMissing);
    }

    [TestMethod]
    public async Task MissingClearReferenceProducesInsufficientEvidenceWithoutPercentage()
    {
        var current = CreateArtifact(
            "current",
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var request = CreateRequest(current, clear: null);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var assessment = CloudAssessmentJson.Parse(outcome.Products.Single().Payload).Assessment!;
        Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, assessment.Status);
        Assert.AreEqual(CloudAssessmentQuality.Unusable, assessment.Quality);
        Assert.IsNull(assessment.CoverageMillionths);
        Assert.IsNull(assessment.ClearReference);
        Assert.IsNull(assessment.Mask);
        CollectionAssert.Contains(
            assessment.ReasonCodes.ToArray(),
            CloudAssessmentReasonCodes.MissingClearReference);
        Assert.HasCount(1, outcome.Products.Single().SourceArtifactIds);
    }

    [TestMethod]
    public async Task ExplicitPrecipitationContextContaminatesOtherwiseQuantifiedEvidence()
    {
        var current = CreateArtifact(
            "current",
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var clear = CreateArtifact(
            "clear",
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Fresh,
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            new string('E', 64),
            true);
        var environmentElement = CaptureContractJson.SerializeToElement(environment);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(environmentElement));
        var request = CreateRequest(current, clear, new ProcessingAuxiliaryInput(
            "environment",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(environmentPayload),
            Payload: environmentPayload));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var assessment = CloudAssessmentJson.Parse(outcome.Products.Single().Payload).Assessment!;
        Assert.AreEqual(CloudAssessmentStatus.Contaminated, assessment.Status);
        Assert.IsNull(assessment.CoverageMillionths);
        Assert.AreEqual(0, assessment.ConfidenceMillionths);
        CollectionAssert.Contains(
            assessment.ReasonCodes.ToArray(),
            CloudAssessmentReasonCodes.PrecipitationContamination);
    }

    [TestMethod]
    public async Task ContradictoryPrecipitationRemainsExplicitDegradedEvidence()
    {
        var current = CreateArtifact(
            "current",
            Guid.Parse("21000000-0000-0000-0000-000000000001"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var clear = CreateArtifact(
            "clear",
            Guid.Parse("21000000-0000-0000-0000-000000000002"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Contradictory,
            null,
            null,
            false,
            new string('E', 64));
        var environmentElement = CaptureContractJson.SerializeToElement(environment);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(environmentElement));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(CreateRequest(
            current,
            clear,
            new ProcessingAuxiliaryInput(
                "environment",
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                IdentitySha256: ProcessingIdentity.ComputePayloadSha256(environmentPayload),
                Payload: environmentPayload))).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var assessment = CloudAssessmentJson.Parse(outcome.Products.Single().Payload).Assessment!;
        Assert.AreEqual(EnvironmentalObservationMatchStatus.Contradictory, assessment.Environment.PrecipitationStatus);
        Assert.AreEqual(CloudAssessmentStatus.Quantified, assessment.Status);
        Assert.AreEqual(CloudAssessmentQuality.Degraded, assessment.Quality);
        CollectionAssert.Contains(
            assessment.ReasonCodes.ToArray(),
            CloudAssessmentReasonCodes.EnvironmentContradictory);
    }

    [TestMethod]
    public async Task SameCaptureDerivativeCannotServeAsClearReference()
    {
        var current = CreateArtifact(
            "current",
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var clear = CreateArtifact(
            "clear",
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            (x, y) => (ushort)(1000 + x * 100 + y * 10)) with
        {
            SourceArtifactIds = [current.ArtifactId]
        };

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            CreateRequest(current, clear)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var assessment = CloudAssessmentJson.Parse(outcome.Products.Single().Payload).Assessment!;
        Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, assessment.Status);
        Assert.IsNull(assessment.CoverageMillionths);
        CollectionAssert.Contains(
            assessment.ReasonCodes.ToArray(),
            CloudAssessmentReasonCodes.IncompatibleClearReference);
    }

    private static ProcessingExecutionRequest CreateRequest(
        ProcessingArtifact current,
        ProcessingArtifact? clear,
        ProcessingAuxiliaryInput? environment = null)
    {
        var inputs = clear is null ? new[] { current } : new[] { current, clear };
        var auxiliary = new List<ProcessingAuxiliaryInput>();
        if (clear is not null)
        {
            auxiliary.Add(new ProcessingAuxiliaryInput(
                "clear-reference",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.Calibrated("clear"),
                ArtifactId: clear.ArtifactId));
        }
        if (environment is not null)
        {
            auxiliary.Add(environment);
        }
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.CloudAssessment,
            JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
                GridColumns: 2,
                GridRows: 1,
                TransmissionThresholdMillionths: 750_000,
                MinimumReferenceSignal: 1,
                MinimumSamplesPerTile: 1)),
            ProcessingInputSelector.Calibrated("current"),
            inputs,
            "cloud-assessment-v1",
            AuxiliaryInputs: auxiliary,
            InputArtifactId: current.ArtifactId);
    }

    private static ProcessingArtifact CreateArtifact(
        string variant,
        Guid id,
        Func<int, int, ushort> value)
    {
        const int width = 4;
        const int height = 2;
        const int stride = width * 2;
        var payload = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = value(x, y);
                var offset = y * stride + x * 2;
                payload[offset] = (byte)sample;
                payload[offset + 1] = (byte)(sample >> 8);
            }
        }
        return new ProcessingArtifact(
            id,
            FrameArtifactRole.Calibrated,
            variant,
            new string('A', 64),
            "application/x-hvo-linear-frame",
            new FrameLayoutDescriptor(
                width,
                height,
                stride,
                CameraPixelFormat.Mono16,
                FrameByteOrder.LittleEndian,
                16,
                16,
                FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None,
                0,
                ushort.MaxValue,
                payload.Length),
            payload,
            DateTimeOffset.Parse("2026-01-15T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(1),
            Compatibility);
    }
}
