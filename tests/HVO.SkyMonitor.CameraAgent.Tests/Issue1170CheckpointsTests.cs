using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Keeps the #1170 composition-checkpoint sidecar bound to the committed pipeline manifest. Every retained pack
/// records the SHA-256 of the manifest it was measured with, and the interleaved comparator rejects a pack whose
/// manifest differs from the committed one, so a checkpoint is recorded in the sidecar and never by editing the
/// manifest. The manifest changes only together with a re-measure under its new SHA-256.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class Issue1170CheckpointsTests
{
    [TestMethod]
    public void CheckpointsBindTheCommittedManifestAndEveryCitedPack()
    {
        var root = RepositoryRoot();
        var manifestSha = Convert.ToHexStringLower(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(root, "docs", "validation", "issue-1170-pipeline-manifest.json"))));
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "docs", "validation", "issue-1170-checkpoints.json")));
        var sidecar = document.RootElement;
        Assert.AreEqual("issue1170-checkpoints-v1", sidecar.GetProperty("schema").GetString());
        Assert.AreEqual("docs/validation/issue-1170-pipeline-manifest.json", sidecar.GetProperty("manifest").GetString());

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var checkpoint in sidecar.GetProperty("checkpoints").EnumerateArray())
        {
            var id = checkpoint.GetProperty("id").GetString()!;
            Assert.IsTrue(ids.Add(id), $"checkpoint {id} appears twice");
            Assert.AreEqual(manifestSha, checkpoint.GetProperty("manifestSha256").GetString(), $"{id} cites a manifest other than the committed one");
            foreach (var arm in new[] { "A", "B" })
                Assert.IsTrue(Revision().IsMatch(checkpoint.GetProperty("arms").GetProperty(arm).GetProperty("revision").GetString()!), $"{id} arm {arm}");
            var packs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in checkpoint.GetProperty("evidence").EnumerateArray())
            {
                var path = pack.GetProperty("pack").GetString()!;
                Assert.IsTrue(packs.Add(path), $"{id} cites {path} twice");
                Assert.AreEqual(manifestSha, pack.GetProperty("manifestSha256").GetString(), $"{id} {path} was measured with another manifest");
                Assert.IsTrue(Sha256().IsMatch(pack.GetProperty("archiveSha256").GetString()!), $"{id} {path} archive");
                Assert.IsTrue(Sha256().IsMatch(pack.GetProperty("sha256SumsSha256").GetString()!), $"{id} {path} SHA256SUMS");
            }
            Assert.IsNotEmpty(packs, $"{id} cites no evidence");
        }
        Assert.IsNotEmpty(ids);
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Revision();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
