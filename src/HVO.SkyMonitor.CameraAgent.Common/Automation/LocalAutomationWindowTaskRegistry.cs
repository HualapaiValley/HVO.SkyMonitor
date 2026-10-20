namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>An installed built-in producer. Descriptors publish only real, registered targets and presets.</summary>
public interface ILocalAutomationWindowTaskAdapter
{
    LocalAutomationTaskDescriptor Describe();

    LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition);

    ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken);
}

/// <summary>
/// Routes typed periodic tasks and source-window tasks to their actual adapters alongside existing
/// environmental acquisition. Periodic adapters are consulted first, by task kind, then source-window
/// adapters, then the environmental registry; the families never share a task kind.
/// </summary>
public sealed class LocalAutomationTaskRegistry : ILocalAutomationTaskRegistry
{
    private readonly EnvironmentalLocalAutomationTaskRegistry _environmental;
    private readonly ILocalAutomationWindowTaskAdapter[] _adapters;
    private readonly ILocalAutomationPeriodicTaskAdapter[] _periodicAdapters;
    private readonly TimeProvider _timeProvider;

    public LocalAutomationTaskRegistry(EnvironmentalLocalAutomationTaskRegistry environmental,
        IEnumerable<ILocalAutomationWindowTaskAdapter> adapters, TimeProvider timeProvider,
        IEnumerable<ILocalAutomationPeriodicTaskAdapter>? periodicAdapters = null)
    {
        _environmental = environmental;
        _adapters = adapters.ToArray();
        _periodicAdapters = periodicAdapters?.ToArray() ?? [];
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
        var windowKinds = descriptors.Select(static item => item.TaskKind).ToHashSet();
        var periodic = _periodicAdapters.Select(static adapter => (adapter.TaskKind, Descriptor: adapter.Describe())).ToArray();
        if (periodic.Length > 8 || periodic.Select(static item => item.TaskKind).Distinct().Count() != periodic.Length ||
            periodic.Any(item => item.TaskKind == LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition ||
                !Enum.IsDefined(item.TaskKind) || windowKinds.Contains(item.TaskKind) ||
                item.Descriptor.TaskKind != item.TaskKind || item.Descriptor.Targets.Count > 32 ||
                item.Descriptor.Targets.Any(target => !LocalAutomationDefinitionValidator.IsText(target, 128)) ||
                item.Descriptor.CompatibleTriggers.Count != 1 ||
                item.Descriptor.CompatibleTriggers[0] != LocalAutomationTriggerKind.Periodic ||
                item.Descriptor.SupportedSourceWindows.Count != 0))
        {
            throw new InvalidOperationException(
                "Local periodic task adapter registration is invalid, duplicated, or shares a task kind.");
        }
    }

    public IReadOnlyList<LocalAutomationTaskDescriptor> Describe()
        => [.. _environmental.Describe(), .. _adapters.Select(static adapter => adapter.Describe()),
            .. _periodicAdapters.Select(static adapter => adapter.Describe())];

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (LocalAutomationDefinitionValidator.Validate(definition) is { } invalid)
        {
            return invalid;
        }
        if (PeriodicAdapter(definition.TaskKind) is { } periodic)
        {
            return ValidatePeriodic(periodic, definition);
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
        if (PeriodicAdapter(definition.TaskKind) is not null)
        {
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Skipped,
                "The periodic task requires a retained durable occurrence."));
        }
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
        if (PeriodicAdapter(occurrence.Definition.TaskKind) is { } periodic)
        {
            return occurrence.SourceWindow is null
                ? periodic.ExecuteAsync(occurrence, cancellationToken)
                : ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Skipped,
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

    public DateTimeOffset ResolveInitialTriggerEpoch(LocalAutomationDefinition proposed, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        return PeriodicAdapter(proposed.TaskKind) is { } periodic
            ? periodic.ResolveInitialTriggerEpoch(nowUtc)
            : nowUtc;
    }

    public bool SupportsRunNow(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return PeriodicAdapter(definition.TaskKind) is { SupportsRunNow: true };
    }

    public bool RunsOffSweep(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return PeriodicAdapter(definition.TaskKind) is { RunsOffSweep: true };
    }

    private ILocalAutomationPeriodicTaskAdapter? PeriodicAdapter(LocalAutomationTaskKind kind)
        => _periodicAdapters.SingleOrDefault(item => item.TaskKind == kind);

    /// <summary>
    /// A periodic kind needs the periodic trigger, no source window, an available descriptor, and a
    /// published target before the adapter applies its own rules, such as a longer minimum interval.
    /// </summary>
    private static LocalAutomationRegistryRejection? ValidatePeriodic(
        ILocalAutomationPeriodicTaskAdapter adapter, LocalAutomationDefinition definition)
    {
        var descriptor = adapter.Describe();
        if (!descriptor.Available || definition.TriggerKind != LocalAutomationTriggerKind.Periodic ||
            definition.SourceWindow is not null || !descriptor.CompatibleTriggers.Contains(definition.TriggerKind))
        {
            return new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        return !descriptor.Targets.Contains(definition.TaskTarget, StringComparer.Ordinal)
            ? new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget")
            : adapter.Validate(definition);
    }
}
