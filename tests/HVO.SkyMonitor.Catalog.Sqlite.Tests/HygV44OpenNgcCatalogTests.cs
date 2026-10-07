using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

/// <summary>
/// Schema 4 (HYG 4.4 stars plus OpenNGC deep sky, issue #525) reader and resolver contracts over the checked-in
/// composed subset fixture, plus guards that schemas 2 and 3 cannot stand in for it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
internal sealed class HygV44OpenNgcCatalogTests
{
    private const string FixtureChecksum = "1459299e1592b0770e6fef263f8abbe028796c7f89e989e19de8d162a22bf6e0";
    private const string FixtureCatalogId = "hyg-v44-openngc-fixture";
    private const string SnapshotVersion = "hyg-v44-openngc-fixture-1";
    private const string DatabaseFile = "hyg_v44_openngc.sqlite";
    private const int StarCount = 17;
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v44-openngc-subset.sqlite");
    private static readonly string SchemaThreeFixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v44-subset.sqlite");

    // Eight objects, two tombstones, and outlines for NGC 650 (levels 1-3, two rings at level 3) and NGC 2023
    // (levels 2-3); SOURCE-v44-openngc.md records the selection.
    private static readonly CatalogDeepSkyCounts FixtureCounts =
        new(8, 59, 2, 2, 5, 6, 679);
    private static readonly string[] FixtureObjectIds =
        ["M040", "NGC0224", "NGC0253", "NGC0650", "NGC1990", "NGC2023", "NGC5457", "NGC5866"];
    private static readonly int[] LittleDumbbellLevels = [1, 2, 3];
    private static readonly int[] LittleDumbbellRingCounts = [1, 1, 2];
    private static readonly int[] ReflectionLevels = [2, 3];
    private static readonly CelestialCollectionKind[] AlnilamCollections =
        [CelestialCollectionKind.Star, CelestialCollectionKind.DeepSky];

    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
        => Assert.IsNotNull(new HygV44OpenNgcCatalogTests());

    [TestMethod]
    public void FixtureChecksumMatchesTheRecordedDerivation()
        => Assert.AreEqual(FixtureChecksum, Checksum(FixturePath));

    [TestMethod]
    public void SchemaFourKeepsTheSchemaThreeStarsAndDeclaresItsDeepSkySemantics()
    {
        var catalog = CreateCatalog();

        Assert.AreEqual(StarCount, catalog.ObjectCount);
        Assert.AreEqual("4", catalog.Metadata.SchemaVersion);
        Assert.AreEqual("5", catalog.PreprocessingVersion);
        Assert.AreEqual(
            new CatalogSemantics(
                "equatorial-j2000-icrs-aligned", "J2000.0", "J2000.0", "V-heterogeneous", "B-V",
                "mu-alpha-cos-delta-and-mu-delta-mas-per-year"),
            catalog.Semantics);
        Assert.IsNotNull(catalog.DeepSky);
        Assert.AreEqual(
            new DeepSkySemantics(
                "OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
                new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned",
                "J2000.0", "arcminute", "degrees-north-through-east-0-inclusive-to-180-exclusive",
                "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote"),
            catalog.DeepSky.Semantics);
        Assert.AreSame(catalog.DeepSky, ((IDeepSkyCatalogSource)catalog).DeepSky);
    }

