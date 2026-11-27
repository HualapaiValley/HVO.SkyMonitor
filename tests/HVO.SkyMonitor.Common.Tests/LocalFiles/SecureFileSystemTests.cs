using System.Net.Sockets;
using HVO.SkyMonitor.LocalFiles;

namespace HVO.SkyMonitor.Common.Tests.LocalFiles;

[TestClass]
[TestCategory("Unit")]
public sealed class SecureFileSystemTests
{
    private string _temp = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [TestMethod]
    public void SetDirectoryModeNoFollow_AppliesModeViaDescriptorAndRefusesSymlinksAndFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes are not applicable on Windows.");
            return;
        }

        const UnixFileMode permissive =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        const UnixFileMode restricted =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        var realDir = Path.Combine(_temp, "real-dir");
        Directory.CreateDirectory(realDir);
        File.SetUnixFileMode(realDir, permissive);

        SecureFileSystem.SetDirectoryModeNoFollow(realDir, restricted);
        Assert.AreEqual(restricted, File.GetUnixFileMode(realDir));

        var symlinkTarget = Path.Combine(_temp, "symlink-target-dir");
        Directory.CreateDirectory(symlinkTarget);
        File.SetUnixFileMode(symlinkTarget, permissive);
        var symlinkDir = Path.Combine(_temp, "symlink-dir");
        Directory.CreateSymbolicLink(symlinkDir, symlinkTarget);

        var symlinkFault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => SecureFileSystem.SetDirectoryModeNoFollow(symlinkDir, restricted));
        Assert.AreEqual(FileSystemFaultKind.Containment, symlinkFault.Kind);
        Assert.AreEqual(permissive, File.GetUnixFileMode(symlinkTarget),
            "Descriptor-based fchmod with O_NOFOLLOW must never modify the symlink target's permissions.");

        var regularFile = Path.Combine(_temp, "regular.txt");
        File.WriteAllText(regularFile, "data");
        File.SetUnixFileMode(regularFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var fileFault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => SecureFileSystem.SetDirectoryModeNoFollow(regularFile, restricted));
        Assert.AreEqual(FileSystemFaultKind.Containment, fileFault.Kind);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(regularFile));
    }

    [TestMethod]
    public void SetFileMode_AppliesModeViaOpenHandleAfterPathReplacement()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes are not applicable on Windows.");
            return;
        }

        var path = Path.Combine(_temp, "mode-file.bin");
        var originalPath = Path.Combine(_temp, "mode-file-original.bin");
        const UnixFileMode permissive = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        const UnixFileMode restricted = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetUnixFileMode(path, permissive);

        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
        {
            File.Move(path, originalPath);
            File.WriteAllBytes(path, [4, 5, 6]);
            File.SetUnixFileMode(path, permissive);
            SecureFileSystem.SetFileMode(handle, restricted, path);
        }

        Assert.AreEqual(restricted, File.GetUnixFileMode(originalPath));
        Assert.AreEqual(permissive, File.GetUnixFileMode(path));
    }

    [TestMethod]
    public void EnsureAndHardenDirectoryChain_WalksPinnedParentComponentsHardensEachAndRejectsSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes and openat/fchmod are not applicable on Windows.");
            return;
        }

        const UnixFileMode permissive =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        const UnixFileMode restricted =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        // 1. Create and harden multi-level hierarchy relative to a pinned parent descriptor without touching anchor mode.
        var anchor = Path.Combine(_temp, "anchor-create");
        Directory.CreateDirectory(anchor);
        File.SetUnixFileMode(anchor, permissive);

        SecureFileSystem.EnsureAndHardenDirectoryChain(
            anchor,
            ["level1", "level2", "leaf"],
            restricted,
            createMissing: true,
            allowSymlinkedAnchor: false);

        Assert.AreEqual(permissive, File.GetUnixFileMode(anchor),
            "Pinning the pre-existing anchor parent must not alter the anchor's own mode.");
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(anchor, "level1")));
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(anchor, "level1", "level2")));
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(anchor, "level1", "level2", "leaf")));

        // 2. Harden existing permissive hierarchy component-by-component with createMissing=false.
        var existingAnchor = Path.Combine(_temp, "anchor-existing");
        var existingLeaf = Path.Combine(existingAnchor, "a", "b", "c");
        Directory.CreateDirectory(existingLeaf);
        File.SetUnixFileMode(existingAnchor, permissive);
        File.SetUnixFileMode(Path.Combine(existingAnchor, "a"), permissive);
        File.SetUnixFileMode(Path.Combine(existingAnchor, "a", "b"), permissive);
        File.SetUnixFileMode(existingLeaf, permissive);

        SecureFileSystem.EnsureAndHardenDirectoryChain(
            existingAnchor,
            ["a", "b", "c"],
            restricted,
            createMissing: false,
            allowSymlinkedAnchor: false);

        Assert.AreEqual(permissive, File.GetUnixFileMode(existingAnchor));
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(existingAnchor, "a")));
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(existingAnchor, "a", "b")));
        Assert.AreEqual(restricted, File.GetUnixFileMode(existingLeaf));

        // 3. Reject symlink at an intermediate component without touching the symlink target's mode.
        var midTarget = Path.Combine(_temp, "mid-symlink-target");
        Directory.CreateDirectory(midTarget);
        Directory.CreateDirectory(Path.Combine(midTarget, "leaf"));
        File.SetUnixFileMode(midTarget, permissive);
        File.SetUnixFileMode(Path.Combine(midTarget, "leaf"), permissive);

        var midParent = Path.Combine(_temp, "mid-parent");
        Directory.CreateDirectory(midParent);
        Directory.CreateSymbolicLink(Path.Combine(midParent, "mid-link"), midTarget);

        var midFault = Assert.ThrowsExactly<FileSystemFaultException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(
                midParent,
                ["mid-link", "leaf"],
                restricted,
                createMissing: true,
                allowSymlinkedAnchor: false));
        Assert.AreEqual(FileSystemFaultKind.Containment, midFault.Kind);
        Assert.AreEqual(permissive, File.GetUnixFileMode(midTarget));
        Assert.AreEqual(permissive, File.GetUnixFileMode(Path.Combine(midTarget, "leaf")));

        // 4. Reject symlink at the leaf component without touching the symlink target's mode.
        var leafTarget = Path.Combine(_temp, "leaf-symlink-target");
        Directory.CreateDirectory(leafTarget);
        File.SetUnixFileMode(leafTarget, permissive);

        var leafParent = Path.Combine(_temp, "leaf-parent");
        Directory.CreateDirectory(Path.Combine(leafParent, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(leafParent, "sub", "leaf-link"), leafTarget);

        var leafFault = Assert.ThrowsExactly<FileSystemFaultException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(
                leafParent,
                ["sub", "leaf-link"],
                restricted,
                createMissing: false,
                allowSymlinkedAnchor: false));
        Assert.AreEqual(FileSystemFaultKind.Containment, leafFault.Kind);
        Assert.AreEqual(permissive, File.GetUnixFileMode(leafTarget));

        // 5. Reject regular file at a component.
        var fileBlocker = Path.Combine(leafParent, "sub", "regular-file");
        File.WriteAllText(fileBlocker, "data");
        var fileFault = Assert.ThrowsExactly<FileSystemFaultException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(
                leafParent,
                ["sub", "regular-file"],
                restricted,
                createMissing: true,
                allowSymlinkedAnchor: false));
        Assert.AreEqual(FileSystemFaultKind.Containment, fileFault.Kind);

        // 6. Reject symlinked anchor when allowSymlinkedAnchor=false, accept when allowSymlinkedAnchor=true
        //    (while still rejecting symlink descendants beneath the symlinked anchor).
        var realAnchorTarget = Path.Combine(_temp, "real-anchor-target");
        Directory.CreateDirectory(realAnchorTarget);
        var symlinkAnchor = Path.Combine(_temp, "symlink-anchor");
        Directory.CreateSymbolicLink(symlinkAnchor, realAnchorTarget);

        var anchorFault = Assert.ThrowsExactly<FileSystemFaultException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(
                symlinkAnchor,
                ["child"],
                restricted,
                createMissing: true,
                allowSymlinkedAnchor: false));
        Assert.AreEqual(FileSystemFaultKind.Containment, anchorFault.Kind);

        SecureFileSystem.EnsureAndHardenDirectoryChain(
            symlinkAnchor,
            ["child"],
            restricted,
            createMissing: true,
            allowSymlinkedAnchor: true);
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(realAnchorTarget, "child")));

        // 7. Reject path-traversal or multi-segment components.
        Assert.ThrowsExactly<ArgumentException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(anchor, [".."], restricted));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(anchor, ["."], restricted));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SecureFileSystem.EnsureAndHardenDirectoryChain(anchor, ["a/b"], restricted));

        // 8. Traverse-only pre-existing parents (0300 and 0100) still pin via O_PATH on Linux and harden descendants.
        if (OperatingSystem.IsLinux() && Environment.UserName != "root")
        {
            var mode0300 = Path.Combine(_temp, "anchor-0300");
            Directory.CreateDirectory(mode0300);
            File.SetUnixFileMode(mode0300, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                SecureFileSystem.EnsureAndHardenDirectoryChain(
                    mode0300,
                    ["nested", "state"],
                    restricted,
                    createMissing: true,
                    allowSymlinkedAnchor: false);
                Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(mode0300, "nested")));
                Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(mode0300, "nested", "state")));
            }
            finally
            {
                File.SetUnixFileMode(mode0300, restricted);
            }

            var mode0100 = Path.Combine(_temp, "anchor-0100");
            var mode0100State = Path.Combine(mode0100, "state");
            Directory.CreateDirectory(mode0100State);
            File.SetUnixFileMode(mode0100State, permissive);
            File.SetUnixFileMode(mode0100, UnixFileMode.UserExecute);
            try
            {
                SecureFileSystem.EnsureAndHardenDirectoryChain(
                    mode0100,
                    ["state"],
                    restricted,
                    createMissing: false,
                    allowSymlinkedAnchor: false);
                Assert.AreEqual(restricted, File.GetUnixFileMode(mode0100State));
            }
            finally
            {
                File.SetUnixFileMode(mode0100, restricted);
            }
        }
    }

    [TestMethod]
    public void InspectPathNoFollowAndFollow_ClassifyDirectoriesFilesSymlinksAndDanglingLinksInSingleObservation()
    {
        var realDir = Path.Combine(_temp, "inspect-dir");
        Directory.CreateDirectory(realDir);
        var realFile = Path.Combine(_temp, "inspect-file.txt");
        File.WriteAllText(realFile, "hello");
        var missingPath = Path.Combine(_temp, "does-not-exist");

        Assert.AreEqual(PathEntryKind.Directory, SecureFileSystem.InspectPathNoFollow(realDir));
        Assert.AreEqual(PathEntryKind.RegularFile, SecureFileSystem.InspectPathNoFollow(realFile));
        Assert.AreEqual(PathEntryKind.NotFound, SecureFileSystem.InspectPathNoFollow(missingPath));
        Assert.AreEqual(PathEntryKind.NotDirectory, SecureFileSystem.InspectPathNoFollow(Path.Combine(realFile, "child")));

        if (!OperatingSystem.IsWindows())
        {
            var linkToDir = Path.Combine(_temp, "link-to-dir");
            var linkToFile = Path.Combine(_temp, "link-to-file");
            var danglingLink = Path.Combine(_temp, "dangling-link");
            Directory.CreateSymbolicLink(linkToDir, realDir);
            File.CreateSymbolicLink(linkToFile, realFile);
            File.CreateSymbolicLink(danglingLink, Path.Combine(_temp, "missing-target"));

            Assert.AreEqual(PathEntryKind.SymbolicLink, SecureFileSystem.InspectPathNoFollow(linkToDir));
            Assert.AreEqual(PathEntryKind.Directory, SecureFileSystem.InspectPathFollow(linkToDir));

            Assert.AreEqual(PathEntryKind.SymbolicLink, SecureFileSystem.InspectPathNoFollow(linkToFile));
            Assert.AreEqual(PathEntryKind.RegularFile, SecureFileSystem.InspectPathFollow(linkToFile));

            // Non-following inspection distinguishes a dangling symlink (SymbolicLink) from genuine absence (NotFound).
            Assert.AreEqual(PathEntryKind.SymbolicLink, SecureFileSystem.InspectPathNoFollow(danglingLink));
            Assert.AreEqual(PathEntryKind.NotFound, SecureFileSystem.InspectPathFollow(danglingLink));

            if (Environment.UserName != "root")
            {
                var lockedDir = Path.Combine(_temp, "inspect-locked");
                Directory.CreateDirectory(lockedDir);
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600 (not traversable)
                try
                {
                    Assert.AreEqual(PathEntryKind.PermissionDenied, SecureFileSystem.InspectPathNoFollow(Path.Combine(lockedDir, "child")));
                }
                finally
                {
                    File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
    }

    [TestMethod]
    public void VerifyDirectoryNoFollow_AcceptsRealDirectoryAndRejectsSymlinksAndFiles()
    {
        var realDir = Path.Combine(_temp, "verify-real-dir");
        Directory.CreateDirectory(realDir);
        SecureFileSystem.VerifyDirectoryNoFollow(realDir);

        var regularFile = Path.Combine(_temp, "verify-file.txt");
        File.WriteAllText(regularFile, "data");
        var fileFault = Assert.ThrowsExactly<FileSystemFaultException>(() => SecureFileSystem.VerifyDirectoryNoFollow(regularFile));
        Assert.AreEqual(FileSystemFaultKind.Containment, fileFault.Kind);

        var missing = Path.Combine(_temp, "verify-missing");
        var missingFault = Assert.ThrowsExactly<FileSystemFaultException>(() => SecureFileSystem.VerifyDirectoryNoFollow(missing));
        Assert.AreEqual(FileSystemFaultKind.NotFound, missingFault.Kind);

        if (!OperatingSystem.IsWindows())
        {
            var linkDir = Path.Combine(_temp, "verify-link-dir");
            Directory.CreateSymbolicLink(linkDir, realDir);
            var linkFault = Assert.ThrowsExactly<FileSystemFaultException>(() => SecureFileSystem.VerifyDirectoryNoFollow(linkDir));
            Assert.AreEqual(FileSystemFaultKind.Containment, linkFault.Kind);
        }
    }

    [TestMethod]
    public void OpenRegularFileReadOnlyNoFollow_VerifiesDescriptorRejectsSymlinksAndFifosWithoutBlockingAndEnforcesSizeBeforeRead()
    {
        var dir = Path.Combine(_temp, "openat-dir");
        Directory.CreateDirectory(dir);

        // 1. Valid regular file within bound opens and reads directly from verified descriptor.
        var validName = "state.json";
        var validPayload = "{\"ok\":true}"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir, validName), validPayload);
        using (var opened = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, validName, maxBytes: validPayload.Length))
        {
            Assert.AreEqual(RegularFileOpenStatus.Opened, opened.Status);
            Assert.AreEqual(validPayload.Length, opened.SizeBytes);
            using var stream = new FileStream(opened.DetachHandle(), FileAccess.Read, 1024, isAsync: false);
            var buffer = new byte[validPayload.Length];
            var read = stream.Read(buffer, 0, buffer.Length);
            Assert.AreEqual(validPayload.Length, read);
            CollectionAssert.AreEqual(validPayload, buffer);
        }

        // 2. Missing file returns NotFound.
        using (var missing = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, "absent.json", maxBytes: 1024))
        {
            Assert.AreEqual(RegularFileOpenStatus.NotFound, missing.Status);
        }

        // 3. Oversized regular file is rejected by fstat(fd) BEFORE returning an open handle or reading payload bytes.
        var oversizedName = "oversized.json";
        File.WriteAllBytes(Path.Combine(dir, oversizedName), new byte[2049]);
        using (var oversized = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, oversizedName, maxBytes: 2048))
        {
            Assert.AreEqual(RegularFileOpenStatus.FileOversized, oversized.Status);
            Assert.AreEqual(2049, oversized.SizeBytes);
            Assert.ThrowsExactly<InvalidOperationException>(() => oversized.DetachHandle());
        }

        // 4. Directory at file name is rejected as FileIsDirectory.
        var subDirName = "subdir";
        Directory.CreateDirectory(Path.Combine(dir, subDirName));
        using (var subDir = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, subDirName, maxBytes: 1024))
        {
            Assert.AreEqual(RegularFileOpenStatus.FileIsDirectory, subDir.Status);
        }

        if (!OperatingSystem.IsWindows())
        {
            // 5. Symbolic link file is rejected via O_NOFOLLOW (ELOOP).
            var linkFileName = "link.json";
            File.CreateSymbolicLink(Path.Combine(dir, linkFileName), Path.Combine(dir, validName));
            using (var symlink = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, linkFileName, maxBytes: 1024))
            {
                Assert.AreEqual(RegularFileOpenStatus.FileSymbolicLink, symlink.Status);
            }

            // 6. Symbolic link directory is rejected via O_DIRECTORY | O_NOFOLLOW.
            var symlinkDir = Path.Combine(_temp, "openat-dir-link");
            Directory.CreateSymbolicLink(symlinkDir, dir);
            using (var dirLink = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(symlinkDir, validName, maxBytes: 1024))
            {
                Assert.AreEqual(RegularFileOpenStatus.DirectorySymbolicLink, dirLink.Status);
            }

            // 7. FIFO (named pipe) with no writer MUST NOT block (O_NONBLOCK) and MUST be rejected via fstat (!S_ISREG).
            var fifoName = "state.fifo";
            var fifoPath = Path.Combine(dir, fifoName);
            using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo")
            {
                ArgumentList = { fifoPath },
                UseShellExecute = false
            }))
            {
                Assert.IsNotNull(mkfifo);
                Assert.IsTrue(mkfifo.WaitForExit(5000));
                Assert.AreEqual(0, mkfifo.ExitCode);
            }

            var openTask = Task.Run(() => SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, fifoName, maxBytes: 1024));
            if (!openTask.Wait(TimeSpan.FromSeconds(5)))
            {
                TryUnblockFifoReader(fifoPath);
                Assert.Fail("Opening a FIFO without a writer blocked past the 5s bounded timeout (O_NONBLOCK regression).");
            }

            using (var fifo = openTask.GetAwaiter().GetResult())
            {
                Assert.AreEqual(RegularFileOpenStatus.FileNonRegular, fifo.Status);
            }

            // 8. Unreadable write-only (0200) oversized vs normal-sized regular files classify via pinned fstatat/statx.
            if (Environment.UserName != "root")
            {
                var writeOnlyOversizedPath = Path.Combine(dir, "write-only-big.json");
                File.WriteAllBytes(writeOnlyOversizedPath, new byte[2049]);
                File.SetUnixFileMode(writeOnlyOversizedPath, UnixFileMode.UserWrite);

                var unreadableSmallPath = Path.Combine(dir, "unreadable-small.json");
                File.WriteAllBytes(unreadableSmallPath, [1, 2, 3]);
                File.SetUnixFileMode(unreadableSmallPath, UnixFileMode.None);

                try
                {
                    using var bigRes = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, "write-only-big.json", maxBytes: 2048);
                    Assert.AreEqual(RegularFileOpenStatus.FileOversized, bigRes.Status);
                    Assert.AreEqual(2049, bigRes.SizeBytes);

                    using var smallRes = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(dir, "unreadable-small.json", maxBytes: 2048);
                    Assert.AreEqual(RegularFileOpenStatus.FilePermissionDenied, smallRes.Status);
                }
                finally
                {
                    File.SetUnixFileMode(writeOnlyOversizedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.SetUnixFileMode(unreadableSmallPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("nested/file")]
    [DataRow("nested\\file")]
    [DataRow("file\0suffix")]
    public void OpenRegularFileReadOnlyNoFollow_RejectsNonLeafNames(string fileName)
    {
        var error = Assert.ThrowsExactly<ArgumentException>(
            () => SecureFileSystem.OpenRegularFileReadOnlyNoFollow(_temp, fileName, maxBytes: 1024));

        Assert.AreEqual("fileName", error.ParamName);
    }

    [TestMethod]
    public void OpenRegularFileReadOnlyNoFollow_RequiresAnAbsoluteDirectoryAndPositiveLimit()
    {
        var relative = Assert.ThrowsExactly<ArgumentException>(
            () => SecureFileSystem.OpenRegularFileReadOnlyNoFollow("relative", "state.json", maxBytes: 1));
        var zero = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SecureFileSystem.OpenRegularFileReadOnlyNoFollow(_temp, "state.json", maxBytes: 0));

        Assert.AreEqual("directoryPath", relative.ParamName);
        Assert.AreEqual("maxBytes", zero.ParamName);
    }

    [TestMethod]
    public void OpenRegularFileReadOnlyNoFollow_ClassifiesLinuxNonDirectorySocketAndIoErrors()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Requires Linux openat errno values.");

        var file = Path.Combine(_temp, "not-a-directory");
        File.WriteAllBytes(file, [1]);
        using var nonDirectory = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(file, "state.json", maxBytes: 1024);
        Assert.AreEqual(RegularFileOpenStatus.DirectoryNotDirectory, nonDirectory.Status);

        var socketPath = Path.Combine(_temp, "state.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        Assert.AreEqual(PathEntryKind.Other, SecureFileSystem.InspectPathNoFollow(socketPath));
        using var nonRegular = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(_temp, "state.sock", maxBytes: 1024);
        Assert.AreEqual(RegularFileOpenStatus.FileNonRegular, nonRegular.Status);

        var longName = new string('x', 4096);
        Assert.AreEqual(PathEntryKind.IoError, SecureFileSystem.InspectPathNoFollow(Path.Combine(_temp, longName)));
        using var badFile = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(_temp, longName, maxBytes: 1024);
        Assert.AreEqual(RegularFileOpenStatus.IoError, badFile.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => badFile.DetachHandle());
    }

    [TestMethod]
    public void OpenRegularFileReadOnlyNoFollow_LinuxTraverseOnlyParentStillRequiresSearchPermission()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            Assert.Inconclusive("Requires Linux without root privileges.");
            return;
        }

        var parent = Path.Combine(_temp, "search-only");
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(Path.Combine(parent, "state.json"), [1]);
        try
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserExecute);
            using var opened = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(parent, "state.json", maxBytes: 1);
            Assert.AreEqual(RegularFileOpenStatus.Opened, opened.Status);
            Assert.AreEqual(1, opened.SizeBytes);

            var createFault = Assert.ThrowsExactly<FileSystemFaultException>(() =>
                SecureFileSystem.EnsureAndHardenDirectoryChain(parent, ["new"], UnixFileMode.UserExecute));
            Assert.AreEqual(FileSystemFaultKind.PermissionDenied, createFault.Kind);
            Assert.IsFalse(Directory.Exists(Path.Combine(parent, "new")));

            File.SetUnixFileMode(parent, UnixFileMode.None);
            using var denied = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(parent, "state.json", maxBytes: 1);
            Assert.AreEqual(RegularFileOpenStatus.DirectoryPermissionDenied, denied.Status);
            Assert.ThrowsExactly<InvalidOperationException>(() => denied.DetachHandle());
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryUnblockFifoReader(string fifoPath)
    {
        try
        {
            using var _ = File.Open(fifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
