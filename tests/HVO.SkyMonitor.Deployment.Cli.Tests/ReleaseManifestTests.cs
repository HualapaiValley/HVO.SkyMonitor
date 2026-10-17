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
    [DataRow("""{"schemaVersion":1,"version":"1.0.0\n"}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":1}""", "it names no release version")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":{}}""", "its upgrade notes are not a list")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":["text"]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0"}]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"  "}]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0","text":"x"}]}""", "upgrade note 1 is not a release's version and its text")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0\n","text":"x"}]}""", "upgrade note 1 is not a release's version and its text")]
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
    [DataRow("""{"schemaVersion":1,"version":"\uDC00"}""")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","upgradeNotes":[{"version":"1.0.0","text":"Upgrade \uD800 now."}]}""")]
    [DataRow("""{"schemaVersion":1,"\uD800":true,"version":"1.0.0"}""")]
    [DataRow("""{"schemaVersion":1,"version":"1.0.0","images":[{"tag":"\uDBFFx"}]}""")]
    public void Parse_EscapedLoneSurrogate_IsRefusedWhereverItIs(string json)
    {
        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.AreEqual(
            "The installer will not use release.json: it contains a string that is not valid Unicode.", exception.Message);
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":1,\"version\":\"1.0.0\",\"upgradeNotes\":[{\"version\":\"1.0.0\",\"text\":\"Upgrade ", new byte[] { 0xC3, 0x28 }, " now.\"}]}")]
    [DataRow("{\"schemaVersion\":1,\"version\":\"1.0.0\",\"upgradeNotes\":[{\"version\":\"1.0.0\",\"text\":\"Upgrade ", new byte[] { 0xED, 0xA0, 0x80 }, " now.\"}]}")]
    [DataRow("{\"schemaVersion\":1,\"version\":\"1.0.0\",\"images\":{\"", new byte[] { 0xFF }, "\":[]}}")]
    public void Parse_InvalidUtf8_IsRefusedWhereverItIs(string before, byte[] invalid, string after)
    {
        byte[] json = [.. Encoding.UTF8.GetBytes(before), .. invalid, .. Encoding.UTF8.GetBytes(after)];

        var exception = Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(json));

        Assert.AreEqual("The installer will not use release.json: it is not UTF-8 text.", exception.Message);
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

    [TestMethod]
    public void Parse_AsManyNotesAsTheLimit_AreReadAndOneMoreIsRefused()
    {
        Assert.HasCount(
            ReleaseManifest.MaximumUpgradeNotes, ReleaseManifest.Parse(Notes(ReleaseManifest.MaximumUpgradeNotes)).UpgradeNotes);

        var exception = Assert.ThrowsExactly<InstallerException>(
            () => ReleaseManifest.Parse(Notes(ReleaseManifest.MaximumUpgradeNotes + 1)));

        Assert.AreEqual(
            $"The installer will not use release.json: it has more than {ReleaseManifest.MaximumUpgradeNotes} upgrade notes.",
            exception.Message);
    }

    [TestMethod]
    public async Task Parse_TheSizeLimitsWorthOfDistinctNotes_IsRefusedForItsCountBeforeAnyIsCompared()
    {
        // 25,000 distinct notes fit in the size limit. Checked against every earlier note they took about 312 million
        // comparisons, minutes of work.
        var json = Notes(25_000);
        Assert.IsLessThan(ReleaseManifest.MaximumLength, json.Length);

        var exception = await Task.Run(() => Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(json)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(
            $"The installer will not use release.json: it has more than {ReleaseManifest.MaximumUpgradeNotes} upgrade notes.",
            exception.Message);
    }

    [TestMethod]
    public async Task Parse_TheLargestValidRelease_IsReadInLinearTime()
    {
        // As many notes as the limit, each for a long pre-release that is costly to match, filling the size limit. Each
        // version is matched once and a repeated release is found in a set, so this reads in milliseconds; checking each
        // note against every earlier one matched half a million pairs and took tens of seconds.
        var prerelease = string.Join('.', Enumerable.Repeat("a", 450));
        var json = Notes(Enumerable.Range(0, ReleaseManifest.MaximumUpgradeNotes).Select(index => $"1.0.0-{prerelease}.{index}"));
        Assert.IsLessThan(ReleaseManifest.MaximumLength, json.Length);

        var manifest = await Task.Run(() => ReleaseManifest.Parse(json)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(ReleaseManifest.MaximumUpgradeNotes, manifest.UpgradeNotes);
    }

    internal static byte[] Fixture()
    {
        using var stream = typeof(ReleaseManifestTests).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Deployment.Cli.Tests.ReleaseManifestSchema1.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>A release.json of 1.0.0 with a one-word note for each of <paramref name="count"/> distinct releases.</summary>
    private static byte[] Notes(int count) => Notes(Enumerable.Range(0, count).Select(static index => $"1.{index}.0"));

    private static byte[] Notes(IEnumerable<string> versions)
        => Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"version\":\"1.0.0\",\"upgradeNotes\":[" +
            string.Join(',', versions.Select(static version => "{\"version\":\"" + version + "\",\"text\":\"x\"}")) + "]}");

    internal static ReleaseManifest Release(string version, params (string Version, string Text)[] notes)
        => new(version, [.. notes.Select(static note => new UpgradeNote(note.Version, note.Text))]);
}
