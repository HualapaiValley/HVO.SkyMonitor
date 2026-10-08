using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

/// <summary>
/// The embedded approved-catalog registry (issue #521) is the single reviewed source of every production catalog
/// identity: it approves exactly HYG 4.2 and HYG 4.4, keeps 4.2 unchanged, agrees with the shell build constants, and
/// refuses any registry text that would let two specifications claim the same identity.
/// </summary>
[TestClass]
[TestCategory("Unit")]
internal sealed partial class ApprovedCatalogSpecificationsTests
{
    private const string ResourceName = "HVO.SkyMonitor.Catalog.Sqlite.ApprovedCatalogSpecifications.json";
    private static readonly string[] ApprovedCatalogIds = ["hyg-v42-production", "hyg-v44-production"];

    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
        => Assert.IsNotNull(new ApprovedCatalogSpecificationsTests());

    [TestMethod]
    public void RegistryApprovesExactlyHygV42AndHygV44()
        => CollectionAssert.AreEqual(
            ApprovedCatalogIds, ApprovedCatalogSpecifications.All.Select(static item => item.CatalogId).ToArray());

    [TestMethod]
    public void RegistryKeepsTheHygV42IdentityUnchanged()
    {
        var specification = ApprovedCatalogSpecifications.Get("hyg-v42-production");

        Assert.AreEqual("hyg-v42-production-p3-s2", specification.PackageLineage);
        Assert.AreEqual("hyg-v4.2-p3-s2-r", specification.PackageVersionPrefix);
        Assert.AreEqual("2", specification.SchemaVersion);
        Assert.AreEqual("3", specification.PreprocessingVersion);
        Assert.AreEqual("hyg_v42.sqlite", specification.Database.RelativePath);
        Assert.AreEqual("b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2", specification.Database.Sha256);
        Assert.AreEqual(9_302_016, specification.Database.Length);
        Assert.AreEqual(119_625, specification.Database.RowCount);
        Assert.AreEqual(1_361, specification.License.Attribution.Length);
    }

    [TestMethod]
    [DataRow("hyg-v4.2-p3-s2-r1", "hyg-v42-production")]
    [DataRow("hyg-v4.4-p4-s3-r1", "hyg-v44-production")]
    [DataRow("hyg-v4.4-p4-s3-r12", "hyg-v44-production")]
    [DataRow("hyg-v4.4-p4-s3-r0", null)]
    [DataRow("hyg-v4.4-p4-s3-r", null)]
    [DataRow("hyg-v4.4-p3-s2-r1", null)]
    [DataRow(null, null)]
    public void PackageVersionSelectsAtMostOneLineage(string? packageVersion, string? catalogId)
    {
        var found = ApprovedCatalogSpecifications.TryGetByPackageVersion(packageVersion, out var specification);

        Assert.AreEqual(catalogId is not null, found);
        Assert.AreEqual(catalogId, specification?.CatalogId);
    }

    [TestMethod]
    public void UnapprovedCatalogIsRefused()
    {
        Assert.IsFalse(ApprovedCatalogSpecifications.TryGet("hyg-v45-production", out _));
        Assert.IsFalse(ApprovedCatalogSpecifications.TryGet(null, out _));
        Assert.ThrowsExactly<InvalidDataException>(() => ApprovedCatalogSpecifications.Get("hyg-v42-fixture"));
    }

    [TestMethod]
    [DataRow("hyg-v42-production", "catalog-common.sh", "HYG_")]
    [DataRow("hyg-v44-production", "hyg-v44-common.sh", "HYG44_")]
    public void ShellBuildConstantsAgreeWithTheRegistry(string catalogId, string script, string prefix)
    {
        var specification = ApprovedCatalogSpecifications.Get(catalogId);
        var constants = ReadShellConstants(Path.Combine(RepositoryRoot(), "scripts", "catalog", script), prefix);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CATALOG_ID"] = specification.CatalogId,
            ["CATALOG_NAME"] = specification.CatalogName,
            ["CATALOG_VERSION"] = specification.CatalogVersion,
            ["SOURCE_PROJECT_URL"] = specification.Source.ProjectUrl.OriginalString,
            ["SOURCE_URL"] = specification.Source.DownloadUrl.OriginalString,
            ["SOURCE_OID"] = specification.Source.Oid,
            ["COMPRESSED_SHA256"] = specification.Source.Compressed.Sha256,
            ["COMPRESSED_LENGTH"] = Invariant(specification.Source.Compressed.Length),
            ["DECOMPRESSED_SHA256"] = specification.Source.Decompressed.Sha256,
            ["DECOMPRESSED_LENGTH"] = Invariant(specification.Source.Decompressed.Length),
            ["SCHEMA_VERSION"] = specification.SchemaVersion,
            ["PREPROCESSING_VERSION"] = specification.PreprocessingVersion,
            ["DATABASE_FILE"] = specification.Database.RelativePath,
            ["DATABASE_SHA256"] = specification.Database.Sha256,
            ["DATABASE_LENGTH"] = Invariant(specification.Database.Length),
            ["EXPECTED_ROWS"] = Invariant(specification.Database.RowCount),
            ["LICENSE_IDENTIFIER"] = specification.License.Identifier,
            ["LICENSE_URL"] = specification.License.Url.OriginalString,
            ["LICENSE_SHA256"] = specification.License.File.Sha256,
            ["LICENSE_LENGTH"] = Invariant(specification.License.File.Length),
            ["ATTRIBUTION_SHA256"] = specification.License.Attribution.Sha256,
            ["ATTRIBUTION_LENGTH"] = Invariant(specification.License.Attribution.Length)
        };

