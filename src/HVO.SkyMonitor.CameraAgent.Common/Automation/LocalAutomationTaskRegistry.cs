using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>One rejected definition, named by its contract reason code and field path.</summary>
public sealed record LocalAutomationRegistryRejection(string ReasonCode, string FieldPath);

/// <summary>The bounded disposition of one registered task execution.</summary>
public sealed record LocalAutomationExecution(LocalAutomationRunOutcome Outcome, string Detail);

/// <summary>
/// The closed set of tasks an automation definition may name and the only place that maps a stored
/// definition onto a real operation. A definition can name nothing this registry does not publish,
/// so no definition can express a command, script, or arbitrary endpoint.
/// </summary>
public interface ILocalAutomationTaskRegistry
{
    /// <summary>Describes every registered task kind, its compatible triggers, and its current targets.</summary>
    IReadOnlyList<LocalAutomationTaskDescriptor> Describe();

    /// <summary>Returns the rejection for an incompatible definition, or null when the registry accepts it.</summary>
    LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition);

    /// <summary>
    /// Runs one occurrence through the task's existing coordinator. The run key is the command
    /// identity, so a retried occurrence replays rather than acquiring twice.
    /// </summary>
    ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationDefinition definition,
        string runKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// The delivered registry. Its single task kind runs the existing environmental on-demand
/// acquisition through <see cref="EnvironmentalOnDemandAcquisitionService"/>, which owns the
/// durable command claim, coalescing, and receipt. The runner never admits an exposure, never
/// changes acquisition timing, and never occupies the live processing slot.
/// </summary>
public sealed class EnvironmentalLocalAutomationTaskRegistry(
    EnvironmentalAcquisitionCoordinator coordinator,
    EnvironmentalOnDemandAcquisitionService onDemandAcquisition,
    IOptions<CameraAgentHostOptions> options) : ILocalAutomationTaskRegistry
{
    private static readonly LocalAutomationTriggerKind[] EnvironmentalTriggers =
    [
        LocalAutomationTriggerKind.Periodic,
        LocalAutomationTriggerKind.CaptureRelative
    ];

    /// <summary>The actor recorded on every command this registry issues on a definition's behalf.</summary>
    internal static string ActorFor(string definitionId) =>
        string.Concat("automation:", definitionId);

    public IReadOnlyList<LocalAutomationTaskDescriptor> Describe()
    {
        var enabled = options.Value.EnvironmentalAcquisition.Enabled;
        var targets = enabled ? OnDemandTargets() : [];
        return
        [
            new LocalAutomationTaskDescriptor(
                LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
                "Acquires one observation from a registered environmental source through its existing "
                + "on-demand contract.",
                EnvironmentalTriggers,
                targets,
                Available: enabled && targets.Count > 0,
                UnavailableReason: !enabled
                    ? "Environmental acquisition is disabled in this CameraAgent's startup configuration."
                    : targets.Count == 0
                        ? "No configured environmental source declares the On Demand trigger."
                        : null)
        ];
    }

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var descriptor = Describe().FirstOrDefault(candidate => candidate.TaskKind == definition.TaskKind);
        if (descriptor is null || !descriptor.CompatibleTriggers.Contains(definition.TriggerKind))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        // A definition may only name a target the registry currently publishes. A target that has
        // disappeared from configuration is rejected on save but never silently rewritten in a
        // stored revision: the stored definition stays exactly what the operator recorded.
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
        if (definition.TaskKind != LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed, "The task kind is not registered on this CameraAgent.");
        }
        if (Validate(definition) is { } rejection)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped,
                string.Concat("The registered task is currently unavailable: ", rejection.ReasonCode, "."));
        }
        try
        {
            var result = await onDemandAcquisition.AcquireAsync(
                definition.TaskTarget,
                runKey,
                ActorFor(definition.DefinitionId),
                "scheduled automation",
                cancellationToken).ConfigureAwait(false);
            return Describe(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EnvironmentalOnDemandCommandBusyException)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped,
                "The environmental source was already acquiring an observation.");
        }
        catch (EnvironmentalOnDemandCommandCapacityException)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped, "Environmental command capacity was unavailable.");
        }
        catch (EnvironmentalOnDemandCommandConflictException)
        {
            // The run key is derived from the definition revision and occurrence, so a conflicting
            // payload means the same occurrence was already recorded under different content.
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped,
                "The occurrence identifier was already bound to a different command.");
        }
        catch (Exception exception)
        {
            return new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed,
                string.Concat("The registered task failed: ", exception.GetType().Name, "."));
        }
    }

    private static LocalAutomationExecution Describe(EnvironmentalOnDemandAcquisitionResult result)
    {
        var replayed = result.Replayed ? " (replayed)" : string.Empty;
        return result.Receipt.Disposition switch
        {
            EnvironmentalAcquisitionDisposition.Produced => new LocalAutomationExecution(
                LocalAutomationRunOutcome.Succeeded,
                string.Concat("Acquired an observation", replayed, ".")),
            EnvironmentalAcquisitionDisposition.Duplicate => new LocalAutomationExecution(
                LocalAutomationRunOutcome.Succeeded,
                string.Concat("The source returned an observation already recorded", replayed, ".")),
            EnvironmentalAcquisitionDisposition.Missing => new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped,
                string.Concat("The source reported no value", replayed, ".")),
            EnvironmentalAcquisitionDisposition.Coalesced => new LocalAutomationExecution(
                LocalAutomationRunOutcome.Skipped,
                string.Concat("The source was already acquiring", replayed, ".")),
            EnvironmentalAcquisitionDisposition.TimedOut => new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed,
                string.Concat("The source timed out", replayed, ".")),
            _ => new LocalAutomationExecution(
                LocalAutomationRunOutcome.Failed,
                string.Concat("The source failed", replayed, "."))
        };
    }

    private IReadOnlyList<string> OnDemandTargets()
        => [.. coordinator.Sources
            .Where(static source => source.Triggers.Contains(EnvironmentalAcquisitionTrigger.OnDemand))
            .Select(static source => source.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)];
}

