using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class RigProjectionContextFactoryTests
{
    [TestMethod]
    public void Create_UsesCalibratedFisheyeGeometryAndOrientation()
    {
        var rig = CreateRig(
            new OpticsProfile(
                "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                PrincipalPointX: 968, PrincipalPointY: 608, ImageCircleRadiusPixels: 595.84,
                FocalLengthXPixels: 379.323, FocalLengthYPixels: 379.323,
                HorizontalFlip: true),
            new RigOrientation(89, 12, 3));

        var projection = RigProjectionContextFactory.Create(rig);

        Assert.AreEqual(ProjectionModel.EquidistantFisheye, projection.Model);
        Assert.AreEqual(ProjectionAperture.Circular, projection.Aperture);
        Assert.AreEqual(968, projection.PrincipalPointX);
        Assert.AreEqual(608, projection.PrincipalPointY);
        Assert.AreEqual(595.84, projection.ImageCircleRadiusPixels);
        Assert.AreEqual(379.323, projection.FocalLengthXPixels);
        Assert.AreEqual(89, projection.BoresightAltitudeDegrees);
        Assert.AreEqual(12, projection.BoresightAzimuthDegrees);
        Assert.AreEqual(3, projection.RollDegrees);
        Assert.IsTrue(projection.HorizontalFlip);
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(RigProjectionContextFactory.Create(
            rig with { Orientation = new RigOrientation(90, 0, 0) }));
        Assert.IsNotNull(landmarks);
        Assert.AreEqual(new PixelPoint(968, 608), landmarks.Center);
        Assert.AreEqual(595.84, landmarks.ImageCircleRadius);
    }

    [TestMethod]
    public void CreateAnnotationLandmarks_ClipsHorizonDirectionsToNarrowFisheyeAperture()
    {
        var rig = CreateRig(new OpticsProfile(
            "EquidistantFisheye", 2.5, 170, 0, LensKind.Fisheye,
            PrincipalPointX: 1776, PrincipalPointY: 1776, ImageCircleRadiusPixels: 1627.5,
            FocalLengthXPixels: 1097.0456, FocalLengthYPixels: 1097.0456));

        var projection = RigProjectionContextFactory.Create(rig);
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(projection);

        Assert.IsNotNull(landmarks);
        Assert.AreEqual(1776, landmarks.North.X, 1e-9);
        Assert.AreEqual(148.5, landmarks.North.Y, 1e-9);
        Assert.AreEqual(3403.5, landmarks.East.X, 1e-9);
        Assert.AreEqual(1776, landmarks.East.Y, 1e-9);
        Assert.AreEqual(1776, landmarks.South.X, 1e-9);
        Assert.AreEqual(3403.5, landmarks.South.Y, 1e-9);
        Assert.AreEqual(148.5, landmarks.West.X, 1e-9);
        Assert.AreEqual(1776, landmarks.West.Y, 1e-9);
    }

    [TestMethod]
    public void Create_DerivesPerspectiveFocalLengthsFromFieldOfView()
    {
        var rig = CreateRig(new OpticsProfile(
            "Rectilinear", 0, 90, 0, LensKind.Rectilinear,
            VerticalFieldOfViewDegrees: 60));

        var projection = RigProjectionContextFactory.Create(rig);

        Assert.AreEqual(ProjectionModel.Perspective, projection.Model);
        Assert.AreEqual(ProjectionAperture.Rectangular, projection.Aperture);
        Assert.AreEqual(968, projection.FocalLengthXPixels, 1e-10);
        Assert.AreEqual(1216 / (2 * Math.Tan(Math.PI / 6)), projection.FocalLengthYPixels, 1e-10);
        Assert.IsNull(projection.ImageCircleRadiusPixels);
    }

    [TestMethod]
    public void Create_UsesPhysicalFocalLengthAndRejectsUnequalRadialCalibration()
    {
        var physical = CreateRig(new OpticsProfile("Gnomonic", 2.5, 90, 0, LensKind.Telescope));
        var projection = RigProjectionContextFactory.Create(physical);

        Assert.AreEqual(2.5 / 0.00586, projection.FocalLengthXPixels, 1e-10);
        Assert.AreEqual(projection.FocalLengthXPixels, projection.FocalLengthYPixels);

        var invalid = CreateRig(new OpticsProfile(
            "EquisolidFisheye", 0, 180, 0, LensKind.Fisheye,
            FocalLengthXPixels: 100, FocalLengthYPixels: 101));
        Assert.ThrowsExactly<NotSupportedException>(() => RigProjectionContextFactory.Create(invalid));
        var legacyRoll = CreateRig(new OpticsProfile("EquidistantFisheye", 0, 180, 1, LensKind.Fisheye));
        Assert.ThrowsExactly<NotSupportedException>(() => RigProjectionContextFactory.Create(legacyRoll));
    }

    [TestMethod]
    public void ParseModel_RejectsUnknownAndBlankNames()
    {
        Assert.AreEqual(ProjectionModel.Perspective, RigProjectionContextFactory.ParseModel("gnomonic"));
        Assert.ThrowsExactly<ArgumentException>(() => RigProjectionContextFactory.ParseModel(" "));
        Assert.ThrowsExactly<NotSupportedException>(() => RigProjectionContextFactory.ParseModel("unknown"));
    }

    [TestMethod]
    public void Create_TransformsAsi174NativeRoiAndFullFrameBinning()
    {
        var rig = CreateRig(new OpticsProfile(
            "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
            PrincipalPointX: 968, PrincipalPointY: 608, ImageCircleRadiusPixels: 595.84,
            FocalLengthXPixels: 379.323, FocalLengthYPixels: 379.323));
        var readout = new SensorReadoutProfile(
            new SensorCrop(648, 368, 640, 480), 4, 4, FrameBinningAlgorithm.DigitalAverageV1,
            CameraPixelFormat.Mono8, 8, 8, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.StoredContainer, 4, 255);

        var roiProjection = RigProjectionContextFactory.Create(rig with { Readout = readout });
        var nativeRoiProjection = RigProjectionContextFactory.CreateNativeRoi(rig with { Readout = readout });

        Assert.AreEqual(160, roiProjection.WidthPixels);
        Assert.AreEqual(120, roiProjection.HeightPixels);
        Assert.AreEqual(80, roiProjection.PrincipalPointX);
        Assert.AreEqual(60, roiProjection.PrincipalPointY);
        Assert.AreEqual(148.96, roiProjection.ImageCircleRadiusPixels);
        Assert.AreEqual(379.323 / 4, roiProjection.FocalLengthXPixels, 1e-12);
        Assert.AreEqual(320, nativeRoiProjection.PrincipalPointX);
        Assert.AreEqual(240, nativeRoiProjection.PrincipalPointY);

        var full = RigProjectionContextFactory.Create(rig with
        {
            Readout = readout with { Roi = new SensorCrop(0, 0, 1936, 1216) }
        });
        Assert.AreEqual(484, full.WidthPixels);
        Assert.AreEqual(304, full.HeightPixels);
        Assert.AreEqual(242, full.PrincipalPointX);
        Assert.AreEqual(152, full.PrincipalPointY);
        Assert.AreEqual(148.96, full.ImageCircleRadiusPixels);
    }

    [TestMethod]
    public void Create_AllowsCroppedFisheyeWithPrincipalPointOutsideReadout()
    {
        var rig = CreateRig(new OpticsProfile(
            "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
            PrincipalPointX: 968, PrincipalPointY: 608, ImageCircleRadiusPixels: 595.84,
            FocalLengthXPixels: 379.323, FocalLengthYPixels: 379.323)) with
        {
            Readout = new SensorReadoutProfile(
                new SensorCrop(400, 368, 480, 480), 4, 4, FrameBinningAlgorithm.DigitalAverageV1,
                CameraPixelFormat.Mono8, 8, 8, FrameSamplePacking.ByteAligned,
                FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.StoredContainer, 4, 255)
        };

        var projection = RigProjectionContextFactory.Create(rig);
        var projector = ProjectorFactory.Create(projection);

        Assert.AreEqual(142, projection.PrincipalPointX);
        Assert.AreEqual(60, projection.PrincipalPointY);
        Assert.IsNotNull(projector.Unproject(new PixelPoint(119, 60)));
    }

    [TestMethod]
    public void Create_RejectsUnequalRadialBinsButAllowsRectilinearScale()
    {
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, 640, 480), 4, 2, FrameBinningAlgorithm.DigitalAverageV1,
            CameraPixelFormat.Mono8, 8, 8, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.StoredContainer, 0, 255);
        var fisheye = CreateRig(new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye)) with
        {
            Readout = readout
        };
        Assert.ThrowsExactly<NotSupportedException>(() => RigProjectionContextFactory.Create(fisheye));

        var rectilinear = CreateRig(new OpticsProfile(
            "Rectilinear", 0, 90, 0, LensKind.Rectilinear,
            FocalLengthXPixels: 800, FocalLengthYPixels: 600)) with
        {
            Readout = readout
        };
        var transformed = RigProjectionContextFactory.Create(rectilinear);
        Assert.AreEqual(200, transformed.FocalLengthXPixels);
        Assert.AreEqual(300, transformed.FocalLengthYPixels);
    }

    private static CameraRigConfig CreateRig(OpticsProfile optics, RigOrientation? orientation = null)
        => new(
            new SensorProfile(
                "ASI174", 1936, 1216, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                SensorResponseMode.Monochrome),
            optics,
            orientation ?? new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), 0, 150));
}