        foreach (var (name, value) in expected)
        {
            Assert.IsTrue(constants.TryGetValue(name, out var actual), $"{prefix}{name} is not defined in {script}.");
            Assert.AreEqual(value, actual, $"{prefix}{name} differs from the approved registry.");
        }
        Assert.IsTrue(specification.IsPackageVersion(constants["PACKAGE_VERSION"]));
    }

    [TestMethod]
    public void EmbeddedRegistryParsesToTheSameSpecifications()
    {
        var parsed = Parse(EmbeddedRegistry().ToJsonString());

        CollectionAssert.AreEquivalent(ApprovedCatalogIds, parsed.Keys.ToArray());
        Assert.AreEqual(ApprovedCatalogSpecifications.Get("hyg-v44-production").Database, parsed["hyg-v44-production"].Database);
    }

    [TestMethod]
    [DataRow("registry-version", DisplayName = "registry-version")]
    [DataRow("empty", DisplayName = "empty")]
    [DataRow("unknown-key", DisplayName = "unknown-key")]
    [DataRow("duplicate-id", DisplayName = "duplicate-id")]
    [DataRow("duplicate-lineage", DisplayName = "duplicate-lineage")]
    [DataRow("overlapping-prefix", DisplayName = "overlapping-prefix")]
    [DataRow("unsupported-schema", DisplayName = "unsupported-schema")]
    [DataRow("sol-row", DisplayName = "sol-row")]
    [DataRow("relative-source", DisplayName = "relative-source")]
    [DataRow("database-path", DisplayName = "database-path")]
    [DataRow("endpoint", DisplayName = "endpoint")]
    public void RegistryRefusesAmbiguousOrUnsupportedSpecifications(string mutation)
    {
        var registry = EmbeddedRegistry();
        var specifications = registry["specifications"]!.AsArray();
        var hygV44 = specifications[1]!.AsObject();
        switch (mutation)
        {
            case "registry-version":
                registry["registryVersion"] = 2;
                break;
            case "empty":
                specifications.Clear();
                break;
            case "unknown-key":
                hygV44["approvedBy"] = "manifest";
                break;
            case "duplicate-id":
                hygV44["catalogId"] = "hyg-v42-production";
                break;
            case "duplicate-lineage":
                hygV44["packageLineage"] = "hyg-v42-production-p3-s2";
                break;
            case "overlapping-prefix":
                hygV44["packageVersionPrefix"] = "hyg-v4.2-p3-s2-r";
                break;
            case "unsupported-schema":
                hygV44["schemaVersion"] = "4";
                break;
            case "sol-row":
                hygV44["database"]!["solCount"] = 1;
                break;
            case "relative-source":
                hygV44["source"]!["downloadUrl"] = "lfs/objects/hyg";
                break;
            case "database-path":
                hygV44["database"]!["relativePath"] = "../hyg_v44.sqlite";
                break;
            case "endpoint":
                hygV44["topology"]!["unresolvedEndpoints"] = new JsonArray("0");
                break;
            default:
                Assert.Fail($"Unknown mutation '{mutation}'.");
                break;
        }

        Assert.ThrowsExactly<InvalidDataException>(() => Parse(registry.ToJsonString()));
    }

    [TestMethod]
    public void RegistryRefusesDuplicateJsonProperties()
    {
        var text = EmbeddedRegistry().ToJsonString().Replace(
            "\"registryVersion\":1", "\"registryVersion\":1,\"registryVersion\":1", StringComparison.Ordinal);

        Assert.ThrowsExactly<InvalidDataException>(() => Parse(text));
    }

    private static IReadOnlyDictionary<string, ApprovedCatalogSpecification> Parse(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return ApprovedCatalogSpecifications.Parse(stream);
    }

    private static JsonObject EmbeddedRegistry()
    {
        using var stream = typeof(ApprovedCatalogSpecifications).Assembly.GetManifestResourceStream(ResourceName);
        Assert.IsNotNull(stream);
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static Dictionary<string, string> ReadShellConstants(string path, string prefix)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var match = ReadonlyAssignment().Match(line);
            if (!match.Success) continue;
            values[match.Groups["name"].Value] = ShellReference().Replace(
                match.Groups["value"].Value, reference => values[reference.Groups["name"].Value]);
        }
        return values
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(item => item.Key[prefix.Length..], item => item.Value, StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.IsNotNull(directory, "Could not locate the repository root from the test output directory.");
        return directory.FullName;
    }

    private static string Invariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex("^readonly (?<name>[A-Z0-9_]+)=\"(?<value>[^\"]*)\"$")]
    private static partial Regex ReadonlyAssignment();

    [GeneratedRegex(@"\$(?<name>[A-Z0-9_]+)")]
    private static partial Regex ShellReference();
}
