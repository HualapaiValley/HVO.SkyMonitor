using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

/// <summary>
/// Schema 3 (HYG 4.4, issue #521) reader and resolver contracts over the checked-in subset fixture, plus guards that
/// schema 2 (HYG 4.2) keeps exactly its prior meaning.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
internal sealed class HygV44CatalogTests
{
    private const string FixtureChecksum = "7912c279a5c6fd3fc26fceb35987b6caa72425232e65db991f28d657ba70b682";
    private const string FixtureCatalogId = "hyg-v44-fixture";
    private const string SnapshotVersion = "hyg-v44-fixture-1";
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v44-subset.sqlite");
    private static readonly string LegacyFixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite");
    private static readonly string[] ThetaOneOrionis = ["26155", "26156", "26159"];

    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
        => Assert.IsNotNull(new HygV44CatalogTests());

    [TestMethod]
    public void FixtureChecksumMatchesTheRecordedDerivation()
        => Assert.AreEqual(FixtureChecksum, Checksum(FixturePath));

    [TestMethod]
    public void SchemaThreeExposesItsDeclaredSemantics()
    {
        var catalog = CreateCatalog();

        Assert.AreEqual(16, catalog.ObjectCount);
        Assert.AreEqual("3", catalog.Metadata.SchemaVersion);
        Assert.AreEqual("4", catalog.PreprocessingVersion);
        Assert.AreEqual(
            new CatalogSemantics(
                "equatorial-j2000-icrs-aligned", "J2000.0", "J2000.0", "V-heterogeneous", "B-V",
                "mu-alpha-cos-delta-and-mu-delta-mas-per-year"),
            catalog.Semantics);
    }

    [TestMethod]
    public void SchemaThreePreservesProperMotionAndDesignations()
    {
        var catalog = CreateCatalog();

        Assert.IsTrue(catalog.TryGetDetails("103879", out var cygni));
        Assert.AreEqual(new CatalogProperMotion(4155.1, 3258.9), cygni.ProperMotion);
        Assert.AreEqual(
            new CelestialObjectDesignations(
                Flamsteed: "61", Constellation: "Cyg", BayerFlamsteed: "61    Cyg", HenryDraperId: "201091",
                HarvardRevisedId: "8085", GlieseId: "Gl 820A"),
            cygni.Designations);
        Assert.IsTrue(catalog.TryGetDetails("32263", out var sirius));
        Assert.AreEqual(new CatalogProperMotion(-546.01, -1223.08), sirius.ProperMotion);
        Assert.AreEqual("Sirius", sirius.Designations.ProperName);
        Assert.AreEqual("Alp", sirius.Designations.Bayer);
        Assert.IsFalse(catalog.TryGetDetails("0", out _));
    }

    [TestMethod]
    public void SchemaThreeRecordsUnknownProperMotionAsNull()
    {
        var catalog = CreateCatalog();

        Assert.IsTrue(catalog.TryGetDetails("87665", out var barnard));
        Assert.IsNull(barnard.ProperMotion);
        Assert.AreEqual("Barnard's Star", barnard.Designations.ProperName);
        Assert.AreEqual("Gl 699", barnard.Designations.GlieseId);
    }

    [TestMethod]
    [DataRow("The-1 Ori", "bayer")]
    [DataRow("41 Ori", "flamsteed")]
    [DataRow("  the-1 ori ", "bayer")]
    public void FindByAliasReturnsEveryObjectASharedDesignationNames(string designation, string kind)
    {
        var catalog = CreateCatalog();

        var matches = catalog.FindByAlias(designation);

        CollectionAssert.AreEqual(ThetaOneOrionis, matches.Select(static item => item.ObjectId).ToArray());
        Assert.IsTrue(matches.All(item => item.Kind == kind));
    }

    [TestMethod]
    public void FindByAliasDistinguishesUniqueAndUnknownDesignations()
    {
        var catalog = CreateCatalog();

        var unique = catalog.FindByAlias("HD 37022");

        Assert.HasCount(1, unique);
        Assert.AreEqual(new CelestialCatalogAlias("HD 37022", "26156", "hd"), unique[0]);
        Assert.IsEmpty(catalog.FindByAlias("HD 0"));
        Assert.ThrowsExactly<ArgumentNullException>(() => catalog.FindByAlias(null!));
    }

