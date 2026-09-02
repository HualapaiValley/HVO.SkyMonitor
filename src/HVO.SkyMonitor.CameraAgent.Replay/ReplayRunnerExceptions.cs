namespace HVO.SkyMonitor.CameraAgent.Replay;

public sealed class LocalReplayRunnerUnavailableException : Exception
{
    public LocalReplayRunnerUnavailableException()
    {
    }

    public LocalReplayRunnerUnavailableException(string message)
        : base(message)
    {
    }

    public LocalReplayRunnerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LocalReplayRunnerUnavailableException(
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        bool requestAccepted = false)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
        RequestAccepted = requestAccepted;
    }

    public TimeSpan? RetryAfter { get; }

    public bool RequestAccepted { get; }
}

public class LocalReplayRunnerProtocolException : Exception
{
    public LocalReplayRunnerProtocolException()
    {
    }

    public LocalReplayRunnerProtocolException(string message)
        : base(message)
    {
    }

    public LocalReplayRunnerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LocalReplayRunnerOutputValidationException : Exception
{
    public LocalReplayRunnerOutputValidationException()
    {
    }

    public LocalReplayRunnerOutputValidationException(string message)
        : base(message)
    {
    }

    public LocalReplayRunnerOutputValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
