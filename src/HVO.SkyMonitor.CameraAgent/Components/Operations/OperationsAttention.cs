using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// One open item that does not stop local acquisition but should be reviewed. <see cref="Tone"/>
/// is the prototype attention-item class and <see cref="Icon"/> its status-icon class.
/// </summary>
internal sealed record OperationsAttentionItem(
    string Section,
    string Tone,
    string Icon,
    string Title,
    string Detail,
    string Href,
    string LinkText);

/// <summary>
/// The attention model shared by the Operations sidebar (health chip and section badges) and the
/// Overview's "Needs attention" panel, so both always count the same reported conditions.
/// </summary>
internal sealed class OperationsAttention
{
    private OperationsAttention(IReadOnlyList<OperationsAttentionItem> items) => Items = items;

    public IReadOnlyList<OperationsAttentionItem> Items { get; }

    public bool HasWarning => Items.Any(static item => item.Tone == "warning");

    /// <summary>The sidebar health chip: any warning, otherwise any review item, otherwise healthy.</summary>
    public string HealthLabel => HasWarning ? "Attention" : Items.Count > 0 ? "Review" : "Healthy";

    public string HealthClass => HasWarning ? "warning" : Items.Count > 0 ? "pending" : "success";

    /// <summary>The number of open items owned by a section; zero renders no badge.</summary>
    public int CountFor(string slug) => Items.Count(item => string.Equals(item.Section, slug, StringComparison.Ordinal));

    public static OperationsAttention From(CameraAgentOperationsView view, bool refreshFailed = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        var summary = view.Summary;
        var items = new List<OperationsAttentionItem>();
        var freshness = Freshness(summary);

        if (refreshFailed || freshness.Contains("stale", StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new("health", "warning", "warning", "Some operating facts are stale",
                "The latest read did not refresh every section; the values shown may be out of date.",
                "/operations/health", "Inspect"));
        }

        if (freshness.Contains("unknown", StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new("health", "info", "pending", "Some operating facts have not been observed",
                "No source has reported them since this CameraAgent started.",
                "/operations/health", "Inspect"));
        }

        var centralEnabled = !string.Equals(summary.Configuration.Value.CentralIntegration, "Disabled", StringComparison.Ordinal);
        if (centralEnabled &&
            string.Equals(summary.Heartbeat.Freshness, "fresh", StringComparison.OrdinalIgnoreCase) &&
            summary.Heartbeat.Value.Availability is "Unavailable" or "Unhealthy")
        {
            items.Add(new("health", "warning", "warning", "LogicHost connection unavailable",
                "Local acquisition continues; central delivery waits in the durable outbox.",
                "/operations/health", "Inspect"));
        }

        var pressuredStorage = summary.Storage.Value.Where(static storage => storage.IsUnderPressure)
            .Select(static storage => $"{OperationsPage.SplitWords(storage.Alias)} storage").ToArray();
        var pressuredLanes = summary.CaptureLanes.Value.Lanes.Where(static lane => lane.PressureLevel > 0)
            .Select(static lane => $"{OperationsPage.SplitWords(lane.Name)} lane").ToArray();
        if (pressuredStorage.Length > 0 || pressuredLanes.Length > 0)
        {
            items.Add(new("storage", "warning", "warning", "Storage or lane pressure",
                $"Under pressure: {string.Join(", ", pressuredStorage.Concat(pressuredLanes))}.",
                "/operations/storage", "Inspect"));
        }

        var artifactRetries = summary.ArtifactOutbox.Value.RetryCount;
        if (artifactRetries > 0)
        {
            items.Add(new("delivery", "info", "running", Plural(artifactRetries, "artifact delivery is", "artifact deliveries are") + " retrying",
                "Captures are durable locally; the outbox is applying bounded backoff.",
                "/operations/delivery", "Inspect"));
        }

        var artifactHeld = Math.Max(summary.ArtifactOutbox.Value.QuarantineCount, view.ArtifactQuarantine.Count);
        if (artifactHeld > 0)
        {
            items.Add(new("storage", "warning", "warning", Plural(artifactHeld, "artifact item is", "artifact items are") + " quarantined",
                "Held items stop retrying until they are replayed or abandoned.",
                "/operations/quarantine", "Review"));
        }

        var environmentalRetries = summary.EnvironmentalDelivery.Value.RetryCount;
        if (environmentalRetries > 0)
        {
            items.Add(new("delivery", "info", "running", Plural(environmentalRetries, "environmental delivery is", "environmental deliveries are") + " retrying",
                "Observations are durable locally; delivery is applying bounded backoff.",
                "/operations/delivery", "Inspect"));
        }

        var environmentalHeld = Math.Max(summary.EnvironmentalDelivery.Value.QuarantineCount, view.EnvironmentalQuarantine.Count);
        if (environmentalHeld > 0)
        {
            items.Add(new("storage", "warning", "warning", Plural(environmentalHeld, "environmental item is", "environmental items are") + " quarantined",
                "Held observations stop retrying until they are replayed or abandoned.",
                "/operations/quarantine?kind=Environmental", "Review"));
        }

        return new OperationsAttention(items);
    }

    private static string[] Freshness(CameraAgentOperationsSummary summary) =>
    [
        summary.CaptureControl.Freshness,
        summary.RawIngress.Freshness,
        summary.CaptureLanes.Freshness,
        summary.CaptureProcessing.Freshness,
        summary.ArtifactOutbox.Freshness,
        summary.Storage.Freshness,
        summary.CaptureRuntime.Freshness,
        summary.Heartbeat.Freshness,
        summary.EnvironmentalDelivery.Freshness,
        summary.TransientWorker.Freshness,
        summary.CaptureTelemetry.Freshness,
        summary.Configuration.Freshness
    ];

    private static string Plural(long count, string singular, string plural) => count == 1
        ? $"One {singular}"
        : $"{count.ToString(CultureInfo.InvariantCulture)} {plural}";
}
