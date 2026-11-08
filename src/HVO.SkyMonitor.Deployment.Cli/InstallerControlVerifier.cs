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
/// A missing acknowledgement never authorizes a second restart. Failure/cancellation never implicitly resumes capture.
/// </summary>
internal sealed class InstallerControlVerifier(IInstallerControlClient client,
    Func<CameraAgentControlReceipt, Task> persist, TimeProvider timeProvider, InstallerControlBudgets? budgets = null)
{
    private readonly InstallerControlBudgets _budgets = budgets ?? InstallerControlBudgets.Default;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Installer-owned terminal evidence must survive transport faults and CameraAgent shutdown.")]
    public async Task<CameraAgentControlReceipt> ExecuteAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Request.IsValid() || receipt.Phase != CameraAgentControlPhase.Queued)
            throw new ArgumentException("Only a queued immutable lifecycle request may execute.", nameof(receipt));
        var stage = "lifecycle";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initial = await ReadAsync(receipt, cancellationToken).ConfigureAwait(false);
            ThrowIfCancelled(initial);
            if (initial.ProcessId == Guid.Empty || !initial.Initialized || initial.CaptureVersion != receipt.Request.ExpectedCaptureVersion ||
                initial.CaptureState is not ("Running" or "Paused"))
                return await FinishAsync(receipt, CameraAgentControlPhase.Failed, "lifecycle.capture-conflict").ConfigureAwait(false);
            if (receipt.Request.Kind == CameraAgentControlKind.Restart &&
                (!initial.Supervised || !initial.RestartWindowSafe))
                return await FinishAsync(receipt, CameraAgentControlPhase.Skipped,
                    initial.Supervised ? "restart.capture-window" : "restart.unsupervised").ConfigureAwait(false);

            receipt = receipt with { OriginalProcessId = initial.ProcessId, ResumeAfterVerification = initial.CaptureState == "Running" };
            receipt = await AdvanceAsync(receipt, CameraAgentControlPhase.Draining, "lifecycle.draining").ConfigureAwait(false);
            await ReadAsync(receipt, cancellationToken).ConfigureAwait(false); // publish before issuing the pause
            stage = "drain";
            var drained = await BoundedAsync(token => client.DrainAsync(receipt.Request, token), _budgets.Drain, cancellationToken)
                .ConfigureAwait(false);
            if (!Drained(drained)) throw new InstallerException("The lifecycle boundary is not drained.");
            receipt = receipt with { PausedVersion = drained.CaptureVersion, DrainedCaptureSequence = drained.CaptureSequence };
            if (receipt.Request.Kind == CameraAgentControlKind.Drain)
                return await FinishAsync(receipt, CameraAgentControlPhase.Succeeded, "lifecycle.drained-paused").ConfigureAwait(false);

            var beforeRestart = await ReadAsync(receipt, cancellationToken).ConfigureAwait(false);
            ThrowIfCancelled(beforeRestart);
            if (!beforeRestart.RestartWindowSafe || beforeRestart.CaptureState != "Paused" ||
                beforeRestart.CaptureVersion != drained.CaptureVersion || beforeRestart.ProcessId != initial.ProcessId)
                return await FinishAsync(receipt, CameraAgentControlPhase.Failed, "restart.boundary-changed").ConfigureAwait(false);
            var verificationDeadline = timeProvider.GetUtcNow() + _budgets.Verify;
            receipt = receipt with { VerificationDeadlineUtc = verificationDeadline };
            receipt = await AdvanceAsync(receipt, CameraAgentControlPhase.Restarting, "restart.requesting").ConfigureAwait(false);
            ThrowIfCancelled(await ReadAsync(receipt, cancellationToken).ConfigureAwait(false));
            stage = "restart";
            try
            {
                await BoundedAsync(async token => { await client.RestartAsync(receipt, token).ConfigureAwait(false); return true; },
                    _budgets.Read, cancellationToken).ConfigureAwait(false);
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
            while (timeProvider.GetUtcNow() < verificationDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = verificationDeadline - timeProvider.GetUtcNow();
                try
                {
                    var returned = await BoundedAsync(token => client.ExchangeAsync(receipt, token),
                        Min(_budgets.Read, remaining), cancellationToken).ConfigureAwait(false);
                    ThrowIfCancelled(returned);
                    if (returned.ProcessId != initial.ProcessId && returned.Initialized)
                    {
                        if (returned.ProcessId == Guid.Empty || returned.CaptureState != "Paused" || returned.CaptureVersion != drained.CaptureVersion)
                            return await FinishAsync(receipt, CameraAgentControlPhase.Failed, "restart.pause-not-preserved").ConfigureAwait(false);
                        var verified = await BoundedAsync(client.ConfirmDrainedAsync,
                            Min(_budgets.Read, verificationDeadline - timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                        if (verified.CaptureVersion != drained.CaptureVersion || verified.CaptureSequence < drained.CaptureSequence)
                            return await FinishAsync(receipt, CameraAgentControlPhase.Failed, "restart.continuity-failed").ConfigureAwait(false);
                        if (!Drained(verified)) throw new HttpRequestException("CameraAgent queue observations are not ready for restart verification.");
                        receipt = receipt with { VerifiedProcessId = returned.ProcessId };
                        await persist(receipt).ConfigureAwait(false);
                        ThrowIfCancelled(await ReadAsync(receipt, cancellationToken).ConfigureAwait(false));
                        // The exact pause version fences a later operator command. An originally paused agent stays paused.
                        if (receipt.ResumeAfterVerification)
                        {
                            await BoundedAsync(async token => { await client.ResumeAsync(receipt, token).ConfigureAwait(false); return true; },
                                Min(_budgets.Read, verificationDeadline - timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                        }
                        return await FinishAsync(receipt, CameraAgentControlPhase.Succeeded,
                            receipt.ResumeAfterVerification ? "restart.verified-resumed" : "restart.verified-paused").ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException) { }
                catch (TimeoutException) { }
                remaining = verificationDeadline - timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(Min(_budgets.Poll, remaining), timeProvider, cancellationToken).ConfigureAwait(false);
            }
            return await FinishAsync(receipt, CameraAgentControlPhase.Failed, "restart.verification-timeout").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(receipt, CameraAgentControlPhase.Cancelled, "lifecycle.cancelled-check-capture").ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await FinishAsync(receipt, CameraAgentControlPhase.Failed, stage == "drain" ? "drain.timeout" : "lifecycle.timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await FinishAsync(receipt, CameraAgentControlPhase.Failed, stage switch
            {
                "drain" => "drain.failed-check-capture",
                "verify" => "restart.verification-failed",
                _ => "lifecycle.failed-check-capture"
            }).ConfigureAwait(false);
        }
    }

    private Task<CameraAgentControlSnapshot> ReadAsync(CameraAgentControlReceipt receipt, CancellationToken token) =>
        BoundedAsync(cancellation => client.ExchangeAsync(receipt, cancellation), _budgets.Read, token);

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
    private async Task<CameraAgentControlReceipt> FinishAsync(CameraAgentControlReceipt receipt, CameraAgentControlPhase phase, string code)
    {
        var terminal = await AdvanceAsync(receipt, phase, code).ConfigureAwait(false);
        try { await ReadAsync(terminal, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* Durable installer evidence is retained independently of the projection. */ }
        return terminal;
    }

    private static void ThrowIfCancelled(CameraAgentControlSnapshot snapshot)
    {
        if (snapshot.CancelRequested) throw new OperationCanceledException();
    }

    private static bool Drained(LifecycleContinuity state) => state.CaptureInitialized && state.QueuesObserved && state.CaptureState == "Paused" &&
        state.CaptureVersion >= 0 && state.CaptureSequence >= 0 &&
        state.RawPending == 0 && state.RawLeased == 0 && state.LanePending == 0 && state.LaneLeased == 0 &&
        state.ProcessingPending == 0 && state.ProcessingLeased == 0 && state.OutboxPending == 0 && state.OutboxLeased == 0;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
