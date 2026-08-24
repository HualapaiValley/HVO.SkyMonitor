using System.Buffers.Binary;
using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseToolTests
{
    [TestMethod]
    public async Task CreateInstaller_IdenticalInputs_ProduceIdenticalSignedAssets()
    {
        using var fixture = ReleaseToolFixture.Create();
        var first = Path.Combine(fixture.Root, "first");
        var second = Path.Combine(fixture.Root, "second");

        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(fixture.CreateArguments(first)));
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(fixture.CreateArguments(second)));

        var firstNames = Directory.GetFiles(first).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var secondNames = Directory.GetFiles(second).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(firstNames, secondNames);
        foreach (var name in firstNames)
        {
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(Path.Combine(first, name!)),
                await File.ReadAllBytesAsync(Path.Combine(second, name!)));
        }

        var manifest = Path.Combine(first, "release-manifest.json");
        var signature = Path.Combine(first, "release-manifest.json.sig");
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifest, "--private-key", fixture.PrivateKey, "--signature", signature]));
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(
            ["verify", "--manifest", manifest, "--signature", signature, "--asset-root", first, "--public-key", fixture.PublicKey]));
    }

    [TestMethod]
    public async Task CreateInstaller_UnsafeVersion_IsRejectedBeforeOutputCreation()
    {
        using var fixture = ReleaseToolFixture.Create();
        var output = Path.Combine(fixture.Root, "unsafe-output");
        var arguments = fixture.CreateArguments(output);
        arguments[2] = "../unsafe";

        Assert.AreEqual(1, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(arguments));
        Assert.IsFalse(Directory.Exists(output));
    }

    [TestMethod]
    public async Task WriteAzureSignature_Base64UrlValue_WritesCanonicalSignature()
    {
        using var fixture = ReleaseToolFixture.Create();
        var output = Path.Combine(fixture.Root, "signature.txt");
        var bytes = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
        var value = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(
            ["write-azure-signature", "--value", value, "--signature", output]));
        Assert.AreEqual(Convert.ToBase64String(bytes) + "\n", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    public async Task CreateIndex_VerifiedManifest_ProducesSignableImmutableReference()
    {
        using var fixture = ReleaseToolFixture.Create();
        var releaseRoot = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(fixture.CreateArguments(releaseRoot)));
        var manifest = Path.Combine(releaseRoot, "release-manifest.json");
        var manifestSignature = manifest + ".sig";
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifest, "--private-key", fixture.PrivateKey, "--signature", manifestSignature]));
        var indexRoot = Path.Combine(fixture.Root, "index");

        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main([
            "create-index", "--train", "installer", "--sequence", "1", "--created-utc", "2026-08-24T04:29:18Z",
            "--manifest", manifest, "--manifest-signature", manifestSignature, "--public-key", fixture.PublicKey,
            "--signing-key-id", fixture.KeyId, "--output", indexRoot
        ]));
        var indexPath = Path.Combine(indexRoot, "installer-release-index.json");
        var indexSignature = indexPath + ".sig";
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main([
            "sign-local", "--manifest", indexPath, "--private-key", fixture.PrivateKey, "--signature", indexSignature,
            "--metadata-kind", "index"
        ]));

        var indexBytes = await File.ReadAllBytesAsync(indexPath);
        var signatureBytes = await File.ReadAllBytesAsync(indexSignature);
        var index = DistributionVerifier.VerifyIndex(indexBytes, signatureBytes, fixture.TrustRoot);
        Assert.AreEqual(0, await HVO.SkyMonitor.Deployment.ReleaseTool.Program.Main([
            "verify-index", "--index", indexPath, "--signature", indexSignature, "--public-key", fixture.PublicKey
        ]));
        Assert.AreEqual("1.2.3", index.DefaultVersion);
        Assert.AreEqual(1, index.Sequence);
    }

    private sealed class ReleaseToolFixture : IDisposable
    {
        private ReleaseToolFixture(
            string root,
            string x64,
            string arm64,
            string notices,
            string privateKey,
            string publicKey,
            DistributionTrustRoot trustRoot)
        {
            Root = root;
            X64 = x64;
            Arm64 = arm64;
            Notices = notices;
            PrivateKey = privateKey;
            PublicKey = publicKey;
            TrustRoot = trustRoot;
        }

        public string Root { get; }
        public string X64 { get; }
        public string Arm64 { get; }
        public string Notices { get; }
        public string PrivateKey { get; }
        public string PublicKey { get; }
        public string KeyId => TrustRoot.KeyId;
        public DistributionTrustRoot TrustRoot { get; }

        public static ReleaseToolFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-release-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var x64 = WriteElf(root, "x64", 0x3e);
            var arm64 = WriteElf(root, "arm64", 0xb7);
            var notices = Path.Combine(root, "notices.md");
            File.WriteAllText(notices, "test notices\n");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var privateKey = Path.Combine(root, "private.pem");
            var publicKey = Path.Combine(root, "public.pem");
            File.WriteAllText(privateKey, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicKey, key.ExportSubjectPublicKeyInfoPem());
            var trustRoot = DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem());
            return new ReleaseToolFixture(root, x64, arm64, notices, privateKey, publicKey, trustRoot);
        }

        public string[] CreateArguments(string output)
            =>
            [
                "create-installer", "--version", "1.2.3", "--revision", new string('a', 40), "--tree", new string('b', 40),
                "--created-utc", "2026-08-24T04:29:18Z", "--linux-x64", X64, "--linux-arm64", Arm64,
                "--notices", Notices, "--signing-key-id", KeyId, "--output", output
            ];

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string WriteElf(string root, string name, ushort machine)
        {
            var path = Path.Combine(root, name);
            var bytes = new byte[20];
            "\u007fELF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), machine);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
