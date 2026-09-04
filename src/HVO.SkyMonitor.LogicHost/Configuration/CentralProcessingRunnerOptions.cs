using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.LogicHost.Configuration;

internal enum CentralProcessingRunnerPlacement
{
    InProcess,
    Runner
}

internal sealed class CentralProcessingRunnerRequirementOptions
{
    public string? ResourceClass { get; init; }

    public string? LatencyClass { get; init; }

    public bool RequiresGpu { get; init; }

    public string? ProcessArchitecture { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];
}

/// <summary>
/// Host placement policy for <c>processing-runner-v1</c>. Placement is host configuration; eligibility classes are
/// protocol. A recipe placed on <see cref="CentralProcessingRunnerPlacement.Runner"/> is skipped by the in-process
/// worker and claimable only by a registered runner whose capabilities satisfy the recipe requirement, so a missing
/// or cold runner creates backlog rather than silently falling back.
/// </summary>
internal sealed class CentralProcessingRunnerOptions
{
    public const string SectionName = "ProcessingRunners";

    public bool Enabled { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ClaimBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan BacklogDegradedAfter { get; init; } = TimeSpan.FromMinutes(10);

    public int MaximumClaimCandidatesPerRequest { get; init; } = 8;

    public long MaximumProductBytes { get; init; } = ProcessingRunnerProtocol.MaximumTransferBytes;

    public Dictionary<string, CentralProcessingRunnerPlacement> Placement { get; init; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, CentralProcessingRunnerRequirementOptions> Requirements { get; init; } =
        new(StringComparer.Ordinal);

    /// <summary>Recipes whose execution requires LogicHost state and therefore can never be placed on a runner.</summary>
    public static bool IsRunnerCapableRecipe(string recipeName)
        => ProcessingRunnerCapabilities.BuiltInRecipeNames.Contains(recipeName, StringComparer.Ordinal);

    public IReadOnlySet<string> ResolveRunnerPlacedRecipes()
        => Placement
            .Where(static pair => pair.Value == CentralProcessingRunnerPlacement.Runner)
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

    public ProcessingRunnerJobRequirement ResolveRequirement(string recipeName)
        => Requirements.TryGetValue(recipeName, out var requirement)
            ? new ProcessingRunnerJobRequirement(
                recipeName,
                requirement.ResourceClass,
                requirement.LatencyClass,
                requirement.RequiresGpu,
                requirement.ProcessArchitecture,
                requirement.Labels.Count == 0 ? null : requirement.Labels)
            : new ProcessingRunnerJobRequirement(recipeName);

    /// <summary>Resolves the runner-placed recipes this runner may claim: version-matched and requirement-satisfied.</summary>
    public IReadOnlyList<string> ResolveEligibleRecipes(ProcessingRunnerCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var versionMatched = capabilities.ResolveVersionMatchedRecipes();
        return ResolveRunnerPlacedRecipes()
            .Where(recipe => ResolveRequirement(recipe).IsSatisfiedBy(capabilities, versionMatched))
            .OrderBy(static recipe => recipe, StringComparer.Ordinal)
            .ToArray();
    }

    public bool Validate(out string? error)
    {
        error = null;
        if (HeartbeatInterval <= TimeSpan.Zero || StaleAfter <= HeartbeatInterval
            || LeaseDuration < ProcessingRunnerProtocol.MinimumLeaseDuration
            || LeaseDuration > ProcessingRunnerProtocol.MaximumLeaseDuration
            || RenewalInterval <= TimeSpan.Zero || RenewalInterval >= LeaseDuration
            || ClaimBackoff <= TimeSpan.Zero || BacklogDegradedAfter <= TimeSpan.Zero)
        {
            error = "ProcessingRunners timing values are invalid.";
            return false;
        }
        if (MaximumClaimCandidatesPerRequest is < 1 or > 64)
        {
            error = "ProcessingRunners:MaximumClaimCandidatesPerRequest must be between 1 and 64.";
            return false;
        }
        if (MaximumProductBytes < 1 || MaximumProductBytes > ProcessingRunnerProtocol.MaximumTransferBytes)
        {
            error = "ProcessingRunners:MaximumProductBytes is out of range.";
            return false;
        }
        foreach (var recipe in Placement.Keys.Concat(Requirements.Keys))
        {
            if (!IsRunnerCapableRecipe(recipe))
            {
                error = $"ProcessingRunners recipe '{recipe}' is not a runner-capable built-in recipe.";
                return false;
            }
        }
        foreach (var requirement in Requirements.Values)
        {
            if ((requirement.ResourceClass is not null && !ProcessingRunnerProtocol.IsValidLabel(requirement.ResourceClass))
                || (requirement.LatencyClass is not null && !ProcessingRunnerProtocol.IsValidLabel(requirement.LatencyClass))
                || requirement.Labels.Any(static label => !ProcessingRunnerProtocol.IsValidLabel(label)))
            {
                error = "ProcessingRunners requirement labels are invalid.";
                return false;
            }
        }
        if (!Enabled && ResolveRunnerPlacedRecipes().Count != 0)
        {
            error = "ProcessingRunners:Placement assigns recipes to runners while ProcessingRunners:Enabled is false.";
            return false;
        }
        return true;
    }
}
