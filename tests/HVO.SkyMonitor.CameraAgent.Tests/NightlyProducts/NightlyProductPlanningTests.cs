using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
public sealed class NightlyProductPlanningTests
{
    [TestMethod]
    public void SegmentWindows_TileTheObservingDayFromLocalNoon()
    {
        var day = NightlyProductFixture.Calendar.Resolve(NightlyProductFixture.ObservingDate);

        var hourly = NightlyProductWindowPlanner.SegmentWindows(day, 60);
        var odd = NightlyProductWindowPlanner.SegmentWindows(day, 100);

        Assert.HasCount(24, hourly);
        Assert.AreEqual(NightlyProductFixture.DayStartUtc, hourly[0].StartUtc);
        Assert.AreEqual(NightlyProductFixture.DayEndUtc, hourly[^1].EndUtc);
        Assert.IsTrue(hourly.Zip(hourly.Skip(1)).All(static pair => pair.First.EndUtc == pair.Second.StartUtc));
        Assert.HasCount(15, odd);
        Assert.AreEqual(TimeSpan.FromMinutes(40), odd[^1].EndUtc - odd[^1].StartUtc, "The last window is clipped.");
        Assert.IsTrue(odd.All(static window => window.ObservingDate == NightlyProductFixture.ObservingDate));
    }

    [TestMethod]
    public void SegmentWindows_FollowDaylightSavingDayLengths()
    {
        var calendar = ObservingDayCalendar.Create("America/Denver");

        var spring = NightlyProductWindowPlanner.SegmentWindows(calendar.Resolve(new DateOnly(2026, 3, 7)), 60);
        var fall = NightlyProductWindowPlanner.SegmentWindows(calendar.Resolve(new DateOnly(2026, 10, 31)), 60);

        Assert.HasCount(23, spring);
        Assert.HasCount(25, fall);
    }

    [TestMethod]
    public void ResolveDays_ReturnsThePreviousAndCurrentDay_AndIsDueHonoursTheSettleInterval()
    {
        var days = NightlyProductWindowPlanner.ResolveDays(
            NightlyProductFixture.Calendar, NightlyProductFixture.DayEndUtc.AddMinutes(1));

        CollectionAssert.AreEqual(
            new[] { NightlyProductFixture.ObservingDate, NightlyProductFixture.ObservingDate.AddDays(1) },
            days.Select(static day => day.Date).ToArray());
        var end = NightlyProductFixture.DayEndUtc;
        Assert.IsFalse(NightlyProductWindowPlanner.IsDue(end, TimeSpan.FromMinutes(5), end.AddMinutes(4)));
        Assert.IsTrue(NightlyProductWindowPlanner.IsDue(end, TimeSpan.FromMinutes(5), end.AddMinutes(5)));
    }

    [TestMethod]
    public void Admission_UsesEachFramesOwnClockLocationAndRig()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var night = NightlyProductFixture.Frame(1, new DateTimeOffset(2026, 10, 2, 5, 0, 0, TimeSpan.Zero)).Candidate;
        var dusk = NightlyProductFixture.Frame(2, new DateTimeOffset(2026, 10, 2, 1, 50, 0, TimeSpan.Zero)).Candidate;
        var day = NightlyProductFixture.Frame(3, new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.Zero)).Candidate;
        var otherRig = NightlyProductFixture.Frame(4, night.ExposureStartedUtc, new string('C', 64)).Candidate;
        var unlocated = NightlyProductFixture.Frame(5, night.ExposureStartedUtc).Candidate;
        ObservatoryLocation? Locate(NightlyProductCandidate candidate) =>
            candidate == unlocated ? null : NightlyProductFixture.Observatory;

        var keogram = NightlyProductAdmission.Admit(
            [night, dusk, day, otherRig, unlocated], NightlyProductFixture.RigProfileSha256, 0, Locate, ephemeris);
        var starTrail = NightlyProductAdmission.Admit(
            [night, dusk, day], NightlyProductFixture.RigProfileSha256, -18, Locate, ephemeris,
            new Dictionary<string, int> { [NightlyProductContract.ExcludedUnsupportedSourceReasonCode] = 2 });

        CollectionAssert.AreEqual(new[] { night, dusk }, keogram.Admitted.ToArray(), "Civil dusk is below the horizon.");
        Assert.AreEqual(1, keogram.Exclusions[NightlyProductContract.ExcludedSolarAltitudeReasonCode]);
        Assert.AreEqual(1, keogram.Exclusions[NightlyProductContract.ExcludedRigReasonCode]);
        Assert.AreEqual(1, keogram.Exclusions[NightlyProductContract.ExcludedLocationReasonCode]);
        CollectionAssert.AreEqual(new[] { night }, starTrail.Admitted.ToArray(), "Twilight is excluded from star trails.");
        Assert.AreEqual(2, starTrail.Exclusions[NightlyProductContract.ExcludedSolarAltitudeReasonCode]);
        Assert.AreEqual(2, starTrail.Exclusions[NightlyProductContract.ExcludedUnsupportedSourceReasonCode]);
    }

    [TestMethod]
    public void Contract_TargetsRoundTripAndUnknownTargetsAreRejected()
    {
        foreach (var kind in Enum.GetValues<NightlyProductKind>())
        {
            Assert.IsTrue(NightlyProductContract.TryParseTarget(NightlyProductContract.TargetFor(kind), out var parsed));
            Assert.AreEqual(kind, parsed);
        }
        Assert.IsFalse(NightlyProductContract.TryParseTarget("time-lapse", out _));
        Assert.IsFalse(NightlyProductContract.TryParseTarget(null, out _));
    }

    [TestMethod]
    public void Options_DefaultsAreValidAndEnabledRequiresASourceNodeAndConsistentKeogramBounds()
    {
        Assert.IsTrue(IsValid(new NightlyProductOptions()));
        Assert.IsTrue(IsValid(NightlyProductFixture.Options()));
        Assert.IsFalse(IsValid(new NightlyProductOptions { Enabled = true }));
        Assert.IsFalse(IsValid(new NightlyProductOptions { Enabled = true, SourceNodeId = " preview" }));
        Assert.IsFalse(IsValid(new NightlyProductOptions { Enabled = true, SourceNodeId = "pre\nview" }));
        Assert.IsFalse(IsValid(new NightlyProductOptions { KeogramMaximumGapColumnCount = 100, KeogramMaximumColumnCount = 50 }));
        Assert.IsFalse(IsValid(new NightlyProductOptions { MaximumSegmentSources = 513 }));
        Assert.IsFalse(IsValid(new NightlyProductOptions { SegmentMinutes = 10 }));
        Assert.IsFalse(IsValid(new NightlyProductOptions { StarTrailMaximumSolarAltitudeDegrees = 1 }));
        Assert.IsFalse(IsValid(new CameraAgentHostOptions
        {
            RawIngressRoot = "/tmp/x",
            NightlyProducts = new NightlyProductOptions { Enabled = true }
        }));
    }

    private static bool IsValid(object options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
