using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = "These tests exercise confined reads on the Linux filesystem.")]
public sealed class ConfinedReadDirectoryTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateTempSubdirectory("confined-read-");

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    [TestMethod]
    [SupportedOSPlatform("linux")]
    public void OpenRead_ReturnsTheContentsOfASingleLinkRegularFile()
    {
        var snapshot = _root.CreateSubdirectory("read-only-snapshot");
        var path = Path.Combine(snapshot.FullName, "regular");
        File.WriteAllText(path, "confined contents");
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        File.SetUnixFileMode(snapshot.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            using var root = new ConfinedReadDirectory(snapshot.FullName);
            using var reader = new StreamReader(root.OpenRead("regular"));

            Assert.AreEqual("confined contents", reader.ReadToEnd());
        }
        finally
        {
            File.SetUnixFileMode(snapshot.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "same-device bind mount has a different mount ID")]
    [DataRow(false, DisplayName = "entry device differs from the pinned device")]
    public void OpenedRegularFileMustMatchBothPinnedDeviceAndMount(bool differentMount)
    {
        const string name = "regular";
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, "confined contents");
        using var directory = new ConfinedReadDirectory(_root.FullName);
        using var file = File.OpenHandle(path);
        var location = directory.ReadLinuxLocation();

        Assert.IsTrue(ConfinedReadDirectory.IsSingleLinkRegularLinuxFile(file,
            (location.DeviceMajor, location.DeviceMinor, location.MountId), name));

        var otherMount = differentMount
            ? (location.DeviceMajor, location.DeviceMinor, location.MountId ^ 1UL)
            : (location.DeviceMajor, location.DeviceMinor ^ 1U, location.MountId);
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() =>
            ConfinedReadDirectory.IsSingleLinkRegularLinuxFile(file, otherMount, name)).Message, "different mount");
    }

    [TestMethod]
    public void Constructor_RejectsASymlinkInAnAncestor()
    {
        var physical = _root.CreateSubdirectory("physical");
        physical.CreateSubdirectory("child");
        var link = Path.Combine(_root.FullName, "link");
        Directory.CreateSymbolicLink(link, physical.FullName);

        Assert.ThrowsExactly<IOException>(() => new ConfinedReadDirectory(Path.Combine(link, "child")));
    }

    [TestMethod]
    public void LinuxLocationIdentifiesThePinnedDirectoryAndMeasuresItsFilesystem()
    {
        var child = _root.CreateSubdirectory("child space");
        using var parent = new ConfinedReadDirectory(_root.FullName);
        using var directory = new ConfinedReadDirectory(child.FullName);
        var parentLocation = parent.ReadLinuxLocation();
        var childLocation = directory.ReadLinuxLocation();

        Assert.AreEqual(parentLocation.MountId, childLocation.MountId);
        Assert.AreEqual(Path.Combine(parentLocation.FileSystemPath, child.Name), childLocation.FileSystemPath);
        Assert.AreNotEqual(parentLocation.Inode, childLocation.Inode);
        Assert.IsGreaterThan(0L, directory.ReadLinuxAvailableBytes());
    }

    [TestMethod]
    public void OpenRead_RejectsALeafSymlink()
    {
        var regular = Path.Combine(_root.FullName, "regular");
        File.WriteAllText(regular, "confined contents");
        File.CreateSymbolicLink(Path.Combine(_root.FullName, "link"), regular);

        using var root = new ConfinedReadDirectory(_root.FullName);

        Assert.ThrowsExactly<IOException>(() => root.OpenRead("link"));
    }

    [TestMethod]
    public async Task OpenRead_RejectsBothNamesOfAHardLinkedFile()
    {
        var regular = Path.Combine(_root.FullName, "regular");
        await File.WriteAllTextAsync(regular, "confined contents");
        await RunUtilityAsync("ln", regular, Path.Combine(_root.FullName, "hard-link"));

        using var root = new ConfinedReadDirectory(_root.FullName);

        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => root.OpenRead("regular")).Message, "one hard link");
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => root.OpenRead("hard-link")).Message, "one hard link");
    }

    [TestMethod]
    public async Task OpenRead_RejectsAFifoWithoutWaitingForAWriter()
    {
        var fifo = Path.Combine(_root.FullName, "pipe");
        await RunUtilityAsync("mkfifo", fifo);
        using var root = new ConfinedReadDirectory(_root.FullName);

        var read = Task.Run(() => Assert.ThrowsExactly<IOException>(() => root.OpenRead("pipe")));
        if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))) != read)
        {
            // Linux can open both ends at once to release a blocked reader after a regression.
            using var releaseReader = new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite);
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("OpenRead waited for a writer on the FIFO.");
        }
        StringAssert.Contains((await read).Message, "must be a regular file");
    }

    [TestMethod]
    public void OpenRead_RejectsASubdirectory()
    {
        _root.CreateSubdirectory("subdir");
        using var root = new ConfinedReadDirectory(_root.FullName);

        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => root.OpenRead("subdir")).Message, "must be a regular file");
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("/")]
    [DataRow("")]
    [DataRow("   ")]
    public void Constructor_RejectsInvalidPaths(string path)
    {
        Assert.AreEqual("path", Assert.ThrowsExactly<ArgumentException>(() => new ConfinedReadDirectory(path)).ParamName);
    }

    [TestMethod]
    [DataRow("./child")]
    [DataRow("child/../child")]
    public void Constructor_RejectsNoncanonicalAbsolutePaths(string relative)
    {
        _root.CreateSubdirectory("child");
        var path = Path.Combine(_root.FullName, relative);

        Assert.AreEqual("path", Assert.ThrowsExactly<ArgumentException>(() => new ConfinedReadDirectory(path)).ParamName);
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("a\0b")]
    [DataRow("")]
    [DataRow(" \t ")]
    public void OpenRead_RejectsInvalidNames(string name)
    {
        using var root = new ConfinedReadDirectory(_root.FullName);

        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => root.OpenRead(name)).ParamName);
    }

    [TestMethod]
    public void OpenRead_RejectsAMissingFile()
    {
        using var root = new ConfinedReadDirectory(_root.FullName);

        Assert.ThrowsExactly<IOException>(() => root.OpenRead("missing"));
    }

    private static async Task RunUtilityAsync(string command, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, $"{command} could not create the test fixture.");
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class ConfinedReadDarwinAbiTests
{
    [TestMethod]
    [DataRow(Architecture.X64, true)]
    [DataRow(Architecture.Arm64, false)]
    public void SupportedArchitecturesSelectTheMatchingDarwinSymbol(Architecture architecture, bool inode64)
    {
        Assert.AreEqual(inode64, ConfinedReadDirectory.UsesDarwinInode64Symbol(architecture));
        Assert.AreEqual(144, Marshal.SizeOf<ConfinedReadDirectory.DarwinStat>());
        Assert.AreEqual((nint)4, Marshal.OffsetOf<ConfinedReadDirectory.DarwinStat>(nameof(ConfinedReadDirectory.DarwinStat.Mode)));
        Assert.AreEqual((nint)6, Marshal.OffsetOf<ConfinedReadDirectory.DarwinStat>(nameof(ConfinedReadDirectory.DarwinStat.LinkCount)));
    }

    [TestMethod]
    public void UnsupportedDarwinArchitecturesAreRefused() =>
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => ConfinedReadDirectory.UsesDarwinInode64Symbol(Architecture.X86));
}
