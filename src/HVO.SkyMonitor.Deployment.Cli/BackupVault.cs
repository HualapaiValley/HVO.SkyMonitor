using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The record a backup writes beside its archive when it commits it, once the archive has been read back and proved
/// whole (#1353). Restore reads only an archive with its record, and requires the archive's bytes to be the ones the
/// record names, so a partial or unrecorded archive is never usable. It names the instance's runtime user, whose files a
/// restore writes.
/// </summary>
internal sealed record BackupRecord(
    int SchemaVersion,
    string Name,
    Guid InstanceId,
    Guid OperationId,
    DateTimeOffset CreatedUtc,
    long ArchiveLength,
    string ArchiveSha256,
    long TotalBytes,
    int Entries,
    uint RuntimeUid,
    uint RuntimeGid)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Whether this is a record this installer writes, for the archive it names.</summary>
    public bool IsValid(string name)
        => SchemaVersion == CurrentSchemaVersion && string.Equals(Name, name, StringComparison.Ordinal) &&
           BackupVaultFiles.IsName(name) && InstanceId != Guid.Empty && OperationId != Guid.Empty &&
           ArchiveLength > 0 && TotalBytes >= 0 && Entries >= 0 && RuntimeUid != 0 &&
           ArchiveSha256 is { Length: 64 } && ArchiveSha256.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')) &&
           string.Equals(Name, BackupVaultFiles.NameFor(InstanceId, CreatedUtc, OperationId), StringComparison.Ordinal);
}

/// <summary>An archive a backup began and did not commit, with the bytes it holds.</summary>
internal sealed record BackupPartial(string Name, long Length);

/// <summary>One stream to or from the vault, which <see cref="CompleteAsync"/> finishes and proves finished.</summary>
internal interface IBackupTransfer : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>
    /// For a write, makes everything written durable; for a read, nothing more. A transfer disposed without completing is
    /// abandoned, and a partial it wrote stays until it is discarded.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Where instance backups are kept: one owner-only folder holding each committed archive, its record, and any partial a
/// backup began. Under the production product root it is <c>/var/backups/hvo-sky</c>, and it and every file in it are
/// root's (<see cref="RootBackupVault"/>). Under a test product root it is <c>&lt;product-root&gt;/backups</c>, and belongs
/// to the user who runs the tests (<see cref="OwnerBackupVault"/>).
/// </summary>
internal interface IBackupVault
{
    /// <summary>The folder, for messages.</summary>
    string Root { get; }

    /// <summary>Creates <paramref name="name"/>'s partial, which must not exist, and returns a stream that writes it.</summary>
    Task<IBackupTransfer> WriteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the committed archive <paramref name="name"/>, or with <paramref name="partial"/> its partial.</summary>
    Task<IBackupTransfer> ReadAsync(string name, bool partial, CancellationToken cancellationToken);

    /// <summary>
    /// Commits a partial: records <paramref name="record"/> durably, then gives the partial its archive name. A name that
    /// is already committed is refused before any record is touched, unless it is this same archive, which is a no-op.
    /// </summary>
    Task CommitAsync(BackupRecord record, CancellationToken cancellationToken);

    /// <summary>Removes <paramref name="name"/>'s partial, when there is one. It never removes a committed archive.</summary>
    Task DiscardAsync(string name, CancellationToken cancellationToken);

    /// <summary>The record of the committed archive <paramref name="name"/>, or <see langword="null"/> when it has none.</summary>
    Task<BackupRecord?> ReadRecordAsync(string name, CancellationToken cancellationToken);

    /// <summary>Every partial of <paramref name="instanceId"/>'s backups.</summary>
    Task<IReadOnlyList<BackupPartial>> ListPartialsAsync(Guid instanceId, CancellationToken cancellationToken);
}

internal static class BackupVault
{
    /// <summary>The vault's folder under a test product root.</summary>
    public const string FolderName = "backups";

