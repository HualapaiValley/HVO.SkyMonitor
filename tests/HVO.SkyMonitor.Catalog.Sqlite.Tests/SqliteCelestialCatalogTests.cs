using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

[TestClass]
[DoNotParallelize]
internal sealed class SqliteCelestialCatalogTests
{
    private const string FixtureChecksum = "F80689217769A6B13C1B9BFB9711485D3CB1AD8DE009D3D6B0F0B0A4F1FA9840";
    private static readonly string[] ExpectedBoundedIds = ["32263", "30365", "69451"];
    private static readonly string[] ExpectedCandidateIds =
        ["32263", "30365", "69451", "71456", "90979", "24549", "24378", "27919", "7574"];
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite");

    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
    {
        var instance = new SqliteCelestialCatalogTests();

        Assert.IsNotNull(instance);
    }

    [TestMethod]
    public void ConstructorChecksumFailureOccursBeforeSqliteOpen()
    {
        var nonDatabase = Path.GetTempFileName();
        try
        {
            File.WriteAllText(nonDatabase, "not sqlite");
            var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                _ = new SqliteCelestialCatalog(CreateOptions(nonDatabase, new string('0', 64))));

            StringAssert.Contains(exception.Message, "SHA-256 mismatch", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(nonDatabase);
        }
    }

