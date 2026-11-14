using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    private const string LedgerPrimary = "{\"nodes\":[{\"nodeId\":\"migrated-attempt\"}],\"opaque\":{\"escape\":\"\\u0061\", \"number\":1.00},\"normalizationPlans\":[]}";

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
        Assert.AreEqual(9L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
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
        Assert.AreEqual(9L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
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
        await SeedNodeAttemptAsync(fixture, SqliteCaptureProcessingStore.LegacySchema8SqlForTests).ConfigureAwait(false);
        var input = variant == "corruptInputs" ? "{\"migrated-attempt\":-1}"
            : variant == "oversizedInputs" ? JsonSerializer.Serialize(Enumerable.Range(0, 65).ToDictionary(index => "node-" + index, static _ => 1))
            : "{\"migrated-attempt\":2}";
        var output = variant == "corruptOutputs" ? "[]" : "{\"migrated-attempt\":7}";
        var original = Encoding.UTF8.GetBytes(variant == "absent" ? LedgerPrimary
            : LedgerPrimary[..^1] + ",\"frozenOutputInputCounts\":" + input + ",\"committedOutputCounts\":" + output + "}");
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_executions SET frozen_plan_json=$plan;";
        command.Parameters.AddWithValue("$plan", original);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return original;
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
