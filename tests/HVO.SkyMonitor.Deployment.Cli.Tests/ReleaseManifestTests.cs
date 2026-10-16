using System.Text;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The release.json reader an upgrade uses before it changes anything: a complete schema-1 document in the contract's
/// shape reads unchanged, and anything that is not a release version with well-formed upgrade notes is refused with the
/// reason.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseManifestTests
{
    [TestMethod]
    public void Parse_CompleteSchema1Release_ReadsItsVersionAndNotesAndLeavesTheRestToAcquisition()
    {
        var manifest = ReleaseManifest.Parse(Fixture());

        Assert.AreEqual("1.2.0", manifest.Version);
        Assert.AreEqual("1.0.0 1.1.0 1.2.0", UpgradeNotesTests.Versions(manifest.UpgradeNotes));
        Assert.AreEqual(
            "Read the Camera & rig page after upgrading.\n\nThe state contract is unchanged, so rollback is a swap.",
            manifest.UpgradeNotes[2].Text);
    }

    [TestMethod]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0"}""")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":null}""")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[]}""")]
    public void Parse_ReleaseWithoutUpgradeNotes_ReadsNone(string json)
    {
        var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json));

        Assert.AreEqual("1.0.0", manifest.Version);
        Assert.AreEqual(0, manifest.UpgradeNotes.Count);
    }

    [TestMethod]
    [DataRow("1.3.0-dev.4")]
    [DataRow("1.3.0-rc.1")]
    [DataRow("1.3.0+0123456")]
    public void Parse_PreReleaseAndBuildVersions_AreReleaseVersions(string version)
    {
        var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"version":"{{version}}"}"""));

        Assert.AreEqual(version, manifest.Version);
    }

    [TestMethod]
    [DataRow("""[]""", "it is not a JSON object")]
    [DataRow("""{"version":"1.0.0"}""", "it has no schemaVersion")]
    [DataRow("""{"schemaVersion":"1","version":"1.0.0"}""", "it has no schemaVersion")]
    [DataRow("""{"schemaVersion":2,"version":"1.0.0"}""", "it is schema 2, and this installer reads schema 1")]
    [DataRow("""{"schemaVersion":1.5,"version":"1.0.0"}""", "it is schema 1.5, and this installer reads schema 1")]
    [DataRow("""{"schemaVersion":1}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"v1.0.0"}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"1.0"}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":1}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":{}}""", "its upgrade notes are not a list")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":["text"]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0"}]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"  "}]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0","text":"x"}]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.1.0","upgradeNotes":[{"version":"1.0.0","text":"x"},{"version":"1.1.0","text":"\u001b[2J"}]}""", "upgrade note 2 (1.1.0) contains control characters")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"DO NOT UPGRADE\rSafe to proceed."}]}""", "upgrade note 1 (1.0.0) contains control characters")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"One.\n\rTwo."}]}""", "upgrade note 1 (1.0.0) contains control characters")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"One.\r"}]}""", "upgrade note 1 (1.0.0) contains control characters")]
    [DataRow("""{"schemaVersion":1,"version":"1.1.0","upgradeNotes":[{"version":"1.1.0","text":"x"},{"version":"1.1.0+build","text":"y"}]}""", "it has more than one upgrade note for 1.1.0+build")]
    public void Parse_MalformedRelease_IsRefusedWithTheReason(string json, string reason)
    {
        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.AreEqual($"The installer will not use release.json: {reason}.", exception.Message);
    }

    [TestMethod]
    public void Parse_CrlfLineEndings_AreReadAsLineFeeds()
    {
        var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"One.\r\n\r\nTwo.\r\n\tThree."}]}"""));

        Assert.AreEqual("One.\n\nTwo.\n\tThree.", manifest.UpgradeNotes.Single().Text);
    }

    [TestMethod]
    public void Parse_InvalidJson_IsRefused()
    {
        var exception = Assert.ThrowsExactly<InstallerException>(
            () => ReleaseManifest.Parse(Encoding.UTF8.GetBytes("""{"schemaVersion":1,""")));

        Assert.AreEqual("The installer cannot read release.json: it is not valid JSON.", exception.Message);
    }

    [TestMethod]
    public void Parse_DocumentLargerThanTheBound_IsRefusedBeforeParsing()
    {
        var oversized = new byte[ReleaseManifest.MaximumLength + 1];

        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(oversized));

        Assert.AreEqual(
            $"The installer will not use release.json: it is larger than {ReleaseManifest.MaximumLength} bytes.",
            exception.Message);
    }

    internal static byte[] Fixture()
    {
        using var stream = typeof(ReleaseManifestTests).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Deployment.Cli.Tests.ReleaseManifestSchema1.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static ReleaseManifest Release(string version, params (string Version, string Text)[] notes)
        => new(version, [.. notes.Select(static note => new UpgradeNote(note.Version, note.Text))]);
}
