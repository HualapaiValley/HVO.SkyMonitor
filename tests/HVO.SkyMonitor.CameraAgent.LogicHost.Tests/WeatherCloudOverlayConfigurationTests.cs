using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class LogicHostProcessingConformanceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WeatherOverlayConfigurationReconstructsIdenticalProductsOnBothHosts(bool hasReference)
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var referencePayload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10), 64, 32);
        var currentPayload = CreateLinearPayload((x, y) => (ushort)((1000 + x * 100 + y * 10) * (x < 32 ? 1.0 : 0.5)), 64, 32);
        var currentDescriptor = CreateCloudSource(baseline, currentPayload, 51, 64, 32);
        var referenceDescriptor = CreateCloudSource(baseline, referencePayload, 52, 64, 32);
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
            hasReference
                ? [new LogicHostProcessingInput(currentDescriptor, currentPayload),
                    new LogicHostProcessingInput(referenceDescriptor, referencePayload, "clear-reference")]
                : [new LogicHostProcessingInput(currentDescriptor, currentPayload)],
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

        var retainedAssessment = assessmentProduct.Payload.ToArray();
        var retainedPreview = previewProduct.Payload.ToArray();
        var rawIdentity = PayloadChecksum.ComputeSha256(currentPayload);
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("WeatherCloudOverlay", "weather-cloud-overlay-v1").Descriptor!;
        Assert.AreSame(WeatherCloudOverlayOptionDescriptors.Descriptor, descriptor);
        var source = JsonSerializer.SerializeToElement(new { future = new { keep = 17 } });
        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["lineThickness"] = ProcessingOptionValue.FromWholeNumber(4),
            ["drawLabels"] = ProcessingOptionValue.FromToggle(false),
            ["maximumLabelCharacters"] = ProcessingOptionValue.FromWholeNumber(12),
            ["outputVariant"] = ProcessingOptionValue.FromText("weather-custom")
        }).Options!.Value;
        var shortLabels = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["maximumLabelCharacters"] = ProcessingOptionValue.FromWholeNumber(4)
        }).Options!.Value;
        var noLabels = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["drawLabels"] = ProcessingOptionValue.FromToggle(false)
        }).Options!.Value;
        var zeroLabels = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["maximumLabelCharacters"] = ProcessingOptionValue.FromWholeNumber(0)
        }).Options!.Value;
        async Task<ProcessingProduct> Execute(JsonElement options)
        {
            var values = ProcessingNodeOptionsCodec.Decode(descriptor, options);
            Assert.IsTrue(values.IsValid);
            var parameters = JsonSerializer.SerializeToElement(new WeatherCloudOverlayOptions(
                (int)values.Get("lineThickness").Value.AsWholeNumber(),
                values.Get("drawLabels").Value.AsToggle(),
                (int)values.Get("maximumLabelCharacters").Value.AsWholeNumber()));
            var previewArtifact = ToArtifact(previewProduct, currentDescriptor.Timing.ExposureStartedUtc);
            var assessmentArtifact = ToArtifact(assessmentProduct, currentDescriptor.Timing.ExposureStartedUtc);
            var selector = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, previewProduct.Variant, previewProduct.Recipe.IdentitySha256);
            var variant = values.Get("outputVariant").Value.AsText();
            var central = await adapter.ExecuteAsync(
                [new LogicHostProcessingInput(null, previewProduct.Payload, Artifact: previewArtifact),
                    new LogicHostProcessingInput(null, assessmentProduct.Payload, "assessment", assessmentArtifact)],
                BuiltInProcessingRecipes.WeatherCloudOverlay, parameters, selector, variant,
                auxiliaryInputs: [environmentInput], cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()).ExecuteAsync(
                new ProcessingExecutionRequest(BuiltInProcessingRecipes.WeatherCloudOverlay, parameters, selector,
                    [previewArtifact, assessmentArtifact], variant,
                    AuxiliaryInputs: [new("assessment", ProcessingAuxiliaryInputKind.Artifact,
                        ProcessingInputSelector.RecipeResult(FrameArtifactRole.Metadata, assessmentArtifact.Variant, assessmentArtifact.RecipeIdentitySha256),
                        ArtifactId: assessmentArtifact.ArtifactId), environmentInput], InputArtifactId: previewArtifact.ArtifactId),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, central.Status, central.ReasonCode);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, edge.Status, edge.ReasonCode);
            var actual = central.Products.Single();
            var expected = edge.Products.Single();
            CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
            Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
            Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
            CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
            CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
            return actual;
        }
        var original = await Execute(source).ConfigureAwait(false);
        var changed = await Execute(edited).ConfigureAwait(false);
        var shortened = await Execute(shortLabels).ConfigureAwait(false);
        var hidden = await Execute(noLabels).ConfigureAwait(false);
        var zero = await Execute(zeroLabels).ConfigureAwait(false);
        var restored = await Execute(source).ConfigureAwait(false);
        Assert.AreNotEqual(original.ChecksumSha256, changed.ChecksumSha256);
        Assert.AreNotEqual(original.ChecksumSha256, shortened.ChecksumSha256);
        Assert.AreNotEqual(original.ChecksumSha256, hidden.ChecksumSha256);
        Assert.AreEqual(hidden.ChecksumSha256, zero.ChecksumSha256);
        if (hasReference)
            Assert.AreNotEqual(hidden.ChecksumSha256, changed.ChecksumSha256, "Only boundary thickness differs when labels are hidden.");
        Assert.AreNotEqual(original.OutputIdentitySha256, changed.OutputIdentitySha256);
        Assert.AreEqual(original.OutputIdentitySha256, restored.OutputIdentitySha256);
        CollectionAssert.AreEqual(original.Payload.ToArray(), restored.Payload.ToArray());
        CollectionAssert.AreEqual(retainedAssessment, assessmentProduct.Payload.ToArray());
        CollectionAssert.AreEqual(retainedPreview, previewProduct.Payload.ToArray());
        Assert.AreEqual(rawIdentity, PayloadChecksum.ComputeSha256(currentPayload));
        var parsed = CloudAssessmentJson.Parse(retainedAssessment).Assessment!;
        Assert.AreEqual(EnvironmentalObservationMatchStatus.Missing, parsed.Environment.PrecipitationStatus);
        if (!hasReference)
        {
            Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, parsed.Status);
            Assert.IsNull(parsed.CoverageMillionths);
        }
        Console.WriteLine($"Reference={hasReference}; original/restore={original.ChecksumSha256}; edited={changed.ChecksumSha256}; short labels={shortened.ChecksumSha256}; raw={rawIdentity}");
    }
}
