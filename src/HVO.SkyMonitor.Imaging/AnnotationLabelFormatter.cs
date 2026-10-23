namespace HVO.SkyMonitor.Imaging;

/// <summary>Bounds final label text without losing an explicitly declared expected-position diagnostic marker.</summary>
public static class AnnotationLabelFormatter
{
    public const string Version = "bounded-expected-position-label-v1";
    public const string ExpectedMarker = "(expected)";
    public const string ExpectedSuffix = " " + ExpectedMarker;

    /// <summary>
    /// Ordinary labels retain prefix clipping. A diagnostic reserves the complete marker, or is suppressed when the
    /// character budget cannot distinguish it. Catalogue names never determine diagnostic semantics.
    /// </summary>
    public static string? Format(ProjectedAnnotationObject item, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        if (!item.DrawLabel || maximumCharacters == 0)
            return null;
        if (!item.ExpectedPosition)
            return item.DisplayName[..Math.Min(item.DisplayName.Length, maximumCharacters)];
        if (maximumCharacters < ExpectedMarker.Length)
            return null;
        var nameLength = Math.Min(item.DisplayName.Length, maximumCharacters - ExpectedSuffix.Length);
        return nameLength <= 0 ? ExpectedMarker : item.DisplayName[..nameLength] + ExpectedSuffix;
    }
}
