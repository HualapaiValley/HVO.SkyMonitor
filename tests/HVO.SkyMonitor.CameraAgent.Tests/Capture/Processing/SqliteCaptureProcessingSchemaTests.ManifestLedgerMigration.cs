using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    private const string LedgerPrimary = "{\"nodes\":[{\"nodeId\":\"migrated-attempt\"}],\"opaque\":{\"escape\":\"\\u0061\", \"number\":1.00},\"normalizationPlans\":[]}";

    [TestMethod]
    public async Task InterruptedSchema9AstrometryIndexMigrationPreservesHistoryAndRetries()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        await SeedNodeAttemptAsync(fixture, SqliteCaptureProcessingStore.LegacySchema9SqlForTests).ConfigureAwait(false);
        using (var previous = await fixture.OpenAsync().ConfigureAwait(false))
        using (var output = previous.CreateCommand())
        {
            output.CommandText = """
                INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,role,variant,
                    payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,algorithms_json,
                    compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms,product_kind,
                    product_schema_version,content_identity_sha256)
                VALUES (printf('%064x',1),printf('%032x',2),'agent','astrometry',printf('%032x',3),'Metadata','astrometry',
                    'unused','unused',X'7B7D',printf('%064x',4),X'5B5D',X'7B7D',0,1,0,'Metadata',
                    'measured-astrometry-solution-v1',printf('%064x',5));
                """;
            Assert.AreEqual(1, await output.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        const string indexCountSql = """
            SELECT COUNT(*) FROM pragma_index_list('processing_outputs')
            WHERE name = 'ix_processing_outputs_astrometry_lookup' AND partial = 1;
            """;
        using (var original = await fixture.OpenAsync().ConfigureAwait(false))
        {
            Assert.AreEqual(9L, await ScalarAsync(original, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(original, indexCountSql).ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(original, "SELECT COUNT(*) FROM processing_outputs;").ConfigureAwait(false));
        }
        using (var migrated = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await migrated.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion,
            await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(16L, await ScalarAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, indexCountSql).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, """
            SELECT COUNT(*) FROM processing_outputs INDEXED BY ix_processing_outputs_astrometry_lookup
            WHERE role = 'Metadata' AND product_schema_version = 'measured-astrometry-solution-v1';
            """).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM processing_node_attempts WHERE node_id='migrated-attempt' AND duration_ticks=1234;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("Inputs", "\\uD800")]
    [DataRow("Inputs", "\\uDC00")]
    [DataRow("Outputs", "\\uD800")]
    [DataRow("Outputs", "\\uDC00")]
    public async Task Schema8LoneSurrogateLedgersAreIndependentAndStableAcrossRestart(string corruptKind, string surrogateEscape)
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        // Keep the escape in the legacy JSON bytes: a typed-ledger fixture would bypass conversion.
        var corruptCounts = "{\"migrated-attempt\":3,\"" + surrogateEscape + "\":1}";
        var original = await SeedLegacyLedgersAsync(fixture,
            corruptKind == "Inputs" ? corruptCounts : "{\"migrated-attempt\":2}",
            corruptKind == "Outputs" ? corruptCounts : "{\"migrated-attempt\":7}").ConfigureAwait(false);
        await AssertLegacyMigrationRollbackAsync(fixture, original).ConfigureAwait(false);

        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await AssertMigratedSurrogateLedgersAsync(fixture, corruptKind).ConfigureAwait(false);

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await AssertMigratedSurrogateLedgersAsync(fixture, corruptKind).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("absent")]
    [DataRow("corruptInputs")]
    [DataRow("corruptOutputs")]
    [DataRow("oversizedInputs")]
    public async Task Schema8LedgerMigrationPreservesPrimaryBytesAndIndependentEvidence(string variant)
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        await SeedLegacyLedgersAsync(fixture, variant).ConfigureAwait(false);
        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(await store.ReadNormalizationPlanAsync(Guid.ParseExact(new string('1', 32), "N"), "migrated-attempt", CancellationToken.None).ConfigureAwait(false));
        }
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(16L, await ScalarAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_node_attempts WHERE node_id='migrated-attempt' AND outcome='Produced' AND duration_ticks=1234;").ConfigureAwait(false));
        Assert.AreEqual(variant == "corruptInputs" ? "Corrupt" : variant == "oversizedInputs" ? "ExceedsBounds" : "Recorded",
            await LedgerStateAsync(verify, "Inputs").ConfigureAwait(false));
        Assert.AreEqual(variant == "corruptOutputs" ? "Corrupt" : "Recorded", await LedgerStateAsync(verify, "Outputs").ConfigureAwait(false));
        if (variant == "absent")
            Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_execution_manifest_counts WHERE node_id<>'';").ConfigureAwait(false));
        else if (variant != "corruptOutputs")
            Assert.AreEqual(7L, await ScalarAsync(verify, "SELECT evidence_count FROM processing_execution_manifest_counts WHERE ledger_kind='Outputs' AND node_id='migrated-attempt';").ConfigureAwait(false));
        Assert.AreEqual((long)Encoding.UTF8.GetByteCount(LedgerPrimary), await ScalarAsync(verify, "SELECT length(frozen_plan_json) FROM processing_executions;").ConfigureAwait(false));
        using var plan = verify.CreateCommand();
        plan.CommandText = "SELECT frozen_plan_json FROM processing_executions;";
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(LedgerPrimary), (byte[])(await plan.ExecuteScalarAsync().ConfigureAwait(false))!);
        var count = await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_execution_manifest_counts;").ConfigureAwait(false);
        using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(count, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_execution_manifest_counts;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow((int)CaptureProcessingFaultPoint.AfterSchemaTransactionBegan)]
    [DataRow((int)CaptureProcessingFaultPoint.BeforeSchemaCommit)]
    public async Task InterruptedSchema8LedgerMigrationRestoresOldLayoutAndRetries(int point)
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var original = await SeedLegacyLedgersAsync(fixture, "valid").ConfigureAwait(false);
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new ThrowOnceFaultInjector((CaptureProcessingFaultPoint)point)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        await AssertLegacyMigrationRollbackAsync(fixture, original).ConfigureAwait(false);
        using var retried = new SqliteCaptureProcessingStore(fixture.Options);
        await retried.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(4L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_execution_manifest_counts;").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow((int)CaptureProcessingFaultPoint.AfterSchemaTransactionBegan)]
    [DataRow((int)CaptureProcessingFaultPoint.BeforeSchemaCommit)]
    public async Task CancelledSchema8LedgerMigrationPreservesItsRetryInput(int point)
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var original = await SeedLegacyLedgersAsync(fixture, "valid").ConfigureAwait(false);
        using var canceled = new CancellationTokenSource();
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new CancelMigrationFaultInjector(canceled, (CaptureProcessingFaultPoint)point)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await interrupted.InitializeAsync(canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        await AssertLegacyMigrationRollbackAsync(fixture, original).ConfigureAwait(false);
        using var retried = new SqliteCaptureProcessingStore(fixture.Options);
        await retried.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<byte[]> SeedLegacyLedgersAsync(SchemaFixture fixture, string variant)
    {
        var input = variant == "corruptInputs" ? "{\"migrated-attempt\":-1}"
            : variant == "oversizedInputs" ? JsonSerializer.Serialize(Enumerable.Range(0, 65).ToDictionary(index => "node-" + index, static _ => 1))
            : "{\"migrated-attempt\":2}";
        var output = variant == "corruptOutputs" ? "[]" : "{\"migrated-attempt\":7}";
        return await SeedLegacyLedgersAsync(fixture, input, output, variant == "absent").ConfigureAwait(false);
    }

    private static async Task<byte[]> SeedLegacyLedgersAsync(SchemaFixture fixture, string input, string output, bool absent = false)
    {
        await SeedNodeAttemptAsync(fixture, SqliteCaptureProcessingStore.LegacySchema8SqlForTests).ConfigureAwait(false);
        var original = Encoding.UTF8.GetBytes(absent ? LedgerPrimary
            : LedgerPrimary[..^1] + ",\"frozenOutputInputCounts\":" + input + ",\"committedOutputCounts\":" + output + "}");
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_executions SET frozen_plan_json=$plan;";
        command.Parameters.AddWithValue("$plan", original);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return original;
    }

    private static async Task AssertMigratedSurrogateLedgersAsync(SchemaFixture fixture, string corruptKind)
    {
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(16L, await ScalarAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_node_attempts WHERE node_id='migrated-attempt' AND outcome='Produced' AND duration_ticks=1234;").ConfigureAwait(false));
        using (var plan = verify.CreateCommand())
        {
            plan.CommandText = "SELECT frozen_plan_json FROM processing_executions;";
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(LedgerPrimary), (byte[])(await plan.ExecuteScalarAsync().ConfigureAwait(false))!);
        }

        using var ledger = verify.CreateCommand();
        ledger.CommandText = """
            SELECT ledger_kind, node_id, evidence_count, evidence_status
            FROM processing_execution_manifest_counts
            ORDER BY ledger_kind, node_id;
            """;
        using var reader = await ledger.ExecuteReaderAsync().ConfigureAwait(false);
        var actual = new List<(string Kind, string Node, long? Count, string Status)>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var count = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? (long?)null : reader.GetInt64(2);
            actual.Add((reader.GetString(0), reader.GetString(1), count, reader.GetString(3)));
        }
        var expected = new List<(string Kind, string Node, long? Count, string Status)>
        {
            ("Inputs", "", null, corruptKind == "Inputs" ? "Corrupt" : "Recorded")
        };
        if (corruptKind == "Outputs") expected.Add(("Inputs", "migrated-attempt", 2, "Recorded"));
        expected.Add(("Outputs", "", null, corruptKind == "Outputs" ? "Corrupt" : "Recorded"));
        if (corruptKind == "Inputs") expected.Add(("Outputs", "migrated-attempt", 7, "Recorded"));
        CollectionAssert.AreEqual(expected, actual);
    }

    private static async Task AssertLegacyMigrationRollbackAsync(SchemaFixture fixture, byte[] original)
    {
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(8L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM sqlite_schema WHERE name='processing_execution_manifest_counts';").ConfigureAwait(false));
        using var command = verify.CreateCommand();
        command.CommandText = "SELECT frozen_plan_json FROM processing_executions;";
        CollectionAssert.AreEqual(original, (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    private static async Task<string> LedgerStateAsync(SqliteConnection connection, string kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_status FROM processing_execution_manifest_counts WHERE ledger_kind=$kind AND node_id='';";
        command.Parameters.AddWithValue("$kind", kind);
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private sealed class CancelMigrationFaultInjector(CancellationTokenSource source, CaptureProcessingFaultPoint target) : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == target) source.Cancel();
        }
    }
}
