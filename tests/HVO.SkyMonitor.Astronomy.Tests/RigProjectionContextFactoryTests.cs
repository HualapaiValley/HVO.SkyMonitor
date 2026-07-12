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
