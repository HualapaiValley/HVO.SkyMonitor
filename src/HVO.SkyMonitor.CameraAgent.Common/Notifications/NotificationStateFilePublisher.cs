using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Restricted (0700/0600 on Unix), symlink-refusing, fsync'd atomic file publication helper backed by
/// <see cref="HVO.SkyMonitor.Storage.FileSystem"/> primitives for operator notification state and checkpoints.
/// </summary>
public static class NotificationStateFilePublisher
{
    private const UnixFileMode RestrictedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode RestrictedFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureRestrictedDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        VerifyPathIsNotSymlink(fullDirectory);
        Directory.CreateDirectory(fullDirectory);
        VerifyPathIsNotSymlink(fullDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fullDirectory, RestrictedDirectoryMode);
        }

        try
        {
            _ = PhysicalRoot.Open(fullDirectory);
            DurableSync.Directory(fullDirectory);
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }
    }

    public static void VerifyRegularFileNoSymlink(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        VerifyPathIsNotSymlink(fullDirectory);

        var targetPath = Path.Combine(fullDirectory, fileName);
        VerifyPathIsNotSymlink(targetPath);

        try
        {
            var root = PhysicalRoot.Open(fullDirectory);
            var resolved = root.Resolve(fileName);
            if (File.Exists(resolved))
            {
                DurableSync.RequireRegularFile(resolved);
            }
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }
    }

    public static async ValueTask PublishRestrictedAtomicAsync(
        string directoryPath,
        string fileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken,
        Action<string>? beforeRename = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        EnsureRestrictedDirectory(fullDirectory);

        PhysicalRoot root;
        string finalPath;
        try
        {
            root = PhysicalRoot.Open(fullDirectory);
            finalPath = root.Resolve(fileName);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                DurableSync.RequireRegularFile(finalPath);
            }
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }

        var tempFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Combine(fullDirectory, tempFileName);

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = RestrictedFileMode;
            }

            var stream = new FileStream(tempPath, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    DurableSync.File(stream.SafeFileHandle, tempPath);
                }
                catch (FileSystemFaultException ex)
                {
                    throw TranslateFault(ex);
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, RestrictedFileMode);
            }

            beforeRename?.Invoke(tempPath);

            try
            {
                AtomicPublisher.PublishPrepared(root, tempPath, finalPath, PublishMode.Replace);
                root.Verify(finalPath, "publish-notification-state");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(finalPath, RestrictedFileMode);
                }
                DurableSync.Directory(fullDirectory);
            }
            catch (FileSystemFaultException ex)
            {
                throw TranslateFault(ex);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort temporary cleanup.
                }
            }
        }
    }

    public static void DeleteIfExistsNoSymlink(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var targetPath = Path.Combine(fullDirectory, fileName);
        if (!File.Exists(targetPath) && new FileInfo(targetPath).LinkTarget is null)
        {
            return;
        }

        VerifyRegularFileNoSymlink(fullDirectory, fileName);
        File.Delete(targetPath);
        try
        {
            DurableSync.Directory(fullDirectory);
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }
    }

    private static void VerifyPathIsNotSymlink(string path)
    {
        var fileInfo = new FileInfo(path);
        var dirInfo = new DirectoryInfo(path);
        if (fileInfo.LinkTarget is not null || dirInfo.LinkTarget is not null)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
        }

        if (fileInfo.Exists && (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
        }

        if (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
        }
    }

    private static IOException TranslateFault(FileSystemFaultException exception) =>
        exception.Kind == FileSystemFaultKind.Containment
            ? new IOException("Refusing to access a symbolic link or non-regular file in the protected operator notification store.", exception)
            : new IOException("Durable operator notification store file operation failed.", exception);
}

/// <summary>
/// Persists the last emitted site-local calendar date for the daily operator notification heartbeat as a tiny
/// fsync'd, 0600 atomic JSON checkpoint under the agent state directory so restarts do not re-emit the same day's
/// heartbeat. Writes zero files while notifications are disabled or before a heartbeat is emitted.
/// </summary>
public sealed class FileOperatorNotificationHeartbeatStore(string stateDirectory) : IOperatorNotificationHeartbeatStore
{
    public const string HeartbeatFileName = "operator-notifications-heartbeat.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory = !string.IsNullOrWhiteSpace(stateDirectory)
        ? stateDirectory
        : throw new ArgumentException("State directory is required.", nameof(stateDirectory));

    internal string FilePath => Path.Combine(_stateDirectory, HeartbeatFileName);

    public async ValueTask<DateOnly?> ReadLastHeartbeatSiteDateAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path) && !Directory.Exists(path) &&
            new FileInfo(path).LinkTarget is null && new DirectoryInfo(path).LinkTarget is null)
        {
            return null;
        }

        NotificationStateFilePublisher.VerifyRegularFileNoSymlink(_stateDirectory, HeartbeatFileName);
        if (Directory.Exists(path))
        {
            throw new IOException("Refusing to read a directory as the operator notification heartbeat checkpoint.");
        }
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<HeartbeatCheckpoint>(json, JsonOptions);
        if (payload is null ||
            !DateOnly.TryParseExact(payload.LastSiteDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }
        return parsed;
    }

    public async ValueTask WriteLastHeartbeatSiteDateAsync(DateOnly siteDate, CancellationToken cancellationToken)
    {
        var payload = new HeartbeatCheckpoint(siteDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            _stateDirectory,
            HeartbeatFileName,
            bytes,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record HeartbeatCheckpoint(string LastSiteDate);
}
