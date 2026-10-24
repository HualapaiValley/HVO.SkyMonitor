using System.Security.Cryptography;

namespace HVO.SkyMonitor.Deployment;

internal static partial class PathPrograms
{
    internal static async Task<string?> HashAsync(string path, CancellationToken cancellationToken)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(path)!);
        var node = NativeLinux.TryGetNodeIdentity(path);
        if (node is null) return null;
        if (node.Type != NativeLinux.RegularFileNodeType || node.LinkCount != 1)
            throw InstallerException.Refused($"Program file '{path}' must be a regular file without links.");
        await using var stream = SafeFileSystem.OpenRegularFileRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task PublishAsync(string source, string target, string sha256, PathProgramRecord? publication, string? destinationRoot,
        CancellationToken cancellationToken)
    {
        EnsureTrustedDestination(target, destinationRoot);
        await ValidatePublicationAsync(target, sha256, publication, cancellationToken).ConfigureAwait(false);
        var before = await HashAsync(target, cancellationToken).ConfigureAwait(false);
        if (before == sha256)
        {
            File.SetUnixFileMode(target, ProgramMode);
            await using var installed = SafeFileSystem.OpenRegularFileRead(target);
            NativeLinux.FlushProgramFile(installed.SafeFileHandle);
            return;
        }
        // Stage and verify before changing either published copy. The cached source also makes a rollback repeatable
        // after a crash between writing .previous and replacing the current file; no in-place swap can lose either.
        var staged = target + $".{Guid.NewGuid():N}.new";
        try
        {
            await WriteVerifiedAsync(source, staged, sha256, executable: true, cancellationToken).ConfigureAwait(false);
            await using var verified = SafeFileSystem.OpenRegularFileRead(staged);
            var identity = NativeLinux.GetOpenNodeIdentity(verified.SafeFileHandle, staged);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(verified, cancellationToken).ConfigureAwait(false)) != sha256)
                throw InstallerException.Refused("The staged program changed before publication.");
            if (before is not null)
                await WriteVerifiedAsync(target, target + ".previous", before, executable: true, cancellationToken).ConfigureAwait(false);
            File.Move(staged, target, overwrite: true);
            RequirePublishedIdentity(target, identity);
            NativeLinux.FlushDirectory(Path.GetDirectoryName(target)!);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    internal static async Task WriteVerifiedAsync(
        string source, string target, string expectedSha256, bool executable, CancellationToken cancellationToken)
    {
        // Both the bytes hashed and the bytes copied come from this one no-follow open file.
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(source)!);
        var sourceNode = NativeLinux.GetNodeIdentity(source);
        if (sourceNode.Type != NativeLinux.RegularFileNodeType || sourceNode.LinkCount != 1)
            throw new InstallerException($"Program source '{source}' must be a regular file without links.");
        await using var input = SafeFileSystem.OpenRegularFileRead(source);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)) != expectedSha256)
            throw new InstallerException($"Program source '{source}' does not match its SHA-256.");
        input.Position = 0;
        var folder = Path.GetDirectoryName(target)!;
        SafeFileSystem.EnsureSafeExistingAncestors(folder);
        CreateProgramDirectories(folder, executable ? ProgramMode : SafeFileSystem.OwnerDirectoryMode);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                File.SetUnixFileMode(temporary, executable ? ProgramMode : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // The executable must be durable before it is published.
                output.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            if (await HashAsync(temporary, cancellationToken).ConfigureAwait(false) != expectedSha256)
                throw new InstallerException("The staged program does not match its SHA-256.");
            _ = await HashAsync(target, cancellationToken).ConfigureAwait(false);
            await using var verified = SafeFileSystem.OpenRegularFileRead(temporary);
            var identity = NativeLinux.GetOpenNodeIdentity(verified.SafeFileHandle, temporary);
            File.Move(temporary, target, overwrite: true);
            RequirePublishedIdentity(target, identity);
            FlushProgramDirectories(folder);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void CreateProgramDirectories(string folder, UnixFileMode mode)
    {
        var missing = new Stack<string>();
        for (var current = folder; !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
            missing.Push(current);
        while (missing.TryPop(out var current))
        {
            Directory.CreateDirectory(current, mode);
            File.SetUnixFileMode(current, mode);
        }
    }

    internal static void FlushProgramDirectories(string folder)
    {
        for (string? current = Path.GetFullPath(folder); current is not null; current = Path.GetDirectoryName(current))
            NativeLinux.FlushDirectory(current);
    }

    internal static async Task RemoveMatchingAsync(string target, string sha256, string? destinationRoot, CancellationToken cancellationToken)
    {
        EnsureTrustedDestination(target, destinationRoot);
        var actual = await HashAsync(target, cancellationToken).ConfigureAwait(false);
        if (actual is null) return;
        if (actual != sha256)
            throw new InstallerException($"Program '{target}' differs from the installed record; it was preserved. Move it aside before retrying.");
        File.Delete(target);
        NativeLinux.FlushDirectory(Path.GetDirectoryName(target)!);
    }
}
