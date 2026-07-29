using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace HVO.SkyMonitor.Common.Observability;

internal sealed partial class SanitizedOpenTelemetryLogSink(Serilog.ILogger logger) : ILogEventSink, IDisposable
{
    private static readonly string[] AllowedPropertyNames = ["EventId", "EventName", "SourceContext"];
    private static readonly MessageTemplate SanitizedTemplate =
        new MessageTemplateParser().Parse("{SanitizedMessage}");

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        logger.Write(CreateSanitizedEvent(logEvent));
    }

    internal static LogEvent CreateSanitizedEvent(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var properties = new List<LogEventProperty>
        {
            new("SanitizedMessage", new ScalarValue(
                SanitizeMessage(logEvent.RenderMessage(CultureInfo.InvariantCulture))))
        };
        foreach (var propertyName in AllowedPropertyNames)
        {
            if (logEvent.Properties.TryGetValue(propertyName, out var value))
            {
                properties.Add(new LogEventProperty(propertyName, value));
            }
        }
        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception: null,
            SanitizedTemplate,
            properties,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }

    internal static string SanitizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ForbiddenValue().IsMatch(message) || CredentialValue().IsMatch(message)
            ? "[REDACTED]"
            : message;
    }

    public void Dispose() => (logger as IDisposable)?.Dispose();

    [GeneratedRegex(
        "(^|[^0-9A-Fa-f])[0-9A-Fa-f]{64}([^0-9A-Fa-f]|$)|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}|/(var|run|tmp|app|home|workspaces)/[^\\s]+|://",
        RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenValue();

    [GeneratedRegex(
        "authorization:\\s*(basic|bearer)|set-cookie:|requestverificationtoken|antiforgery|client[_-]?secret|connection[_-]?string|owner[_-]?password|[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialValue();
}
