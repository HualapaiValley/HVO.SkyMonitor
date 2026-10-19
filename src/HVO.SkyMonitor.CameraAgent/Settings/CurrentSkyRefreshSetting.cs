using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Settings;

/// <summary>
/// The operator's minimum time between Current Sky image changes, <c>CameraAgent:CurrentSky:MinimumRefreshSeconds</c>,
/// read from the live host configuration on every access so a saved value applies at the page's next refresh. A value
/// the host cannot use applies the default until it is corrected, and the settings read reports it.
/// </summary>
internal sealed class CurrentSkyRefreshSetting(IConfiguration configuration)
{
    public const string ConfigurationKey = "CameraAgent:CurrentSky:MinimumRefreshSeconds";

    /// <summary>Current Sky polls every 15 seconds, so a shorter interval could not be honoured.</summary>
    public const int MinimumSeconds = 15;

    public const int MaximumSeconds = 3600;

    public const int DefaultSeconds = 30;

    public int Seconds => TryParse(configuration[ConfigurationKey], out var seconds) ? seconds : DefaultSeconds;

    public static bool TryParse(string? value, out int seconds)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) &&
           seconds is >= MinimumSeconds and <= MaximumSeconds;
}
