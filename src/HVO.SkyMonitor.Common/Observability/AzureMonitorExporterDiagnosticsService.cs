using System.Diagnostics.Tracing;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Common.Observability;

/// <summary>
/// Listens to the Azure Monitor OpenTelemetry exporter EventSource and forwards diagnostic events to ILogger when enabled.
/// </summary>
public sealed class AzureMonitorExporterDiagnosticsService(ILogger<AzureMonitorExporterDiagnosticsService> logger)
    : IHostedService, IDisposable
{
    private readonly ILogger<AzureMonitorExporterDiagnosticsService> _logger = logger;
    private AzureMonitorExporterEventListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new AzureMonitorExporterEventListener(_logger);
        Log.AzureMonitorDiagnosticsEnabled(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
    }

    private sealed class AzureMonitorExporterEventListener(ILogger logger) : EventListener
    {
        private readonly ILogger _logger = logger;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource?.Name == "OpenTelemetry-AzureMonitor-Exporter")
            {
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData is null)
            {
                return;
            }

            var message = eventData.Message;
            if (string.IsNullOrWhiteSpace(message) && eventData.Payload is { Count: > 0 })
            {
                message = string.Join(" | ", eventData.Payload.Select(payload => payload?.ToString()).Where(payload => !string.IsNullOrWhiteSpace(payload)));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = "(no payload)";
            }

            Log.AzureMonitorExporterEvent(_logger, eventData.EventId, message);
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Azure Monitor exporter diagnostics listener enabled.")]
    public static partial void AzureMonitorDiagnosticsEnabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AzureMonitorExporter[{EventId}] {Message}")]
    public static partial void AzureMonitorExporterEvent(ILogger logger, int eventId, string message);
}
