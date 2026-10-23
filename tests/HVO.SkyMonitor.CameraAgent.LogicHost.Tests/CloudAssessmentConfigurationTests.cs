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
    [DataRow(8, 1, ProcessingReasonCodes.InvalidLayout)]
    [DataRow(64, 65, ProcessingReasonCodes.InvalidOptions)]
    public async Task CloudAssessmentRetainsRuntimeGridResourceLimitsOnBothHosts(int columns, int rows, string reason)
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var payload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10));
        var currentDescriptor = CreateCloudSource(baseline, payload, 71);
        var referenceDescriptor = CreateCloudSource(baseline, payload, 72);
        var current = CreateProcessingArtifact(currentDescriptor, payload);
        var reference = CreateProcessingArtifact(referenceDescriptor, payload);
        var parameters = JsonSerializer.SerializeToElement(new CloudAssessmentOptions(GridColumns: columns, GridRows: rows));
        var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()).ExecuteAsync(
            new ProcessingExecutionRequest(BuiltInProcessingRecipes.CloudAssessment, parameters, ProcessingInputSelector.Raw("source"),
                [current, reference], "cloud-assessment-v1", AuxiliaryInputs:
                [new("clear-reference", ProcessingAuxiliaryInputKind.Artifact, ProcessingInputSelector.Raw("source"), ArtifactId: reference.ArtifactId)],
                InputArtifactId: current.ArtifactId), CancellationToken.None).ConfigureAwait(false);
        var central = await new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor()).ExecuteAsync(
            [new LogicHostProcessingInput(currentDescriptor, payload, BindingKind: ProcessingGraphInputBindingKind.PrimaryArtifact),
                new LogicHostProcessingInput(referenceDescriptor, payload, "clear-reference")],
            BuiltInProcessingRecipes.CloudAssessment, parameters, ProcessingInputSelector.Raw("source"), "cloud-assessment-v1",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, edge.Status);
        Assert.AreEqual(edge.Status, central.Status);
        Assert.AreEqual(reason, edge.ReasonCode);
        Assert.AreEqual(reason, central.ReasonCode);
        Assert.IsEmpty(edge.Products);
        Assert.IsEmpty(central.Products);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("compatible")]
    [DataRow("absent")]
    [DataRow("incompatible")]
    public async Task CloudAssessmentConfigurationReconstructsRetainedOutputsOnBothHosts(string referenceState)
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var referencePayload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10), 64, 64);
        var currentPayload = CreateLinearPayload((x, y) => x % 32 == 0
            ? ushort.MaxValue : (ushort)((1000 + x * 100 + y * 10) * (x < 32 ? 0.9 : 0.5)), 64, 64);
        var currentDescriptor = CreateCloudSource(baseline, currentPayload, 61, 64, 64);
        var referenceDescriptor = CreateCloudSource(baseline, referencePayload, 62, 64, 64);
        if (referenceState == "incompatible")
            referenceDescriptor = referenceDescriptor with { Layout = referenceDescriptor.Layout with { BlackLevel = 1 } };
        var current = CreateProcessingArtifact(currentDescriptor, currentPayload);
        var reference = CreateProcessingArtifact(referenceDescriptor, referencePayload);
        var rawHash = PayloadChecksum.ComputeSha256(currentPayload);
        var referenceHash = PayloadChecksum.ComputeSha256(referencePayload);
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("CloudAssessment", "comparative-tiled-transmission-v1").Descriptor!;
        Assert.AreSame(CloudAssessmentOptionDescriptors.Descriptor, descriptor);
        var source = JsonSerializer.SerializeToElement(new
        {
            gridColumns = 2, gridRows = 1, clearReferenceManifestPath = "retained/clear.manifest.json", future = new { keep = 17 }
        });
        var retainedJson = source.GetRawText();
        var environment = new CloudAssessmentEnvironmentV1(CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night, EnvironmentalObservationMatchStatus.Missing, null, null, false);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(environment)));
        var environmentInput = new ProcessingAuxiliaryInput("environment", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(environmentPayload), Payload: environmentPayload);
        async Task<(ProcessingProduct Product, CloudAssessmentV1 Assessment)> Execute(JsonElement options)
        {
            var values = ProcessingNodeOptionsCodec.Decode(descriptor, options);
            Assert.IsTrue(values.IsValid);
            var parameters = JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
                (int)values.Get("gridColumns").Value.AsWholeNumber(),
                (int)values.Get("gridRows").Value.AsWholeNumber(),
                (int)values.Get("transmissionThresholdMillionths").Value.AsWholeNumber(),
                (ushort)values.Get("minimumReferenceSignal").Value.AsWholeNumber(),
                (int)values.Get("minimumSamplesPerTile").Value.AsWholeNumber(),
                (int)values.Get("maximumSaturatedFractionMillionths").Value.AsWholeNumber(),
                values.Get("includeMask").Value.AsToggle()));
            var hasReference = referenceState != "absent";
            var variant = values.Get("outputVariant").Value.AsText();
            var auxiliary = new List<ProcessingAuxiliaryInput> { environmentInput };
            if (hasReference)
                auxiliary.Add(new("clear-reference", ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.Raw("source"), ArtifactId: reference.ArtifactId));
            var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()).ExecuteAsync(
                new ProcessingExecutionRequest(BuiltInProcessingRecipes.CloudAssessment, parameters, ProcessingInputSelector.Raw("source"),
                    hasReference ? [current, reference] : [current], variant, AuxiliaryInputs: auxiliary, InputArtifactId: current.ArtifactId),
                CancellationToken.None).ConfigureAwait(false);
            var central = await new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor()).ExecuteAsync(
                hasReference ? [new LogicHostProcessingInput(currentDescriptor, currentPayload, BindingKind: ProcessingGraphInputBindingKind.PrimaryArtifact),
                    new LogicHostProcessingInput(referenceDescriptor, referencePayload, "clear-reference")]
                    : [new LogicHostProcessingInput(currentDescriptor, currentPayload, BindingKind: ProcessingGraphInputBindingKind.PrimaryArtifact)],
                BuiltInProcessingRecipes.CloudAssessment, parameters, ProcessingInputSelector.Raw("source"), variant,
                auxiliaryInputs: [environmentInput], cancellationToken: CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, edge.Status, edge.ReasonCode);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, central.Status, central.ReasonCode);
            var expected = edge.Products.Single();
            var actual = central.Products.Single();
            CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
            Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
            Assert.AreEqual(PayloadChecksum.ComputeSha256(actual.Payload.Span), actual.ChecksumSha256);
            Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
            Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
            CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
            CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
            var parsed = CloudAssessmentJson.Parse(actual.Payload);
            Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
            Assert.AreEqual(parsed.Assessment!.AssessmentIdentitySha256, actual.ContentIdentitySha256);
            Assert.AreEqual(EnvironmentalObservationMatchStatus.Missing, parsed.Assessment.Environment.PrecipitationStatus);
            Assert.HasCount(hasReference ? 2 : 1, actual.SourceArtifactIds);
            return (actual, parsed.Assessment);
        }
        var original = await Execute(source).ConfigureAwait(false);
        async Task<CloudAssessmentV1> Edit(string key, ProcessingOptionValue value)
        {
            var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue> { [key] = value });
            Assert.IsTrue(edited.IsValid);
            var result = await Execute(edited.Options!.Value).ConfigureAwait(false);
            Assert.AreNotEqual(original.Product.Recipe.IdentitySha256, result.Product.Recipe.IdentitySha256, key);
            Assert.AreNotEqual(original.Product.ChecksumSha256, result.Product.ChecksumSha256, key);
            return result.Assessment;
        }
        var columns = await Edit("gridColumns", ProcessingOptionValue.FromWholeNumber(4)).ConfigureAwait(false);
        var rows = await Edit("gridRows", ProcessingOptionValue.FromWholeNumber(2)).ConfigureAwait(false);
        var threshold = await Edit("transmissionThresholdMillionths", ProcessingOptionValue.FromWholeNumber(950_000)).ConfigureAwait(false);
        var signal = await Edit("minimumReferenceSignal", ProcessingOptionValue.FromWholeNumber(65_535)).ConfigureAwait(false);
        var samples = await Edit("minimumSamplesPerTile", ProcessingOptionValue.FromWholeNumber(int.MaxValue)).ConfigureAwait(false);
        var saturation = await Edit("maximumSaturatedFractionMillionths", ProcessingOptionValue.FromWholeNumber(0)).ConfigureAwait(false);
        var mask = await Edit("includeMask", ProcessingOptionValue.FromToggle(false)).ConfigureAwait(false);
        Assert.AreEqual(4, columns.Grid.Columns);
        Assert.AreEqual(2, rows.Grid.Rows);
        Assert.IsNull(mask.Mask);
        var route = await Execute(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["outputVariant"] = ProcessingOptionValue.FromText("cloud-custom")
        }).Options!.Value).ConfigureAwait(false);
        Assert.AreEqual("cloud-custom", route.Product.Variant);
        Assert.AreNotEqual(original.Product.OutputIdentitySha256, route.Product.OutputIdentitySha256);
        Assert.AreEqual(original.Product.ChecksumSha256, route.Product.ChecksumSha256, "Routing does not change assessment evidence.");
        var bounded = await Execute(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["gridColumns"] = ProcessingOptionValue.FromWholeNumber(64), ["gridRows"] = ProcessingOptionValue.FromWholeNumber(64)
        }).Options!.Value).ConfigureAwait(false);
        Assert.HasCount(4096, bounded.Assessment.Regions);
        Assert.IsTrue(bounded.Product.Payload.Length <= 1024 * 1024);
        if (referenceState == "compatible")
        {
            Assert.AreEqual(CloudAssessmentStatus.Quantified, original.Assessment.Status);
            Assert.AreEqual(500_000, original.Assessment.CoverageMillionths);
            Assert.AreEqual(1_000_000, threshold.CoverageMillionths);
            Assert.IsNotNull(original.Assessment.Mask);
            Assert.AreEqual(0b0000_0010, original.Assessment.Mask.Bits.Span[0]);
            Assert.IsNull(signal.CoverageMillionths);
            Assert.IsNull(samples.CoverageMillionths);
            Assert.IsNull(bounded.Assessment.CoverageMillionths, "One-sample tiles cannot estimate regression transmission.");
            Assert.AreEqual(CloudAssessmentStatus.Contaminated, saturation.Status);
            Assert.Contains(CloudAssessmentReasonCodes.SaturationContamination, saturation.ReasonCodes);
            Assert.IsNull(saturation.CoverageMillionths);
        }
        else
        {
            foreach (var assessment in new[] { original.Assessment, columns, rows, threshold, signal, samples, saturation, mask })
            {
                Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, assessment.Status);
                Assert.IsNull(assessment.CoverageMillionths);
                Assert.IsNull(assessment.Mask);
                Assert.Contains(referenceState == "absent" ? CloudAssessmentReasonCodes.MissingClearReference
                    : CloudAssessmentReasonCodes.IncompatibleClearReference, assessment.ReasonCodes);
            }
        }
        var restored = await Execute(source).ConfigureAwait(false);
        CollectionAssert.AreEqual(original.Product.Payload.ToArray(), restored.Product.Payload.ToArray());
        Assert.AreEqual(original.Product.OutputIdentitySha256, restored.Product.OutputIdentitySha256);
        Assert.AreEqual(retainedJson, source.GetRawText());
        Assert.AreEqual(rawHash, PayloadChecksum.ComputeSha256(currentPayload));
        Assert.AreEqual(referenceHash, PayloadChecksum.ComputeSha256(referencePayload));
        Console.WriteLine($"Reference={referenceState}; original/restore={original.Product.ChecksumSha256}; threshold={threshold.AssessmentIdentitySha256}; raw={rawHash}; retained reference={referenceHash}");
    }
}
