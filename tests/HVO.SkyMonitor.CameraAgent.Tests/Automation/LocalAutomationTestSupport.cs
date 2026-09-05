using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>A deterministic clock for the local automation contract tests.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

/// <summary>A registry whose targets and outcomes the test controls.</summary>
internal sealed class StubAutomationTaskRegistry : ILocalAutomationTaskRegistry
{
    public List<string> Targets { get; } = ["virtual-sky-temperature"];

    public List<LocalAutomationTriggerKind> Triggers { get; } =
    [
        LocalAutomationTriggerKind.Periodic,
        LocalAutomationTriggerKind.CaptureRelative
    ];

    public List<string> ExecutedRunKeys { get; } = [];

    public LocalAutomationExecution Result { get; set; } =
        new(LocalAutomationRunOutcome.Succeeded, "Acquired an observation.");

    public Exception? Throw { get; set; }

    public bool Available { get; set; } = true;

    public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() =>
    [
        new LocalAutomationTaskDescriptor(
            LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            "Stub task.",
            Triggers,
            Targets,
            Available,
            Available ? null : "No target is registered.")
    ];

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Triggers.Contains(definition.TriggerKind))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        return Targets.Contains(definition.TaskTarget, StringComparer.Ordinal)
            ? null
            : new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationDefinition definition,
        string runKey,
        CancellationToken cancellationToken)
    {
        ExecutedRunKeys.Add(runKey);
        return Throw is null ? ValueTask.FromResult(Result) : ValueTask.FromException<LocalAutomationExecution>(Throw);
    }
}

/// <summary>A capture-sequence source the test moves by hand.</summary>
internal sealed class StubCaptureSequenceSource : ILocalAutomationCaptureSequenceSource
{
    public long? Sequence { get; set; }

    public Exception? Throw { get; set; }

    public int Reads { get; private set; }

    public ValueTask<long?> GetCaptureSequenceAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Throw is null ? ValueTask.FromResult(Sequence) : ValueTask.FromException<long?>(Throw);
    }
}
