using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>
/// Registers nightly keogram and star-trail generation as a periodic automation task. A definition names a product
/// kind as its target; each occurrence evaluates every settled window, so the interval sets how promptly hourly
/// segments and the observing-day rollover are published. Generation is idempotent by output identity, so a retried
/// occurrence reuses rather than republishes.
/// </summary>
internal sealed class NightlyProductAutomationTaskRegistry(
    NightlyProductGenerator generator,
    IOptions<CameraAgentHostOptions> options) : ILocalAutomationTaskRegistry
{
    private static readonly LocalAutomationTriggerKind[] Triggers = [LocalAutomationTriggerKind.Periodic];

    private static readonly string[] Targets =
        [NightlyProductContract.KeogramTarget, NightlyProductContract.StarTrailTarget];

    public IReadOnlyList<LocalAutomationTaskDescriptor> Describe()
    {
        var nightly = options.Value.NightlyProducts;
        var enabled = nightly.Enabled && !string.IsNullOrWhiteSpace(nightly.SourceNodeId);
        return
        [
            new LocalAutomationTaskDescriptor(
                LocalAutomationTaskKind.NightlyProductGeneration,
                "Generates hourly keogram or star-trail segments from published preview frames and composes each "
                + "observing night once it has settled.",
                Triggers,
                enabled ? Targets : [],
                Available: enabled,
                UnavailableReason: enabled
                    ? null
                    : "Nightly product generation is disabled in this CameraAgent's startup configuration.")
        ];
    }

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var descriptor = Describe()[0];
        if (definition.TaskKind != descriptor.TaskKind || !descriptor.CompatibleTriggers.Contains(definition.TriggerKind))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        return descriptor.Targets.Contains(definition.TaskTarget, StringComparer.Ordinal)
            ? null
            : new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "One failing automation run is recorded as a failure; it must not stop the runner.")]
    public async ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationDefinition definition,
        string runKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        if (definition.TaskKind != LocalAutomationTaskKind.NightlyProductGeneration)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed, "The task kind is not registered on this CameraAgent.");
        }
        if (Validate(definition) is { } rejection ||
            !NightlyProductContract.TryParseTarget(definition.TaskTarget, out var kind))
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped,
                "The registered task is currently unavailable: nightly product generation is disabled or the target "
                + "is not a nightly product kind.");
        }
        try
        {
            var report = await generator.RunAsync(kind, cancellationToken).ConfigureAwait(false);
            return new LocalAutomationExecution(Outcome(report), report.Describe());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed,
                string.Concat("The registered task failed: ", exception.GetType().Name, "."));
        }
    }

    /// <summary>
    /// A run that verified nothing is skipped, a run that left a window unverifiable failed, and every other run
    /// succeeded, including one that left budgeted work pending for the next occurrence.
    /// </summary>
    internal static LocalAutomationRunOutcome Outcome(NightlyProductRunReport report) =>
        !report.Ready ? LocalAutomationRunOutcome.Skipped
        : report.FailedWindows > 0 ? LocalAutomationRunOutcome.Failed
        : LocalAutomationRunOutcome.Succeeded;
}
