using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DurableSyncTests
{
    private string _temp = null!;
    private PhysicalRoot _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _root = PhysicalRoot.Open(_temp);
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
    public void SupportsDirectorySync_IsTrueExactlyOnLinux()
    {
        Assert.AreEqual(OperatingSystem.IsLinux(), DurableSync.SupportsDirectorySync);
    }

    [TestMethod]
    public void Directory_ReportsUnsupportedKernelSyncInsteadOfClaimingDurability()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Requires Linux procfs.");
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.Directory("/proc"));
        Assert.AreEqual("sync-directory", fault.Operation);
        Assert.AreEqual(FileSystemFaultKind.Io, fault.Kind);
        var original = Assert.IsInstanceOfType<IOException>(fault.InnerException);
        Assert.AreEqual(22, original.HResult, "Preserve the actual EINVAL from fsync(procfs).");
    }

    [TestMethod]
    public void File_FlushesAnExistingFileByPathHandleAndStream()
    {
        var path = Path.Combine(_temp, "data.bin");
        File.WriteAllBytes(path, new byte[64 * 1024]);
        DurableSync.File(path);
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DurableSync.File(handle, path);
        }
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write([1, 2, 3]);

        DurableSync.File(stream);

        Assert.AreEqual(64 * 1024 + 3, new FileInfo(path).Length, "the stream's buffered bytes reached the file");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void File_ReportsPermissionDeniedForAnUnreadableFile()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs a non-root Linux user so that mode 000 is enforced.");
        }
        var path = Path.Combine(_temp, "sealed.bin");
        File.WriteAllBytes(path, [1]);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(path));
            Assert.AreEqual(FileSystemFaultKind.PermissionDenied, fault.Kind);
            Assert.IsInstanceOfType<System.ComponentModel.Win32Exception>(fault.InnerException, "the errno is preserved as the inner fact");
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [TestMethod]
    public void File_ReportsNotFoundForAMissingPath()
    {
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(Path.Combine(_temp, "absent.bin")));
        Assert.AreEqual(FileSystemFaultKind.NotFound, fault.Kind);
        Assert.AreEqual("sync-file", fault.Operation);
    }

    [TestMethod]
    public void File_MapsAnUnclassifiedLinuxOpenErrorToIo()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The errno mapping is Linux-only.");
        }
        var path = Path.Combine(_temp, new string('x', 300));

        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(path));

        Assert.AreEqual(FileSystemFaultKind.Io, fault.Kind);
        Assert.AreEqual("sync-file", fault.Operation);
    }

    [TestMethod]
    public void RequireRegularFile_ReportsStatxFailureForAMissingPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The statx failure path is Linux-only.");
        }

        var fault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => DurableSync.RequireRegularFile(Path.Combine(_temp, "absent.bin")));

        Assert.AreEqual(FileSystemFaultKind.Io, fault.Kind);
        Assert.AreEqual("inspect-file", fault.Operation);
        Assert.IsInstanceOfType<System.ComponentModel.Win32Exception>(fault.InnerException);
    }

    [TestMethod]
    public void RequireRegularFile_AcceptsAFileAndRejectsADirectoryOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The statx type proof is Linux-only.");
        }
        var file = Path.Combine(_temp, "regular.bin");
        File.WriteAllBytes(file, [1]);

        DurableSync.RequireRegularFile(file);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.RequireRegularFile(_temp));

        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        Assert.AreEqual("inspect-file", fault.Operation);
    }

    [TestMethod]
    public void NonLinuxDispatch_UsesManagedFileChecksAndNoOpDirectorySync()
    {
        var file = Path.Combine(_temp, "managed.bin");
        File.WriteAllBytes(file, [1]);

        DurableSync.File(file, isLinux: false);
        DurableSync.RequireRegularFile(file, isLinux: false);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => DurableSync.RequireRegularFile(_temp, isLinux: false));
        DurableSync.Directory(_temp, isLinux: false);
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read);
        DurableSync.Directory(handle, _temp, isLinux: false);

        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        Assert.AreEqual(0, DurableSync.DirectoryChain(_root, _temp, isLinux: false));
    }

    [TestMethod]
    public void LinuxSync_RejectsAnInvalidHandleBeforeCallingFsync()
    {
        // The guard precedes any libc call, so the Linux dispatch is exercised on every platform.
        using var invalid = new Microsoft.Win32.SafeHandles.SafeFileHandle(-1, ownsHandle: false);

        var fault = Assert.ThrowsExactly<ArgumentException>(() => DurableSync.Directory(invalid, _temp, isLinux: true));

        Assert.AreEqual("handle", fault.ParamName);
    }

    [TestMethod]
    public void File_RefusesToFollowASymbolicLinkOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("O_NOFOLLOW is the Linux mechanism; elsewhere containment is the only guard.");
        }
        var real = Path.Combine(_temp, "real.bin");
        File.WriteAllBytes(real, [1]);
        var link = Path.Combine(_temp, "link.bin");
        File.CreateSymbolicLink(link, real);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(link));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public void Directory_FlushesARealDirectoryAndRefusesALinkedOne()
    {
        var dir = Path.Combine(_temp, "d");
        Directory.CreateDirectory(dir);
        DurableSync.Directory(dir); // no-op off Linux, real fsync on it; must not throw either way

        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var link = Path.Combine(_temp, "dlink");
        Directory.CreateSymbolicLink(link, dir);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.Directory(link));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);

        var notADirectory = Path.Combine(_temp, "file.bin");
        File.WriteAllBytes(notADirectory, [1]);
        var notDir = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.Directory(notADirectory));
        Assert.AreEqual(FileSystemFaultKind.Containment, notDir.Kind);
    }

    [TestMethod]
    public void DirectoryChain_FlushesEveryLevelUpToTheRootAndStopsThere()
    {
        var deep = Path.Combine(_temp, "a", "b", "c");
        Directory.CreateDirectory(deep);
        var count = DurableSync.DirectoryChain(_root, deep);
        Assert.AreEqual(OperatingSystem.IsLinux() ? 4 : 0, count, "c, b, a, root");
    }

    [TestMethod]
    public void DirectoryChain_RefusesAPathOutsideTheRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Directory sync is a no-op off Linux, so the chain walk does not run.");
        }
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fs-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.DirectoryChain(_root, outside));
            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
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

        DurableSync.SetDirectoryModeNoFollow(realDir, restricted);
        Assert.AreEqual(restricted, File.GetUnixFileMode(realDir));

        var symlinkTarget = Path.Combine(_temp, "symlink-target-dir");
        Directory.CreateDirectory(symlinkTarget);
        File.SetUnixFileMode(symlinkTarget, permissive);
        var symlinkDir = Path.Combine(_temp, "symlink-dir");
        Directory.CreateSymbolicLink(symlinkDir, symlinkTarget);

        var symlinkFault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => DurableSync.SetDirectoryModeNoFollow(symlinkDir, restricted));
        Assert.AreEqual(FileSystemFaultKind.Containment, symlinkFault.Kind);
        Assert.AreEqual(permissive, File.GetUnixFileMode(symlinkTarget),
            "Descriptor-based fchmod with O_NOFOLLOW must never modify the symlink target's permissions.");

        var regularFile = Path.Combine(_temp, "regular.txt");
        File.WriteAllText(regularFile, "data");
        File.SetUnixFileMode(regularFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var fileFault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => DurableSync.SetDirectoryModeNoFollow(regularFile, restricted));
        Assert.AreEqual(FileSystemFaultKind.Containment, fileFault.Kind);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(regularFile));
    }

    [TestMethod]
    public void SetFileMode_AppliesModeViaOpenHandle()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes are not applicable on Windows.");
            return;
        }

        var path = Path.Combine(_temp, "mode-file.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DurableSync.SetFileMode(handle, UnixFileMode.UserRead | UnixFileMode.UserWrite, path);
        }

        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
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

        DurableSync.EnsureAndHardenDirectoryChain(
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

        DurableSync.EnsureAndHardenDirectoryChain(
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
            DurableSync.EnsureAndHardenDirectoryChain(
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
            DurableSync.EnsureAndHardenDirectoryChain(
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
            DurableSync.EnsureAndHardenDirectoryChain(
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
            DurableSync.EnsureAndHardenDirectoryChain(
                symlinkAnchor,
                ["child"],
                restricted,
                createMissing: true,
                allowSymlinkedAnchor: false));
        Assert.AreEqual(FileSystemFaultKind.Containment, anchorFault.Kind);

        DurableSync.EnsureAndHardenDirectoryChain(
            symlinkAnchor,
            ["child"],
            restricted,
            createMissing: true,
            allowSymlinkedAnchor: true);
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(realAnchorTarget, "child")));

        // 7. Reject path-traversal or multi-segment components.
        Assert.ThrowsExactly<ArgumentException>(() =>
            DurableSync.EnsureAndHardenDirectoryChain(anchor, [".."], restricted));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DurableSync.EnsureAndHardenDirectoryChain(anchor, ["."], restricted));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DurableSync.EnsureAndHardenDirectoryChain(anchor, ["a/b"], restricted));

        // 8. Traverse-only pre-existing parents (0300 and 0100) still pin via O_PATH on Linux and harden descendants.
        if (OperatingSystem.IsLinux() && Environment.UserName != "root")
        {
            var mode0300 = Path.Combine(_temp, "anchor-0300");
            Directory.CreateDirectory(mode0300);
            File.SetUnixFileMode(mode0300, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                DurableSync.EnsureAndHardenDirectoryChain(
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
                DurableSync.EnsureAndHardenDirectoryChain(
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

        Assert.AreEqual(PathEntryKind.Directory, DurableSync.InspectPathNoFollow(realDir));
        Assert.AreEqual(PathEntryKind.RegularFile, DurableSync.InspectPathNoFollow(realFile));
        Assert.AreEqual(PathEntryKind.NotFound, DurableSync.InspectPathNoFollow(missingPath));
        Assert.AreEqual(PathEntryKind.NotDirectory, DurableSync.InspectPathNoFollow(Path.Combine(realFile, "child")));

        if (!OperatingSystem.IsWindows())
        {
            var linkToDir = Path.Combine(_temp, "link-to-dir");
            var linkToFile = Path.Combine(_temp, "link-to-file");
            var danglingLink = Path.Combine(_temp, "dangling-link");
            Directory.CreateSymbolicLink(linkToDir, realDir);
            File.CreateSymbolicLink(linkToFile, realFile);
            File.CreateSymbolicLink(danglingLink, Path.Combine(_temp, "missing-target"));

            Assert.AreEqual(PathEntryKind.SymbolicLink, DurableSync.InspectPathNoFollow(linkToDir));
            Assert.AreEqual(PathEntryKind.Directory, DurableSync.InspectPathFollow(linkToDir));

            Assert.AreEqual(PathEntryKind.SymbolicLink, DurableSync.InspectPathNoFollow(linkToFile));
            Assert.AreEqual(PathEntryKind.RegularFile, DurableSync.InspectPathFollow(linkToFile));

            // Non-following inspection distinguishes a dangling symlink (SymbolicLink) from genuine absence (NotFound).
            Assert.AreEqual(PathEntryKind.SymbolicLink, DurableSync.InspectPathNoFollow(danglingLink));
            Assert.AreEqual(PathEntryKind.NotFound, DurableSync.InspectPathFollow(danglingLink));

            if (Environment.UserName != "root")
            {
                var lockedDir = Path.Combine(_temp, "inspect-locked");
                Directory.CreateDirectory(lockedDir);
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600 (not traversable)
                try
                {
                    Assert.AreEqual(PathEntryKind.PermissionDenied, DurableSync.InspectPathNoFollow(Path.Combine(lockedDir, "child")));
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
        DurableSync.VerifyDirectoryNoFollow(realDir);

        var regularFile = Path.Combine(_temp, "verify-file.txt");
        File.WriteAllText(regularFile, "data");
        var fileFault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.VerifyDirectoryNoFollow(regularFile));
        Assert.AreEqual(FileSystemFaultKind.Containment, fileFault.Kind);

        var missing = Path.Combine(_temp, "verify-missing");
        var missingFault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.VerifyDirectoryNoFollow(missing));
        Assert.AreEqual(FileSystemFaultKind.NotFound, missingFault.Kind);

        if (!OperatingSystem.IsWindows())
        {
            var linkDir = Path.Combine(_temp, "verify-link-dir");
            Directory.CreateSymbolicLink(linkDir, realDir);
            var linkFault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.VerifyDirectoryNoFollow(linkDir));
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
        using (var opened = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, validName, maxBytes: 1024))
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
        using (var missing = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, "absent.json", maxBytes: 1024))
        {
            Assert.AreEqual(RegularFileOpenStatus.NotFound, missing.Status);
        }

        // 3. Oversized regular file is rejected by fstat(fd) BEFORE returning an open handle or reading payload bytes.
        var oversizedName = "oversized.json";
        File.WriteAllBytes(Path.Combine(dir, oversizedName), new byte[2049]);
        using (var oversized = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, oversizedName, maxBytes: 2048))
        {
            Assert.AreEqual(RegularFileOpenStatus.FileOversized, oversized.Status);
            Assert.AreEqual(2049, oversized.SizeBytes);
            Assert.ThrowsExactly<InvalidOperationException>(() => oversized.DetachHandle());
        }

        // 4. Directory at file name is rejected as FileIsDirectory.
        var subDirName = "subdir";
        Directory.CreateDirectory(Path.Combine(dir, subDirName));
        using (var subDir = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, subDirName, maxBytes: 1024))
        {
            Assert.AreEqual(RegularFileOpenStatus.FileIsDirectory, subDir.Status);
        }

        if (!OperatingSystem.IsWindows())
        {
            // 5. Symbolic link file is rejected via O_NOFOLLOW (ELOOP).
            var linkFileName = "link.json";
            File.CreateSymbolicLink(Path.Combine(dir, linkFileName), Path.Combine(dir, validName));
            using (var symlink = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, linkFileName, maxBytes: 1024))
            {
                Assert.AreEqual(RegularFileOpenStatus.FileSymbolicLink, symlink.Status);
            }

            // 6. Symbolic link directory is rejected via O_DIRECTORY | O_NOFOLLOW.
            var symlinkDir = Path.Combine(_temp, "openat-dir-link");
            Directory.CreateSymbolicLink(symlinkDir, dir);
            using (var dirLink = DurableSync.OpenRegularFileReadOnlyNoFollow(symlinkDir, validName, maxBytes: 1024))
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var fifo = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, fifoName, maxBytes: 1024))
            {
                sw.Stop();
                Assert.AreEqual(RegularFileOpenStatus.FileNonRegular, fifo.Status);
                Assert.IsLessThan(TimeSpan.FromSeconds(5), sw.Elapsed, "Opening a FIFO with O_NONBLOCK must never block.");
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
                    using var bigRes = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, "write-only-big.json", maxBytes: 2048);
                    Assert.AreEqual(RegularFileOpenStatus.FileOversized, bigRes.Status);
                    Assert.AreEqual(2049, bigRes.SizeBytes);

                    using var smallRes = DurableSync.OpenRegularFileReadOnlyNoFollow(dir, "unreadable-small.json", maxBytes: 2048);
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
}