    [TestMethod]
    public void SchemaFourLoadsExactlyTheDeclaredDeepSkyCounts()
    {
        var catalog = CreateCatalog();

        Assert.AreEqual(FixtureCounts, Counts(catalog.DeepSky!));
        CollectionAssert.AreEqual(FixtureObjectIds, catalog.DeepSky!.Objects.Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    public void AMessierDesignationResolvesToItsNgcObject()
    {
        var deepSky = CreateCatalog().DeepSky!;

        Assert.AreEqual(new DeepSkyAlias("M31", "NGC0224", DeepSkyAliasKinds.Messier), deepSky.FindByAlias("m31").Single());
        Assert.IsTrue(deepSky.TryGetObject("NGC0224", out var andromeda));
        Assert.AreEqual("NGC 224", andromeda.Designation);
        Assert.AreEqual("M31", andromeda.DisplayName);
        Assert.AreEqual(31, andromeda.MessierNumber);
        Assert.AreEqual("Andromeda Galaxy", andromeda.CommonName);
        Assert.AreEqual("G", andromeda.ObjectType);
        Assert.AreEqual("And", andromeda.Constellation);
        Assert.AreEqual(0 + 42 / 60.0 + 44.35 / 3600.0, andromeda.RightAscensionHours, 1e-12);
        Assert.AreEqual(41 + 16 / 60.0 + 8.6 / 3600.0, andromeda.DeclinationDegrees, 1e-12);
        Assert.AreEqual(177.83, andromeda.MajorAxisArcminutes);
        Assert.AreEqual(69.66, andromeda.MinorAxisArcminutes);
        Assert.AreEqual(35.0, andromeda.PositionAngleDegrees);
        Assert.IsTrue(andromeda.HasOrientedExtent);
        Assert.IsEmpty(deepSky.GetOutlines("NGC0224"));
    }

    [TestMethod]
    public void TheDisputedMessier102IsAFlaggedAliasOfM101Only()
    {
        var deepSky = CreateCatalog().DeepSky!;

        var disputed = deepSky.FindByAlias("M102").Single();
        Assert.AreEqual(new DeepSkyAlias("M102", "NGC5457", DeepSkyAliasKinds.Disputed), disputed);
        Assert.IsTrue(disputed.IsDisputed);
        Assert.IsTrue(deepSky.TryGetObject("NGC5457", out var pinwheel));
        Assert.AreEqual("M101", pinwheel.DisplayName);
        Assert.AreEqual(101, pinwheel.MessierNumber);
        Assert.IsTrue(deepSky.TryGetObject("NGC5866", out var spindle));
        Assert.AreEqual("NGC 5866", spindle.DisplayName);
        Assert.IsNull(spindle.MessierNumber);
        Assert.AreEqual(126.0, spindle.PositionAngleDegrees);
        Assert.IsFalse(Aliases(FixturePath).Any(static item =>
            string.Equals(item.Alias, "M102", StringComparison.OrdinalIgnoreCase) && item.ObjectId != "NGC5457"));
    }

    [TestMethod]
    public void StellarRowsCarryIdentityAndTheirMessierLabel()
    {
        var deepSky = CreateCatalog().DeepSky!;

        Assert.IsTrue(deepSky.TryGetObject("M040", out var winnecke));
        Assert.AreEqual(DeepSkyObjectTypes.DoubleStar, winnecke.ObjectType);
        Assert.IsTrue(winnecke.IsStellar);
        Assert.AreEqual("M40", winnecke.Designation);
        Assert.AreEqual("M40", winnecke.DisplayName);
        Assert.AreEqual(40, winnecke.MessierNumber);
        Assert.AreEqual(new DeepSkyAlias("M40", "M040", DeepSkyAliasKinds.Designation), deepSky.FindByAlias("M40").Single());
        Assert.IsTrue(deepSky.TryGetObject("NGC1990", out var alnilam));
        Assert.AreEqual(DeepSkyObjectTypes.Star, alnilam.ObjectType);
        Assert.IsTrue(alnilam.IsStellar);
        Assert.AreEqual("26311", alnilam.HipparcosId);
        Assert.AreEqual("Alnilam", alnilam.CommonName);
        Assert.AreEqual(
            new DeepSkyAlias("HIP 26311", "NGC1990", DeepSkyAliasKinds.Hipparcos), deepSky.FindByAlias("HIP 26311").Single());
    }

    [TestMethod]
    public void ExtentsWithoutAPositionAngleAreNotOriented()
    {
        var deepSky = CreateCatalog().DeepSky!;

        Assert.IsTrue(deepSky.TryGetObject("NGC0650", out var littleDumbbell));
        Assert.AreEqual("M76", littleDumbbell.DisplayName);
        Assert.AreEqual(1.12, littleDumbbell.MajorAxisArcminutes);
        Assert.IsNull(littleDumbbell.MinorAxisArcminutes);
        Assert.IsNull(littleDumbbell.PositionAngleDegrees);
        Assert.IsFalse(littleDumbbell.HasOrientedExtent);
        Assert.IsTrue(deepSky.TryGetObject("NGC2023", out var reflection));
        Assert.AreEqual(10.0, reflection.MajorAxisArcminutes);
        Assert.AreEqual(8.0, reflection.MinorAxisArcminutes);
        Assert.IsNull(reflection.PositionAngleDegrees);
        Assert.IsFalse(reflection.HasOrientedExtent);
    }

    [TestMethod]
    public void CaldwellDuplicateAndCommonAliasesNameTheirLiveObject()
    {
        var deepSky = CreateCatalog().DeepSky!;

        Assert.AreEqual(new DeepSkyAlias("C65", "NGC0253", DeepSkyAliasKinds.Caldwell), deepSky.FindByAlias("c65").Single());
        Assert.IsTrue(deepSky.TryGetObject("NGC0253", out var sculptor));
        Assert.AreEqual(65, sculptor.CaldwellNumber);
        Assert.AreEqual("Sculptor Filament", sculptor.CommonName);
        Assert.AreEqual(new DeepSkyAlias("Silver Coin", "NGC0253", DeepSkyAliasKinds.Common), deepSky.FindByAlias("Silver Coin").Single());
        Assert.AreEqual(new DeepSkyAlias("NGC 651", "NGC0650", DeepSkyAliasKinds.Duplicate), deepSky.FindByAlias("NGC 651").Single());
        Assert.IsFalse(deepSky.TryGetObject("NGC0651", out _));
    }

    [TestMethod]
    public void OutlinesAreClosedRingsOrderedByLevel()
    {
        var deepSky = CreateCatalog().DeepSky!;

        var littleDumbbell = deepSky.GetOutlines("NGC0650");
        CollectionAssert.AreEqual(LittleDumbbellLevels, littleDumbbell.Select(static item => item.Level).ToArray());
        CollectionAssert.AreEqual(LittleDumbbellRingCounts, littleDumbbell.Select(static item => item.Rings.Count).ToArray());
        var reflection = deepSky.GetOutlines("NGC2023");
        CollectionAssert.AreEqual(ReflectionLevels, reflection.Select(static item => item.Level).ToArray());
        foreach (var ring in littleDumbbell.Concat(reflection).SelectMany(static item => item.Rings))
        {
            Assert.IsGreaterThanOrEqualTo(DeepSkyCatalog.MinimumRingPointCount, ring.Points.Count);
            Assert.AreEqual(ring.Points[0], ring.Points[^1]);
        }
        Assert.IsEmpty(deepSky.GetOutlines("M040"));
    }

    [TestMethod]
    public void TombstonesAreNeitherLiveNorAliased()
    {
        var deepSky = CreateCatalog().DeepSky!;

        Assert.IsTrue(deepSky.TryGetTombstone("IC0067", out var tombstone));
        Assert.AreEqual("IC 67", tombstone.Designation);
        Assert.AreEqual(DeepSkyTombstoneReasons.Nonexistent, tombstone.Reason);
        Assert.IsEmpty(tombstone.Candidates);
        Assert.IsTrue(deepSky.TryGetTombstone("NGC0412", out _));
        Assert.IsFalse(deepSky.TryGetObject("IC0067", out _));
        Assert.IsEmpty(deepSky.FindByAlias("IC 67"));
        Assert.IsFalse(deepSky.TryGetTombstone("NGC0224", out _));
    }

    [TestMethod]
    public void FindByAliasReturnsExactlyTheStoredDeepSkyAliasTable()
    {
        var deepSky = CreateCatalog().DeepSky!;
        var stored = Aliases(FixturePath);

        Assert.HasCount(checked((int)FixtureCounts.AliasCount), stored);
        foreach (var group in stored.GroupBy(static item => item.Alias, StringComparer.OrdinalIgnoreCase))
        {
            var expected = group
                .OrderBy(static item => item.ObjectId, StringComparer.Ordinal)
                .ThenBy(static item => item.Alias, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(expected, deepSky.FindByAlias(group.Key).ToArray(), group.Key);
        }
    }

    [TestMethod]
    public void SearchFindsTheStarBeforeItsStellarDeepSkyRow()
    {
        var catalog = CreateCatalog();
        var search = new CelestialObjectSearch(catalog, catalog);

        var alnilam = search.Find("Alnilam");

        CollectionAssert.AreEqual(AlnilamCollections, alnilam.Select(static item => item.Collection).ToArray());
        Assert.AreEqual("NGC1990", alnilam[1].ObjectId);
        Assert.AreEqual(
            new CelestialSearchMatch(CelestialCollectionKind.DeepSky, "NGC0224", "M31", DeepSkyAliasKinds.Messier, false),
            search.Find("M31").Single());
        Assert.IsTrue(search.Find("M102").Single().Disputed);
    }

    [TestMethod]
    public void EarlierSchemasHaveNoDeepSkyCollection()
    {
        var catalog = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            SchemaThreeFixturePath, Checksum(SchemaThreeFixturePath), "3", "4"));

        Assert.IsNull(catalog.DeepSky);
        Assert.IsNull(((IDeepSkyCatalogSource)catalog).DeepSky);
        Assert.IsEmpty(new CelestialObjectSearch(catalog, catalog).Find("M31"));
    }

    [TestMethod]
    public void SchemaVersionsCannotBeSubstitutedForEachOther()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(FixturePath, FixtureChecksum, "3", "4")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(FixturePath, FixtureChecksum, "4", "4")));
        Assert.ThrowsExactly<InvalidDataException>(() => _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            SchemaThreeFixturePath, Checksum(SchemaThreeFixturePath), "4", "5")));
    }

    [TestMethod]
    public void DeclaredDeepSkyCountsMustMatchTheTables()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            FixturePath, FixtureChecksum, "4", "5", ExpectedDeepSkyCounts: FixtureCounts with
            {
                OutlinePointCount = FixtureCounts.OutlinePointCount + 1
            })));
        Assert.ThrowsExactly<ArgumentException>(() => _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            SchemaThreeFixturePath, Checksum(SchemaThreeFixturePath), "3", "4", ExpectedDeepSkyCounts: FixtureCounts)));
    }

    [TestMethod]
    [DataRow("UPDATE catalog_metadata SET value = 'equatorial-j2000' WHERE key = 'deep_sky_coordinate_frame'", DisplayName = "frame")]
    [DataRow("UPDATE catalog_metadata SET value = 'J2000.5' WHERE key = 'deep_sky_coordinate_epoch'", DisplayName = "epoch")]
    [DataRow("UPDATE catalog_metadata SET value = 'arcsecond' WHERE key = 'deep_sky_axis_unit'", DisplayName = "axis-unit")]
    [DataRow("UPDATE catalog_metadata SET value = 'degrees-east-of-north-0-to-360' WHERE key = 'deep_sky_position_angle_convention'", DisplayName = "position-angle-convention")]
    [DataRow("UPDATE catalog_metadata SET value = '1-narrowest-3-widest' WHERE key = 'deep_sky_outline_level_convention'", DisplayName = "level-convention")]
    [DataRow("UPDATE catalog_metadata SET value = 'v-mag-per-square-arcsecond' WHERE key = 'deep_sky_surface_brightness_unit'", DisplayName = "surface-brightness-unit")]
    [DataRow("UPDATE catalog_metadata SET value = 'http://github.com/mattiaverga/OpenNGC' WHERE key = 'deep_sky_source_url'", DisplayName = "source-url")]
    [DataRow("UPDATE catalog_metadata SET value = '36CB178A0F69DBA8BFC03A99C10512831EDF1C6B' WHERE key = 'deep_sky_source_commit'", DisplayName = "commit")]
    [DataRow("DELETE FROM catalog_metadata WHERE key = 'deep_sky_license'", DisplayName = "missing-key")]
    [DataRow("INSERT INTO catalog_metadata VALUES ('deep_sky_extra', 'value')", DisplayName = "extra-key")]
    [DataRow("UPDATE deep_sky_objects SET object_type = 'Gal' WHERE id = 'NGC0224'", DisplayName = "object-type")]
    [DataRow("UPDATE deep_sky_objects SET position_angle_degrees = 180 WHERE id = 'NGC0224'", DisplayName = "position-angle-180")]
    [DataRow("UPDATE deep_sky_objects SET minor_axis_arcminutes = 200 WHERE id = 'NGC0224'", DisplayName = "minor-over-major")]
    [DataRow("UPDATE deep_sky_objects SET major_axis_arcminutes = NULL WHERE id = 'NGC2023'", DisplayName = "minor-without-major")]
    [DataRow("UPDATE deep_sky_objects SET display_name = 'NGC 224' WHERE id = 'NGC0224'", DisplayName = "messier-display")]
    [DataRow("UPDATE deep_sky_objects SET hipparcos_id = '26311' WHERE id = 'NGC2023'", DisplayName = "repeated-hipparcos")]
    [DataRow("UPDATE deep_sky_objects SET hipparcos_id = '026311' WHERE id = 'NGC1990'", DisplayName = "padded-hipparcos")]
    [DataRow("UPDATE deep_sky_objects SET right_ascension_hours = 24 WHERE id = 'M040'", DisplayName = "ra-24")]
    [DataRow("INSERT INTO deep_sky_aliases VALUES ('NGC 9999', 'NGC9999', 'identifier')", DisplayName = "dangling-alias")]
    [DataRow("INSERT INTO deep_sky_aliases VALUES ('Andromeda Nebula', 'NGC0224', 'nickname')", DisplayName = "untyped-alias")]
    [DataRow("INSERT INTO deep_sky_aliases VALUES (' Andromeda Nebula', 'NGC0224', 'common')", DisplayName = "untrimmed-alias")]
    [DataRow("DELETE FROM deep_sky_aliases WHERE alias = 'NGC 224'", DisplayName = "missing-designation")]
    [DataRow("UPDATE deep_sky_aliases SET alias = 'NGC 0224' WHERE alias = 'NGC 224' AND kind = 'designation'", DisplayName = "wrong-designation")]
    [DataRow("UPDATE deep_sky_tombstones SET reason = 'gone' WHERE id = 'IC0067'", DisplayName = "tombstone-reason")]
    [DataRow("UPDATE deep_sky_tombstones SET reason = 'duplicate-conflict' WHERE id = 'IC0067'", DisplayName = "conflict-without-candidates")]
    [DataRow("INSERT INTO deep_sky_tombstones VALUES ('NGC0224', 'NGC 224', 'nonexistent', NULL)", DisplayName = "live-tombstone")]
    [DataRow("DELETE FROM deep_sky_outline_points WHERE object_id = 'NGC2023' AND level = 2 AND ring = 0 AND sequence = 1", DisplayName = "sequence-gap")]
    [DataRow("UPDATE deep_sky_outline_points SET ring = 2 WHERE object_id = 'NGC0650' AND level = 3 AND ring = 1", DisplayName = "ring-gap")]
    [DataRow("UPDATE deep_sky_outline_points SET level = 4 WHERE object_id = 'NGC2023' AND level = 3", DisplayName = "level-4")]
    [DataRow("UPDATE deep_sky_outline_points SET declination_degrees = declination_degrees + 0.01 WHERE object_id = 'NGC2023' AND level = 2 AND ring = 0 AND sequence = 0", DisplayName = "open-ring")]
    [DataRow("UPDATE deep_sky_outline_points SET object_id = 'NGC9999' WHERE object_id = 'NGC2023'", DisplayName = "dangling-outline")]
    [DataRow("CREATE INDEX deep_sky_aliases_object ON deep_sky_aliases(object_id)", DisplayName = "extra-index")]
    [DataRow("DROP TABLE deep_sky_tombstones", DisplayName = "missing-table")]
    [DataRow("ALTER TABLE deep_sky_objects ADD COLUMN extra TEXT", DisplayName = "extra-column")]
    [DataRow("UPDATE celestial_objects SET gliese_id = ' Gl 699' WHERE id = '87665'", DisplayName = "star-rule")]
    [DataRow("PRAGMA user_version = 3", DisplayName = "user-version")]
    public void SchemaFourRefusesContradictorySemanticsAndRows(string mutation)
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
                _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(path, Checksum(path), "4", "5")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ResolverLoadsAVersionThreeFixturePackage()
    {
        using var installation = CreateInstallation();

        var result = Resolve(installation.Root);

        Assert.AreEqual(CatalogSnapshotResolver.ComposedManifestVersion, result.ManifestVersion);
        Assert.AreEqual("explicit-manifest-v3", result.CatalogIdentitySource);
        Assert.AreEqual(FixtureCatalogId, result.CatalogId);
        Assert.AreEqual("4", result.SchemaVersion);
        Assert.AreEqual("5", result.PreprocessingVersion);
        Assert.AreEqual(StarCount, result.RowCount);
        Assert.AreEqual(FixtureCounts, result.DeepSkyCounts);
        Assert.AreEqual(FixtureCounts, Counts(((SqliteCelestialCatalog)result.Catalog).DeepSky!));
    }

    [TestMethod]
    [DataRow(2, DatabaseFile, "4", "5", true, DisplayName = "version-two-with-deep-sky")]
    [DataRow(2, DatabaseFile, "4", "5", false, DisplayName = "version-two-schema-four")]
    [DataRow(3, DatabaseFile, "4", "5", false, DisplayName = "version-three-without-deep-sky")]
    [DataRow(3, "hyg_v44.sqlite", "4", "5", true, DisplayName = "wrong-database-name")]
    [DataRow(3, DatabaseFile, "3", "4", true, DisplayName = "schema-three-pair")]
    [DataRow(3, DatabaseFile, "4", "4", true, DisplayName = "unsupported-pair")]
    [DataRow(4, DatabaseFile, "4", "5", true, DisplayName = "unsupported-manifest-version")]
    public void ResolverRefusesASchemaFourPackageThatClaimsAnotherFormat(
        int manifestVersion, string databaseFile, string schemaVersion, string preprocessingVersion, bool deepSky)
    {
        using var installation = CreateInstallation(
            manifestVersion, databaseFile, schemaVersion, preprocessingVersion, declareDeepSky: deepSky);

        Assert.ThrowsExactly<InvalidDataException>(() => Resolve(installation.Root));
    }

    [TestMethod]
    [DataRow(1, 0, 0, 0, 0, 0, 0, DisplayName = "objects")]
    [DataRow(0, 1, 0, 0, 0, 0, 0, DisplayName = "aliases")]
    [DataRow(0, 0, 1, 0, 0, 0, 0, DisplayName = "tombstones")]
    [DataRow(0, 0, 0, 1, 0, 0, 0, DisplayName = "outline-objects")]
    [DataRow(0, 0, 0, 0, 1, 0, 0, DisplayName = "outline-sets")]
    [DataRow(0, 0, 0, 0, 0, 1, 0, DisplayName = "outline-rings")]
    [DataRow(0, 0, 0, 0, 0, 0, -1, DisplayName = "outline-points")]
    public void ResolverRefusesAManifestWhoseDeepSkyCountsDiffer(
        int objects, int aliases, int tombstones, int outlineObjects, int outlineSets, int outlineRings, int outlinePoints)
    {
        using var installation = CreateInstallation(deepSkyCounts: new CatalogDeepSkyCounts(
            FixtureCounts.ObjectCount + objects,
            FixtureCounts.AliasCount + aliases,
            FixtureCounts.TombstoneCount + tombstones,
            FixtureCounts.OutlineObjectCount + outlineObjects,
            FixtureCounts.OutlineSetCount + outlineSets,
            FixtureCounts.OutlineRingCount + outlineRings,
            FixtureCounts.OutlinePointCount + outlinePoints));

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => Resolve(installation.Root));

        StringAssert.Contains(exception.Message, "deep-sky counts mismatch", StringComparison.Ordinal);
    }

    internal static CatalogSnapshotResult Resolve(string root)
        => CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(root, FixtureCatalogId)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture
        });

    /// <summary>
    /// Installs the composed fixture with a fixture manifest. The defaults describe it truthfully; the manifest declares
    /// <c>database.deepSky</c> when <paramref name="declareDeepSky"/> is set, by default for manifest version 3.
    /// </summary>
    internal static Installation CreateInstallation(
        int manifestVersion = CatalogSnapshotResolver.ComposedManifestVersion,
        string databaseFile = DatabaseFile,
        string schemaVersion = "4",
        string preprocessingVersion = "5",
        bool? declareDeepSky = null,
        CatalogDeepSkyCounts? deepSkyCounts = null)
    {
        deepSkyCounts = declareDeepSky ?? manifestVersion == CatalogSnapshotResolver.ComposedManifestVersion
            ? deepSkyCounts ?? FixtureCounts
            : null;
        var root = Path.Combine(Path.GetTempPath(), $"hvo-catalog-v44-openngc-{Guid.NewGuid():N}");
        var versionDirectory = Path.Combine(root, "versions", SnapshotVersion);
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateSymbolicLink(Path.Combine(root, "current"), $"versions/{SnapshotVersion}");
        var databasePath = Path.Combine(versionDirectory, databaseFile);
        File.Copy(FixturePath, databasePath);
        var deepSky = deepSkyCounts is { } counts
            ? $$"""
              ,
                "deepSky": {
                  "objectCount": {{counts.ObjectCount}},
                  "aliasCount": {{counts.AliasCount}},
                  "tombstoneCount": {{counts.TombstoneCount}},
                  "outlineObjectCount": {{counts.OutlineObjectCount}},
                  "outlineSetCount": {{counts.OutlineSetCount}},
                  "outlineRingCount": {{counts.OutlineRingCount}},
                  "outlinePointCount": {{counts.OutlinePointCount}}
                }
              """
            : string.Empty;
        File.WriteAllText(Path.Combine(versionDirectory, "manifest.json"), $$"""
            {
              "manifestVersion": {{manifestVersion}},
              "package": { "kind": "fixture", "version": "{{SnapshotVersion}}" },
              "catalog": { "id": "{{FixtureCatalogId}}", "name": "HYG 4.4 + OpenNGC subset test fixture", "version": "4.4+openngc-fixture.1" },
              "schemaVersion": "{{schemaVersion}}",
              "preprocessingVersion": "{{preprocessingVersion}}",
              "database": {
                "relativePath": "{{databaseFile}}",
                "sha256": "{{FixtureChecksum}}",
                "length": {{new FileInfo(databasePath).Length}},
                "rowCount": {{StarCount}}{{deepSky}}
              }
            }
            """);
        return new Installation(root);
    }

    private static SqliteCelestialCatalog CreateCatalog()
        => new(new SqliteCelestialCatalogOptions(FixturePath, FixtureChecksum, "4", "5", ExpectedDeepSkyCounts: FixtureCounts));

    private static CatalogDeepSkyCounts Counts(DeepSkyCatalog deepSky)
        => new(
            deepSky.Objects.Count,
            deepSky.AliasCount,
            deepSky.Tombstones.Count,
            deepSky.OutlineObjectCount,
            deepSky.OutlineCount,
            deepSky.OutlineRingCount,
            deepSky.OutlinePointCount);

    private static List<DeepSkyAlias> Aliases(string path)
    {
        var result = new List<DeepSkyAlias>();
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias, object_id, kind FROM deep_sky_aliases";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DeepSkyAlias(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private static string CopyFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvo-catalog-v44-openngc-{Guid.NewGuid():N}.sqlite");
        File.Copy(FixturePath, path);
        return path;
    }

    private static string Checksum(string path)
    {
        using var source = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(source));
    }

    internal sealed record Installation(string Root) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
