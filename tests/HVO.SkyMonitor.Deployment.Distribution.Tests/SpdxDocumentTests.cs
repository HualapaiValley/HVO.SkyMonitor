using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// Contract tests for the SPDX document the installer, catalog, and image trains all publish through the release
/// tool's shared writer. SPDX 2.3 requires a SHA-1 checksum on every file (clause 8.4) and, for a package that
/// declares <c>filesAnalyzed</c>, the package verification code derived from exactly those SHA-1 values
/// (clause 7.9). A document that claims to have analyzed files while supplying neither cannot be validated by any
/// SPDX consumer, so every train is checked against the format rather than against the writer's own output.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SpdxDocumentTests
{
    [TestMethod]
    public async Task InstallerRelease_SbomDocument_IsValidSpdx23()
    {
        using var fixture = SpdxFixture.Create();
        var output = Path.Combine(fixture.Root, "installer");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.InstallerArguments(output)));

        AssertValidSpdx23(Path.Combine(output, "installer-sbom.spdx.json"), output, expectedFiles: 2);
    }

    [TestMethod]
    public async Task CatalogRelease_SbomDocument_IsValidSpdx23()
    {
        using var fixture = SpdxFixture.Create();
        var output = Path.Combine(fixture.Root, "catalog");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CatalogArguments(output)));

        // The catalog document analyzes the bundle's source files rather than the published archive, so the file
        // checksums are resolved against the bundle directory the release was built from.
        AssertValidSpdx23(Path.Combine(output, "catalog-sbom.spdx.json"), fixture.Bundle, expectedFiles: 4);
    }

    [TestMethod]
    public async Task ImageRelease_SbomDocument_IsValidSpdx23()
    {
        using var fixture = SpdxFixture.Create();
        var output = Path.Combine(fixture.Root, "image");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.ImageArguments(output)));

        AssertValidSpdx23(Path.Combine(output, "image-sbom.spdx.json"), output, expectedFiles: 2);
    }

    /// <summary>
    /// Asserts the document satisfies the parts of SPDX 2.3 a consumer needs to validate it: one described
    /// package, a SHA-1 and SHA-256 checksum per file that match the referenced bytes, and a package verification
    /// code recomputed here from those SHA-1 values rather than read back from the document that supplied them.
    /// </summary>
    private static void AssertValidSpdx23(string documentPath, string fileRoot, int expectedFiles)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(documentPath));
        var root = document.RootElement;
        Assert.AreEqual("SPDX-2.3", root.GetProperty("spdxVersion").GetString());
        Assert.AreEqual("CC0-1.0", root.GetProperty("dataLicense").GetString());
        Assert.AreEqual("SPDXRef-DOCUMENT", root.GetProperty("SPDXID").GetString());

        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.AreEqual(expectedFiles, files.Length, "The document must analyze every file the release published.");
        var identifiers = new List<string>();
        var sha1Values = new List<string>();
        foreach (var file in files)
        {
            var fileName = file.GetProperty("fileName").GetString()!;
            StringAssert.StartsWith(fileName, "./", StringComparison.Ordinal);
            var path = Path.Combine(fileRoot, fileName[2..]);
            Assert.IsTrue(File.Exists(path), $"The document names '{fileName}', which the release does not contain.");
            var checksums = file.GetProperty("checksums").EnumerateArray()
                .ToDictionary(
                    static checksum => checksum.GetProperty("algorithm").GetString()!,
                    static checksum => checksum.GetProperty("checksumValue").GetString()!,
                    StringComparer.Ordinal);
            Assert.IsTrue(
                checksums.ContainsKey("SHA1"),
                $"SPDX 2.3 requires a SHA1 checksum for every file; '{fileName}' declares only {string.Join(", ", checksums.Keys)}.");
            Assert.AreEqual(Sha1(path), checksums["SHA1"]);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), checksums["SHA256"]);
            identifiers.Add(file.GetProperty("SPDXID").GetString()!);
            sha1Values.Add(checksums["SHA1"]);
        }

        var package = root.GetProperty("packages").EnumerateArray().Single();
        Assert.IsTrue(package.GetProperty("filesAnalyzed").GetBoolean());
        Assert.IsTrue(
            package.TryGetProperty("packageVerificationCode", out var verificationCode),
            "SPDX 2.3 requires a package verification code whenever filesAnalyzed is true.");
        Assert.AreEqual(
            ExpectedVerificationCode(sha1Values),
            verificationCode.GetProperty("packageVerificationCodeValue").GetString());
        CollectionAssert.AreEqual(
            identifiers,
            package.GetProperty("hasFiles").EnumerateArray().Select(static value => value.GetString()).ToArray());

        var packageId = package.GetProperty("SPDXID").GetString();
        CollectionAssert.AreEqual(
            new[] { packageId },
            root.GetProperty("documentDescribes").EnumerateArray().Select(static value => value.GetString()).ToArray());
        var relationship = root.GetProperty("relationships").EnumerateArray().Single();
        Assert.AreEqual("SPDXRef-DOCUMENT", relationship.GetProperty("spdxElementId").GetString());
        Assert.AreEqual("DESCRIBES", relationship.GetProperty("relationshipType").GetString());
        Assert.AreEqual(packageId, relationship.GetProperty("relatedSpdxElement").GetString());
    }