    [TestMethod]
    public void SchemaTwoKeepsItsLegacyMeaning()
    {
        var catalog = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            LegacyFixturePath, Checksum(LegacyFixturePath), "2", "3"));

        Assert.AreSame(CatalogSemantics.LegacyFixedJ2000, catalog.Semantics);
        Assert.IsFalse(catalog.TryGetDetails("32263", out _));
        Assert.IsEmpty(catalog.FindByAlias("Sirius"));
    }

    [TestMethod]
    public void SchemaVersionsCannotBeSubstitutedForEachOther()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(FixturePath, FixtureChecksum, "2", "3")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(FixturePath, FixtureChecksum, "3", "3")));
        Assert.ThrowsExactly<InvalidDataException>(() => _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            LegacyFixturePath, Checksum(LegacyFixturePath), "3", "4")));
    }

    [TestMethod]
    [DataRow("UPDATE catalog_metadata SET value = 'equatorial-j2000' WHERE key = 'coordinate_frame'", DisplayName = "frame")]
    [DataRow("UPDATE catalog_metadata SET value = 'J1991.25' WHERE key = 'coordinate_epoch'", DisplayName = "epoch")]
    [DataRow("UPDATE catalog_metadata SET value = 'mas-per-year' WHERE key = 'proper_motion_convention'", DisplayName = "convention")]
    [DataRow("UPDATE catalog_metadata SET value = 'zero-is-known' WHERE key = 'proper_motion_unknown_rule'", DisplayName = "unknown-rule")]
    [DataRow("UPDATE catalog_metadata SET value = '53E3DF311869E813ACE5F1AD2EC4CE909F13256C' WHERE key = 'source_commit'", DisplayName = "commit")]
    [DataRow("DELETE FROM catalog_metadata WHERE key = 'magnitude_band'", DisplayName = "missing-key")]
    [DataRow("INSERT INTO catalog_metadata VALUES ('extra', 'value')", DisplayName = "extra-key")]
    [DataRow("UPDATE celestial_objects SET proper_motion_dec_mas_per_year = NULL WHERE id = '103879'", DisplayName = "half-motion")]
    [DataRow("UPDATE celestial_objects SET gliese_id = ' Gl 699' WHERE id = '87665'", DisplayName = "untrimmed-designation")]
    [DataRow("INSERT INTO celestial_object_aliases VALUES ('Missing', '1', 'hd')", DisplayName = "dangling-alias")]
    [DataRow("INSERT INTO celestial_object_aliases VALUES ('Dog Star', '32263', 'nickname')", DisplayName = "untyped-alias")]
    [DataRow("INSERT INTO celestial_object_aliases VALUES (' Sirius', '32263', 'proper')", DisplayName = "untrimmed-alias")]
    [DataRow("PRAGMA user_version = 2", DisplayName = "user-version")]
    public void SchemaThreeRefusesContradictorySemanticsAndRows(string mutation)
    {
        var path = CopyFixture();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Statements are constants supplied only by this test class.
                command.CommandText = mutation;
#pragma warning restore CA2100
                command.ExecuteNonQuery();
            }

            Assert.ThrowsExactly<InvalidDataException>(() =>
                _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(path, Checksum(path), "3", "4")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ResolverLoadsSchemaThreeFixturePackage()
    {
        using var installation = CreateInstallation("hyg_v44.sqlite", "3", "4");

        var result = Resolve(installation.Root);

        Assert.AreEqual(FixtureCatalogId, result.CatalogId);
        Assert.AreEqual("3", result.SchemaVersion);
        Assert.AreEqual("4", result.PreprocessingVersion);
        Assert.AreEqual(16, result.RowCount);
        Assert.IsInstanceOfType<ICelestialCatalogDetailsSource>(result.Catalog);
        Assert.HasCount(3, ((ICelestialCatalogDetailsSource)result.Catalog).FindByAlias("The-1 Ori"));
    }

    [TestMethod]
    [DataRow("hyg_v42.sqlite", "3", "4", DisplayName = "wrong-database-name")]
    [DataRow("hyg_v44.sqlite", "2", "3", DisplayName = "schema-two-pair")]
    [DataRow("hyg_v44.sqlite", "3", "3", DisplayName = "unsupported-pair")]
    [DataRow("hyg_v44.sqlite", "2", "4", DisplayName = "unsupported-schema")]
    public void ResolverRefusesASchemaThreePackageThatClaimsAnotherFormat(
        string databaseFile, string schemaVersion, string preprocessingVersion)
    {
        using var installation = CreateInstallation(databaseFile, schemaVersion, preprocessingVersion);

        Assert.ThrowsExactly<InvalidDataException>(() => Resolve(installation.Root));
    }

    private static SqliteCelestialCatalog CreateCatalog()
        => new(new SqliteCelestialCatalogOptions(FixturePath, FixtureChecksum, "3", "4"));

    private static CatalogSnapshotResult Resolve(string root)
        => CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(root, FixtureCatalogId)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture
        });

    private static Installation CreateInstallation(string databaseFile, string schemaVersion, string preprocessingVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-v44-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(root, "versions", SnapshotVersion);
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateSymbolicLink(Path.Combine(root, "current"), $"versions/{SnapshotVersion}");
        var databasePath = Path.Combine(versionDirectory, databaseFile);
        File.Copy(FixturePath, databasePath);
        File.WriteAllText(Path.Combine(versionDirectory, "manifest.json"), $$"""
            {
              "manifestVersion": 2,
              "package": { "kind": "fixture", "version": "{{SnapshotVersion}}" },
              "catalog": { "id": "{{FixtureCatalogId}}", "name": "HYG 4.4 subset test fixture", "version": "4.4-fixture.1" },
              "schemaVersion": "{{schemaVersion}}",
              "preprocessingVersion": "{{preprocessingVersion}}",
              "database": {
                "relativePath": "{{databaseFile}}",
                "sha256": "{{FixtureChecksum}}",
                "length": {{new FileInfo(databasePath).Length}},
                "rowCount": 16
              }
            }
            """);
        return new Installation(root);
    }

    private static string CopyFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvo-catalog-v44-{Guid.NewGuid():N}.sqlite");
        File.Copy(FixturePath, path);
        return path;
    }

    private static string Checksum(string path)
    {
        using var source = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(source));
    }

    private sealed record Installation(string Root) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
