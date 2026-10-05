using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest discovers public test classes.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Each test owns its expected values.")]
public sealed class TimeLapseTimelineTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(180)]
    [DataRow(300)]
    public void VeryShortSunrisePartitionsRetainPositiveContiguousMediaTime(int compression)
    {
        foreach (var seconds in new[] { .0000001, .0183828, .5323656, 1.9411429, 2.5524336 })
        {
            var end = Origin.AddSeconds(seconds);
            var expected = Math.Max(1L, (long)Math.Round(seconds / compression * 1_000_000, MidpointRounding.AwayFromZero));
            foreach (var sources in new[] { Array.Empty<TimeLapseSource>(), new[] { Source(0, .001) } })
            {
                var result = TimeLapseTimelinePlanner.Create(Origin, end, sources, new(compression));
                Assert.AreEqual(expected, result.DurationTicks);
                Assert.HasCount(1, result.Intervals);
                Assert.AreEqual(expected, result.Intervals[0].EndTick);
                Assert.AreEqual(sources.Length > 0, result.HasSources);
            }
        }
    }

    [TestMethod]
    public void HoldsFollowCaptureClockRegardlessOfExposureOrIntegration()
    {
        var sources = new[] { Source(0, 20), Source(20, .001), Source(25, .005), Source(30, 20) };
        var result = TimeLapseTimelinePlanner.Create(Origin, Origin.AddSeconds(35), sources);
        var expected = new[] { 20d, 5d, 5d, 5d };
        foreach (var pair in result.Intervals.Zip(expected))
            Assert.AreEqual(pair.Second, pair.First.DurationTicks * 180d / TimeLapseTimeline.TicksPerSecond, .0002);
        Assert.IsFalse(result.HasGaps);
        Assert.AreEqual(4, result.Sources.Length);
        Assert.AreEqual(35d / 180, result.DurationTicks / (double)TimeLapseTimeline.TicksPerSecond, 0.000001);
    }

    [TestMethod]
    [DataRow(180, 20d)]
    [DataRow(300, 12d)]
    public void GenuineSourceHourRetainsEveryNightCapture(int compression, double duration)
    {
        var result = TimeLapseTimelinePlanner.Create(Origin, Origin.AddHours(1),
            Enumerable.Range(0, 180).Reverse().Select(index => Source(index * 20, 20)), new(compression));
        Assert.AreEqual(180, result.Intervals.Length);
        Assert.AreEqual(0, result.SampledOutOrdinals.Length);
        Assert.AreEqual(duration, result.DurationTicks / (double)TimeLapseTimeline.TicksPerSecond);
        Assert.AreEqual(Origin, result.Sources[0].ObservationUtc);
        Assert.AreEqual(result.DurationTicks, result.Intervals.Sum(static value => value.DurationTicks));
    }

    [TestMethod]
    public void SparseWindowShowsLeadingInteriorAndTrailingGaps()
    {
        var result = TimeLapseTimelinePlanner.Create(Origin, Origin.AddHours(1), [Source(20, 20), Source(900, 20)]);
        CollectionAssert.AreEqual(new int?[] { null, 0, null, 1, null },
            result.Intervals.Select(static interval => interval.SourceOrdinal).ToArray());
        var expected = new[] { 20d, 60d, 820d, 5d, 2695d };
        foreach (var pair in result.Intervals.Zip(expected))
            Assert.AreEqual(pair.Second, pair.First.DurationTicks * 180d / TimeLapseTimeline.TicksPerSecond, .0002);
        Assert.AreEqual(result.DurationTicks, result.Intervals.Sum(static value => value.DurationTicks));
    }

    [TestMethod]
    public void MillisecondDayExposuresAtFiveSecondCadenceRetainWholeDayWithoutDrift()
    {
        var result = TimeLapseTimelinePlanner.Create(Origin, Origin.AddDays(1),
            Enumerable.Range(0, 17280).Select(index => Source(index * 5, .001)), new(300));
        Assert.AreEqual(17280, result.Intervals.Length);
        Assert.AreEqual(0, result.SampledOutOrdinals.Length);
        Assert.AreEqual(288 * (long)TimeLapseTimeline.TicksPerSecond, result.DurationTicks);
        Assert.AreEqual(result.DurationTicks, result.Intervals[^1].EndTick);
        Assert.IsFalse(result.HasGaps);
    }

    [TestMethod]
    public void DenseSamplingRetainsOmittedLineageAndRoundsCumulativeBoundaries()
    {
        var result = TimeLapseTimelinePlanner.Create(Origin, Origin.AddSeconds(120.123),
            Enumerable.Range(0, 10000).Select(index => Source(index * .012, .001)), new(180));
        Assert.AreEqual(10000, result.Sources.Length);
        Assert.IsGreaterThan(9900, result.SampledOutOrdinals.Length);
        Assert.AreEqual(result.DurationTicks, result.Intervals.Sum(static value => value.DurationTicks));
        Assert.IsTrue(result.Intervals.Zip(result.Intervals.Skip(1), (a, b) => a.EndTick == b.StartTick).All(static value => value));
        Assert.AreEqual(120.123 / 180, result.DurationTicks / (double)TimeLapseTimeline.TicksPerSecond, 0.0000005);
    }

    [TestMethod]
    public void QualityRejectedCapturesRemainOnTheClockAsVisibleGaps()
    {
        var input = new[] { Source(0, .001), Source(5, .001) with { ExclusionReasonCode = "quality.saturated" },
            Source(10, .001) with { ExclusionReasonCode = "quality.saturated" }, Source(15, .001) };
        var timeline = TimeLapseTimelinePlanner.Create(Origin, Origin.AddSeconds(20), input);
        Assert.AreEqual(4, timeline.Sources.Length);
        CollectionAssert.AreEqual(new int?[] { 0, null, 3 }, timeline.Intervals.Select(interval => interval.SourceOrdinal).ToArray());
        Assert.AreEqual(10d, timeline.Intervals[1].DurationTicks * 180d / TimeLapseTimeline.TicksPerSecond, .0002);
        var empty = TimeLapseTimelinePlanner.Create(Origin, Origin.AddSeconds(20),
            input.Select(source => source with { ExclusionReasonCode = "quality.saturated" }));
        Assert.IsFalse(empty.HasSources);
        Assert.HasCount(1, empty.Intervals);
    }

    [TestMethod]
    public void EmptyWindowHasNoSyntheticSourceAndInvalidOrOverboundInputFails()
    {
        var empty = TimeLapseTimelinePlanner.Create(Origin, Origin.AddHours(1), []);
        Assert.IsFalse(empty.HasSources);
        Assert.IsNull(empty.Intervals.Single().SourceOrdinal);
        Assert.ThrowsExactly<ArgumentException>(() => TimeLapseTimelinePlanner.Create(Origin, Origin.AddHours(1), [Source(-1, 1)]));
        Assert.ThrowsExactly<ArgumentException>(() => TimeLapseTimelinePlanner.Create(Origin, Origin.AddDays(1),
            Enumerable.Range(0, TimeLapseTimeline.MaximumSources + 1).Select(index => Source(index, .001))));
        var duplicate = Source(0, 1);
        Assert.ThrowsExactly<ArgumentException>(() => TimeLapseTimelinePlanner.Create(Origin, Origin.AddHours(1), [duplicate, duplicate]));
    }

    private static TimeLapseSource Source(double seconds, double exposure) =>
        new(Guid.NewGuid(), new string('A', 64), Origin.AddSeconds(seconds), TimeSpan.FromSeconds(exposure));
}
