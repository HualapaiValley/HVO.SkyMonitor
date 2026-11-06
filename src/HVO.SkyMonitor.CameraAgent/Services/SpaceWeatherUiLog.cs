namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The operator UI's closed event range, after the space-weather core's 7825-7834. Events carry exception type names
/// and fixed tokens only: never an exception object or message, notice text, URL, header, actor or path. 7839 is
/// reserved and unused.
/// </summary>
internal static partial class SpaceWeatherUiLog
{
    [LoggerMessage(7835, LogLevel.Warning, "Space-weather UI read failed ({ExceptionType}).")]
    internal static partial void ReadFailed(ILogger logger, string exceptionType);

    [LoggerMessage(7836, LogLevel.Warning, "Space-weather UI command failed ({Command}, {ExceptionType}).")]
    internal static partial void CommandFailed(ILogger logger, string command, string exceptionType);

    [LoggerMessage(7837, LogLevel.Information, "Space-weather UI access denied ({Operation}).")]
    internal static partial void AccessDenied(ILogger logger, string operation);

    [LoggerMessage(7838, LogLevel.Warning, "Space-weather UI projection inconsistent after retry.")]
    internal static partial void Inconsistent(ILogger logger);
}
