using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RealReceiveAndReconcileEmitBoundedSanitizedRuntimeEvidenceAndPreserveDependencyHealth()
    {
        var metrics = new ConcurrentQueue<RuntimeMetric>();
        var spans = new ConcurrentQueue<string>();
        var spanTagKeys = new ConcurrentQueue<string[]>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ExecutionEvidenceTelemetry.MeterName) { listener.EnableMeasurementEvents(instrument); }
        };
        void Record(Instrument instrument, string value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (metrics.Count >= 512) { return; }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags) { values[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty; }
            metrics.Enqueue(new(instrument.Name, value, values));
        }
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value.ToString(CultureInfo.InvariantCulture), tags));
        meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value.ToString(CultureInfo.InvariantCulture), tags));
        meter.Start();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ExecutionEvidenceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (spans.Count < 64)
                {
                    spans.Enqueue(activity.OperationName);
                    spanTagKeys.Enqueue(activity.TagObjects.Select(tag => tag.Key).ToArray());
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);
        using var logs = new EvidenceLogCapture();
        var root = AssemblyHooks.Fixture.Factory.Services;
        root.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        var setup = await PrepareReconciliationAsync().ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var revision = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), setup.Device);
        using var duplicate = await PostAsync(client, setup.Device, revision).ConfigureAwait(false);
        Assert.IsTrue((await FeedbackAsync(duplicate).ConfigureAwait(false)).Facts.All(f => f.Duplicate));
        using var conflict = await PostAsync(client, setup.Device, GraphExecutionEvidenceJson.Seal(revision with { EvidenceId = Guid.NewGuid() })).ConfigureAwait(false);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.SequenceConflict, (await FeedbackAsync(conflict).ConfigureAwait(false)).Facts.Single().ReasonCode);
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, DateTimeOffset.UtcNow).ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceReconciler>().ReconcileAsync(1).ConfigureAwait(false);
        var next = GraphExecutionEvidenceJson.Seal(revision with { OriginSequence = 4, EvidenceId = Guid.NewGuid() });
        var options = root.GetRequiredService<IOptions<ExecutionEvidenceOptions>>().Value;
        var originalLimit = options.MaximumUnitsPerBinding;
        HttpStatusCode pressureStatus;
        try
        {
            options.MaximumUnitsPerBinding = 3;
            using var pressure = await PostAsync(client, setup.Device, next).ConfigureAwait(false);
            pressureStatus = pressure.StatusCode;
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, pressureStatus);
            Assert.IsNotNull(pressure.Headers.RetryAfter);
        }
        finally { options.MaximumUnitsPerBinding = originalLimit; }
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER [TR_Test_ExecutionEvidenceRuntimeOutage] ON [ImportedExecutionEvidenceUnits] AFTER INSERT AS
            BEGIN
                THROW 51939, 'Injected execution evidence SQL failure.', 1;
            END
            """).ConfigureAwait(false);
        HttpStatusCode outageStatus;
        try
        {
            using var outage = await PostAsync(client, setup.Device, next).ConfigureAwait(false);
            outageStatus = outage.StatusCode;
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, outageStatus);
            Assert.IsNotNull(outage.Headers.RetryAfter);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [TR_Test_ExecutionEvidenceRuntimeOutage]").ConfigureAwait(false); }
        using var recovered = await PostAsync(client, setup.Device, next).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceFactKind.Acknowledged, (await FeedbackAsync(recovered).ConfigureAwait(false)).Facts[^1].Kind);
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        using var healthBody = JsonDocument.Parse(await health.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        var checks = healthBody.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        var databaseHealth = checks.Single(c => c.GetProperty("name").GetString() == "database").GetProperty("status").GetString();
        var storageHealth = checks.Single(c => c.GetProperty("name").GetString() == "object-store").GetProperty("status").GetString();
        Assert.AreEqual("Healthy", databaseHealth);
        Assert.AreEqual("Healthy", storageHealth);
        Assert.IsTrue(metrics.Any(m => m.Name == "hvo.execution_evidence.units" && m.Tags.GetValueOrDefault("outcome") == "accepted"));
        Assert.IsTrue(metrics.Any(m => m.Tags.GetValueOrDefault("outcome") == "duplicate"));
        Assert.IsTrue(metrics.Any(m => m.Tags.GetValueOrDefault("outcome") == "conflict"));
        Assert.IsTrue(metrics.Any(m => m.Tags.GetValueOrDefault("outcome") == "pressure"));
        Assert.IsTrue(metrics.Any(m => m.Tags.GetValueOrDefault("outcome") == "unavailable"));
        Assert.IsTrue(metrics.Any(m => m.Name == "hvo.execution_evidence.observations"));
        Assert.IsTrue(metrics.All(m => m.Tags.Count == 1 && m.Tags.Keys.All(k => k is "outcome" or "state")));
        Assert.IsTrue(spans.Contains("execution-evidence.receive"));
        Assert.IsTrue(spans.Contains("execution-evidence.reconcile"));
        Assert.IsTrue(spanTagKeys.All(keys => keys.All(key => key is "outcome" or "state")));
        Assert.IsTrue(logs.Entries.Any(e => e.EventId == 2481));
        Assert.IsTrue(logs.Entries.Any(e => e.EventId == 2482));
        Assert.IsTrue(logs.Entries.Any(e => e.EventId == 2483));
        var text = string.Join('\n', logs.Entries.Select(e => e.Message));
        foreach (var secretOrIdentity in new[] { setup.Device.DeviceKey, setup.Device.DeviceId, revision.PayloadSha256,
            revision.Origin.IdentitySha256, revision.Origin.AgentInstanceId.ToString(), Encoding.UTF8.GetString(GraphExecutionEvidenceJson.Serialize(revision)) })
        {
            Assert.IsFalse(text.Contains(secretOrIdentity, StringComparison.Ordinal));
        }
        Assert.IsTrue(logs.Entries.All(e => e.Message.Length <= 512 && e.ExceptionType is null));
        TestContext.WriteLine("ExecutionEvidenceRuntimeEvidence=" + JsonSerializer.Serialize(new
        {
            Metrics = metrics.ToArray(),
            Spans = spans.ToArray(),
            Logs = logs.Entries.ToArray(),
            Http = new { Pressure = (int)pressureStatus, SqlOutage = (int)outageStatus, Recovery = (int)recovered.StatusCode, Health = (int)health.StatusCode },
            Health = new { Database = databaseHealth, ObjectStore = storageHealth },
            SensitiveValuesAbsent = true
        }));
    }

    private sealed record RuntimeMetric(string Name, string Value, Dictionary<string, string> Tags);
    private sealed record RuntimeLog(int EventId, string Message, string? ExceptionType);
    private sealed class EvidenceLogCapture : ILoggerProvider
    {
        public ConcurrentQueue<RuntimeLog> Entries { get; } = new();
        private volatile bool _disposed;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this,
            categoryName.StartsWith("HVO.SkyMonitor.LogicHost.Services.ExecutionEvidence", StringComparison.Ordinal));
        public void Dispose() => _disposed = true;
        private sealed class CaptureLogger(EvidenceLogCapture capture, bool relevant) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => relevant && !capture._disposed && logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel) && capture.Entries.Count < 32) { capture.Entries.Enqueue(new(eventId.Id, formatter(state, exception), exception?.GetType().Name)); }
            }
        }
    }
}
