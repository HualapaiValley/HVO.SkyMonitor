using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Pipes;
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

    [TestMethod]
    public async Task PrivilegedVault_StreamsEveryOperationThroughTheHelper()
    {
        using var root = new Scratch();
        var files = new BackupVaultFiles(Path.Combine(root.Path, BackupVault.FolderName));
        var launcher = new InProcessHelper(files);
        var vault = new PrivilegedBackupVault(root.Path, launcher);
        await using (var transfer = await vault.WriteAsync(Name, CancellationToken.None))
        {
            await transfer.Stream.WriteAsync("archive"u8.ToArray());
            await transfer.CompleteAsync(CancellationToken.None);
        }
        Assert.AreEqual(Name, (await vault.ListPartialsAsync(Instance, CancellationToken.None)).Single().Name);
        await vault.CommitAsync(Record(7), CancellationToken.None);
        string content;
        await using (var transfer = await vault.ReadAsync(Name, partial: false, CancellationToken.None))
        {
            using var reader = new StreamReader(transfer.Stream);
            content = await reader.ReadToEndAsync();
            await transfer.CompleteAsync(CancellationToken.None);
        }

        Assert.AreEqual("archive", content);
        Assert.AreEqual(Record(7), await vault.ReadRecordAsync(Name, CancellationToken.None));
        string[] read = ["--", $"/proc/{Environment.ProcessId}/exe", BackupVaultHelper.CommandName, "--product-root", root.Path, "--read", Name];
        Assert.IsTrue(
            launcher.Invocations.Any(arguments => arguments.SequenceEqual(read, StringComparer.Ordinal)),
            string.Join(Environment.NewLine, launcher.Invocations.Select(static arguments => string.Join(' ', arguments))));
    }

    [TestMethod]
    public async Task PrivilegedVault_HelperRefusal_IsARefusal()
    {
        using var root = new Scratch();
        var vault = new PrivilegedBackupVault(root.Path, new InProcessHelper(new BackupVaultFiles(Path.Combine(root.Path, BackupVault.FolderName))));

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(async () =>
        {
            await using var transfer = await vault.ReadAsync(Name, partial: false, CancellationToken.None);
            await transfer.CompleteAsync(CancellationToken.None);
        });

        Assert.IsTrue(refused.IsRefusal, refused.Message);
    }

    [TestMethod]
    public void HelperArguments_AreParsedExactly()
    {
        Assert.IsTrue(BackupVaultHelper.TryParse(
            [BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--write", Name], out var productRoot, out var mode, out var value));
        Assert.AreEqual(("/var/lib/hvo-sky", "--write", Name), (productRoot, mode, value));
        Assert.IsTrue(BackupVaultHelper.TryParse(
            [BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--partials", Instance.ToString("D")], out _, out _, out _));
        foreach (var args in new[]
                 {
                     new[] { BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--write", "../etc/shadow" },
                     new[] { BackupVaultHelper.CommandName, "--product-root", "relative", "--write", Name },
                     new[] { BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--delete", Name },
                     new[] { BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--partials", Name },
                     new[] { BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--write", Name, "extra" }
                 })
        {
            Assert.IsFalse(BackupVaultHelper.TryParse(args, out _, out _, out _), string.Join(' ', args));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void RunInternal_WithoutRoot_IsRefused()
    {
        if (NativeLinux.getuid() == 0) Assert.Inconclusive("The test runs as the runtime user.");
        Assert.AreEqual(2, BackupVaultHelper.RunInternal([BackupVaultHelper.CommandName, "--product-root", "/var/lib/hvo-sky", "--record", Name]));
    }

    private static BackupRecord Record(long length)
        => new(BackupRecord.CurrentSchemaVersion, Name, Instance, Operation, Started, length, new string('a', 64), 7, 1);

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

    /// <summary>Runs the vault helper in this process over pipes, as <c>sudo</c> would run it in another.</summary>
    private sealed class InProcessHelper(BackupVaultFiles files) : IBackupHelperLauncher
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public IBackupHelperProcess Start(IReadOnlyList<string> sudoArguments)
        {
            Invocations.Add(sudoArguments);
            Assert.IsTrue(BackupVaultHelper.TryParse([.. sudoArguments.Skip(2)], out _, out var mode, out var value));
            return new Helper(files, mode, value);
        }

        private sealed class Helper : IBackupHelperProcess
        {
            private readonly AnonymousPipeServerStream input = new(PipeDirection.Out);
            private readonly AnonymousPipeServerStream output = new(PipeDirection.In);
            private readonly Task<(int, string)> run;

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The helper's task disposes its ends of the pipes when it returns.")]
            public Helper(BackupVaultFiles files, string mode, string value)
            {
                var helperInput = new AnonymousPipeClientStream(PipeDirection.In, input.ClientSafePipeHandle);
                var helperOutput = new AnonymousPipeClientStream(PipeDirection.Out, output.ClientSafePipeHandle);
                run = Task.Run(() =>
                {
                    try
                    {
                        return (BackupVaultHelper.Run(files, mode, value, helperInput, helperOutput), string.Empty);
                    }
                    catch (InstallerException exception) when (exception.IsRefusal)
                    {
                        return (2, exception.Message);
                    }
                    finally
                    {
                        helperInput.Dispose();
                        helperOutput.Dispose();
                    }
                });
            }

            public Stream Input => input;
            public Stream Output => output;

            public async Task<(int ExitCode, string Error)> WaitAsync(CancellationToken cancellationToken) => await run;

            public void Kill()
            {
            }

            public async ValueTask DisposeAsync()
            {
                await input.DisposeAsync();
                await output.DisposeAsync();
                await run;
            }
        }
    }

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-backup-vault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