#pragma warning disable CA5350 // SPDX 2.3 defines the file checksum and package verification code as SHA-1.
    private static string Sha1(string path) => Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(path)));

    private static string ExpectedVerificationCode(IEnumerable<string> sha1Values)
        => Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(sha1Values.Order(StringComparer.Ordinal)))));
#pragma warning restore CA5350

    private sealed class SpdxFixture : IDisposable
    {
        private SpdxFixture(string root) => Root = root;

        public string Root { get; }
        public string Bundle { get; private set; } = string.Empty;
        private string X64 { get; set; } = string.Empty;
        private string Arm64 { get; set; } = string.Empty;
        private string Notices { get; set; } = string.Empty;
        private ImageReleaseFixture Images { get; set; } = null!;

        public static SpdxFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-spdx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var fixture = new SpdxFixture(root)
            {
                X64 = WriteElf(root, "x64", 0x3e),
                Arm64 = WriteElf(root, "arm64", 0xb7),
                Notices = Path.Combine(root, "notices.md")
            };
            File.WriteAllText(fixture.Notices, "test notices\n");
            fixture.Bundle = CreateBundle(root);
            fixture.Images = ImageReleaseFixture.Create();
            return fixture;
        }

        public string[] InstallerArguments(string output) =>
        [
            "create-installer", "--version", "1.2.3", "--revision", new string('a', 40), "--tree", new string('b', 40),
            "--created-utc", "2026-08-24T04:29:18Z", "--linux-x64", X64, "--linux-arm64", Arm64,
            "--notices", Notices, "--output", output
        ];

        public string[] CatalogArguments(string output) =>
        [
            "create-catalog", "--revision", new string('a', 40), "--tree", new string('b', 40),
            "--created-utc", "2026-08-24T04:29:18Z", "--bundle", Bundle, "--output", output
        ];

        public string[] ImageArguments(string output) => Images.CreateArguments(output);

        /// <summary>
        /// Writes the smallest catalog bundle the release tool reads. The published catalog's own contents are
        /// validated by the catalog train's tests; this fixture exists only to exercise the shared SPDX writer on
        /// the catalog train's file set.
        /// </summary>
        private static string CreateBundle(string root)
        {
            var bundle = Path.Combine(root, "hyg-v4.2-p3-s2-r1.bundle");
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "LICENSE-HYG.md"), "license\n");
            File.WriteAllText(Path.Combine(bundle, "ATTRIBUTION-HYG.md"), "attribution\n");
            File.WriteAllBytes(Path.Combine(bundle, "hyg_v42.sqlite"), "sqlite"u8.ToArray());
            var manifest = new
            {
                manifestVersion = 2,
                schemaVersion = "2",
                preprocessingVersion = "3",
                package = new { version = "hyg-v4.2-p3-s2-r1", kind = "production" },
                catalog = new { id = "hyg-v42-production" },
                database = new { sha256 = new string('c', 64), length = 6L, rowCount = 1L },
                license = new { identifier = "CC-BY-SA-4.0" },
                topology = new { identity = "hyg-v42-topology-1", sha256 = new string('d', 64) }
            };
            File.WriteAllText(
                Path.Combine(bundle, "manifest.json"),
                JsonSerializer.Serialize(manifest),
                new UTF8Encoding(false));
            return bundle;
        }

        /// <summary>Writes the minimal ELF header the installer train validates before archiving.</summary>
        private static string WriteElf(string root, string name, ushort machine)
        {
            var path = Path.Combine(root, name);
            var bytes = new byte[20];
            "\u007fELF"u8.CopyTo(bytes);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), machine);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            Images.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
