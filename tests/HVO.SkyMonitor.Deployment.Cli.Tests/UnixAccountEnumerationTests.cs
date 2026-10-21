using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class UnixAccountEnumerationTests
{
    [TestMethod]
    [DataRow("sss")]
    [DataRow("systemd")]
    public void SuccessfulPartialEnumeration_DoesNotProvePrivateGroup(string unverifiedProvider)
    {
        var configuration = $"passwd: files {unverifiedProvider}\ngroup: files {unverifiedProvider}";
        var groupResult = new ProcessResult(0, "installer:x:1000:", "");
        var passwdResult = new ProcessResult(0, "installer:x:1000:1000::/:/bin/sh", "");
        Assert.AreEqual(0, groupResult.ExitCode);
        Assert.AreEqual(0, passwdResult.ExitCode);
        Assert.AreEqual("", groupResult.StandardError + passwdResult.StandardError);
        var sourceReads = 0;
        var database = UnixAccountDatabase.FromFileSources(() => configuration.Split('\n'),
            () => { sourceReads++; return groupResult.StandardOutput.Split('\n'); },
            () => { sourceReads++; return passwdResult.StandardOutput.Split('\n'); });

        Assert.IsFalse(database.IsPrivateGroup(1000, 1000, 1000),
            $"{configuration}: successful visible local rows do not exclude an omitted UID 2000 with primary gid 1000; provider enumeration is unverified (SSSD disables enumeration by default).");
        Assert.AreEqual(0, sourceReads, "Unverified providers must be refused before trusting visible rows.");
    }

    [TestMethod]
    [DataRow("passwd: files\ngroup: files", true)]
    [DataRow("# local accounts\n passwd : files # comment\ngroup:\tfiles\ninitgroups: files\nhosts: files [NOTFOUND=return] dns", true)]
    [DataRow("passwd: files systemd\ngroup: files systemd", false)]
    [DataRow("passwd: files sss\ngroup: files sss", false)]
    [DataRow("passwd: files\ngroup: files sss", false)]
    [DataRow("passwd: files\ngroup: files\ninitgroups: sss", false)]
    [DataRow("passwd: compat\ngroup: files", false)]
    [DataRow("passwd: files\ngroup: unknown", false)]
    [DataRow("passwd: files", false)]
    [DataRow("group: files", false)]
    [DataRow("passwd: files\npasswd: files\ngroup: files", false)]
    [DataRow("passwd: files\ngroup: files\ngroup: files", false)]
    [DataRow("passwd: files\ngroup: files\ninitgroups: files\ninitgroups: files", false)]
    [DataRow("passwd: files files\ngroup: files", false)]
    [DataRow("passwd: files [SUCCESS=return]\ngroup: files", false)]
    [DataRow("passwd: files\ngroup:", false)]
    [DataRow("passwd: files\ngroup: files\\\n sss", false)]
    [DataRow("passwd files\ngroup: files", false)]
    [DataRow("passwd: FILES\ngroup: files", false)]
    [DataRow("passwd: files\ngroup: files\npasswd sss: files", false)]
    [DataRow("passwd: files\ngroup: files\ngroup sss: files", false)]
    [DataRow("passwd: files\ngroup: files\ninitgroups sss: files", false)]
    public void PrivateGroup_RequiresVerifiedCompleteFileConfiguration(string configuration, bool expected)
    {
        var database = UnixAccountDatabase.FromFileSources(() => configuration.Split('\n'),
            () => ["installer:x:1000:"], () => ["installer:x:1000:1000::/:/bin/sh"]);
        Assert.AreEqual(expected, database.IsPrivateGroup(1000, 1000, 1000));
    }

    [TestMethod]
    public void PrivateGroup_UnverifiedCapability_IsRefusedBeforeReadingAccounts()
    {
        var sourceReads = 0;
        var database = new UnixAccountDatabase(
            () => { sourceReads++; return ["installer:x:1000:"]; },
            () => { sourceReads++; return ["installer:x:1000:1000::/:/bin/sh"]; }, () => false);
        Assert.IsFalse(database.IsPrivateGroup(1000, 1000, 1000));
        Assert.AreEqual(0, sourceReads);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PrivateGroup_UnreadableConfiguration_IsRefused(bool denied)
    {
        var database = UnixAccountDatabase.FromFileSources(
            () => denied ? throw new UnauthorizedAccessException() : throw new IOException(),
            () => ["installer:x:1000:"], () => ["installer:x:1000:1000::/:/bin/sh"]);
        Assert.IsFalse(database.IsPrivateGroup(1000, 1000, 1000));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PrivateGroup_PartialFileRead_IsRefused(bool groupFailure)
    {
        var database = UnixAccountDatabase.FromFileSources(() => ["passwd: files", "group: files"],
            () => groupFailure ? PartialRead("installer:x:1000:") : ["installer:x:1000:"],
            () => groupFailure ? ["installer:x:1000:1000::/:/bin/sh"] : PartialRead("installer:x:1000:1000::/:/bin/sh"));
        Assert.IsFalse(database.IsPrivateGroup(1000, 1000, 1000));
    }

    [TestMethod]
    public void PrivateGroup_PartialConfigurationRead_IsRefused()
    {
        var database = UnixAccountDatabase.FromFileSources(
            () => PartialRead("passwd: files", "group: files"),
            () => ["installer:x:1000:"], () => ["installer:x:1000:1000::/:/bin/sh"]);
        Assert.IsFalse(database.IsPrivateGroup(1000, 1000, 1000));
    }

    [TestMethod]
    public void DirectoryWithoutGroupWrite_DoesNotRequireAccountEnumeration()
    {
        var capabilityReads = 0;
        var database = new UnixAccountDatabase(() => [], () => [], () => { capabilityReads++; return false; });
        Assert.IsTrue(PathPrograms.IsTrustedDirectoryIdentity(new(1000, 1000, (UnixFileMode)0x1ed), 1000, 1000, database));
        Assert.AreEqual(0, capabilityReads);
    }

    [TestMethod]
    [DataRow("utf8", true)]
    [DataRow("utf16", false)]
    [DataRow("invalid-utf8", false)]
    public void PrivateGroup_FileConfiguration_RequiresCompleteUtf8(string encoding, bool expected)
    {
        const string configuration = "passwd: files\ngroup: files\n";
        byte[] bytes = encoding switch
        {
            "utf16" => [0xff, 0xfe, .. Encoding.Unicode.GetBytes(configuration)],
            "invalid-utf8" => [.. Encoding.UTF8.GetBytes(configuration), 0xff],
            _ => Encoding.UTF8.GetBytes(configuration)
        };
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, bytes);
            var database = UnixAccountDatabase.FromFileSources(() => UnixAccountDatabase.ReadFile(file),
                () => ["installer:x:1000:"], () => ["installer:x:1000:1000::/:/bin/sh"]);
            Assert.AreEqual(expected, database.IsPrivateGroup(1000, 1000, 1000));
        }
        finally { File.Delete(file); }
    }

    private static IEnumerable<string> PartialRead(params string[] lines)
    {
        foreach (var line in lines) yield return line;
        throw new IOException("The source stopped after visible local rows.");
    }
}
