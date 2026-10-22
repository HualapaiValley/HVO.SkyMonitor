using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

/// <summary>
/// The embedded approved-catalog registry (issue #521) is the single reviewed source of every production catalog
/// identity: it approves exactly HYG 4.2, HYG 4.4 and the composed HYG 4.4 + OpenNGC catalog (issue #525), keeps 4.2
/// unchanged, agrees with the shell build constants, and refuses any registry text that would let two specifications
/// claim the same identity or a composed catalog name a stars component the registry does not approve.
/// </summary>
[TestClass]
[TestCategory("Unit")]
internal sealed partial class ApprovedCatalogSpecificationsTests
{
    private const string ResourceName = "HVO.SkyMonitor.Catalog.Sqlite.ApprovedCatalogSpecifications.json";
    private const string ComposedCatalogId = "hyg-v44-openngc-production";
    private static readonly string[] ApprovedCatalogIds = ["hyg-v42-production", ComposedCatalogId, "hyg-v44-production"];
    private static readonly string[] DisputedDuplicateSubjects = ["M102->NGC5457"];

    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
        => Assert.IsNotNull(new ApprovedCatalogSpecificationsTests());

    [TestMethod]
    public void RegistryApprovesExactlyHygV42HygV44AndTheComposedCatalog()
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
    [DataRow("hyg-v4.4-openngc-v20260501-p5-s4-r1", ComposedCatalogId)]
    [DataRow("hyg-v4.4-openngc-v20260501-p5-s4-r0", null)]
    [DataRow("hyg-v4.4-openngc-v20260501-p4-s3-r1", null)]
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
    public void RegistryComposesTheApprovedHygV44PackageWithOpenNgc()
    {
        var composed = ApprovedCatalogSpecifications.Get(ComposedCatalogId);
        var stars = ApprovedCatalogSpecifications.Get("hyg-v44-production");
        var composition = composed.Composition;

        Assert.IsNotNull(composition);
        Assert.IsNull(stars.Composition);
        Assert.AreEqual(CatalogSnapshotResolver.ComposedManifestVersion, composed.ManifestVersion);
        Assert.AreEqual("4", composed.SchemaVersion);
        Assert.AreEqual("5", composed.PreprocessingVersion);
        Assert.AreEqual(stars.Database.RowCount, composed.Database.RowCount, "RowCount remains the star count.");
        Assert.AreEqual(stars.Source, composed.Source);
        Assert.AreEqual("hyg-v44-production", composition.Stars.CatalogId);
        Assert.IsTrue(stars.IsPackageVersion(composition.Stars.PackageVersion));
        Assert.AreEqual(stars.Database.Sha256, composition.Stars.Database.Sha256);
        Assert.AreEqual(
            new CatalogDeepSkyCounts(13_371, 65_822, 10, 182, 359, 441, 90_861), composition.DeepSkyCounts);
        Assert.AreEqual("36cb178a0f69dba8bfc03a99c10512831edf1c6b", composition.DeepSky.Commit);
        Assert.AreEqual(
            1, composition.Transformations.Single(static item => item.Id == "sh2-identifier-dropped").Count);
        CollectionAssert.AreEqual(
            DisputedDuplicateSubjects,
            composition.Transformations.Single(static item => item.Id == "duplicate-disputed").Subjects.ToArray());
    }

