using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Tests;

internal static class FileSystemTestPaths
{
    internal static string CreatePhysicalTemporaryDirectory(string prefix)
    {
        var directory = Path.Combine(
            ResolvePhysicalPath(Path.GetTempPath()),
            prefix,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string CreateShortUnixSocketPath()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix socket tests require a Unix host.");
        }

        var temporaryRoot = ResolvePhysicalPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp"));
        var path = Path.Combine(temporaryRoot, $"hvo-{Guid.NewGuid():N}.sock");
        if (Encoding.UTF8.GetByteCount(path) > 100)
        {
            throw new InvalidOperationException("The test Unix socket path exceeds the production byte limit.");
        }
        return path;
    }

    private static string ResolvePhysicalPath(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new IOException($"No filesystem root exists for '{path}'.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return Path.GetFullPath(current);
    }
}
