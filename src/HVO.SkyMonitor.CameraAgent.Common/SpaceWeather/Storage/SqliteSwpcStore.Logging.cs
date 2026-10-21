using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    [LoggerMessage(EventId = 7810, Level = LogLevel.Information, Message = "Space-weather SQLite store initialized at schema {SchemaVersion}.")]
    private static partial void StoreInitialized(ILogger logger, int schemaVersion);

    [LoggerMessage(EventId = 7812, Level = LogLevel.Warning, Message = "Space-weather completion projection failed with type {ExceptionType}.")]
    private static partial void ProjectionFailed(ILogger logger, string exceptionType);
}
