using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class RadialDistortionTests
{
    private static readonly ProjectionContext[] Distorted =
    [
        new(ProjectionModel.EquidistantFisheye, 259.5, 253, 150, 150, 512, 512, ProjectionAperture.Circular, 232,
            HorizontalFlip: true, RadialDistortionK1: -.006),
        new(ProjectionModel.EquisolidFisheye, 256, 256, 150, 150, 512, 512, ProjectionAperture.Circular, 232,
            RadialDistortionK1: .01),
        new(ProjectionModel.OrthographicFisheye, 256, 256, 240, 240, 512, 512, ProjectionAperture.Circular, 232,
            RadialDistortionK1: -.02),
        new(ProjectionModel.Perspective, 322, 236, 500, 505, 640, 480, ProjectionAperture.Rectangular, null,
            BoresightAltitudeDegrees: 40, BoresightAzimuthDegrees: 120, RollDegrees: 7, RadialDistortionK1: .05)
    ];

    [TestMethod]
    public void Undistort_InvertsDistortWithinTheSupportedDomain()
    {
        foreach (var model in Enum.GetValues<ProjectionModel>())
        {
            foreach (var k1 in new[] { -.5, -.02, -.001, .001, .02, .5 })
            {
                var maximum = Math.Min(RadialDistortion.MaximumIdealRadius(model, k1), 3);
                for (var step = 0; step <= 50; step++)
                {
                    var ideal = maximum * step / 50;
                    var distorted = RadialDistortion.Distort(ideal, k1);
                    Assert.AreEqual(ideal, RadialDistortion.Undistort(distorted, model, k1), 1e-12, $"{model} k1={k1} r={ideal}");
                }
            }
        }
        Assert.AreEqual(.7, RadialDistortion.Undistort(.7, ProjectionModel.Perspective, 0));
        Assert.IsTrue(double.IsNaN(RadialDistortion.Undistort(-.1, ProjectionModel.Perspective, .01)));
        Assert.IsTrue(double.IsNaN(RadialDistortion.Undistort(
            RadialDistortion.MaximumDistortedRadius(ProjectionModel.EquidistantFisheye, -.2) * 1.01,
            ProjectionModel.EquidistantFisheye, -.2)));
    }

    [TestMethod]
    public void DistortedProjectors_RoundTripEveryFamilyAndKeepRadialDerivativeMargin()
    {
        foreach (var context in Distorted)
        {
            context.Validate();
            var projector = ProjectorFactory.Create(context);
            var ideal = ProjectorFactory.Create(context with { RadialDistortionK1 = 0, EnforceSensorBounds = false, ImageCircleRadiusPixels = context.Aperture == ProjectionAperture.Circular ? RadialDistortion.MaximumIdealRadius(context.Model, 0) * context.FocalLengthXPixels : null });
            var tested = 0;
            for (var y = 1.5; y < context.HeightPixels; y += 17)
            {
                for (var x = 1.5; x < context.WidthPixels; x += 17)
                {
                    var pixel = new PixelPoint(x, y);
                    if (projector.Unproject(pixel) is not { } direction) continue;
                    var projected = projector.Project(direction);
                    Assert.IsNotNull(projected, $"{context.Model} {pixel}");
                    Assert.AreEqual(x, projected.Value.X, 1e-7, context.Model.ToString());
                    Assert.AreEqual(y, projected.Value.Y, 1e-7, context.Model.ToString());

                    // The distortion is radial about the principal point and scales normalized radius by 1 + k1 r^2.
                    var undistorted = ideal.Project(direction)!.Value;
                    var nx = (undistorted.X - context.PrincipalPointX) / context.FocalLengthXPixels;
                    var ny = (context.PrincipalPointY - undistorted.Y) / context.FocalLengthYPixels;
                    var scale = 1 + context.RadialDistortionK1 * (nx * nx + ny * ny);
                    Assert.AreEqual(context.PrincipalPointX + context.FocalLengthXPixels * nx * scale, x, 1e-7);
                    Assert.AreEqual(context.PrincipalPointY - context.FocalLengthYPixels * ny * scale, y, 1e-7);
                    tested++;
                }
            }
            Assert.IsGreaterThan(200, tested, context.Model.ToString());

            var edge = RadialDistortion.Undistort(RadialDistortion.ApertureEdgeRadius(context), context.Model, context.RadialDistortionK1);
            Assert.IsGreaterThanOrEqualTo(RadialDistortion.MinimumRadialDerivative, 1 + 3 * context.RadialDistortionK1 * edge * edge);
        }
    }

    [TestMethod]
    public void Validate_RejectsNoninvertibleOrOutOfRangeCoefficients()
    {
        var baseline = Distorted[0];
        // R/f = 1.6 lies beyond the image of the margin-limited domain for k1 = -0.2 (about 0.76).
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (baseline with { FocalLengthXPixels = 145, FocalLengthYPixels = 145, RadialDistortionK1 = -.2 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (baseline with { RadialDistortionK1 = .6 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (baseline with { RadialDistortionK1 = double.NaN }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Distorted[3] with { RadialDistortionK1 = -.5 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProjectorFactory.Create(baseline with { RadialDistortionK1 = -.3 }));
    }

    [TestMethod]
    public void BlindSearch_KeepsDistortedPerspectiveScalesInsideTheSupportedDomain()
    {
        // Corner radius 2.404163 sits just inside the k1 = -0.02 supported maximum 2.405626, so scale 0.99 would leave it.
        var context = new ProjectionContext(ProjectionModel.Perspective, 1700, 1700, 1000, 1000, 3400, 3400, ProjectionAperture.Rectangular, null,
            BoresightAltitudeDegrees: 90, BoresightAzimuthDegrees: 0, RollDegrees: 0, RadialDistortionK1: -.02);
        context.Validate();
        var optics = SolverOptics.From(context);
        var minimum = AstrometricSolverCore.MinimumPhysicalScale(optics);
        Assert.AreEqual(RadialDistortion.ApertureEdgeRadius(context) / RadialDistortion.MaximumDistortedRadius(ProjectionModel.Perspective, -.02), minimum, 1e-15);
        Assert.IsGreaterThan(.99, minimum);
        Assert.IsLessThan(1, minimum);
        _ = new AstrometricRayCamera(optics, minimum);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AstrometricRayCamera(optics, .99));

        // The default ±10% blind search and the acquisition path return reason-coded outcomes instead of throwing.
        var detections = Enumerable.Range(0, 40).Select(i => new CoreDetection(i, 200 + 73 * i % 3000, 300 + 131 * i % 2800, 1000 - i, 100)).ToArray();
        var site = new CoreSite(35, -114);
        var utc = new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);
        var solved = AstrometricSolverCore.Solve(detections, AstrometricTestFixture.Catalog().Stars, optics, site, utc);
        Assert.IsFalse(solved.Accepted);
        var candidates = new List<CoreCandidate>();
        _ = AstrometricSolverCore.Acquire(detections, AstrometricTestFixture.Catalog().Stars, optics, site, utc, candidates);
        Assert.IsTrue(candidates.All(c => c.Scale >= minimum));

        // Undistorted and positive-coefficient perspective views keep an unbounded lower scale.
        Assert.AreEqual(0, AstrometricSolverCore.MinimumPhysicalScale(SolverOptics.From(context with { RadialDistortionK1 = 0 })));
        Assert.AreEqual(0, AstrometricSolverCore.MinimumPhysicalScale(SolverOptics.From(context with { RadialDistortionK1 = .02 })));
    }

    [TestMethod]
    public void ZeroCoefficient_KeepsExistingSerializedIdentities()
    {
        var context = Distorted[0] with { RadialDistortionK1 = 0 };
        Assert.DoesNotContain("RadialDistortionK1", JsonSerializer.Serialize(context));
        Assert.Contains("\"RadialDistortionK1\":-0.006", JsonSerializer.Serialize(Distorted[0]));

        var optics = new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, PrincipalPointX: 256, PrincipalPointY: 256,
            ImageCircleRadiusPixels: 232, FocalLengthXPixels: 150, FocalLengthYPixels: 150);
        Assert.DoesNotContain("RadialDistortionK1", JsonSerializer.Serialize(optics));
        Assert.Contains("RadialDistortionK1", JsonSerializer.Serialize(optics with { RadialDistortionK1 = .01 }));

        var projection = new ProjectedSceneProjection(ProjectionModel.EquidistantFisheye, ProjectionAperture.Circular, "v1", "a1",
            512, 512, 256, 256, 150, 150, 232, 90, 0, 0, false, true);
        Assert.DoesNotContain("RadialDistortionK1", JsonSerializer.Serialize(projection));
        Assert.Contains("RadialDistortionK1", JsonSerializer.Serialize(projection with { RadialDistortionK1 = .01 }));
        Assert.AreEqual(context, JsonSerializer.Deserialize<ProjectionContext>(JsonSerializer.Serialize(context)));
        Assert.AreEqual(Distorted[0], JsonSerializer.Deserialize<ProjectionContext>(JsonSerializer.Serialize(Distorted[0])));
    }

    [TestMethod]
    public void CreateReadoutView_MapsOneNativeCalibrationAndRejectsForeignReadouts()
    {
        var native = Distorted[0];
        var view = RigProjectionContextFactory.CreateReadoutView(native, Readout(64, 32, 384, 448, 2, 2));
        Assert.AreEqual((259.5 - 64) / 2, view.PrincipalPointX, 1e-12);
        Assert.AreEqual((253 - 32) / 2d, view.PrincipalPointY, 1e-12);
        Assert.AreEqual(75, view.FocalLengthXPixels, 1e-12);
        Assert.AreEqual(116, view.ImageCircleRadiusPixels!.Value, 1e-12);
        Assert.AreEqual(native.RadialDistortionK1, view.RadialDistortionK1);
        Assert.AreEqual(192, view.WidthPixels);
        Assert.AreEqual(224, view.HeightPixels);

        // A direction seen in both the native and derived views lands on the same photosite.
        var nativeProjector = ProjectorFactory.Create(native);
        var viewProjector = ProjectorFactory.Create(view);
        var direction = nativeProjector.Unproject(new PixelPoint(300.25, 200.75))!.Value;
        var mapped = viewProjector.Project(direction)!.Value;
        Assert.AreEqual((300.25 - 64) / 2, mapped.X, 1e-7);
        Assert.AreEqual((200.75 - 32) / 2, mapped.Y, 1e-7);

        foreach (var readout in new[]
        {
            Readout(0, 0, 512, 512, 1, 1) with { NativeWidth = 1024 },
            Readout(-1, 0, 256, 256, 1, 1),
            Readout(300, 0, 256, 256, 1, 1),
            Readout(0, 0, 255, 256, 2, 2),
            Readout(0, 0, 256, 256, 0, 1),
            Readout(0, 0, 0, 256, 1, 1)
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => RigProjectionContextFactory.CreateReadoutView(native, readout), readout.ToString());
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => RigProjectionContextFactory.CreateReadoutView(native, null!));
    }

    private static FrameReadoutDescriptor Readout(int x, int y, int width, int height, int binX, int binY)
        => new(512, 512, x, y, width, height, binX, binY, binX == 1 && binY == 1 ? FrameBinningAlgorithm.IdentityV1 : FrameBinningAlgorithm.DigitalSumV1, null, null);
}
