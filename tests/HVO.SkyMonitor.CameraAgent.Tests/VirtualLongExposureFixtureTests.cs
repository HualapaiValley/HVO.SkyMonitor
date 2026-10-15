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
    private static readonly string[] VariantNames = ["none", "product-depth", "bright-background", "clouds-partial", "overcast"];
    private static readonly int[] ExposureSeconds = [1, 20, 60];
    private static readonly string[] EquidistantNativeViews = ["mono-native", "cfa-native"];
    private static readonly string[] MonoNativeView = ["mono-native"];

    [TestMethod]
    public void Variants_AreFrozenWithTheUnchangedBaselineFirst()
    {
        var variants = VirtualAstrometryFixture.Variants;
        CollectionAssert.AreEqual(VariantNames, variants.Select(v => v.Name).ToArray());
        Assert.AreEqual(VirtualAstrometryFixture.QualificationDepth, variants[0].Depth);
        Assert.AreEqual(VirtualLongExposureVariant.Accepted, variants[0].ExpectedOutcome);
        Assert.IsNull(variants[0].CloudScenario);
        Assert.AreEqual(new VirtualRenderDepth(6.5, 32768), variants[1].Depth);
        Assert.AreEqual(20d, variants[2].BackgroundElectronsPerSecond);
        Assert.IsNotNull(variants[3].CloudScenario);
        Assert.AreEqual(VirtualLongExposureVariant.Rejected, variants[4].ExpectedOutcome);
        Assert.IsTrue(variants.Where(v => v.Depth != VirtualAstrometryFixture.ProductDepth)
            .All(v => v.Depth == VirtualAstrometryFixture.QualificationDepth), "Only product depth renders deeper than the qualification depth.");
        CollectionAssert.AreEqual(ExposureSeconds, VirtualAstrometryFixture.ExposureSecondsValues.ToArray());
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
            CollectionAssert.AreEqual(EquidistantNativeViews, profiles.Select(p => p.Name).ToArray(), variant.Name);
            CollectionAssert.AreEqual(MonoNativeView, VirtualAstrometryFixture.Profiles(1, rectilinear, variant).Select(p => p.Name).ToArray());
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
    public void BoundarySlots_DerivesTheSlotCountOnlyWhenTheLongerExposureMustBeRefused()
    {
        static double Motion(int seconds, double speed)
        {
            var exposure = TimeSpan.FromSeconds(seconds);
            var slots = (int)Math.Ceiling(exposure.TotalSeconds * speed / .15);
            return speed * ((exposure.Ticks + slots - 1) / slots) / TimeSpan.TicksPerSecond;
        }
        var derived = VirtualLongExposureCapacityTests.BoundarySlots(TimeSpan.FromSeconds(56), Motion(56, .168825), TimeSpan.FromSeconds(57), .15, 64);
        Assert.IsNotNull(derived);
        Assert.AreEqual(64, derived.Value.Slots);
        Assert.AreEqual(.168825, derived.Value.SpeedBoundPixelsPerSecond, 1e-12);
        // At 0.0405 px/s (16 slots of 0.142 px) even 64 slots would cover 57 s, so no slot count explains its refusal.
        Assert.IsNull(VirtualLongExposureCapacityTests.BoundarySlots(TimeSpan.FromSeconds(56), Motion(56, .0405), TimeSpan.FromSeconds(57), .15, 64));
        // The motion bound alone admits every slot count below 1 / (1 - motion / step); only the refusal selects one.
        Assert.IsNull(VirtualLongExposureCapacityTests.BoundarySlots(TimeSpan.FromSeconds(56), Motion(56, .168825), TimeSpan.FromSeconds(56), .15, 64));
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
        // A trail whose box touches the best distance exactly is still evaluated, so an exact tie keeps MinBy order. Integer
        // coordinates from fixed strides make exact ties common.
        var trails = Enumerable.Range(0, 64).Select(i =>
        {
            var start = new PixelPoint(i * 53 % 200, i * 97 % 200);
            return Trail(Line(start, new(start.X + i * 29 % 41 - 20, start.Y + i * 31 % 41 - 20)));
        }).ToArray();
        for (var k = 0; k < 256; k++)
        {
            var pixel = new PixelPoint(k * 71 % 221 - 10, k * 113 % 221 - 10);
            Assert.AreSame(trails.MinBy(trail => trail.DistanceTo(pixel)), VirtualAstrometryReference.Nearest(trails, pixel), $"{pixel}");
        }
    }

    [TestMethod]
    public void Project_UnclippedKeepsTheClippedArithmeticAndExtendsPastTheReadout()
    {
        var equidistant = VirtualAstrometryFixture.Families.Single(f => f.Name == "equidistant");
        var profiles = VirtualAstrometryFixture.Profiles(1, equidistant, VirtualAstrometryFixture.Variants[0]);
        var native = profiles.Single(p => p.Name == "mono-native").Config.Rig;
        var roiBin = profiles.Single(p => p.Name == "mono-roi-bin2").Config.Rig;
        // Inside the image circle but above the 240,96 ROI origin: the binned ROI readout clips it.
        var outside = VirtualAstrometryReference.Unproject(native, new(968, 50))!.Value;
        Assert.IsNull(VirtualAstrometryReference.Project(roiBin, outside));
        var unclipped = VirtualAstrometryReference.Project(roiBin, outside, clip: false)!.Value;
        Assert.AreEqual((968 - 240) / 2d, unclipped.X, 1e-6);
        Assert.AreEqual((50 - 96) / 2d, unclipped.Y, 1e-6);
        var inside = VirtualAstrometryReference.Unproject(native, new(900, 600))!.Value;
        Assert.AreEqual(VirtualAstrometryReference.Project(roiBin, inside), VirtualAstrometryReference.Project(roiBin, inside, clip: false));
        Assert.AreEqual(VirtualAstrometryReference.Project(native, inside), VirtualAstrometryReference.Project(native, inside, clip: true));
    }

    [TestMethod]
    public void InvariantFailures_AcceptOnlyGeometricReclassificationAndNameEveryViolation()
    {
        // A 100 px frame with the #1126 border margin, widened as the invariant widens it.
        static bool Interior(PixelPoint p, double widen) => p.X > 6 + widen && p.Y > 6 + widen && p.X < 94 - widen && p.Y < 94 - widen;
        (string, bool, bool)[] both = [("v1", true, true), ("v2", true, true)];
        VirtualMeasuredStarQualificationTests.ReclassificationStar Candidate(string id, double x, double y, double reach, bool legacy, bool trail,
            bool belowHorizon = false, bool outsideDomain = false, (string, bool, bool)[]? recovered = null) =>
            new(id, new(x, y), reach, belowHorizon, outsideDomain, legacy, trail, recovered ?? both);

        VirtualMeasuredStarQualificationTests.ReclassificationStar[] explained =
        [
            Candidate("whole", 30, 30, 2, legacy: true, trail: true),
            Candidate("border", 8, 60, 2, legacy: true, trail: false),
            Candidate("pair-a", 60, 30, 1, legacy: true, trail: false),
            Candidate("pair-b", 60, 44, 1, legacy: true, trail: false),
            Candidate("horizon", 80, 80, 0.5, legacy: true, trail: false, belowHorizon: true),
            Candidate("domain", 30, 80, 0.5, legacy: true, trail: false, outsideDomain: true),
            Candidate("never", 50, 3, 0, legacy: false, trail: false)
        ];
        Assert.IsEmpty(VirtualMeasuredStarQualificationTests.InvariantFailures("c", explained, Interior),
            "Border within the reach (inclusive), mids within 12 + both reaches, the horizon and the domain edge all explain a loss.");

        VirtualMeasuredStarQualificationTests.ReclassificationStar[] defects =
        [
            Candidate("widened", 70, 20, 2, legacy: true, trail: true, recovered: [("v1", true, true), ("v2", true, false)]),
            Candidate("gained", 50, 3, 0, legacy: false, trail: true),
            Candidate("border", 8.5, 60, 2, legacy: true, trail: false),
            // Isolation applied at 24 px instead of 12: 20 px apart with 1 px reaches is no geometric cause.
            Candidate("far-a", 40, 70, 1, legacy: true, trail: false),
            Candidate("far-b", 60, 70, 1, legacy: true, trail: false)
        ];
        string[] expected =
        [
            "c: invariant 2: v2 recovers widened at its mid-exposure point but not on its trail",
            "c: invariant 1: gained is trail-eligible but not legacy-eligible",
            "c: invariant 3: border lost eligibility with no geometric cause within its 2.000 px reach",
            "c: invariant 3: far-a lost eligibility with no geometric cause within its 1.000 px reach",
            "c: invariant 3: far-b lost eligibility with no geometric cause within its 1.000 px reach"
        ];
        CollectionAssert.AreEqual(expected, VirtualMeasuredStarQualificationTests.InvariantFailures("c", defects, Interior));
    }

    [TestMethod]
    public void ScoreFailures_RejectCountsThatDisagreeWithTheCheckedPopulation()
    {
        // Ten stars eligible under both rules, v1 and v2 recovering the first eight on both, plus two legacy-only stars of
        // which both measurers recover one at its point: trail 10 / 8 / 0.8 with 2 missed, legacy 12 / 9 / 0.75 with 3 missed.
        VirtualMeasuredStarQualificationTests.ReclassificationStar[] stars =
        [
            .. Enumerable.Range(0, 10).Select(i => new VirtualMeasuredStarQualificationTests.ReclassificationStar(
                $"s{i}", new(20 + 6 * i, 50), 1, false, false, true, true, [("v1", i < 8, i < 8), ("v2", i < 8, i < 8)])),
            .. Enumerable.Range(10, 2).Select(i => new VirtualMeasuredStarQualificationTests.ReclassificationStar(
                $"s{i}", new(20 + 6 * i, 50), 1, false, false, true, false, [("v1", i == 10, false), ("v2", i == 10, false)]))
        ];
        static VirtualMeasuredStarQualificationTests.MeasuredStarScore Score(int eligible, int recovered, double? recall = null) =>
            new(eligible, recovered, recall ?? recovered / (double)eligible, 8, 0, 0.1, 0.2);
        static Dictionary<string, int> Missed(int count) => new(StringComparer.Ordinal) { ["no-candidate"] = count };
        List<string> Failures(int eligibleTruthStars, VirtualMeasuredStarQualificationTests.MeasuredStarScore trail,
            VirtualMeasuredStarQualificationTests.MeasuredStarScore legacy, int missed, int legacyEligibleTruthStars = 12, int legacyMissed = 3) =>
            VirtualMeasuredStarQualificationTests.ScoreFailures("c", stars, (eligibleTruthStars, legacyEligibleTruthStars),
                [("v1", trail, Score(12, 9)), ("v2", trail, legacy)], ("v2", Missed(missed), Missed(legacyMissed)));

        Assert.IsEmpty(Failures(10, Score(10, 8), Score(12, 9), 2), "Counts that match the population pass.");

        // r1 N1 (b): eligibleStars and recovered halved with recall kept.
        string[] halved =
        [
            "c: score: v1 eligibleStars is 5, not 10",
            "c: score: v1 recovered is 4, not 8",
            "c: score: v2 eligibleStars is 5, not 10",
            "c: score: v2 recovered is 4, not 8"
        ];
        CollectionAssert.AreEqual(halved, Failures(10, Score(5, 4), Score(12, 9), 2));

        // (b'): every trail count rewritten self-consistently, which the continuity runner cannot see.
        string[] rewritten =
        [
            "c: score: eligibleTruthStars is 5, not 10",
            "c: score: v1 eligibleStars is 5, not 10",
            "c: score: v1 recovered is 4, not 8",
            "c: score: v2 eligibleStars is 5, not 10",
            "c: score: v2 recovered is 4, not 8",
            "c: score: v2 missed reasons total is 1, not 2"
        ];
        CollectionAssert.AreEqual(rewritten, Failures(5, Score(5, 4), Score(12, 9), 1));

        // A recall that disagrees with its own counts, and a legacy score taken from the wrong rule.
        string[] misstated =
        [
            "c: score: legacy eligibleTruthStars is 10, not 12",
            "c: score: v1 recall is 0.75, not 0.8",
            "c: score: v2 recall is 0.75, not 0.8",
            "c: score: legacy v2 recovered is 6, not 9",
            "c: score: legacy v2 recall is 0.5, not 0.75",
            "c: score: legacy v2 missed reasons total is 2, not 3"
        ];
        CollectionAssert.AreEqual(misstated, Failures(10, Score(10, 8, 0.75), Score(12, 6, 0.5), 2, legacyEligibleTruthStars: 10, legacyMissed: 2));
    }

    private static VirtualAstrometryReference.TruthTrail Trail(PixelPoint?[] samples) => new(Star, samples);

    private static PixelPoint?[] Line(PixelPoint start, PixelPoint end) =>
        [.. Enumerable.Range(0, VirtualAstrometryReference.TrailSamples).Select(k => (PixelPoint?)new PixelPoint(
            start.X + (end.X - start.X) * k / (VirtualAstrometryReference.TrailSamples - 1),
            start.Y + (end.Y - start.Y) * k / (VirtualAstrometryReference.TrailSamples - 1)))];
}
