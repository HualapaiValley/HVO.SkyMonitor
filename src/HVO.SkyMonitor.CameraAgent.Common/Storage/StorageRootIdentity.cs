namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>Lexical root identity for in-memory coordination; configured paths and on-disk layout are unchanged.</summary>
internal static class StorageRootIdentity
{
    internal static string Normalize(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    internal static bool AreEqual(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}
