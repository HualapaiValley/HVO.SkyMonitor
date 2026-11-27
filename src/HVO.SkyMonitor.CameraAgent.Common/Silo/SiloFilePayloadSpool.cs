using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This internal exception identifies exhausted staging capacity.")]
internal sealed class SiloSpoolFullException() : IOException("The Silo payload spool has reached its configured capacity.");

/// <summary>
/// Fsynced, content-addressed files for unpublished members. The instance root lock excludes another process.
/// Within it, the publisher gate covers staging, ownership changes, release and recovery.
/// </summary>
internal sealed class SiloFilePayloadSpool : ISiloPayloadSpool
{
    private const string DirectoryName = "payloads-v1";
    private const string Suffix = ".payload";
    private readonly PhysicalRoot _root;
    private readonly string _relativeDirectory;
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly int _maximumFiles;
    private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);
    private long _bytes;

    public SiloFilePayloadSpool(SiloStateRoot state, long maximumBytes, int maximumFiles)
        : this(state?.Root ?? throw new ArgumentNullException(nameof(state)), DirectoryName, maximumBytes, maximumFiles)
    {
        if (!DurableSync.SupportsDirectorySync)
        {
            throw new PlatformNotSupportedException("Durable Silo payload staging requires directory fsync.");
        }
        AtomicPublisher.EnsureDirectory(_root, _directory);
        LoadFiles(removePartials: true);
    }

    private SiloFilePayloadSpool(PhysicalRoot root, string relativeDirectory, long maximumBytes, int maximumFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        _root = root;
        _relativeDirectory = relativeDirectory;
        _directory = root.Resolve(relativeDirectory);
        _maximumBytes = maximumBytes;
        _maximumFiles = maximumFiles;
    }

    internal static Inspection Inspect(PhysicalRoot root, string relativeState, long maximumBytes, int maximumFiles)
    {
        var spool = new SiloFilePayloadSpool(root, Path.Combine(relativeState, DirectoryName), maximumBytes, maximumFiles);
        if (Directory.Exists(spool._directory))
            spool.LoadFiles(removePartials: false);
        else if (File.Exists(spool._directory))
            throw new IOException("The Silo payload spool path is not a directory.");
        return new(spool);
    }

    // Forecast staging and release in memory. Only files present at inspection are opened for verification.
    internal sealed class Inspection(SiloFilePayloadSpool spool)
    {
        private readonly HashSet<string> _projected = new(StringComparer.Ordinal);

        internal async ValueTask StageAsync(IReadOnlyList<SiloDraftMember> members, CancellationToken cancellationToken)
        {
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await spool.RequiresStagingAsync(member, _projected.Contains(member.SourceToken), cancellationToken).ConfigureAwait(false))
                {
                    spool.Track(member.SourceToken, member.Length);
                    _projected.Add(member.SourceToken);
                }
            }
        }

        internal async ValueTask<bool> MatchesAsync(SiloDraftMember member, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _projected.Contains(member.SourceToken)
                ? spool._files[member.SourceToken] == member.Length
                : await spool.MatchesAsync(member.SourceToken, member.Sha256, member.Length, cancellationToken).ConfigureAwait(false);
        }

        internal void RequireWithinCapacity() => spool.RequireWithinCapacity();

        internal void Release(string token)
        {
            _projected.Remove(token);
            spool.Forget(token);
        }
    }

    public static string TokenOf(string sha256)
    {
        SiloHash.Require(sha256, nameof(sha256));
        return sha256 + Suffix;
    }

    public async ValueTask StageAsync(SiloDraftMember member,
        Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open,
        CancellationToken cancellationToken)
    {
        if (!await RequiresStagingAsync(member, projected: false, cancellationToken).ConfigureAwait(false))
        {
            DurableSync.Directory(_directory);
            return;
        }

        var relative = WithinSpool(member.SourceToken);
        var path = _root.Resolve(relative);
        try
        {
            await AtomicPublisher.PublishAsync(_root, relative, PublishMode.CreateNew, async (output, ct) =>
            {
                var input = await open(member, ct).ConfigureAwait(false);
                await using var lease = input.ConfigureAwait(false);
                await CopyVerifiedAsync(input, output, member, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileSystemFaultException exception) when (exception.Kind == FileSystemFaultKind.Cancelled && cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            // A failure after rename may leave a final file. Charge it even before a database claim exists.
            if (File.Exists(path))
            {
                Track(new FileInfo(path));
            }
        }
    }

    private async ValueTask<bool> RequiresStagingAsync(SiloDraftMember member, bool projected, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(member.Length);
        var token = TokenOf(member.Sha256);
        if (token != member.SourceToken)
            throw new ArgumentException("The staged source token must name the member's SHA-256.", nameof(member));

        RequireWithinCapacity();

        if (_files.TryGetValue(token, out var length))
        {
            if (length != member.Length || !projected && !await MatchesAsync(token, member.Sha256, length, cancellationToken).ConfigureAwait(false))
                throw new SiloConflictException("silo.staged-payload-damaged", "An existing staged payload differs from its identity.");
            return false;
        }
        if (_files.Count >= _maximumFiles || member.Length > _maximumBytes - _bytes)
            throw new SiloSpoolFullException();
        return true;
    }

    public void RequireWithinCapacity()
    {
        if (_bytes > _maximumBytes || _files.Count > _maximumFiles)
            throw new SiloSpoolFullException();
    }

    private async ValueTask<bool> MatchesAsync(string token, string sha256, long length, CancellationToken cancellationToken)
    {
        using var existing = Open(token);
        return await MatchesAsync(existing, sha256, length, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<bool> MatchesAsync(Stream existing, string sha256, long length, CancellationToken cancellationToken) =>
        existing.Length == length &&
        string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false)),
            sha256, StringComparison.Ordinal);

    private static async Task CopyVerifiedAsync(Stream input, Stream output, SiloDraftMember member, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var left = member.Length;
            while (left > 0)
            {
                var count = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new SiloConflictException("silo.staged-payload-length", "The supplied payload ended before its declared length.");
                }
                left -= count;
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0 ||
                !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), member.Sha256, StringComparison.Ordinal))
            {
                throw new SiloConflictException("silo.staged-payload-identity", "The supplied payload differs from its declared length or digest.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the opened stream passes to the caller.")]
    public ValueTask<Stream> OpenAsync(string sourceToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(Open(sourceToken));
    }

    private FileStream Open(string sourceToken)
    {
        var path = _root.Resolve(WithinSpool(sourceToken));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public ValueTask ReleaseAsync(string sourceToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Release(sourceToken);
        return ValueTask.CompletedTask;
    }

    private void Release(string sourceToken)
    {
        var path = _root.Resolve(WithinSpool(sourceToken));
        File.Delete(path);
        DurableSync.Directory(_directory);
        Forget(sourceToken);
    }

    private void Forget(string sourceToken)
    {
        if (_files.Remove(sourceToken, out var length))
        {
            _bytes -= length;
        }
    }

    public ValueTask ReconcileAsync(IReadOnlySet<string> claimedTokens, CancellationToken cancellationToken)
    {
        foreach (var token in _files.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!claimedTokens.Contains(token))
            {
                Release(token);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void LoadFiles(bool removePartials)
    {
        // Adoption only inspects this instance's bounded payload directory. A partial never carries a journal claim.
        long examined = 0;
        var removed = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(_directory))
        {
            _root.Verify(path, "silo-payload-spool-start");
            var info = new FileInfo(path);
            if (IsPartialName(info.Name))
            {
                if (removePartials)
                {
                    File.Delete(path);
                    removed = true;
                }
            }
            else
            {
                if (++examined > (long)_maximumFiles + 1)
                    throw new SiloSpoolFullException();
                _ = WithinSpool(info.Name);
                Track(info);
            }
        }
        if (removed)
        {
            DurableSync.Directory(_directory);
        }
    }

    private void Track(FileInfo info)
    {
        _root.Verify(info.FullName, "silo-payload-spool-account");
        Track(info.Name, info.Length);
    }

    private void Track(string token, long length)
    {
        _files.Add(token, length);
        _bytes = checked(_bytes + length);
    }

    private string WithinSpool(string token)
    {
        if (!token.EndsWith(Suffix, StringComparison.Ordinal) || !SiloHash.IsValid(token[..^Suffix.Length]))
        {
            throw new ArgumentException("A staged payload token is a SHA-256 and payload suffix.", nameof(token));
        }
        return Path.Combine(_relativeDirectory, token);
    }

    private static bool IsPartialName(string name)
    {
        const int digestLength = 64;
        const int guidLength = 32;
        var tokenLength = digestLength + Suffix.Length;
        return name.Length == tokenLength + 1 + guidLength + 4 && name.EndsWith(".tmp", StringComparison.Ordinal) &&
            name[tokenLength] == '.' &&
            name.AsSpan(digestLength, Suffix.Length).SequenceEqual(Suffix) &&
            SiloHash.IsValid(name[..digestLength]) &&
            Guid.TryParseExact(name.AsSpan(tokenLength + 1, guidLength), "N", out _);
    }
}
