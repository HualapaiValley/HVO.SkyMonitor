using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Tests.Components;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCombinedSpanProjectorTests
{
    [TestMethod]
    public void SpanUsesRecordedStartsInsteadOfExposureOrFrameCount()
    {
        var (lineage, product, captures) = CreateWindow();
        Assert.AreEqual(TimeSpan.FromSeconds(17), CameraAgentCombinedSpanProjector.Project(lineage, product, captures, captures[^1].CaptureId));
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("missing")]
    [DataRow("unrelated")]
    [DataRow("nonmonotonic")]
    [DataRow("no-timing")]
    [DataRow("wrong-endpoint")]
    [DataRow("wrong-order")]
    public void IncompleteOrUnboundWindowsNeverProduceASpan(string fault)
    {
        var (lineage, product, captures) = CreateWindow();
        if (fault == "truncated") product = product with { SourcesTruncated = true };
        if (fault == "missing") captures = [captures[1]];
        if (fault == "unrelated") captures[0] = captures[0] with { CaptureId = Guid.NewGuid() };
        if (fault == "nonmonotonic") captures[0] = captures[0] with { ExposureStartedUtc = captures[1].ExposureStartedUtc.AddSeconds(1), Detail = captures[0].Detail! with { Timing = captures[0].Detail!.Timing! with { ExposureStartedUtc = captures[1].ExposureStartedUtc.AddSeconds(1) } } };
        if (fault == "no-timing") captures[0] = captures[0] with { Detail = captures[0].Detail! with { Timing = null } };
        if (fault == "wrong-order") product = product with { Sources = product.Sources.Reverse().ToArray() };
        var endpoint = fault == "wrong-endpoint" ? Guid.NewGuid() : captures[^1].CaptureId;
        Assert.IsNull(CameraAgentCombinedSpanProjector.Project(lineage, product, captures, endpoint));
    }

    internal static (CameraAgentCombinedLineage, CameraAgentProductDetail, CameraAgentGalleryCapture[]) CreateWindow()
    {
        var endpoint = OperatorUiTestData.Capture();
        var start = endpoint.ExposureStartedUtc.AddSeconds(-17);
        var first = endpoint with
        {
            CaptureId = Guid.NewGuid(),
            CaptureSequence = endpoint.CaptureSequence - 1,
            ExposureStartedUtc = start,
            Detail = endpoint.Detail! with { Timing = endpoint.Detail!.Timing! with { ExposureStartedUtc = start } }
        };
        var sources = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var artifact = Guid.Parse("00000000-0000-0000-0000-000000000103");
        var lineage = new CameraAgentCombinedLineage(artifact, 2, sources, "rolling-mean", null);
        var product = new CameraAgentProduct(artifact, new string('A', 64), endpoint.CaptureId, endpoint.CaptureSequence,
            "private-device", "combine", FrameArtifactRole.Combined, "combined", OperatorUiTestData.Now, OperatorUiTestData.Now,
            "application/x-hvo-linear-frame", new string('B', 64), 1024, new CameraAgentGalleryRecipe("rolling-mean", "1", "1", "OPTIONS", "RECIPE"),
            [], null, null, null, "Available", null, TimeSpan.FromSeconds(2), 2, null, null, "Live", false);
        return (lineage, new CameraAgentProductDetail(product, endpoint.ExposureStartedUtc, ObservingDayCalendar.Utc.Resolve(endpoint.ExposureStartedUtc), null,
            [new(0, sources[0], FrameArtifactRole.Calibrated, first.CaptureId, first.CaptureSequence),
             new(1, sources[1], FrameArtifactRole.Calibrated, endpoint.CaptureId, endpoint.CaptureSequence)], false, null, []), [first, endpoint]);
    }
}
