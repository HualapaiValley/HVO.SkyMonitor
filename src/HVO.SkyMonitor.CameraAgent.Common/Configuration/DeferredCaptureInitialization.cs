using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

internal static class DeferredCaptureInitialization
{
    internal const string FailureReason = "startup-initialization-failed";
    private static readonly Action<ILogger, string, string, Exception?> InitializationFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(7312, "DeferredCaptureInitializationFailed"),
        "Deferred capture initialization {Service} failed ({ErrorType}); capture readiness is unhealthy");

    internal static void Fail(ILogger logger, CaptureLaneState? state, Type service, Exception exception)
    {
        state?.SetUnhealthy(FailureReason, service.Name);
        InitializationFailed(logger, service.Name, exception.GetType().Name, null);
    }

    internal static void Recover(CaptureLaneState? state, Type service)
        => state?.ClearUnhealthy(FailureReason, service.Name);
}
