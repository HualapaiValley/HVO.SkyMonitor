using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualAstrometryInputTests
{
    [TestMethod]
    public void IndependentReferenceMatchesExternalAstronomyAnchors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "hualapai-asi174-conformance-v1.json")));
        var rig = VirtualAstrometryFixture.Profiles(1)[0].Config.Rig;
        rig = rig with { Orientation = new(90, 0, 0), Optics = rig.Optics with { HorizontalFlip = false } };
        var count = 0;
        foreach (var anchor in document.RootElement.GetProperty("astronomyCases").EnumerateArray())
        {
            var source = document.RootElement.GetProperty("objects").EnumerateArray().Single(item =>
                item.GetProperty("id").GetString() == anchor.GetProperty("objectId").GetString());
            var star = new CelestialCatalogObject(source.GetProperty("id").GetString()!, "external anchor",
                source.GetProperty("rightAscensionHours").GetDouble(), source.GetProperty("declinationDegrees").GetDouble(), 0);
            var site = anchor.GetProperty("observer");
            var observer = new ObserverLocation(site.GetProperty("latitudeDegrees").GetDouble(),
                site.GetProperty("longitudeDegrees").GetDouble(), site.GetProperty("elevationMeters").GetDouble());
            var enu = VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.J2000(star),
                anchor.GetProperty("utc").GetDateTimeOffset(), observer);
            var horizontal = VirtualAstrometryReference.Horizontal(enu);
            var expected = anchor.GetProperty("expectedHorizontal");
            var tolerance = anchor.GetProperty("horizontalToleranceDegrees").GetDouble();
            Assert.AreEqual(expected.GetProperty("altitudeDegrees").GetDouble(), horizontal.AltitudeDegrees, tolerance);
            var azimuthError = Math.Abs(((horizontal.AzimuthDegrees - expected.GetProperty("azimuthDegrees").GetDouble() + 540) % 360) - 180);
            Assert.IsLessThanOrEqualTo(tolerance, azimuthError);
            if (anchor.TryGetProperty("expectedFullPixel", out var expectedPixel))
            {
                var pixel = VirtualAstrometryReference.Project(rig, enu)!.Value;
                Assert.IsLessThanOrEqualTo(anchor.GetProperty("pixelTolerance").GetDouble(),
                    VirtualAstrometryReference.Distance(pixel, new(expectedPixel.GetProperty("x").GetDouble(), expectedPixel.GetProperty("y").GetDouble())));
            }
            count++;
        }
        Assert.AreEqual(4, count);
    }

    [TestMethod]
    [DataRow(FrameByteOrder.LittleEndian)]
    [DataRow(FrameByteOrder.BigEndian)]
    public void PixelDecodeHonorsStrideByteOrderAndStoredSaturation(FrameByteOrder order)
    {
        var layout = Layout(14, 14) with { StrideBytes = 32, ByteLength = 448, ByteOrder = order };
        var payload = Enumerable.Repeat((byte)255, 448).ToArray();
        for (var y = 0; y < 14; y++)
            for (var x = 0; x < 14; x++)
            {
                var span = payload.AsSpan(y * 32 + x * 2, 2);
                var value = (ushort)(y * 14 + x);
                if (order == FrameByteOrder.LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(span, value);
                else BinaryPrimitives.WriteUInt16BigEndian(span, value);
            }
        var last = payload.AsSpan(13 * 32 + 13 * 2, 2);
        if (order == FrameByteOrder.LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(last, 4095);
        else BinaryPrimitives.WriteUInt16BigEndian(last, 4095);
        var (samples, valid) = VirtualAstrometryFixture.Decode(new(payload, layout, Utc, TimeSpan.FromSeconds(1), 0));
        for (var i = 0; i < samples.Length - 1; i++) { Assert.AreEqual((double)i, samples[i]); Assert.IsTrue(valid[i]); }
        Assert.AreEqual(4095d, samples[^1]); Assert.IsFalse(valid[^1]);
        Assert.ThrowsExactly<ArgumentException>(() => VirtualAstrometryFixture.Decode(
            new(payload.AsMemory(1), layout, Utc, TimeSpan.FromSeconds(1), 0)));
    }

    [TestMethod]
    public void ProjectedTruthCannotChangeMeasurementOrSourceIdentity()
    {
        var layout = Layout(32, 32); var payload = new byte[(int)layout.ByteLength];
        for (var i = 0; i < 32 * 32; i++) BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 2), 64);
        foreach (var (dx, dy, value) in new[] { (0, 0, 1000), (-1, 0, 200), (1, 0, 200), (0, -1, 200), (0, 1, 200), (-1, -1, 100), (1, 1, 100), (-1, 1, 100), (1, -1, 100) })
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(((16 + dy) * 32 + 16 + dx) * 2), (ushort)value);
        var frame = new CameraFrame(Utc, 32, 32, CameraPixelFormat.Mono16, payload, new(TimeSpan.FromSeconds(1), 0, 0)) { Layout = layout };
        var poisoned = frame with
        {
            Metadata = frame.Metadata with
            {
                Scene = new("misleading-scene", "wrong-rig", "wrong-catalog", "wrong-version", new string('F', 64),
                    "wrong-family", "wrong-projection", "wrong-algorithm", "wrong-recipe",
                    Objects: [new("fake-correspondence", "fake source", 1, 1, -20)], SceneUtc: Utc.AddYears(5))
            }
        };
        var nominal = new AstrometricCalibration(new(ProjectionModel.EquidistantFisheye, 16, 16, 8, 8,
            32, 32, ProjectionAperture.Circular, 15), "input-isolation", new string('0', 64));
        var originalInput = VirtualAstrometryPixels.FromCapture(frame);
        var poisonedInput = VirtualAstrometryPixels.FromCapture(poisoned);
        var measured = VirtualAstrometryFixture.Measure(originalInput, nominal);
        Assert.HasCount(1, measured.Detections);
        CollectionAssert.AreEqual(measured.Detections.ToArray(), VirtualAstrometryFixture.Measure(poisonedInput, nominal).Detections.ToArray());
        Assert.AreEqual(VirtualAstrometryFixture.FrameContext(originalInput), VirtualAstrometryFixture.FrameContext(poisonedInput));
        payload.AsSpan().Clear();
        Assert.AreEqual(1000d, VirtualAstrometryFixture.Decode(originalInput).Samples[16 * 32 + 16]);
        var differentPose = new AstrometricCalibration(nominal.Projection with
        {
            BoresightAltitudeDegrees = 12,
            BoresightAzimuthDegrees = 123,
            RollDegrees = 81
        }, "different-pose", new string('0', 64));
        CollectionAssert.AreEqual(measured.Detections.ToArray(), VirtualAstrometryFixture.Measure(originalInput, differentPose).Detections.ToArray());
    }

    [TestMethod]
    public async Task UnsupportedCameraBayerReadoutsAndUnequalRadialBinsReject()
    {
        var profiles = VirtualAstrometryFixture.Profiles(1);
        var cfa = profiles.Single(p => p.Name == "cfa-native").Config;
        var catalog = new InMemoryCelestialCatalog([]);
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        var crop = cfa with
        {
            Rig = cfa.Rig with
            {
                Readout = cfa.Rig.Readout! with
                { Roi = new(2, 2, 3094, 2078), StrideBytes = 6188 }
            }
        };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => module.InitializeAsync(crop, CancellationToken.None)).ConfigureAwait(false);
        var bin = cfa with
        {
            Rig = cfa.Rig with
            {
                Readout = cfa.Rig.Readout! with
                { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1, StrideBytes = 3096 }
            }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => module.InitializeAsync(bin, CancellationToken.None)).ConfigureAwait(false);
        var mono = profiles[0].Config.Rig;
        var unequal = mono with { Readout = mono.Readout! with { BinX = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 } };
        Assert.ThrowsExactly<NotSupportedException>(() => RigProjectionContextFactory.Create(unequal));
        var charge = mono with { Readout = mono.Readout! with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.ChargeSumV1 } };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => module.InitializeAsync(profiles[0].Config with { Rig = charge }, CancellationToken.None)).ConfigureAwait(false);
    }

    private static DateTimeOffset Utc => new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static FrameLayoutDescriptor Layout(int width, int height) => new(width, height, width * 2,
        CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned,
        ColorFilterArrayPattern.None, 64, 4095, width * height * 2)
    { StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1, LevelCodeSpace = FrameLevelCodeSpace.NativeSample };
}
