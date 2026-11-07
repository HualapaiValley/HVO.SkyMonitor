using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class QuarantinedReadinessTests
{
    [TestMethod]
    [DataRow("required-pressure", 0, 1, 0)]
    [DataRow("required-quarantine", 1, 1, 1)]
    [DataRow("required-quarantine", 2, 0, 1)]
    [DataRow("required-quarantine", 1, 0, 0)]
    [DataRow("lane-worker-failed", 1, 0, 1)]
    [DataRow("lane-claim-failed", 1, 0, 1)]
    [DataRow("lane-state-unavailable", 1, 0, 1)]
    [DataRow("ephemeral-worker-failed", 1, 0, 1)]
    [DataRow("unknown-failure", 1, 0, 1)]
    public void TryAccept_UnrelatedLaneCauseWithAQuarantine_IsRefused(string reason, int requiredQuarantined, int pressure, int processingQuarantined)
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        var lane = CauseData(report, "capture-lanes");
        lane["LaneCount"] = 3;
        lane["QuarantineCount"] = 2;
        lane["Reason"] = reason;
        lane["RequiredQuarantinedLaneCount"] = requiredQuarantined;
        lane["RequiredPressureLaneCount"] = pressure;
        lane["RequiredProcessingQuarantineCount"] = processingQuarantined;

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    [DataRow("Reason")]
    [DataRow("RequiredQuarantinedLaneCount")]
    [DataRow("RequiredPressureLaneCount")]
    [DataRow("RequiredProcessingQuarantineCount")]
    [DataRow("OperationalFailureCount")]
    public void TryAccept_MissingLaneCauseEvidence_IsRefused(string key)
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        CauseData(report, "capture-lanes").Remove(key);

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    [DataRow("LaneQuarantineTerminalCount", 0)]
    [DataRow("LaneQuarantineTerminalCount", 2)]
    [DataRow("TerminalCount", 2)]
    [DataRow("RetryCount", 1)]
    [DataRow("MissingProductCount", 1)]
    [DataRow("ReplayTerminalCount", 1)]
    [DataRow("ReplayRetryCount", 1)]
    public void TryAccept_UnrelatedProcessingOutcome_IsRefused(string key, int value)
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        CauseData(report, "capture-processing")[key] = value;

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    [DataRow("DurableStateUnavailable")]
    [DataRow("ReconciliationFailed")]
    public void TryAccept_ProcessingFailureAlongsideATerminal_IsRefused(string key)
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        CauseData(report, "capture-processing")[key] = true;

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    public void TryAccept_BareTerminalWithoutDurableLaneAttribution_IsRefused()
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        CauseData(report, "capture-processing").Remove("LaneQuarantineTerminalCount");

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    public void TryAccept_WorkerFailureCannotBeHiddenByBacklogQuarantineReason()
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        CauseData(report, "capture-lanes")["OperationalFailureCount"] = 1;

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    public void TryAccept_DisagreeingLaneAndTerminalCounts_IsRefused()
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        var lane = CauseData(report, "capture-lanes");
        lane["QuarantineCount"] = 2;
        lane["RequiredProcessingQuarantineCount"] = 2;

        Assert.IsFalse(AcceptCauseReport(report));
    }

    [TestMethod]
    public void TryAccept_MatchingDurableTerminalAndOptionalQuarantine_IsReady()
    {
        var report = JsonNode.Parse(Health(1, "terminal"))!;
        var lane = CauseData(report, "capture-lanes");
        lane["LaneCount"] = 2;
        lane["QuarantineCount"] = 2;

        Assert.IsTrue(AcceptCauseReport(report));
    }

    private static JsonObject CauseData(JsonNode report, string name)
        => report["checks"]!.AsArray().Single(check => check!["name"]!.GetValue<string>() == name)!["data"]!.AsObject();

    private static bool AcceptCauseReport(JsonNode report)
    {
        using var document = JsonDocument.Parse(report.ToJsonString());
        return QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out _);
    }
}
