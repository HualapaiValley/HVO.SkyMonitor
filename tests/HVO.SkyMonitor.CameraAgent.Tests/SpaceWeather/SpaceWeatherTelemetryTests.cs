using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SpaceWeatherTelemetryTests
{
    [TestMethod]
    public async Task EveryInstrumentUsesClosedTagsAndGaugeCallbacksNeverTouchStorage()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var observed = new List<(string Name, string Key, string Value)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == SpaceWeatherTelemetry.MeterName) current.EnableMeasurementEvents(instrument);
        };
        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            names.Add(instrument.Name);
            foreach (var tag in tags) observed.Add((instrument.Name, tag.Key, tag.Value?.ToString() ?? "null"));
        }
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Record(instrument, tags));
        listener.Start();
        await host.RunAsync().ConfigureAwait(false);
        var view = await host.ViewAsync().ConfigureAwait(false);
        foreach (var outcome in Enum.GetValues<SwpcFetchOutcome>())
            host.Telemetry.RecordAttempt(new(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, null, Guid.NewGuid(), outcome,
                SwpcPolicy.OutcomeToken(outcome), [], [new("provider/alerts/format-drift", "new", null), new("provider/alerts/record-drift", "new", null), new("ALTZZZ/1", "escalated", null)], view, SwpcWriteStatus.Applied));
        foreach (var outcome in new[] { LocalAutomationRunOutcome.Succeeded, LocalAutomationRunOutcome.Skipped, LocalAutomationRunOutcome.Failed })
            host.Telemetry.RecordRun(outcome);
        host.Store.ReadFailure = new SwpcStorageException("Gauges must never read this.");
        host.Sqlite.Dispose();
        listener.RecordObservableInstruments();
        var prefix = "skymonitor.cameraagent.space_weather.";
        string[] expected = ["attempts", "fetch_duration", "decoded_bytes", "attention_raised", "runs", "retention_deleted", "product_age_seconds", "active_notices", "halted", "store_bytes"];
        CollectionAssert.AreEquivalent(expected, names.Select(name => name[prefix.Length..]).ToArray());
        foreach (var item in observed)
        {
            var instrument = item.Name[prefix.Length..];
            string[] allowed = (instrument, item.Key) switch
            {
                ("attempts", "product") or ("fetch_duration", "product") or ("decoded_bytes", "product") or ("product_age_seconds", "product") => Enum.GetValues<SwpcProduct>().Select(static p => p.Token()).ToArray(),
                ("attempts", "outcome") => Enum.GetValues<SwpcFetchOutcome>().Select(SwpcPolicy.OutcomeToken).ToArray(),
                ("attention_raised", "kind") => ["new", "escalated", "format-drift", "record-drift"],
                ("runs", "outcome") => ["Succeeded", "Skipped", "Failed"],
                ("retention_deleted", "kind") => ["groups", "versions", "snapshots", "raw-bytes"],
                ("active_notices", "family") => Enum.GetValues<SpaceWeatherFamily>().Select(static p => p.Token()).ToArray(),
                ("halted", "scope") => ["provider", "product"],
                _ => throw new AssertFailedException("Unexpected instrument tag: " + instrument + "/" + item.Key)
            };
            CollectionAssert.Contains(allowed, item.Value);
        }
    }

    [TestMethod]
    public async Task HeadersValidatorsQueriesAndMessageTextNeverAppearInLogsDetailOrDiagnostics()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        var body = Encoding.UTF8.GetBytes(SwpcStoreTests.Message(detail: "message-redaction-sentinel"));
        using var handler = new SwpcStubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            response.Headers.TryAddWithoutValidation("Authorization", "authorization-redaction-sentinel");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "cookie-redaction-sentinel");
            response.Headers.TryAddWithoutValidation("x-amz-cf-id", "cdn-redaction-sentinel");
            response.Headers.TryAddWithoutValidation("via", "proxy-redaction-sentinel");
            response.Headers.TryAddWithoutValidation("ETag", "validator-redaction-sentinel");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new SwpcClient(http, host.Clock, SwpcSource.UserAgent);
        host.Client.Fetch = (product, ct) => client.FetchAsync(product, null, ct);
        var result = await host.RunAsync().ConfigureAwait(false);
        var diagnostics = await host.Store.ReadDiagnosticsAsync(new(host.Clock.GetUtcNow(), null, null, 200), default).ConfigureAwait(false);
        var text = string.Join("\n", host.Logs.Select(static log => log.Text)) + result.Detail + JsonSerializer.Serialize(diagnostics);
        foreach (var secret in new[] { "message-redaction-sentinel", "authorization-redaction-sentinel", "cookie-redaction-sentinel",
            "cdn-redaction-sentinel", "proxy-redaction-sentinel", "validator-redaction-sentinel", "?token=", "services.swpc.noaa.gov" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(1, handler.Requests.Count);
    }
}