    [TestMethod]
    public void ConstructorRejectsNullOptions()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new SqliteCelestialCatalog(null!));
    }

    [TestMethod]
    public void ConstructorValidatesRequiredOptionsBeforeAccessingTheDatabase()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(null!, FixtureChecksum)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(" ", FixtureChecksum)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(FixturePath, FixtureChecksum, schemaVersion: " ")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(FixturePath, FixtureChecksum, preprocessingVersion: " ")));
    }

    [TestMethod]
    public void ConstructorRejectsMalformedChecksums()
    {
        var wrongLength = Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(FixturePath, new string('0', 63))));
        var nonHex = Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(FixturePath, new string('g', 64))));

        StringAssert.Contains(wrongLength.Message, "64 hexadecimal characters", StringComparison.Ordinal);
        StringAssert.Contains(nonHex.Message, "64 hexadecimal characters", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConstructorReportsMissingDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-catalog-{Guid.NewGuid():N}.sqlite");

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(path, new string('0', 64))));
    }

    [TestMethod]
    public void ConstructorRejectsSchemaAndPreprocessingVersionMismatch()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(FixturePath, FixtureChecksum, schemaVersion: "1")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new SqliteCelestialCatalog(CreateOptions(FixturePath, FixtureChecksum, preprocessingVersion: "1")));
    }

    [TestMethod]
    public void ConstructorRejectsNonIntegerAndMismatchedSqliteUserVersion()
    {
        AssertGeneratedCatalogThrows<ArgumentException>(schemaVersion: "v1",
            mutation: "UPDATE catalog_metadata SET value = 'v1' WHERE key = 'schema_version';");
        var exception = AssertGeneratedCatalogThrows<InvalidDataException>(
            mutation: "PRAGMA user_version = 2;");

        StringAssert.Contains(exception.Message, "user_version mismatch", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConstructorRejectsMissingCatalogTables()
    {
        AssertGeneratedCatalogThrows<InvalidDataException>("DROP TABLE catalog_metadata;");
        AssertGeneratedCatalogThrows<InvalidDataException>("DROP TABLE celestial_objects;");
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("catalog_version")]
    [DataRow("source_url")]
    [DataRow("license")]
    [DataRow("schema_version")]
    [DataRow("preprocessing_version")]
    public void ConstructorRejectsEachMissingRequiredMetadataKey(string key)
    {
        var exception = AssertGeneratedCatalogThrows<InvalidDataException>(
            $"DELETE FROM catalog_metadata WHERE key = '{key}';");

        StringAssert.Contains(exception.Message, $"'{key}'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConstructorRejectsBlankAndDuplicateMetadata()
    {
        var blank = AssertGeneratedCatalogThrows<InvalidDataException>(
            "UPDATE catalog_metadata SET value = '   ' WHERE key = 'name';");
        var incompatible = AssertGeneratedCatalogThrows<InvalidDataException>(
            "ALTER TABLE catalog_metadata ADD COLUMN unexpected TEXT;");

        StringAssert.Contains(blank.Message, "missing required key 'name'", StringComparison.Ordinal);
        StringAssert.Contains(incompatible.Message, "required schema", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConstructorRejectsMalformedMetadataSourceUrl()
    {
        AssertGeneratedCatalogThrows<UriFormatException>(
            "UPDATE catalog_metadata SET value = 'relative/source' WHERE key = 'source_url';");
    }

    [TestMethod]
    [DataRow("UPDATE celestial_objects SET id = '   ' WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET display_name = '' WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET right_ascension_hours = 1e999 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET right_ascension_hours = -0.1 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET right_ascension_hours = 24 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET declination_degrees = 1e999 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET declination_degrees = -90.1 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET declination_degrees = 90.1 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET magnitude = 1e999 WHERE id = 'a';")]
    [DataRow("UPDATE celestial_objects SET color_index = 1e999 WHERE id = 'a';")]
    public void ConstructorRejectsInvalidObjectFields(string mutation)
    {
        var exception = AssertGeneratedCatalogThrows<InvalidDataException>(mutation);

        StringAssert.Contains(exception.Message, "contains invalid data", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConstructorUsesReadOnlyDatabaseAndReleasesItAfterCaching()
    {
        var path = CopyFixture();
        var checksumBefore = Checksum(path);
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            var catalog = new SqliteCelestialCatalog(CreateOptions(path, checksumBefore));
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);

            var result = catalog.Query(new CatalogQuery(1, 100));

            Assert.HasCount(9, result);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ConstructorRejectsSidecarsAndDoesNotCreateThem()
    {
        var path = CopyFixture();
        var sidecar = path + "-wal";
        try
        {
            File.WriteAllText(sidecar, "unexpected");
            var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                _ = new SqliteCelestialCatalog(CreateOptions(path, Checksum(path))));
            StringAssert.Contains(exception.Message, "sidecar", StringComparison.Ordinal);

            File.Delete(sidecar);
            _ = new SqliteCelestialCatalog(CreateOptions(path, Checksum(path)));
            Assert.IsFalse(File.Exists(path + "-journal"));
            Assert.IsFalse(File.Exists(path + "-wal"));
            Assert.IsFalse(File.Exists(path + "-shm"));
        }
        finally
        {
            File.Delete(sidecar);
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ConstructorRejectsHardLinkedDatabase()
    {
        var path = CopyFixture();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-external-{Guid.NewGuid():N}");
        CatalogSnapshotResolverTests.CreateHardLink(path, externalPath);
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                _ = new SqliteCelestialCatalog(CreateOptions(path, Checksum(path))));

            StringAssert.Contains(exception.Message, "hard-link", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(externalPath);
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ConstructorConsumesAuthenticatedDatabaseAcrossAbaSqliteOpenReplacement()
    {
        var path = CopyFixture();
        var externalPath = Path.Combine(Path.GetTempPath(), $"hvo-catalog-original-{Guid.NewGuid():N}");
        var originalChecksum = Checksum(path);
        var swapped = false;
        var replacementBlocked = false;
        CatalogSnapshotResolver.ValidationTestHook = (hookPath, point) =>
        {
            if (!string.Equals(hookPath, path, StringComparison.Ordinal))
            {
                return;
            }
            if (point == CatalogSnapshotValidationPoint.BeforeSqliteOpen)
            {
                try
                {
                    File.Move(path, externalPath);
                    swapped = true;
                    File.WriteAllBytes(path, new byte[checked((int)new FileInfo(externalPath).Length)]);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    replacementBlocked = true;
                }
            }
            else if (point == CatalogSnapshotValidationPoint.AfterSqliteLoad && swapped)
            {
                File.Delete(path);
                File.Move(externalPath, path);
                swapped = false;
            }
        };
        try
        {
            var catalog = new SqliteCelestialCatalog(CreateOptions(path, originalChecksum));

            Assert.AreEqual(9, catalog.ObjectCount);
            Assert.AreEqual(OperatingSystem.IsWindows(), replacementBlocked);
            Assert.AreEqual(originalChecksum, Checksum(path));
        }
        finally
        {
            CatalogSnapshotResolver.ValidationTestHook = null;
            if (swapped)
            {
                File.Delete(path);
                File.Move(externalPath, path);
            }
            else
            {
                File.Delete(externalPath);
            }
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ConstructorRejectsIncompatibleSchemaIndexSolAndHipparcosIdentity()
    {
        AssertGeneratedCatalogThrows<InvalidDataException>("CREATE TABLE unexpected(value TEXT);");
        AssertGeneratedCatalogThrows<InvalidDataException>("DROP INDEX celestial_objects_magnitude_id;");
        AssertGeneratedCatalogThrows<InvalidDataException>(
            "DROP INDEX celestial_objects_magnitude_id; CREATE INDEX celestial_objects_magnitude_id ON celestial_objects(id, magnitude);");
        AssertGeneratedCatalogThrows<InvalidDataException>("UPDATE celestial_objects SET id = '0' WHERE id = 'a';");
        AssertGeneratedCatalogThrows<InvalidDataException>("UPDATE celestial_objects SET hipparcos_id = '1' WHERE id = 'b';");
        AssertGeneratedCatalogThrows<InvalidDataException>("UPDATE celestial_objects SET hipparcos_id = '01' WHERE id = 'a';");
        AssertGeneratedCatalogThrows<InvalidDataException>("UPDATE celestial_objects SET hipparcos_id = ' ' WHERE id = 'a';");
    }

    [TestMethod]
    public void ConstructorValidatesExpectedRowCountAndCatalogVersion()
    {
        AssertGeneratedCatalogThrows<InvalidDataException>(expectedRowCount: 3);
        AssertGeneratedCatalogThrows<InvalidDataException>(expectedCatalogVersion: "other");
    }

    [TestMethod]
    public void MetadataReportsSnapshotAndSourceEvidence()
    {
        var catalog = CreateCatalog();

        Assert.AreEqual("HYG bright-star test fixture", catalog.Metadata.Name);
        Assert.AreEqual("4.2-fixture.1", catalog.Metadata.Version);
        Assert.AreEqual("https://codeberg.org/astronexus/hyg", catalog.Metadata.SourceUrl.AbsoluteUri);
        Assert.AreEqual(FixtureChecksum, catalog.Metadata.Checksum);
        Assert.AreEqual("CC BY-SA 4.0", catalog.Metadata.License);
        Assert.AreEqual("2", catalog.Metadata.SchemaVersion);
        Assert.AreEqual("3", catalog.PreprocessingVersion);
        Assert.AreEqual(9, catalog.ObjectCount);
        Assert.AreEqual(FixturePath, catalog.Options.DatabasePath);
    }

    [TestMethod]
    public async Task QueriesOrderByMagnitudeThenIdAndCandidatesAreNotPreTruncated()
    {
        var catalog = CreateCatalog();

        var bounded = catalog.Query(new CatalogQuery(0.45, 3));
        var candidates = await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(0.45)).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedBoundedIds, bounded.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(ExpectedCandidateIds, candidates.Select(item => item.Id).ToArray());
        Assert.AreEqual("32349", candidates.Single(item => item.Id == "32263").HipparcosId);
    }

    [TestMethod]
    public async Task RegionalCandidatesMatchInMemoryContractAndPreserveOrder()
    {
        var catalog = CreateCatalog();
        var all = await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(10)).ConfigureAwait(false);
        var center = all[3];
        var query = new CatalogCandidateQuery(10, new J2000SphericalCap(
            center.RightAscensionHours, center.DeclinationDegrees, 35));

        var sqlite = await catalog.QueryCandidatesAsync(query).ConfigureAwait(false);
        var inMemory = await new InMemoryCelestialCatalog(all).QueryCandidatesAsync(query).ConfigureAwait(false);

        Assert.IsNotEmpty(sqlite);
        Assert.IsTrue(sqlite.Count < all.Count);
        CollectionAssert.AreEqual(inMemory.Select(item => item.Id).ToArray(), sqlite.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task QueryCandidatesAsyncHonorsCancellation()
    {
        var catalog = CreateCatalog();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(6.5), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task GetByHipparcosIdsAsync_ReturnsStableMatchesIndependentOfMagnitude()
    {
        var catalog = CreateCatalog();

        var result = await catalog.GetByHipparcosIdsAsync(["91262", "32349", "91262", "missing"])
            .ConfigureAwait(false);

        Assert.HasCount(2, result);
        Assert.AreEqual("32263", result[0].Id);
        Assert.AreEqual("90979", result[1].Id);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await catalog.GetByHipparcosIdsAsync([" "]).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task QueriesHonorInclusiveMagnitudeAndResultBoundaries()
    {
        var catalog = CreateCatalog();

        var one = catalog.Query(new CatalogQuery(-1.44, 1));
        var maximumAllowed = catalog.Query(new CatalogQuery(-1.44, 100_000));
        var none = catalog.Query(new CatalogQuery(-1.45, 100_000));
        var candidatesAtBoundary = await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(-1.44)).ConfigureAwait(false);
        var noCandidates = await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(-1.45)).ConfigureAwait(false);
        var all = catalog.Query(new CatalogQuery(10, 100_000));
        var allCandidates = await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(10)).ConfigureAwait(false);

        Assert.HasCount(1, one);
        Assert.HasCount(1, maximumAllowed);
        Assert.IsEmpty(none);
        Assert.HasCount(1, candidatesAtBoundary);
        Assert.IsEmpty(noCandidates);
        Assert.HasCount(9, all);
        Assert.HasCount(9, allCandidates);
    }

    [TestMethod]
    public async Task QueriesRejectNullAndInvalidBoundaries()
    {
        var catalog = CreateCatalog();

        Assert.ThrowsExactly<ArgumentNullException>(() => catalog.Query(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await catalog.QueryCandidatesAsync(null!).ConfigureAwait(false)).ConfigureAwait(false);

        foreach (var query in new[]
                 {
                     new CatalogQuery(double.NaN, 1),
                     new CatalogQuery(double.PositiveInfinity, 1),
                     new CatalogQuery(1, 0),
                     new CatalogQuery(1, 100_001)
                 })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => catalog.Query(query));
        }

        foreach (var magnitude in new[] { double.NaN, double.NegativeInfinity })
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(magnitude)).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public void CacheDoesNotObserveDatabaseChangesAfterConstruction()
    {
        var path = CopyFixture();
        try
        {
            var catalog = new SqliteCelestialCatalog(CreateOptions(path, Checksum(path)));
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM celestial_objects";
                command.ExecuteNonQuery();
            }

            Assert.HasCount(9, catalog.Query(new CatalogQuery(1, 100)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SqliteCelestialCatalog CreateCatalog()
        => new(CreateOptions(FixturePath, FixtureChecksum));

    private static SqliteCelestialCatalogOptions CreateOptions(
        string path,
        string checksum,
        string schemaVersion = "2",
        string preprocessingVersion = "3")
        => new(path, checksum, schemaVersion, preprocessingVersion);

    private static string CopyFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvo-catalog-{Guid.NewGuid():N}.sqlite");
        File.Copy(FixturePath, path);
        return path;
    }

    private static string Checksum(string path)
    {
        using var source = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static T AssertGeneratedCatalogThrows<T>(
        string mutation = "",
        string schemaVersion = "1",
        long? expectedRowCount = null,
        string? expectedCatalogVersion = null)
        where T : Exception
    {
        var path = CreateGeneratedFixture(mutation);
        try
        {
            return Assert.ThrowsExactly<T>(() =>
                _ = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
                    path, Checksum(path), schemaVersion, "1", expectedRowCount, expectedCatalogVersion)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateGeneratedFixture(string mutation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvo-generated-catalog-{Guid.NewGuid():N}.sqlite");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Statements are constants supplied only by this test class.
        command.CommandText = """
            PRAGMA user_version = 1;
            CREATE TABLE catalog_metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;
            INSERT INTO catalog_metadata(key, value) VALUES
                ('name', 'Generated test catalog'),
                ('catalog_version', 'test-1'),
                ('source_url', 'https://example.test/catalog'),
                ('license', 'test license'),
                ('schema_version', '1'),
                ('preprocessing_version', '1');
            CREATE TABLE celestial_objects (
                id TEXT PRIMARY KEY NOT NULL,
                display_name TEXT NOT NULL,
                right_ascension_hours REAL NOT NULL,
                declination_degrees REAL NOT NULL,
                magnitude REAL NOT NULL,
                color_index REAL NULL,
                hipparcos_id TEXT NULL) WITHOUT ROWID;
            INSERT INTO celestial_objects VALUES
                ('a', 'Alpha', 0, -90, -1.46, NULL, '1'),
                ('b', 'Beta', 23.999, 90, 6.5, 0.25, '2');
            CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
            """ + mutation;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
        return path;
    }
}
