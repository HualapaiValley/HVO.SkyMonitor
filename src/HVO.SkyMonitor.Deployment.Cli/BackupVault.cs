using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The record a backup writes beside its archive when it commits it, once the archive has been read back and proved
/// whole (#1353). Restore reads only an archive with its record, and requires the archive's bytes to be the ones the
/// record names, so a partial or unrecorded archive is never usable. It names the account the backup ran as, verified as
/// the instance's service account (PR-1369-R2 F13), which a restore of an absent instance must run as.
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
/// Where instance backups are kept: <c>&lt;product-root&gt;/backups</c>, the instance's service account's own folder, mode
/// 0700, holding each committed archive and its record, mode 0600, and any partial a backup began (#1353). Backups are
/// owner-only (owner decision 2026-10-18): the service account owns every live store and secret and is in the
/// <c>docker</c> group, so the account that can read the instance can read its backups, and no one else can.
/// </summary>
internal interface IBackupVault
{
    /// <summary>The folder, for messages.</summary>
    string Root { get; }

    /// <summary>
    /// Refuses (exit 3) a folder backups cannot be kept in, before anything is written and without creating anything,
    /// whether or not it exists yet (#1353 PR-1369-R2 F18; <see cref="BackupVaultFiles.Problem"/>).
    /// </summary>
    void Admit();

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
    /// <summary>The vault's folder under the product root.</summary>
    public const string FolderName = "backups";

    /// <summary>Replaces the vault a lifecycle command uses, given the product root. Tests set it; production leaves it unset.</summary>
    internal static Func<string, IBackupVault>? Override { get; set; }

    /// <summary>The vault of <paramref name="request"/>'s product root, <c>&lt;product-root&gt;/backups</c>.</summary>
    public static IBackupVault For(LifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Override?.Invoke(request.ProductRoot)
               ?? new OwnerBackupVault(new BackupVaultFiles(Path.Combine(request.ProductRoot, FolderName)));
    }
}

/// <summary>
/// The vault's files, read and written by the service account that owns them. Every name is one this installer gives an
/// archive, every file is opened without following a link and must be a single-link regular file owned by the caller
/// with mode 0600, every change happens under the vault's lock, and every step a later one depends on is durable before
/// it returns. The folder itself must pass <see cref="Problem"/> before anything in it is read or written.
/// </summary>
internal sealed partial class BackupVaultFiles(string root)
{
    /// <summary>The file type statx reports for a directory.</summary>
    private const uint DirectoryType = 0x4000;

    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

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
    /// Why the folder cannot hold backups, or <see langword="null"/> when it can (#1353 PR-1369-R2 F18). Each existing
    /// folder above it must be a folder, not a link, that no group or other user can write, unless its sticky bit keeps
    /// them from renaming what is not theirs, so no one else can put another folder in its place. The folder itself, once
    /// it exists, must be a folder, not a link, owned by the account running the command, with mode 0700. A folder that
    /// does not exist yet is created with that mode on the first write. Only reads metadata; creates nothing.
    /// </summary>
    public string? Problem()
    {
        for (var current = Path.GetDirectoryName(Root); current is not null; current = Path.GetDirectoryName(current))
        {
            if (NativeLinux.TryGetNodeIdentity(current) is not { } ancestor) continue;
            if (ancestor.Type != DirectoryType) return $"{current}, which holds it, is not a folder";
            if ((ancestor.Mode & GroupOrOtherWrite) != 0 && (ancestor.Mode & UnixFileMode.StickyBit) == 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{current}, which holds it, has mode {Convert.ToString((int)ancestor.Mode & 0xFFF, 8)}, so someone else can replace what is in it. Remove the group and other write permissions (chmod go-w {current})");
            }
        }
        if (NativeLinux.TryGetNodeIdentity(Root) is not { } folder) return null;
        if (folder.Type != DirectoryType) return $"{Root} is not a folder";
        if (folder.Uid != NativeLinux.getuid())
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Root} belongs to uid {folder.Uid}, not to the account running the backup, uid {NativeLinux.getuid()}. Give it to the instance's service account");
        }
        if ((folder.Mode & GroupOrOtherAccess) != 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Root} has mode {Convert.ToString((int)folder.Mode & 0xFFF, 8)}, so others can read the backups it holds. Make it the service account's alone (chmod 700 {Root})");
        }
        return null;
    }

    /// <summary>Refuses (exit 3) a folder that cannot hold backups (<see cref="Problem"/>); creates nothing.</summary>
    public void Admit()
    {
        if (Problem() is { } problem)
        {
            throw InstallerException.Refused(
                $"Backups are kept only in the service account's own folder, mode 0700, in folders no one else can change, and {problem}. Then run it again. Nothing was changed.");
        }
    }

    /// <summary>
    /// Requires the folder to pass <see cref="Admit"/>, creating it with mode 0700 when asked. Its entry in its parent is
    /// durable before anything is written in it.
    /// </summary>
    private void EnsureFolder(bool create)
    {
        Admit();
        if (!Directory.Exists(Root))
        {
            if (!create) throw InstallerException.Refused($"There is no backup folder {Root}.");
            SafeFileSystem.CreateOwnerDirectory(Root);
            NativeLinux.FlushDirectory(Path.GetDirectoryName(Root)!);
            Admit();
        }
    }

    [GeneratedRegex("^hvo-sky-[0-9a-f]{32}-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\\.tar\\.gz$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();
}

/// <summary>The service account's vault, each operation run as the account the command runs as.</summary>
internal sealed class OwnerBackupVault(BackupVaultFiles files) : IBackupVault
{
    public string Root => files.Root;

    public void Admit() => files.Admit();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned transfer owns the stream and disposes it.")]
    public Task<IBackupTransfer> WriteAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult<IBackupTransfer>(new FileTransfer(files.CreatePartial(name), write: true, files));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned transfer owns the stream and disposes it.")]
    public Task<IBackupTransfer> ReadAsync(string name, bool partial, CancellationToken cancellationToken)
        => Task.FromResult<IBackupTransfer>(new FileTransfer(files.OpenRead(name, partial), write: false, files));

    public Task CommitAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        files.Commit(record);
        return Task.CompletedTask;
    }

    public Task DiscardAsync(string name, CancellationToken cancellationToken)
    {
        files.Discard(name);
        return Task.CompletedTask;
    }

    public Task<BackupRecord?> ReadRecordAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult(files.ReadRecord(name));

    public Task<IReadOnlyList<BackupPartial>> ListPartialsAsync(Guid instanceId, CancellationToken cancellationToken)
        => Task.FromResult(files.Partials(instanceId));

    private sealed class FileTransfer(FileStream stream, bool write, BackupVaultFiles files) : IBackupTransfer
    {
        public Stream Stream => stream;

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (write)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                BackupVaultFiles.Sync(stream);
                files.SyncFolder();
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(BackupRecord))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class BackupVaultJsonContext : JsonSerializerContext;
