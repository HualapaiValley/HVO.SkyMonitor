using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// The catalog train must refuse a bundle whose bytes do not match the approved registry before it writes any release
/// output (issue #521). The registry pins the real database bytes, so these cases copy the retained production bundles
/// and are environment-gated like the deployment CLI's catalog transitions.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CatalogReleaseToolTests
{
    private const string HygV44PackageVersion = "hyg-v4.4-p4-s3-r1";

    [TestMethod]
    public async Task CreateCatalog_ApprovedHygV44Bundle_RecordsTheRegisteredIdentity()
    {
        using var fixture = CatalogReleaseFixture.Create(RequireBundle("HVO_HYG_V44_CATALOG_BUNDLE"));

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.Arguments()));

        var specification = ApprovedCatalogSpecifications.All.Single(candidate => candidate.CatalogId == "hyg-v44-production");
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.Output, "catalog-manifest.json")));
        var catalog = manifest.RootElement.GetProperty("catalog");
        Assert.AreEqual("hyg-v44-production", catalog.GetProperty("catalogId").GetString());
        Assert.AreEqual(HygV44PackageVersion, catalog.GetProperty("packageVersion").GetString());
        Assert.AreEqual(specification.Database.Sha256, catalog.GetProperty("databaseSha256").GetString());
        Assert.AreEqual(specification.Database.RowCount, catalog.GetProperty("rowCount").GetInt64());
    }

    [TestMethod]
    [DataRow("corrupt-database")]
    [DataRow("truncated-database")]
    [DataRow("altered-attribution")]
    public async Task CreateCatalog_DamagedPayloadUnderAnUnchangedManifest_IsRefusedBeforeAnyOutput(string damage)
    {
        using var fixture = CatalogReleaseFixture.Create(RequireBundle("HVO_HYG_V44_CATALOG_BUNDLE"));
        var database = Path.Combine(fixture.Bundle, "hyg_v44.sqlite");
        switch (damage)
        {
            case "corrupt-database":
                await using (var stream = new FileStream(database, FileMode.Open, FileAccess.ReadWrite))
                {
                    stream.Position = stream.Length / 2;
                    var value = stream.ReadByte();
                    stream.Position--;
                    stream.WriteByte((byte)(value ^ 0xFF));
                }
                break;
            case "truncated-database":
                await using (var stream = new FileStream(database, FileMode.Open, FileAccess.ReadWrite))
                {
                    stream.SetLength(stream.Length - 4096);
                }
                break;
            default:
                await File.AppendAllTextAsync(Path.Combine(fixture.Bundle, "ATTRIBUTION-HYG.md"), "unapproved\n");
                break;
        }

        Assert.AreEqual(1, await ReleaseTool.Program.Main(fixture.Arguments()));

        Assert.IsFalse(Directory.Exists(fixture.Output), "A refused bundle must not produce release output.");
    }

    [TestMethod]
    public async Task CreateCatalog_HygV44BytesUnderAHygV42Manifest_IsRefusedBeforeAnyOutput()
    {
        // The 4.4 payload relabelled as the 4.2 lineage has the 4.2 file set and an approved 4.2 package version,
        // so only validating the bytes against the 4.2 specification can refuse it.
        using var fixture = CatalogReleaseFixture.Create(RequireBundle("HVO_HYG_V44_CATALOG_BUNDLE"));
        File.Move(Path.Combine(fixture.Bundle, "hyg_v44.sqlite"), Path.Combine(fixture.Bundle, "hyg_v42.sqlite"));
        var manifestPath = Path.Combine(fixture.Bundle, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["package"]!["version"] = "hyg-v4.2-p3-s2-r1";
        manifest["catalog"]!["id"] = "hyg-v42-production";
        manifest["database"]!["relativePath"] = "hyg_v42.sqlite";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        Assert.AreEqual(1, await ReleaseTool.Program.Main(fixture.Arguments()));

        Assert.IsFalse(Directory.Exists(fixture.Output), "A relabelled bundle must not produce release output.");
    }

    [TestMethod]
    [Timeout(60_000)]
    [DataRow("fifo-database")]
    [DataRow("fifo-manifest")]
    [DataRow("symlinked-database")]
    [DataRow("hard-linked-database")]
    public async Task CreateCatalog_NonRegularOrLinkedSourceFile_IsRefusedBeforeAnyOutput(string source)
    {
        // A FIFO under a registry name would block the copy or the manifest read indefinitely, and a hard-linked input
        // can be rewritten through its other name after validation, so each original input must be proven a regular,
        // singly linked file through the handle that is then read.
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("FIFO and hard-link source cases are created on Linux.");
        using var fixture = CatalogReleaseFixture.Create(RequireBundle("HVO_HYG_V44_CATALOG_BUNDLE"));
        var database = Path.Combine(fixture.Bundle, "hyg_v44.sqlite");
        switch (source)
        {
            case "fifo-database":
                ReplaceWithFifo(database);
                break;
            case "fifo-manifest":
                ReplaceWithFifo(Path.Combine(fixture.Bundle, "manifest.json"));
                break;
            case "symlinked-database":
                var target = Path.Combine(fixture.Root, "hyg_v44.sqlite");
                File.Move(database, target);
                File.CreateSymbolicLink(database, target);
                break;
            default:
                Assert.AreEqual(0, NativeLink(database, Path.Combine(fixture.Root, "alias.sqlite")),
                    $"link failed with errno {Marshal.GetLastPInvokeError()}.");
                break;
        }

        Assert.AreEqual(1, await ReleaseTool.Program.Main(fixture.Arguments()));

        Assert.IsFalse(Directory.Exists(fixture.Output), "A refused source file must not produce release output.");
    }

    private static void ReplaceWithFifo(string path)
    {
        File.Delete(path);
        Assert.AreEqual(0, NativeMkFifo(path, 0x180), $"mkfifo failed with errno {Marshal.GetLastPInvokeError()}.");
    }

    private static string RequireBundle(string variable)
    {
        var bundle = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive($"Set {variable} to run catalog release validation.");
        return bundle;
    }

    private sealed class CatalogReleaseFixture : IDisposable
    {
        private CatalogReleaseFixture(string root)
        {
            Root = root;
            Bundle = Path.Combine(root, $"{HygV44PackageVersion}.bundle");
            Output = Path.Combine(root, "output");
        }

        public string Root { get; }
        public string Bundle { get; }
        public string Output { get; }

        public static CatalogReleaseFixture Create(string source)
        {
            var fixture = new CatalogReleaseFixture(Path.Combine(Path.GetTempPath(), $"hvo-catalog-release-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(fixture.Bundle);
            foreach (var path in Directory.EnumerateFiles(source))
            {
                var copy = Path.Combine(fixture.Bundle, Path.GetFileName(path));
                File.Copy(path, copy);
                File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return fixture;
        }

        public string[] Arguments() =>
        [
            "create-catalog", "--revision", new string('a', 40), "--tree", new string('b', 40),
            "--created-utc", "2026-08-24T04:29:18Z", "--bundle", Bundle, "--output", Output
        ];

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

#pragma warning disable SYSLIB1054 // Test-only creation of an actual FIFO and hard link for the Linux qualification case.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "mkfifo", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMkFifo(string path, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeLink(string existingPath, string newPath);
#pragma warning restore SYSLIB1054
}
