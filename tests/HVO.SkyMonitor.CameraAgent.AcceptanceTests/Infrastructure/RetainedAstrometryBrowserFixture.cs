namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

/// <summary>Copies an already canonical generator tree without rewriting retained identities.</summary>
internal static class RetainedAstrometryBrowserFixture
{
    internal static async Task CopyAsync(string source, string root)
    {
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(root, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = File.OpenRead(path);
            using var output = File.Create(destination);
            await input.CopyToAsync(output).ConfigureAwait(false);
        }
    }
}
