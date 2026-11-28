using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using HVO.SkyMonitor.CameraAgent.Common.Sqlite;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Data.Migrations;
using HVO.SkyMonitor.CameraAgent.Tests.Deployment;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.CameraAgent.Tests.State;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Deployment")]
[DoNotParallelize]
public sealed class CameraAgentDatabaseBaselineTests
{
    private const string InitialIdentityId = "20261128144428_InitialIdentity";
    private const string FutureIdentityId = "20261129000000_SyntheticForward";
    private const string RawJournal = "raw/journal/raw-ingress.db";
    private const string Identity = "identity/cameraagent_identity.db";

    [TestMethod]
    [DataRow("rawIngress", "silo_raw_admissions", 17)]
    [DataRow("siloPublication", "journal_head", 0)]
    [DataRow("siloCatalog", "catalog_authority", 0)]
    [DataRow("siloArchive", "archive_work", 0)]
    [DataRow("environmentalObservations", "environmental_observation_journal", 5)]
    [DataRow("timeLapses", "products", 1)]
    [DataRow("nightlyProducts", "nightly_window_products", 3)]
    [DataRow("localAutomations", "automation_script_stops", 4)]
    [DataRow("orbitalElements", "orbital_installation", 1)]
    [DataRow("spaceWeather", "swpc_products", 1)]
    [DataRow("calibrationFamilies", "calibration_families", 1)]
    [DataRow("retainedIntegrity", "metadata", 0)]
    [DataRow("retainedValidation", "proofs", 0)]
    [DataRow("processing", "processing_execution_manifest_counts", 12)]
    public async Task SqliteBaselineRefusesUnsupportedVersionsAndCanMigrateForward(
        string store, string requiredTable, int oldVersion)
    {
        var location = store == "processing" ? RawJournal :
            CameraAgentStateContractsLabelTests.Stores.Single(entry => entry.Store == store).Location;
        using var harness = CameraAgentStateStoreHarness.Create();
        if (store == "processing")
            await harness.OpenStoreAsync("rawIngress").ConfigureAwait(false);
        await harness.OpenStoreAsync(store).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();

        var path = harness.StorePath(location);
        var baselineSchema = CameraAgentStateStoreHarness.ReadSchema(path);
        Assert.IsTrue(baselineSchema.ContainsKey($"table:{requiredTable}"), store);
        Assert.AreEqual("1", baselineSchema["pragma:user_version"]);
        var applicationId = int.Parse(baselineSchema["pragma:application_id"], CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(0, applicationId, store);
        if (store == "processing")
            Assert.AreEqual("1", baselineSchema["version:capture_processing_schema.version"]);

        await harness.OpenStoreAsync(store).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        CollectionAssert.AreEqual(baselineSchema.ToArray(), CameraAgentStateStoreHarness.ReadSchema(path).ToArray());

        using (var storage = Open(path))
            _ = Scalar(storage, "PRAGMA journal_mode=DELETE;");

        var checkpoint = $"{path}.baseline";
        File.Copy(path, checkpoint);
        using (var database = Open(path))
            WriteUnsupportedVersion(database, store, oldVersion, applicationId: 0);
        await AssertRefusedWithoutMutationAsync(harness, store, path).ConfigureAwait(false);
        if (store == "processing")
            await AssertRefusedWithoutMutationAsync(harness, "rawIngress", path).ConfigureAwait(false);
        File.Copy(checkpoint, path, overwrite: true);

        using (var database = Open(path))
            WriteUnsupportedVersion(database, store, 900, applicationId);
        await AssertRefusedWithoutMutationAsync(harness, store, path).ConfigureAwait(false);
        File.Copy(checkpoint, path, overwrite: true);

        var baselineRows = CameraAgentStateStoreHarness.Dump(path);
        using (var database = Open(path))
        {
            Assert.AreEqual("ok", Scalar(database, "PRAGMA integrity_check;"));
            Assert.AreEqual(0L, Scalar(database, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
            ApplySyntheticSqliteMigration(database, store, commit: false);
            Assert.AreEqual(baselineRows, CameraAgentStateStoreHarness.Dump(path), "An interrupted migration must roll back the DDL and version together.");
            ApplySyntheticSqliteMigration(database, store, commit: true);
            Assert.AreEqual(1L, Scalar(database, "SELECT version FROM future_schema_probe;"));
            Assert.AreEqual("ok", Scalar(database, "PRAGMA integrity_check;"));
            Assert.AreEqual(0L, Scalar(database, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        }
        var upgraded = CameraAgentStateStoreHarness.Dump(path);
        var baselineLines = baselineRows.Split('\n').Where(line => !IsSchemaMarker(line)).ToArray();
        var upgradedLines = upgraded.Split('\n').Where(line =>
            !IsSchemaMarker(line) && !line.Contains("future_schema_probe", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEquivalent(baselineLines, upgradedLines, "The forward migration must preserve the existing schema and rows.");
        await AssertRefusedWithoutMutationAsync(harness, store, path).ConfigureAwait(false);

        File.Copy(checkpoint, path, overwrite: true);
        await harness.OpenStoreAsync(store).ConfigureAwait(false);
        CollectionAssert.AreEqual(baselineSchema.ToArray(), CameraAgentStateStoreHarness.ReadSchema(path).ToArray());

        bool IsSchemaMarker(string line) => store == "processing"
            ? (line.Contains("capture_processing_schema", StringComparison.Ordinal) || line.Contains("CHECK(version = ", StringComparison.Ordinal))
            : line.StartsWith("PRAGMA user_version", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task IdentityBaselineMatchesTheModelAndSupportsAnEfForwardMigration()
    {
        using var harness = CameraAgentStateStoreHarness.Create();
        await harness.OpenStoreAsync("identity").ConfigureAwait(false);
        var path = harness.StorePath(Identity);
        var current = harness.OpenIdentity();
        await using (current.ConfigureAwait(false))
        {
            Assert.IsFalse(current.Database.HasPendingModelChanges());
            CollectionAssert.AreEqual(new[] { InitialIdentityId }, current.Database.GetMigrations().ToArray());
            CollectionAssert.AreEqual(new[] { InitialIdentityId }, (await current.Database.GetAppliedMigrationsAsync().ConfigureAwait(false)).ToArray());
            current.Users.Add(new ApplicationUser { Id = "baseline-user", UserName = "baseline" });
            await current.SaveChangesAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(CameraAgentStateStoreHarness.ReadSchema(path).ContainsKey("table:AspNetUserPasskeys"));

        var future = harness.OpenIdentity(options => options.ReplaceService<IMigrationsAssembly, ForwardIdentityMigrations>());
        await using (future.ConfigureAwait(false))
        {
            await CameraAgentIdentitySchema.InitializeAsync(future, CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(new[] { InitialIdentityId, FutureIdentityId }, (await future.Database.GetAppliedMigrationsAsync().ConfigureAwait(false)).ToArray());
            Assert.AreEqual("baseline", (await future.Users.SingleAsync().ConfigureAwait(false)).UserName);
            using (var version = Open(path))
                Assert.AreEqual(1L, Scalar(version, "SELECT version FROM future_schema_probe;"));
            await AssertRefusedWithoutMutationAsync(harness, "identity", path).ConfigureAwait(false);

            // The synthetic additive change has an exact EF downgrade. A real release must qualify its own rollback.
            await future.GetService<IMigrator>().MigrateAsync(InitialIdentityId).ConfigureAwait(false);
        }
        await harness.OpenStoreAsync("identity").ConfigureAwait(false);
        using var restored = Open(path);
        Assert.AreEqual("baseline", Scalar(restored, """SELECT "UserName" FROM "AspNetUsers";"""));
        Assert.AreEqual(0L, Scalar(restored, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'future_schema_probe';"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("20260827053715_InitialIdentity")]
    [DataRow("20990101000000_ForeignIdentity")]
    public async Task IdentityRefusesUnversionedLegacyAndUnknownDatabases(string migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        using var harness = CameraAgentStateStoreHarness.Create();
        var path = harness.StorePath(Identity);
        using (var database = Open(path))
        {
            using var command = database.CreateCommand();
            command.CommandText = "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY); INSERT INTO AspNetUsers VALUES('retained-user');";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (migration.Length != 0)
            {
                command.CommandText = """
                    CREATE TABLE "__EFMigrationsHistory"("MigrationId" TEXT PRIMARY KEY, "ProductVersion" TEXT);
                    INSERT INTO "__EFMigrationsHistory" VALUES ($migration, '10.0.0');
                    """;
                command.Parameters.AddWithValue("$migration", migration);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        await AssertRefusedWithoutMutationAsync(harness, "identity", path).ConfigureAwait(false);
    }

    private static async Task AssertRefusedWithoutMutationAsync(CameraAgentStateStoreHarness harness, string store, string path)
    {
        var before = await ReadDurableFilesAsync(path).ConfigureAwait(false);
        var error = await Assert.ThrowsAsync<Exception>(() => harness.OpenStoreAsync(store)).ConfigureAwait(false);
        StringAssert.Contains(error.Message, "fresh install required", StringComparison.OrdinalIgnoreCase);
        SqliteConnection.ClearAllPools();
        var after = await ReadDurableFilesAsync(path).ConfigureAwait(false);
        CollectionAssert.AreEqual(before.Keys.ToArray(), after.Keys.ToArray(), store);
        foreach (var (file, bytes) in before)
            CollectionAssert.AreEqual(bytes, after[file], $"{store}: {file}");
    }

    private static async Task<Dictionary<string, byte[]>> ReadDurableFilesAsync(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var suffix in new[] { "", "-wal", "-journal" })
        {
            // A read-only SQLite connection may create empty coordination files.
            var file = path + suffix;
            if (new FileInfo(file) is { Exists: true, Length: > 0 })
                files.Add(suffix, await File.ReadAllBytesAsync(file).ConfigureAwait(false));
        }
        return files;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The version is a fixed integer case from the disposable store test.")]
    private static void WriteUnsupportedVersion(SqliteConnection database, string store, int version, int applicationId)
    {
        using var command = database.CreateCommand();
        command.CommandText = store == "processing"
            ? string.Create(CultureInfo.InvariantCulture, $"""
                PRAGMA ignore_check_constraints = ON;
                UPDATE capture_processing_schema SET version = {version};
                """)
            : SqliteSchemaBaseline.Stamp(version, applicationId);
        command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The store selects between two fixed synthetic migration statements.")]
    private static void ApplySyntheticSqliteMigration(SqliteConnection db, string store, bool commit)
    {
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = store == "processing"
            ? "SELECT version FROM capture_processing_schema WHERE schema_key = 1;"
            : "PRAGMA user_version;";
        Assert.AreEqual(1L, command.ExecuteScalar(), "The synthetic forward migration accepts only the shipped baseline.");
        command.CommandText = """
            CREATE TABLE future_schema_probe(version INTEGER NOT NULL) STRICT;
            INSERT INTO future_schema_probe VALUES (1);
            """;
        command.ExecuteNonQuery();
        command.CommandText = store == "processing" ? """
            ALTER TABLE capture_processing_schema RENAME TO baseline_processing_schema;
            CREATE TABLE capture_processing_schema(
                schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
                version INTEGER NOT NULL CHECK(version = 2)
            ) STRICT;
            INSERT INTO capture_processing_schema SELECT schema_key, 2 FROM baseline_processing_schema WHERE version = 1;
            DROP TABLE baseline_processing_schema;
            """ : "PRAGMA user_version = 2;";
        command.ExecuteNonQuery();
        if (commit)
            tx.Commit();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed schema test queries reach this helper.")]
    private static object? Scalar(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "EF creates the replacement service through dependency injection.")]
    private sealed class ForwardIdentityMigrations : IMigrationsAssembly
    {
        public IReadOnlyDictionary<string, TypeInfo> Migrations { get; } = new SortedDictionary<string, TypeInfo>(StringComparer.Ordinal)
        {
            [InitialIdentityId] = typeof(InitialIdentity).GetTypeInfo(),
            [FutureIdentityId] = typeof(ForwardIdentity).GetTypeInfo()
        };

        public Assembly Assembly => typeof(InitialIdentity).Assembly;
        public ModelSnapshot ModelSnapshot => new ApplicationDbContextModelSnapshot();

        public string? FindMigrationId(string nameOrId)
            => Migrations.Keys.FirstOrDefault(id => id == nameOrId || id.EndsWith("_" + nameOrId, StringComparison.Ordinal));

        public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
        {
            var migration = migrationClass.AsType() == typeof(ForwardIdentity) ? new ForwardIdentity() : (Migration)new InitialIdentity();
            migration.ActiveProvider = activeProvider;
            return migration;
        }
    }

    [Migration(FutureIdentityId)]
    private sealed class ForwardIdentity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("""
                CREATE TABLE future_schema_probe(version INTEGER NOT NULL) STRICT;
                INSERT INTO future_schema_probe VALUES (1);
                """);

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("future_schema_probe");
    }
}
