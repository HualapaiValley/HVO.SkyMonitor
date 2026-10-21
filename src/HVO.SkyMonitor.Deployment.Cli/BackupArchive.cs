using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The format of an instance backup (#1353): a gzip-compressed PAX tar holding the instance's files under
/// <c>instance/</c>, the verified snapshots of its SQLite stores under <c>snapshots/</c>, and, as its last entry, the
/// manifest <c>hvo-sky-backup.json</c>, which names every entry with its type, mode, size and SHA-256. An archive is
/// whole only when it ends with a manifest that names exactly the entries before it, byte for byte; anything else,
/// a truncated or partial archive included, is refused before anything is changed.
/// </summary>
internal static class BackupArchive
{
    public const int SchemaVersion = 1;
    public const string ManifestName = "hvo-sky-backup.json";
    public const string InstancePrefix = "instance/";
    public const string SnapshotPrefix = "snapshots/";
    public const string Product = "HVO.SkyMonitor";
    public const string Component = "CameraAgent";

    /// <summary>The largest manifest read: room for hundreds of thousands of entries.</summary>
    public const int MaximumManifestBytes = 128 * 1024 * 1024;

    /// <summary>The bytes a tar entry costs beyond its data, PAX extended header included, rounded up.</summary>
    private const long EntryOverheadBytes = 3 * 512;

    /// <summary>
    /// The space an archive of <paramref name="items"/> can take, compressed, at most: its tar, with every entry's
    /// header and padding, and gzip's worst case for data that does not compress.
    /// </summary>
    public static long EstimateArchiveBytes(IReadOnlyList<BackupItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        long tar = 1024;
        foreach (var item in items)
        {
            tar = checked(tar + EntryOverheadBytes + (item.Size + 511) / 512 * 512);
        }
        // Deflate stores incompressible data in blocks of at most 64 KiB, each with a five-byte header.
        return checked(tar + tar / 65_535 * 5 + 1024 * 1024);
    }

    /// <summary>
    /// Writes <paramref name="items"/>, then the manifest built from what was read, to <paramref name="destination"/>.
    /// Each file is hashed as it is written, and must still be the size it was when it was collected.
    /// </summary>
    public static async Task<BackupArchiveManifest> WriteAsync(
        Stream destination,
        IReadOnlyList<BackupItem> items,
        Func<IReadOnlyList<BackupArchiveEntry>, long, BackupArchiveManifest> createManifest,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(createManifest);
        var total = items.Sum(static item => item.Size);
        long written = 0;
        var entries = new List<BackupArchiveEntry>(items.Count);
        await using (var gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true))
        {
            await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Directory)
                    {
                        var directory = new PaxTarEntry(TarEntryType.Directory, item.Name) { Mode = item.Mode };
                        await tar.WriteEntryAsync(directory, cancellationToken).ConfigureAwait(false);
                        entries.Add(new BackupArchiveEntry(item.Name, BackupArchiveEntry.DirectoryType, FormatMode(item.Mode), 0, null));
                        continue;
                    }
                    entries.Add(await WriteFileAsync(tar, item, cancellationToken).ConfigureAwait(false));
                    written += item.Size;
                    progress?.Invoke(written, total);
                }
                var manifest = createManifest(entries, total);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupArchiveJsonContext.Default.BackupArchiveManifest);
                using var manifestStream = new MemoryStream(bytes, writable: false);
                await tar.WriteEntryAsync(
                        new PaxTarEntry(TarEntryType.RegularFile, ManifestName)
                        {
                            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                            DataStream = manifestStream
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return manifest;
            }
        }
    }

    /// <summary>
    /// Writes one file, hashing it as it is read; it must still be the size it was when it was collected.
    /// </summary>
    private static async Task<BackupArchiveEntry> WriteFileAsync(TarWriter tar, BackupItem item, CancellationToken cancellationToken)
    {
        var source = SafeFileSystem.OpenRegularFileRead(item.Source!);
        try
        {
            if (source.Length != item.Size)
            {
                throw new InstallerException($"'{CameraAgentStateDiagnostics.Reason(item.Source!)}' changed size while it was backed up.");
            }
            using var hashing = new HashingReadStream(source, item.Size);
            var file = new PaxTarEntry(TarEntryType.RegularFile, item.Name) { Mode = item.Mode, DataStream = hashing };
            await tar.WriteEntryAsync(file, cancellationToken).ConfigureAwait(false);
            if (hashing.BytesRead != item.Size)
            {
                throw new InstallerException($"'{CameraAgentStateDiagnostics.Reason(item.Source!)}' changed size while it was backed up.");
            }
            return new BackupArchiveEntry(item.Name, BackupArchiveEntry.FileType, FormatMode(item.Mode), item.Size, hashing.Sha256());
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads an archive from <paramref name="source"/> and proves it whole: every entry has a canonical name under
    /// <c>instance/</c> or <c>snapshots/</c>, appears once, is a directory or a regular file, and is named by the
    /// manifest that comes last, with the same type, mode, size and SHA-256; and the manifest names nothing else.
    /// <paramref name="extract"/>, when given, receives each entry's data as it is read and hashed, so a restore stages
    /// what it verifies; a caller extracts only after a first pass without it has proved the archive.
    /// </summary>
    public static async Task<BackupArchiveManifest> ReadAsync(
        Stream source,
        Func<string, bool, UnixFileMode, Stream?, CancellationToken, Task>? extract,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var seen = new Dictionary<string, BackupArchiveEntry>(StringComparer.Ordinal);
        BackupArchiveManifest? manifest = null;
        long read = 0;
        try
        {
            await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new TarReader(gzip, leaveOpen: true);
            while (await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (manifest is not null)
                {
                    throw Invalid("it has entries after its manifest");
                }
                if (entry.Name == ManifestName)
                {
                    if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null || entry.Length > MaximumManifestBytes)
                    {
                        throw Invalid("its manifest is not a file the installer reads");
                    }
                    using var buffer = new MemoryStream();
                    await entry.DataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    manifest = ParseManifest(buffer.ToArray());
                    continue;
                }
                if (NameProblem(entry.Name, entry.EntryType) is { } problem)
                {
                    throw Invalid(problem);
                }
                var mode = (UnixFileMode)((int)entry.Mode & 0x1FF);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    if (!seen.TryAdd(entry.Name, new BackupArchiveEntry(entry.Name, BackupArchiveEntry.DirectoryType, FormatMode(mode), 0, null)))
                    {
                        throw Invalid($"it holds {CameraAgentStateDiagnostics.Reason(entry.Name)} twice");
                    }
                    if (extract is not null)
                    {
                        await ExtractAsync(extract, entry.Name, true, mode, null, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }
                await using var data = new HashingReadStream(entry.DataStream ?? Stream.Null, entry.Length);
                if (extract is not null)
                {
                    await ExtractAsync(extract, entry.Name, false, mode, data, cancellationToken).ConfigureAwait(false);
                }
                await data.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                if (data.BytesRead != entry.Length)
                {
                    throw Invalid($"its {CameraAgentStateDiagnostics.Reason(entry.Name)} is shorter than its header says");
                }
                if (!seen.TryAdd(entry.Name, new BackupArchiveEntry(entry.Name, BackupArchiveEntry.FileType, FormatMode(mode), entry.Length, data.Sha256())))
                {
                    throw Invalid($"it holds {CameraAgentStateDiagnostics.Reason(entry.Name)} twice");
                }
                read += entry.Length;
                progress?.Invoke(read);
            }
            // After the end marker the decompressed stream may hold only the zero padding a tar writer adds, to its end:
            // anything else, a further gzip member included, or a decompressor error, means the archive is not one whole
            // backup. The bytes themselves are bound by the record's SHA-256 and length; this proves nothing more about them.
            var tail = new byte[64 * 1024];
            int count;
            while ((count = await gzip.ReadAsync(tail, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (tail.AsSpan(0, count).ContainsAnyExcept((byte)0))
                {
                    throw Invalid("it holds data after its end");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or FormatException or IOException)
        {
            throw Invalid($"it cannot be read to its end ({CameraAgentStateDiagnostics.Reason(exception.Message)})");
        }
        if (manifest is null)
        {
            throw Invalid("it ends without its manifest, so it is not a whole backup");
        }
        var listed = new Dictionary<string, BackupArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (!listed.TryAdd(entry.Path, entry))
            {
                throw Invalid($"its manifest names {CameraAgentStateDiagnostics.Reason(entry.Path)} twice");
            }
        }
        foreach (var (name, entry) in seen)
        {
            if (!listed.TryGetValue(name, out var expected) || expected != entry)
            {
                throw Invalid($"its {CameraAgentStateDiagnostics.Reason(name)} is not the one its manifest names");
            }
        }
        if (listed.Keys.FirstOrDefault(name => !seen.ContainsKey(name)) is { } missing)
        {
            throw Invalid($"it does not hold {CameraAgentStateDiagnostics.Reason(missing)}, which its manifest names");
        }
        if (manifest.TotalBytes != manifest.Entries.Sum(static entry => entry.Size))
        {
            throw Invalid("its manifest's total is not the sum of its files");
        }
        return manifest;
    }

    /// <summary>
    /// Hands one entry to the caller. A file the caller cannot write, a full disk for one, is a failed step rather than
    /// a damaged archive, so it is not reported as one.
    /// </summary>
    private static async Task ExtractAsync(
        Func<string, bool, UnixFileMode, Stream?, CancellationToken, Task> extract,
        string name,
        bool directory,
        UnixFileMode mode,
        Stream? data,
        CancellationToken cancellationToken)
    {
        try
        {
            await extract(name, directory, mode, data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerException($"{CameraAgentStateDiagnostics.Reason(name)} could not be restored: {CameraAgentStateDiagnostics.Reason(exception.Message)}", exception);
        }
    }

    /// <summary>Why <paramref name="name"/> is not the canonical name of a backed-up entry, or null when it is.</summary>
    internal static string? NameProblem(string name, TarEntryType type)
    {
        var directory = type == TarEntryType.Directory;
        if (type is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            return $"its {CameraAgentStateDiagnostics.Reason(name)} is neither a file nor a folder";
        }
        if (!name.StartsWith(InstancePrefix, StringComparison.Ordinal) && !name.StartsWith(SnapshotPrefix, StringComparison.Ordinal))
        {
            return $"it holds {CameraAgentStateDiagnostics.Reason(name)}, outside the instance and its snapshot";
        }
        // A folder's name ends in exactly one '/'; with it removed, no segment may be empty.
        var trimmed = directory ? name[..^1] : name;
        if (directory != name.EndsWith('/') || name.Length > 4096 || name.Any(char.IsControl) || name.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            return $"it holds an entry named '{CameraAgentStateDiagnostics.Reason(name)}', which is not the canonical spelling of a path";
        }
        return null;
    }

    public static string FormatMode(UnixFileMode mode)
        => Convert.ToString((int)mode & 0x1FF, 8).PadLeft(4, '0');

    private static BackupArchiveManifest ParseManifest(byte[] bytes)
    {
        BackupArchiveManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(bytes, BackupArchiveJsonContext.Default.BackupArchiveManifest)
                ?? throw Invalid("its manifest is empty");
        }
        catch (JsonException exception)
        {
            throw Invalid($"its manifest is not valid ({CameraAgentStateDiagnostics.Reason(exception.Message)})");
        }
        if (manifest.SchemaVersion > SchemaVersion)
        {
            throw Invalid(string.Create(
                CultureInfo.InvariantCulture,
                $"it is schema {manifest.SchemaVersion}, and this installer reads schema {SchemaVersion}"));
        }
        if (manifest.SchemaVersion != SchemaVersion || manifest.Product != Product || manifest.Component != Component ||
            manifest.InstanceId == Guid.Empty || manifest.OperationId == Guid.Empty || manifest.Entries is null ||
            manifest.Omitted is null || manifest.StateSnapshot is null || manifest.TotalBytes < 0 ||
            manifest.Entries.Any(static entry => entry is null || NameProblem(
                entry.Path, entry.Type == BackupArchiveEntry.DirectoryType ? TarEntryType.Directory : TarEntryType.RegularFile) is not null ||
                entry.Type is not (BackupArchiveEntry.DirectoryType or BackupArchiveEntry.FileType) || entry.Size < 0 ||
                entry.Type == BackupArchiveEntry.FileType && (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(Uri.IsHexDigit))))
        {
            throw Invalid("its manifest is not one this installer wrote");
        }
        return manifest;
    }

    private static InstallerException Invalid(string reason)
        => InstallerException.Refused($"The backup is not one the installer can restore: {reason}. Nothing was changed.");
}

/// <summary>One thing a backup holds: a folder, or a file read from <see cref="Source"/>, under its archive name.</summary>
internal sealed record BackupItem(string Name, string? Source, bool Directory, UnixFileMode Mode, long Size);

/// <summary>The manifest of an instance backup (schema <see cref="BackupArchive.SchemaVersion"/>).</summary>
internal sealed record BackupArchiveManifest(
    int SchemaVersion,
    string Product,
    string Component,
    Guid InstanceId,
    string FriendlyName,
    Guid OperationId,
    DateTimeOffset CreatedUtc,
    string InstallerVersion,
    string ProductRoot,
    uint RuntimeUid,
    uint RuntimeGid,
    string ImageId,
    string? ReleaseVersion,
    string CatalogId,
    string CatalogPackageVersion,
    LifecycleStateSnapshot StateSnapshot,
    IReadOnlyList<BackupArchiveEntry> Entries,
    IReadOnlyList<string> Omitted,
    long TotalBytes);

/// <summary>One entry of a backup's manifest: its archive name, its type, its permission bits and, for a file, its size and SHA-256.</summary>
internal sealed record BackupArchiveEntry(string Path, string Type, string Mode, long Size, string? Sha256)
{
    public const string FileType = "file";
    public const string DirectoryType = "directory";
}

/// <summary>Reads a stream of known length, hashing what it reads; it reports the length so a tar writer streams it.</summary>
internal sealed class HashingReadStream(Stream inner, long length) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesRead { get; private set; }

    public string Sha256() => Convert.ToHexStringLower(hash.GetCurrentHash());

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => BytesRead;
        set
        {
            if (value != BytesRead) throw new NotSupportedException("A hashed stream is read once, in order.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, Math.Max(0, length - BytesRead))]);
        hash.AppendData(buffer[..read]);
        BytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Math.Max(0, length - BytesRead))], cancellationToken)
            .ConfigureAwait(false);
        hash.AppendData(buffer.Span[..read]);
        BytesRead += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
        => origin == SeekOrigin.Current && offset == 0 || origin == SeekOrigin.Begin && offset == BytesRead
            ? BytesRead
            : throw new NotSupportedException("A hashed stream is read once, in order.");

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) hash.Dispose();
        base.Dispose(disposing);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(BackupArchiveManifest))]
internal sealed partial class BackupArchiveJsonContext : JsonSerializerContext;
