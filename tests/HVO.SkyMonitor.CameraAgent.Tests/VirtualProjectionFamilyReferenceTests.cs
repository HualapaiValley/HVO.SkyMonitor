using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// The issue #1126 truth scorer is an independent restatement of each projection family. These checks pin it to the
/// production projectors on every frozen family profile so a qualification failure cannot come from the scorer itself.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class VirtualProjectionFamilyReferenceTests
{
    private static readonly string[] MonoProfileNames = ["mono-native", "mono-roi", "mono-bin2", "mono-roi-bin2", "mono-mirror", "mono-roll"];

    [TestMethod]
    public void FrozenMatrix_DeclaresEveryFamilyOnceWithTheEquidistantBaselineFirst()
    {
        var families = VirtualAstrometryFixture.Families;
        Assert.AreEqual("equidistant", families[0].Name);
        Assert.HasCount(families.Count, families.Select(f => f.Name).Distinct());
        Assert.HasCount(families.Count, families.Select(f => f.CalibrationVersion).Distinct());
        foreach (var model in Enum.GetValues<ProjectionModel>())
            Assert.IsTrue(families.Any(f => f.Model == model), model.ToString());
        Assert.HasCount(7, VirtualAstrometryFixture.Profiles(1, families[0]));
        foreach (var family in families.Skip(1))
        {
            var profiles = VirtualAstrometryFixture.Profiles(1, family);
            CollectionAssert.AreEqual(MonoProfileNames, profiles.Select(p => p.Name).ToArray(), family.Name);
            Assert.IsTrue(profiles.All(p => p.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.Mono16), "CFA is outside every new family.");
        }
    }

    [TestMethod]
    public void NominalCalibration_IsValidAndOffsetFromTruthForEveryFamilyProfile()
    {
        foreach (var family in VirtualAstrometryFixture.Families)
            foreach (var profile in VirtualAstrometryFixture.Profiles(1, family))
            {
                var nominal = VirtualAstrometryFixture.NominalCalibration(profile);
                nominal.Projection.Validate();
                Assert.AreEqual(family.Model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
                    nominal.Projection.Aperture, $"{family.Name}/{profile.Name}");
                if (profile.Config.Rig.Sensor.PixelFormat != CameraPixelFormat.Mono16) continue;
                var truth = VirtualAstrometryReference.NativeFocal(profile.Config.Rig) / profile.Config.Rig.Readout!.BinX;
                Assert.AreEqual(family.NominalFocalFactor, nominal.Projection.FocalLengthXPixels / truth, 1e-12, $"{family.Name}/{profile.Name}");
            }
    }

    [TestMethod]
    public void ReferenceProjection_MatchesProductionProjectorForEveryFamilyProfile()
    {
        foreach (var family in VirtualAstrometryFixture.Families)
            foreach (var profile in VirtualAstrometryFixture.Profiles(1, family))
            {
                var rig = profile.Config.Rig;
                var projector = ProjectorFactory.Create(RigProjectionContextFactory.Create(rig));
                var compared = 0;
                for (var altitude = 1d; altitude < 90; altitude += 4)
                    for (var azimuth = 0d; azimuth < 360; azimuth += 7.5)
                    {
                        var a = VirtualAstrometryReference.Radians(altitude); var z = VirtualAstrometryReference.Radians(azimuth);
                        var enu = new VirtualAstrometryReference.Vector(Math.Cos(a) * Math.Sin(z), Math.Cos(a) * Math.Cos(z), Math.Sin(a));
                        var expected = VirtualAstrometryReference.Project(rig, enu);
                        var actual = projector.Project(new(altitude, azimuth));
                        // Exact aperture-boundary rays may legitimately differ by the production boundary tolerance.
                        if (expected is null || actual is null) continue;
                        Assert.AreEqual(expected.Value.X, actual.Value.X, 1e-6, $"{family.Name}/{profile.Name} {altitude},{azimuth}");
                        Assert.AreEqual(expected.Value.Y, actual.Value.Y, 1e-6, $"{family.Name}/{profile.Name} {altitude},{azimuth}");
                        compared++;
                    }
                Assert.IsGreaterThan(40, compared, $"{family.Name}/{profile.Name}");
            }
    }

    [TestMethod]
    [DataRow(-.008)]
    [DataRow(0d)]
    [DataRow(.006)]
    public void ReferenceInverse_RoundTripsDistortedOpticsForEveryFamily(double k1)
    {
        foreach (var family in VirtualAstrometryFixture.Families)
        {
            var native = VirtualAstrometryFixture.Profiles(1, family)[0].Config.Rig;
            var rig = native with { Optics = native.Optics with { RadialDistortionK1 = k1 } };
            var projector = ProjectorFactory.Create(RigProjectionContextFactory.Create(rig));
            var tested = 0;
            for (var y = 3.5; y < 1216; y += 41)
                for (var x = 3.5; x < 1936; x += 41)
                {
                    var pixel = new PixelPoint(x, y);
                    if (VirtualAstrometryReference.Unproject(rig, pixel) is not { } ray) continue;
                    var back = VirtualAstrometryReference.Project(rig, ray);
                    Assert.IsNotNull(back, $"{family.Name} {pixel}");
                    Assert.AreEqual(x, back.Value.X, 1e-7, $"{family.Name} {pixel}");
                    Assert.AreEqual(y, back.Value.Y, 1e-7, $"{family.Name} {pixel}");
                    if (projector.Unproject(pixel) is { } production)
                    {
                        var p = VirtualAstrometryReference.Radians(production.AltitudeDegrees);
                        var z = VirtualAstrometryReference.Radians(production.AzimuthDegrees);
                        var vector = new VirtualAstrometryReference.Vector(Math.Cos(p) * Math.Sin(z), Math.Cos(p) * Math.Cos(z), Math.Sin(p));
                        Assert.IsLessThan(1e-7, VirtualAstrometryReference.SeparationDegrees(vector, ray), $"{family.Name} {pixel}");
                    }
                    tested++;
                }
            Assert.IsGreaterThan(300, tested, family.Name);
        }
    }

    [TestMethod]
    public void RectangularAperture_HasNoCircularInteriorAndRejectsOutsideFramePixels()
    {
        var rectilinear = VirtualAstrometryFixture.Families.Single(f => f.Name == "rectilinear");
        var rig = VirtualAstrometryFixture.Profiles(1, rectilinear)[0].Config.Rig;
        Assert.IsFalse(VirtualAstrometryReference.IsCircular(rig));
        Assert.IsTrue(VirtualAstrometryReference.IsApertureInterior(rig, new(1, 1), 12));
        Assert.IsNull(VirtualAstrometryReference.Unproject(rig, new(-.5, 608)));
        Assert.IsNull(VirtualAstrometryReference.Unproject(rig, new(1936.5, 608)));
        Assert.AreEqual(6 / .00586, VirtualAstrometryReference.NativeFocal(rig), 1e-9);

        var equidistant = VirtualAstrometryFixture.Profiles(1, VirtualAstrometryFixture.Families[0])[0].Config.Rig;
        Assert.IsTrue(VirtualAstrometryReference.IsApertureInterior(equidistant, new(968, 608), 12));
        Assert.IsFalse(VirtualAstrometryReference.IsApertureInterior(equidistant, new(968, 608 + 595.84 - 11), 12));
    }
}
