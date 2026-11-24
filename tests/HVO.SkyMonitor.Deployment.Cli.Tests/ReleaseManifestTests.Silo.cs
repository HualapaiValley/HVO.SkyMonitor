using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The stock Silo image a release names (#1966, G1): release.json's <c>images.silo</c> pins the upstream index and each
/// platform's manifest and config, with its source, licence and supplier. The reader never refuses a release for it, so
/// releases written before it stay readable, and a Silo-layout install admits only a release that names exactly this
/// installer's pin.
/// </summary>
public sealed partial class ReleaseManifestTests
{
    [TestMethod]
    public void SiloPin_TheReleaseToolsOutputNamesExactlyThePinnedUpstreamImage()
    {
        using var stream = typeof(ReleaseManifestTests).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Deployment.Cli.Tests.ReleaseToolOutput.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var root = JsonNode.Parse(buffer.ToArray())!.AsObject();

        var manifest = ReleaseManifest.Parse(buffer.ToArray());

        Assert.IsTrue(manifest.NamesSiloImage);
        Assert.IsTrue(manifest.SiloImageMatchesPin);
        SiloInstallation.AdmitRelease(manifest);
        var silo = root["images"]![SiloImagePin.ImageKey]!.AsObject();
        Assert.IsTrue(JsonNode.DeepEquals(SiloImagePin.ReleaseEntry(), silo));
        Assert.AreEqual("docker.io/pgsty/silo:RELEASE.2026-11-12T00-00-00Z@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329",
            (string?)silo["reference"]);
        Assert.AreEqual("upstream", (string?)silo["supplier"]);
        Assert.AreEqual("AGPL-3.0-or-later", (string?)silo["license"]);
        Assert.AreEqual("https://github.com/pgsty/silo/tree/RELEASE.2026-11-12T00-00-00Z", (string?)silo["source"]);
        var platforms = silo["platforms"]!.AsArray().Select(static platform => (
            (string?)platform!["platform"], (string?)platform["manifestDigest"], (string?)platform["configDigest"])).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            ("linux/amd64", "sha256:6f48a60830eb101e19f955124914554d73c4a4721396d3d13d61634e1396eadf",
                "sha256:f8cec3d6c63585d664c035acecfacb92dc974ab70c1f57a528d5baa3c224e082"),
            ("linux/arm64", "sha256:dad2a4bc279f9095d9835398dda266abd4f2f7486d0cb66a071951f1e4a6791d",
                "sha256:66e09caa8c3e0e834de2202d9a4c45a9a0608f63ab2061a8f3953ae23fc135e7")
        }, platforms);
        // The CameraAgent image the release names is read as before.
        Assert.HasCount(2, manifest.CameraAgent!.OfflineArchives);
    }

    [TestMethod]
    public void SiloPin_AReleaseWrittenBeforeItIsReadWholeButPredatesTheSiloLayout()
    {
        // The schema-1 fixture is a release written before #1966: it names no Silo image.
        var manifest = ReleaseManifest.Parse(Fixture());

        Assert.IsFalse(manifest.NamesSiloImage);
        Assert.IsFalse(manifest.SiloImageMatchesPin);
        Assert.IsNotNull(manifest.CameraAgent);
        var refusal = Assert.ThrowsExactly<InstallerException>(() => SiloInstallation.AdmitRelease(manifest));
        Assert.IsTrue(refusal.IsRefusal);
        StringAssert.Contains(refusal.Message, "predates the Silo layout", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Message, "Nothing was changed.", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("index-digest")]
    [DataRow("arm64-config")]
    [DataRow("amd64-manifest")]
    [DataRow("platform-missing")]
    [DataRow("platform-extra")]
    [DataRow("supplier")]
    [DataRow("revision")]
    [DataRow("license")]
    [DataRow("member-extra")]
    [DataRow("member-missing")]
    [DataRow("not-an-object")]
    [DataRow("null")]
    public void SiloPin_AReleaseNamingAnotherSiloImageIsReadButNeverAdmitted(string change)
    {
        var json = Document(root =>
        {
            var entry = SiloImagePin.ReleaseEntry();
            var platforms = entry["platforms"]!.AsArray();
            switch (change)
            {
                case "index-digest": entry["digest"] = "sha256:" + new string('0', 64); break;
                case "arm64-config": platforms[1]!["configDigest"] = "sha256:" + new string('1', 64); break;
                case "amd64-manifest": platforms[0]!["manifestDigest"] = "sha256:" + new string('2', 64); break;
                case "platform-missing": platforms.RemoveAt(1); break;
                case "platform-extra":
                    platforms.Add(new JsonObject
                    {
                        ["platform"] = "linux/riscv64",
                        ["manifestDigest"] = "sha256:" + new string('3', 64),
                        ["configDigest"] = "sha256:" + new string('4', 64)
                    });
                    break;
                case "supplier": entry["supplier"] = "hvo"; break;
                case "revision": entry["revision"] = new string('5', 40); break;
                case "license": entry["license"] = "Apache-2.0"; break;
                case "member-extra": entry["mirror"] = "registry.example/silo"; break;
                case "member-missing": entry.Remove("source"); break;
            }
            root["images"]![SiloImagePin.ImageKey] = change switch
            {
                "not-an-object" => JsonValue.Create("docker.io/pgsty/silo"),
                "null" => null,
                _ => entry
            };
        });

        var manifest = ReleaseManifest.Parse(json);

        Assert.IsTrue(manifest.NamesSiloImage);
        Assert.IsFalse(manifest.SiloImageMatchesPin);
        Assert.IsNotNull(manifest.CameraAgent);
        var refusal = Assert.ThrowsExactly<InstallerException>(() => SiloInstallation.AdmitRelease(manifest));
        Assert.IsTrue(refusal.IsRefusal);
        StringAssert.Contains(refusal.Message, "names a stock Silo image this installer has not qualified", StringComparison.Ordinal);
    }

    [TestMethod]
    public void SiloPin_AcceptsTheImageIdsEitherDockerStoreGivesThePinnedImage()
    {
        foreach (var platform in SiloImagePin.Platforms)
        {
            CollectionAssert.AreEqual(new[] { SiloImagePin.IndexDigest, platform.ManifestDigest, platform.ConfigDigest },
                SiloImagePin.AcceptedImageIds(platform).ToArray());
            Assert.AreSame(platform, SiloImagePin.ForArchitecture(platform.Architecture));
        }
        Assert.IsNull(SiloImagePin.ForArchitecture("riscv64"));
        Assert.IsNull(SiloImagePin.ForArchitecture(null));
        using var array = JsonDocument.Parse("[]");
        Assert.IsFalse(SiloImagePin.Matches(array.RootElement));
    }
}
