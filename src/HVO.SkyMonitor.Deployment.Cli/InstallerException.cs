namespace HVO.SkyMonitor.Deployment;

internal sealed class InstallerException : Exception
{
    public InstallerException()
    {
    }

    public InstallerException(string message) : base(message)
    {
    }

    public InstallerException(string message, Exception? innerException) : base(message, innerException)
    {
        IsFormattedDiagnostic = innerException is InstallerException { IsFormattedDiagnostic: true } && message == innerException.Message;
    }

    /// <summary>Whether the message is composed from fixed prose and already formatted untrusted fragments.</summary>
    public bool IsFormattedDiagnostic { get; private init; }

    /// <summary>An operator diagnostic whose untrusted fragments have already been redacted and escaped.</summary>
    public static InstallerException Formatted(string message, Exception? innerException = null)
        => new(message, innerException) { IsFormattedDiagnostic = true };

    /// <summary>A refusal composed from fixed prose and already formatted untrusted fragments.</summary>
    public static InstallerException RefusedFormatted(string message, Exception? innerException = null)
        => new(message, innerException) { IsRefusal = true, IsFormattedDiagnostic = true };

    /// <summary>
    /// Whether the installer refused before it changed anything, which exits 3 rather than 1 (install contract section 5).
    /// Only a gate that decides not to go ahead raises one, through <see cref="Refused(string)"/>; a step that failed part way is
    /// an ordinary failure, which re-running continues.
    /// </summary>
    public bool IsRefusal { get; private init; }

    /// <summary>A refusal: the installer will not do what was asked here, and nothing was changed.</summary>
    public static InstallerException Refused(string message) => new(message) { IsRefusal = true };

    /// <summary>A refusal that keeps the failure which decided it.</summary>
    public static InstallerException Refused(string message, Exception innerException) => new(message, innerException) { IsRefusal = true };
}
