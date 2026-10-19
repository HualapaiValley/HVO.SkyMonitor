using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>How the command line lays out what it shows: aligned rows, tables, times, durations and sizes.</summary>
internal static class SkyCliFormat
{
    /// <summary>Writes rows as <c>Label: value</c>, aligned.</summary>
    public static void WriteRows(TextWriter output, IEnumerable<(string Label, string Value)> rows)
    {
        ArgumentNullException.ThrowIfNull(output);
        var list = rows.ToList();
        var width = list.Count == 0 ? 0 : list.Max(static row => row.Label.Length) + 1;
        foreach (var (label, value) in list)
        {
            output.WriteLine($"{(label + ":").PadRight(width)} {value}");
        }
    }

    /// <summary>Writes a table with a header row; columns are as wide as their widest cell.</summary>
    public static void WriteTable(TextWriter output, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(headers);
        var list = rows.ToList();
        var widths = headers.Select((header, column) => list.Select(row => row[column].Length).Append(header.Length).Max()).ToArray();
        output.WriteLine(Line(headers));
        foreach (var row in list)
        {
            output.WriteLine(Line(row));
        }

        string Line(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((cell, column) => cell.PadRight(widths[column]))).TrimEnd();
    }

    /// <summary>A UTC time to the second, as <c>2026-10-18T19:30:12Z</c>.</summary>
    public static string Time(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset? value) => value is { } time ? Time(time) : "never";

    /// <summary>A duration in its largest whole units, such as <c>3d 4h</c>, <c>12m 5s</c> or <c>9s</c>.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalDays >= 1 ? $"{(int)value.TotalDays}d {value.Hours}h"
            : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m"
            : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes}m {value.Seconds}s"
            : $"{(int)value.TotalSeconds}s";
    }

    /// <summary>A size in binary units, such as <c>4.2 GiB</c>.</summary>
    public static string Bytes(long? value)
    {
        if (value is not { } bytes)
        {
            return "unknown";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{size:0.0} {units[unit]}");
    }

    public static string Count(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    public static string Number(double? value, string format = "0.##")
        => value?.ToString(format, CultureInfo.InvariantCulture) ?? "unknown";
}