    /// <summary>The vault of the production product root, root's, as RoofController keeps its own in <c>/var/backups/hvo-roof</c>.</summary>
    public const string ProductionFolder = "/var/backups/hvo-sky";

    /// <summary>Replaces the vault a lifecycle command uses. Tests set it; production leaves it unset.</summary>
    internal static Func<string, string, IBackupVault>? Override { get; set; }

    /// <summary>
    /// The vault of <paramref name="request"/>'s product root. A command that runs as root through <c>sudo</c>
    /// (<see cref="RootPrivilege"/>) keeps root's vault: <see cref="ProductionFolder"/> under the production product root,
    /// and under a test product root the folder the request names, or <c>&lt;product-root&gt;/backups</c>. A command run by
    /// the test product root's own user keeps its vault in <c>&lt;product-root&gt;/backups</c>.
    /// </summary>
    public static IBackupVault For(LifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Override?.Invoke(request.ProductRoot, request.ProductionProductRoot) is { } replaced) return replaced;
        if (RootPrivilege.Held)
        {
            return new RootBackupVault(
                new BackupVaultFiles(RootFolder(request), requireRootAncestry: true), RootWindow.Instance);
        }
        return new OwnerBackupVault(new BackupVaultFiles(Path.Combine(request.ProductRoot, FolderName)));
    }

    /// <summary>The folder root's vault uses for <paramref name="request"/>.</summary>
    public static string RootFolder(LifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(request.ProductRoot, request.ProductionProductRoot, StringComparison.Ordinal)
            ? ProductionFolder
            : request.TestBackupFolder ?? Path.Combine(request.ProductRoot, FolderName);
    }
}

/// <summary>Runs one step of a vault operation with the authority the vault's files need.</summary>
internal interface IRootWindow
{
    T Run<T>(Func<T> step);
}

/// <summary>Runs each step as root (<see cref="RootPrivilege.AsRoot{T}"/>).</summary>
internal sealed class RootWindow : IRootWindow
{
    public static RootWindow Instance { get; } = new();

    public T Run<T>(Func<T> step) => RootPrivilege.AsRoot(step);
}

/// <summary>Runs each step as the current user.</summary>
internal sealed class CurrentUserWindow : IRootWindow
{
    public static CurrentUserWindow Instance { get; } = new();

    public T Run<T>(Func<T> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step();
    }
}

/// <summary>
/// The vault's files, read and written by the vault's owner: root, or the test product root's user. Every name is one
/// this installer gives an archive, every file is opened without following a link and must be a single-link regular file
/// owned by the caller with mode 0600, every change happens under the vault's lock, and every step a later one depends on
/// is durable before it returns. Root's vault also requires its folder and every folder above it to be root's and
/// writable by root alone (<see cref="RootPrivilege.RootAncestryProblem"/>).
/// </summary>
internal sealed partial class BackupVaultFiles(string root, bool requireRootAncestry = false)
{
    public const string PartialSuffix = ".partial";
    public const string RecordSuffix = ".json";
    private const string LockName = ".lock";

    /// <summary>The largest record the vault reads.</summary>
    private const int MaximumRecordBytes = 64 * 1024;

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>The name of the archive operation <paramref name="operationId"/> writes for <paramref name="instanceId"/>.</summary>
    public static string NameFor(Guid instanceId, DateTimeOffset startedUtc, Guid operationId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"hvo-sky-{instanceId:N}-{startedUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{operationId.ToString("N")[..8]}.tar.gz");

    public static bool IsName(string name) => ArchiveName().IsMatch(name);

