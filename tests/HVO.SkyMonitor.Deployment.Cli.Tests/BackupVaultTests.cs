using System.Formats.Tar;
using System.IO.Compression;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The backup archive and the folder that keeps it (#1353): an archive is whole only when it ends with a manifest naming
/// exactly what precedes it, byte for byte; a partial or unrecorded archive is never read as a backup; and the root-only
/// vault's helper accepts exactly one archive name and streams it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class BackupVaultTests
{
    private static readonly Guid Instance = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");
    private static readonly Guid Operation = Guid.Parse("11223344-5566-4778-8899-aabbccddeeff");
    private static readonly DateTimeOffset Started = new(2026, 10, 18, 7, 30, 0, TimeSpan.Zero);
    private static readonly string Name = BackupVaultFiles.NameFor(Instance, Started, Operation);

    [TestMethod]
    public void NameFor_IsTheOnlyShapeTheVaultAccepts()
    {
        Assert.AreEqual("hvo-sky-0f1e2d3c4b5a49688776655443322110-20261018T073000Z-11223344.tar.gz", Name);
        Assert.IsTrue(BackupVaultFiles.IsName(Name));
        foreach (var name in new[] { "../" + Name, Name + ".partial", "/tmp/" + Name, Name.Replace("tar.gz", "tgz", StringComparison.Ordinal), string.Empty })
        {
            Assert.IsFalse(BackupVaultFiles.IsName(name), name);
        }
    }

    [TestMethod]
    public async Task ArchiveRoundTrip_ProvesEveryEntryAndTheManifestLast()
    {
        using var root = new Scratch();
        var items = Items(root.Path);
        using var archive = new MemoryStream();
        var written = await WriteAsync(archive, items);
        archive.Position = 0;
        var names = new List<string>();

        var read = await BackupArchive.ReadAsync(archive, (name, _, _, _, _) =>
        {
            names.Add(name);
            return Task.CompletedTask;
        }, null, CancellationToken.None);

        Assert.AreEqual(written.TotalBytes, read.TotalBytes);
        CollectionAssert.AreEqual(written.Entries.Select(static entry => entry.Path).ToArray(), names.ToArray());
        CollectionAssert.AreEqual(written.Entries.ToArray(), read.Entries.ToArray());
    }

    [TestMethod]
    [DataRow("instance/../escape", "canonical")]
    [DataRow("instance//double", "canonical")]
    [DataRow("outside/file", "outside the instance")]
    [DataRow("instance/./dot", "canonical")]
    public async Task ReadAsync_EntryWithANameThatIsNotCanonical_IsRefused(string name, string reason)
    {
        using var archive = new MemoryStream();
        await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream("x"u8.ToArray()) });
        }
        archive.Position = 0;

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => BackupArchive.ReadAsync(archive, null, null, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal);
        StringAssert.Contains(refused.Message, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// F6: a commit under a name that is already committed is refused before any record is touched, so the backup that has
    /// the name keeps its record; committing that same archive again changes nothing.
    /// </summary>
    [TestMethod]
    public async Task OwnerVault_CommitUnderANameAlreadyCommitted_IsRefusedAndLeavesTheCommittedRecord()
    {
        using var root = new Scratch();
        var vault = new OwnerBackupVault(new BackupVaultFiles(Path.Combine(root.Path, BackupVault.FolderName)));
        await WritePartialAsync(vault, "archive"u8.ToArray());
        await vault.CommitAsync(Record(7), CancellationToken.None);
        await WritePartialAsync(vault, "another"u8.ToArray());
        var other = Record(7) with { ArchiveSha256 = new string('b', 64) };

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => vault.CommitAsync(other, CancellationToken.None));
        await vault.CommitAsync(Record(7), CancellationToken.None);

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        Assert.AreEqual(Record(7), await vault.ReadRecordAsync(Name, CancellationToken.None), "the committed backup's record was replaced");
        await using var transfer = await vault.ReadAsync(Name, partial: false, CancellationToken.None);
        using var reader = new StreamReader(transfer.Stream);
        Assert.AreEqual("archive", await reader.ReadToEndAsync());
    }

    /// <summary>
    /// F10: after the tar end marker the decompressed stream must hold nothing but zero padding to its end, so a second
    /// gzip member with data refuses the archive. The archive's bytes as a whole, any after the gzip stream included, are
    /// bound by its record's SHA-256 and length instead (<c>RestoreAsync_PartialOrDamagedArchive_...</c>).
    /// </summary>
    [TestMethod]
    public async Task ReadAsync_DataAfterTheTarEnd_IsRefused()
    {
        using var root = new Scratch();
        using var archive = new MemoryStream();
        await WriteAsync(archive, Items(root.Path));
        await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync("smuggled after the end"u8.ToArray());
        }
        archive.Position = 0;

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => BackupArchive.ReadAsync(archive, null, null, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
    }

    /// <summary>F11: exactly one trailing '/' marks a folder; any other empty segment is not a canonical name.</summary>
    [TestMethod]
    [DataRow("instance/state//")]
    [DataRow("instance//")]
    public void NameProblem_FolderNameEndingInMoreThanOneSlash_IsRefused(string name)
        => Assert.IsNotNull(BackupArchive.NameProblem(name, TarEntryType.Directory), name);

    [TestMethod]
    public async Task OwnerVault_CommitsOnlyARecordedWholeArchiveAndNeverDiscardsOne()
    {
        using var root = new Scratch();
        var vault = new OwnerBackupVault(new BackupVaultFiles(Path.Combine(root.Path, BackupVault.FolderName)));
        await using (var transfer = await vault.WriteAsync(Name, CancellationToken.None))
        {
            await transfer.Stream.WriteAsync("archive"u8.ToArray());
            await transfer.CompleteAsync(CancellationToken.None);
        }

        Assert.IsNull(await vault.ReadRecordAsync(Name, CancellationToken.None), "a partial has no record");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => vault.ReadAsync(Name, partial: false, CancellationToken.None));
        Assert.AreEqual(1, (await vault.ListPartialsAsync(Instance, CancellationToken.None)).Count);
        var record = Record(7);
        await vault.CommitAsync(record, CancellationToken.None);
        await vault.CommitAsync(record, CancellationToken.None);
        await vault.DiscardAsync(Name, CancellationToken.None);

        Assert.AreEqual(record, await vault.ReadRecordAsync(Name, CancellationToken.None), "committing again changes nothing, and discard keeps it");
        Assert.IsEmpty(await vault.ListPartialsAsync(Instance, CancellationToken.None));
    }

    /// <summary>
    /// F18: a backup folder that does not exist yet, under folders only their owners can write, can hold backups; checking
    /// it creates nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Problem_FolderNotMadeYetUnderSafeFolders_IsNoneAndCreatesNothing()
    {
        using var root = new Scratch();
        var folder = Path.Combine(root.Path, BackupVault.FolderName);
        var files = new BackupVaultFiles(folder);

        Assert.IsNull(files.Problem());
        files.Admit();
        Assert.IsFalse(Directory.Exists(folder));
    }

    /// <summary>
    /// F18: a folder above the backup folder that a group or other users can write is a problem, named with its remedy,
    /// unless its sticky bit keeps them from renaming what is not theirs.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(UnixFileMode.GroupWrite, true)]
    [DataRow(UnixFileMode.OtherWrite, true)]
    [DataRow(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit, false)]
    public void Problem_FolderAboveOthersCanWrite_IsNamedUnlessSticky(UnixFileMode added, bool problem)
    {
        using var root = new Scratch();
        var parent = Path.Combine(root.Path, "product");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | added);
        var files = new BackupVaultFiles(Path.Combine(parent, BackupVault.FolderName));

        if (!problem)
        {
            Assert.IsNull(files.Problem());
            return;
        }
        StringAssert.Contains(files.Problem()!, $"{parent}, which holds it", StringComparison.Ordinal);
        StringAssert.Contains(files.Problem()!, $"chmod go-w {parent}", StringComparison.Ordinal);
        Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(files.Admit).IsRefusal);
    }

    /// <summary>F18: the backup folder itself must be the account's own, mode 0700, and a folder, not a link.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("group-readable", "chmod 700")]
    [DataRow("link", "is not a folder")]
    public void Problem_BackupFolderNotOwnerOnly_IsNamed(string shape, string named)
    {
        using var root = new Scratch();
        var folder = Path.Combine(root.Path, BackupVault.FolderName);
        if (shape == "link")
        {
            var target = Path.Combine(root.Path, "elsewhere");
            Directory.CreateDirectory(target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateSymbolicLink(folder, target);
        }
        else
        {
            Directory.CreateDirectory(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        }

        StringAssert.Contains(new BackupVaultFiles(folder).Problem()!, named, StringComparison.Ordinal);
    }

    /// <summary>F18: the vault refuses a folder that cannot hold backups before it writes anything, and creates nothing.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OwnerVault_InAFolderOthersCanChange_IsRefusedBeforeAnythingIsWritten()
    {
        using var root = new Scratch();
        File.SetUnixFileMode(root.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        var folder = Path.Combine(root.Path, BackupVault.FolderName);
        var vault = new OwnerBackupVault(new BackupVaultFiles(folder));

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => vault.WriteAsync(Name, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        Assert.IsFalse(Directory.Exists(folder));
    }

    private static async Task WritePartialAsync(OwnerBackupVault vault, byte[] content)
    {
        await using var transfer = await vault.WriteAsync(Name, CancellationToken.None);
        await transfer.Stream.WriteAsync(content);
        await transfer.CompleteAsync(CancellationToken.None);
    }

    private static BackupRecord Record(long length)
        => new(BackupRecord.CurrentSchemaVersion, Name, Instance, Operation, Started, length, new string('a', 64), 7, 1, 1000, 1000);

    private static List<BackupItem> Items(string root)
    {
        var file = Path.Combine(root, "frame.fits");
        File.WriteAllBytes(file, [.. Enumerable.Range(0, 200_000).Select(static index => (byte)index)]);
        return
        [
            new("instance/state/", null, Directory: true, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, 0),
            new("instance/state/frame.fits", file, Directory: false, UnixFileMode.UserRead | UnixFileMode.UserWrite, new FileInfo(file).Length)
        ];
    }

    private static Task<BackupArchiveManifest> WriteAsync(Stream destination, IReadOnlyList<BackupItem> items)
        => BackupArchive.WriteAsync(destination, items, static (entries, total) => new BackupArchiveManifest(
            BackupArchive.SchemaVersion, BackupArchive.Product, BackupArchive.Component, Instance, "Test Camera", Operation, Started,
            "0.0.0-dev+unknown", "/var/lib/hvo-sky", 1000, 1000, $"sha256:{new string('c', 64)}", null, "hyg", "1",
            new LifecycleStateSnapshot(Guid.NewGuid(), Operation, new string('b', 64), Started), entries, [], total),
            null, CancellationToken.None);

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-backup-vault-{Guid.NewGuid():N}");
            // Owner-only, as the installer makes the product root, whatever the umask.
            Directory.CreateDirectory(Path);
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
