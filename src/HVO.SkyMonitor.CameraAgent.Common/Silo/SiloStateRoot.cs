using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// The instance-owned State directory of the Silo catalog. The physical split (#1965 4.6, chosen by #1967):
/// <list type="bullet">
/// <item><c>publication-state.db</c>: staged groups, the journal writer head and pending segment, and orphan
/// candidates. Not rebuildable; it needs the instance backup.</item>
/// <item><c>catalog-index.db</c>: every searchable index. Entirely rebuildable from the durable journal and
/// manifests; a rebuild replaces only this file.</item>
/// <item><c>spool/</c>: exact bytes of manifests, journal segments and checkpoints, fsynced before their first PUT.</item>
/// </list>
/// One process owns the root through an exclusive lock.
/// </summary>
internal sealed class SiloStateRoot : IDisposable
{
    public const string LockFileName = "silo-catalog.lock";
    public const string PublicationDatabaseFileName = "publication-state.db";
    public const string IndexDatabaseFileName = "catalog-index.db";
    public const string SpoolDirectoryName = "spool";
    private const int MaximumStartupMetadataPartials = 256;

    private FileStream? _lock;

    private SiloStateRoot(PhysicalRoot root, FileStream lockStream)
    {
        Root = root;
        _lock = lockStream;
        Spool = new SiloObjectSpool(root, SpoolDirectoryName);
    }

    public PhysicalRoot Root { get; }

    public SiloObjectSpool Spool { get; }

    public string PublicationDatabasePath => Root.Resolve(PublicationDatabaseFileName);

    public string IndexDatabasePath => Root.Resolve(IndexDatabaseFileName);

    public static SiloStateRoot Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        var root = PhysicalRoot.Open(path);
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(root.Resolve(LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.None);
        }
        catch (IOException exception) when (exception is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidOperationException("Another process already owns this Silo catalog State root.", exception);
        }
        try
        {
            var directory = root.Resolve(SpoolDirectoryName);
            AtomicPublisher.EnsureDirectory(root, directory);
            // The process lock is held and no metadata writer has started.
            if (AtomicPublisher.CleanupTemporaries(root, directory, MaximumStartupMetadataPartials) > 0)
            {
                DurableSync.Directory(directory);
            }
            return new SiloStateRoot(root, lockStream);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

/// <summary>Durable exact bytes, named by their SHA-256, for exact-byte replay of uncertain writes.</summary>
internal sealed class SiloObjectSpool(PhysicalRoot root, string directoryName)
{
    private const string Suffix = ".obj";

    public static string NameOf(string sha256)
    {
        SiloHash.Require(sha256, nameof(sha256));
        return sha256 + Suffix;
    }

    /// <summary>Write and fsync the bytes before any PUT. An existing intact copy is kept; a damaged one is replaced.</summary>
    public async ValueTask<string> WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var sha = SiloHash.Sha256(bytes);
        var name = NameOf(sha);
        var existing = await ReadAsync(name, sha, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return name;
        }
        await AtomicPublisher.PublishAsync(root, Relative(name), PublishMode.Replace,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
        return name;
    }

    /// <summary>The intact bytes, or null when absent or damaged.</summary>
    public async ValueTask<byte[]?> ReadAsync(string name, string expectedSha256, CancellationToken cancellationToken)
    {
        var path = root.Resolve(Relative(name));
        if (!File.Exists(path))
        {
            return null;
        }
        root.Verify(path, "silo-spool-read");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(SiloHash.Sha256(bytes), expectedSha256, StringComparison.Ordinal) ? bytes : null;
    }

    public void Delete(string name)
    {
        var path = root.Resolve(Relative(name));
        if (File.Exists(path))
        {
            root.Verify(path, "silo-spool-delete");
            File.Delete(path);
        }
    }

    public IReadOnlyList<string> Names()
    {
        var directory = root.Resolve(directoryName);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*" + Suffix).Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    private string Relative(string name)
    {
        if (!name.EndsWith(Suffix, StringComparison.Ordinal) || !SiloHash.IsValid(name[..^Suffix.Length]))
        {
            throw new ArgumentException("A spool name is a SHA-256 with the spool suffix.", nameof(name));
        }
        return Path.Combine(directoryName, name);
    }
}
