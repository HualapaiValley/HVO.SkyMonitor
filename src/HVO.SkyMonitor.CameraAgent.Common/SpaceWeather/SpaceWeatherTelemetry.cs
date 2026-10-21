using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>Closed metric labels; gauge callbacks use immutable cached facts and never open storage.</summary>
/// <remarks>
/// store_bytes reports retained raw-body bytes, excluding SQLite/index/journal overhead. The frozen
/// health seam exposes that bounded quantity. retention_deleted counts explicit retention only;
/// automatic purge in timed calls exposes no deletion totals. Physical database growth is measured separately.
/// </remarks>
public sealed class SpaceWeatherTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.SpaceWeather";
    private const string Prefix = "skymonitor.cameraagent.space_weather.";
    private readonly Meter meter = new(MeterName);
    private readonly TimeProvider clock;
    private readonly Counter<long> attempts;
    private readonly Histogram<double> duration;
    private readonly Histogram<long> bytes;
    private readonly Counter<long> attention;
    private readonly Counter<long> runs;
    private readonly Counter<long> retention;
    private volatile Observation? observation;

    public SpaceWeatherTelemetry(TimeProvider clock)
    {
        this.clock = clock;
        attempts = meter.CreateCounter<long>(Prefix + "attempts", "{attempt}");
        duration = meter.CreateHistogram<double>(Prefix + "fetch_duration", "ms");
        bytes = meter.CreateHistogram<long>(Prefix + "decoded_bytes", "By");
        attention = meter.CreateCounter<long>(Prefix + "attention_raised", "{notice}");
        runs = meter.CreateCounter<long>(Prefix + "runs", "{run}");
        retention = meter.CreateCounter<long>(Prefix + "retention_deleted", "{item}");
        meter.CreateObservableGauge(Prefix + "product_age_seconds", Ages, "s");
        meter.CreateObservableGauge(Prefix + "active_notices", Active, "{notice}");
        meter.CreateObservableGauge(Prefix + "halted", Halts, "{halt}");
        meter.CreateObservableGauge(Prefix + "store_bytes", StoreBytes, "By", "Retained raw-body bytes, excluding SQLite overhead.");
    }

    internal void RecordFetch(SwpcProduct product, TimeSpan elapsed, long decodedBytes)
    {
        var tag = new KeyValuePair<string, object?>("product", product.Token());
        duration.Record(Math.Max(0, elapsed.TotalMilliseconds), tag);
        bytes.Record(decodedBytes, tag);
    }

    internal void RecordAttempt(SwpcRunResult result)
    {
        if (result.Outcome is { } outcome && result.WriteStatus == SwpcWriteStatus.Applied)
            attempts.Add(1, new("product", result.Product.Token()), new("outcome", SwpcPolicy.OutcomeToken(outcome)));
        if (result.WriteStatus != SwpcWriteStatus.Applied) return;
        foreach (var item in result.Attention)
        {
            var kind = item.Group.EndsWith("/format-drift", StringComparison.Ordinal) ? "format-drift"
                : item.Group.EndsWith("/record-drift", StringComparison.Ordinal) ? "record-drift"
                : item.Reason == "escalated" ? "escalated" : "new";
            attention.Add(1, new KeyValuePair<string, object?>("kind", kind));
        }
    }

    internal void RecordRun(LocalAutomationRunOutcome outcome) => runs.Add(1, new KeyValuePair<string, object?>("outcome", outcome.ToString()));
    internal void RecordRetention(SwpcPurgeResult result)
    {
        retention.Add(result.GroupsDeleted, new KeyValuePair<string, object?>("kind", "groups"));
        retention.Add(result.VersionsDeleted, new KeyValuePair<string, object?>("kind", "versions"));
        retention.Add(result.SnapshotsDeleted, new KeyValuePair<string, object?>("kind", "snapshots"));
        retention.Add(result.RawBytesDeleted, new KeyValuePair<string, object?>("kind", "raw-bytes"));
    }

    internal void Observe(SwpcStoreView view, SwpcStoreHealth health, DateTimeOffset atUtc)
    {
        var active = view.Notices.Groups.Where(group => SwpcNoticeLifecycle.Evaluate(group, view.Notices.Groups, atUtc).State == SwpcNoticeState.Active)
            .GroupBy(static group => group.Family).ToDictionary(static group => group.Key, static group => group.Count());
        observation = new(view.Products.ToDictionary(static p => p.Key, static p => p.Value.LastSuccessUtc),
            active, view.ProviderHalt is not null, view.Products.Values.Any(static p => p.ProductHalt is not null), health.RawBodyBytes);
    }

    private IEnumerable<Measurement<double>> Ages()
    {
        var current = observation;
        if (current is null) yield break;
        var now = clock.GetUtcNow();
        foreach (var product in current.Successes)
            if (product.Value is { } success)
                yield return new(Math.Max(0, (now - success).TotalSeconds), new KeyValuePair<string, object?>("product", product.Key.Token()));
    }
    private IEnumerable<Measurement<long>> Active()
    {
        var current = observation;
        if (current is null) yield break;
        foreach (var family in Enum.GetValues<SpaceWeatherFamily>())
            yield return new(current.Active.GetValueOrDefault(family), new KeyValuePair<string, object?>("family", family.Token()));
    }
    private IEnumerable<Measurement<int>> Halts() => observation is { } current
        ? [new(current.ProviderHalted ? 1 : 0, new KeyValuePair<string, object?>("scope", "provider")),
            new(current.ProductHalted ? 1 : 0, new KeyValuePair<string, object?>("scope", "product"))] : [];
    private IEnumerable<Measurement<long>> StoreBytes() => observation is { } current ? [new(current.RawBytes)] : [];
    public void Dispose() => meter.Dispose();

    private sealed record Observation(Dictionary<SwpcProduct, DateTimeOffset?> Successes,
        Dictionary<SpaceWeatherFamily, int> Active, bool ProviderHalted, bool ProductHalted, long RawBytes);
}

/// <summary>Measures exactly the client exchange without changing its response or policy.</summary>
internal sealed class SpaceWeatherMeasuredClient(ISwpcClient inner, SpaceWeatherTelemetry telemetry, TimeProvider clock) : ISwpcClient
{
    public async ValueTask<SwpcHttpResponse> FetchAsync(SwpcProduct product, SwpcRequestValidators? validators, CancellationToken cancellationToken)
    {
        var start = clock.GetTimestamp();
        var result = await inner.FetchAsync(product, validators, cancellationToken).ConfigureAwait(false);
        telemetry.RecordFetch(product, clock.GetElapsedTime(start), result.Body.Length);
        return result;
    }
}
