using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

public sealed partial record GpsReceiverSettings(bool Enabled, string Device, int BaudRate, bool UsePps)
{
    public const string SectionKey = TimeSyncSettings.SectionKey + ":Gps";
    public static GpsReceiverSettings Default { get; } = new(false, string.Empty, 9600, false);


    public string? Validate()
    {
        if (Device is null || Device.Length > 255 || ((Enabled || Device.Length > 0) && !DevicePath().IsMatch(Device)))
            return "Enter an absolute serial device path directly under /dev, or a numbered /dev/pts device.";
        return BaudRate is 4800 or 9600 or 38400 or 115200 ? null : "Choose a supported baud rate.";
    }

    internal static GpsReceiverSettings Read(IConfigurationSection section)
    {
        var enabled = section["Enabled"];
        var pps = section["UsePps"];
        var baud = section["BaudRate"];
        // A malformed option disables device access until corrected. Keep it visible as invalid configuration.
        bool usePps = false;
        int rate = Default.BaudRate;
        bool active = false;
        var valid = (enabled is null || bool.TryParse(enabled, out active)) &&
            (pps is null || bool.TryParse(pps, out usePps)) &&
            (baud is null || int.TryParse(baud, NumberStyles.None, CultureInfo.InvariantCulture, out rate));
        return new(active || !valid, section["Device"] ?? string.Empty, valid ? rate : 0, usePps);
    }

    [GeneratedRegex(@"\A/dev/(?:[A-Za-z0-9][A-Za-z0-9_.-]*|pts/[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePath();
}
