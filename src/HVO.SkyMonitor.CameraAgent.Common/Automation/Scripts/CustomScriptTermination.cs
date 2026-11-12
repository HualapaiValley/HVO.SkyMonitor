namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>First exit/termination decision wins, independently of redirected stream drain.</summary>
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

    internal string Status(int exitCode)
    {
        lock (_sync)
        {
            return _cause is null or "exited" ? exitCode == 0 ? "completed" : "process-failed" : _cause;
        }
    }
}
