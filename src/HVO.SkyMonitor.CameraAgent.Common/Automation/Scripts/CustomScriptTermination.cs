namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>Latches exit/termination decisions before drain; excess-byte accounting is evaluated at settlement.</summary>
internal sealed class CustomScriptTermination(Func<bool> hasExited, Action kill)
{
    private readonly object _sync = new();
    private string? _cause;

    internal void Exited()
    {
        lock (_sync) { _cause ??= "exited"; }
    }

    internal void Stop(string cause)
    {
        lock (_sync)
        {
            if (_cause is not null) return;
            // The native exit can precede its managed notification and pipe drain.
            _cause = hasExited() ? "exited" : cause;
            if (_cause != "exited") kill();
        }
    }

    internal string Status(int exitCode, bool outputLimitExceeded = false)
    {
        lock (_sync)
        {
            // Output is a byte-budget failure even when native exit precedes redirected EOF.
            // A previously latched live cancellation/timeout still owns its termination cause.
            if (outputLimitExceeded && _cause is not ("cancelled" or "timeout")) return "output-limit";
            return _cause is null or "exited" ? exitCode == 0 ? "completed" : "process-failed" : _cause;
        }
    }
}
