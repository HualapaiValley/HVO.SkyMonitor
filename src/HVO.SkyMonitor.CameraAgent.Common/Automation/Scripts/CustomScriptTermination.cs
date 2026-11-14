using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>
/// Latches the first exit/termination cause before drain. Labels follow the launch phase, and excess-byte accounting
/// is evaluated at settlement.
/// </summary>
/// <remarks>
/// The latching caller performs the kill itself, outside the latch, so a later timeout, stop or host-stop callback
/// returns at once. A kill never throws into a cancellation callback, including a timer thread. A kill that fails while
/// the tree is still live is recorded, and settlement retries it once.
/// </remarks>
internal sealed class CustomScriptTermination(Func<bool> hasExited, Action kill)
{
    internal const string KillFailed = "kill-failed";
    private readonly object _sync = new();
    private string? _cause;
    private bool _verified;
    private bool _killFailed;
    private Task _kill = Task.CompletedTask;

    /// <summary>The kernel reported this launch's budgets: from here an exit belongs to the script, not the launcher.</summary>
    internal void Verified()
    {
        lock (_sync) { _verified = true; }
    }

    internal void Exited()
    {
        lock (_sync) { _cause ??= "exited"; }
    }

    internal void Stop(string cause)
    {
        TaskCompletionSource attempt;
        lock (_sync)
        {
            if (_cause is not null) return;
            // The native exit can precede its managed notification and pipe drain.
            _cause = hasExited() ? "exited" : cause;
            if (_cause == "exited") return;
            attempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _kill = attempt.Task;
        }
        try { TryKill(); }
        finally { attempt.SetResult(); }
    }

    /// <summary>
    /// Waits for an in-flight callback kill, then retries a failed one once. A live tree with no latched cause is
    /// stopped under its phase's label. A tree that survives the retry is left to the scope's runtime ceiling.
    /// </summary>
    internal async Task SettleAsync()
    {
        Task pending;
        lock (_sync) { pending = _kill; }
        await pending.ConfigureAwait(false);
        if (hasExited()) return;
        bool retry;
        string fallback;
        lock (_sync)
        {
            retry = _killFailed;
            fallback = _verified ? "process-failed" : "launch-failed";
        }
        if (retry) TryKill();
        else Stop(fallback);
    }

    internal string Status(int exitCode, bool outputLimitExceeded = false)
    {
        lock (_sync)
        {
            // The tree outlived a termination decision; that outranks the cause that made it.
            if (_killFailed) return KillFailed;
            // Before budget verification no policy has been written and no script has run: the exit is the launcher's.
            var cause = _cause is null or "exited" ? _verified ? null : "launch-failed" : _cause;
            // Output is a byte-budget failure even when native exit precedes redirected EOF. A previously latched
            // live cancellation, timeout or launch failure still owns its cause; launcher output is not script output.
            if (outputLimitExceeded && cause is not ("cancelled" or "timeout" or "launch-failed")) return "output-limit";
            return cause ?? (exitCode == 0 ? "completed" : "process-failed");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Kills run inside cancellation callbacks, including timer threads; a failure is recorded, never thrown into the host.")]
    private void TryKill()
    {
        try { kill(); }
        catch (Exception) when (hasExited())
        {
            // The tree exited while the signal was being delivered.
        }
        catch (Exception)
        {
            lock (_sync) { _killFailed = true; }
        }
    }
}
