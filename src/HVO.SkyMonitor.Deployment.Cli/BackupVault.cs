using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The record a backup writes beside its archive when it commits it, once the archive has been read back and proved
/// whole (#1353). Restore reads only an archive with its record, and requires the archive's bytes to be the ones the
/// record names, so a partial or unrecorded archive is never usable.
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
    int Entries)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Whether this is a record this installer writes, for the archive it names.</summary>
    public bool IsValid(string name)
        => SchemaVersion == CurrentSchemaVersion && string.Equals(Name, name, StringComparison.Ordinal) &&
           BackupVaultFiles.IsName(name) && InstanceId != Guid.Empty && OperationId != Guid.Empty &&
           ArchiveLength > 0 && TotalBytes >= 0 && Entries >= 0 &&
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
    /// For a write, makes everything written durable; for a read, reads what is left and proves the read ended cleanly.
    /// A transfer disposed without completing is abandoned, and a partial it wrote stays until it is discarded.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Where instance backups are kept: <c>&lt;product-root&gt;/backups</c>, one owner-only folder holding each committed archive,
/// its record, and any partial a backup began. Under the production product root the folder and every file in it are
/// <c>root:root</c> and owner-only, so only root can read a backup: the runtime user reaches them through one narrow
/// <c>sudo</c> helper (<see cref="BackupVaultHelper"/>). Under a test product root they belong to the runtime user.
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
    /// Commits a partial: records <paramref name="record"/> durably, then gives the partial its archive name. Committing
    /// again is a no-op once it is done, and finishes it when an interruption stopped it between the two.
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
    /// <summary>The vault's folder under a product root.</summary>
    public const string FolderName = "backups";

    /// <summary>Replaces the vault a lifecycle command uses. Tests set it; production leaves it unset.</summary>
    internal static Func<string, string, IBackupVault>? Override { get; set; }

    /// <summary>
    /// The vault of <paramref name="productRoot"/>: the root-only one through <c>sudo</c> under the production product
    /// root, and an owner-only folder under a test product root.
    /// </summary>
    public static IBackupVault For(string productRoot, string productionProductRoot)
        => Override?.Invoke(productRoot, productionProductRoot) ??
           (string.Equals(productRoot, productionProductRoot, StringComparison.Ordinal)
               ? new PrivilegedBackupVault(productRoot, new SudoBackupHelperLauncher())
               : new OwnerBackupVault(new BackupVaultFiles(Path.Combine(productRoot, FolderName))));
}

