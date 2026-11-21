namespace HVO.SkyMonitor.AgentCore;

/// <summary>Certifies that a bound capture was refused before shutter start because its grant expired.</summary>
public sealed class CaptureStartWindowClosedException : InvalidOperationException
{
    public CaptureStartWindowClosedException() : base("The bound capture grant closed before shutter start.") { }

    public CaptureStartWindowClosedException(string message) : base(message) { }

    public CaptureStartWindowClosedException(string message, Exception innerException) : base(message, innerException) { }
}
