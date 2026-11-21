extern alias InstallerRuntime;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using InstallerRawSchema = InstallerRuntime::HVO.SkyMonitor.Deployment.RawIngressV13Schema;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    // The separately released #1902 writer's sqlite_schema definition, including the partial predicate.
    private const string WitnessedAstrometryIndexSql = """
        CREATE INDEX ix_processing_outputs_astrometry_lookup
            ON processing_outputs(role, product_schema_version, capture_id, artifact_id, content_identity_sha256, availability_state)
            WHERE role = 'Metadata' AND product_schema_version = 'measured-astrometry-solution-v1';
        """;

    [TestMethod]
    [SupportedOSPlatform("linux")]
    public async Task BetaAstrometryJournalUpgradesAndRetainsIndexedHistoryAcrossRollbackAndRestart()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            await InstallBetaAstrometrySchemaAsync(connection, WitnessedAstrometryIndexSql, token).ConfigureAwait(false);
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO processing_outputs(output_identity_sha256, capture_id, agent_id, node_id, artifact_id,
                    role, variant, payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                    algorithms_json, compatibility_json, total_integration_ticks, capture_sequence, committed_unix_ms,
                    product_kind, product_schema_version, content_identity_sha256)
                VALUES ($output, '10000000000000000000000000000001', 'fixture', 'astrometry',
                    '20000000000000000000000000000002', 'Metadata', 'measured-astrometry', 'product.json', 'sidecar.json',
                    X'7B7D', $output, X'5B5D', X'7B7D', 1, 1, 0, 'Metadata', 'measured-astrometry-solution-v1', $content);
                INSERT INTO processing_output_sources VALUES ($output, 0, '30000000000000000000000000000003');
                """;
            seed.Parameters.AddWithValue("$output", new string('A', 64));
            seed.Parameters.AddWithValue("$content", new string('B', 64));
            await seed.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            Assert.IsTrue(InstallerRawSchema.IsCanonicalV16(connection));
        }

        var journal = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        await journal.InitializeAsync(token).ConfigureAwait(false);
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            Assert.AreEqual(17L, await ScalarAsync(connection, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.IsTrue(InstallerRawSchema.IsCanonicalV17(connection));
            Assert.AreEqual(10L, await ScalarAsync(connection, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'processing_centered_windows';").ConfigureAwait(false));
            await AssertIndexedAstrometryHistoryAsync(connection, token).ConfigureAwait(false);
        }

        await journal.InitializeAsync(token).ConfigureAwait(false);
        using (var migrated = new SqliteCaptureProcessingStore(fixture.Options))
            await migrated.InitializeAsync(token).ConfigureAwait(false);
        await journal.InitializeAsync(token).ConfigureAwait(false);
        using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
        await restarted.InitializeAsync(token).ConfigureAwait(false);

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.IsTrue(InstallerRawSchema.IsCanonicalV17(verify));
        Assert.AreEqual(12L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_centered_windows;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_centered_view_bases;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
        await AssertIndexedAstrometryHistoryAsync(verify, token).ConfigureAwait(false);
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    public async Task BetaAstrometryLookalikesWithWrongOrMissingIndexAreRejected()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var journal = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            await InstallBetaAstrometrySchemaAsync(connection,
                WitnessedAstrometryIndexSql.Replace("'Metadata'", "'Preview'", StringComparison.Ordinal), token).ConfigureAwait(false);
            Assert.IsFalse(InstallerRawSchema.IsCanonicalV16(connection));
        }

        var rawFailure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => journal.InitializeAsync(token)).ConfigureAwait(false);
        StringAssert.Contains(rawFailure.Message, "canonical schema 16", StringComparison.Ordinal);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            Assert.AreEqual(16L, await ScalarAsync(connection, "PRAGMA user_version;").ConfigureAwait(false));
            using var remove = connection.CreateCommand();
            remove.CommandText = "DROP INDEX ix_processing_outputs_astrometry_lookup;";
            await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await journal.InitializeAsync(token).ConfigureAwait(false);
        using var processing = new SqliteCaptureProcessingStore(fixture.Options);
        var processingFailure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await processing.InitializeAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        StringAssert.Contains(processingFailure.Message, "canonical schema 10", StringComparison.Ordinal);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(10L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'processing_centered_windows';").ConfigureAwait(false));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The test combines only fixed schema and index definitions.")]
    private static async Task InstallBetaAstrometrySchemaAsync(SqliteConnection connection, string indexSql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE centered_exposure_holds;
            DROP TABLE centered_exposure_products;
            DROP TABLE centered_exposure_attempts;
            DROP TABLE centered_exposure_runs;
            DROP TABLE centered_acquisition_owners;
            PRAGMA user_version = 16;
            """ + "\n" + SqliteCaptureProcessingStore.LegacySchema9SqlForTests
            .Replace("version = 9", "version = 10", StringComparison.Ordinal)
            .Replace("VALUES (1, 9)", "VALUES (1, 10)", StringComparison.Ordinal) + "\n" + indexSql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task AssertIndexedAstrometryHistoryAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.content_identity_sha256, source.source_artifact_id
            FROM processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup
            JOIN processing_output_sources source ON source.output_identity_sha256 = output.output_identity_sha256
            WHERE output.role = 'Metadata' AND output.product_schema_version = 'measured-astrometry-solution-v1'
              AND output.capture_id = '10000000000000000000000000000001'
              AND output.artifact_id = '20000000000000000000000000000002' AND output.availability_state = 'Available';
            """;
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
        Assert.AreEqual(new string('B', 64), reader.GetString(0));
        Assert.AreEqual("30000000000000000000000000000003", reader.GetString(1));
        Assert.IsFalse(await reader.ReadAsync(token).ConfigureAwait(false));
    }
}
