namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>A stop names the exact retained occurrence revision, even after its definition is edited or removed.</summary>
public sealed record LocalAutomationScriptStopRequest(
    string DefinitionId, string RunKey, long ExpectedDefinitionVersion, string Actor);

/// <summary>Operator script controls use the durable queue and the runner's existing execution authority.</summary>
public sealed class LocalAutomationScriptCommands(ILocalAutomationStore store,
    LocalAutomationOffSweepWorker worker)
{
    public const string DefaultRunNowReason = "Operator requested script run-now.";
    public const string ScriptOnlyReason = "automation.script-control-only";
    public const string RunNotFoundReason = "automation.script-run-not-found";
    public const string StopUnavailableReason = "automation.script-stop-unavailable";

    public async ValueTask<LocalAutomationCommandResult> RunNowAsync(LocalAutomationRunNowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (store is not ILocalAutomationOccurrenceStore occurrences)
            return new(LocalAutomationCommandStatus.Invalid, ScriptOnlyReason, null, state);
        if (await occurrences.FindRunNowReplayAsync(request, cancellationToken).ConfigureAwait(false) is { } replay) return replay;
        var definition = state.Definitions.SingleOrDefault(item => item.Definition.DefinitionId == request.DefinitionId);
        if (definition is null) return new(LocalAutomationCommandStatus.NotFound,
            LocalAutomationContract.UnknownDefinitionReasonCode, null, state);
        if (definition.Definition.TaskKind != LocalAutomationTaskKind.CustomScript)
            return new(LocalAutomationCommandStatus.Invalid, ScriptOnlyReason, null, state);
        // Bind the script-kind check to the requested revision. The store rechecks this version
        // atomically: a concurrent replacement must never turn a script-only request into a built-in run.
        if (definition.Version != request.ExpectedVersion)
            return new(LocalAutomationCommandStatus.Conflict, LocalAutomationContract.ExpectedVersionConflictReasonCode, null, state);
        // The store owns actor/idempotency validation, atomic version/availability checks, queue bounds and replay.
        return await occurrences.RunNowAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LocalAutomationCommandResult> StopAsync(LocalAutomationScriptStopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!LocalAutomationDefinitionValidator.IsIdentifier(request.DefinitionId, LocalAutomationContract.MaximumDefinitionIdLength) ||
            !LocalAutomationDefinitionValidator.IsText(request.RunKey, 128) || request.ExpectedDefinitionVersion <= 0 ||
            !LocalAutomationDefinitionValidator.IsText(request.Actor, LocalAutomationContract.MaximumActorLength))
            return new(LocalAutomationCommandStatus.Invalid, LocalAutomationContract.InvalidCommandReasonCode, null, state);
        var run = state.Runs.Concat(state.ActiveRuns).FirstOrDefault(item => item.RunKey == request.RunKey && item.DefinitionId == request.DefinitionId);
        if (run?.Occurrence is not { } occurrence || !occurrence.IsValid())
            return new(LocalAutomationCommandStatus.NotFound, RunNotFoundReason, null, state);
        if (occurrence.Definition.TaskKind != LocalAutomationTaskKind.CustomScript)
            return new(LocalAutomationCommandStatus.Invalid, ScriptOnlyReason, null, state);
        if (occurrence.DefinitionVersion != request.ExpectedDefinitionVersion)
            return new(LocalAutomationCommandStatus.Conflict, LocalAutomationContract.ExpectedVersionConflictReasonCode, null, state);
        if (run.Outcome is not (LocalAutomationRunOutcome.Queued or LocalAutomationRunOutcome.Running))
            return new(LocalAutomationCommandStatus.Unchanged, null, null, state);
        // A queued run has not acquired execution authority; never report it stopped or affect a
        // different active task. Running stops are naturally idempotent by immutable run identity.
        if (run.Outcome != LocalAutomationRunOutcome.Running ||
            !await worker.StopScriptRunAsync(run.RunKey, cancellationToken).ConfigureAwait(false))
            return new(LocalAutomationCommandStatus.Conflict, StopUnavailableReason, null, state);
        var settled = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (settled.ActiveRuns.Concat(settled.Runs).Any(item => item.RunKey == run.RunKey && item.Outcome == LocalAutomationRunOutcome.Running))
            return new(LocalAutomationCommandStatus.Conflict, StopUnavailableReason, null, settled);
        return new(LocalAutomationCommandStatus.Applied, null, null, settled);
    }
}
