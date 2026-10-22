namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>
/// The exit codes of <c>hvo-sky</c>. They are the installer's (install contract section 5) wherever the meanings
/// overlap, which is every code: scripts may rely on them, docs/runbooks/hvo-sky.md lists them, and a test pins each
/// value. With <c>--json</c>, the error document's <c>kind</c> says which case of a code it was.
/// </summary>
internal enum SkyExitCode
{
    /// <summary>The command did what it was asked, or there was nothing to change.</summary>
    Success = 0,

    /// <summary>
    /// Something failed that re-running may get past: CameraAgent could not be reached or did not answer in time,
    /// answered with a server error or with an answer that could not be read, did not come back after a restart, or a
    /// change of several groups stopped after an earlier group was saved.
    /// </summary>
    Failed = 1,

    /// <summary>
    /// The command line was not valid (an unknown command or option, a missing or malformed value, a value outside the
    /// catalogue's rules, or a credential given on it), or a question had no one to answer it.
    /// </summary>
    Usage = 2,

    /// <summary>
    /// Refused, and nothing was changed: there is no address or API key, the credentials file cannot be used,
    /// CameraAgent did not accept the key or its access (HTTP 401 or 403), or it refused the request (a version
    /// conflict, a read-only setting, a value it does not accept, or a state that does not allow it).
    /// </summary>
    Refused = 3,

    /// <summary>The command was interrupted (Ctrl+C, SIGTERM, or the terminal closing) before it finished.</summary>
    Interrupted = 130
}
