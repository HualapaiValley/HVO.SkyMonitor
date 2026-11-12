namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>Lexical root paths and in-memory keys; configured paths and on-disk layout are unchanged.</summary>
internal static class StorageRootIdentity
{
    internal static string CanonicalPath(string root)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static string Key(string root)
    {
        var full = CanonicalPath(root);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    internal static bool AreEqual(string left, string right)
        => string.Equals(Key(left), Key(right), StringComparison.Ordinal);
}
