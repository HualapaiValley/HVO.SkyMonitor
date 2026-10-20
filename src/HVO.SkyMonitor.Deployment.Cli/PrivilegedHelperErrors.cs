namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Preserves a pre-mutation refusal across the narrow sudo helper boundary. Argument/authentication failures keep
/// their existing helper usage status; ordinary execution or write failures never become refusals.
/// </summary>
internal static class PrivilegedHelperErrors
{
    internal static int ExitCode(Exception exception)
        => exception is InstallerException { IsRefusal: true }
            ? (int)InstallerExitCode.Refused
            : (int)InstallerExitCode.Failed;

    internal static void ThrowIfFailed(ProcessResult result, string failureContext)
    {
        if (result.ExitCode == 0)
            return;
        var diagnostic = Redaction.SafeDiagnostic(result.StandardError);
        if (result.ExitCode == (int)InstallerExitCode.Refused)
            throw InstallerException.Refused(diagnostic);
        throw new InstallerException($"{failureContext}: {diagnostic}");
    }
}
