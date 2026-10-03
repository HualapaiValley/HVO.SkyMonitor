namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>An installed built-in producer. Descriptors publish only real, registered targets and presets.</summary>
public interface ILocalAutomationWindowTaskAdapter
{
    LocalAutomationTaskDescriptor Describe();

    LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition);

    ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken);
}

/// <summary>Routes typed source-window tasks to their actual adapters alongside existing environmental acquisition.</summary>
public sealed class LocalAutomationTaskRegistry : ILocalAutomationTaskRegistry
{
    private readonly EnvironmentalLocalAutomationTaskRegistry _environmental;
    private readonly ILocalAutomationWindowTaskAdapter[] _adapters;
    private readonly TimeProvider _timeProvider;

    public LocalAutomationTaskRegistry(EnvironmentalLocalAutomationTaskRegistry environmental,
        IEnumerable<ILocalAutomationWindowTaskAdapter> adapters, TimeProvider timeProvider)
    {
        _environmental = environmental;
        _adapters = adapters.ToArray();
        _timeProvider = timeProvider;
        var descriptors = _adapters.Select(static adapter => adapter.Describe()).ToArray();
        if (descriptors.Length > 8 || descriptors.Select(static item => item.TaskKind).Distinct().Count() != descriptors.Length ||
            descriptors.Any(static item => item.TaskKind == LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition ||
                !Enum.IsDefined(item.TaskKind) || item.Targets.Count > 32 ||
                item.Targets.Any(target => !LocalAutomationDefinitionValidator.IsText(target, 128)) ||
                item.CompatibleTriggers.Count != 1 || item.CompatibleTriggers[0] != LocalAutomationTriggerKind.SourceWindowClosed ||
                item.SupportedSourceWindows.Count is < 1 or > 2 || item.SupportedSourceWindows.Any(kind => !Enum.IsDefined(kind))))
        {
            throw new InvalidOperationException("Local source-window task adapter registration is invalid or duplicated.");
        }
    }

    public IReadOnlyList<LocalAutomationTaskDescriptor> Describe()
        => [.. _environmental.Describe(), .. _adapters.Select(static adapter => adapter.Describe())];

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (LocalAutomationDefinitionValidator.Validate(definition) is { } invalid)
        {
            return invalid;
        }
        if (definition.TaskKind == LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition)
        {
            return _environmental.Validate(definition);
        }
        var adapter = _adapters.SingleOrDefault(item => item.Describe().TaskKind == definition.TaskKind);
        var descriptor = adapter?.Describe();
        if (descriptor is null || !descriptor.Available || definition.SourceWindow is null ||
            !descriptor.CompatibleTriggers.Contains(definition.TriggerKind) ||
            !descriptor.SupportedSourceWindows.Contains(definition.SourceWindow.Kind))
        {
            return new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        return !descriptor.Targets.Contains(definition.TaskTarget, StringComparer.Ordinal)
            ? new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget")
            : adapter!.Validate(definition);
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition,
        string runKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.TaskKind == LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition && definition.SourceWindow is null
            ? _environmental.ExecuteAsync(definition, runKey, cancellationToken)
            : ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Skipped,
                "The task requires a retained source-window occurrence."));
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!occurrence.IsValid() || Validate(occurrence.Definition) is not null)
        {
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Skipped,
                "The retained occurrence or its registered task is unavailable."));
        }
        if (occurrence.SourceWindow is not { } window)
        {
            return _environmental.ExecuteAsync(occurrence.Definition, occurrence.RunKey, cancellationToken);
        }
        if (!window.IsEligibleForFinal(_timeProvider.GetUtcNow()))
        {
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Skipped,
                "The retained source window is not yet eligible for final generation."));
        }
        return _adapters.Single(item => item.Describe().TaskKind == occurrence.Definition.TaskKind)
            .ExecuteAsync(occurrence, cancellationToken);
    }
}
