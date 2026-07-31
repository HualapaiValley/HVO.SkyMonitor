using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal sealed class Issue107RuntimeEvidenceCollector : DbCommandInterceptor, ILoggerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> AllowedTagKeys =
        ["operation", "audience", "outcome", "role", "state", "origin", "caller.kind"];
    private readonly ConcurrentBag<LogEvidence> logs = [];
    private readonly ConcurrentBag<MetricEvidence> metrics = [];
    private readonly ConcurrentBag<TraceEvidence> traces = [];
    private readonly ConcurrentBag<SqlEvidence> sql = [];
    private readonly MeterListener meterListener;
    private readonly ActivityListener activityListener;

    internal Issue107RuntimeEvidenceCollector()
    {
        meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name is "HVO.SkyMonitor.LogicHost.OperatorUi"
                    or CentralArtifactRetrievalTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            RecordMetric(instrument, value, tags));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            RecordMetric(instrument, value, tags));
        meterListener.Start();

        activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "HVO.SkyMonitor.LogicHost.OperatorUi",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => traces.Add(new(
                activity.OperationName,
                activity.Kind.ToString(),
                activity.Status.ToString(),
                activity.Duration.TotalMilliseconds,
                activity.Tags
                    .Where(tag => tag.Key == "operator_ui.operation")
                    .ToDictionary(tag => tag.Key, tag => tag.Value ?? string.Empty, StringComparer.Ordinal)))
        };
        ActivitySource.AddActivityListener(activityListener);
    }

    public ILogger CreateLogger(string categoryName) => new EvidenceLogger(categoryName, logs);

    public void Dispose()
    {
        meterListener.Dispose();
        activityListener.Dispose();
        GC.SuppressFinalize(this);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        RecordSql(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        RecordSql(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        RecordSql(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        RecordSql(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        RecordSql(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        RecordSql(command, eventData);
        return ValueTask.FromResult(result);
    }

    internal async Task WriteAsync(string root, string revision, object health)
    {
        Directory.CreateDirectory(root);
        meterListener.RecordObservableInstruments();
        await File.WriteAllLinesAsync(
            Path.Combine(root, "logs.jsonl"),
            logs.OrderBy(item => item.EventId).Select(item => JsonSerializer.Serialize(item))).ConfigureAwait(false);
        await WriteJsonAsync("metrics.json", new
        {
            Schema = "hvo-logichost-ui-107-metrics-v1",
            Revision = revision,
            Measurements = metrics
        }).ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            Path.Combine(root, "traces.jsonl"),
            traces.Select(item => JsonSerializer.Serialize(item))).ConfigureAwait(false);
        await WriteJsonAsync("sql.json", new
        {
            Schema = "hvo-logichost-ui-107-sql-v1",
            Revision = revision,
            Commands = sql
        }).ConfigureAwait(false);
        await WriteJsonAsync("health.json", health).ConfigureAwait(false);
        var expectedFixtureWarnings = logs.Count(IsExpectedFixtureHealthWarning);
        var unexpectedSevereLogs = logs.Count(item =>
            !IsExpectedFixtureHealthWarning(item)
            && item.EventId is < 7500 or > 7508
            && item.Level is "Warning" or "Error" or "Critical");
        var signalPayload = JsonSerializer.Serialize(new { Logs = logs, Metrics = metrics, Traces = traces });
        var privacyFindings = FindPrivacyViolations(signalPayload);
        await WriteJsonAsync("cardinality-leak-scan.json", new
        {
            Schema = "hvo-logichost-ui-107-cardinality-leak-scan-v1",
            Revision = revision,
            ExpectedFixtureWarnings = expectedFixtureWarnings,
            UnexpectedSevereLogs = unexpectedSevereLogs,
            MetricSeries = metrics
                .GroupBy(item => new
                {
                    item.Instrument,
                    Tags = string.Join('|', item.Tags.OrderBy(tag => tag.Key).Select(tag => $"{tag.Key}={tag.Value}"))
                })
                .GroupBy(item => item.Key.Instrument)
                .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal),
            ForbiddenValueMatches = privacyFindings.Count,
            Findings = privacyFindings,
            Passed = unexpectedSevereLogs == 0 && privacyFindings.Count == 0
        }).ConfigureAwait(false);

        async Task WriteJsonAsync(string fileName, object value)
            => await File.WriteAllTextAsync(
                Path.Combine(root, fileName),
                JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
    }

    private static bool IsExpectedFixtureHealthWarning(LogEvidence item)
        => item.EventId == 103
            && item.Source == "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService"
            && item.Level == "Warning";

    private static List<string> FindPrivacyViolations(string payload)
    {
        var findings = new List<string>();
        AddIfMatch(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", "email");
        AddIfMatch(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", "entity-id");
        AddIfMatch(@"(?i)\b(?:https?|minio)://", "url-or-storage-reference");
        AddIfMatch(@"(?i)(?:connectionstrings:|password=|downloadtoken=|invitationtoken=)", "credential-or-token");
        AddIfMatch(@"(?i)(?:/tmp/|/workspaces/|\\AppData\\)", "filesystem-path");
        AddIfMatch(@"(?<![0-9])(?:19\.812345|-155\.412345)(?![0-9])", "exact-coordinate");
        return findings;

        void AddIfMatch(string pattern, string finding)
        {
            if (Regex.IsMatch(payload, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                findings.Add(finding);
            }
        }
    }

    private void RecordMetric<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var safeTags = tags
            .ToArray()
            .Where(tag => AllowedTagKeys.Contains(tag.Key))
            .ToDictionary(
                tag => tag.Key,
                tag => Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.Ordinal);
        metrics.Add(new(
            instrument.Meter.Name,
            instrument.Name,
            instrument.Unit,
            Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
            safeTags));
    }

    private void RecordSql(DbCommand command, CommandExecutedEventData eventData)
    {
        var normalized = string.Join(' ', command.CommandText.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        sql.Add(new(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))),
            command.CommandType.ToString(),
            command.Parameters.Count,
            eventData.Duration.TotalMilliseconds));
    }

    private sealed class EvidenceLogger(string category, ConcurrentBag<LogEvidence> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id is < 7500 or > 7508 && logLevel < LogLevel.Warning) return;
            var values = state as IEnumerable<KeyValuePair<string, object?>>;
            var template = values?.FirstOrDefault(item => item.Key == "{OriginalFormat}").Value?.ToString()
                ?? formatter(state, null);
            var fields = values?
                .Where(item => AllowedTagKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(
                    item => item.Key,
                    item => Convert.ToString(item.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    StringComparer.Ordinal) ?? [];
            sink.Add(new(
                logLevel.ToString(),
                eventId.Id,
                category,
                template,
                exception?.GetType().FullName,
                fields));
        }
    }

    private sealed record LogEvidence(
        string Level,
        int EventId,
        string Source,
        string Template,
        string? ExceptionType,
        IReadOnlyDictionary<string, string> Fields);

    private sealed record MetricEvidence(
        string Meter,
        string Instrument,
        string? Unit,
        double Value,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record TraceEvidence(
        string Name,
        string Kind,
        string Status,
        double DurationMilliseconds,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record SqlEvidence(
        string NormalizedCommandSha256,
        string CommandType,
        int ParameterCount,
        double DurationMilliseconds);
}
