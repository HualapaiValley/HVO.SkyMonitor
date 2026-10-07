using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Pins the frozen issue #1168 matrix (variants, seeds, refusals) and the truth-trail geometry the long-exposure scorers
/// rely on, so a qualification outcome cannot come from a drifted fixture or a scorer defect.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class VirtualLongExposureFixtureTests
{
    private static readonly CelestialCatalogObject Star = new("star", "star", 0, 0, 1);

    [TestMethod]
    public void Variants_AreFrozenWithTheUnchangedBaselineFirst()
    {
        var variants = VirtualAstrometryFixture.Variants;
        CollectionAssert.AreEqual(new[] { "none", "product-depth", "bright-background", "clouds-partial", "overcast" },
            variants.Select(v => v.Name).ToArray());
        Assert.AreEqual(VirtualAstrometryFixture.QualificationDepth, variants[0].Depth);
        Assert.AreEqual(VirtualLongExposureVariant.Accepted, variants[0].ExpectedOutcome);
        Assert.IsNull(variants[0].CloudScenario);
        Assert.AreEqual(new VirtualRenderDepth(6.5, 32768), variants[1].Depth);
        Assert.AreEqual(20d, variants[2].BackgroundElectronsPerSecond);
        Assert.IsNotNull(variants[3].CloudScenario);
        Assert.AreEqual(VirtualLongExposureVariant.Rejected, variants[4].ExpectedOutcome);
        Assert.IsTrue(variants.Where(v => v.Depth != VirtualAstrometryFixture.ProductDepth)
            .All(v => v.Depth == VirtualAstrometryFixture.QualificationDepth), "Only product depth renders deeper than the qualification depth.");
        CollectionAssert.AreEqual(new[] { 1, 20, 60 }, VirtualAstrometryFixture.ExposureSecondsValues.ToArray());
    }

    [TestMethod]
    public void Seed_KeepsTheBaselineSeedAndPairsEveryOtherVariantIntoThe1168Family()
    {
        var variants = VirtualAstrometryFixture.Variants;
        Assert.AreEqual(110205, VirtualAstrometryFixture.Seed(110205, variants[0]));
        foreach (var variant in variants.Skip(1))
        {
            Assert.AreEqual(116820, VirtualAstrometryFixture.Seed(110220, variant), variant.Name);
            Assert.AreEqual(116809, VirtualAstrometryFixture.Seed(110209, variant), variant.Name);
        }
    }

    [TestMethod]
    public void Profiles_RenderOnlyTheNativeViewsForEveryNonBaselineVariant()
    {
        var equidistant = VirtualAstrometryFixture.Families.Single(f => f.Name == "equidistant");
        var rectilinear = VirtualAstrometryFixture.Families.Single(f => f.Name == "rectilinear");
        foreach (var variant in VirtualAstrometryFixture.Variants.Skip(1))
        {
            var profiles = VirtualAstrometryFixture.Profiles(1, equidistant, variant);
            CollectionAssert.AreEqual(new[] { "mono-native", "cfa-native" }, profiles.Select(p => p.Name).ToArray(), variant.Name);
            CollectionAssert.AreEqual(new[] { "mono-native" }, VirtualAstrometryFixture.Profiles(1, rectilinear, variant).Select(p => p.Name).ToArray());
        }
        Assert.HasCount(7, VirtualAstrometryFixture.Profiles(1, equidistant, VirtualAstrometryFixture.Variants[0]));
    }

    [TestMethod]
    public void DeclaredRefusal_NamesOnlyTheRectilinearFullFrameSixtySecondAndMonoCloudCells()
    {
        var none = VirtualAstrometryFixture.Variants[0];
        foreach (var family in VirtualAstrometryFixture.Families.Where(f => f.Name != "rectilinear"))
            foreach (var profile in VirtualAstrometryFixture.Profiles(1, family, none))
                foreach (var seconds in VirtualAstrometryFixture.ExposureSecondsValues)
                    Assert.IsNull(VirtualAstrometryFixture.DeclaredRefusal(family, profile, seconds, none), $"{family.Name} {profile.Name} {seconds}");
        var rectilinear = VirtualAstrometryFixture.Families.Single(f => f.Name == "rectilinear");
        foreach (var profile in VirtualAstrometryFixture.Profiles(1, rectilinear, none))
        {
            Assert.IsNull(VirtualAstrometryFixture.DeclaredRefusal(rectilinear, profile, 20, none), profile.Name);
            var refusal = VirtualAstrometryFixture.DeclaredRefusal(rectilinear, profile, 60, none);
            if (profile.Name is "mono-roi" or "mono-roi-bin2")
            {
                Assert.IsNull(refusal, profile.Name);
                continue;
            }
            Assert.AreEqual(new VirtualRenderRefusal("capture", typeof(InvalidOperationException).FullName!,
                "stellar-exposure-temporal-budget-exceeded"), refusal, profile.Name);
        }
        var equidistant = VirtualAstrometryFixture.Families[0];
        foreach (var variant in VirtualAstrometryFixture.Variants.Where(v => v.CloudScenario is not null))
            foreach (var profile in VirtualAstrometryFixture.Profiles(1, equidistant, variant))
            {
                var refusal = VirtualAstrometryFixture.DeclaredRefusal(equidistant, profile, 20, variant);
                if (profile.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16)
                    Assert.IsNull(refusal, $"{variant.Name} {profile.Name}");
                else
                    Assert.AreEqual(new VirtualRenderRefusal("initialize", typeof(NotSupportedException).FullName!,
                        VirtualRenderRefusal.NativeReadoutClouds), refusal, $"{variant.Name} {profile.Name}");
            }
    }

    [TestMethod]
    public void RenderRefusal_MatchesOnlyTheExactTypeAndMessage()
    {
        var refusal = new VirtualRenderRefusal("capture", typeof(InvalidOperationException).FullName!, "stellar-exposure-temporal-budget-exceeded");
        Assert.IsTrue(refusal.Matches(new InvalidOperationException("stellar-exposure-temporal-budget-exceeded")));
        Assert.IsFalse(refusal.Matches(new InvalidOperationException("stellar-exposure-temporal-budget-exceeded.")));
        Assert.IsFalse(refusal.Matches(new InvalidOperationException("Stellar-exposure-temporal-budget-exceeded")));
        Assert.IsFalse(refusal.Matches(new ObjectDisposedException("stellar-exposure-temporal-budget-exceeded")), "A derived type is a different exception.");
        Assert.IsFalse(refusal.Matches(new NotSupportedException("stellar-exposure-temporal-budget-exceeded")));
    }

    [TestMethod]
    public void TruthTrail_MeasuresOnlyVisibleSegmentsAndFoldsTheAngleToTheMeasurerRange()
    {
        var whole = Trail(Line(new(10, 10), new(18, 10)));
        Assert.IsFalse(whole.Truncated); Assert.IsTrue(whole.Visible);
        Assert.AreEqual(8, whole.LengthPixels, 1e-12);
        Assert.AreEqual(0, whole.AngleDegrees!.Value, 1e-12);
        Assert.AreEqual(0, whole.MidpointBiasPixels!.Value, 1e-12);
        Assert.AreEqual(0, Trail(Line(new(18, 10), new(10, 10))).AngleDegrees!.Value, 1e-12, "A reversed trail is the same axis.");
        Assert.AreEqual(-90, Trail(Line(new(0, 0), new(0, 8))).AngleDegrees!.Value, 1e-12);
        Assert.AreEqual(-90, Trail(Line(new(0, 8), new(0, 0))).AngleDegrees!.Value, 1e-12);
        Assert.AreEqual(45, Trail(Line(new(8, 8), new(0, 0))).AngleDegrees!.Value, 1e-12);
        Assert.AreEqual(-45, Trail(Line(new(0, 8), new(8, 0))).AngleDegrees!.Value, 1e-12);

        var samples = Line(new(0, 0), new(8, 0)); samples[0] = null; samples[4] = null;
        var truncated = Trail(samples);
        Assert.IsTrue(truncated.Truncated); Assert.IsTrue(truncated.Visible);
        Assert.IsNull(truncated.Mid); Assert.IsNull(truncated.MidpointBiasPixels);
        Assert.AreEqual(5, truncated.LengthPixels, 1e-12, "Segments touching a missing sample are not part of the visible trail.");
        Assert.AreEqual(0, truncated.AngleDegrees!.Value, 1e-12);

        var invisible = Trail(new PixelPoint?[9]);
        Assert.IsFalse(invisible.Visible); Assert.IsNull(invisible.AngleDegrees);
        Assert.AreEqual(double.PositiveInfinity, invisible.DistanceTo(new(0, 0)));
    }

    [TestMethod]
    public void TruthTrail_DistanceIsToThePolylineAndBoundsGapIsALowerBound()
    {
        var trail = Trail(Line(new(0, 0), new(16, 0)));
        Assert.AreEqual(3, trail.DistanceTo(new(7, 3)), 1e-12, "Between samples the distance is to the segment, not the nearest sample.");
        Assert.AreEqual(5, trail.DistanceTo(new(-3, 4)), 1e-12);
        var samples = Line(new(0, 0), new(16, 0)); samples[3] = null;
        Assert.AreEqual(Math.Sqrt(1 + 9), Trail(samples).DistanceTo(new(7, 3)), 1e-12, "A missing sample removes both of its segments.");

        var other = Trail(Line(new(20, 4), new(28, 4)));
        Assert.AreEqual(Math.Sqrt(16 + 16), trail.BoundsGapTo(other), 1e-12);
        Assert.AreEqual(trail.BoundsGapTo(other), other.BoundsGapTo(trail), 1e-12);
        Assert.AreEqual(0, trail.BoundsGapTo(Trail(Line(new(4, -2), new(4, 2)))), 1e-12);
        Assert.AreEqual(double.PositiveInfinity, trail.BoundsGapTo(Trail(new PixelPoint?[9])));
    }

    [TestMethod]
    public void Nearest_ReturnsTheFirstMinimalTrailAndPrunesWithoutChangingTheResult()
    {
        var first = Trail(Line(new(0, 0), new(8, 0)));
        var tie = Trail(Line(new(0, 4), new(8, 4)));
        var far = Trail(Line(new(100, 100), new(108, 100)));
        var invisible = Trail(new PixelPoint?[9]);
        Assert.IsNull(VirtualAstrometryReference.Nearest([], new(0, 0)));
        Assert.IsNull(VirtualAstrometryReference.Nearest([invisible], new(0, 0)));
        Assert.AreSame(first, VirtualAstrometryReference.Nearest([invisible, first, tie, far], new(4, 2)));
        Assert.AreSame(tie, VirtualAstrometryReference.Nearest([tie, first], new(4, 2)));
        Assert.AreSame(tie, VirtualAstrometryReference.Nearest([far, first, tie], new(4, 3)));
        // A trail whose box touches the best distance exactly is still evaluated, so an exact tie keeps MinBy order.
        var random = new Random(1168);
        var trails = Enumerable.Range(0, 64).Select(_ =>
        {
            var start = new PixelPoint(random.Next(0, 200), random.Next(0, 200));
            return Trail(Line(start, new(start.X + random.Next(-20, 21), start.Y + random.Next(-20, 21))));
        }).ToArray();
        for (var k = 0; k < 256; k++)
        {
            var pixel = new PixelPoint(random.Next(-10, 211), random.Next(-10, 211));
            Assert.AreSame(trails.MinBy(trail => trail.DistanceTo(pixel)), VirtualAstrometryReference.Nearest(trails, pixel), $"{pixel}");
        }
    }

    private static VirtualAstrometryReference.TruthTrail Trail(PixelPoint?[] samples) => new(Star, samples);

    private static PixelPoint?[] Line(PixelPoint start, PixelPoint end) =>
        [.. Enumerable.Range(0, VirtualAstrometryReference.TrailSamples).Select(k => (PixelPoint?)new PixelPoint(
            start.X + (end.X - start.X) * k / (VirtualAstrometryReference.TrailSamples - 1),
            start.Y + (end.Y - start.Y) * k / (VirtualAstrometryReference.TrailSamples - 1)))];
}
