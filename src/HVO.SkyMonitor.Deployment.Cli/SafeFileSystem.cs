using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Deployment;

internal static class SafeFileSystem
{
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    // Includes the setuid/setgid/sticky bits so an adopted bind source cannot keep them while reporting 0700.
    private const UnixFileMode AllPermissions =
        UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit |
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static void EnsureSafeExistingAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = new DirectoryInfo(Path.GetPathRoot(fullPath)!);
        foreach (var segment in fullPath[Path.GetPathRoot(fullPath)!.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            if (!current.Exists)
            {
                break;
            }

            current.Refresh();
            if (current.LinkTarget is not null)
            {
                throw new InstallerException($"Path '{current.FullName}' must not be a symbolic link.");
            }
        }
    }

    public static void CreateOwnerDirectory(string path)
    {
        EnsureSafeExistingAncestors(path);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectoryMode);
    }

    /// <summary>
    /// Creates or adopts a writable Compose bind source with the configured runtime ownership and owner-only mode.
    /// Docker creates a missing nested bind source as <c>root:root</c> mode <c>0755</c>, which the
    /// capability-dropped CameraAgent container cannot restrict, so every source must exist before Compose starts.
    /// </summary>
    public static void CreateRuntimeDirectory(string path, uint uid, uint gid)
    {
        EnsureSafeExistingAncestors(path);
        var created = false;
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            created = true;
            File.SetUnixFileMode(path, OwnerDirectoryMode);
        }
        else if (new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new InstallerException($"Writable bind source '{path}' must not be a symbolic link.");
        }

        var identity = NativeLinux.GetDirectoryIdentity(path);
        if (identity.Uid != uid || identity.Gid != gid)
        {
            // A directory this call just created belongs to the invoking user, not the configured runtime identity.
            // Remove it again so a mismatched invocation cannot leave behind a source it will refuse forever.
            if (created)
            {
                Directory.Delete(path);
            }
            throw new InstallerException(
                $"Writable bind source '{path}' is owned by {identity.Uid}:{identity.Gid} instead of the configured runtime {uid}:{gid}; complete the CameraAgent reset procedure or restore its ownership before deploying.");
        }
        if ((identity.Mode & AllPermissions) != OwnerDirectoryMode)
        {
            File.SetUnixFileMode(path, OwnerDirectoryMode);
        }
    }

    public static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InstallerException($"Path '{path}' has no parent directory.");
        CreateOwnerDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                File.SetUnixFileMode(temporaryPath, OwnerFileMode);
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide the required flush-to-disk guarantee.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            File.Move(temporaryPath, path, overwrite: true);
            NativeLinux.FlushDirectory(directory);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static void ValidateOwnerFile(string path, bool allowReadOnly = false)
    {
        using var handle = NativeLinux.OpenReadOnlyNoFollow(path);
        ValidateOwnerFile(handle, path, allowReadOnly);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned FileStream takes ownership of the authenticated handle.")]
    public static FileStream OpenOwnerFileRead(string path, bool allowReadOnly = false)
    {
        var handle = NativeLinux.OpenReadOnlyNoFollow(path);
        try
        {
            ValidateOwnerFile(handle, path, allowReadOnly);
            return new FileStream(handle, FileAccess.Read, 16 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned FileStream takes ownership of the authenticated handle.")]
    public static FileStream OpenRegularFileRead(string path)
    {
        var handle = NativeLinux.OpenReadOnlyNoFollow(path);
        try
        {
            if (NativeLinux.GetOpenFileIdentity(handle, path).LinkCount != 1)
            {
                throw new InstallerException($"Input file '{path}' must not have hard links.");
            }
            return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned FileStream takes ownership of the authenticated handle.")]
    public static FileStream OpenOwnerFileAppend(string path)
    {
        var handle = NativeLinux.OpenReadWriteNoFollow(path);
        try
        {
            ValidateOwnerFile(handle, path);
            var stream = new FileStream(handle, FileAccess.ReadWrite, 128 * 1024, isAsync: false);
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static void ValidateOwnerFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, bool allowReadOnly = false)
    {
        var identity = NativeLinux.GetOpenFileIdentity(handle, path);
        if (identity.LinkCount != 1)
        {
            throw new InstallerException($"Protected file '{path}' must not have hard links.");
        }

        var mode = identity.Mode;
        var allowed = OwnerFileMode | (allowReadOnly ? UnixFileMode.None : UnixFileMode.UserWrite);
        if (identity.Uid != NativeLinux.getuid() || identity.Gid != NativeLinux.getgid() ||
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
            (mode & UnixFileMode.UserRead) == 0 ||
            (!allowReadOnly && (mode & UnixFileMode.UserWrite) == 0) ||
            (mode & ~allowed) != 0)
        {
            throw new InstallerException($"Protected file '{path}' must be owner-only mode 0600 or 0400.");
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenRegularFileRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static void WriteTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InstallerException($"Path '{path}' has no parent directory.");
        CreateOwnerDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                File.SetUnixFileMode(temporary, OwnerFileMode);
                writer.Write(content);
                writer.Flush();
#pragma warning disable CA1849 // Atomic configuration publication requires a flush-to-disk boundary.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(temporary, path, overwrite: true);
            NativeLinux.FlushDirectory(directory);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<string> CopyPrivateFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = OpenRegularFileRead(sourcePath);
        return await CopyPrivateFileAsync(source, destinationPath, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> CopyPrivateFileAsync(
        FileStream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InstallerException($"Path '{destinationPath}' has no parent directory.");
        CreateOwnerDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            File.SetUnixFileMode(temporary, OwnerFileMode);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Private staging requires a flush-to-disk boundary.
            destination.Flush(flushToDisk: true);
#pragma warning restore CA1849
            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            destination.Close();
            File.Move(temporary, destinationPath, overwrite: false);
            NativeLinux.FlushDirectory(directory);
            return sha256;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void MakeTreeOwnerWritable(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, OwnerFileMode);
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Reverse())
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is null)
            {
                File.SetUnixFileMode(directory, OwnerDirectoryMode);
            }
        }
        File.SetUnixFileMode(root, OwnerDirectoryMode);
    }
}
