using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// One explicit step-version upgrade (#1386 design decision 2). It runs only inside
/// <see cref="ProcessingNodeOptionsCatalog.ApplyUpgrade"/>, for every eligible node of a draft at once; decoding and
/// validation never upgrade.
/// </summary>
public interface IProcessingNodeOptionsUpgrade
{
    string StepAlias { get; }

    string FromStepVersion { get; }

    string ToStepVersion { get; }

    /// <summary>The node at <see cref="ToStepVersion"/>, keeping its id and step alias.</summary>
    ProcessingGraphNodeDefinition Upgrade(ProcessingGraphNodeDefinition node);
}

public enum ProcessingNodeOptionsResolutionStatus
{
    /// <summary>No descriptor covers the step alias; its options stay opaque.</summary>
    Opaque,

    /// <summary>A descriptor covers this exact step version.</summary>
    Described,

    /// <summary>The step version is not described, but an explicit upgrade leads to one that is.</summary>
    UpgradeRequired,

    /// <summary>The step alias is described at other versions only, with no upgrade from this one.</summary>
    UnsupportedVersion
}

/// <summary>How a node's options resolve. <see cref="Upgrade"/> is the upgrade offered from this step version.</summary>
public sealed record ProcessingNodeOptionsResolution(
    ProcessingNodeOptionsResolutionStatus Status,
    ProcessingNodeOptionsDescriptor? Descriptor,
    IProcessingNodeOptionsUpgrade? Upgrade);

public sealed record ProcessingNodeOptionsUpgradeStep(
    string NodeId,
    string StepAlias,
    string FromStepVersion,
    string ToStepVersion);

/// <summary>Every upgrade a draft is eligible for, bound to the draft's definition identity.</summary>
public sealed record ProcessingGraphOptionsUpgradePlan(
    string SourceDefinitionIdentitySha256,
    string SourceRevision,
    ImmutableArray<ProcessingNodeOptionsUpgradeStep> Steps)
{
    public bool IsEmpty => Steps.IsEmpty;
}

public sealed record ProcessingGraphOptionsUpgradeResult(
    ProcessingGraphDefinition? Definition,
    ImmutableArray<ProcessingGraphDiagnostic> Diagnostics)
{
    public bool IsValid => Definition is not null && Diagnostics.IsEmpty;
}

/// <summary>
/// The option descriptors and upgrades both hosts resolve nodes against (#1532). Steps are matched by alias ignoring
/// case, as CameraAgent's step registry does, and by exact step version.
/// </summary>
public sealed class ProcessingNodeOptionsCatalog
{
    private readonly Dictionary<(string Alias, string Version), ProcessingNodeOptionsDescriptor> _descriptors;
    private readonly Dictionary<(string Alias, string Version), IProcessingNodeOptionsUpgrade> _upgrades;
    private readonly HashSet<string> _describedAliases;

