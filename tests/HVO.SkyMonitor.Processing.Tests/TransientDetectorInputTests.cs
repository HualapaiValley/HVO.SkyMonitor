using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientDetectorInputTests
{
    [TestMethod]
    public void Mono16VerifiesSourceAndBorrowsExactMemory()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var original = value.Artifact.Payload.ToArray();

        var result = TransientDetectorInputFactory.Create(value.Artifact, value.Source, value.Levels);

        Assert.IsTrue(result.Validation.IsValid, result.Validation.ReasonCode);
        Assert.IsNotNull(result.Input);
        Assert.AreEqual(TransientDetectorInputOwnership.Borrowed, result.Input.Ownership);
        Assert.AreEqual(value.Artifact.Payload.Length, result.BytesScanned);
        Assert.AreEqual(1, result.BytesCopied);
        Assert.IsTrue(MemoryMarshal.TryGetArray(result.Input.Pixels, out var inputSegment));
        Assert.IsTrue(MemoryMarshal.TryGetArray(value.Artifact.Payload, out var sourceSegment));
        Assert.AreSame(sourceSegment.Array, inputSegment.Array);
        CollectionAssert.AreEqual(original, value.Artifact.Payload.ToArray());
        Assert.IsFalse(Linear16MaskOperations.IsExcluded(result.Input.SaturationMask, 0, 0));
        Assert.IsTrue(TransientContractJson.Validate(result.Input.Descriptor).IsValid);
    }

    [TestMethod]
    public void Rggb16UsesOneBoundedCellAverageCopyWithoutMutatingSource()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.BayerRggb16);
        var original = value.Artifact.Payload.ToArray();
        var sourceChecksum = Sha256(value.Artifact.Payload.Span);

        var result = TransientDetectorInputFactory.Create(value.Artifact, value.Source, value.Levels);

        Assert.IsTrue(result.Validation.IsValid, result.Validation.ReasonCode);
        Assert.IsNotNull(result.Input);
        Assert.AreEqual(TransientDetectorInputOwnership.Owned, result.Input.Ownership);
        Assert.AreEqual(value.Artifact.Payload.Length * 2L, result.BytesScanned);
        Assert.AreEqual(3, result.BytesCopied);
        CollectionAssert.AreEqual(new byte[] { 250, 0 }, result.Input.Pixels.ToArray());
        CollectionAssert.AreEqual(original, value.Artifact.Payload.ToArray());
        Assert.AreEqual(sourceChecksum, Sha256(value.Artifact.Payload.Span));
        Assert.AreEqual(1, result.Input.Descriptor.Layout.Width);
        Assert.AreEqual(1, result.Input.Descriptor.Layout.Height);
        Assert.AreEqual(0.5, result.Input.Descriptor.SourceToDetectorTransform.ScaleX);
        Assert.AreEqual(0.5, result.Input.Descriptor.SourceToDetectorTransform.ScaleY);
        Assert.AreEqual(0, result.Input.Descriptor.SourceToDetectorTransform.OffsetX);
        Assert.AreEqual(0, result.Input.Descriptor.SourceToDetectorTransform.OffsetY);
    }

    [TestMethod]
    public void RggbSaturationMaskPreservesAnyPhotositeClippingHiddenByCellAverage()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.BayerRggb16);
        var pixels = new byte[] { 0xA0, 0x0F, 0, 0, 0, 0, 0, 0 };
        var artifact = value.Artifact with { Payload = pixels };
        var source = value.Source with
        {
            Locator = value.Source.Locator with
            {
                Artifact = value.Source.Locator.Artifact with
                {
                    ChecksumSha256 = Sha256(pixels)
                }
            }
        };

        var result = TransientDetectorInputFactory.Create(artifact, source, value.Levels);

        Assert.IsTrue(result.Validation.IsValid, result.Validation.ReasonCode);
        Assert.IsNotNull(result.Input);
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(result.Input.SaturationMask, 0, 0));
        Assert.IsLessThan(value.Levels.SaturationLevel, BinaryPrimitives.ReadUInt16LittleEndian(result.Input.Pixels.Span));
    }

    [TestMethod]
    public void EquivalentInMemoryAndReconstructedArtifactsProduceSameInputIdentityAndPixels()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.BayerRggb16);
        var reconstructed = value.Artifact with
        {
            Payload = value.Artifact.Payload.ToArray(),
            Layout = value.Artifact.Layout! with { }
        };

        var local = TransientDetectorInputFactory.Create(value.Artifact, value.Source, value.Levels);
        var central = TransientDetectorInputFactory.Create(reconstructed, value.Source, value.Levels);

        Assert.IsTrue(local.Validation.IsValid);
        Assert.IsTrue(central.Validation.IsValid);
        Assert.AreEqual(local.Input!.Descriptor.InputIdentitySha256, central.Input!.Descriptor.InputIdentitySha256);
        CollectionAssert.AreEqual(local.Input.Pixels.ToArray(), central.Input.Pixels.ToArray());
        CollectionAssert.AreEqual(
            TransientContractJson.Serialize(local.Input.Descriptor),
            TransientContractJson.Serialize(central.Input.Descriptor));

        var calibratedArtifact = reconstructed with
        {
            Role = FrameArtifactRole.Calibrated,
            SourceArtifactIds = [Guid.Parse("f0000000-0000-0000-0000-000000000001")]
        };
        var calibratedSource = value.Source with
        {
            Locator = value.Source.Locator with
            {
                Artifact = value.Source.Locator.Artifact with { Role = FrameArtifactRole.Calibrated }
            }
        };
        var calibrated = TransientDetectorInputFactory.Create(calibratedArtifact, calibratedSource, value.Levels);
        Assert.IsTrue(calibrated.Validation.IsValid, calibrated.Validation.ReasonCode);
    }

    [TestMethod]
    public void FactoryReturnsStableReasonsForSourceRoleChecksumTimingAndProfileFailures()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidRole,
            value.Artifact with { Role = FrameArtifactRole.Preview },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact with { ArtifactId = Guid.NewGuid() },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact with { SourceArtifactIds = [value.Artifact.ArtifactId] },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact,
            value.Source with { ObservationEndedUtc = value.Source.ObservationEndedUtc.AddSeconds(1) },
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact,
            value.Source with
            {
                ObservationStartedUtc = value.Source.ObservationStartedUtc.AddSeconds(1),
                ObservationEndedUtc = value.Source.ObservationEndedUtc.AddSeconds(1)
            },
            value.Levels);
        var unrelatedArtifactTimestamp = TransientDetectorInputFactory.Create(
            value.Artifact with { CreatedUtc = DateTimeOffset.MaxValue, Integration = TimeSpan.MaxValue },
            value.Source,
            value.Levels);
        Assert.IsTrue(unrelatedArtifactTimestamp.Validation.IsValid, unrelatedArtifactTimestamp.Validation.ReasonCode);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact with { ObservationStartedUtc = null },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact with
            {
                ObservationStartedUtc = value.Artifact.ObservationEndedUtc,
                ObservationEndedUtc = value.Artifact.ObservationStartedUtc
            },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidSource,
            value.Artifact with
            {
                ObservationStartedUtc = value.Artifact.ObservationStartedUtc!.Value.ToOffset(TimeSpan.FromHours(1))
            },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidProfile,
            value.Artifact with
            {
                Compatibility = value.Artifact.Compatibility with { Calibration = " " }
            },
            value.Source,
            value.Levels);
        AssertReason(
            TransientDetectorInputReasonCodes.ChecksumMismatch,
            value.Artifact,
            value.Source with
            {
                Locator = value.Source.Locator with
                {
                    Artifact = value.Source.Locator.Artifact with { ChecksumSha256 = new string('0', 64) }
                }
            },
            value.Levels);
    }

    [TestMethod]
    public void FactoryRejectsLayoutCfaDepthPackingByteOrderAndLevelFailures()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        AssertLayoutReason(value, value.Artifact.Layout! with { PixelFormat = CameraPixelFormat.Mono8 },
            TransientDetectorInputReasonCodes.UnsupportedFormat);
        AssertLayoutReason(value, value.Artifact.Layout! with { ByteOrder = FrameByteOrder.BigEndian },
            TransientDetectorInputReasonCodes.InvalidByteOrder);
        AssertLayoutReason(value, value.Artifact.Layout! with { SampleDepthBits = 12 },
            TransientDetectorInputReasonCodes.InvalidSampleDepth);
        AssertLayoutReason(value, value.Artifact.Layout! with { Packing = FrameSamplePacking.Packed },
            TransientDetectorInputReasonCodes.InvalidPacking);
        AssertLayoutReason(value, value.Artifact.Layout! with { CfaPattern = ColorFilterArrayPattern.Rggb },
            TransientDetectorInputReasonCodes.InvalidCfa);
        AssertLayoutReason(value, value.Artifact.Layout! with { ByteLength = 7 },
            TransientDetectorInputReasonCodes.InvalidLayout);
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidLevels,
            value.Artifact,
            value.Source,
            value.Levels with { SaturationLevel = 0 });
        AssertReason(
            TransientDetectorInputReasonCodes.InvalidLevels,
            value.Artifact with { Layout = value.Artifact.Layout! with { WhiteLevel = 4094 } },
            value.Source,
            value.Levels);

        var rggb = TransientTestData.CreateDetectorSource(CameraPixelFormat.BayerRggb16);
        var oddLayout = rggb.Artifact.Layout! with { Width = 1, StrideBytes = 2, ByteLength = 4 };
        AssertLayoutReason(rggb, oddLayout, TransientDetectorInputReasonCodes.InvalidLayout);
    }

    [TestMethod]
    public void CancellationIsThrownBeforeValidationOrConversion()
    {
        var value = TransientTestData.CreateDetectorSource(CameraPixelFormat.BayerRggb16);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => TransientDetectorInputFactory.Create(
            value.Artifact,
            value.Source,
            value.Levels,
            cancellation.Token));
    }

    private static void AssertLayoutReason(
        (ProcessingArtifact Artifact, TransientSourceEvidenceReferenceV1 Source, TransientLinearLevelsV1 Levels) value,
        FrameLayoutDescriptor layout,
        string reason)
        => AssertReason(reason, value.Artifact with { Layout = layout }, value.Source, value.Levels);

    private static void AssertReason(
        string expected,
        ProcessingArtifact artifact,
        TransientSourceEvidenceReferenceV1 source,
        TransientLinearLevelsV1 levels)
    {
        var result = TransientDetectorInputFactory.Create(artifact, source, levels);
        Assert.IsNull(result.Input);
        Assert.AreEqual(expected, result.Validation.ReasonCode);
    }

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));
}
