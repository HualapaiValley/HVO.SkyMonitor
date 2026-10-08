using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Components.Operations;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class PipelineGraphLayoutTests
{
    [TestMethod]
    [DataRow(false, 0, DisplayName = "ordinal (named graphs)")]
    [DataRow(true, 1, DisplayName = "ignore case (profile pipeline)")]
    public void Arrange_MatchesDependenciesWithTheCallersIdComparer(bool ignoreCase, int expectedRank)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var placements = PipelineGraphLayout.Arrange([Node("preview"), Node("thumbnail", "PREVIEW")], comparer);

        Assert.AreEqual(0, placements[0].Rank);
        Assert.AreEqual(expectedRank, placements[1].Rank);
    }

    [TestMethod]
    public void Arrange_RanksByLongestChainAndStopsAtCycles()
    {
        var placements = PipelineGraphLayout.Arrange(
            [Node("a", PipelineGraphLayout.RawInput), Node("b", "a"), Node("c", "a", "b"), Node("x", "y"), Node("y", "x")],
            StringComparer.Ordinal);

        Assert.AreEqual(5, placements.Count);
        Assert.AreEqual("0,1,2", string.Join(",", placements.Take(3).Select(static placement => placement.Rank)));
        Assert.AreEqual("0,0,0", string.Join(",", placements.Take(3).Select(static placement => placement.Row)));
    }

    private static CaptureProcessingPlanNode Node(string id, params string[] dependencies)
        => new(id, id, Enabled: true, Required: false, Order: null, Options: null, dependencies, RecipeName: null, OutputRole: null, OutputVariant: null);
}
