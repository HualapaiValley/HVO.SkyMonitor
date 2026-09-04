using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Ordered, list-first editing model for a draft processing graph. Every change is a plain form
/// operation; the diagram is only a projection of the compiled preview. Opaque step options carried
/// from a source revision stay attached to their row and are re-emitted untouched.
/// </summary>
internal sealed class ProcessingGraphDraftModel
{
    public const string RawInput = "$raw";

    public string Name { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public List<NodeRow> Nodes { get; } = [];
    public string? SourceRevisionId { get; private set; }

    /// <summary>Carried from the source revision and shown read-only; a new draft uses the current explicit schema.</summary>
    public string SchemaVersion { get; private set; } = CapturePipelineSchemaVersions.ExplicitV2;

    public CapturePipelineDependencyPolicy DependencyPolicy { get; private set; } = CapturePipelineDependencyPolicy.RejectEnabledDependent;

    public static ProcessingGraphDraftModel Empty() => new();

    public static ProcessingGraphDraftModel FromPipeline(CapturePipelineConfig pipeline, string? sourceRevisionId, string name, string revision)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var model = new ProcessingGraphDraftModel
        {
            Name = name,
            Revision = revision,
            SourceRevisionId = sourceRevisionId,
            SchemaVersion = pipeline.SchemaVersion,
            DependencyPolicy = pipeline.DependencyPolicy
        };
        foreach (var step in pipeline.Steps)
        {
            model.Nodes.Add(new NodeRow
            {
                Id = string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim(),
                Type = step.Type,
                Required = step.Required,
                Enabled = step.Enabled ?? true,
                DependsOn = new HashSet<string>(step.DependsOn ?? [], StringComparer.Ordinal),
                Options = step.Options,
                Publication = step.Publication
            });
        }
        return model;
    }

    public void AddNode(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var id = type;
        for (var suffix = 2; Nodes.Any(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            id = string.Create(CultureInfo.InvariantCulture, $"{type}-{suffix}");
        }
        Nodes.Add(new NodeRow { Id = id, Type = type, Required = true, Enabled = true, DependsOn = new HashSet<string>([RawInput], StringComparer.Ordinal) });
    }

    public void Remove(NodeRow node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Nodes.Remove(node);
        foreach (var other in Nodes)
        {
            other.DependsOn.Remove(node.Id);
        }
    }

    public void Move(NodeRow node, int delta)
    {
        ArgumentNullException.ThrowIfNull(node);
        var index = Nodes.IndexOf(node);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Nodes.Count)
        {
            return;
        }
        Nodes.RemoveAt(index);
        Nodes.Insert(target, node);
        // A node that moved above one of its inputs can no longer name it; drop the edge visibly.
        node.DependsOn.IntersectWith(EligibleDependencies(node));
    }

    /// <summary>Inputs to render for a node: everything eligible plus any stale selection so it can be cleared.</summary>
    public IReadOnlyList<string> RenderableDependencies(NodeRow node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var eligible = EligibleDependencies(node);
        return [.. eligible, .. node.DependsOn.Where(dependency => !eligible.Contains(dependency, StringComparer.Ordinal)).OrderBy(static dependency => dependency, StringComparer.Ordinal)];
    }

    /// <summary>Identifiers a node may depend on: the raw input and every node above it in the list.</summary>
    public IReadOnlyList<string> EligibleDependencies(NodeRow node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var index = Nodes.IndexOf(node);
        return [RawInput, .. Nodes.Take(Math.Max(index, 0)).Select(static row => row.Id)];
    }

    public bool TryBuild(out CapturePipelineConfig pipeline, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("The graph needs a name.");
        }
        if (string.IsNullOrWhiteSpace(Revision))
        {
            problems.Add("The graph needs a revision label.");
        }
        if (Nodes.Count == 0)
        {
            problems.Add("Add at least one node.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                problems.Add("Every node needs an identifier.");
            }
            else if (!seen.Add(node.Id.Trim()))
            {
                problems.Add($"Node identifier '{node.Id.Trim()}' is used more than once.");
            }
            if (string.IsNullOrWhiteSpace(node.Type))
            {
                problems.Add($"Node '{node.Id}' needs a type.");
            }
            var eligible = EligibleDependencies(node);
            foreach (var dependency in node.DependsOn.Where(dependency => !eligible.Contains(dependency, StringComparer.Ordinal)))
            {
                problems.Add($"Node '{node.Id}' depends on '{dependency}', which is not the raw input or a node above it.");
            }
            if (node.DependsOn.Count == 0)
            {
                problems.Add($"Node '{node.Id}' must declare at least one dependency (use {RawInput} for the raw capture).");
            }
        }
        errors = problems.Distinct(StringComparer.Ordinal).ToArray();
        if (errors.Count > 0)
        {
            pipeline = CapturePipelineConfig.Empty;
            return false;
        }
        var steps = Nodes.Select((node, index) => new CaptureProcessingStepConfig(
            node.Type.Trim(),
            node.Id.Trim(),
            (index + 1) * 10,
            node.Options,
            node.DependsOn.OrderBy(static dependency => dependency, StringComparer.Ordinal).ToArray(),
            node.Required,
            node.Enabled ? null : false,
            node.Publication)).ToArray();
        pipeline = new CapturePipelineConfig(steps, SchemaVersion, DependencyPolicy);
        return true;
    }

    internal sealed class NodeRow
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool Required { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public HashSet<string> DependsOn { get; set; } = new(StringComparer.Ordinal);
        public JsonElement? Options { get; init; }
        public CaptureProcessingPublicationPolicy? Publication { get; init; }
        public bool HasOptions => Options is not null;

        public void ToggleDependency(string dependency, bool selected)
        {
            if (selected)
            {
                DependsOn.Add(dependency);
            }
            else
            {
                DependsOn.Remove(dependency);
            }
        }
    }
}
