using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The compact space-weather summary: the feature state, the alerts freshness chip, the active and pending count and
/// the first <see cref="SpaceWeatherUiBounds.SummaryRows"/> current notices with both of their lines. A turned-off
/// feature lists nothing; a projection without data, or an explicit <see cref="Unavailable"/>, claims nothing.
/// </summary>
public sealed partial class SpaceWeatherSummary : ComponentBase
{
    internal const string PageLink = "/operations/space-weather";
    internal const string SwitchLink = PageLink + "#space-weather-switch";
    internal const string UnavailableText = "Space-weather data is unavailable.";
    internal const string NoCurrentText = "No pending or active SWPC notices retained.";
    internal const string NoFetchText = "no successful fetch yet";
    private SummaryMode _mode = SummaryMode.Unavailable;
    private Chip _chip = new("Unavailable", "failure");

    /// <summary>The projection to summarize, or null when none has been read.</summary>
    [Parameter] public SpaceWeatherUiProjection? Projection { get; set; }

    /// <summary>Shows the unavailable state even without a projection, for a read that failed.</summary>
    [Parameter] public bool Unavailable { get; set; }

    private string Credit => Projection?.Credit ?? SpaceWeatherOperations.Credit;

    protected override void OnParametersSet()
    {
        _mode = Projection switch
        {
            { Enabled: false } => SummaryMode.Off,
            { HasData: true } when !Unavailable => SummaryMode.Ready,
            _ => SummaryMode.Unavailable,
        };
        _chip = _mode switch
        {
            SummaryMode.Off => new("Off", "skipped"),
            SummaryMode.Ready => FreshnessChip(Projection!.AlertsFreshness),
            _ => new("Unavailable", "failure"),
        };
    }

    /// <summary>
    /// The space-weather freshness chip, distinct from the shared freshness badge: fresh alerts are success, stale or
    /// outdated alerts a warning, and no successful fetch a neutral chip that says so.
    /// </summary>
    internal static Chip FreshnessChip(string? freshness) => freshness switch
    {
        "fresh" => new("Alerts fresh", "success"),
        "stale" => new("Alerts stale", "warning"),
        "outdated" => new("Alerts outdated", "warning"),
        "none" or null => new(NoFetchText, "pending"),
        _ => new("Alerts freshness unknown", "pending"),
    };

    internal static string StateTone(SpaceWeatherUiNotice notice) => notice.State switch
    {
        SwpcNoticeState.Active => "warning",
        SwpcNoticeState.Pending => "pending",
        _ => "skipped",
    };

    private static int MoreCount(SpaceWeatherUiProjection projection)
        => projection.CurrentTotal - Math.Min(projection.Current.Count, SpaceWeatherUiBounds.SummaryRows);

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A chip's text and its shared state-chip tone.</summary>
    internal sealed record Chip(string Text, string Tone);

    private enum SummaryMode
    {
        Ready,
        Off,
        Unavailable,
    }
}
