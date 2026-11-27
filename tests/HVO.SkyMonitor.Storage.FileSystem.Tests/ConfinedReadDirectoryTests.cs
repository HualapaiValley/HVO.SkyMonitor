using System.Diagnostics;

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
    public void OpenRead_ReturnsTheContentsOfASingleLinkRegularFile()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "regular"), "confined contents");

        using var root = new ConfinedReadDirectory(_root.FullName);
        using var reader = new StreamReader(root.OpenRead("regular"));

        Assert.AreEqual("confined contents", reader.ReadToEnd());
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
