using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest discovers public test classes.")]
public sealed class TimeLapseFrameRendererTests
{
    [TestMethod]
    [DataRow(1936, 1216, 1280, 804)]
    [DataRow(3096, 2080, 1280, 860)]
    [DataRow(1304, 976, 1280, 958)]
    [DataRow(1216, 1936, 804, 1280)]
    [DataRow(3552, 3552, 1280, 1280)]
    [DataRow(513, 301, 512, 300)]
    public void OutputFitUsesAvailableBoundWithEvenProportionalDimensions(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.AreEqual((expectedWidth, expectedHeight), TimeLapseFrameRenderer.Fit(width, height, 1280));
        Assert.AreEqual((width - width % 2, height - height % 2), TimeLapseFrameRenderer.Fit(width, height, 4096));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmupAndThreeFrameMeanKeepLineageAndDistinctSourceOutputDimensions(bool color)
    {
        var rig = Rig(640, 480, color);
        var inputs = Enumerable.Range(0, 3).Select(index => Source(rig, index)).ToArray();
        var first = TimeLapseFrameRenderer.Render([inputs[0]], rig, new(), false, 320, 240);
        var stacked = TimeLapseFrameRenderer.Render(inputs, rig, new(), false, 320, 240);
        Assert.HasCount(1, first.StackSourceIds);
        Assert.HasCount(3, stacked.StackSourceIds);
        Assert.AreEqual(TimeSpan.FromSeconds(60), stacked.TotalIntegration);
        Assert.AreEqual(TimeSpan.FromSeconds(20), first.TotalIntegration);
        var decoded = JpegImageCodec.DecodeJpeg(stacked.Jpeg);
        Assert.AreEqual(640, decoded.Width); // The encoder performs the final qualified Lanczos resize.
        Assert.IsTrue(stacked.Layers.SelectMany(static layer => layer.TextBlocks).SelectMany(static block => block.Lines)
            .Contains("Video 320 x 240", StringComparer.Ordinal));
        Assert.IsTrue(stacked.Layers.SelectMany(static layer => layer.TextBlocks).SelectMany(static block => block.Lines)
            .Contains("Source 640 x 480", StringComparer.Ordinal));
        Assert.AreNotEqual(first.RenderingIdentitySha256, stacked.RenderingIdentitySha256);
        var noOverlay = TimeLapseFrameRenderer.Render(inputs, rig, new(CardinalDirections: false, CornerMetadata: false, ImageCircle: false),
            false, 320, 240);
        Assert.HasCount(0, noOverlay.Layers);
        Assert.AreNotEqual(stacked.RecipeIdentitySha256, noOverlay.RecipeIdentitySha256);
    }

    [TestMethod]
    public void RectangularOverlaysHaveFourCardinalsAndCornerPlatesMeetImageMargins()
    {
        var rig = Rig(1936, 1216, false);
        var frame = TimeLapseFrameRenderer.Render([Source(rig, 0)], rig, new(), false, 1280, 804);
        var blocks = frame.Layers.SelectMany(static layer => layer.TextBlocks).ToArray();
        Assert.AreEqual(4, blocks.Count(static block => block.Anchor == PresentationTextAnchor.Point));
        Assert.AreEqual(4, blocks.Count(static block => block.Anchor != PresentationTextAnchor.Point));
        foreach (var block in blocks)
        {
            using var font = PresentationFont.Create(block, 0);
            var bounds = PresentationFont.BackplateBounds(block, 1936, 1216, font);
            Assert.IsGreaterThanOrEqualTo(0, bounds.Left);
            Assert.IsGreaterThanOrEqualTo(0, bounds.Top);
            Assert.IsLessThanOrEqualTo(1936, bounds.Right);
            Assert.IsLessThanOrEqualTo(1216, bounds.Bottom);
            if (block.Anchor is PresentationTextAnchor.TopLeft or PresentationTextAnchor.BottomLeft) Assert.IsLessThanOrEqualTo(1, bounds.Left);
            if (block.Anchor is PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomRight) Assert.IsLessThanOrEqualTo(1, 1936 - bounds.Right);
        }
    }

    [TestMethod]
    public async Task SequenceAcceptsSerializedEmptyPayloadButRejectsChangedMetadata()
    {
        var rig = Rig(128, 128, false);
        var actual = Source(rig, 0);
        var description = System.Text.Json.JsonSerializer.Deserialize<ProcessingArtifact>(System.Text.Json.JsonSerializer.Serialize(
            actual with { Payload = ReadOnlyMemory<byte>.Empty }))!;
        var sequence = new TimeLapseFrameSequence([description], rig, new(), 128, 128,
            (_, _) => ValueTask.FromResult(actual), _ => false);
        var rendered = await sequence.RenderAsync(actual.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, rendered.StackSourceIds);
        var changed = new TimeLapseFrameSequence([description], rig, new(), 128, 128,
            (_, _) => ValueTask.FromResult(actual with { RecipeIdentitySha256 = new string('B', 64) }), _ => false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await changed.RenderAsync(actual.ArtifactId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DaytimeUsesOnlyCurrentSourceAndNightWarmsUpWithoutDayHistory(bool color)
    {
        var rig = Rig(128, 128, color);
        var sources = Enumerable.Range(0, 6).Select(index => Source(rig, index)).ToArray();
        var descriptions = sources.Select(source => source with { Payload = ReadOnlyMemory<byte>.Empty }).ToArray();
        var restored = new List<Guid>();
        var sequence = new TimeLapseFrameSequence(descriptions, rig, new(), 128, 128,
            (id, _) =>
            {
                restored.Add(id);
                return ValueTask.FromResult(sources.Single(source => source.ArtifactId == id));
            }, utc => utc < DateTimeOffset.UnixEpoch.AddSeconds(60));
        var day = await sequence.RenderAsync(sources[2].ArtifactId, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { sources[2].ArtifactId }, restored);
        CollectionAssert.AreEqual(new[] { sources[2].ArtifactId }, day.StackSourceIds.ToArray());
        Assert.AreEqual(sources[2].Integration, day.TotalIntegration);
        var expected = TimeLapseFrameRenderer.Render([sources[2]], rig, new(), true, 128, 128);
        Assert.IsTrue(expected.Jpeg.Span.SequenceEqual(day.Jpeg.Span));
        Assert.ThrowsExactly<ArgumentException>(() => TimeLapseFrameRenderer.Render(sources[..3], rig, new(), true, 128, 128));
        for (var index = 3; index < sources.Length; index++)
        {
            var night = await sequence.RenderAsync(sources[index].ArtifactId, CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(sources[3..(index + 1)].Select(source => source.ArtifactId).ToArray(), night.StackSourceIds.ToArray());
            Assert.AreEqual(TimeSpan.FromSeconds((index - 2) * 20), night.TotalIntegration);
        }
    }

    [TestMethod]
    public void OptionalQualityAdmissionUsesVerifiedLinearSamplesAndPinnedThreshold()
    {
        var source = Source(Rig(128, 128, false), 0);
        var saturated = source with { Payload = Enumerable.Repeat((byte)255, source.Payload.Length).ToArray() };
        Assert.IsTrue(TimeLapseFrameRenderer.AcceptsQuality(saturated, new()));
        Assert.IsTrue(TimeLapseFrameRenderer.AcceptsQuality(source, new(MaximumSaturatedMillionths: 0)));
        Assert.IsFalse(TimeLapseFrameRenderer.AcceptsQuality(saturated, new(MaximumSaturatedMillionths: 999_999)));
        Assert.IsTrue(TimeLapseFrameRenderer.AcceptsQuality(saturated, new(MaximumSaturatedMillionths: 1_000_000)));
        Assert.AreNotEqual(TimeLapseFrameRenderer.RecipeIdentity(new()), TimeLapseFrameRenderer.RecipeIdentity(new(MaximumSaturatedMillionths: 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TimeLapseFrameRenderer.RecipeIdentity(new(MaximumSaturatedMillionths: -1)));
    }

    [TestMethod]
    public void IncompatibleStackAndOldGapSourcesAreRejectedAndDayTransferIsPinned()
    {
        var rig = Rig(640, 480, false);
        var first = Source(rig, 0);
        Assert.ThrowsExactly<ArgumentException>(() => TimeLapseFrameRenderer.Render([first, first], rig, new(), false, 320, 240));
        Assert.ThrowsExactly<ArgumentException>(() => TimeLapseFrameRenderer.Render([first, Source(rig, 4)], rig, new(), false, 320, 240));
        var dark = TimeLapseFrameRenderer.Render([first], rig, new(), false, 320, 240);
        var day = TimeLapseFrameRenderer.Render([first], rig, new(), true, 320, 240);
        Assert.AreNotEqual(dark.RenderingIdentitySha256, day.RenderingIdentitySha256);
        Assert.IsNotEmpty(TimeLapseFrameRenderer.GapImage(320, 240));
    }

    private static CameraRigConfig Rig(int width, int height, bool color) => new(
        new("Virtual test sensor", width, height, 2, color ? SensorColorMode.Color : SensorColorMode.Mono,
            color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16),
        new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, width / 2d, height / 2d,
            Math.Min(width, height) * .49, HorizontalFlip: true, CalibrationVersion: "timelapse-test-v1"),
        new(90, 0, 0), new(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(.1), TimeSpan.FromSeconds(20), 0, 0));

    private static ProcessingArtifact Source(CameraRigConfig rig, int ordinal)
    {
        var width = rig.Sensor.WidthPixels;
        var height = rig.Sensor.HeightPixels;
        var raw = new byte[width * height * 2];
        for (var index = 0; index < width * height; index++)
        {
            var value = 100 + (index * 17 + ordinal * 53) % 3900;
            raw[index * 2] = (byte)value;
            raw[index * 2 + 1] = (byte)(value >> 8);
        }
        var layout = new FrameLayoutDescriptor(width, height, width * 2, rig.Sensor.PixelFormat, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            0, 65535, raw.Length);
        var identity = RigProjectionContextFactory.CreateProfileHashSha256(rig);
        return new(Guid.NewGuid(), FrameArtifactRole.Raw, "raw", new string('A', 64), "application/octet-stream", layout, raw,
            DateTimeOffset.UnixEpoch.AddSeconds(ordinal * 20), TimeSpan.FromSeconds(20),
            new(identity, identity, "none", "none", "test", "test", "test"),
            ObservationStartedUtc: DateTimeOffset.UnixEpoch.AddSeconds(ordinal * 20));
    }
}
