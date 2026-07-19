using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.AgentCore;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
public sealed class LogicHostProcessingConformanceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterProducesCanonicalPreviewFixture()
    {
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());

        var outcome = await adapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            request.RecipeName,
            request.Options,
            request.Input,
            request.OutputVariant).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        ProcessingConformanceFixture.AssertProduct(outcome.Products.Single());

        var jpeg = await adapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.EncodedPreview,
            System.Text.Json.JsonSerializer.SerializeToElement(new EncodedPreviewOptions()),
            ProcessingInputSelector.Raw("source"),
            "jpeg").ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, jpeg.Status);
        Assert.AreEqual(HVO.SkyMonitor.Imaging.JpegImageCodec.MediaType, jpeg.Products.Single().MediaType);

        var invalidChecksum = descriptor with
        {
            Artifact = descriptor.Artifact with { ChecksumSha256 = new string('0', 64) }
        };
        var invalid = await adapter.ExecuteAsync(
            invalidChecksum,
            ProcessingConformanceFixture.Payload,
            request.RecipeName,
            request.Options,
            request.Input,
            request.OutputVariant).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalid.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, invalid.ReasonCode);
        Assert.AreEqual("payload", invalid.Field);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterExecutesOrderedRollingWindow()
    {
        var firstPayload = new byte[] { 10, 0, 20, 0, 30, 0, 40, 0 };
        var secondPayload = new byte[] { 30, 0, 40, 0, 50, 0, 60, 0 };
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var first = CreateSource(baseline, firstPayload, 1);
        var second = CreateSource(baseline, secondPayload, 2);
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var outcome = await adapter.ExecuteAsync(
            [new LogicHostProcessingInput(first, firstPayload), new LogicHostProcessingInput(second, secondPayload)],
            BuiltInProcessingRecipes.RollingMean,
            System.Text.Json.JsonSerializer.SerializeToElement(new RollingMeanOptions(2)),
            ProcessingInputSelector.Raw("source"),
            "mean-2").ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(
            new byte[] { 20, 0, 30, 0, 40, 0, 50, 0 },
            outcome.Products.Single().Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.Artifact.ArtifactId, second.Artifact.ArtifactId },
            outcome.Products.Single().SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EquivalentAnnotationRequestMatchesCameraAgentAdapter()
    {
        var artifact = ProcessingConformanceFixture.CreateProcessingArtifact();
        var options = System.Text.Json.JsonSerializer.SerializeToElement(
            new AnnotationRecipeOptions(OutputEncoding: "Packed"));
        var selector = ProcessingInputSelector.Raw("source");
        var annotation = new ProcessingAnnotationInput(
            [new ProjectedAnnotationObject("fixture", "Fixture", new PixelPoint(1, 1), true, true)],
            [],
            new PreviewTransform(1, 1),
            null,
            new string('A', 64));
        var cameraAgent = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var logicHost = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var cameraOutcome = await cameraAgent.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            options,
            selector,
            [artifact],
            "annotated-conformance",
            annotation), CancellationToken.None).ConfigureAwait(false);
        var logicOutcome = await logicHost.ExecuteAsync(
            ProcessingConformanceFixture.CreateDescriptor(),
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.Annotation,
            options,
            selector,
            "annotated-conformance",
            annotation).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, cameraOutcome.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, logicOutcome.Status);
        var expected = cameraOutcome.Products.Single();
        var actual = logicOutcome.Products.Single();
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EquivalentCloudAssessmentRequestMatchesCameraAgentAdapter()
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var referencePayload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10));
        var currentPayload = CreateLinearPayload((x, y) =>
        {
            var reference = 1000 + x * 100 + y * 10;
            return (ushort)(x < 2 ? reference : reference / 2 + 100);
        });
        var currentDescriptor = CreateCloudSource(baseline, currentPayload, 41);
        var referenceDescriptor = CreateCloudSource(baseline, referencePayload, 42);
        var current = CreateProcessingArtifact(currentDescriptor, currentPayload);
        var reference = CreateProcessingArtifact(referenceDescriptor, referencePayload);
        var options = JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
            GridColumns: 2,
            GridRows: 1,
            TransmissionThresholdMillionths: 750_000,
            MinimumReferenceSignal: 1,
            MinimumSamplesPerTile: 1));
        var selector = ProcessingInputSelector.Raw("source");
        ProcessingAuxiliaryInput[] auxiliary =
        [
            new(
                "clear-reference",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.Raw("source"),
                ArtifactId: reference.ArtifactId)
        ];
        var cameraAgent = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var logicHost = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var edge = await cameraAgent.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.CloudAssessment,
            options,
            selector,
            [current, reference],
            "cloud-assessment-v1",
            AuxiliaryInputs: auxiliary,
            InputArtifactId: current.ArtifactId), CancellationToken.None).ConfigureAwait(false);
        var central = await logicHost.ExecuteAsync(
            [
                new LogicHostProcessingInput(currentDescriptor, currentPayload),
                new LogicHostProcessingInput(referenceDescriptor, referencePayload, "clear-reference")
            ],
            BuiltInProcessingRecipes.CloudAssessment,
            options,
            selector,
            "cloud-assessment-v1",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, edge.Status, edge.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, central.Status, central.ReasonCode);
        var expected = edge.Products.Single();
        var actual = central.Products.Single();
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterExecutesOverlayWithLayoutlessAssessmentInput()
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var referencePayload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10));
        var currentPayload = CreateLinearPayload((x, y) => (ushort)(800 + x * 80 + y * 8));
        var currentDescriptor = CreateCloudSource(baseline, currentPayload, 51);
        var referenceDescriptor = CreateCloudSource(baseline, referencePayload, 52);
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(environment)));
        var environmentInput = new ProcessingAuxiliaryInput(
            "environment",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(environmentPayload),
            Payload: environmentPayload);
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var assessment = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(currentDescriptor, currentPayload),
                new LogicHostProcessingInput(referenceDescriptor, referencePayload, "clear-reference")
            ],
            BuiltInProcessingRecipes.CloudAssessment,
            JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
                GridColumns: 2,
                GridRows: 1,
                MinimumReferenceSignal: 1,
                MinimumSamplesPerTile: 1)),
            ProcessingInputSelector.Raw("source"),
            "cloud-assessment-v1",
            auxiliaryInputs: [environmentInput],
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var preview = await adapter.ExecuteAsync(
            currentDescriptor,
            currentPayload,
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("source"),
            "preview").ConfigureAwait(false);
        var assessmentProduct = assessment.Products.Single();
        var previewProduct = preview.Products.Single();

        var overlay = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(
                    null,
                    previewProduct.Payload,
                    Artifact: ToArtifact(previewProduct, currentDescriptor.Timing.ExposureStartedUtc)),
                new LogicHostProcessingInput(
                    null,
                    assessmentProduct.Payload,
                    "assessment",
                    ToArtifact(assessmentProduct, currentDescriptor.Timing.ExposureStartedUtc))
            ],
            BuiltInProcessingRecipes.WeatherCloudOverlay,
            JsonSerializer.SerializeToElement(new WeatherCloudOverlayOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            "weather-cloud-overlay-v1",
            auxiliaryInputs: [environmentInput],
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, overlay.Status, overlay.ReasonCode);
        Assert.HasCount(2, overlay.Products.Single().SourceArtifactIds);
        Assert.IsNotNull(overlay.Products.Single().Layout);
    }

    private static byte[] CreateLinearPayload(Func<int, int, ushort> value)
    {
        const int width = 4;
        const int height = 2;
        var payload = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = value(x, y);
                var offset = (y * width + x) * 2;
                payload[offset] = (byte)sample;
                payload[offset + 1] = (byte)(sample >> 8);
            }
        }
        return payload;
    }

    private static ProcessingArtifact ToArtifact(ProcessingProduct product, DateTimeOffset createdUtc)
        => new(
            ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.Role,
            product.Variant,
            product.Recipe.IdentitySha256,
            product.MediaType,
            product.Layout,
            product.Payload,
            createdUtc,
            product.TotalIntegration,
            product.Compatibility,
            SourceArtifactIds: product.SourceArtifactIds);

    private static ReconstructionDescriptor CreateCloudSource(
        ReconstructionDescriptor baseline,
        byte[] payload,
        int suffix)
        => baseline with
        {
            Capture = baseline.Capture with
            {
                CaptureId = new Guid($"94000000-0000-0000-0000-{suffix:D12}"),
                CaptureSequence = suffix
            },
            Layout = baseline.Layout with
            {
                Width = 4,
                Height = 2,
                StrideBytes = 8,
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                ByteLength = payload.Length
            },
            Artifact = baseline.Artifact with
            {
                ArtifactId = new Guid($"95000000-0000-0000-0000-{suffix:D12}"),
                Role = FrameArtifactRole.Raw,
                Variant = "source",
                ChecksumSha256 = PayloadChecksum.ComputeSha256(payload),
                SourceArtifactIds = []
            }
        };

    private static ProcessingArtifact CreateProcessingArtifact(
        ReconstructionDescriptor descriptor,
        byte[] payload)
        => new(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            payload,
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Controls.EffectiveExposure,
            LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence);

    private static HVO.SkyMonitor.AgentCore.ReconstructionDescriptor CreateSource(
        HVO.SkyMonitor.AgentCore.ReconstructionDescriptor baseline,
        byte[] payload,
        int sequence)
    {
        var artifactId = new Guid($"93000000-0000-0000-0000-{sequence + 10:D12}");
        return baseline with
        {
            Capture = baseline.Capture with
            {
                CaptureId = new Guid($"93000000-0000-0000-0000-{sequence + 20:D12}"),
                CaptureSequence = sequence
            },
            Artifact = baseline.Artifact with
            {
                ArtifactId = artifactId,
                Variant = "source",
                CreatedUtc = baseline.Artifact.CreatedUtc.AddSeconds(sequence),
                ChecksumSha256 = HVO.SkyMonitor.AgentCore.PayloadChecksum.ComputeSha256(payload)
            }
        };
    }
}
