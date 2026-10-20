using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The installer names itself, in its usage and in the commands its messages tell the operator to run, as the program
/// that is running (#1384). A release's installer is <c>hvo-sky-install-linux-&lt;arch&gt;</c> in the release folder and
/// nothing puts it on PATH, so the bare name <c>hvo-sky-install</c> is a command that is not there.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InstallerProgramTests
{
    [TestMethod]
    [DataRow("/home/pi/hvo-sky-v0.9.0/hvo-sky-install-linux-arm64", "/home/pi/hvo-sky-v0.9.0/hvo-sky-install-linux-arm64")]
    [DataRow("/usr/local/bin/hvo-sky-install", "/usr/local/bin/hvo-sky-install")]
    [DataRow("/home/pi/sky releases/hvo-sky-install-linux-x64", "'/home/pi/sky releases/hvo-sky-install-linux-x64'")]
    [DataRow("/home/pi/it's here/hvo-sky-install-linux-arm64", "'/home/pi/it'\\''s here/hvo-sky-install-linux-arm64'")]
    public void Of_TheInstaller_IsItsPathAsTheShellTakesIt(string processPath, string expected)
        => Assert.AreEqual(expected, InstallerProgram.Of(processPath));

    /// <summary>A process that is not the installer itself, such as <c>dotnet</c> running it or a test host, says the product's name.</summary>
    [TestMethod]
    [DataRow("/usr/share/dotnet/dotnet")]
    [DataRow("/home/pi/src/bin/Debug/net10.0/HVO.SkyMonitor.Deployment.Cli")]
    [DataRow("")]
    [DataRow(null)]
    public void Of_AnotherProgram_IsTheProductName(string? processPath)
        => Assert.AreEqual("hvo-sky-install", InstallerProgram.Of(processPath));

    [TestMethod]
    public void Usage_NamesTheRunningInstaller()
    {
        StringAssert.StartsWith(CommandLine.GeneralUsage(), $"Usage: {InstallerProgram.Current} <command> [options]", StringComparison.Ordinal);
        StringAssert.Contains(
            CommandLine.GeneralUsage(), $"Run '{InstallerProgram.Current} <command> --help'", StringComparison.Ordinal);
    }
}
