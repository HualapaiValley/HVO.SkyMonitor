using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Unit")]
public sealed class ReconstructableCaptureContractTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, 3, 2, 8,
        "925DB25F0A90DB14EDD8C8A47ECFF5C1B226A5E25C5DDACB6A21F469F9C1BD64",
        "EA0E62A3870BDAD3EF760AAAD03B542246ED7DB286AA7C83665B7BEF6655499D")]
    [DataRow(CameraPixelFormat.Rgb24, 2, 2, 8,
        "4E3DA2AE0C246503682E10C16BD2AEDEC6DCB37EC2045F347A4B92798F3E1724",
        "7E6A77AF689D63BEBB1BF93844A1032383EAEC3476831F75F858BAF553F7F3A8")]
    [DataRow(CameraPixelFormat.BayerRggb16, 3, 2, 8,
        "A3745287E291AFE11547F7D6E13350A6F4BAA1ED047C513D675AAF88D35EC8DF",
        "3A2157CB09033AD66E5460C4977E666762966CEAA225A8A60A8D52C050D06671")]
    [DataRow(CameraPixelFormat.Mono8, 3, 2, 4,
        "CF487B545104026DCCC2F9844FB5F0A58BDC9673D8192E14F7B8F15FFB1E624B",
        "F94FB322447BA346B3505D989DCBC1E055CA0B38EA3C4E8EED9EB9DB03269B06")]
    public void ManifestV2_RoundTripsAndReconstructsOriginalBytes(
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        string expectedDescriptorSha256,
        string expectedManifestSha256)
    {
        var payload = Enumerable.Range(0, stride * height).Select(static value => (byte)value).ToArray();
        var manifest = CreateManifest(format, width, height, stride, payload);

        var firstJson = CaptureContractJson.Serialize(manifest);
        var parse = CaptureContractJson.ParseManifest(firstJson);
        var parsed = parse.Document?.Manifest;
        var secondJson = CaptureContractJson.Serialize(parsed!);
        var result = FrameReconstructor.TryReconstruct(parsed!.Descriptor, payload, out var frame);

        Assert.IsTrue(parse.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.Complete, parse.Document!.Completeness);
        CollectionAssert.AreEqual(firstJson, secondJson);
        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(frame);
        Assert.AreEqual(format, frame.PixelFormat);
        Assert.AreEqual(stride, frame.StrideBytes);
        Assert.AreEqual(expectedDescriptorSha256, manifest.IdempotencyKey);
        Assert.AreEqual(expectedManifestSha256, CaptureContractJson.ComputeManifestSha256(manifest));
        CollectionAssert.AreEqual(payload, frame.PixelData.ToArray());

        payload[0] = 255;
        Assert.AreEqual(255, frame.PixelData.Span[0], "Reconstruction must retain the caller-owned payload without copying it.");
    }

    [TestMethod]
    public void ManifestV2_PreservesVariantOrderedLineageAndCaptureProfiles()
    {
        var payload = new byte[8];
        var firstSource = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var secondSource = Guid.Parse("00000000-0000-0000-0000-000000000012");
        var raw = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var manifest = raw with
        {
            Descriptor = raw.Descriptor with
            {
                Artifact = raw.Descriptor.Artifact with
                {
                    Role = FrameArtifactRole.Combined,
                    Variant = "rolling-5",
                    SourceArtifactIds = [firstSource, secondSource]
                }
            }
        };

        var parse = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(manifest));
        var descriptor = parse.Document!.Manifest!.Descriptor;

        Assert.IsTrue(parse.IsValid);
        Assert.AreEqual("rolling-5", descriptor.Artifact.Variant);
        CollectionAssert.AreEqual(new[] { firstSource, secondSource }, descriptor.Artifact.SourceArtifactIds.ToArray());
        Assert.AreEqual("rig-a", descriptor.Profiles.Rig.Name);
        Assert.AreEqual(42L, descriptor.Capture.CaptureSequence);
    }

    [TestMethod]
    public void ParseManifest_WithV1_ReturnsLegacyIncompleteWithoutDescriptor()
    {
        var legacy = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            "agent-a",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Raw,
            "application/octet-stream",
            4,
            new string('A', 64),
            DateTimeOffset.UnixEpoch,
            "raw-v1",
            "frames/raw.bin");
        var json = JsonSerializer.SerializeToUtf8Bytes(legacy, WebJsonOptions);

        var result = CaptureContractJson.ParseManifest(json);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.LegacyIncomplete, result.Document!.Completeness);
        Assert.IsNotNull(result.Document.LegacyManifest);
        Assert.IsNull(result.Document.Manifest);
    }

    [TestMethod]
    public void ParseManifest_WithUnknownOrMalformedVersion_ReturnsStableReason()
    {
        var unknown = CaptureContractJson.ParseManifest("{\"schemaVersion\":\"v3\"}"u8.ToArray());
        var missing = CaptureContractJson.ParseManifest("{}"u8.ToArray());
        var malformed = CaptureContractJson.ParseManifest("{"u8.ToArray());

        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedSchema, unknown.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedSchema, missing.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, malformed.Validation.ReasonCode);
    }

    [TestMethod]
    public void ParseManifest_WithInvalidV1OrV2_ReturnsStableReason()
    {
        var invalidLegacy = CaptureContractJson.ParseManifest(
            "{\"schemaVersion\":\"v1\",\"agentId\":\"\"}"u8.ToArray());
        var payload = new byte[8];
        var current = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var invalidCurrent = current with
        {
            Descriptor = current.Descriptor with
            {
                Capture = current.Descriptor.Capture with
                {
                    CaptureId = Guid.Empty
                }
            }
        };

        var currentResult = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(invalidCurrent));

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidLegacy.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, currentResult.Validation.ReasonCode);
    }

    [TestMethod]
    public void ParseManifest_WithMissingNullOrNumericRequiredFields_ReturnsStableReason()
    {
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var missing = CaptureContractJson.ParseManifest(
            "{\"schemaVersion\":\"v2\",\"relativeArtifactPath\":\"frames/raw.bin\"}"u8.ToArray());

        var nullNode = JsonNode.Parse(CaptureContractJson.Serialize(manifest))!.AsObject();
        nullNode["descriptor"]!.AsObject()["timing"] = null;
        var nestedNull = CaptureContractJson.ParseManifest(Encoding.UTF8.GetBytes(nullNode.ToJsonString()));

        var numericNode = JsonNode.Parse(CaptureContractJson.Serialize(manifest))!.AsObject();
        numericNode["descriptor"]!["layout"]!.AsObject()["pixelFormat"] = 1;
        var numericEnum = CaptureContractJson.ParseManifest(Encoding.UTF8.GetBytes(numericNode.ToJsonString()));

        Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, missing.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder, nestedNull.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, numericEnum.Validation.ReasonCode);
    }

    [TestMethod]
    public void RecipeOptionsHash_IsCanonicalAcrossPropertyOrder()
    {
        using var first = JsonDocument.Parse("{\"z\":1,\"name\":\"fixture\",\"nested\":{\"b\":true,\"a\":null},\"array\":[2,1]}");
        using var second = JsonDocument.Parse("{\"array\":[2,1],\"nested\":{\"a\":null,\"b\":true},\"name\":\"fixture\",\"z\":1}");

        var firstHash = CaptureContractJson.ComputeCanonicalJsonSha256(first.RootElement);
        var secondHash = CaptureContractJson.ComputeCanonicalJsonSha256(second.RootElement);

        Assert.AreEqual(firstHash, secondHash);
        Assert.AreEqual(
            "{\"array\":[2,1],\"name\":\"fixture\",\"nested\":{\"a\":null,\"b\":true},\"z\":1}",
            CaptureContractJson.Canonicalize(first.RootElement).GetRawText());
    }

    [TestMethod]
    public void ManifestIdempotency_IgnoresStoragePathButChangesWithVariant()
    {
        var payload = new byte[8];
        var first = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var moved = first with { RelativeArtifactPath = "archive/raw.bin" };
        var variant = first with
        {
            Descriptor = first.Descriptor with
            {
                Artifact = first.Descriptor.Artifact with { Variant = "alternate" }
            }
        };

        Assert.AreEqual(first.IdempotencyKey, moved.IdempotencyKey);
        Assert.AreNotEqual(first.IdempotencyKey, variant.IdempotencyKey);
        Assert.AreNotEqual(first.IdempotencyKey, CaptureContractJson.ComputeManifestSha256(moved));
    }

    [TestMethod]
    public void ManifestIdempotency_NormalizesChecksumCasing()
    {
        var upper = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var profiles = upper.Descriptor.Profiles;
        var lower = upper with
        {
            Descriptor = upper.Descriptor with
            {
                Profiles = profiles with
                {
                    Rig = profiles.Rig with { Sha256 = new string('a', 64) },
                    Calibration = profiles.Calibration with { Sha256 = new string('a', 64) },
                    Mask = profiles.Mask with { Sha256 = new string('a', 64) },
                    Sensor = profiles.Sensor with { Sha256 = new string('a', 64) },
                    Processing = profiles.Processing with { Sha256 = new string('a', 64) }
                },
                Artifact = upper.Descriptor.Artifact with
                {
                    ChecksumSha256 = "af5570f5a1810b7af78caf4bc70a660f0df51e42baf91d4de5b2328de0e83dfc",
                    Recipe = upper.Descriptor.Artifact.Recipe with
                    {
                        OptionsSha256 = "18de2b6210171497ea31bddec3fe87364962d62ade091704355dfee11873b768"
                    }
                }
            }
        };

        Assert.AreEqual(upper.IdempotencyKey, lower.IdempotencyKey);
        CollectionAssert.AreEqual(CaptureContractJson.Serialize(upper), CaptureContractJson.Serialize(lower));
    }

    [TestMethod]
    public async Task PayloadChecksum_StreamAndSpanProduceSameValue()
    {
        var payload = Enumerable.Range(0, 8193).Select(static value => (byte)value).ToArray();
        using var stream = new MemoryStream(payload, writable: false);

        var streamHash = await PayloadChecksum.ComputeSha256Async(stream).ConfigureAwait(false);

        Assert.AreEqual(PayloadChecksum.ComputeSha256(payload), streamHash);
    }

    [TestMethod]
    public void Reconstruct_WithLengthOrChecksumMismatch_ReturnsStableReason()
    {
        var payload = new byte[8];
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;

        var length = FrameReconstructor.TryReconstruct(descriptor, payload.AsMemory(0, 7), out _);
        payload[0] = 1;
        var checksum = FrameReconstructor.TryReconstruct(descriptor, payload, out _);

        Assert.AreEqual(CaptureContractReasonCodes.PayloadLengthMismatch, length.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.PayloadChecksumMismatch, checksum.ReasonCode);
    }

    [TestMethod]
    public void Validate_WithInvalidIdentityTimingProfileRecipeLayoutAndLineage_ReturnsStableReasons()
    {
        var payload = new byte[8];
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;
        var invalidIdentity = descriptor with { Capture = descriptor.Capture with { CaptureId = Guid.Empty } };
        var invalidSequence = descriptor with { Capture = descriptor.Capture with { CaptureSequence = 0 } };
        var invalidTiming = descriptor with
        {
            Timing = descriptor.Timing with { ReadoutCompletedUtc = descriptor.Timing.ExposureStartedUtc.AddSeconds(-1) }
        };
        var invalidProfile = descriptor with
        {
            Profiles = descriptor.Profiles with { Mask = descriptor.Profiles.Mask with { Sha256 = "bad" } }
        };
        var invalidRecipe = descriptor with
        {
            Artifact = descriptor.Artifact with
            {
                Recipe = descriptor.Artifact.Recipe with { OptionsSha256 = new string('0', 64) }
            }
        };
        var invalidLayout = descriptor with { Layout = descriptor.Layout with { StrideBytes = 3 } };
        var invalidLineage = descriptor with
        {
            Artifact = descriptor.Artifact with { SourceArtifactIds = [Guid.Empty] }
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidIdentity.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidCaptureSequence, invalidSequence.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder, invalidTiming.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidProfile, invalidProfile.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.RecipeHashMismatch, invalidRecipe.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidStride, invalidLayout.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage, invalidLineage.Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithFormatSpecificLayoutErrors_ReturnsStableReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.BayerRggb16, 2, 2, 4, new byte[8]).Descriptor;

        Assert.AreEqual(CaptureContractReasonCodes.InvalidSampleDepth,
            (descriptor with { Layout = descriptor.Layout with { SampleDepthBits = 12 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidByteOrder,
            (descriptor with { Layout = descriptor.Layout with { ByteOrder = FrameByteOrder.BigEndian } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidPacking,
            (descriptor with { Layout = descriptor.Layout with { Packing = FrameSamplePacking.Packed } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidCfa,
            (descriptor with { Layout = descriptor.Layout with { CfaPattern = ColorFilterArrayPattern.None } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLevels,
            (descriptor with { Layout = descriptor.Layout with { BlackLevel = 10, WhiteLevel = 5 } }).Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithInvalidControlsArtifactRecipeChecksumAndSchema_ReturnsStableReasons()
    {
        var payload = new byte[8];
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var descriptor = manifest.Descriptor;

        Assert.AreEqual(CaptureContractReasonCodes.InvalidControls,
            (descriptor with { Controls = descriptor.Controls with { EffectiveGain = double.NaN } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidDimensions,
            (descriptor with { Layout = descriptor.Layout with { Width = 0 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedFormat,
            (descriptor with { Layout = descriptor.Layout with { PixelFormat = (CameraPixelFormat)999 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidArtifactRole,
            (descriptor with { Artifact = descriptor.Artifact with { Role = (FrameArtifactRole)999 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidArtifactVariant,
            (descriptor with { Artifact = descriptor.Artifact with { Variant = " " } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidRecipe,
            (descriptor with { Artifact = descriptor.Artifact with { Recipe = descriptor.Artifact.Recipe with { Name = "" } } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidChecksum,
            (descriptor with { Artifact = descriptor.Artifact with { ChecksumSha256 = "bad" } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedSchema,
            (manifest with { SchemaVersion = "v3" }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidPath,
            (manifest with { RelativeArtifactPath = "../raw.bin" }).Validate().ReasonCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/raw.bin")]
    [DataRow("\\raw.bin")]
    [DataRow("C:\\raw.bin")]
    [DataRow("frames/./raw.bin")]
    [DataRow("frames/\0/raw.bin")]
    public void Validate_WithUnsafeManifestPaths_ReturnsStableReason(string path)
    {
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]) with
        {
            RelativeArtifactPath = path
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidPath, manifest.Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithArtifactIdentityTimingLineageAndMediaErrors_ReturnsStableReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var source = Guid.Parse("30000000-0000-0000-0000-000000000001");

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity,
            (descriptor with { Artifact = null! }).Validate().ReasonCode);
        var invalidSource = (descriptor with
        {
            Artifact = descriptor.Artifact with { SourceId = "" }
        }).Validate();
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidSource.ReasonCode);
        Assert.AreEqual("descriptor.artifact.sourceId", invalidSource.FieldPath);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder,
            (descriptor with { Artifact = descriptor.Artifact with { CreatedUtc = descriptor.Artifact.CreatedUtc.ToOffset(TimeSpan.FromHours(1)) } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { SourceArtifactIds = [source, source] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { Role = FrameArtifactRole.Preview, SourceArtifactIds = [] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidArtifactRole,
            (descriptor with { Artifact = descriptor.Artifact with { MediaType = "" } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity,
            (descriptor with { Capture = descriptor.Capture with { CaptureId = descriptor.Artifact.ArtifactId } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { SourceArtifactIds = [descriptor.Artifact.ArtifactId] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { SourceArtifactIds = [descriptor.Capture.CaptureId] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLevels,
            (descriptor with { Layout = descriptor.Layout with { WhiteLevel = double.NaN } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLevels,
            (descriptor with { Layout = descriptor.Layout with { WhiteLevel = 65536 } }).Validate().ReasonCode);
    }

    [TestMethod]
    public void Reconstruct_WithoutChecksumVerification_WrapsPayload()
    {
        var payload = new byte[8];
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;
        payload[0] = 42;

        var result = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame, verifyChecksum: false);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(42, frame!.PixelData.Span[0]);
    }

    [TestMethod]
    public void Reconstruct_WithNullOrInvalidDescriptor_ReturnsStableReason()
    {
        var nullResult = FrameReconstructor.TryReconstruct(null!, ReadOnlyMemory<byte>.Empty, out var nullFrame);
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor with
        {
            Capture = new CaptureIdentityDescriptor("", "rig", 1, Guid.NewGuid())
        };
        var invalidResult = FrameReconstructor.TryReconstruct(descriptor, new byte[8], out var invalidFrame);

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, nullResult.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidResult.ReasonCode);
        Assert.IsNull(nullFrame);
        Assert.IsNull(invalidFrame);
    }

    internal static ArtifactManifestV2 CreateManifest(
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        byte[] payload)
    {
        using var options = JsonDocument.Parse("{\"stretch\":{\"white\":65535,\"black\":0},\"enabled\":true}");
        var hash = new string('A', 64);
        var sampleDepth = format is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 ? 8 : 16;
        var requestedStart = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(
                "agent-a", "rig-a", 42,
                Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new CaptureTimingDescriptor(
                requestedStart,
                requestedStart.AddSeconds(1),
                requestedStart.AddSeconds(2),
                requestedStart.AddSeconds(3),
                requestedStart.AddSeconds(4)),
            new CaptureControlDescriptor(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 100, 99.5,
                10, 10, -10, -9.5),
            new CaptureProfileSet(
                new("rig-a", "1.2.3", hash),
                new("dark-library", "2.0.0", hash),
                new("sensor-mask", "1.0.0", hash),
                new("asi-sensor", "3.0.0", hash),
                new("capture-profile", "4.0.0", hash)),
            new FrameLayoutDescriptor(
                width,
                height,
                stride,
                format,
                sampleDepth == 16 ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable,
                sampleDepth,
                sampleDepth,
                FrameSamplePacking.ByteAligned,
                format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                0,
                sampleDepth == 16 ? 65535 : 255,
                payload.LongLength),
            new ArtifactDescriptor(
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                FrameArtifactRole.Raw,
                "VirtualSky",
                "native",
                requestedStart.AddSeconds(4),
                [],
                RecipeIdentityDescriptor.Create("capture-raw", "1.0.0", "build-1", options.RootElement),
                "application/octet-stream",
                PayloadChecksum.ComputeSha256(payload)));
        return new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            descriptor,
            "frames/raw.bin");
    }
}