/// <summary>Shared bounds validation for a definition, independent of any registered task kind.</summary>
public static class LocalAutomationDefinitionValidator
{
    /// <summary>Returns the contract rejection for an unusable definition, or null when it is well formed.</summary>
    public static LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsIdentifier(definition.DefinitionId, LocalAutomationContract.MaximumDefinitionIdLength))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.InvalidCommandReasonCode, "definition.definitionId");
        }
        if (!IsText(definition.Name, LocalAutomationContract.MaximumNameLength))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.InvalidCommandReasonCode, "definition.name");
        }
        if (!IsText(definition.TaskTarget, LocalAutomationContract.MaximumTargetLength))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.InvalidCommandReasonCode, "definition.taskTarget");
        }
        if (!Enum.IsDefined(definition.TaskKind))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.taskKind");
        }
        if (!Enum.IsDefined(definition.TriggerKind))
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        var (minimum, maximum) = definition.TriggerKind switch
        {
            LocalAutomationTriggerKind.Periodic => (
                LocalAutomationContract.MinimumPeriodicIntervalSeconds,
                LocalAutomationContract.MaximumPeriodicIntervalSeconds),
            _ => (
                LocalAutomationContract.MinimumCaptureInterval,
                LocalAutomationContract.MaximumCaptureInterval)
        };
        if (definition.TriggerInterval < minimum || definition.TriggerInterval > maximum)
        {
            return new LocalAutomationRegistryRejection(
                LocalAutomationContract.InvalidCommandReasonCode, "definition.triggerInterval");
        }
        return definition.TriggerEpochUtc.Offset == TimeSpan.Zero
            ? null
            : new LocalAutomationRegistryRejection(
                LocalAutomationContract.InvalidCommandReasonCode, "definition.triggerEpochUtc");
    }

    /// <summary>An identifier is lower-case ASCII with digits and separators, so it is stable in a URL and a log.</summary>
    public static bool IsIdentifier(string? value, int maximumLength)
        => !string.IsNullOrEmpty(value)
           && value.Length <= maximumLength
           && char.IsAsciiLetterLower(value[0])
           && char.IsAsciiLetterOrDigit(value[^1])
           && value.All(static character =>
               char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.');

    /// <summary>Free text is trimmed, bounded, printable, and single-line.</summary>
    public static bool IsText(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= maximumLength
           && string.Equals(value, value.Trim(), StringComparison.Ordinal)
           && !value.Any(char.IsControl);

}
