using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Reading a release from its folder: release.json is the file SHA256SUMS names, the two list the same files with the
/// same SHA-256, the image is selected for the platform that will run it, and an offline archive is the size and SHA-256
/// release.json gives it. Every other case fails closed with the reason, before Docker is asked anything.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseAcquirerTests
{
    private static readonly string HostPlatform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_Archive_SelectsThisHostsArchiveAndVerifiesIt()
    {
        using var release = ReleaseFixture.Create();

        var image = (await ReleaseAcquirer.AcquireImageAsync(Request(release.Folder), CancellationToken.None))!;

        Assert.AreEqual(ReleaseImageSource.Archive, image.Source);
        Assert.AreEqual(release.ArchivePath(HostPlatform), image.ArchivePath);
        Assert.AreEqual((string)release.Archive(HostPlatform)["sha256"]!, image.ArchiveSha256);
        // The archive is loaded by its classic-store image ID; the selection lists every identity Docker may give it.
        Assert.AreEqual(release.ConfigDigest(HostPlatform), image.ImageReference);
        CollectionAssert.AreEqual(
            new[] { release.Digest, release.ManifestDigest(HostPlatform), release.ConfigDigest(HostPlatform) },
            image.Image.ImageIds!.ToArray());
        Assert.AreEqual(new ReleaseImageSelection(
            $"v{release.Version}", release.Version, ReleaseFixture.Commit, ReleaseFixture.Created, HostPlatform, release.DigestReference,
            image.Image.ImageIds), image.Image);
        Assert.AreEqual(release.Version, image.Release.Version);
        Assert.AreEqual(Sha256(Path.Combine(release.Folder, "release.json")), image.VerifiedRelease.ManifestSha256);
        Assert.AreEqual(Sha256(Path.Combine(release.Folder, "SHA256SUMS")), image.VerifiedRelease.ChecksumsSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_Registry_PullsTheDigestReferenceAndNeedsNoArchive()
    {
        using var release = ReleaseFixture.Create();
        File.Delete(release.ArchivePath(HostPlatform));

        var image = (await ReleaseAcquirer.AcquireImageAsync(
            Request(release.Folder) with { ImageSource = ReleaseImageSource.Registry }, CancellationToken.None))!;

        Assert.AreEqual(ReleaseImageSource.Registry, image.Source);
        Assert.AreEqual(release.DigestReference, image.ImageReference);
        Assert.IsNull(image.ArchivePath);
        Assert.IsNull(image.ArchiveSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_RegistryReleaseWithoutAnArchiveForThePlatform_RequiresOnlyTheDigest()
    {
        using var release = ReleaseFixture.Create(archivePlatforms: []);

        var image = (await ReleaseAcquirer.AcquireImageAsync(
            Request(release.Folder) with { ImageSource = ReleaseImageSource.Registry }, CancellationToken.None))!;

        // Without an archive the release names no per-platform identity, so Docker's repository digest alone binds it.
        Assert.IsNull(image.Image.ImageIds);
        Assert.AreEqual(release.DigestReference, image.ImageReference);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_RecordedDaemon_SelectsItsArchitectureNotTheProcesses()
    {
        var daemon = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var release = ReleaseFixture.Create();

        var image = (await ReleaseAcquirer.AcquireImageAsync(release.Folder, null, daemon, CancellationToken.None))!;

        Assert.AreEqual($"linux/{daemon}", image.Image.Platform);
        Assert.AreEqual(release.ArchivePath($"linux/{daemon}"), image.ArchivePath);
    }

    [TestMethod]
    public async Task AcquireImageAsync_NoRelease_SelectsNothing()
    {
        Assert.IsNull(await ReleaseAcquirer.AcquireImageAsync(Request(null), CancellationToken.None));
        Assert.IsNull(await ReleaseAcquirer.AcquireImageAsync(null, null, "amd64", CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("folder-missing", "The release folder {folder} does not exist.")]
    [DataRow("release-json-missing", "The release folder {folder} has no release.json.")]
    [DataRow("checksums-missing", "The release folder {folder} has no SHA256SUMS.")]
    [DataRow("checksums-without-release-json", "SHA256SUMS does not list release.json.")]
    [DataRow("release-json-changed", "release.json does not match its SHA-256 in SHA256SUMS: it is not the release.json the release published.")]
    [DataRow("checksums-extra-file", "SHA256SUMS lists extra.bin, which release.json does not.")]
    [DataRow("checksums-missing-asset", "SHA256SUMS does not list hvo-sky-install-linux-arm64, which release.json lists.")]
    [DataRow("checksums-disagree", "release.json and SHA256SUMS give hvo-sky-install-linux-arm64 different SHA-256s.")]
    [DataRow("checksums-malformed", "The installer will not use SHA256SUMS: line 1 is not a SHA-256 and a release file name.")]
    [DataRow("schema-unknown", "The installer will not use release.json: it is schema 2, and this installer reads schema 1.")]
    [DataRow("digest-malformed", "The installer will not use release.json: the skymonitor-cameraagent image's digest is not sha256:<64 lowercase hex digits>.")]
    [DataRow("platform-unpublished", "Release v1.2.0-dev.3 publishes the CameraAgent image for {other} and does not support this host's {host} architecture.")]
    [DataRow("archive-missing", "The offline archive {archive} is not in {folder}. Copy it there from the release, or pass --image-source registry to pull {reference} instead.")]
    [DataRow("archive-unlisted", "Release v1.2.0-dev.3 has no offline archive for {host}. Pass --image-source registry to pull {reference} instead.")]
    [DataRow("archive-truncated", "{archive} is {length} bytes, and release.json gives it {size}: it is not the file the release published.")]
    [DataRow("archive-changed", "{archive} does not match its SHA-256 in release.json and SHA256SUMS: it is not the file the release published.")]
    [DataRow("release-json-oversized", "The installer will not use release.json: it is larger than 1048576 bytes.")]
    [DataRow("release-json-symlink", "Release file '{folder}/release.json' is not a regular file.")]
    [DataRow("release-json-directory", "Release file '{folder}/release.json' is not a regular file.")]
    [DataRow("release-json-fifo", "Release file '{folder}/release.json' is not a regular file.")]
    [DataRow("archive-symlink", "Release file '{folder}/{archive}' is not a regular file.")]
    [DataRow("release-json-hard-link", "Input file '{folder}/release.json' must not have hard links.")]
    public async Task AcquireImageAsync_InvalidRelease_FailsClosedWithTheReason(string fault, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var other = HostPlatform == "linux/amd64" ? "linux/arm64" : "linux/amd64";
        using var release = ReleaseFixture.Create(
            platforms: fault == "platform-unpublished" ? [other] : null,
            archivePlatforms: fault == "archive-unlisted" ? [] : null);
        var archive = fault is "platform-unpublished" or "archive-unlisted" ? null : release.ArchivePath(HostPlatform);
        var folder = release.Folder;
        var outside = Path.Combine(Path.GetDirectoryName(folder)!, "outside");
        var length = 0L;
        switch (fault)
        {
            case "folder-missing": folder = Path.Combine(folder, "absent"); break;
            case "release-json-missing": File.Delete(Path.Combine(folder, "release.json")); break;
            case "checksums-missing": File.Delete(Path.Combine(folder, "SHA256SUMS")); break;
            case "checksums-without-release-json":
                release.WriteChecksums(string.Concat(release.ReadChecksums().Split('\n')
                    .Where(static line => line.Length > 0 && !line.EndsWith("  release.json", StringComparison.Ordinal))
                    .Select(static line => line + "\n")));
                break;
            case "release-json-changed": await File.AppendAllTextAsync(Path.Combine(folder, "release.json"), " "); break;
            case "checksums-extra-file": release.WriteChecksums(release.ReadChecksums() + $"{new string('e', 64)}  extra.bin\n"); break;
            case "checksums-missing-asset":
                release.WriteChecksums(string.Concat(release.ReadChecksums().Split('\n')
                    .Where(static line => line.Length > 0 && !line.EndsWith("  hvo-sky-install-linux-arm64", StringComparison.Ordinal))
                    .Select(static line => line + "\n")));
                break;
            case "checksums-disagree":
                var sums = release.ReadChecksums();
                var installerLine = sums.Split('\n').Single(static line => line.EndsWith("  hvo-sky-install-linux-arm64", StringComparison.Ordinal));
                release.WriteChecksums(sums.Replace(installerLine, $"{new string('0', 64)}  hvo-sky-install-linux-arm64", StringComparison.Ordinal));
                break;
            case "checksums-malformed": release.WriteChecksums(release.ReadChecksums().Replace("  ", " *", StringComparison.Ordinal)); break;
            case "schema-unknown": release.Rewrite(static root => root["schemaVersion"] = 2); break;
            case "digest-malformed":
                release.Rewrite(static root => root["images"]![ReleaseFixture.ImageKey]!["digest"] = "sha256:short");
                break;
            case "archive-missing": File.Delete(archive!); break;
            case "archive-truncated":
                await File.WriteAllTextAsync(archive!, "short");
                length = 5;
                break;
            case "archive-changed":
                var bytes = await File.ReadAllBytesAsync(archive!);
                bytes[0] ^= 0xFF;
                await File.WriteAllBytesAsync(archive!, bytes);
                break;
            case "release-json-oversized":
                await File.WriteAllBytesAsync(Path.Combine(folder, "release.json"), new byte[ReleaseManifest.MaximumLength + 1]);
                break;
            case "release-json-symlink":
                File.Move(Path.Combine(folder, "release.json"), outside);
                File.CreateSymbolicLink(Path.Combine(folder, "release.json"), outside);
                break;
            case "release-json-directory":
                File.Delete(Path.Combine(folder, "release.json"));
                Directory.CreateDirectory(Path.Combine(folder, "release.json"));
                break;
            case "release-json-fifo":
                File.Delete(Path.Combine(folder, "release.json"));
                await CreateFifoOrInconclusiveAsync(Path.Combine(folder, "release.json"));
                break;
            case "archive-symlink":
                File.Move(archive!, outside);
                File.CreateSymbolicLink(archive!, outside);
                break;
            case "release-json-hard-link": NativeLinux.CreateHardLink(Path.Combine(folder, "release.json"), outside); break;
            case "platform-unpublished" or "archive-unlisted": break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
        var expected = message
            .Replace("{folder}", folder, StringComparison.Ordinal)
            .Replace("{archive}", archive is null ? string.Empty : Path.GetFileName(archive), StringComparison.Ordinal)
            .Replace("{reference}", release.DigestReference, StringComparison.Ordinal)
            .Replace("{host}", HostPlatform, StringComparison.Ordinal)
            .Replace("{other}", other, StringComparison.Ordinal)
            .Replace("{length}", length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{size}", archive is null ? string.Empty : ((int)release.Archive(HostPlatform)["size"]!).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => ReleaseAcquirer.AcquireImageAsync(Request(folder), CancellationToken.None));

        Assert.AreEqual(expected, exception.Message);
        Assert.IsTrue(exception.IsRefusal, "the release is checked before Docker is contacted, so it is a refusal (exit 3)");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ToEvidence_RecordsTheRunningImageOnlyWhenTheReleaseNamesIt()
    {
        using var release = ReleaseFixture.Create();
        var image = (await ReleaseAcquirer.AcquireImageAsync(Request(release.Folder), CancellationToken.None))!;
        var running = new ImageInstallationIdentity(
            "archive", release.Digest, release.Digest, ReleaseAcquirer.HostImageArchitecture(), image.ArchiveSha256);

        var evidence = image.ToEvidence(running);

        Assert.AreEqual(DeploymentSchemaVersions.ImageReleaseEvidence, evidence.SchemaVersion);
        Assert.AreEqual(release.Version, evidence.ReleaseVersion);
        Assert.AreEqual(release.Digest, evidence.ImageId);
        Assert.AreEqual("archive", evidence.Source);
        var stranger = running with { ImageId = $"sha256:{new string('d', 64)}" };
        var exception = Assert.ThrowsExactly<InstallerException>(() => image.ToEvidence(stranger));
        Assert.AreEqual("The running CameraAgent image is not the image the release names.", exception.Message);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CreateFifoOrInconclusive_WhenCommandMissing_IsInconclusive()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-guard-");
        try
        {
            var missingCommand = Path.Combine(root.FullName, "no-such-mkfifo");
            var fifoPath = Path.Combine(root.FullName, "release.json");

            await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(
                () => CreateFifoOrInconclusiveAsync(fifoPath, missingCommand));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CreateFifoOrInconclusive_WhenCommandNotExecutable_IsInconclusive()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-noexec-");
        try
        {
            var nonExecutable = Path.Combine(root.FullName, "mkfifo-noexec");
            await File.WriteAllTextAsync(nonExecutable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(nonExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var fifoPath = Path.Combine(root.FullName, "release.json");

            await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(
                () => CreateFifoOrInconclusiveAsync(fifoPath, nonExecutable));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CreateFifoOrInconclusive_WhenCommandParentIsRegularFile_FailsInsteadOfSkipping()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-enotdir-");
        try
        {
            var regularFile = Path.Combine(root.FullName, "not-a-directory");
            await File.WriteAllTextAsync(regularFile, "file");
            var invalidCommand = Path.Combine(regularFile, "mkfifo");
            var fifoPath = Path.Combine(root.FullName, "release.json");

            await Assert.ThrowsExactlyAsync<AssertFailedException>(
                () => CreateFifoOrInconclusiveAsync(fifoPath, invalidCommand));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("already-exists")]
    [DataRow("EPERM")]
    [DataRow("not supported")]
    [DataRow("Operation not permitted")]
    public async Task CreateFifoOrInconclusive_WhenTargetAlreadyExists_FailsInsteadOfSkipping(string directoryName)
    {
        ArgumentNullException.ThrowIfNull(directoryName);
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-existing-");
        try
        {
            // Verify mkfifo itself works on this host first so a restricted environment remains inconclusive cleanly.
            await CreateFifoOrInconclusiveAsync(Path.Combine(root.FullName, "probe.fifo"));

            var directory = Path.Combine(root.FullName, directoryName);
            Directory.CreateDirectory(directory);
            var existingFile = Path.Combine(directory, "release.json");
            await File.WriteAllTextAsync(existingFile, "occupied");

            await Assert.ThrowsExactlyAsync<AssertFailedException>(
                () => CreateFifoOrInconclusiveAsync(existingFile));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CreateFifoOrInconclusive_WhenCommandHangs_FailsWithinTimeout()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-hang-");
        var fifoPath = Path.Combine(root.FullName, "release.json");
        var pidFile = fifoPath + ".pid";
        var script = Path.Combine(root.FullName, "hang.sh");
        try
        {
            await File.WriteAllTextAsync(
                script,
                "#!/bin/sh\nset -e\nIFS= read -r stat_line < \"/proc/$$/stat\"\nprintf '%s\\n%s\\n' \"$$\" \"$stat_line\" > \"$1.pid.tmp\"\nmv -f \"$1.pid.tmp\" \"$1.pid\"\nexec sleep 30\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var stopwatch = Stopwatch.StartNew();
            var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                () => CreateFifoOrInconclusiveAsync(fifoPath, script, TimeSpan.FromMilliseconds(500)));
            stopwatch.Stop();

            StringAssert.Contains(failure.Message, "timed out", StringComparison.Ordinal);
            Assert.IsLessThan(5.0, stopwatch.Elapsed.TotalSeconds, $"Expected bounded timeout, took {stopwatch.Elapsed}.");
            var fixtureProcess = await ReadFixtureProcessIdentityAsync(pidFile);
            Assert.IsFalse(Directory.Exists($"/proc/{fixtureProcess.Pid}"), $"Timed-out command PID {fixtureProcess.Pid} was not reaped.");
        }
        finally
        {
            await CleanUpFifoFixtureAsync(root, pidFile, script, allowZombieAsTerminated: false);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CreateFifoOrInconclusive_WhenChildHoldsRedirectedPipesOpen_FailsWithinTimeout()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-pipe-");
        var fifoPath = Path.Combine(root.FullName, "release.json");
        var pidFile = fifoPath + ".pid";
        var script = Path.Combine(root.FullName, "hold-pipes.sh");
        try
        {
            await File.WriteAllTextAsync(
                script,
                "#!/bin/sh\nset -e\nsleep 30 &\nchild_pid=$!\nIFS= read -r stat_line < \"/proc/$child_pid/stat\"\nprintf '%s\\n%s\\n' \"$child_pid\" \"$stat_line\" > \"$1.pid.tmp\"\nmv -f \"$1.pid.tmp\" \"$1.pid\"\nexit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var stopwatch = Stopwatch.StartNew();
            var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                () => CreateFifoOrInconclusiveAsync(fifoPath, script, TimeSpan.FromMilliseconds(500)));
            stopwatch.Stop();

            StringAssert.Contains(failure.Message, "remained open", StringComparison.Ordinal);
            Assert.IsLessThan(5.0, stopwatch.Elapsed.TotalSeconds, $"Expected bounded pipe drain timeout, took {stopwatch.Elapsed}.");
            _ = await ReadFixtureProcessIdentityAsync(pidFile);
        }
        finally
        {
            // The command has exited, so the fixture owns cleanup of its reparented descendant.
            // Minimal containers whose PID 1 does not reap adopted orphans may leave the killed sleep in state 'Z'.
            await CleanUpFifoFixtureAsync(root, pidFile, script, allowZombieAsTerminated: true);
        }
    }

    [TestMethod]
    public void TryParseLinuxProcessStat_ParsesStateAndStartTimeIncludingComplexComm()
    {
        Assert.IsTrue(
            TryParseLinuxProcessStat(
                BuildSyntheticLinuxProcessStat(12345, "sleep", 'S', 987654321UL),
                out var sleeping));
        Assert.AreEqual(new LinuxProcessStat(12345, 'S', 987654321UL), sleeping);

        Assert.IsTrue(
            TryParseLinuxProcessStat(
                BuildSyntheticLinuxProcessStat(23456, "sleep", 'Z', 42000UL),
                out var zombie));
        Assert.AreEqual(new LinuxProcessStat(23456, 'Z', 42000UL), zombie);

        Assert.IsTrue(
            TryParseLinuxProcessStat(
                BuildSyntheticLinuxProcessStat(34567, "my (weird) proc)) ", 'Z', ulong.MaxValue),
                out var complexComm));
        Assert.AreEqual(new LinuxProcessStat(34567, 'Z', ulong.MaxValue), complexComm);

        // Minimum Linux 2.6.24+ stat format (44 total fields = 42 fields after "(comm) "), with negative signed fields.
        const string linux26Stat = "45678 (worker 1) I 1 45678 45678 0 -1 4194304 10 0 0 0 0 0 0 0 -100 -20 1 0 555 4096 128 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0\n";
        Assert.IsTrue(TryParseLinuxProcessStat(linux26Stat, out var minimalLinux26));
        Assert.AreEqual(new LinuxProcessStat(45678, 'I', 555UL), minimalLinux26);

        var validFullStat = BuildSyntheticLinuxProcessStat(12345, "sleep", 'S', 987654321UL);
        var validWithoutNewline = validFullStat[..^1];
        const string truncatedInsideStartTime = "12345 (sleep) S 1 12345 12345 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 98765432";
        const string truncatedAfterStartTimeWithNewline = "12345 (sleep) S 1 12345 12345 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 98765432\n";
        const string only43Fields = "45678 (worker) R 1 45678 45678 0 -1 4194304 10 0 0 0 0 0 0 0 20 0 1 0 555 4096 128 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0\n";
        var nonNumericPpid = validFullStat.Replace(") S 1 ", ") S badppid ", StringComparison.Ordinal);
        var negativePpid = validFullStat.Replace(") S 1 ", ") S -1 ", StringComparison.Ordinal);
        var trailingGarbage = $"{validWithoutNewline} garbage\n";
        var trailingSpace = $"{validWithoutNewline} \n";
        var invalidStateLetter = validFullStat.Replace(") S ", ") Q ", StringComparison.Ordinal);
        var crlfStat = $"{validWithoutNewline}\r\n";
        var embeddedNewlineInComm = BuildSyntheticLinuxProcessStat(45678, "line1\nline2", 'R', 555UL);
        var twoConcatenatedLines = $"{validFullStat}{validFullStat}";

        Assert.IsFalse(TryParseLinuxProcessStat(null, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(string.Empty, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validWithoutNewline, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(truncatedInsideStartTime, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(truncatedAfterStartTimeWithNewline, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(only43Fields, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(nonNumericPpid, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(negativePpid, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(trailingGarbage, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(trailingSpace, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(invalidStateLetter, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(crlfStat, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(embeddedNewlineInComm, out _));
        Assert.IsFalse(TryParseLinuxProcessStat(twoConcatenatedLines, out _));
        Assert.IsFalse(TryParseLinuxProcessStat($" {validFullStat}", out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace("(sleep)", "sleep", StringComparison.Ordinal), out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace("12345 (sleep)", "(sleep)", StringComparison.Ordinal), out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace("(sleep)", ")sleep(", StringComparison.Ordinal), out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace(") S ", ")S ", StringComparison.Ordinal), out _));
        Assert.IsFalse(TryParseLinuxProcessStat(BuildSyntheticLinuxProcessStat(0, "swapper", 'S', 0UL), out _));
        Assert.IsFalse(TryParseLinuxProcessStat("12345 (sleep) S 1 2 3\n", out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace(") S ", ") ZZ ", StringComparison.Ordinal), out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace(") S ", ") 9 ", StringComparison.Ordinal), out _));
        Assert.IsFalse(TryParseLinuxProcessStat(validFullStat.Replace("987654321", "not-ticks", StringComparison.Ordinal), out _));
    }

    [TestMethod]
    public void TryParseFixtureProcessIdentity_ParsesAtomicIdentityAndRejectsTruncatedOrMismatchedContent()
    {
        var validStat = BuildSyntheticLinuxProcessStat(12345, "hang (fixture) sh", 'S', 987654321UL);
        var initStat = BuildSyntheticLinuxProcessStat(1, "init", 'S', 1UL);

        Assert.IsTrue(TryParseFixtureProcessIdentity($"12345\n{validStat}", out var identity));
        Assert.AreEqual(new FixtureProcessIdentity(12345, 987654321UL), identity);

        Assert.IsTrue(TryParseFixtureProcessIdentity($"12345\r\n{validStat}", out var crlfIdentity));
        Assert.AreEqual(new FixtureProcessIdentity(12345, 987654321UL), crlfIdentity);

        Assert.IsFalse(TryParseFixtureProcessIdentity(null, out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity(string.Empty, out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity("   \n", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity("12345", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity("12345\n", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity($"12345\n{validStat[..^1]}", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity("12345\n12345 (sleep) S 1 2 3\n", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity($"not-a-pid\n{validStat}", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity($"1\n{initStat}", out _));
        Assert.IsFalse(TryParseFixtureProcessIdentity($"99999\n{validStat}", out _));
    }

    [TestMethod]
    public async Task TryReadFixtureProcessIdentityAsync_HandlesMissingEmptyPartialAndAtomicFilesWithoutThrowing()
    {
        var root = Directory.CreateTempSubdirectory("hvo-pidfile-parse-");
        try
        {
            var pidFile = Path.Combine(root.FullName, "release.json.pid");
            Assert.IsNull(await TryReadFixtureProcessIdentityAsync(pidFile));

            await File.WriteAllTextAsync(pidFile, string.Empty);
            Assert.IsNull(await TryReadFixtureProcessIdentityAsync(pidFile));

            await File.WriteAllTextAsync(pidFile, "12345");
            Assert.IsNull(await TryReadFixtureProcessIdentityAsync(pidFile));

            await File.WriteAllTextAsync(pidFile, "12345\n12345 (sleep) S 1 2");
            Assert.IsNull(await TryReadFixtureProcessIdentityAsync(pidFile));

            var tempFile = pidFile + ".tmp";
            await File.WriteAllTextAsync(
                tempFile,
                $"12345\n{BuildSyntheticLinuxProcessStat(12345, "sleep", 'S', 987654321UL)}");
            File.Move(tempFile, pidFile, overwrite: true);

            Assert.AreEqual(
                new FixtureProcessIdentity(12345, 987654321UL),
                await TryReadFixtureProcessIdentityAsync(pidFile));
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public void ReadLinuxProcessStat_ClassifiesPresentNotFoundAndUnreadableOrMalformed()
    {
        const int fixturePid = 12345;
        var validStat = BuildSyntheticLinuxProcessStat(fixturePid, "sleep", 'S', 987654321UL);
        var validComplexCommStat = BuildSyntheticLinuxProcessStat(fixturePid, "my (complex) sleep", 'S', 987654321UL);

        var present = ReadLinuxProcessStat(fixturePid, path =>
        {
            Assert.AreEqual("/proc/12345/stat", path);
            return validStat;
        });
        Assert.AreEqual(
            new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', 987654321UL)),
            present);

        var presentComplexComm = ReadLinuxProcessStat(fixturePid, _ => validComplexCommStat);
        Assert.AreEqual(
            new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', 987654321UL)),
            presentComplexComm);

        var fileNotFound = ReadLinuxProcessStat(fixturePid, static _ => throw new FileNotFoundException("gone"));
        Assert.AreSame(LinuxProcessStatReadResult.NotFound.Instance, fileNotFound);

        var directoryNotFound = ReadLinuxProcessStat(fixturePid, static _ => throw new DirectoryNotFoundException("gone"));
        Assert.AreSame(LinuxProcessStatReadResult.NotFound.Instance, directoryNotFound);

        var unauthorized = ReadLinuxProcessStat(fixturePid, static _ => throw new UnauthorizedAccessException("hidepid"));
        var unauthorizedResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(unauthorized);
        StringAssert.Contains(unauthorizedResult.Diagnostic, "UnauthorizedAccessException", StringComparison.Ordinal);
        StringAssert.Contains(unauthorizedResult.Diagnostic, "/proc/12345/stat", StringComparison.Ordinal);

        var ioFailure = ReadLinuxProcessStat(fixturePid, static _ => throw new IOException("read error"));
        var ioFailureResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(ioFailure);
        StringAssert.Contains(ioFailureResult.Diagnostic, "IOException", StringComparison.Ordinal);
        StringAssert.Contains(ioFailureResult.Diagnostic, "/proc/12345/stat", StringComparison.Ordinal);

        var malformed = ReadLinuxProcessStat(fixturePid, static _ => "12345 (sleep) S truncated");
        var malformedResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(malformed);
        StringAssert.Contains(malformedResult.Diagnostic, "malformed", StringComparison.Ordinal);
        StringAssert.Contains(malformedResult.Diagnostic, "/proc/12345/stat", StringComparison.Ordinal);

        var truncatedInsideStartTime = ReadLinuxProcessStat(
            fixturePid,
            static _ => "12345 (sleep) S 1 12345 12345 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 98765432");
        var truncatedInsideResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(truncatedInsideStartTime);
        StringAssert.Contains(truncatedInsideResult.Diagnostic, "malformed", StringComparison.Ordinal);

        var truncatedAfterStartTime = ReadLinuxProcessStat(
            fixturePid,
            static _ => "12345 (sleep) S 1 12345 12345 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 98765432\n");
        var truncatedAfterResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(truncatedAfterStartTime);
        StringAssert.Contains(truncatedAfterResult.Diagnostic, "malformed", StringComparison.Ordinal);

        var nonNumericPpid = ReadLinuxProcessStat(
            fixturePid,
            _ => validStat.Replace(") S 1 ", ") S badppid ", StringComparison.Ordinal));
        var nonNumericPpidResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(nonNumericPpid);
        StringAssert.Contains(nonNumericPpidResult.Diagnostic, "malformed", StringComparison.Ordinal);

        var trailingGarbage = ReadLinuxProcessStat(
            fixturePid,
            _ => $"{validStat[..^1]} trailing-garbage\n");
        var trailingGarbageResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(trailingGarbage);
        StringAssert.Contains(trailingGarbageResult.Diagnostic, "malformed", StringComparison.Ordinal);

        var mismatchedPid = ReadLinuxProcessStat(
            fixturePid,
            static _ => BuildSyntheticLinuxProcessStat(54321, "sleep", 'S', 987654321UL));
        var mismatchedPidResult = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(mismatchedPid);
        StringAssert.Contains(mismatchedPidResult.Diagnostic, "mismatched PID 54321", StringComparison.Ordinal);
        StringAssert.Contains(mismatchedPidResult.Diagnostic, "/proc/12345/stat", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ShouldKillFixtureProcess_RejectsIdentityAndCommandLineMismatches()
    {
        var expected = new FixtureProcessIdentity(12345, 987654321UL);
        var matchingStat = new LinuxProcessStatReadResult.Present(new LinuxProcessStat(12345, 'S', 987654321UL));
        const string scriptPath = "/tmp/hvo-mkfifo-hang/hang.sh";

        Assert.IsTrue(ShouldKillFixtureProcess(expected, matchingStat, "sleep\030\0", scriptPath));
        Assert.IsTrue(ShouldKillFixtureProcess(expected, matchingStat, "/usr/bin/sleep\030\0", scriptPath));
        Assert.IsTrue(ShouldKillFixtureProcess(expected, matchingStat, "busybox\0sleep\030\0", scriptPath));
        Assert.IsTrue(ShouldKillFixtureProcess(expected, matchingStat, $"/bin/sh\0{scriptPath}\0/tmp/hvo-mkfifo-hang/release.json\0", scriptPath));

        // Reused PID with a different start time must never be killed even if the command line happens to match.
        var reusedPidStat = new LinuxProcessStatReadResult.Present(new LinuxProcessStat(12345, 'S', 999999999UL));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, reusedPidStat, "sleep\030\0", scriptPath));

        // Different PID, unrelated command line, empty command line, zombie, missing stat, or unreadable stat must not be signaled.
        Assert.IsFalse(ShouldKillFixtureProcess(expected, new LinuxProcessStatReadResult.Present(new LinuxProcessStat(54321, 'S', 987654321UL)), "sleep\030\0", scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, matchingStat, "/usr/bin/dotnet\0test\0", scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, matchingStat, "sleep\060\0", scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, matchingStat, string.Empty, scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, matchingStat, null, scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, new LinuxProcessStatReadResult.Present(new LinuxProcessStat(12345, 'Z', 987654321UL)), string.Empty, scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, LinuxProcessStatReadResult.NotFound.Instance, "sleep\030\0", scriptPath));
        Assert.IsFalse(ShouldKillFixtureProcess(expected, new LinuxProcessStatReadResult.UnreadableOrMalformed("permission denied"), "sleep\030\0", scriptPath));
    }

    [TestMethod]
    public void IsFixtureProcessTerminated_TreatsZombieAsTerminatedOnlyWhenAllowed()
    {
        var expected = new FixtureProcessIdentity(12345, 987654321UL);
        var liveStat = new LinuxProcessStatReadResult.Present(new LinuxProcessStat(12345, 'S', 987654321UL));
        var zombieStat = new LinuxProcessStatReadResult.Present(new LinuxProcessStat(12345, 'Z', 987654321UL));
        var reusedPidStat = new LinuxProcessStatReadResult.Present(new LinuxProcessStat(12345, 'S', 999999999UL));
        var unreadableStat = new LinuxProcessStatReadResult.UnreadableOrMalformed("unable to read '/proc/12345/stat'");

        Assert.IsTrue(IsFixtureProcessTerminated(expected, LinuxProcessStatReadResult.NotFound.Instance, allowZombieAsTerminated: false));
        Assert.IsTrue(IsFixtureProcessTerminated(expected, LinuxProcessStatReadResult.NotFound.Instance, allowZombieAsTerminated: true));

        Assert.IsTrue(IsFixtureProcessTerminated(expected, reusedPidStat, allowZombieAsTerminated: false));
        Assert.IsTrue(IsFixtureProcessTerminated(expected, reusedPidStat, allowZombieAsTerminated: true));

        Assert.IsFalse(IsFixtureProcessTerminated(expected, liveStat, allowZombieAsTerminated: false));
        Assert.IsFalse(IsFixtureProcessTerminated(expected, liveStat, allowZombieAsTerminated: true));

        Assert.IsFalse(IsFixtureProcessTerminated(expected, zombieStat, allowZombieAsTerminated: false));
        Assert.IsTrue(IsFixtureProcessTerminated(expected, zombieStat, allowZombieAsTerminated: true));

        Assert.IsFalse(IsFixtureProcessTerminated(expected, unreadableStat, allowZombieAsTerminated: false));
        Assert.IsFalse(IsFixtureProcessTerminated(expected, unreadableStat, allowZombieAsTerminated: true));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("12345")]
    [DataRow("not-a-pid")]
    public async Task CleanUpFifoFixtureAsync_WhenPidFileMissingEmptyOrPartial_DeletesFixtureWithoutThrowing(string? pidFileContent)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-cleanup-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        if (pidFileContent is not null)
        {
            await File.WriteAllTextAsync(pidFile, pidFileContent);
        }

        await CleanUpFifoFixtureAsync(root, pidFile, allowZombieAsTerminated: true);

        Assert.IsFalse(Directory.Exists(root.FullName), "Fixture directory should be removed even when the PID file is absent or partial.");
    }

    private static readonly string[] ExpectedSignalSequence =
    [
        "read-stat",
        "open-pidfd:12345",
        "read-stat",
        "read-cmdline",
        $"send-signal:42:{PosixSigKill}",
        "close-pidfd:42",
        "read-stat"
    ];

    private static readonly string[] ExpectedReusedAfterOpenSequence =
    [
        "read-stat",
        "open-pidfd:12345",
        "read-stat",
        "close-pidfd:42"
    ];

    private static readonly string[] ExpectedRefusedSignalSequence =
    [
        "read-stat",
        "open-pidfd:12345",
        "read-stat",
        "read-cmdline",
        "close-pidfd:42",
        "read-stat"
    ];

    private static readonly string[] ExpectedOpenEsrchSequence =
    [
        "read-stat",
        "open-pidfd:12345",
        "read-stat"
    ];

    private static readonly string[] ExpectedInitialReadOnlySequence = ["read-stat"];

    private static readonly string[] ExpectedUnreadableInitialStatSequence =
    [
        "read-stat",
        "read-stat"
    ];

    private static readonly string[] ExpectedUnreadableVerifiedStatSequence =
    [
        "read-stat",
        "open-pidfd:12345",
        "read-stat",
        "close-pidfd:42",
        "read-stat"
    ];

    [TestMethod]
    public async Task CleanUpFifoFixtureAsync_OpensPidFdBeforeIdentityVerificationSignalsViaPidFdAndClosesInFinally()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-pidfd-order-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                Assert.AreEqual(fixturePid, pid);
                events.Add("read-stat");
                statReadCount++;
                return statReadCount <= 2
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks))
                    : LinuxProcessStatReadResult.NotFound.Instance;
            },
            ReadProcessCmdline: pid =>
            {
                Assert.AreEqual(fixturePid, pid);
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                Assert.AreEqual(fixturePid, pid);
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: true, operations);

        CollectionAssert.AreEqual(ExpectedSignalSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    public async Task CleanUpFifoFixtureAsync_WhenPidReusedAfterOpeningPidFd_ClosesPidFdWithoutSignalingAndSucceeds()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-pidfd-reused-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                statReadCount++;
                return statReadCount == 1
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks))
                    : new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks + 1));
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: false, operations);

        CollectionAssert.AreEqual(ExpectedReusedAfterOpenSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    public async Task CleanUpFifoFixtureAsync_WhenCmdlineMismatchesAfterOpeningPidFdAndFixtureRemainsAlive_ClosesPidFdWithoutSignalingAndFails()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-pidfd-mismatch-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                return new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks));
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "/usr/bin/dotnet\0test\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: true, operations));

        StringAssert.Contains(failure.Message, "cmdline did not match", StringComparison.Ordinal);
        CollectionAssert.AreEqual(ExpectedRefusedSignalSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CleanUpFifoFixtureAsync_WhenCmdlineUnreadableAndFixtureRemainsAlive_RefusesToSignalClosesPidFdAndFails(
        bool allowZombieAsTerminated)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-cmdline-null-alive-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                return new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks));
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return null;
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations));

        StringAssert.Contains(failure.Message, "cmdline could not be read", StringComparison.Ordinal);
        CollectionAssert.AreEqual(ExpectedRefusedSignalSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    public async Task CleanUpFifoFixtureAsync_WhenCmdlineUnreadableBecauseFixtureExited_RefusesToSignalClosesPidFdAndSucceeds()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-cmdline-null-exited-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                statReadCount++;
                return statReadCount <= 2
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks))
                    : LinuxProcessStatReadResult.NotFound.Instance;
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return null;
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: false, operations);

        CollectionAssert.AreEqual(ExpectedRefusedSignalSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    public async Task CleanUpFifoFixtureAsync_WhenOpenPidFdReturnsEsrchAndProcessExited_SucceedsWithoutSignalingOrClosing()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-open-esrch-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                statReadCount++;
                return statReadCount == 1
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks))
                    : LinuxProcessStatReadResult.NotFound.Instance;
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Failure(PosixNoSuchProcess);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: false, operations);

        CollectionAssert.AreEqual(ExpectedOpenEsrchSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CleanUpFifoFixtureAsync_WhenSendPidFdSignalReturnsEsrchAndProcessGoneOrReused_ClosesPidFdAndSucceeds(
        bool pidReused)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-signal-esrch-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                statReadCount++;
                if (statReadCount <= 2)
                {
                    return new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks));
                }

                return pidReused
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks + 10))
                    : LinuxProcessStatReadResult.NotFound.Instance;
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Failure(PosixNoSuchProcess);
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: false, operations);

        CollectionAssert.AreEqual(ExpectedSignalSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CleanUpFifoFixtureAsync_WhenAlreadyExitedOrPidReused_SucceedsWithoutOpeningPidFd(bool pidReused)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-already-gone-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ =>
            {
                events.Add("read-stat");
                return pidReused
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks + 1))
                    : LinuxProcessStatReadResult.NotFound.Instance;
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: false, operations);

        CollectionAssert.AreEqual(ExpectedInitialReadOnlySequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CleanUpFifoFixtureAsync_WhenPidFdOperationFailsWithNonEsrchAndFixtureRemainsAlive_FailsExplicitly(
        bool failOnOpen)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-pidfd-err-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var closedPidFd = false;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ => new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks)),
            ReadProcessCmdline: _ => "sleep\030\0",
            OpenPidFd: _ => failOnOpen
                ? PidFdOpenResult.Failure(PosixOperationNotPermitted)
                : PidFdOpenResult.Success(42),
            SendPidFdSignal: (_, _) => PidFdSignalResult.Failure(PosixOperationNotPermitted),
            ClosePidFd: pidfd =>
            {
                Assert.AreEqual(42, pidfd);
                closedPidFd = true;
            },
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: true, operations));

        StringAssert.Contains(
            failure.Message,
            failOnOpen ? "pidfd_open failed with errno 1" : "pidfd_send_signal(SIGKILL) failed with errno 1",
            StringComparison.Ordinal);
        Assert.AreEqual(!failOnOpen, closedPidFd);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    public async Task CleanUpFifoFixtureAsync_WhenSignalThrows_StillClosesPidFdInFinally()
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-pidfd-throw-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var closedPidFd = -1;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: _ => new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks)),
            ReadProcessCmdline: _ => "sleep\030\0",
            OpenPidFd: _ => PidFdOpenResult.Success(99),
            SendPidFdSignal: (_, _) => throw new InvalidOperationException("injected signal fault"),
            ClosePidFd: pidfd => closedPidFd = pidfd,
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated: true, operations));

        Assert.AreEqual(99, closedPidFd);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false, "unauthorized", true)]
    [DataRow(true, "unauthorized", true)]
    [DataRow(false, "malformed", false)]
    [DataRow(true, "malformed", false)]
    [DataRow(false, "io-error", false)]
    [DataRow(true, "io-error", true)]
    public async Task CleanUpFifoFixtureAsync_WhenStatUnreadableOrMalformedAfterCmdlineRefusal_FailsExplicitly(
        bool allowZombieAsTerminated,
        string statFault,
        bool unreadableCmdline)
    {
        ArgumentNullException.ThrowIfNull(statFault);
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-stat-fault-cmdline-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                events.Add("read-stat");
                statReadCount++;
                if (statReadCount <= 2)
                {
                    return new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks));
                }

                return ReadSimulatedFaultyStat(pid, statFault);
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return unreadableCmdline ? null : "/usr/bin/dotnet\0test\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations));

        StringAssert.Contains(
            failure.Message,
            unreadableCmdline ? "cmdline could not be read" : "cmdline did not match",
            StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, ExpectedStatFaultDiagnosticToken(statFault), StringComparison.Ordinal);
        CollectionAssert.AreEqual(ExpectedRefusedSignalSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false, "unauthorized", false)]
    [DataRow(true, "unauthorized", false)]
    [DataRow(false, "malformed", false)]
    [DataRow(true, "malformed", false)]
    [DataRow(false, "unauthorized", true)]
    [DataRow(true, "malformed", true)]
    public async Task CleanUpFifoFixtureAsync_WhenStatUnreadableOrMalformedAfterFailedSignal_FailsExplicitly(
        bool allowZombieAsTerminated,
        string statFault,
        bool failOnOpen)
    {
        ArgumentNullException.ThrowIfNull(statFault);
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-stat-fault-signal-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var presentReadLimit = failOnOpen ? 1 : 2;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                events.Add("read-stat");
                statReadCount++;
                if (statReadCount <= presentReadLimit)
                {
                    return new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks));
                }

                return ReadSimulatedFaultyStat(pid, statFault);
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return failOnOpen
                    ? PidFdOpenResult.Failure(PosixOperationNotPermitted)
                    : PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Failure(PosixOperationNotPermitted);
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations));

        StringAssert.Contains(
            failure.Message,
            failOnOpen ? "pidfd_open failed with errno 1" : "pidfd_send_signal(SIGKILL) failed with errno 1",
            StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, ExpectedStatFaultDiagnosticToken(statFault), StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            failOnOpen ? ExpectedOpenEsrchSequence : ExpectedSignalSequence,
            events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false, "unauthorized")]
    [DataRow(true, "unauthorized")]
    [DataRow(false, "malformed")]
    [DataRow(true, "malformed")]
    public async Task CleanUpFifoFixtureAsync_WhenStatInitiallyUnreadableOrMalformed_NeverSignalsAndFailsExplicitly(
        bool allowZombieAsTerminated,
        string statFault)
    {
        ArgumentNullException.ThrowIfNull(statFault);
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-stat-fault-initial-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                events.Add("read-stat");
                return ReadSimulatedFaultyStat(pid, statFault);
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations));

        StringAssert.Contains(failure.Message, ExpectedStatFaultDiagnosticToken(statFault), StringComparison.Ordinal);
        CollectionAssert.AreEqual(ExpectedUnreadableInitialStatSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CleanUpFifoFixtureAsync_WhenStatUnreadableOrMalformedAfterOpeningPidFd_ClosesPidFdWithoutSignalingAndFails(
        bool allowZombieAsTerminated)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-stat-fault-verified-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                events.Add("read-stat");
                statReadCount++;
                return statReadCount == 1
                    ? new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks))
                    : ReadSimulatedFaultyStat(pid, "unauthorized");
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations));

        StringAssert.Contains(failure.Message, "UnauthorizedAccessException", StringComparison.Ordinal);
        CollectionAssert.AreEqual(ExpectedUnreadableVerifiedStatSequence, events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false, true, true)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    public async Task CleanUpFifoFixtureAsync_WhenStatNotFoundAfterCmdlineRefusalOrFailedSignal_SucceedsCleanly(
        bool allowZombieAsTerminated,
        bool refuseByCmdline,
        bool useFileNotFound)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-stat-notfound-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                events.Add("read-stat");
                statReadCount++;
                if (statReadCount <= 2)
                {
                    return new LinuxProcessStatReadResult.Present(new LinuxProcessStat(fixturePid, 'S', startTimeTicks));
                }

                return ReadLinuxProcessStat(
                    pid,
                    _ => throw (useFileNotFound
                        ? new FileNotFoundException("process exited")
                        : new DirectoryNotFoundException("proc dir gone")));
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return refuseByCmdline ? "/usr/bin/dotnet\0test\0" : "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Failure(PosixOperationNotPermitted);
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        await CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations);

        CollectionAssert.AreEqual(
            refuseByCmdline ? ExpectedRefusedSignalSequence : ExpectedSignalSequence,
            events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    public async Task CleanUpFifoFixtureAsync_WhenStatTruncatedInsideOrAfterStartTime_DoesNotTreatAsPidReuseAndFails(
        bool allowZombieAsTerminated,
        bool includeNewlineAfterStartTime,
        bool failOnInitialRead)
    {
        var root = Directory.CreateTempSubdirectory("hvo-mkfifo-stat-truncated-starttime-");
        var pidFile = Path.Combine(root.FullName, "release.json.pid");
        const int fixturePid = 12345;
        const ulong startTimeTicks = 987654321UL;
        await WriteFixturePidFileAsync(pidFile, fixturePid, startTimeTicks);

        var truncatedStatContent = includeNewlineAfterStartTime
            ? "12345 (sleep) S 1 12345 12345 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 98765432\n"
            : "12345 (sleep) S 1 12345 12345 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 98765432";
        var parsedTruncated = ReadLinuxProcessStat(fixturePid, _ => truncatedStatContent);
        _ = Assert.IsInstanceOfType<LinuxProcessStatReadResult.UnreadableOrMalformed>(parsedTruncated);

        var events = new List<string>();
        var statReadCount = 0;
        var operations = new FifoFixtureCleanupOperations(
            ReadProcessStat: pid =>
            {
                events.Add("read-stat");
                statReadCount++;
                if (!failOnInitialRead && statReadCount <= 2)
                {
                    return ReadLinuxProcessStat(
                        pid,
                        static _ => BuildSyntheticLinuxProcessStat(fixturePid, "sleep", 'S', startTimeTicks));
                }

                return ReadLinuxProcessStat(pid, _ => truncatedStatContent);
            },
            ReadProcessCmdline: _ =>
            {
                events.Add("read-cmdline");
                return "sleep\030\0";
            },
            OpenPidFd: pid =>
            {
                events.Add($"open-pidfd:{pid}");
                return PidFdOpenResult.Success(42);
            },
            SendPidFdSignal: (pidfd, signal) =>
            {
                events.Add($"send-signal:{pidfd}:{signal}");
                return PidFdSignalResult.Success();
            },
            ClosePidFd: pidfd => events.Add($"close-pidfd:{pidfd}"),
            TerminationTimeout: TimeSpan.Zero,
            PollInterval: TimeSpan.Zero);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => CleanUpFifoFixtureAsync(root, pidFile, expectedScriptPath: null, allowZombieAsTerminated, operations));

        StringAssert.Contains(failure.Message, "malformed", StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            failOnInitialRead ? ExpectedUnreadableInitialStatSequence : ExpectedSignalSequence,
            events);
        Assert.IsFalse(Directory.Exists(root.FullName));
    }

    private static LinuxProcessStatReadResult ReadSimulatedFaultyStat(int pid, string statFault)
        => statFault switch
        {
            "unauthorized" => ReadLinuxProcessStat(pid, static _ => throw new UnauthorizedAccessException("hidepid")),
            "io-error" => ReadLinuxProcessStat(pid, static _ => throw new IOException("simulated proc read error")),
            "malformed" => ReadLinuxProcessStat(pid, static _ => "12345 (sleep) S truncated"),
            _ => throw new ArgumentOutOfRangeException(nameof(statFault), statFault, null)
        };

    private static string ExpectedStatFaultDiagnosticToken(string statFault)
        => statFault switch
        {
            "unauthorized" => "UnauthorizedAccessException",
            "io-error" => "IOException",
            "malformed" => "malformed",
            _ => throw new ArgumentOutOfRangeException(nameof(statFault), statFault, null)
        };

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(13, true)]
    [DataRow(5, false)]
    [DataRow(12, false)]
    [DataRow(20, false)]
    [DataRow(24, false)]
    public void IsUnavailableCommandStartError_OnlySkipsAvailabilityAndPermissionErrors(int nativeErrorCode, bool expected)
        => Assert.AreEqual(expected, IsUnavailableCommandStartError(nativeErrorCode));

    [TestMethod]
    [DataRow(127, "", null, true)]
    [DataRow(126, "", null, true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/x': Operation not permitted", "/tmp/x", true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/x': Operation not supported", "/tmp/x", true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/x': Function not implemented", "/tmp/x", true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/x': Permission denied", "/tmp/x", true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/x': Read-only file system", "/tmp/x", true)]
    [DataRow(1, "mkfifo: failed (EPERM)", null, true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/EPERM/release.json': File exists", "/tmp/EPERM/release.json", false)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/not supported/release.json': File exists", "/tmp/not supported/release.json", false)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/Operation not permitted:x/release.json': No space left on device", "/tmp/Operation not permitted:x/release.json", false)]
    [DataRow(1, "mkfifo: '/tmp/EPERM/release.json'", "/tmp/EPERM/release.json", false)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/EPERM\n/fifo': File exists", "/tmp/EPERM\n/fifo", false)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/not supported\r\n/fifo': Permission denied", "/tmp/not supported\r\n/fifo", true)]
    [DataRow(1, "mkfifo: failed (TEST_EPERM)", null, false)]
    [DataRow(1, "mkfifo: failed (EPERM_DETAIL)", null, false)]
    [DataRow(1, "mkfifo: failed (TEST_EPERM, EPERM)", null, true)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/fileperm/x': File exists", "/tmp/fileperm/x", false)]
    [DataRow(1, "mkfifo: cannot create fifo '/tmp/missing/x': No such file or directory", "/tmp/missing/x", false)]
    public void IsRestrictedFifoError_ClassifiesRestrictedAndUnexpectedErrors(
        int exitCode,
        string stderr,
        string? targetPath,
        bool expected)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        Assert.AreEqual(expected, IsRestrictedFifoError(exitCode, stderr, targetPath));
    }

    private const int PosixOperationNotPermitted = 1;
    private const int PosixNoSuchFileOrDirectory = 2;
    private const int PosixNoSuchProcess = 3;
    private const int PosixInterruptedSystemCall = 4;
    private const int PosixPermissionDenied = 13;
    private const int PosixSigKill = 9;
    // Unified Linux syscall numbers across x86_64 and arm64 (since Linux 5.1 / 5.3).
    private const nint LinuxSysPidfdSendSignal = 424;
    private const nint LinuxSysPidfdOpen = 434;
    private static readonly TimeSpan DefaultFifoCommandTimeout = TimeSpan.FromSeconds(10);

    private static async Task CreateFifoOrInconclusiveAsync(
        string path,
        string command = "mkfifo",
        TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            Assert.Inconclusive("FIFO creation requires a POSIX test environment.");
        }

        var effectiveTimeout = timeout ?? DefaultFifoCommandTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveTimeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(path);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (IsUnavailableCommandStartError(exception.NativeErrorCode))
        {
            Assert.Inconclusive($"'{command}' is unavailable in this test environment (errno {exception.NativeErrorCode}): {exception.Message}");
            return;
        }
        catch (Win32Exception exception)
        {
            Assert.Fail($"'{command}' failed to start unexpectedly (errno {exception.NativeErrorCode}): {exception.Message}");
            return;
        }

        if (process is null)
        {
            Assert.Fail($"'{command}' failed to start unexpectedly (Process.Start returned null).");
            return;
        }

        using (process)
        {
            using var pipeCts = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(pipeCts.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(pipeCts.Token);

            using var exitCts = new CancellationTokenSource(effectiveTimeout);
            try
            {
                await process.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                await KillAndReapProcessTreeAsync(process);
                await CancelAndClosePipesAsync(process, pipeCts, standardOutputTask, standardErrorTask);
                Assert.Fail(
                    $"'{command}' timed out after {effectiveTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms while creating FIFO '{path}'.");
                return;
            }

            var remaining = effectiveTimeout - stopwatch.Elapsed;
            var drainTimeout = remaining > TimeSpan.FromMilliseconds(50)
                ? remaining
                : TimeSpan.FromMilliseconds(50);
            var drainTask = Task.WhenAll(standardOutputTask, standardErrorTask);
            string standardError;
            try
            {
                await drainTask.WaitAsync(drainTimeout);
                standardError = (await standardErrorTask).Trim();
            }
            catch (TimeoutException)
            {
                ObserveFaults(drainTask);
                await CancelAndClosePipesAsync(process, pipeCts, standardOutputTask, standardErrorTask);
                Assert.Fail(
                    $"'{command}' exited with code {process.ExitCode}, but its redirected output streams remained open past the {effectiveTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms timeout.");
                return;
            }

            if (process.ExitCode == 0)
            {
                return;
            }

            if (IsRestrictedFifoError(process.ExitCode, standardError, path))
            {
                Assert.Inconclusive($"'{command}' cannot create a FIFO in this restricted or non-POSIX test environment (exit {process.ExitCode}): {standardError}");
            }

            Assert.Fail($"'{command}' failed unexpectedly (exit {process.ExitCode}): {standardError}");
        }
    }

    private static bool IsUnavailableCommandStartError(int nativeErrorCode)
        => nativeErrorCode is PosixOperationNotPermitted or PosixNoSuchFileOrDirectory or PosixPermissionDenied;

    private static bool IsRestrictedFifoError(int exitCode, string standardError, string? targetPath = null)
    {
        if (exitCode is 126 or 127)
        {
            return true;
        }

        var diagnostic = string.IsNullOrEmpty(targetPath)
            ? standardError
            : standardError.Replace(targetPath, string.Empty, StringComparison.Ordinal);
        foreach (var line in diagnostic.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var reason = line[(line.LastIndexOf(':') + 1)..].Trim();
            if (reason.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("operation not supported", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("function not implemented", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("read-only file system", StringComparison.OrdinalIgnoreCase)
                || ContainsErrnoToken(reason, "EPERM")
                || ContainsErrnoToken(reason, "ENOTSUP")
                || ContainsErrnoToken(reason, "EOPNOTSUPP")
                || ContainsErrnoToken(reason, "ENOSYS")
                || ContainsErrnoToken(reason, "EACCES")
                || ContainsErrnoToken(reason, "EROFS"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsErrnoToken(string reason, string token)
    {
        var start = 0;
        while ((start = reason.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = start == 0 || !IsErrnoIdentifierCharacter(reason[start - 1]);
            var end = start + token.Length;
            var afterOk = end == reason.Length || !IsErrnoIdentifierCharacter(reason[end]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            start = end;
        }

        return false;
    }

    private static bool IsErrnoIdentifierCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    private readonly record struct LinuxProcessStat(int Pid, char State, ulong StartTimeTicks);

    private abstract record LinuxProcessStatReadResult
    {
        private LinuxProcessStatReadResult()
        {
        }

        public sealed record Present(LinuxProcessStat Stat) : LinuxProcessStatReadResult;

        public sealed record NotFound : LinuxProcessStatReadResult
        {
            public static NotFound Instance { get; } = new();
        }

        public sealed record UnreadableOrMalformed(string Diagnostic) : LinuxProcessStatReadResult;
    }

    private readonly record struct FixtureProcessIdentity(int Pid, ulong StartTimeTicks);

    private readonly record struct PidFdOpenResult(int FileDescriptor, int ErrorCode)
    {
        public bool Succeeded => FileDescriptor >= 0;
        public bool IsProcessGone => FileDescriptor < 0 && ErrorCode == PosixNoSuchProcess;

        public static PidFdOpenResult Success(int fileDescriptor) => new(fileDescriptor, 0);
        public static PidFdOpenResult Failure(int errorCode) => new(-1, errorCode);
    }

    private readonly record struct PidFdSignalResult(bool Succeeded, int ErrorCode)
    {
        public bool IsProcessGone => !Succeeded && ErrorCode == PosixNoSuchProcess;

        public static PidFdSignalResult Success() => new(true, 0);
        public static PidFdSignalResult Failure(int errorCode) => new(false, errorCode);
    }

    private sealed record FifoFixtureCleanupOperations(
        Func<int, LinuxProcessStatReadResult> ReadProcessStat,
        Func<int, string?> ReadProcessCmdline,
        Func<int, PidFdOpenResult> OpenPidFd,
        Func<int, int, PidFdSignalResult> SendPidFdSignal,
        Action<int> ClosePidFd,
        TimeSpan TerminationTimeout,
        TimeSpan PollInterval)
    {
        public static FifoFixtureCleanupOperations DefaultLinux { get; } = new(
            ReadLinuxProcessStat,
            TryReadLinuxProcessCmdline,
            TryOpenLinuxPidFd,
            TrySendLinuxPidFdSignal,
            CloseLinuxPidFd,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(20));
    }

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "Test assembly does not enable unsafe blocks for LibraryImport source generation.")]
    private static class LinuxPidFdInterop
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint SyscallPidfdOpen(nint number, nint pid, nint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint SyscallPidfdSendSignal(nint number, nint pidfd, nint signal, nint info, nint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("libc", EntryPoint = "close", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CloseFileDescriptor(int fd);
    }

    private static string BuildSyntheticLinuxProcessStat(
        int pid,
        string comm = "sleep",
        char state = 'S',
        ulong startTimeTicks = 987654321UL)
    {
        var pidText = pid.ToString(CultureInfo.InvariantCulture);
        var ticksText = startTimeTicks.ToString(CultureInfo.InvariantCulture);
        // 52-field Linux /proc/{pid}/stat format (Linux 3.5+), including negative tpgid (-1)
        // and 64-bit unsigned rsslim (RLIM_INFINITY = 18446744073709551615).
        return $"{pidText} ({comm}) {state} 1 {pidText} {pidText} 0 -1 4194304 89 0 0 0 0 0 0 0 20 0 1 0 {ticksText} 4096 128 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n";
    }

    private static Task WriteFixturePidFileAsync(string pidFile, int pid, ulong startTimeTicks)
    {
        var pidText = pid.ToString(CultureInfo.InvariantCulture);
        var statLine = BuildSyntheticLinuxProcessStat(pid, "sleep", 'S', startTimeTicks);
        return File.WriteAllTextAsync(pidFile, $"{pidText}\n{statLine}");
    }

    private static async Task<FixtureProcessIdentity> ReadFixtureProcessIdentityAsync(string pidFile)
    {
        Assert.IsTrue(File.Exists(pidFile), $"FIFO fixture PID file '{pidFile}' was not written.");
        var content = await File.ReadAllTextAsync(pidFile);
        Assert.IsTrue(
            TryParseFixtureProcessIdentity(content, out var identity),
            $"Invalid FIFO fixture identity in '{pidFile}': '{content.Trim()}'.");
        return identity;
    }

    private static async Task<FixtureProcessIdentity?> TryReadFixtureProcessIdentityAsync(string pidFile)
    {
        if (!File.Exists(pidFile))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(pidFile);
            return TryParseFixtureProcessIdentity(content, out var identity) ? identity : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryParseFixtureProcessIdentity(string? content, out FixtureProcessIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var newlineIndex = content.IndexOf('\n', StringComparison.Ordinal);
        if (newlineIndex <= 0)
        {
            return false;
        }

        var pidLine = content.AsSpan(0, newlineIndex).Trim();
        if (!int.TryParse(pidLine, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 1)
        {
            return false;
        }

        var statLine = content[(newlineIndex + 1)..];
        if (!TryParseLinuxProcessStat(statLine, out var stat) || stat.Pid != pid)
        {
            return false;
        }

        identity = new FixtureProcessIdentity(pid, stat.StartTimeTicks);
        return true;
    }

    private static bool TryParseLinuxProcessStat(string? statContent, out LinuxProcessStat stat)
    {
        stat = default;
        if (string.IsNullOrEmpty(statContent) || statContent[^1] != '\n')
        {
            return false;
        }

        var line = statContent.AsSpan(0, statContent.Length - 1);
        if (line.ContainsAny('\n', '\r'))
        {
            return false;
        }

        var openParen = line.IndexOf('(');
        var closeParen = line.LastIndexOf(')');
        if (openParen < 2
            || line[openParen - 1] != ' '
            || closeParen <= openParen
            || closeParen + 2 >= line.Length
            || line[closeParen + 1] != ' ')
        {
            return false;
        }

        var pidSpan = line[..(openParen - 1)];
        if (!int.TryParse(pidSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return false;
        }

        var fields = statContent[(closeParen + 2)..^1].Split(' ', StringSplitOptions.None);
        // After "(comm) ", /proc/{pid}/stat starts at field 3 (state) at index 0;
        // field 22 (starttime in clock ticks since boot) is at index 19;
        // Linux 2.6.24+ emits at least 44 total fields (42 after "(comm) "), and Linux 3.5+ emits 52 (50 after "(comm) ").
        const int StateFieldIndex = 0;
        const int StartTimeFieldIndex = 19;
        const int MinimumFieldsAfterComm = 42;
        if (fields.Length < MinimumFieldsAfterComm || fields[StateFieldIndex].Length != 1)
        {
            return false;
        }

        var state = fields[StateFieldIndex][0];
        if (!IsValidLinuxProcessState(state))
        {
            return false;
        }

        ulong startTimeTicks = 0;
        for (var index = 1; index < fields.Length; index++)
        {
            var field = fields[index];
            var fieldNumber = index + 3;
            if (index == StartTimeFieldIndex)
            {
                if (!ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out startTimeTicks))
                {
                    return false;
                }

                continue;
            }

            if (!IsValidLinuxStatNumericField(fieldNumber, field))
            {
                return false;
            }
        }

        stat = new LinuxProcessStat(pid, state, startTimeTicks);
        return true;
    }

    private static bool IsValidLinuxProcessState(char state)
        => state is 'R' or 'S' or 'D' or 'Z' or 'T' or 't' or 'W' or 'X' or 'x' or 'K' or 'P' or 'I';

    private static bool IsValidLinuxStatNumericField(int fieldNumber, ReadOnlySpan<char> token)
    {
        if (token.IsEmpty)
        {
            return false;
        }

        return fieldNumber switch
        {
            // Field 4 (ppid): non-negative PID (%d).
            4 => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _),
            // Signed fields in Linux fs/proc/array.c do_task_stat (%d / %ld).
            5 or 6 or 7 or 8 or 16 or 17 or 18 or 19 or 20 or 21 or 24 or 38 or 39 or 44 or 52 =>
                IsStrictSignedInteger(token),
            // Unsigned fields in Linux fs/proc/array.c do_task_stat (%u / %lu / %llu).
            9 or 10 or 11 or 12 or 13 or 14 or 15 or 22 or 23 or (>= 25 and <= 37) or (>= 40 and <= 43) or (>= 45 and <= 51) =>
                IsStrictUnsignedInteger(token),
            // Future kernel fields beyond field 52: accept strict signed or unsigned 64-bit integer tokens only.
            _ => IsStrictUnsignedInteger(token) || IsStrictSignedInteger(token)
        };
    }

    private static bool IsStrictSignedInteger(ReadOnlySpan<char> token)
        => !token.IsEmpty
            && token[0] != '+'
            && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    private static bool IsStrictUnsignedInteger(ReadOnlySpan<char> token)
        => !token.IsEmpty
            && ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsSameFixtureProcess(FixtureProcessIdentity expected, LinuxProcessStat actual)
        => actual.Pid == expected.Pid && actual.StartTimeTicks == expected.StartTimeTicks;

    private static bool IsExpectedFixtureCommandLine(string? cmdline, string? expectedScriptPath)
    {
        if (string.IsNullOrEmpty(cmdline))
        {
            return false;
        }

        var args = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(expectedScriptPath)
            && args.Any(arg => string.Equals(arg, expectedScriptPath, StringComparison.Ordinal)))
        {
            return true;
        }

        var executable = Path.GetFileName(args[0]);
        if (string.Equals(executable, "sleep", StringComparison.Ordinal)
            && args.Length == 2
            && string.Equals(args[1], "30", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(executable, "busybox", StringComparison.Ordinal)
            && args.Length == 3
            && string.Equals(args[1], "sleep", StringComparison.Ordinal)
            && string.Equals(args[2], "30", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(executable, "coreutils", StringComparison.Ordinal)
            && args.Length >= 3
            && string.Equals(args[^1], "30", StringComparison.Ordinal)
            && args.Any(static arg => string.Equals(Path.GetFileName(arg), "sleep", StringComparison.Ordinal)
                || arg.EndsWith("=sleep", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldKillFixtureProcess(
        FixtureProcessIdentity expected,
        LinuxProcessStatReadResult currentStat,
        string? currentCmdline,
        string? expectedScriptPath)
    {
        if (currentStat is not LinuxProcessStatReadResult.Present(var stat)
            || !IsSameFixtureProcess(expected, stat)
            || stat.State == 'Z')
        {
            return false;
        }

        return IsExpectedFixtureCommandLine(currentCmdline, expectedScriptPath);
    }

    private static bool IsFixtureProcessTerminated(
        FixtureProcessIdentity expected,
        LinuxProcessStatReadResult currentStat,
        bool allowZombieAsTerminated)
        => currentStat switch
        {
            LinuxProcessStatReadResult.NotFound => true,
            LinuxProcessStatReadResult.Present(var stat) when !IsSameFixtureProcess(expected, stat) => true,
            LinuxProcessStatReadResult.Present(var stat) => allowZombieAsTerminated && stat.State == 'Z',
            LinuxProcessStatReadResult.UnreadableOrMalformed => false,
            _ => false
        };

    private static LinuxProcessStatReadResult ReadLinuxProcessStat(int pid)
        => ReadLinuxProcessStat(pid, File.ReadAllText);

    private static LinuxProcessStatReadResult ReadLinuxProcessStat(int pid, Func<string, string> readAllText)
    {
        var statPath = $"/proc/{pid}/stat";
        try
        {
            var content = readAllText(statPath);
            if (!TryParseLinuxProcessStat(content, out var stat))
            {
                return new LinuxProcessStatReadResult.UnreadableOrMalformed(
                    $"malformed stat content in '{statPath}'");
            }

            if (stat.Pid != pid)
            {
                return new LinuxProcessStatReadResult.UnreadableOrMalformed(
                    $"mismatched PID {stat.Pid} in '{statPath}'");
            }

            return new LinuxProcessStatReadResult.Present(stat);
        }
        catch (FileNotFoundException)
        {
            return LinuxProcessStatReadResult.NotFound.Instance;
        }
        catch (DirectoryNotFoundException)
        {
            return LinuxProcessStatReadResult.NotFound.Instance;
        }
        catch (UnauthorizedAccessException exception)
        {
            return new LinuxProcessStatReadResult.UnreadableOrMalformed(
                $"unable to read '{statPath}' ({exception.GetType().Name}: {exception.Message})");
        }
        catch (IOException exception)
        {
            return new LinuxProcessStatReadResult.UnreadableOrMalformed(
                $"unable to read '{statPath}' ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private static string? TryReadLinuxProcessCmdline(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PidFdOpenResult TryOpenLinuxPidFd(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux pidfd_open is only supported on Linux.");
        }

        while (true)
        {
            var result = LinuxPidFdInterop.SyscallPidfdOpen(LinuxSysPidfdOpen, pid, 0);
            if (result >= 0)
            {
                return PidFdOpenResult.Success(checked((int)result));
            }

            var errorCode = Marshal.GetLastPInvokeError();
            if (errorCode == PosixInterruptedSystemCall)
            {
                continue;
            }

            return PidFdOpenResult.Failure(errorCode);
        }
    }

    private static PidFdSignalResult TrySendLinuxPidFdSignal(int pidfd, int signal)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux pidfd_send_signal is only supported on Linux.");
        }

        while (true)
        {
            var result = LinuxPidFdInterop.SyscallPidfdSendSignal(
                LinuxSysPidfdSendSignal,
                pidfd,
                signal,
                IntPtr.Zero,
                0);
            if (result == 0)
            {
                return PidFdSignalResult.Success();
            }

            var errorCode = Marshal.GetLastPInvokeError();
            if (errorCode == PosixInterruptedSystemCall)
            {
                continue;
            }

            return PidFdSignalResult.Failure(errorCode);
        }
    }

    private static void CloseLinuxPidFd(int pidfd)
    {
        if (pidfd >= 0 && OperatingSystem.IsLinux())
        {
            _ = LinuxPidFdInterop.CloseFileDescriptor(pidfd);
        }
    }

    private static Task CleanUpFifoFixtureAsync(
        DirectoryInfo root,
        string pidFile,
        string? expectedScriptPath = null,
        bool allowZombieAsTerminated = false)
        => CleanUpFifoFixtureAsync(
            root,
            pidFile,
            expectedScriptPath,
            allowZombieAsTerminated,
            FifoFixtureCleanupOperations.DefaultLinux);

    private static async Task CleanUpFifoFixtureAsync(
        DirectoryInfo root,
        string pidFile,
        string? expectedScriptPath,
        bool allowZombieAsTerminated,
        FifoFixtureCleanupOperations operations)
    {
        try
        {
            var identity = await TryReadFixtureProcessIdentityAsync(pidFile);
            if (identity is null)
            {
                return;
            }

            var currentStat = operations.ReadProcessStat(identity.Value.Pid);
            if (IsFixtureProcessTerminated(identity.Value, currentStat, allowZombieAsTerminated))
            {
                return;
            }

            string? signalFailureReason = null;
            if (currentStat is LinuxProcessStatReadResult.Present { Stat.State: not 'Z' })
            {
                // Open the process-instance pidfd BEFORE verifying /proc/{pid}/stat and /proc/{pid}/cmdline so that
                // signaling cannot race with PID reuse: if the verified (pid, starttime) process still exists while
                // pidfd is open, pidfd is guaranteed to refer to that exact process instance, and pidfd_send_signal
                // will return ESRCH rather than hitting a newly reused PID.
                var openResult = operations.OpenPidFd(identity.Value.Pid);
                if (openResult.Succeeded)
                {
                    try
                    {
                        var verifiedStat = operations.ReadProcessStat(identity.Value.Pid);
                        var verifiedCmdline = verifiedStat is LinuxProcessStatReadResult.Present(var presentVerifiedStat)
                            && presentVerifiedStat.State != 'Z'
                            && IsSameFixtureProcess(identity.Value, presentVerifiedStat)
                            ? operations.ReadProcessCmdline(identity.Value.Pid)
                            : null;
                        if (ShouldKillFixtureProcess(identity.Value, verifiedStat, verifiedCmdline, expectedScriptPath))
                        {
                            var signalResult = operations.SendPidFdSignal(openResult.FileDescriptor, PosixSigKill);
                            if (!signalResult.Succeeded && !signalResult.IsProcessGone)
                            {
                                signalFailureReason = $"pidfd_send_signal(SIGKILL) failed with errno {signalResult.ErrorCode}";
                            }
                        }
                        else if (IsFixtureProcessTerminated(identity.Value, verifiedStat, allowZombieAsTerminated))
                        {
                            return;
                        }
                        else if (verifiedStat is LinuxProcessStatReadResult.UnreadableOrMalformed(var verifiedStatDiagnostic))
                        {
                            signalFailureReason = $"refused to signal because /proc stat was unreadable or malformed: {verifiedStatDiagnostic}";
                        }
                        else if (verifiedStat is LinuxProcessStatReadResult.Present { Stat.State: not 'Z' })
                        {
                            signalFailureReason = verifiedCmdline is null
                                ? "refused to signal because /proc cmdline could not be read"
                                : "refused to signal because /proc cmdline did not match the fixture";
                        }
                    }
                    finally
                    {
                        operations.ClosePidFd(openResult.FileDescriptor);
                    }
                }
                else if (!openResult.IsProcessGone)
                {
                    signalFailureReason = $"pidfd_open failed with errno {openResult.ErrorCode}";
                }
            }
            else if (currentStat is LinuxProcessStatReadResult.UnreadableOrMalformed(var initialStatDiagnostic))
            {
                signalFailureReason = $"refused to signal because /proc stat was unreadable or malformed: {initialStatDiagnostic}";
            }

            // A zombie still has a /proc entry until its parent reaps it; reparented pipe-holders may remain 'Z'
            // on minimal containers whose PID 1 does not reap adopted orphans.
            // Even when signaling was refused or failed, require positive evidence that the fixture process
            // terminated (NotFound, PID reused by a different start time, or 'Z' when allowed) before returning.
            var stopwatch = Stopwatch.StartNew();
            var finalStat = operations.ReadProcessStat(identity.Value.Pid);
            while (!IsFixtureProcessTerminated(identity.Value, finalStat, allowZombieAsTerminated)
                && stopwatch.Elapsed < operations.TerminationTimeout)
            {
                if (operations.PollInterval > TimeSpan.Zero)
                {
                    await Task.Delay(operations.PollInterval);
                }

                finalStat = operations.ReadProcessStat(identity.Value.Pid);
            }

            if (!IsFixtureProcessTerminated(identity.Value, finalStat, allowZombieAsTerminated))
            {
                var detailParts = new List<string>(2);
                if (signalFailureReason is not null)
                {
                    detailParts.Add(signalFailureReason);
                }

                if (finalStat is LinuxProcessStatReadResult.UnreadableOrMalformed(var finalStatDiagnostic))
                {
                    detailParts.Add($"termination could not be verified because /proc stat was unreadable or malformed: {finalStatDiagnostic}");
                }

                var detail = detailParts.Count == 0
                    ? string.Empty
                    : $" ({string.Join("; ", detailParts)})";
                var failureLead = finalStat is LinuxProcessStatReadResult.UnreadableOrMalformed
                    ? $"FIFO fixture PID {identity.Value.Pid} (start time {identity.Value.StartTimeTicks}) termination could not be confirmed during cleanup"
                    : allowZombieAsTerminated
                        ? $"FIFO fixture PID {identity.Value.Pid} (start time {identity.Value.StartTimeTicks}) did not terminate during cleanup"
                        : $"FIFO fixture PID {identity.Value.Pid} (start time {identity.Value.StartTimeTicks}) was not reaped during cleanup";
                Assert.Fail($"{failureLead}{detail}.");
            }
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(recursive: true);
            }
        }
    }

    private static async Task KillAndReapProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(reapCts.Token);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task CancelAndClosePipesAsync(
        Process process,
        CancellationTokenSource pipeCts,
        Task<string> standardOutputTask,
        Task<string> standardErrorTask)
    {
        await pipeCts.CancelAsync();
        TryCloseStream(process.StandardOutput);
        TryCloseStream(process.StandardError);
        ObserveFaults(standardOutputTask);
        ObserveFaults(standardErrorTask);
    }

    private static void TryCloseStream(StreamReader reader)
    {
        try
        {
            reader.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ObserveFaults(Task task)
        => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static InstallRequest Request(string? release) => new()
    {
        FriendlyName = "Camera",
        OwnerEmail = "owner@example.test",
        CatalogBundle = "/srv/catalog.bundle",
        Release = release
    };

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
