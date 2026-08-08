using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;

namespace HVO.SkyMonitor.Tests.CameraAgent.Deployment;

[TestClass]
[TestCategory("Unit")]
public sealed class CapturePipelineTraceStoreTests
{
    [TestMethod]
    public void Record_BoundsRetentionAndSupportsExactWorkloadCorrelation()
    {
        var store = new CapturePipelineTraceStore();
        var artifacts = Enumerable.Range(1, CapturePipelineTraceStore.Capacity + 5)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        for (var index = 0; index < artifacts.Length; index++)
        {
            store.Record(index + 1, Guid.NewGuid(), artifacts[index], Context(index + 1));
        }

        store.Snapshot().Should().HaveCount(CapturePipelineTraceStore.Capacity);
        store.Find(1, artifacts[0]).Should().BeNull();
        var latestArtifact = artifacts[^1];
        store.Find(artifacts.Length, latestArtifact).Should().NotBeNull()
            .And.Match<CapturePipelineTrace>(trace => trace.ArtifactId == latestArtifact && trace.CaptureSequence == artifacts.Length);
        store.Find(artifacts.Length - 1, artifacts[^1]).Should().BeNull();
    }

    [TestMethod]
    public void Record_ReplacesDuplicateIdentityWithoutIncreasingCardinality()
    {
        var store = new CapturePipelineTraceStore();
        var artifactId = Guid.NewGuid();
        store.Record(4, Guid.NewGuid(), artifactId, Context(4));
        store.Record(4, Guid.NewGuid(), artifactId, Context(5));

        store.Snapshot().Should().ContainSingle();
        store.Find(4, artifactId)!.TraceId.Should().Be(Context(5).TraceId.ToHexString());
    }

    private static ActivityContext Context(int value)
        => new(
            ActivityTraceId.CreateFromString(value.ToString("x32", CultureInfo.InvariantCulture).AsSpan()),
            ActivitySpanId.CreateFromString(value.ToString("x16", CultureInfo.InvariantCulture).AsSpan()),
            ActivityTraceFlags.Recorded);
}
