using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>Closed event range, with no exception object, provider text or command payload.</summary>
internal static partial class SpaceWeatherLog
{
    [LoggerMessage(7825, LogLevel.Information, "Space-weather run admitted ({Trigger}, {ProductCount}).")]
    internal static partial void Admitted(ILogger logger, string trigger, int productCount);
    [LoggerMessage(7826, LogLevel.Information, "Space-weather run settled ({Outcome}, {ReasonCode}, {Detail}).")]
    internal static partial void Settled(ILogger logger, string outcome, string reasonCode, string detail);
    [LoggerMessage(7827, LogLevel.Warning, "Space-weather start budget exhausted ({Products}).")]
    internal static partial void Budget(ILogger logger, string products);
    [LoggerMessage(7828, LogLevel.Warning, "Space-weather store unavailable ({ExceptionType}).")]
    internal static partial void Storage(ILogger logger, string exceptionType);
    [LoggerMessage(7829, LogLevel.Information, "Space-weather incomplete startup attempts to settle ({Count}).")]
    internal static partial void Incomplete(ILogger logger, int count);
    [LoggerMessage(7830, LogLevel.Information, "Space-weather refresh requested ({Outcome}).")]
    internal static partial void Refresh(ILogger logger, string outcome);
    [LoggerMessage(7831, LogLevel.Information, "Space-weather resume accepted ({Scope}, {Product}).")]
    internal static partial void Resumed(ILogger logger, string scope, string product);
    [LoggerMessage(7832, LogLevel.Warning, "Space-weather command rejected ({ReasonCode}).")]
    internal static partial void Rejected(ILogger logger, string reasonCode);
    [LoggerMessage(7833, LogLevel.Information, "Space-weather notice acknowledged ({Group}).")]
    internal static partial void Acknowledged(ILogger logger, string group);
    [LoggerMessage(7834, LogLevel.Warning, "Space-weather antiforgery rejected.")]
    internal static partial void Antiforgery(ILogger logger);
}
