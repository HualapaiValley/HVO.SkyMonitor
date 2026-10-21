using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The space-weather attention items shared by the Operations sidebar and the Overview's "Needs attention" panel.
/// Items are aggregated, never one per notice, and a turned-off feature contributes none. A projection without data
/// claims nothing about activity: it contributes only the store-unavailable item, and only when the feature is on.
/// </summary>
internal static class SpaceWeatherAttention
{
    internal const string Section = "space-weather";
    internal const string Href = "/operations/space-weather";

    internal static IReadOnlyList<OperationsAttentionItem> From(SpaceWeatherUiProjection? projection)
    {
        if (projection is null || !projection.Enabled)
        {
            return [];
        }
        if (!projection.HasData)
        {
            return Unavailable(enabled: true);
        }
        var items = new List<OperationsAttentionItem>();
        if (projection.ReachCount > 0)
        {
            items.Add(new(Section, "warning", "warning",
                Count(projection.ReachCount, "notice reaches or approaches", "notices reach or approach") +
                " this site's geomagnetic latitude",
                "An HVO SkyMonitor estimate from SWPC notices that are unacknowledged or changed since acknowledgement. Cloud is not assessed.",
                Href, "Review"));
        }
        if (projection.ReviewCount > 0)
        {
            items.Add(new(Section, "info", "pending",
                Count(projection.ReviewCount, "notice", "notices") + " to review",
                "Pending or active SWPC notices that are unacknowledged or escalated since acknowledgement.",
                Href, "Review"));
        }
        foreach (var product in projection.FormatDriftProducts)
        {
            items.Add(new(Section, "info", "pending", $"Format drift on {product}",
                "Responses for this product have been rejected repeatedly; the provider's format may have changed.",
                Href, "Inspect"));
        }
        if (projection.RecordDriftProducts.Count > 0)
        {
            items.Add(new(Section, "info", "pending", "Record drift",
                $"Some records could not be read for: {string.Join(", ", projection.RecordDriftProducts)}.",
                Href, "Inspect"));
        }
        if (projection.ProviderHalt is { } providerHalt)
        {
            items.Add(Halted(providerHalt));
        }
        foreach (var halt in projection.ProductHalts)
        {
            items.Add(Halted(halt));
        }
        return items;
    }

    /// <summary>A failed read keeps no earlier items; it reports only that the store is unavailable.</summary>
    internal static IReadOnlyList<OperationsAttentionItem> Unavailable(bool enabled) => enabled
        ?
        [
            new(Section, "info", "pending", "Space-weather store unavailable",
                "Space-weather data could not be read; nothing is claimed about current notices.",
                Href, "Inspect")
        ]
        : [];

    private static OperationsAttentionItem Halted(SpaceWeatherUiHalt halt)
        => new(Section, "info", "pending", $"SWPC fetching halted ({halt.Target})",
            $"Fetching stays halted until an operator resumes it: {halt.Description}.", Href, "Inspect");

    private static string Count(int count, string singular, string plural) => count == 1
        ? $"One {singular}"
        : $"{count.ToString(CultureInfo.InvariantCulture)} {plural}";
}
