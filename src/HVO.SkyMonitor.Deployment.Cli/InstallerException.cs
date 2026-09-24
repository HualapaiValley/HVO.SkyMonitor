namespace HVO.SkyMonitor.Deployment;

internal sealed class InstallerException : Exception
{
    public InstallerException()
    {
    }

    public InstallerException(string message) : base(message)
    {
    }

    public InstallerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
