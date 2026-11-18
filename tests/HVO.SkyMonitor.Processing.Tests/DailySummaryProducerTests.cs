using System.Diagnostics;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class DailySummaryProducerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('A', 64);
    private static DailySummaryCaptureV1 Source(int index, DateTimeOffset time) =>
        new(new Guid(index, 0, 0, new byte[8]), new Guid(index, 1, 0, new byte[8]), Hash, time);

    [TestMethod]
    public void CoverageCountsDistinctHalfOpenBinsIncludingShortLastBinAndNeverInventsEvents()
    {
        var sources = new[] { Source(1, Start), Source(2, Start.AddSeconds(1)), Source(3, Start.AddMinutes(5)),
            Source(4, Start.AddMinutes(10)) };
        var summary = DailySummaryProducer.Summarize(new(2026, 10, 1), Hash, Start, Start.AddMinutes(11), 4, sources, [],
            new Dictionary<string, int>());
        Assert.AreEqual(3, summary.SampledBins);
        Assert.AreEqual(3, summary.TotalBins);
        Assert.AreEqual(4, summary.CaptureCount);
        Assert.IsNull(summary.EventCount);
        Assert.IsFalse(summary.Partial);
        Assert.AreEqual(sources[2].ArtifactId, summary.RepresentativeArtifactId);
    }

    [TestMethod]
    public void GapsAndExclusionsArePartialAndOrderingIsDeterministic()
    {
        var first = Source(1, Start.AddMinutes(3));
        var second = Source(2, Start.AddMinutes(7));
        var summary = DailySummaryProducer.Summarize(new(2026, 10, 1), Hash, Start, Start.AddMinutes(20), 3,
            [second, first], [], new Dictionary<string, int> { ["unsupported"] = 1 });
        Assert.IsTrue(summary.Partial);
        Assert.AreEqual(2, summary.SampledBins);
        CollectionAssert.AreEqual(new[] { first, second }, summary.Captures.ToArray());
        Assert.AreEqual(second.ArtifactId, summary.RepresentativeArtifactId);
    }

    [TestMethod]
    public void RepresentativeTieBreaksByArtifactIdentityAndFactsChangeOutputIdentity()
    {
        var first = Source(1, Start.AddMinutes(4));
        var second = Source(2, Start.AddMinutes(6));
        var summary = DailySummaryProducer.Summarize(new(2026, 10, 1), Hash, Start, Start.AddMinutes(10), 2,
            [second, first], [], new Dictionary<string, int>());
        Assert.AreEqual(first.ArtifactId, summary.RepresentativeArtifactId);
        var selector = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "preview", Hash);
        var original = DailySummaryProducer.CreateIdentity(summary, selector, []);
        Assert.AreEqual(original.IdentitySha256, DailySummaryProducer.CreateIdentity(summary, selector, []).IdentitySha256);
        var changed = DailySummaryProducer.Summarize(new(2026, 10, 1), Hash, Start, Start.AddMinutes(10), 3,
            [first, second], [], new Dictionary<string, int> { ["excluded"] = 1 });
        Assert.AreNotEqual(original.IdentitySha256, DailySummaryProducer.CreateIdentity(changed, selector, []).IdentitySha256);
    }

    [TestMethod]
    public void ProducerCopiesTheRealRepresentativeAndBindsEverySourceAndOccurrence()
    {
        var source = Source(1, Start);
        var summary = DailySummaryProducer.Summarize(new(2026, 10, 1), Hash, Start, Start.AddMinutes(10), 1,
            [source], [], new Dictionary<string, int>());
        var selector = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "preview", Hash);
        var frame = new ProcessingArtifact(source.ArtifactId, FrameArtifactRole.Preview, "preview", Hash,
            "application/x-hvo-packed-image", new(2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, 255, 4),
            new byte[] { 1, 2, 3, 4 }, Start, TimeSpan.FromSeconds(1), new(Hash, "o", "c", "m", "s", "t", "p"),
            ObservationStartedUtc: Start);
        var product = DailySummaryProducer.Produce(summary, frame, selector, []);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, product.Payload.ToArray());
        CollectionAssert.AreEqual(new[] { source.ArtifactId }, product.SourceArtifactIds.ToArray());
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(product.Payload), product.ChecksumSha256);
        var repeated = DailySummaryProducer.Produce(summary, frame, selector, []);
        Assert.AreEqual(product.OutputIdentitySha256, repeated.OutputIdentitySha256);
        Assert.ThrowsExactly<ArgumentException>(() => DailySummaryProducer.Produce(summary with { SampledBins = 2 }, frame, selector, []));
        Assert.ThrowsExactly<ArgumentException>(() => DailySummaryProducer.Produce(summary, frame with { ArtifactId = Guid.NewGuid() }, selector, []));
    }

    [TestMethod]
    public void MaximumMetadataWorkloadRecordsBoundedResourceEvidence()
    {
        var sources = Enumerable.Range(1, 4096).Select(index => Source(index, Start.AddSeconds((index - 1) * 20))).ToArray();
        var times = new List<double>();
        var selector = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "preview", Hash);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var byteCount = 0;
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var watch = Stopwatch.StartNew();
            var summary = DailySummaryProducer.Summarize(new(2026, 10, 1), Hash, Start, Start.AddDays(1), 4096,
                sources, [], new Dictionary<string, int>());
            byteCount = JsonSerializer.SerializeToUtf8Bytes(summary).Length;
            _ = DailySummaryProducer.CreateIdentity(summary, selector, []);
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.AreEqual(4096, summary.CaptureCount);
        }
        times.Sort();
        Console.WriteLine(FormattableString.Invariant($"daily-summary metadata: sources=4096 iterations=10 bytes={byteCount} p50-ms={times[5]:F2} max-ms={times[^1]:F2} allocated-bytes={GC.GetAllocatedBytesForCurrentThread() - allocatedBefore} cpu-ms={(process.TotalProcessorTime - cpuBefore).TotalMilliseconds:F2} working-set={process.WorkingSet64}"));
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("end")]
    [DataRow("duplicate")]
    [DataRow("bound")]
    [DataRow("exclusions")]
    [DataRow("period")]
    public void InvalidSourcesAndBoundsFailExplicitly(string invalid)
    {
        DailySummaryCaptureV1[] sources = [Source(1, Start)];
        if (invalid == "empty") sources = [];
        if (invalid == "end") sources = [Source(1, Start.AddMinutes(10))];
        if (invalid == "duplicate") sources = [sources[0], sources[0]];
        if (invalid == "bound") sources = Enumerable.Range(1, 4097).Select(index => Source(index, Start)).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => DailySummaryProducer.Summarize(new(2026, 10, 1),
            invalid == "period" ? "not-a-hash" : Hash, Start, Start.AddMinutes(10), sources.Length,
            sources, [], invalid == "exclusions" ? new Dictionary<string, int> { ["bad"] = 1 } : new Dictionary<string, int>()));
    }
}