/// <summary>
/// The vault's files, read and written by the vault's owner: root through the helper, or the runtime user under a test
/// product root. Every name is one this installer gives an archive, every file is opened without following a link and
/// must be a single-link regular file owned by the caller with mode 0600, and every step a later one depends on is
/// durable before it returns.
/// </summary>
internal sealed partial class BackupVaultFiles(string root)
{
    public const string PartialSuffix = ".partial";
    public const string RecordSuffix = ".json";

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
        if (!File.Exists(partial))
        {
            if (File.Exists(archive) && ReadRecord(record.Name) == record) return;
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

    /// <summary>
    /// Requires the folder to be the caller's own, owner-only and not a link, creating it when asked. Its entry in the
    /// product root is durable before anything is written in it.
    /// </summary>
    private void EnsureFolder(bool create)
    {
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

/// <summary>The vault of a test product root, read and written directly by the runtime user.</summary>
internal sealed class OwnerBackupVault(BackupVaultFiles files) : IBackupVault
{
    public string Root => files.Root;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned transfer owns the stream and disposes it.")]
    public Task<IBackupTransfer> WriteAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult<IBackupTransfer>(new FileTransfer(files.CreatePartial(name), write: true, files.Root));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned transfer owns the stream and disposes it.")]
    public Task<IBackupTransfer> ReadAsync(string name, bool partial, CancellationToken cancellationToken)
        => Task.FromResult<IBackupTransfer>(new FileTransfer(files.OpenRead(name, partial), write: false, files.Root));

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

    private sealed class FileTransfer(FileStream stream, bool write, string root) : IBackupTransfer
    {
        public Stream Stream => stream;

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (write)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                BackupVaultFiles.Sync(stream);
                NativeLinux.FlushDirectory(root);
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

/// <summary>
/// The root-only vault of the production product root. Each operation runs the helper as
/// <c>sudo -- /proc/&lt;pid&gt;/exe internal-backup-vault --product-root &lt;root&gt; &lt;--mode&gt; &lt;value&gt;</c>; an archive
/// streams through the helper's standard input or output, and the helper re-reads the host policy itself.
/// </summary>
internal sealed class PrivilegedBackupVault(string productRoot, IBackupHelperLauncher launcher) : IBackupVault
{
    public string Root { get; } = Path.Combine(productRoot, BackupVault.FolderName);

    public async Task<IBackupTransfer> WriteAsync(string name, CancellationToken cancellationToken)
        => new HelperTransfer(await StartAsync("--write", name, cancellationToken).ConfigureAwait(false), write: true, Root);

    public async Task<IBackupTransfer> ReadAsync(string name, bool partial, CancellationToken cancellationToken)
        => new HelperTransfer(
            await StartAsync(partial ? "--read-partial" : "--read", name, cancellationToken).ConfigureAwait(false), write: false, Root);

    public async Task CommitAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var helper = await StartAsync("--commit", record.Name, cancellationToken).ConfigureAwait(false);
        await helper.Input.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(record, BackupVaultJsonContext.Default.BackupRecord), cancellationToken).ConfigureAwait(false);
        await helper.Input.DisposeAsync().ConfigureAwait(false);
        await EnsureSucceededAsync(helper, Root, cancellationToken).ConfigureAwait(false);
    }

    public async Task DiscardAsync(string name, CancellationToken cancellationToken)
    {
        await using var helper = await StartAsync("--discard", name, cancellationToken).ConfigureAwait(false);
        await helper.Input.DisposeAsync().ConfigureAwait(false);
        await EnsureSucceededAsync(helper, Root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupRecord?> ReadRecordAsync(string name, CancellationToken cancellationToken)
    {
        var output = await RunForOutputAsync("--record", name, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(output, BackupVaultJsonContext.Default.BackupRecord) is { } record && record.IsValid(name)
            ? record
            : null;
    }

    public async Task<IReadOnlyList<BackupPartial>> ListPartialsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var output = await RunForOutputAsync("--partials", instanceId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var partials = JsonSerializer.Deserialize(output, BackupVaultJsonContext.Default.BackupPartialArray) ?? [];
        return partials.All(partial => BackupVaultFiles.IsName(partial.Name) && partial.Length >= 0 &&
                                       partial.Name.StartsWith($"hvo-sky-{instanceId:N}-", StringComparison.Ordinal))
            ? partials
            : throw new InstallerException("The backup helper listed partials that are not this instance's.");
    }

    private Task<IBackupHelperProcess> StartAsync(string mode, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(launcher.Start(BackupVaultHelper.HelperArguments(productRoot, mode, value)));
    }

    private async Task<byte[]> RunForOutputAsync(string mode, string value, CancellationToken cancellationToken)
    {
        await using var helper = await StartAsync(mode, value, cancellationToken).ConfigureAwait(false);
        await helper.Input.DisposeAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await helper.Output.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > BackupVaultHelper.MaximumListingBytes)
                throw new InstallerException("The backup helper answered with more than a listing holds.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await EnsureSucceededAsync(helper, Root, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task EnsureSucceededAsync(IBackupHelperProcess helper, string root, CancellationToken cancellationToken)
    {
        var (exitCode, error) = await helper.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (exitCode == 0) return;
        var message = $"The backup folder {root} could not be used: {Redaction.SafeDiagnostic(error.Trim())}";
        throw exitCode == 2 ? InstallerException.Refused(message) : new InstallerException(message);
    }

    private sealed class HelperTransfer(IBackupHelperProcess helper, bool write, string root) : IBackupTransfer
    {
        private bool completed;

        public Stream Stream => write ? helper.Input : helper.Output;

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (write)
            {
                await helper.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                await helper.Input.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                // What the reader left unread is drained, so the helper is never left blocked on a full pipe.
                await helper.Output.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            }
            await EnsureSucceededAsync(helper, root, cancellationToken).ConfigureAwait(false);
            completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!completed) helper.Kill();
            await helper.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Starts the vault helper; tests run it in process.</summary>
internal interface IBackupHelperLauncher
{
    IBackupHelperProcess Start(IReadOnlyList<string> sudoArguments);
}

/// <summary>A running vault helper: its standard input and output, and how it ended.</summary>
internal interface IBackupHelperProcess : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }

    /// <summary>Waits for the helper to end, returning its exit code and what it wrote to standard error.</summary>
    Task<(int ExitCode, string Error)> WaitAsync(CancellationToken cancellationToken);

    /// <summary>Ends the helper; what it was writing stays a partial for <see cref="IBackupVault.DiscardAsync"/>.</summary>
    void Kill();
}

/// <summary>Runs the vault helper through <c>sudo</c>; the terminal stays the caller's, so <c>sudo</c> can ask for a password.</summary>
internal sealed class SudoBackupHelperLauncher : IBackupHelperLauncher
{
    public IBackupHelperProcess Start(IReadOnlyList<string> sudoArguments)
    {
        ArgumentNullException.ThrowIfNull(sudoArguments);
        var start = new ProcessStartInfo("sudo")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in sudoArguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InstallerException("The backup helper could not be started.");
        return new SudoProcess(process);
    }

    private sealed class SudoProcess : IBackupHelperProcess
    {
        private const int MaximumErrorCharacters = 16 * 1024;
        private readonly Process process;
        private readonly Task<string> error;

        public SudoProcess(Process process)
        {
            this.process = process;
            error = ReadBoundedAsync(process.StandardError);
        }

        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;

        public async Task<(int ExitCode, string Error)> WaitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await error.ConfigureAwait(false));
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It has already ended.
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                if (text.Length < MaximumErrorCharacters) text.Append(buffer, 0, Math.Min(read, MaximumErrorCharacters - text.Length));
            }
            return text.ToString();
        }
    }
}

/// <summary>
/// The <c>sudo</c>-executed vault helper, <c>internal-backup-vault --product-root &lt;root&gt; &lt;mode&gt; &lt;value&gt;</c>. It
/// accepts only the production product root the host policy resolves, which it re-reads itself, and only archive names
/// this installer gives; it streams one archive through its standard input or output and never takes a path.
/// </summary>
internal static class BackupVaultHelper
{
    public const string CommandName = "internal-backup-vault";

    /// <summary>The most a record or listing answer holds.</summary>
    public const int MaximumListingBytes = 4 * 1024 * 1024;

    private static readonly string[] Modes = ["--write", "--read", "--read-partial", "--commit", "--discard", "--record", "--partials"];

    public static IReadOnlyList<string> HelperArguments(string productRoot, string mode, string value)
        => ["--", $"/proc/{Environment.ProcessId}/exe", CommandName, "--product-root", productRoot, mode, value];

    /// <summary>Parses exactly <c>internal-backup-vault --product-root &lt;root&gt; &lt;mode&gt; &lt;value&gt;</c>.</summary>
    internal static bool TryParse(string[] args, out string productRoot, out string mode, out string value)
    {
        ArgumentNullException.ThrowIfNull(args);
        productRoot = mode = value = string.Empty;
        if (args.Length != 5 || args[0] != CommandName || args[1] != "--product-root" || !Modes.Contains(args[3], StringComparer.Ordinal))
        {
            return false;
        }
        var valid = args[3] == "--partials"
            ? Guid.TryParseExact(args[4], "D", out _)
            : BackupVaultFiles.IsName(args[4]);
        if (!valid || !Path.IsPathFullyQualified(args[2])) return false;
        (productRoot, mode, value) = (args[2], args[3], args[4]);
        return true;
    }

    /// <summary>
    /// Runs as root through <c>sudo</c> for the runtime user that invoked it. Refusals exit 2; a failed step exits 1.
    /// </summary>
    public static int RunInternal(string[] args)
    {
        if (NativeLinux.getuid() != 0 || !TryParse(args, out var productRoot, out var mode, out var value) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var sudoUid) || sudoUid == 0)
        {
            Console.Error.WriteLine("The backup helper runs only through sudo, for the runtime user, with one archive name.");
            return 2;
        }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            using var policyLock = HostProductRootPolicy.AcquireLock(HostPolicyLocation.Production);
            var production = HostProductRootPolicy.Resolve(HostPolicyLocation.Production);
            if (!string.Equals(productRoot, production.Path, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("The backup helper serves only the production product root.");
                return 2;
            }
            return Run(new BackupVaultFiles(Path.Combine(production.Path, BackupVault.FolderName)), mode, value, input, output);
        }
        catch (InstallerException exception) when (exception.IsRefusal)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(Redaction.SafeDiagnostic(exception.Message));
            return 1;
        }
    }

    /// <summary>One vault operation on <paramref name="files"/>, streaming through <paramref name="input"/> and <paramref name="output"/>.</summary>
    internal static int Run(BackupVaultFiles files, string mode, string value, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        switch (mode)
        {
            case "--write":
                {
                    using var partial = files.CreatePartial(value);
                    input.CopyTo(partial, 1024 * 1024);
                    BackupVaultFiles.Sync(partial);
                    NativeLinux.FlushDirectory(files.Root);
                    return 0;
                }
            case "--read" or "--read-partial":
                {
                    using var archive = files.OpenRead(value, partial: mode == "--read-partial");
                    archive.CopyTo(output, 1024 * 1024);
                    output.Flush();
                    return 0;
                }
            case "--commit":
                {
                    using var buffer = new MemoryStream();
                    CopyBounded(input, buffer);
                    var record = JsonSerializer.Deserialize(buffer.ToArray(), BackupVaultJsonContext.Default.BackupRecord);
                    if (record is null || !record.IsValid(value)) throw InstallerException.Refused("The backup record does not name this archive.");
                    files.Commit(record);
                    return 0;
                }
            case "--discard":
                files.Discard(value);
                return 0;
            case "--record":
                output.Write(JsonSerializer.SerializeToUtf8Bytes(files.ReadRecord(value), BackupVaultJsonContext.Default.BackupRecord));
                output.Flush();
                return 0;
            case "--partials":
                output.Write(JsonSerializer.SerializeToUtf8Bytes(
                    [.. files.Partials(Guid.ParseExact(value, "D"))], BackupVaultJsonContext.Default.BackupPartialArray));
                output.Flush();
                return 0;
            default:
                throw InstallerException.Refused($"Unknown backup helper mode '{mode}'.");
        }
    }

    private static void CopyBounded(Stream input, MemoryStream buffer)
    {
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = input.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaximumListingBytes) throw InstallerException.Refused("The backup record is too large.");
            buffer.Write(chunk, 0, read);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(BackupRecord))]
[JsonSerializable(typeof(BackupPartial[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class BackupVaultJsonContext : JsonSerializerContext;
