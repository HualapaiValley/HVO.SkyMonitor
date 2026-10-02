using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    public async Task NamedCalibrationReferencesBindBytesConditionsAndOrderedLineage()
    {
        var light = CalibrationSource("source", [370, 620, 370, 620], 2);
        var references = new[]
        {
            CalibrationSource(CalibrationReferenceKinds.Bias, [100, 100, 100, 100], .001),
            CalibrationSource(CalibrationReferenceKinds.Dark, [200, 200, 200, 200], 10),
            CalibrationSource(CalibrationReferenceKinds.Flat, [700, 1200, 700, 1200], 10),
            CalibrationSource(CalibrationReferenceKinds.Defect, [0, 0, 0, 0], .001)
        };
        var profile = new ReferenceCalibrationProfileV1(ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            "host-calibration", "1", "bounded host binding fixture", light.CreatedUtc.AddDays(-1), null,
            2, 2, CameraPixelFormat.Mono16, 1000, 9, 11, -11, -9,
            references.Select(a => new CalibrationReferenceDescriptorV1(a.Variant, a.ArtifactId,
                PayloadChecksum.ComputeSha256(a.Payload.Span), a.Integration, 10, -10)).ToArray());
        var identity = ReferenceCalibrationProfileJson.ComputeIdentitySha256(profile);
        var auxiliary = new ProcessingAuxiliaryInput("calibration-profile", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: ReferenceCalibrationProfileV1.CurrentSchemaVersion, IdentitySha256: identity,
            Payload: ReferenceCalibrationProfileJson.Serialize(profile));
        var inputs = new[] { new LogicHostProcessingInput(null, light.Payload, Artifact: light) }
            .Concat(references.Select(a => new LogicHostProcessingInput(null, a.Payload, $"{a.Variant}-reference", a))).ToArray();
        var recorder = new RecordingExecutor();
        var adapter = new LogicHostRecipeExecutionAdapter(recorder);
        async Task<ProcessingOutcome> Run(IReadOnlyList<LogicHostProcessingInput> bound) => await adapter.ExecuteAsync(
            bound, BuiltInProcessingRecipes.ReferenceCalibration, JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
            ProcessingInputSelector.Raw("source"), "calibrated", auxiliaryInputs: [auxiliary]);
        var produced = await Run(inputs);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, produced.Status, produced.ReasonCode);
        var product = Assert.ContainsSingle(produced.Products);
        CollectionAssert.AreEqual(new byte[] { 244, 1, 244, 1, 244, 1, 244, 1 }, product.Payload.ToArray());
        CollectionAssert.AreEqual(new[] { light.ArtifactId }.Concat(references.Select(a => a.ArtifactId)).ToArray(), product.SourceArtifactIds.ToArray());
        Assert.AreEqual(identity, product.Compatibility.Calibration);
        AssertContract(recorder.Request!, product);
        Assert.AreEqual(light.ArtifactId, recorder.Request!.InputArtifactId);
        CollectionAssert.AreEquivalent(references.Select(a => a.ArtifactId).ToArray(),
            recorder.Request.AuxiliaryInputs!.Where(a => a.Kind == ProcessingAuxiliaryInputKind.Artifact).Select(a => a.ArtifactId!.Value).ToArray());

        var missing = await Run(inputs.Where(i => i.BindingName != "dark-reference").ToArray());
        Assert.AreEqual(ProcessingReasonCodes.MissingCalibrationReference, missing.ReasonCode);
        Assert.HasCount(0, missing.Products);
        var corrupt = await Run(inputs.Select(i => i.BindingName == "bias-reference" ? i with { Payload = new byte[8] } : i).ToArray());
        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceChecksumMismatch, corrupt.ReasonCode);
        Assert.HasCount(0, corrupt.Products);
        var wrongConditions = await Run(inputs.Select(i => i.BindingName == "dark-reference"
            ? i with { Artifact = i.Artifact! with { Conditions = i.Artifact.Conditions! with { Gain = 100 } } } : i).ToArray());
        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, wrongConditions.ReasonCode);
        Assert.HasCount(0, wrongConditions.Products);
        var wrongLayout = await Run(inputs.Select(i => i.BindingName == "bias-reference"
            ? i with { Artifact = i.Artifact! with { Layout = i.Artifact.Layout! with { PixelFormat = CameraPixelFormat.BayerRggb16, CfaPattern = ColorFilterArrayPattern.Rggb } } } : i).ToArray());
        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceLayoutMismatch, wrongLayout.ReasonCode);
        Assert.HasCount(0, wrongLayout.Products);
    }

    [TestMethod]
    public async Task CentralWindowUsesBoundedNewestFramesAndRejectsMixedCompatibility()
    {
        var first = CalibrationSource("source", [10, 10, 10, 10], 1) with { Conditions = null };
        var second = first with { ArtifactId = Guid.NewGuid(), Payload = new byte[] { 20, 0, 20, 0, 20, 0, 20, 0 }, CreatedUtc = first.CreatedUtc.AddSeconds(1) };
        var third = first with { ArtifactId = Guid.NewGuid(), Payload = new byte[] { 40, 0, 40, 0, 40, 0, 40, 0 }, CreatedUtc = first.CreatedUtc.AddSeconds(2) };
        var recorder = new RecordingExecutor();
        async Task<ProcessingOutcome> Run(ProcessingArtifact[] artifacts, string options) => await new LogicHostRecipeExecutionAdapter(recorder)
            .ExecuteAsync(artifacts.Select(a => new LogicHostProcessingInput(null, a.Payload, Artifact: a)).ToArray(),
                BuiltInProcessingRecipes.RollingMean, JsonSerializer.Deserialize<JsonElement>(options), ProcessingInputSelector.Raw("source"), "mean");
        var result = await Run([first, second, third], "{\"maximumFrameCount\":2}");
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
        var product = Assert.ContainsSingle(result.Products);
        CollectionAssert.AreEqual(new byte[] { 30, 0, 30, 0, 30, 0, 30, 0 }, product.Payload.ToArray());
        CollectionAssert.AreEqual(new[] { second.ArtifactId, third.ArtifactId }, product.SourceArtifactIds.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(2), product.TotalIntegration);
        Assert.IsNull(recorder.Request!.InputArtifactId, "A window must not be reduced to one primary artifact.");
        AssertContract(recorder.Request, product);
        foreach (var option in new[] { "{\"maximumAgeMilliseconds\":500}", "{\"maximumIntegrationMilliseconds\":1500}" })
        {
            var bounded = await Run([first, second, third], option);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, bounded.Status, bounded.ReasonCode);
            CollectionAssert.AreEqual(new[] { third.ArtifactId }, bounded.Products.Single().SourceArtifactIds.ToArray());
            CollectionAssert.AreEqual(third.Payload.ToArray(), bounded.Products.Single().Payload.ToArray());
        }
        var compatibility = first.Compatibility;
        foreach (var incompatible in new[] { compatibility with { Rig = "other" }, compatibility with { Orientation = "other" },
            compatibility with { Calibration = "other" }, compatibility with { Mask = "other" }, compatibility with { Sensor = "other" },
            compatibility with { SetpointRegime = "other" }, compatibility with { ProcessingProfile = "other" } })
        {
            var rejected = await Run([first, second with { Compatibility = incompatible }], "{}");
            Assert.AreEqual(ProcessingReasonCodes.IncompatibleInput, rejected.ReasonCode);
            Assert.HasCount(0, rejected.Products);
        }
        var duplicate = await Run([first, first], "{}");
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, duplicate.ReasonCode);
        Assert.HasCount(0, duplicate.Products);
    }

    private static ProcessingArtifact CalibrationSource(string variant, ushort[] values, double seconds)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return ProcessingConformanceFixture.CreateProcessingArtifact() with
        {
            ArtifactId = Guid.NewGuid(),
            Variant = variant,
            Payload = bytes,
            Integration = TimeSpan.FromSeconds(seconds),
            Conditions = new ProcessingCaptureConditions(10, 0, -10),
            CaptureSequence = null,
            ObservationStartedUtc = null,
            ObservationEndedUtc = null
        };
    }
}