    public ProcessingNodeOptionsCatalog(
        IEnumerable<ProcessingNodeOptionsDescriptor> descriptors,
        IEnumerable<IProcessingNodeOptionsUpgrade>? upgrades = null)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        Descriptors = [.. descriptors];
        Upgrades = upgrades is null ? [] : [.. upgrades];
        _descriptors = new(StepKeyComparer.Instance);
        foreach (var descriptor in Descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor, nameof(descriptors));
            if (!_descriptors.TryAdd((descriptor.StepAlias, descriptor.StepVersion), descriptor))
            {
                throw new ArgumentException(
                    $"Step '{descriptor.StepAlias}' version '{descriptor.StepVersion}' is described more than once.",
                    nameof(descriptors));
            }
        }
        _describedAliases = Descriptors.Select(static descriptor => descriptor.StepAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _upgrades = new(StepKeyComparer.Instance);
        foreach (var upgrade in Upgrades)
        {
            ArgumentNullException.ThrowIfNull(upgrade, nameof(upgrades));
            if (string.Equals(upgrade.FromStepVersion, upgrade.ToStepVersion, StringComparison.Ordinal) ||
                !_descriptors.ContainsKey((upgrade.StepAlias, upgrade.ToStepVersion)) ||
                !_upgrades.TryAdd((upgrade.StepAlias, upgrade.FromStepVersion), upgrade))
            {
                throw new ArgumentException(
                    $"The upgrade of step '{upgrade.StepAlias}' from '{upgrade.FromStepVersion}' must be the only one " +
                    "from that version and lead to a described version.",
                    nameof(upgrades));
            }
        }
    }

    /// <summary>The shipped descriptors. No upgrade ships yet, so no saved pipeline changes.</summary>
    public static ProcessingNodeOptionsCatalog BuiltIn { get; } = new(ProcessingNodeOptionsDescriptors.All);

    public ImmutableArray<ProcessingNodeOptionsDescriptor> Descriptors { get; }

    public ImmutableArray<IProcessingNodeOptionsUpgrade> Upgrades { get; }

    public ProcessingNodeOptionsResolution Resolve(string stepAlias, string stepVersion)
    {
        ArgumentNullException.ThrowIfNull(stepAlias);
        ArgumentNullException.ThrowIfNull(stepVersion);
        _upgrades.TryGetValue((stepAlias, stepVersion), out var upgrade);
        if (_descriptors.TryGetValue((stepAlias, stepVersion), out var descriptor))
        {
            return new(ProcessingNodeOptionsResolutionStatus.Described, descriptor, upgrade);
        }
        if (upgrade is not null)
        {
            return new(ProcessingNodeOptionsResolutionStatus.UpgradeRequired, null, upgrade);
        }
        return new(_describedAliases.Contains(stepAlias)
            ? ProcessingNodeOptionsResolutionStatus.UnsupportedVersion
            : ProcessingNodeOptionsResolutionStatus.Opaque, null, null);
    }

    /// <summary>
    /// Validates the options of every described node, enabled or not. Nodes of other steps or step versions stay
    /// opaque and produce no diagnostic, so a graph that compiles today still validates.
    /// </summary>
    public ImmutableArray<ProcessingGraphDiagnostic> Validate(ProcessingGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var diagnostics = ImmutableArray.CreateBuilder<ProcessingGraphDiagnostic>();
        for (var index = 0; index < definition.Nodes.Length; index++)
        {
            var node = definition.Nodes[index];
            if (Resolve(node.StepAlias, node.StepVersion).Descriptor is { } descriptor)
            {
                diagnostics.AddRange(ProcessingNodeOptionsCodec.Decode(
                    descriptor, node.EffectiveOptions, $"nodes[{index}].effectiveOptions").Diagnostics);
            }
        }
        return diagnostics.ToImmutable();
    }

    /// <summary>Lists every node of the draft an upgrade applies to, without changing anything.</summary>
    public ProcessingGraphOptionsUpgradePlan PlanUpgrade(ProcessingGraphDefinition draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var steps = draft.Nodes
            .Select(node => (node, Resolve(node.StepAlias, node.StepVersion).Upgrade))
            .Where(static candidate => candidate.Upgrade is not null)
            .Select(static candidate => new ProcessingNodeOptionsUpgradeStep(
                candidate.node.Id,
                candidate.node.StepAlias,
                candidate.node.StepVersion,
                candidate.Upgrade!.ToStepVersion))
            .ToImmutableArray();
        return new(ProcessingGraphJson.ComputeDefinitionIdentity(draft), draft.Revision, steps);
    }

    /// <summary>
    /// Upgrades every planned node together into a new revision, or nothing. The plan must be the current plan of
    /// exactly this draft, the revision must be new, and every upgraded node must validate against its new descriptor
    /// and the whole graph must still compile.
    /// </summary>
    public ProcessingGraphOptionsUpgradeResult ApplyUpgrade(
        ProcessingGraphDefinition draft,
        ProcessingGraphOptionsUpgradePlan plan,
        string revision)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(plan);
        var diagnostics = ImmutableArray.CreateBuilder<ProcessingGraphDiagnostic>();
        if (string.IsNullOrWhiteSpace(revision) || string.Equals(revision, draft.Revision, StringComparison.Ordinal))
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.UpgradeRevisionRequired, "revision",
                "An upgrade produces a new revision of the draft."));
        }
        var current = PlanUpgrade(draft);
        if (!string.Equals(plan.SourceDefinitionIdentitySha256, current.SourceDefinitionIdentitySha256,
                StringComparison.Ordinal) ||
            !string.Equals(plan.SourceRevision, current.SourceRevision, StringComparison.Ordinal) ||
            !plan.Steps.SequenceEqual(current.Steps))
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.UpgradePlanStale, "$",
                "The upgrade plan is not the current plan of this draft."));
        }
        else if (current.IsEmpty)
        {
            diagnostics.Add(new(ProcessingOptionReasonCodes.UpgradeNotRequired, "nodes",
                "No node of this draft has an upgrade."));
        }
        if (diagnostics.Count > 0)
        {
            return new(null, diagnostics.ToImmutable());
        }

        var nodes = draft.Nodes.ToBuilder();
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (Resolve(node.StepAlias, node.StepVersion).Upgrade is not { } upgrade)
            {
                continue;
            }
            var upgraded = upgrade.Upgrade(node);
            if (!string.Equals(upgraded.Id, node.Id, StringComparison.Ordinal) ||
                !string.Equals(upgraded.StepAlias, node.StepAlias, StringComparison.Ordinal) ||
                !string.Equals(upgraded.StepVersion, upgrade.ToStepVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The upgrade of step '{upgrade.StepAlias}' from '{upgrade.FromStepVersion}' must keep the node " +
                    $"id and step alias and produce version '{upgrade.ToStepVersion}'.");
            }
            diagnostics.AddRange(ProcessingNodeOptionsCodec.Decode(
                Resolve(upgraded.StepAlias, upgraded.StepVersion).Descriptor!,
                upgraded.EffectiveOptions,
                $"nodes[{index}].effectiveOptions").Diagnostics);
            nodes[index] = upgraded;
        }
        if (diagnostics.Count > 0)
        {
            return new(null, diagnostics.ToImmutable());
        }

        var result = draft with { Revision = revision, Nodes = nodes.ToImmutable() };
        var compiled = ProcessingGraphCompiler.Compile(result);
        return compiled.IsValid ? new(result, []) : new(null, compiled.Diagnostics);
    }

    private sealed class StepKeyComparer : IEqualityComparer<(string Alias, string Version)>
    {
        public static StepKeyComparer Instance { get; } = new();

        public bool Equals((string Alias, string Version) x, (string Alias, string Version) y)
            => string.Equals(x.Alias, y.Alias, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Version, y.Version, StringComparison.Ordinal);

        public int GetHashCode((string Alias, string Version) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Alias),
                StringComparer.Ordinal.GetHashCode(obj.Version));
    }
}
