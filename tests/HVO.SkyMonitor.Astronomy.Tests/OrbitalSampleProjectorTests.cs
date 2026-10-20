using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalSampleProjectorTests
{
    [TestMethod]
    public void NativeAndReadoutCoordinatesApplyCropBinMirrorAndRotationExactlyOnce()
    {
        var original = OrbitalContractExamples.Geometry;
        var horizontal = ProjectorFactory.Create(original.Projection).Unproject(new(360, 300))!.Value;
        var readout = original.Readout with
        {
            RoiX = 160,
            RoiY = 160,
            RoiWidth = 320,
            RoiHeight = 320,
            BinX = 2,
            BinY = 2,
            BinningAlgorithm = FrameBinningAlgorithm.DigitalSumV1
        };
        var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            640, 640, 160, 160, 320, 320, 2, 2, true, false, ProjectedSceneQuarterRotation.Degrees90, 160, 160);
        var native = new OrbitalSampleProjector(original with { Readout = readout, ImageTransform = transform });
        var actual = native.Project(OrbitalContractExamples.Epoch, State(horizontal));
        Assert.IsNull(actual.ClipReason); AssertPixel(new(90, 60), actual.Sample.Pixel!.Value);
        Assert.AreEqual(.5, native.RemainingPixelScaleBound);
        var view = new OrbitalSampleProjector(original with
        {
            ProjectionCoordinates = OrbitalProjectionCoordinates.ReadoutView,
            Readout = readout,
            Projection = original.Projection with
            {
                WidthPixels = 160,
                HeightPixels = 160,
                PrincipalPointX = 80,
                PrincipalPointY = 80,
                FocalLengthXPixels = 100,
                FocalLengthYPixels = 100,
                ImageCircleRadiusPixels = 157
            },
            ImageTransform = ProjectedSceneImageTransformV1.Identity(160, 160) with
            { HorizontalMirror = true, Rotation = ProjectedSceneQuarterRotation.Degrees90 }
        });
        AssertPixel(actual.Sample.Pixel.Value, view.Project(OrbitalContractExamples.Epoch, State(horizontal)).Sample.Pixel!.Value);
        Assert.AreEqual(1, view.RemainingPixelScaleBound);
    }

    [TestMethod]
    public void HorizonApertureAndCropClipsRemainDistinctAndIlluminationUnknown()
    {
        var geometry = OrbitalContractExamples.Geometry;
        var wide = geometry with { Projection = geometry.Projection with { FocalLengthXPixels = 100, FocalLengthYPixels = 100 } };
        var below = State(new(-10, 0));
        var horizon = new OrbitalSampleProjector(wide).Project(OrbitalContractExamples.Epoch, below);
        Assert.AreEqual(OrbitalClipReason.Horizon, horizon.ClipReason); Assert.IsNull(horizon.Sample.Pixel);
        var opticalOnly = new OrbitalSampleProjector(wide with { HorizonPolicy = HorizonPolicy.ProjectionOnly });
        Assert.IsNotNull(opticalOnly.Project(OrbitalContractExamples.Epoch, below).Sample.Pixel);
        var aperture = new OrbitalSampleProjector(geometry with { HorizonPolicy = HorizonPolicy.ProjectionOnly })
            .Project(OrbitalContractExamples.Epoch, State(new(-80, 0)));
        Assert.AreEqual(OrbitalClipReason.Aperture, aperture.ClipReason);
        var cropped = geometry with
        {
            Readout = geometry.Readout with { RoiX = 160, RoiY = 160, RoiWidth = 320, RoiHeight = 320 },
            ImageTransform = new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 640, 640,
                160, 160, 320, 320, 1, 1, false, false, ProjectedSceneQuarterRotation.Degrees0, 320, 320)
        };
        var outsideCrop = ProjectorFactory.Create(geometry.Projection).Unproject(new(100, 320))!.Value;
        var sensor = new OrbitalSampleProjector(cropped).Project(OrbitalContractExamples.Epoch, State(outsideCrop));
        Assert.AreEqual(OrbitalClipReason.Sensor, sensor.ClipReason); Assert.IsNull(sensor.Sample.Pixel);
        Assert.AreEqual(OrbitalIllumination.Unknown, sensor.Sample.Illumination);
        Assert.AreEqual(-1, sensor.Sample.RangeRateKilometersPerSecond);
    }

    private static OrbitalTopocentricState State(AltAzPoint point) => new(point, 1000, -1, .2, CameraBasis.FromHorizontal(point));
    private static void AssertPixel(PixelPoint expected, PixelPoint actual)
    {
        Assert.AreEqual(expected.X, actual.X, 1e-8); Assert.AreEqual(expected.Y, actual.Y, 1e-8);
    }
}
