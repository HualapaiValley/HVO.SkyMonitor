using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

// These wrappers preserve File/Directory semantics and are no-ops for integrity purposes
// outside registered retained roots and for bytes outside the manifest verification scope.
internal static class RetainedFileMutations
{
    internal static void Delete(string path)
    {
        using var mutation = RetainedIntegrityLedger.BeginDelete(path);
        File.Delete(path);
    }
    internal static void Move(string source, string destination, bool overwrite = false)
    {
        using var mutation = RetainedIntegrityLedger.BeginPublication(source, destination);
        File.Move(source, destination, overwrite);
    }
    internal static void Copy(string source, string destination, bool overwrite = false)
    {
        using var mutation = RetainedIntegrityLedger.BeginPublication(source, destination);
        File.Copy(source, destination, overwrite);
    }
    internal static void DeleteDirectory(string path, bool recursive = false)
    {
        using var mutation = RetainedIntegrityLedger.BeginTrees(path);
        Directory.Delete(path, recursive);
    }
    internal static void MoveDirectory(string source, string destination)
    {
        using var mutation = RetainedIntegrityLedger.BeginTreeMove(source, destination);
        Directory.Move(source, destination);
    }
    internal static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        using var mutation = RetainedIntegrityLedger.BeginWrite(path, bytes);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }
    internal static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        using var mutation = path.EndsWith(".json", StringComparison.Ordinal)
            ? RetainedIntegrityLedger.BeginWrite(path, bytes) : null;
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }
    internal static async Task WriteAllTextAsync(string path, string? text, CancellationToken cancellationToken = default)
    {
        using var mutation = path.EndsWith(".json", StringComparison.Ordinal)
            ? RetainedIntegrityLedger.BeginWrite(path, Encoding.UTF8.GetBytes(text ?? string.Empty)) : null;
        await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
    }
    internal static void WriteAllText(string path, string? text)
    {
        using var mutation = path.EndsWith(".json", StringComparison.Ordinal)
            ? RetainedIntegrityLedger.BeginWrite(path, Encoding.UTF8.GetBytes(text ?? string.Empty)) : null;
        File.WriteAllText(path, text);
    }
}
