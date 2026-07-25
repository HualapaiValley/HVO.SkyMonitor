namespace HVO.SkyMonitor.CameraAgent.Common.Scheduling;

public sealed class CaptureProfileCompatibilityException : Exception
{
    public CaptureProfileCompatibilityException()
    {
    }

    public CaptureProfileCompatibilityException(string message) : base(message)
    {
    }

    public CaptureProfileCompatibilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
