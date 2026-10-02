namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// The registry the automation store and runner see: the union of every task registry, each owning a disjoint set of
/// task kinds. A definition is validated and executed only by the registry that publishes its task kind.
/// </summary>
internal sealed class CompositeLocalAutomationTaskRegistry : ILocalAutomationTaskRegistry
{
    private readonly IReadOnlyList<ILocalAutomationTaskRegistry> _registries;

    public CompositeLocalAutomationTaskRegistry(params ILocalAutomationTaskRegistry[] registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        _registries = [.. registries];
        var kinds = _registries.SelectMany(static registry => registry.Describe()).Select(static d => d.TaskKind).ToArray();
        if (kinds.Length != kinds.Distinct().Count())
        {
            throw new InvalidOperationException("Two automation task registries publish the same task kind.");
        }
    }

    public IReadOnlyList<LocalAutomationTaskDescriptor> Describe()
        => [.. _registries.SelectMany(static registry => registry.Describe()).OrderBy(static d => d.TaskKind)];

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Owner(definition.TaskKind) is { } owner
            ? owner.Validate(definition)
            : new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationDefinition definition,
        string runKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Owner(definition.TaskKind) is { } owner
            ? owner.ExecuteAsync(definition, runKey, cancellationToken)
            : ValueTask.FromResult(new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed, "The task kind is not registered on this CameraAgent."));
    }

    private ILocalAutomationTaskRegistry? Owner(LocalAutomationTaskKind kind)
        => _registries.FirstOrDefault(registry => registry.Describe().Any(descriptor => descriptor.TaskKind == kind));
}
