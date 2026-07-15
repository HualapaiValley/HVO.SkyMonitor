using System.Globalization;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralArtifactByteRange(long Start, long Length)
{
    public long End => Start + Length - 1;

    public static bool TryParse(string? value, long totalLength, out CentralArtifactByteRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (totalLength <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var specification = value[6..].Trim();
        if (specification.Length == 0 || specification.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }
        var separator = specification.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0 || specification[(separator + 1)..].Contains('-', StringComparison.Ordinal))
        {
            return false;
        }
        var startText = specification[..separator].Trim();
        var endText = specification[(separator + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)
                || suffix <= 0)
            {
                return false;
            }
            var length = Math.Min(suffix, totalLength);
            range = new CentralArtifactByteRange(totalLength - length, length);
            return true;
        }
        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start < 0 || start >= totalLength)
        {
            return false;
        }
        var end = totalLength - 1;
        if (endText.Length > 0
            && (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end)
                || end < start))
        {
            return false;
        }
        end = Math.Min(end, totalLength - 1);
        range = new CentralArtifactByteRange(start, end - start + 1);
        return true;
    }
}