    [TestMethod]
    [DataRow("hyg-v42-production", "HYG_", new[] { "catalog-common.sh" })]
    [DataRow("hyg-v44-production", "HYG44_", new[] { "hyg-v44-common.sh" })]
    [DataRow(ComposedCatalogId, "HYGNGC_", new[] { "catalog-common.sh", "hyg-v44-common.sh", "hyg-v44-openngc-common.sh" })]
    public void ShellBuildConstantsAgreeWithTheRegistry(string catalogId, string prefix, string[] scripts)
    {
        var specification = ApprovedCatalogSpecifications.Get(catalogId);
        var script = scripts[^1];
        var constants = ReadShellConstants(ScriptPaths(scripts), prefix);
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
    public void ComposedShellBuildConstantsAgreeWithTheRegistryComposition()
    {
        var specification = ApprovedCatalogSpecifications.Get(ComposedCatalogId);
        var composition = specification.Composition!;
        var scripts = ScriptPaths(["catalog-common.sh", "hyg-v44-common.sh", "hyg-v44-openngc-common.sh"]);
        var composed = ReadShellConstants(scripts, "HYGNGC_");
        var openNgc = ReadShellConstants(scripts, "OPENNGC_");
        var stars = ReadShellConstants(scripts, "HYG44_");
        var deepSky = composition.DeepSky;
        var expected = new (IReadOnlyDictionary<string, string> Constants, string Name, string Value)[]
        {
            (composed, "PACKAGE_LINEAGE", specification.PackageLineage),
            (composed, "MANIFEST_VERSION", Invariant(specification.ManifestVersion)),
            (composed, "DEEP_SKY_OBJECT_COUNT", Invariant(composition.DeepSkyCounts.ObjectCount)),
            (composed, "DEEP_SKY_ALIAS_COUNT", Invariant(composition.DeepSkyCounts.AliasCount)),
            (composed, "DEEP_SKY_TOMBSTONE_COUNT", Invariant(composition.DeepSkyCounts.TombstoneCount)),
            (composed, "OUTLINE_OBJECT_COUNT", Invariant(composition.DeepSkyCounts.OutlineObjectCount)),
            (composed, "OUTLINE_SET_COUNT", Invariant(composition.DeepSkyCounts.OutlineSetCount)),
            (composed, "OUTLINE_RING_COUNT", Invariant(composition.DeepSkyCounts.OutlineRingCount)),
            (composed, "OUTLINE_POINT_COUNT", Invariant(composition.DeepSkyCounts.OutlinePointCount)),
            (composed, "LICENSE_FILE", specification.License.File.RelativePath),
            (composed, "ATTRIBUTION_FILE", specification.License.Attribution.RelativePath),
            (stars, "CATALOG_ID", composition.Stars.CatalogId),
            (stars, "DATABASE_SHA256", composition.Stars.Database.Sha256),
            (stars, "DATABASE_LENGTH", Invariant(composition.Stars.Database.Length)),
            (stars, "EXPECTED_ROWS", Invariant(composition.Stars.Database.RowCount)),
            (openNgc, "NAME", deepSky.Name),
            (openNgc, "VERSION", deepSky.Version),
            (openNgc, "COMMIT", deepSky.Commit),
            (openNgc, "PROJECT_URL", deepSky.ProjectUrl.OriginalString),
            (openNgc, "LICENSE_IDENTIFIER", deepSky.License),
            (openNgc, "NGC_FILE", deepSky.Inputs[0].RelativePath),
            (openNgc, "NGC_SHA256", deepSky.Inputs[0].Sha256),
            (openNgc, "NGC_LENGTH", Invariant(deepSky.Inputs[0].Length)),
            (openNgc, "NGC_ROWS", Invariant(deepSky.Inputs[0].RowCount)),
            (openNgc, "ADDENDUM_FILE", deepSky.Inputs[1].RelativePath),
            (openNgc, "ADDENDUM_SHA256", deepSky.Inputs[1].Sha256),
            (openNgc, "ADDENDUM_LENGTH", Invariant(deepSky.Inputs[1].Length)),
            (openNgc, "ADDENDUM_ROWS", Invariant(deepSky.Inputs[1].RowCount)),
            (openNgc, "OUTLINE_DIRECTORY", deepSky.Outlines.RelativePath),
            (openNgc, "OUTLINE_FILE_COUNT", Invariant(deepSky.Outlines.FileCount)),
            (openNgc, "OUTLINE_LISTING_SHA256", deepSky.Outlines.ListingSha256),
            (openNgc, "OUTLINE_POINT_COUNT", Invariant(deepSky.Outlines.PointCount))
        };

        foreach (var (constants, name, value) in expected)
        {
            Assert.IsTrue(constants.TryGetValue(name, out var actual), $"{name} is not defined in the composer scripts.");
            Assert.AreEqual(value, actual, $"{name} differs from the approved registry composition.");
        }
        Assert.AreEqual(2, deepSky.Inputs.Count);
        Assert.IsTrue(
            ApprovedCatalogSpecifications.Get(composition.Stars.CatalogId).IsPackageVersion(stars["PACKAGE_VERSION"]));
        Assert.AreEqual(composition.Stars.PackageVersion, stars["PACKAGE_VERSION"]);
        CollectionAssert.AreEqual(
            composition.Transformations
                .Select(static item => $"{item.Id}|{Invariant(item.Count)}|{string.Join(';', item.Subjects)}")
                .ToArray(),
            ReadShellArray(scripts[^1], "HYGNGC_TRANSFORMATIONS"));
    }

    [TestMethod]
    public void ComposedOutlineListingIsTheCommittedFile()
    {
        var listing = Path.Combine(RepositoryRoot(), "scripts", "catalog", "openngc-v20260501-outlines.sha256");
        var outlines = ApprovedCatalogSpecifications.Get(ComposedCatalogId).Composition!.DeepSky.Outlines;

        Assert.AreEqual(
            outlines.ListingSha256,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(listing))));
        Assert.AreEqual(outlines.FileCount, File.ReadAllLines(listing).Length);
    }

    [TestMethod]
    public void ComposedLicenseFilesAreTheCommittedSources()
    {
        var license = ApprovedCatalogSpecifications.Get(ComposedCatalogId).License;
        foreach (var (pinned, source) in new[]
                 {
                     (license.File, "hyg-v44-openngc-license.md"),
                     (license.Attribution, "hyg-v44-openngc-attribution.md")
                 })
        {
            var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "docs", "catalog", source));
            Assert.AreEqual(pinned.Length, bytes.LongLength, source);
            Assert.AreEqual(pinned.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), source);
        }
    }

    [TestMethod]
    public void EmbeddedRegistryParsesToTheSameSpecifications()
    {
        var parsed = Parse(EmbeddedRegistry().ToJsonString());

        CollectionAssert.AreEquivalent(ApprovedCatalogIds, parsed.Keys.ToArray());
        Assert.AreEqual(ApprovedCatalogSpecifications.Get("hyg-v44-production").Database, parsed["hyg-v44-production"].Database);
    }

    [TestMethod]
    [DataRow("registry-version-previous", DisplayName = "registry-version-previous")]
    [DataRow("registry-version-next", DisplayName = "registry-version-next")]
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
    [DataRow("uncomposed-manifest-v3", DisplayName = "uncomposed-manifest-v3")]
    [DataRow("composed-manifest-v2", DisplayName = "composed-manifest-v2")]
    [DataRow("composed-schema-3", DisplayName = "composed-schema-3")]
    [DataRow("composed-without-components", DisplayName = "composed-without-components")]
    [DataRow("composed-without-deep-sky-counts", DisplayName = "composed-without-deep-sky-counts")]
    [DataRow("composed-zero-objects", DisplayName = "composed-zero-objects")]
    [DataRow("composed-star-rows", DisplayName = "composed-star-rows")]
    [DataRow("composed-unapproved-stars", DisplayName = "composed-unapproved-stars")]
    [DataRow("composed-composed-stars", DisplayName = "composed-composed-stars")]
    [DataRow("composed-stars-package", DisplayName = "composed-stars-package")]
    [DataRow("composed-stars-database", DisplayName = "composed-stars-database")]
    [DataRow("composed-source", DisplayName = "composed-source")]
    [DataRow("composed-component-order", DisplayName = "composed-component-order")]
    [DataRow("composed-unknown-component-key", DisplayName = "composed-unknown-component-key")]
    [DataRow("composed-http-project", DisplayName = "composed-http-project")]
    [DataRow("composed-short-commit", DisplayName = "composed-short-commit")]
    [DataRow("composed-input-path", DisplayName = "composed-input-path")]
    [DataRow("composed-duplicate-input", DisplayName = "composed-duplicate-input")]
    [DataRow("composed-transformation-id", DisplayName = "composed-transformation-id")]
    [DataRow("composed-duplicate-transformation", DisplayName = "composed-duplicate-transformation")]
    [DataRow("composed-negative-count", DisplayName = "composed-negative-count")]
    [DataRow("composed-missing-subject", DisplayName = "composed-missing-subject")]
    [DataRow("composed-unsorted-subjects", DisplayName = "composed-unsorted-subjects")]
    [DataRow("composed-listed-large-count", DisplayName = "composed-listed-large-count")]
    [DataRow("composed-control-character", DisplayName = "composed-control-character")]
    public void RegistryRefusesAmbiguousOrUnsupportedSpecifications(string mutation)
    {
        var registry = EmbeddedRegistry();
        var specifications = registry["specifications"]!.AsArray();
        var hygV44 = specifications[1]!.AsObject();
        var composed = specifications[2]!.AsObject();
        var components = composed["components"]!.AsArray();
        var transformations = composed["transformations"]!.AsArray();
        Assert.AreEqual(ComposedCatalogId, composed["catalogId"]!.GetValue<string>());
        switch (mutation)
        {
            case "registry-version-previous":
                registry["registryVersion"] = ApprovedCatalogSpecifications.RegistryVersion - 1;
                break;
            case "registry-version-next":
                registry["registryVersion"] = ApprovedCatalogSpecifications.RegistryVersion + 1;
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
            case "uncomposed-manifest-v3":
                hygV44["manifestVersion"] = CatalogSnapshotResolver.ComposedManifestVersion;
                break;
            case "composed-manifest-v2":
                composed["manifestVersion"] = CatalogSnapshotResolver.MinimumSupportedManifestVersion;
                break;
            case "composed-schema-3":
                composed["schemaVersion"] = "3";
                break;
            case "composed-without-components":
                composed.Remove("components");
                break;
            case "composed-without-deep-sky-counts":
                composed["database"]!.AsObject().Remove("deepSky");
                break;
            case "composed-zero-objects":
                composed["database"]!["deepSky"]!["objectCount"] = 0;
                break;
            case "composed-star-rows":
                composed["database"]!["rowCount"] = 119_614;
                break;
            case "composed-unapproved-stars":
                components[0]!["catalogId"] = "hyg-v45-production";
                break;
            case "composed-composed-stars":
                components[0]!["catalogId"] = ComposedCatalogId;
                components[0]!["packageVersion"] = "hyg-v4.4-openngc-v20260501-p5-s4-r1";
                break;
            case "composed-stars-package":
                components[0]!["packageVersion"] = "hyg-v4.2-p3-s2-r1";
                break;
            case "composed-stars-database":
                components[0]!["database"]!["sha256"] = new string('a', 64);
                break;
            case "composed-source":
                composed["source"]!["oid"] = new string('b', 64);
                break;
            case "composed-component-order":
                composed["components"] = new JsonArray(components[1]!.DeepClone(), components[0]!.DeepClone());
                break;
            case "composed-unknown-component-key":
                components[1]!["approvedBy"] = "manifest";
                break;
            case "composed-http-project":
                components[1]!["projectUrl"] = "http://github.com/mattiaverga/OpenNGC";
                break;
            case "composed-short-commit":
                components[1]!["commit"] = "36cb178a";
                break;
            case "composed-input-path":
                components[1]!["inputs"]![0]!["relativePath"] = "../NGC.csv";
                break;
            case "composed-duplicate-input":
                components[1]!["inputs"]![1]!["relativePath"] = "database_files/NGC.csv";
                break;
            case "composed-transformation-id":
                transformations[0]!["id"] = "Position_Angle";
                break;
            case "composed-duplicate-transformation":
                transformations[1]!["id"] = transformations[0]!["id"]!.GetValue<string>();
                break;
            case "composed-negative-count":
                transformations[0]!["count"] = -1;
                break;
            case "composed-missing-subject":
                Single(transformations, "nonexistent-tombstoned")["subjects"]!.AsArray().RemoveAt(0);
                break;
            case "composed-unsorted-subjects":
                Single(transformations, "bare-prefix-identifier-dropped")["subjects"] =
                    new JsonArray("NGC7437:HD", "NGC0234:HD", "NGC7659:HD");
                break;
            case "composed-listed-large-count":
                Single(transformations, "duplicate-resolved")["subjects"] = new JsonArray("M31->NGC0224");
                break;
            case "composed-control-character":
                components[1]!["name"] = "Open\u0007NGC";
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
        var version = $"\"registryVersion\":{Invariant(ApprovedCatalogSpecifications.RegistryVersion)}";
        var original = EmbeddedRegistry().ToJsonString();
        var text = original.Replace(version, $"{version},{version}", StringComparison.Ordinal);

        Assert.AreNotEqual(original, text);
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(text));
    }

    private static JsonObject Single(JsonArray transformations, string id)
        => transformations.Single(item => item!["id"]!.GetValue<string>() == id)!.AsObject();

    private static string[] ScriptPaths(string[] scripts)
        => [.. scripts.Select(static script => Path.Combine(RepositoryRoot(), "scripts", "catalog", script))];

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

    /// <summary>
    /// Reads the literal <c>readonly NAME="value"</c> constants of scripts sourced in the given order. A reference to
    /// a constant defined earlier is expanded; a reference to a computed variable such as a script directory is kept
    /// literally, because no compared constant depends on it.
    /// </summary>
    private static Dictionary<string, string> ReadShellConstants(string[] paths, string prefix)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in paths.SelectMany(File.ReadLines))
        {
            var match = ReadonlyAssignment().Match(line);
            if (!match.Success) continue;
            values[match.Groups["name"].Value] = ShellReference().Replace(
                match.Groups["value"].Value,
                reference => values.TryGetValue(reference.Groups["name"].Value, out var value) ? value : reference.Value);
        }
        return values
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(item => item.Key[prefix.Length..], item => item.Value, StringComparer.Ordinal);
    }

    /// <summary>Reads the double-quoted elements of a <c>readonly -a NAME=(...)</c> array, one per line.</summary>
    private static string[] ReadShellArray(string path, string name)
    {
        var lines = File.ReadAllLines(path);
        var start = Array.IndexOf(lines, $"readonly -a {name}=(");
        Assert.IsGreaterThanOrEqualTo(0, start, $"{name} is not defined in {path}.");
        return
        [
            .. lines.Skip(start + 1)
                .TakeWhile(static line => line != ")")
                .Select(static line => ShellArrayElement().Match(line))
                .Select(static match => match.Success ? match.Groups["value"].Value : throw new InvalidDataException(
                    $"Unexpected shell array element: {match.Value}"))
        ];
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

    [GeneratedRegex("^    \"(?<value>[^\"]*)\"$")]
    private static partial Regex ShellArrayElement();
}
