namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// An installed built-in task that runs on the wall-clock periodic trigger, routed by its task kind.
/// It never owns a source window, and its kind is never shared with a source-window adapter or the
/// environmental registry, so registering one leaves every existing task's routing unchanged.
/// </summary>
public interface ILocalAutomationPeriodicTaskAdapter
{
    /// <summary>The single task kind this adapter owns; it must equal the kind its descriptor publishes.</summary>
    LocalAutomationTaskKind TaskKind { get; }

    /// <summary>Publishes only the periodic trigger and the real targets this adapter can currently run.</summary>
    LocalAutomationTaskDescriptor Describe();

    /// <summary>
    /// Applies this task's own rules, such as a longer minimum interval, after the registry has accepted
    /// the shared bounds, the periodic trigger, the absent source window, and a published target.
    /// </summary>
    LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition);

    /// <summary>
    /// The phase origin of a definition saved for the first time; later saves keep the stored epoch.
    /// The store accepts only a millisecond-truncated UTC instant at or after
    /// <paramref name="nowUtc"/> and less than one interval after it. It is called inside the
    /// automation store's write transaction, so it must be fast and must not call back into that store.
    /// </summary>
    DateTimeOffset ResolveInitialTriggerEpoch(DateTimeOffset nowUtc) => nowUtc;

    /// <summary>Whether an explicit operator run-now may be queued for this task kind.</summary>
    bool SupportsRunNow { get; }

    /// <summary>
    /// Runs one durable occurrence. A run-now occurrence is identified by
    /// <see cref="LocalAutomationRunKeys.IsRunNow(string)"/> on its run key.
    /// </summary>
    ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken);
}
