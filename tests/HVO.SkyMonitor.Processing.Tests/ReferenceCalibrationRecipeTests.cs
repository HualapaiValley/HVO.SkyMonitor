using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Repository test naming convention.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "MSTest methods do not require context-free awaits.")]
public sealed class ReferenceCalibrationRecipeTests
{
    private const string LegacyProfileSha256 = "044F86BF838784C7646EAE1D681440BB5D73A90AB8CDF799BE6930F09DCB86E2";
    private static readonly DateTimeOffset CaptureTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");

    [TestMethod]
    public async Task LegacyProfileGoldenRoundTripsWithoutChangingCanonicalBytesOrIdentity()
    {
        var fixture = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-calibration-profile-v1.json"));
        Assert.AreEqual((byte)'\n', fixture[^1]);
        var expected = fixture[..^1];

        var profile = ReferenceCalibrationProfileJson.Parse(expected);

        Assert.IsNotNull(profile);
        CollectionAssert.AreEqual(expected, ReferenceCalibrationProfileJson.Serialize(profile));
        Assert.AreEqual(LegacyProfileSha256, Convert.ToHexString(SHA256.HashData(expected)));
        Assert.AreEqual(LegacyProfileSha256, ReferenceCalibrationProfileJson.ComputeIdentitySha256(profile));
    }

    [TestMethod]
    public async Task ExecuteAsync_CorrectsReferencesAndBindsOrderedLineageAndProfileIdentity()
    {
        var fixture = CreateFixture();

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(fixture.Request);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        var product = outcome.Products.Single();
        CollectionAssert.AreEqual(
            new[] { fixture.Light.ArtifactId, fixture.Bias.ArtifactId, fixture.Dark.ArtifactId, fixture.Flat.ArtifactId, fixture.Defect.ArtifactId },
            product.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(Bytes([500, 500, 500, 500]), product.Payload.ToArray());
        Assert.AreEqual(fixture.ProfileIdentity, product.Compatibility.Calibration);
        Assert.AreEqual(fixture.Profile.References.Single(item => item.Kind == CalibrationReferenceKinds.Defect).PayloadSha256,
            product.Compatibility.Mask);
        CollectionAssert.AreEqual(
            new[]
            {
                CalibrationMasterBuilder.NormalizationAlgorithmVersion,
                "linear16-reference-calibration-v1"
            },
            product.Algorithms.Select(static algorithm => algorithm.Version).ToArray());
        Assert.AreEqual(0, product.Layout!.BlackLevel);
        Assert.AreEqual(ushort.MaxValue, product.Layout.WhiteLevel);
        ProcessingRecipeTests.AssertProductMatchesContract(fixture.Request, product);
    }

    [TestMethod]
    public async Task ExecuteAsync_NormalizesNative12BitLightBeforeApplyingNormalizedMasters()
    {
        var fixture = CreateFixture();
        var nativePayload = Bytes([100, 120, 100, 120]);
        var nativeBefore = nativePayload.ToArray();
        var nativeLight = fixture.Light with
        {
            Layout = fixture.Light.Layout! with
            {
                SampleDepthBits = 12,
                BlackLevel = 64,
                WhiteLevel = 4095,
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample
            },
            Payload = nativePayload
        };
        var request = fixture.Request with
        {
            Inputs = fixture.Request.Inputs.Select(input => input.ArtifactId == fixture.Light.ArtifactId
                ? nativeLight
                : input).ToArray()
        };

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(16, product.Layout!.SampleDepthBits);
        Assert.AreEqual(ushort.MaxValue, product.Layout.WhiteLevel);
        Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, product.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, product.Layout.LevelCodeSpace);
        CollectionAssert.AreEqual(new ushort[] { 930, 790, 930, 790 }, Values(product.Payload.Span));
        CollectionAssert.AreEqual(nativeBefore, nativeLight.Payload.ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingCorruptStaleAndInvalidFlatReferencesFailBeforePublication()
    {
        var missing = CreateFixture(omitKind: CalibrationReferenceKinds.Dark);
        var missingOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(missing.Request);
        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, missingOutcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.MissingCalibrationReference, missingOutcome.ReasonCode);
        Assert.IsEmpty(missingOutcome.Products);

        var corrupt = CreateFixture(corruptKind: CalibrationReferenceKinds.Bias);
        var corruptOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(corrupt.Request);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, corruptOutcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceChecksumMismatch, corruptOutcome.ReasonCode);
        Assert.IsEmpty(corruptOutcome.Products);

        var stale = CreateFixture(stale: true);
        var staleOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(stale.Request);
        Assert.AreEqual(ProcessingReasonCodes.StaleCalibrationProfile, staleOutcome.ReasonCode);
        Assert.IsEmpty(staleOutcome.Products);

        var invalidFlat = CreateFixture(invalidFlat: true);
        var invalidFlatOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(invalidFlat.Request);
        Assert.AreEqual(ProcessingReasonCodes.InvalidCalibrationFlat, invalidFlatOutcome.ReasonCode);
        Assert.IsEmpty(invalidFlatOutcome.Products);

        var unrepairable = CreateFixture(unrepairableDefects: true);
        var unrepairableOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(unrepairable.Request);
        Assert.AreEqual(ProcessingReasonCodes.UnrepairableCalibrationDefect, unrepairableOutcome.ReasonCode);
        Assert.IsEmpty(unrepairableOutcome.Products);
    }

