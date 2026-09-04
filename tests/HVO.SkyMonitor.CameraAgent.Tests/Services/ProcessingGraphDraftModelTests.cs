using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphDraftModelTests
{
    private static readonly string[] PreviewOnly = ["preview"];
    private static readonly string[] AddedIds = ["preview", "preview-2", "telemetry"];
    private static readonly string[] EligibleForThird = ["$raw", "preview", "preview-2"];
    private static readonly string[] MovedIds = ["preview", "telemetry", "preview-2"];

    [TestMethod]
    public void FromPipeline_ThenBuild_RoundTripsNodesDependenciesAndOpaqueOptions()
    {
        var options = JsonSerializer.SerializeToElement(new { outputVariant = "display" });
        var source = new CapturePipelineConfig(
        [
            new CaptureProcessingStepConfig("Preview", "preview", 10, options, ["$raw"], Enabled: true),
            new CaptureProcessingStepConfig("Telemetry", "telemetry", 20, null, ["preview"], Required: false, Enabled: false)
        ]);

        var model = ProcessingGraphDraftModel.FromPipeline(source, "rev-1", "night", "2");
        Assert.IsTrue(model.TryBuild(out var built, out var errors), string.Join(" ", errors));

        Assert.AreEqual("rev-1", model.SourceRevisionId);
        Assert.HasCount(2, built.Steps);
        Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, built.SchemaVersion);
        Assert.AreEqual(CapturePipelineDependencyPolicy.RejectEnabledDependent, built.DependencyPolicy);
        Assert.AreEqual(options.GetRawText(), built.Steps[0].Options!.Value.GetRawText());
        Assert.IsNull(built.Steps[0].Enabled);
        Assert.IsFalse(built.Steps[1].Enabled);
        Assert.IsFalse(built.Steps[1].Required);
        CollectionAssert.AreEqual(PreviewOnly, built.Steps[1].DependsOn!.ToArray());
        Assert.AreEqual(10, built.Steps[0].Order);
        Assert.AreEqual(20, built.Steps[1].Order);
        Assert.IsTrue(model.Nodes[0].HasOptions);
    }

    [TestMethod]
    public void EditingOperations_KeepTheListConsistent()
    {
        var model = ProcessingGraphDraftModel.Empty();
        model.Name = "night";
        model.Revision = "1";
        model.AddNode("preview");
        model.AddNode("preview");
        model.AddNode("telemetry");

        CollectionAssert.AreEqual(AddedIds, model.Nodes.Select(static node => node.Id).ToArray());
        CollectionAssert.AreEqual(EligibleForThird, model.EligibleDependencies(model.Nodes[2]).ToArray());
        model.Nodes[2].ToggleDependency("preview", selected: true);
        model.Move(model.Nodes[2], -1);
        CollectionAssert.AreEqual(MovedIds, model.Nodes.Select(static node => node.Id).ToArray());
        model.Remove(model.Nodes[0]);
        Assert.IsFalse(model.Nodes[0].DependsOn.Contains("preview"));
        Assert.IsTrue(model.TryBuild(out _, out _));
        model.Nodes[0].ToggleDependency(ProcessingGraphDraftModel.RawInput, selected: false);
        Assert.IsFalse(model.TryBuild(out _, out var errors));
        CollectionAssert.Contains(errors.ToArray(), "Node 'telemetry' must declare at least one dependency (use $raw for the raw capture).");
    }

    [TestMethod]
    public void Build_ReportsIdentityDuplicateAndForwardDependencyProblems()
    {
        var model = ProcessingGraphDraftModel.Empty();
        model.AddNode("preview");
        model.AddNode("telemetry");
        model.Nodes[1].Id = "preview";
        model.Nodes[0].ToggleDependency("telemetry", selected: true);

        Assert.IsFalse(model.TryBuild(out _, out var errors));

        CollectionAssert.Contains(errors.ToArray(), "The graph needs a name.");
        CollectionAssert.Contains(errors.ToArray(), "The graph needs a revision label.");
        CollectionAssert.Contains(errors.ToArray(), "Node identifier 'preview' is used more than once.");
        CollectionAssert.Contains(errors.ToArray(), "Node 'preview' depends on 'telemetry', which is not the raw input or a node above it.");
    }
}
