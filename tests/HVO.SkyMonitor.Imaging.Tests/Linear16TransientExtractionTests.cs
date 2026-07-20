using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class Linear16TransientExtractionTests
{
    private static readonly Linear16TransientExtractionOptions Options = new(
        MinimumResidualAdu: 20,
        MinimumComponentPixels: 2,
        MinimumIntegratedSignalAdu: 40,
        MaximumCandidates: 8,
        ProfileSampleCount: 4,
        MaximumSaturationBridgePixels: 8,
        MaximumForegroundPixels: 1_000,
        MaximumFragmentGapPixels: 0,
        MinimumFragmentAlignmentCosine: 0.95);

    [TestMethod]
    public void ExtractsElongatedComponentWithDeterministicGeometryAndProfiles()
    {
        var background = Enumerable.Repeat((ushort)10, 40).ToArray();
        var target = background.ToArray();
        for (var x = 1; x <= 6; x++)
        {
            target[2 * 8 + x] = (ushort)(100 + x * 10);
        }

        var result = Extract(target, background, 8, 5);

        Assert.AreEqual(1, result.Components.Count);
        var component = result.Components[0];
        Assert.AreEqual(17, component.FirstPixelIndex);
        Assert.AreEqual(1, component.BoundsX);
        Assert.AreEqual(2, component.BoundsY);
        Assert.AreEqual(6, component.BoundsWidth);
        Assert.AreEqual(1, component.BoundsHeight);
        Assert.AreEqual(6, component.LengthPixels, 1e-12);
        Assert.AreEqual(1, component.MeanWidthPixels, 1e-12);
        Assert.AreEqual(1, component.MaximumWidthPixels, 1e-12);
        Assert.AreEqual(0, component.SaturatedSampleCount);
        Assert.AreEqual(1, component.FragmentCount);
        Assert.AreEqual(4, component.WidthProfile.Count);
        Assert.AreEqual(0, component.WidthProfile[0].PositionMillionths);
        Assert.AreEqual(1_000_000, component.WidthProfile[^1].PositionMillionths);
        Assert.IsGreaterThan(0, component.StartX);
        Assert.IsGreaterThan(component.StartX, component.EndX);
        Assert.AreEqual(Linear16TransientExtraction.AlgorithmVersion, result.AlgorithmVersion);
    }

    [TestMethod]
    public void SaturatedCoreBridgesFragmentsWithoutSeedingAnIsolatedCandidate()
    {
        var background = new ushort[10];
        var target = new ushort[10];
        target[1] = 100;
        target[2] = 100;
        target[5] = 100;
        target[6] = 100;
        var saturation = Mask(10, 1, 3, 4, 9);

        var result = Linear16TransientExtraction.Extract(
            Frame(target, 10, 1),
            Frame(background, 10, 1),
            Linear16MaskOperations.Empty(10, 1),
            saturation,
            Options);

        Assert.AreEqual(1, result.Components.Count);
        Assert.AreEqual(2, result.Components[0].SaturatedSampleCount);
        Assert.AreEqual(2, result.Components[0].FragmentCount);
        Assert.AreEqual(6, result.Components[0].BoundsWidth);
        Assert.AreEqual(6, result.Components[0].LengthPixels, 1e-12);
        Assert.AreEqual(1, result.Components[0].MaximumWidthPixels, 1e-12);
        CollectionAssert.AreEqual(
            new double[] { 200, 0, 0, 200 },
            result.Components[0].BrightnessProfile.Select(static sample => sample.Value).ToArray());
        Assert.AreEqual(3, result.SaturatedPixelCount);
    }

    [TestMethod]
    public void HardExclusionWinsOverSaturationAndPositiveResiduals()
    {
        var background = new ushort[5];
        var target = new ushort[] { 100, 100, 100, 100, 100 };

        var result = Linear16TransientExtraction.Extract(
            Frame(target, 5, 1),
            Frame(background, 5, 1),
            Mask(5, 1, 2),
            Mask(5, 1, 2),
            Options);

        Assert.AreEqual(2, result.Components.Count);
        Assert.AreEqual(1, result.HardMaskedPixelCount);
        Assert.AreEqual(0, result.SaturatedPixelCount);
        Assert.IsTrue(result.Components.Select(static value => value.FirstPixelIndex).SequenceEqual([0, 3]));
    }

    [TestMethod]
    public void HardMaskRemovesSaturationOnlyBitsButPreservesPersistentOverlapAndNoSupport()
    {
        var effective = Mask(8, 1, 1, 2, 3, 4);
        var saturation = Mask(8, 1, 2, 3);
        var persistent = Mask(8, 1, 1, 2);

        var noSupport = Mask(8, 1, 3, 4);

        var hard = Linear16TransientExtraction.CreateHardExclusionMask(effective, saturation, noSupport, [persistent]);

        Assert.IsTrue(Linear16MaskOperations.IsExcluded(hard, 1, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(hard, 2, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(hard, 3, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(hard, 4, 0));
    }

    [TestMethod]
    public void OrdersByFirstForegroundPixelWhenSaturationBridgeLeadsBounds()
    {
        var first = Linear16TransientExtraction.Extract(
            Frame([0, 0, 100, 100, 0, 0, 100, 100], 8, 1),
            Frame(new ushort[8], 8, 1),
            Linear16MaskOperations.Empty(8, 1),
            Mask(8, 1, 1),
            Options);

        Assert.HasCount(2, first.Components);
        Assert.AreEqual(2, first.Components[0].FirstPixelIndex);
        Assert.AreEqual(6, first.Components[1].FirstPixelIndex);
        Assert.AreEqual(1, first.Components[0].BoundsX);
        Assert.AreEqual(2, first.Components[0].LengthPixels, 1e-12);
        Assert.AreEqual(1, first.Components[0].MeanWidthPixels, 1e-12);
    }

    [TestMethod]
    public void ReturnsNoCandidateForNoiseAndReportsDeterministicLimitOverflow()
    {
        var noEvent = Extract([19, 0, 19], [0, 0, 0], 3, 1);
        Assert.IsEmpty(noEvent.Components);

        var limited = Linear16TransientExtraction.Extract(
            Frame([100, 100, 0, 100, 100], 5, 1),
            Frame(new ushort[5], 5, 1),
            Linear16MaskOperations.Empty(5, 1),
            Linear16MaskOperations.Empty(5, 1),
            Options with { MaximumCandidates = 1 });
        Assert.IsEmpty(limited.Components);
        Assert.IsTrue(limited.CandidateLimitExceeded);
        Assert.AreEqual(Linear16TransientExtractionLimit.Candidates, limited.Limit);
    }

    [TestMethod]
    public void GroupsNearbyAlignedFragmentsBeforeApplyingCandidateLimit()
    {
        var target = new ushort[] { 100, 100, 0, 0, 100, 100 };
        var result = Linear16TransientExtraction.Extract(
            Frame(target, 6, 1),
            Frame(new ushort[6], 6, 1),
            Linear16MaskOperations.Empty(6, 1),
            Linear16MaskOperations.Empty(6, 1),
            Options with { MaximumCandidates = 1, MaximumFragmentGapPixels = 2 });

        Assert.IsFalse(result.CandidateLimitExceeded);
        Assert.HasCount(1, result.Components);
        Assert.AreEqual(2, result.Components[0].FragmentCount);
        Assert.AreEqual(6, result.Components[0].BoundsWidth);
    }

    [TestMethod]
    public void RejectsMalformedInputsAndHonorsCancellationBeforeAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Linear16TransientExtraction.Extract(
            Frame([100, 100], 2, 1),
            Frame([0, 0], 2, 1),
            Linear16MaskOperations.Empty(2, 1),
            Linear16MaskOperations.Empty(2, 1),
            Options,
            cancellation.Token));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientExtraction.Extract(
            Frame([100, 100], 2, 1),
            Frame([0, 0, 0], 3, 1),
            Linear16MaskOperations.Empty(2, 1),
            Linear16MaskOperations.Empty(2, 1),
            Options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Linear16TransientExtraction.Extract(
            Frame([100, 100], 2, 1),
            Frame([0, 0], 2, 1),
            Linear16MaskOperations.Empty(2, 1),
            Linear16MaskOperations.Empty(2, 1),
            Options with { ProfileSampleCount = 1 }));

        var dense = Linear16TransientExtraction.Extract(
            Frame([100, 100, 100], 3, 1),
            Frame([0, 0, 0], 3, 1),
            Linear16MaskOperations.Empty(3, 1),
            Linear16MaskOperations.Empty(3, 1),
            Options with { MaximumForegroundPixels = 2 });
        Assert.IsTrue(dense.CandidateLimitExceeded);
        Assert.IsEmpty(dense.Components);
    }

    private static Linear16TransientExtractionResult Extract(
        IReadOnlyList<ushort> target,
        IReadOnlyList<ushort> background,
        int width,
        int height)
        => Linear16TransientExtraction.Extract(
            Frame(target, width, height),
            Frame(background, width, height),
            Linear16MaskOperations.Empty(width, height),
            Linear16MaskOperations.Empty(width, height),
            Options);

    private static Linear16Frame Frame(IReadOnlyList<ushort> values, int width, int height)
    {
        var bytes = new byte[values.Count * 2];
        for (var index = 0; index < values.Count; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return new Linear16Frame(width, height, width * 2, CameraPixelFormat.Mono16, bytes);
    }

    private static Linear16PixelMask Mask(int width, int height, params int[] pixels)
    {
        var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        foreach (var pixel in pixels)
        {
            bits[pixel >> 3] |= (byte)(1 << (pixel & 7));
        }
        return new Linear16PixelMask(width, height, bits);
    }
}
