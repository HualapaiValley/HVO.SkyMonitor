using System.Diagnostics;
using HVO.SkyMonitor.Common.Observability;
using Serilog.Events;
using Serilog.Parsing;

namespace HVO.SkyMonitor.Tests.Observability;

[TestClass]
[TestCategory("Unit")]
public sealed class SanitizedOpenTelemetryLogSinkTests
{
    [TestMethod]
    public void SanitizationRetainsLowCardinalityMessagesAndRedactsSensitiveValues()
    {
        Assert.AreEqual(
            "Capture processing completed.",
            SanitizedOpenTelemetryLogSink.SanitizeMessage("Capture processing completed."));
        foreach (var sensitive in new[]
        {
            $"Identity {new string('A', 64)}",
            "Candidate 12345678-1234-1234-1234-1234567890AB",
            "Path /var/lib/hvo/private.json",
            "Endpoint http://central.example.test",
            "Authorization: Bearer secret",
            "Owner operator@example.test"
        })
        {
            Assert.AreEqual("[REDACTED]", SanitizedOpenTelemetryLogSink.SanitizeMessage(sensitive));
        }

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var original = new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Warning,
            new InvalidOperationException("secret"),
            new MessageTemplateParser().Parse("Authorization: Bearer {Secret}"),
            [
                new LogEventProperty("Secret", new ScalarValue("credential")),
                new LogEventProperty("EventId", new ScalarValue(2032)),
                new LogEventProperty("EventName", new ScalarValue("CaptureRetry")),
                new LogEventProperty("SourceContext", new ScalarValue("HVO.SkyMonitor.Capture"))
            ],
            traceId,
            spanId);

        var sanitized = SanitizedOpenTelemetryLogSink.CreateSanitizedEvent(original);

        Assert.IsNull(sanitized.Exception);
        Assert.AreEqual(traceId, sanitized.TraceId);
        Assert.AreEqual(spanId, sanitized.SpanId);
        CollectionAssert.AreEquivalent(
            new[] { "SanitizedMessage", "EventId", "EventName", "SourceContext" },
            sanitized.Properties.Keys.ToArray());
        Assert.AreEqual("[REDACTED]", sanitized.Properties["SanitizedMessage"].ToString().Trim('"'));

        foreach (var (eventId, operation, result, retained) in new[]
        {
            (2048, "commit", "success", true),
            (2048, "checkpoint", "failure", true),
            (2048, "initialization", "success", false),
            (2048, "commit", "unbounded", false),
            (2042, "commit", "success", false)
        })
        {
            var sqliteEvent = new LogEvent(
                DateTimeOffset.UnixEpoch,
                LogEventLevel.Warning,
                exception: null,
                new MessageTemplateParser().Parse("SQLite {Operation} {Result}"),
                [
                    new LogEventProperty("EventId", new ScalarValue(eventId)),
                    new LogEventProperty("Operation", new ScalarValue(operation)),
                    new LogEventProperty("Result", new ScalarValue(result))
                ]);

            var sanitizedSqliteEvent = SanitizedOpenTelemetryLogSink.CreateSanitizedEvent(sqliteEvent);

            Assert.AreEqual(retained, sanitizedSqliteEvent.Properties.ContainsKey("Operation"));
            Assert.AreEqual(retained, sanitizedSqliteEvent.Properties.ContainsKey("Result"));
            if (retained)
            {
                Assert.AreEqual(operation, ((ScalarValue)sanitizedSqliteEvent.Properties["Operation"]).Value);
                Assert.AreEqual(result, ((ScalarValue)sanitizedSqliteEvent.Properties["Result"]).Value);
            }
        }
    }
}
