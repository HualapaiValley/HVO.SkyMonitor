using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// An operator-staged Silo image archive (#1966) copied once into a private file that no path names, verified there, and
/// loaded from there. The staged file is read once, without following a symbolic link; the copy is created owner-only
/// in a fresh owner-only directory and unlinked at once, so only this handle reaches it. The copy never holds more than
/// the archive size bound, even if the staged file grows while it is read. Docker reads the copy through
/// <see cref="DescriptorPath"/>, the handle's <c>/proc</c> entry, so it loads exactly the bytes that were verified even
/// if the staged path is replaced, renamed or written to afterwards. Nothing remains after disposal or a crash.
/// </summary>
internal sealed class SiloImageSnapshot : IDisposable
{
    private readonly FileStream stream;

    private SiloImageSnapshot(FileStream stream, string name, string sha256)
    {
        this.stream = stream;
        Name = name;
        Sha256 = sha256;
    }

    /// <summary>The staged archive's file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The SHA-256 of the verified bytes.</summary>
    public string Sha256 { get; }

    /// <summary>The path through which another process on this host opens exactly the verified bytes.</summary>
    public string DescriptorPath =>
        $"/proc/{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}/fd/{stream.SafeFileHandle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Copies the staged archive at <paramref name="path"/> into a private snapshot and verifies the snapshot as the pinned
    /// image for <paramref name="platform"/>. Refuses, keeping nothing, when it is not.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The snapshot owns the stream it returns; every failure path disposes it.")]
    public static async Task<SiloImageSnapshot> CreateAsync(string path, SiloImagePin.PlatformImage platform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var name = Path.GetFileName(path);
        var directory = Directory.CreateTempSubdirectory("hvo-silo-image-");
        FileStream? snapshot = null;
        try
        {
            var file = Path.Combine(directory.FullName, "archive.tar");
            snapshot = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                // Docker opens it through /proc as this user; nothing else can name it once it is unlinked.
                Share = FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                BufferSize = 128 * 1024
            });
            File.Delete(file);
            directory.Delete();
            await using (var staged = SafeFileSystem.OpenRegularFileRead(path))
                await CopyStagedAsync(staged, snapshot, ReleaseImageArchive.MaximumSiloArchiveBytes, name, cancellationToken).ConfigureAwait(false);
            await snapshot.FlushAsync(cancellationToken).ConfigureAwait(false);
            snapshot.Position = 0;
            await ReleaseImageArchive.VerifySiloArchiveAsync(snapshot, name, platform, cancellationToken).ConfigureAwait(false);
            snapshot.Position = 0;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(snapshot, cancellationToken).ConfigureAwait(false));
            snapshot.Position = 0;
            var created = new SiloImageSnapshot(snapshot, name, sha256);
            snapshot = null;
            return created;
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.DisposeAsync().ConfigureAwait(false);
            directory.Refresh();
            if (directory.Exists)
                directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Copies <paramref name="staged"/> into <paramref name="snapshot"/>, refusing it when it holds more than
    /// <paramref name="maximumBytes"/>. Its length is checked first, and then every read, because the staged file can grow
    /// after its length was read: each read asks for at most one byte more than may still be written, so a read that
    /// would take the copy past the bound is refused before any of it is written.
    /// </summary>
    internal static async Task CopyStagedAsync(Stream staged, Stream snapshot, long maximumBytes, string name, CancellationToken cancellationToken)
    {
        if (staged.Length > maximumBytes)
            throw TooLarge(name);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var copied = 0L;
            while (true)
            {
                var allowed = maximumBytes - copied;
                var read = await staged.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, allowed + 1)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return;
                if (read > allowed)
                    throw TooLarge(name);
                await snapshot.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static InstallerException TooLarge(string name) =>
        InstallerException.Refused($"The staged Silo archive {name} is larger than any image of the pinned release.");

    public void Dispose() => stream.Dispose();
}

/// <summary>The verified snapshot an install holds from admission until its image phase loads it, disposed with the install.</summary>
internal sealed class SiloImageSnapshotHolder : IDisposable
{
    public SiloImageSnapshot? Snapshot { get; set; }

    public void Dispose() => Snapshot?.Dispose();
}