    public FileStream CreatePartial(string name)
    {
        var path = PathOf(name) + PartialSuffix;
        EnsureFolder(create: true);
        using var gate = Lock();
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1024 * 1024,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        try
        {
            NativeLinux.FlushDirectory(Root);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Makes what <paramref name="stream"/> wrote durable.</summary>
    public static void Sync(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Makes the folder's entries durable.</summary>
    public void SyncFolder() => NativeLinux.FlushDirectory(Root);

    public FileStream OpenRead(string name, bool partial)
    {
        EnsureFolder(create: false);
        if (!partial && ReadRecord(name) is null)
        {
            throw InstallerException.Refused($"'{name}' is not a recorded backup in {Root}.");
        }
        var path = PathOf(name) + (partial ? PartialSuffix : string.Empty);
        if (!File.Exists(path))
        {
            throw InstallerException.Refused($"'{name}' is not in {Root}.");
        }
        return SafeFileSystem.OpenOwnerFileRead(path);
    }

    public void Commit(BackupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsValid(record.Name)) throw InstallerException.Refused("The backup record is not one this installer writes.");
        EnsureFolder(create: false);
        var archive = PathOf(record.Name);
        var partial = archive + PartialSuffix;
        using var gate = Lock();
        // A committed name is never committed again: refused before its record is touched, unless this is that archive.
        if (File.Exists(archive))
        {
            if (ReadRecord(record.Name) == record) return;
            throw InstallerException.Refused($"'{record.Name}' is already a committed backup in {Root}; a backup never replaces another.");
        }
        if (!File.Exists(partial))
        {
            throw new InstallerException($"The partial backup '{record.Name}' is missing, so it cannot be committed.");
        }
        SafeFileSystem.ValidateOwnerFile(partial);
        if (new FileInfo(partial).Length != record.ArchiveLength)
        {
            throw new InstallerException($"The partial backup '{record.Name}' is not the length its record names.");
        }
        // The record comes first: a committed name always has its record, and a record beside a partial is finished by
        // committing again.
        SafeFileSystem.WriteTextAtomic(archive + RecordSuffix, JsonSerializer.Serialize(record, BackupVaultJsonContext.Default.BackupRecord));
        File.Move(partial, archive, overwrite: false);
        NativeLinux.FlushDirectory(Root);
    }

    public void Discard(string name)
    {
        var partial = PathOf(name) + PartialSuffix;
        if (!Directory.Exists(Root) || !File.Exists(partial)) return;
        EnsureFolder(create: false);
        using var gate = Lock();
        if (!File.Exists(partial)) return;
        SafeFileSystem.ValidateOwnerFile(partial);
        File.Delete(partial);
        NativeLinux.FlushDirectory(Root);
    }

    public BackupRecord? ReadRecord(string name)
    {
        var archive = PathOf(name);
        if (!Directory.Exists(Root)) return null;
        EnsureFolder(create: false);
        if (!File.Exists(archive) || !File.Exists(archive + RecordSuffix)) return null;
        SafeFileSystem.ValidateOwnerFile(archive);
        using var stream = SafeFileSystem.OpenOwnerFileRead(archive + RecordSuffix);
        if (stream.Length > MaximumRecordBytes) return null;
        try
        {
            var record = JsonSerializer.Deserialize(stream, BackupVaultJsonContext.Default.BackupRecord);
            return record is not null && record.IsValid(name) && new FileInfo(archive).Length == record.ArchiveLength ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<BackupPartial> Partials(Guid instanceId)
    {
        if (!Directory.Exists(Root)) return [];
        EnsureFolder(create: false);
        var prefix = string.Create(CultureInfo.InvariantCulture, $"hvo-sky-{instanceId:N}-");
        return
        [
            .. new DirectoryInfo(Root).EnumerateFiles($"{prefix}*{PartialSuffix}")
                .Select(static file => (File: file, Name: file.Name[..^PartialSuffix.Length]))
                .Where(static entry => IsName(entry.Name) && entry.File.LinkTarget is null)
                .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
                .Select(static entry => new BackupPartial(entry.Name, entry.File.Length))
        ];
    }

    private string PathOf(string name)
    {
        if (!IsName(name)) throw InstallerException.Refused($"'{name}' is not the name of a backup this installer writes.");
        return Path.Combine(Root, name);
    }

    /// <summary>Serializes every change to the folder, across processes.</summary>
    private OperationLock Lock() => OperationLock.Acquire(Path.Combine(Root, LockName));

    /// <summary>
    /// Requires the folder to be the caller's own, owner-only and not a link, and for root's vault every folder above it to
    /// be root's and writable by root alone, creating it when asked. Its entry in its parent is durable before anything
    /// is written in it.
    /// </summary>
    private void EnsureFolder(bool create)
    {
        if (requireRootAncestry && RootPrivilege.RootAncestryProblem(Root) is { } problem)
        {
            throw InstallerException.Refused(
                $"Backups are kept only in a folder no one but root can change, and {problem}. Give it to root, writable by root alone (sudo chown root:root <folder>; sudo chmod go-w <folder>), then run it again.");
        }
        SafeFileSystem.EnsureSafeExistingAncestors(Root);
        if (!Directory.Exists(Root))
        {
            if (!create) throw InstallerException.Refused($"There is no backup folder {Root}.");
            SafeFileSystem.CreateOwnerDirectory(Root);
            NativeLinux.FlushDirectory(Path.GetDirectoryName(Root)!);
        }
        var identity = NativeLinux.GetDirectoryIdentity(Root);
        if (identity.Uid != NativeLinux.getuid() ||
            (identity.Mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new InstallerException($"The backup folder {Root} must be owner-only and owned by the installer's user.");
        }
    }

    [GeneratedRegex("^hvo-sky-[0-9a-f]{32}-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\\.tar\\.gz$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();
}

/// <summary>
/// A vault in one folder, each operation run through <see cref="IRootWindow"/>. A stream it opens stays usable after the
/// window closes, so an archive streams as the runtime user while only the folder's operations run as root.
/// </summary>
internal class FolderBackupVault(BackupVaultFiles files, IRootWindow window) : IBackupVault
{
    public string Root => files.Root;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned transfer owns the stream and disposes it.")]
    public Task<IBackupTransfer> WriteAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult<IBackupTransfer>(new FileTransfer(window.Run(() => files.CreatePartial(name)), write: true, files, window));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned transfer owns the stream and disposes it.")]
    public Task<IBackupTransfer> ReadAsync(string name, bool partial, CancellationToken cancellationToken)
        => Task.FromResult<IBackupTransfer>(new FileTransfer(window.Run(() => files.OpenRead(name, partial)), write: false, files, window));

    public Task CommitAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        window.Run(() =>
        {
            files.Commit(record);
            return true;
        });
        return Task.CompletedTask;
    }

    public Task DiscardAsync(string name, CancellationToken cancellationToken)
    {
        window.Run(() =>
        {
            files.Discard(name);
            return true;
        });
        return Task.CompletedTask;
    }

    public Task<BackupRecord?> ReadRecordAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult(window.Run(() => files.ReadRecord(name)));

    public Task<IReadOnlyList<BackupPartial>> ListPartialsAsync(Guid instanceId, CancellationToken cancellationToken)
        => Task.FromResult(window.Run(() => files.Partials(instanceId)));

    private sealed class FileTransfer(FileStream stream, bool write, BackupVaultFiles files, IRootWindow window) : IBackupTransfer
    {
        public Stream Stream => stream;

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (write)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                BackupVaultFiles.Sync(stream);
                window.Run(() =>
                {
                    files.SyncFolder();
                    return true;
                });
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

/// <summary>The vault of a test product root, read and written by the user who runs the tests.</summary>
internal sealed class OwnerBackupVault(BackupVaultFiles files) : FolderBackupVault(files, CurrentUserWindow.Instance);

/// <summary>Root's vault, each of its operations run as root while the command otherwise runs as the runtime user.</summary>
internal sealed class RootBackupVault(BackupVaultFiles files, IRootWindow window) : FolderBackupVault(files, window);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(BackupRecord))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class BackupVaultJsonContext : JsonSerializerContext;
