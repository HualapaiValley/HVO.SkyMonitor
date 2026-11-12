using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Fixed-purpose transport to the installed instance. Credentials and target never come from an intent.</summary>
internal interface IInstallerControlClient
{
    Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken);
    Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken cancellationToken);
    Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken);
    Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken);
}

internal sealed record InstallerControlBudgets(TimeSpan Drain, TimeSpan Verify, TimeSpan Read, TimeSpan Poll)
{
    internal static InstallerControlBudgets Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
}

/// <summary>
/// Runs in Deployment.Cli, outside CameraAgent's lifetime. Every effect has a prior durable journal boundary.
/// A missing acknowledgement never authorizes a second restart. Pre-stop recovery resumes only the latest owned pause.
/// </summary>
internal sealed class InstallerControlVerifier(IInstallerControlClient client,
    Func<CameraAgentControlReceipt, Task> persist, TimeProvider timeProvider, InstallerControlBudgets? budgets = null)
{
    private readonly InstallerControlBudgets _budgets = budgets ?? InstallerControlBudgets.Default;
    private CameraAgentControlCancellation? _cancellation;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Installer-owned terminal evidence must survive transport faults and CameraAgent shutdown.")]
    public async Task<CameraAgentControlReceipt> ExecuteAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsValid() || receipt.Phase != CameraAgentControlPhase.Queued)
            throw new ArgumentException("Only a queued immutable lifecycle request may execute.", nameof(receipt));
        var restartCallIssued = false;
        Task<CameraAgentControlReceipt> FinishExecutionAsync(CameraAgentControlReceipt current, CameraAgentControlPhase phase, string code) =>
            FinishAsync(current, phase, code, restartDefinitelyNotIssued: !restartCallIssued);
        var stage = "claim";
        // CameraAgent accepts only successors that keep every field it has seen, so no exchange, including the failure and
        // recovery path, may publish a field the journal lacks. A field is adopted only after its journal write succeeds;
        // a failed write is reported as a journal failure, not as the stage that produced the field.
        async Task<CameraAgentControlReceipt> JournalAsync(CameraAgentControlReceipt next)
        {
            var resumeStage = stage;
            stage = "journal";
            await persist(next).ConfigureAwait(false);
            stage = resumeStage;
            return next;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initial = await ReadAsync(receipt, cancellationToken).ConfigureAwait(false);
            ThrowIfCancelled(initial);
            if (initial.ProcessId == Guid.Empty || !initial.Initialized || initial.CaptureVersion != receipt.Request.ExpectedCaptureVersion ||
                initial.CaptureState is not ("Running" or "Paused"))
                return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, "lifecycle.capture-conflict").ConfigureAwait(false);
            if (receipt.Request.Kind == CameraAgentControlKind.Restart &&
                (!initial.Supervised || !initial.RestartWindowSafe))
                return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Skipped,
                    initial.Supervised ? "restart.capture-window" : "restart.unsupervised").ConfigureAwait(false);

            receipt = receipt with { OriginalProcessId = initial.ProcessId, ResumeAfterVerification = initial.CaptureState == "Running" };
            receipt = await AdvanceAsync(receipt, CameraAgentControlPhase.Draining, "lifecycle.draining").ConfigureAwait(false);
            stage = "claim";
            await ReadAsync(receipt, cancellationToken).ConfigureAwait(false); // publish the claim before issuing the pause
            stage = "drain";
            var drained = await BoundedAsync(token => DrainWithHeartbeatAsync(receipt, token), _budgets.Drain, cancellationToken)
                .ConfigureAwait(false);
            if (!Drained(drained)) throw new InstallerException("The lifecycle boundary is not drained.");
            receipt = await JournalAsync(receipt with { PausedVersion = drained.CaptureVersion, DrainedCaptureSequence = drained.CaptureSequence })
                .ConfigureAwait(false);
            if (receipt.Request.Kind == CameraAgentControlKind.Drain)
            {
                // A claimed cancellation is observed at the same post-drain boundary as a Restart's. A standalone Drain never
                // resumes, so capture stays paused either way; an unreadable boundary cannot be reported as an uncancelled drain.
                stage = "drained";
                var afterDrain = await ReadAsync(receipt, cancellationToken).ConfigureAwait(false);
                return afterDrain.CancelRequested
                    ? await FinishExecutionAsync(receipt, CameraAgentControlPhase.Cancelled, "drain.cancelled-paused").ConfigureAwait(false)
                    : await FinishExecutionAsync(receipt, CameraAgentControlPhase.Succeeded, "lifecycle.drained-paused").ConfigureAwait(false);
            }

            stage = "pre-restart";
            var beforeRestart = await ReadAsync(receipt, cancellationToken).ConfigureAwait(false);
            ThrowIfCancelled(beforeRestart);
            if (!beforeRestart.RestartWindowSafe || beforeRestart.CaptureState != "Paused" ||
                beforeRestart.CaptureVersion != drained.CaptureVersion || beforeRestart.ProcessId != initial.ProcessId)
                return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, "restart.boundary-changed").ConfigureAwait(false);
            var verificationStarted = timeProvider.GetTimestamp();
            var verificationDeadline = timeProvider.GetUtcNow() + _budgets.Verify;
            receipt = await JournalAsync(receipt with { VerificationDeadlineUtc = verificationDeadline }).ConfigureAwait(false);
            // Claim cancellation before committing to the potentially ambiguous restart effect.
            ThrowIfCancelled(await ReadAsync(receipt, cancellationToken).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            receipt = receipt with { RestartRequested = true };
            receipt = await AdvanceAsync(receipt, CameraAgentControlPhase.Restarting, "restart.requesting").ConfigureAwait(false);
            stage = "restart-publication";
            await ReadAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            stage = "restart";
            try
            {
                await BoundedAsync(async token =>
                {
                    restartCallIssued = true;
                    await client.RestartAsync(receipt, token).ConfigureAwait(false);
                    return true;
                },
                    _budgets.Read, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InstallerControlRefusalException)
            {
                // The definite refusal authorizes the owned-pause restore; it counts only once journaled. If that write
                // fails, the receipt stays ambiguous and nothing is restored.
                receipt = await JournalAsync(receipt with { RestartRefused = true }).ConfigureAwait(false);
                return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, "restart.refused").ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The process can stop before its response arrives. Verify the durable boundary; never request twice.
            }
            catch (TimeoutException)
            {
                // Same ambiguous acknowledgement rule as a disconnected transport.
            }
            receipt = await AdvanceAsync(receipt, CameraAgentControlPhase.Verifying, "restart.verifying").ConfigureAwait(false);
            stage = "verify";
            while (timeProvider.GetElapsedTime(verificationStarted) < _budgets.Verify)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = _budgets.Verify - timeProvider.GetElapsedTime(verificationStarted);
                try
                {
                    var returned = await BoundedAsync(token => client.ExchangeAsync(receipt, token),
                        Min(_budgets.Read, remaining), cancellationToken).ConfigureAwait(false);
                    ThrowIfCancelled(returned);
                    if (returned.ProcessId != initial.ProcessId && returned.Initialized)
                    {
                        if (returned.ProcessId == Guid.Empty || returned.CaptureState != "Paused" || returned.CaptureVersion != drained.CaptureVersion)
                            return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, "restart.pause-not-preserved").ConfigureAwait(false);
                        var verified = await BoundedAsync(client.ConfirmDrainedAsync,
                            Min(_budgets.Read, _budgets.Verify - timeProvider.GetElapsedTime(verificationStarted)), cancellationToken).ConfigureAwait(false);
                        if (verified.CaptureVersion != drained.CaptureVersion || verified.CaptureSequence < drained.CaptureSequence)
                            return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, "restart.continuity-failed").ConfigureAwait(false);
                        if (!Drained(verified)) throw new HttpRequestException("CameraAgent queue observations are not ready for restart verification.");
                        receipt = receipt with { VerifiedProcessId = returned.ProcessId };
                        await persist(receipt).ConfigureAwait(false);
                        ThrowIfCancelled(await ReadAsync(receipt, cancellationToken).ConfigureAwait(false));
                        // The exact pause version fences a later operator command. An originally paused agent stays paused.
                        if (receipt.ResumeAfterVerification)
                        {
                            receipt = receipt with { ResumeRequested = true };
                            await persist(receipt).ConfigureAwait(false);
                            ThrowIfCancelled(await ReadAsync(receipt, cancellationToken).ConfigureAwait(false));
                            await BoundedAsync(async token => { await client.ResumeAsync(receipt, token).ConfigureAwait(false); return true; },
                                Min(_budgets.Read, _budgets.Verify - timeProvider.GetElapsedTime(verificationStarted)), cancellationToken).ConfigureAwait(false);
                            receipt = receipt with { ResumeConfirmed = true };
                            await persist(receipt).ConfigureAwait(false);
                        }
                        return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Succeeded,
                            receipt.ResumeAfterVerification ? "restart.verified-resumed" : "restart.verified-paused").ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException) when (!receipt.ResumeRequested) { }
                catch (TimeoutException) when (!receipt.ResumeRequested) { }
                remaining = _budgets.Verify - timeProvider.GetElapsedTime(verificationStarted);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(Min(_budgets.Poll, remaining), timeProvider, cancellationToken).ConfigureAwait(false);
            }
            return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, "restart.verification-timeout").ConfigureAwait(false);
        }
        catch (InstallerControlRefusalException) when (stage == "claim")
        {
            // Local cancellation won the serialized unclaimed-to-Draining race; no pause was authorized.
            return await FinishExecutionAsync(receipt with { ResumeAfterVerification = false }, CameraAgentControlPhase.Cancelled,
                "lifecycle.claim-refused").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Cancelled, "lifecycle.cancelled-check-capture").ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, receipt.ResumeRequested ? "restart.resume-unconfirmed" : stage == "drain" ? "drain.timeout" : "lifecycle.timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await FinishExecutionAsync(receipt, CameraAgentControlPhase.Failed, stage switch
            {
                "drain" => "drain.failed-check-capture",
                "verify" => receipt.ResumeRequested ? "restart.resume-unconfirmed" : "restart.verification-failed",
                _ => "lifecycle.failed-check-capture"
            }).ConfigureAwait(false);
        }
    }

    internal Task<CameraAgentControlReceipt> SettleInterruptedAsync(CameraAgentControlReceipt receipt) =>
        FinishAsync(receipt, CameraAgentControlPhase.Failed, "lifecycle.installer-interrupted");

    private async Task<CameraAgentControlSnapshot> ReadAsync(CameraAgentControlReceipt receipt, CancellationToken token)
    {
        var snapshot = await BoundedAsync(cancellation => client.ExchangeAsync(receipt, cancellation), _budgets.Read, token).ConfigureAwait(false);
        _cancellation ??= snapshot.Current?.Cancellation;
        return snapshot;
    }

    private async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (budget <= TimeSpan.Zero) throw new TimeoutException();
        using var timeout = new CancellationTokenSource(budget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try { return await operation(linked.Token).WaitAsync(budget, timeProvider, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException(); }
        finally { await linked.CancelAsync().ConfigureAwait(false); }
    }

    private async Task<CameraAgentControlReceipt> AdvanceAsync(CameraAgentControlReceipt receipt, CameraAgentControlPhase phase, string code)
    {
        var next = receipt with { Phase = phase, Events = [.. receipt.Events, new(phase, timeProvider.GetUtcNow(), code)] };
        await persist(next).ConfigureAwait(false);
        return next;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The journal stays authoritative when CameraAgent is offline; publication is retried by the installer worker.")]
    private async Task<CameraAgentControlReceipt> FinishAsync(CameraAgentControlReceipt receipt, CameraAgentControlPhase phase, string code,
        bool restartDefinitelyNotIssued = false)
    {
        // Only the live execution knows the call was never entered. After a crash, an unmarked durable intent stays ambiguous.
        if (restartDefinitelyNotIssued && receipt.RestartRequested && !receipt.RestartRefused && !receipt.RestartNotIssued)
        {
            receipt = receipt with { RestartNotIssued = true };
            await persist(receipt).ConfigureAwait(false);
        }
        if (phase != CameraAgentControlPhase.Succeeded && receipt.Request.Kind == CameraAgentControlKind.Restart &&
            (receipt.Phase is CameraAgentControlPhase.Draining or CameraAgentControlPhase.Restarting) &&
            !receipt.RestartMayHaveOccurred && receipt.ResumeAfterVerification)
        {
            var restored = await RestoreBeforeRestartAsync(receipt).ConfigureAwait(false);
            receipt = restored.Receipt;
            if (!restored.Settled) return receipt; // Keep recovery eligible until the owned pause and restore settle.
        }
        receipt = receipt with { Cancellation = receipt.Cancellation ?? _cancellation };
        if (receipt.ResumeRequested && !receipt.ResumeConfirmed && phase != CameraAgentControlPhase.Succeeded)
            code = "restart.resume-unconfirmed";
        var terminal = await AdvanceAsync(receipt, phase, code).ConfigureAwait(false);
        try { await ReadAsync(terminal, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* Durable installer evidence is retained independently of the projection. */ }
        return terminal;
    }

    private async Task<(CameraAgentControlReceipt Receipt, bool Settled)> RestoreBeforeRestartAsync(CameraAgentControlReceipt receipt)
    {
        try
        {
            if (receipt.ResumeConfirmed) return (receipt, true);
            var current = await ReadAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            if (!current.Initialized) return (receipt, false);
            if (current.ProcessId != receipt.OriginalProcessId) return (receipt, true);
            // The durable pause outlives a cancelled or disconnected drain request. Its final version is not known yet.
            if (current.CaptureState == "PauseRequested") return (receipt, false);
            if (!receipt.ResumeRequested)
            {
                if (current.CaptureState != "Paused" || current.OwnedPauseVersion != current.CaptureVersion)
                    return (receipt, true);
                // Persist before publishing. A retry uses this same native idempotent resume even if its response was lost.
                var next = receipt with { PausedVersion = current.CaptureVersion, VerifiedProcessId = current.ProcessId, ResumeRequested = true };
                await persist(next).ConfigureAwait(false);
                receipt = next;
            }
            await ReadAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await BoundedAsync(async token => { await client.ResumeAsync(receipt, token).ConfigureAwait(false); return true; },
                    _budgets.Read, CancellationToken.None).ConfigureAwait(false);
            }
            // The shared native lifecycle client wraps transport failures. A definite command refusal must instead
            // preserve the later owner's pause and settle without retrying or losing the journaled resume intent.
            catch (InstallerException exception) when (exception.InnerException is HttpRequestException) { return (receipt, false); }
            catch (InstallerException) { return (receipt, true); }
            var confirmed = receipt with { ResumeConfirmed = true };
            await persist(confirmed).ConfigureAwait(false);
            return (confirmed, true);
        }
        catch (InstallerControlRefusalException) { return (receipt, true); } // A later owner command must remain authoritative.
        catch (HttpRequestException) { return (receipt, false); }
        catch (TimeoutException) { return (receipt, false); }
        catch (OperationCanceledException) { return (receipt, false); }
    }

    private async Task<LifecycleContinuity> DrainWithHeartbeatAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        var drain = client.DrainAsync(receipt.Request, cancellationToken);
        while (!drain.IsCompleted)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(Min(TimeSpan.FromSeconds(10), _budgets.Poll), timeProvider, heartbeat.Token);
            if (await Task.WhenAny(drain, delay).ConfigureAwait(false) == drain)
            {
                await heartbeat.CancelAsync().ConfigureAwait(false);
                break;
            }
            await delay.ConfigureAwait(false);
            // Heartbeats renew observation only. A transport outage must not abandon the authoritative native drain.
            try { await ReadAsync(receipt, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) { }
            catch (TimeoutException) { }
        }
        return await drain.ConfigureAwait(false);
    }

    private void ThrowIfCancelled(CameraAgentControlSnapshot snapshot)
    {
        _cancellation ??= snapshot.Current?.Cancellation;
        if (snapshot.CancelRequested) throw new OperationCanceledException();
    }

    private static bool Drained(LifecycleContinuity state) => state.CaptureInitialized && state.QueuesObserved && state.CaptureState == "Paused" &&
        state.CaptureVersion >= 0 && state.CaptureSequence >= 0 &&
        state.RawPending == 0 && state.RawLeased == 0 && state.LanePending == 0 && state.LaneLeased == 0 &&
        state.ProcessingPending == 0 && state.ProcessingLeased == 0 && state.OutboxPending == 0 && state.OutboxLeased == 0;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
