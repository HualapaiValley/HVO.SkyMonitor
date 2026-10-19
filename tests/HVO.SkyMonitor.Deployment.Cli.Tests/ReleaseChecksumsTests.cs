using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// SHA256SUMS is read exactly as <c>sha256sum</c> writes it and a release publishes it. Anything else is refused rather
/// than read around, because a list that verifies a file it does not quite name verifies nothing.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseChecksumsTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);

    [TestMethod]
    public void Parse_SumsAsSha256sumWritesThem_ReadsEachFile()
    {
        var sums = ReleaseChecksums.Parse(Encoding.ASCII.GetBytes($"{A}  hvo-sky-install-linux-arm64\n{B}  release.json\n"));

        Assert.HasCount(2, sums.Files);
        Assert.AreEqual(A, sums.Files["hvo-sky-install-linux-arm64"]);
        Assert.AreEqual(B, sums.Files["release.json"]);
    }

    [TestMethod]
    [DataRow("", "it does not end in a line feed")]
    [DataRow("{A}  release.json", "it does not end in a line feed")]
    [DataRow("{A}  release.json\r\n", "it is not lines of printable ASCII text")]
    [DataRow("{A}\trelease.json\n", "it is not lines of printable ASCII text")]
    [DataRow("{A}  release.json\n\n", "line 2 is not a SHA-256 and a release file name")]
    [DataRow("{A} *release.json\n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{A} release.json\n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{UPPER}  release.json\n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{SHORT}  release.json\n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{A}  ../release.json\n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{A}  dir/release.json\n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{A}  release.json \n", "line 1 is not a SHA-256 and a release file name")]
    [DataRow("{A}  release.json\n{B}  release.json\n", "it lists release.json more than once")]
    public void Parse_AnythingButWhatSha256sumWrites_IsRefused(string template, string reason)
    {
        ArgumentNullException.ThrowIfNull(template);
        var text = template
            .Replace("{A}", A, StringComparison.Ordinal)
            .Replace("{B}", B, StringComparison.Ordinal)
            .Replace("{UPPER}", new string('A', 64), StringComparison.Ordinal)
            .Replace("{SHORT}", new string('a', 63), StringComparison.Ordinal);

        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseChecksums.Parse(Encoding.ASCII.GetBytes(text)));

        Assert.AreEqual($"The installer will not use SHA256SUMS: {reason}.", exception.Message);
    }

    [TestMethod]
    public void Parse_NonAsciiName_IsRefusedWithoutEchoingIt()
    {
        var bytes = Encoding.UTF8.GetBytes($"{A}  release‮json\n");

        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseChecksums.Parse(bytes));

        Assert.AreEqual("The installer will not use SHA256SUMS: it is not lines of printable ASCII text.", exception.Message);
    }

    [TestMethod]
    public void Parse_LargerThanTheBound_IsRefused()
    {
        var exception = Assert.ThrowsExactly<InstallerException>(
            () => ReleaseChecksums.Parse(new byte[ReleaseChecksums.MaximumLength + 1]));

        Assert.AreEqual(
            $"The installer will not use SHA256SUMS: it is larger than {ReleaseChecksums.MaximumLength} bytes.", exception.Message);
    }
}