    [TestMethod]
    public async Task ExecuteAsync_AmbiguousLayoutAndConditionsMismatchesAreTerminal()
    {
        var fixture = CreateFixture();
        var executor = new ProcessingRecipeExecutor();

        var ambiguous = await executor.ExecuteAsync(fixture.Request with
        {
            Inputs = [.. fixture.Request.Inputs, fixture.Bias]
        }).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.AmbiguousCalibrationReference, ambiguous.ReasonCode);

        var mismatchedLayout = fixture.Bias with
        {
            Layout = fixture.Bias.Layout! with
            {
                PixelFormat = CameraPixelFormat.BayerRggb16,
                CfaPattern = ColorFilterArrayPattern.Rggb
            }
        };
        var layout = await executor.ExecuteAsync(fixture.Request with
        {
            Inputs = fixture.Request.Inputs.Select(input => input.ArtifactId == fixture.Bias.ArtifactId
                ? mismatchedLayout
                : input).ToArray()
        }).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceLayoutMismatch, layout.ReasonCode);

        var mismatchedConditions = fixture.Dark with
        {
            Conditions = fixture.Dark.Conditions! with { Gain = fixture.Dark.Conditions.Gain + 1 }
        };
        var conditions = await executor.ExecuteAsync(fixture.Request with
        {
            Inputs = fixture.Request.Inputs.Select(input => input.ArtifactId == fixture.Dark.ArtifactId
                ? mismatchedConditions
                : input).ToArray()
        }).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, conditions.ReasonCode);
    }

    private static Fixture CreateFixture(
        string? omitKind = null,
        string? corruptKind = null,
        bool stale = false,
        bool invalidFlat = false,
        bool unrepairableDefects = false)
    {
        var light = Artifact("source", [370, 620, 370, 620], TimeSpan.FromSeconds(2));
        var bias = Artifact(CalibrationReferenceKinds.Bias, [100, 100, 100, 100], TimeSpan.FromMilliseconds(1));
        var dark = Artifact(CalibrationReferenceKinds.Dark, [200, 200, 200, 200], TimeSpan.FromSeconds(10));
        var flat = Artifact(
            CalibrationReferenceKinds.Flat,
            invalidFlat ? [100, 100, 100, 100] : [700, 1200, 700, 1200],
            TimeSpan.FromSeconds(10));
        var defect = Artifact(
            CalibrationReferenceKinds.Defect,
            unrepairableDefects ? [1, 1, 1, 1] : [0, 0, 0, 0],
            TimeSpan.FromMilliseconds(1));
        var references = new Dictionary<string, ProcessingArtifact>(StringComparer.Ordinal)
        {
            [CalibrationReferenceKinds.Bias] = bias,
            [CalibrationReferenceKinds.Dark] = dark,
            [CalibrationReferenceKinds.Flat] = flat,
            [CalibrationReferenceKinds.Defect] = defect
        };
        var descriptors = CalibrationReferenceKinds.All.Select(kind =>
        {
            var artifact = references[kind];
            var checksum = ProcessingIdentity.ComputePayloadSha256(artifact.Payload);
            if (string.Equals(kind, corruptKind, StringComparison.Ordinal))
            {
                checksum = new string('A', 64);
            }
            return new CalibrationReferenceDescriptorV1(
                kind, artifact.ArtifactId, checksum, artifact.Integration, artifact.Conditions!.Gain, artifact.Conditions.TemperatureC);
        }).ToArray();
        var profile = new ReferenceCalibrationProfileV1(
            ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            "synthetic-profile",
            "1",
            "deterministic test references",
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            stale ? new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero) : null,
            2,
            2,
            CameraPixelFormat.Mono16,
            1000,
            9,
            11,
            -11,
            -9,
            descriptors);
        var profileBytes = ReferenceCalibrationProfileJson.Serialize(profile);
        var profileIdentity = ReferenceCalibrationProfileJson.ComputeIdentitySha256(profile);
        var inputs = new List<ProcessingArtifact> { light };
        inputs.AddRange(references.Where(pair => !string.Equals(pair.Key, omitKind, StringComparison.Ordinal)).Select(static pair => pair.Value));
        var auxiliaries = new List<ProcessingAuxiliaryInput>
        {
            new("calibration-profile", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                IdentitySha256: profileIdentity,
                Payload: profileBytes)
        };
        auxiliaries.AddRange(references
            .Where(pair => !string.Equals(pair.Key, omitKind, StringComparison.Ordinal))
            .Select(pair => new ProcessingAuxiliaryInput(
                $"{pair.Key}-reference",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.Raw(pair.Key),
                ArtifactId: pair.Value.ArtifactId)));
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ReferenceCalibration,
            JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
            ProcessingInputSelector.Raw("source"),
            inputs,
            "synthetic-corrected",
            AuxiliaryInputs: auxiliaries,
            InputArtifactId: light.ArtifactId);
        return new Fixture(request, profile, profileIdentity, light, bias, dark, flat, defect);
    }

    private static ProcessingArtifact Artifact(string variant, ushort[] values, TimeSpan exposure)
        => new(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            variant,
            new string('F', 64),
            "application/x-hvo-linear-frame",
            new FrameLayoutDescriptor(
                2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
                16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
                0, ushort.MaxValue, 8),
            Bytes(values),
            CaptureTime,
            exposure,
            Compatibility,
            ObservationStartedUtc: CaptureTime,
            ObservationEndedUtc: CaptureTime.Add(exposure),
            Conditions: new ProcessingCaptureConditions(10, 0, -10));

    private static byte[] Bytes(IEnumerable<ushort> values)
    {
        var source = values.ToArray();
        var bytes = new byte[source.Length * 2];
        for (var index = 0; index < source.Length; index++)
        {
            bytes[index * 2] = (byte)source[index];
            bytes[index * 2 + 1] = (byte)(source[index] >> 8);
        }
        return bytes;
    }

    private static ushort[] Values(ReadOnlySpan<byte> bytes)
    {
        var values = new ushort[bytes.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(bytes[index * 2] | bytes[index * 2 + 1] << 8);
        }
        return values;
    }

    private sealed record Fixture(
        ProcessingExecutionRequest Request,
        ReferenceCalibrationProfileV1 Profile,
        string ProfileIdentity,
        ProcessingArtifact Light,
        ProcessingArtifact Bias,
        ProcessingArtifact Dark,
        ProcessingArtifact Flat,
        ProcessingArtifact Defect);
}
