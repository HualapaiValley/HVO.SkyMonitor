using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

internal static class PipelineRunPresentation
{
    internal static string StatusClass(string status) => status switch
    {
        "Completed" or "Succeeded" or "Produced" => "success",
        "Running" => "running",
        "RetryableFailure" => "warning",
        "Failed" or "TerminalFailure" or "Cancelled" or "Expired" => "failure",
        "Skipped" => "skipped",
        _ => "pending"
    };

    internal static string StatusLabel(string status) => status switch
    {
        "Completed" => "Succeeded",
        "RetryableFailure" => "Retry pending",
        "TerminalFailure" => "Failed",
        _ => status
    };

    internal static string Duration(TimeSpan? duration) => duration is null ? "Not recorded"
        : duration.Value.TotalSeconds < 1
            ? $"{duration.Value.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)} ms"
            : $"{duration.Value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s";

    internal static TimeSpan? Duration(CameraAgentProcessingNodeView node) =>
        node.StartedUtc is { } start && node.CompletedUtc is { } end ? end - start : null;

    internal static string Name(CameraAgentProcessingNodeView node)
    {
        // Only frozen product contracts supply semantic names; custom nodes keep their own identity.
        var recipes = node.OutputContracts.Select(static contract => contract.Recipe?.Name).ToArray();
        if (recipes.Contains(BuiltInProcessingRecipes.CloudAssessment, StringComparer.Ordinal)) return "Cloud assessment";
        if (recipes.Contains("projected-scene", StringComparer.Ordinal)) return "Scene projection";
        if (recipes.Contains("linear-normalization", StringComparer.Ordinal)) return "Calibration";
        if (recipes.Contains("rolling-mean", StringComparer.Ordinal)) return "Rolling combination";
        if (recipes.Contains("image-quality", StringComparer.Ordinal)) return "Image quality";
        if (recipes.Contains("overlay-manifest", StringComparer.Ordinal)) return "Overlay manifest";
        if (recipes.Contains("presentation-materialization", StringComparer.Ordinal)) return "Materialize image";
        if (recipes.Contains("presentation-metadata-facts", StringComparer.Ordinal)) return "Environment facts";
        if (recipes.Contains("presentation-layer-payload", StringComparer.Ordinal)) return "Presentation layers";
        if (node.OutputContracts.Any(static contract => contract.Role == FrameArtifactRole.Preview)) return "Display preview";
        return node.NodeId;
    }

    internal static string Type(CameraAgentProcessingNodeView node) =>
        node.OutputContracts.Select(static contract => contract.Recipe?.Name)
            .FirstOrDefault(static name => name is not null) ?? "Recorded processing stage";

    internal static string Description(CameraAgentProcessingNodeView node) =>
        node.OutputContracts.Count == 0
            ? "Recorded capture-time stage. No output contract was retained for this stage."
            : $"Recorded stage {node.NodeId} declares {node.OutputContracts.Count} output contract(s) in this execution's frozen graph.";

    internal static string Runner(CameraAgentProcessingNodeView node)
    {
        var routes = node.Attempts.Select(static attempt => attempt.ExecutionRoute).Distinct(StringComparer.Ordinal).ToArray();
        return routes.Length == 0 ? "Not recorded" : string.Join(", ", routes.Select(static route => route switch
        {
            "InProcess" => "CameraAgent / in-process",
            "LocalRunner" => "CameraAgent / local replay runner",
            _ => route
        }));
    }
}
