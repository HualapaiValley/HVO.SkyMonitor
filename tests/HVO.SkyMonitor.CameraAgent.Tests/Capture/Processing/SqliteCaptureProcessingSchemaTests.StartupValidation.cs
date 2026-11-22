using System.Collections.Concurrent;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    // integrity_check and foreign_key_check each read every page of the shared journal. On a 30-day
    // retained history they dominated the processing-store startup that delays the first headline (#1914).
    private static readonly string[] InspectionScanOnly = ["inspection"];
    private static readonly string[] InspectionAndWriteScans = ["inspection", "schema"];

    [TestMethod]
    public async Task CurrentSchemaRestartScansWholeDatabaseOnce()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(InspectionScanOnly, recorder.Scopes.ToArray());
    }

    [TestMethod]
    public async Task FreshDatabaseScansBeforeAndAfterCreatingProcessingSchema()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var recorder = new ValidationRecorder();

        using (var created = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await created.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(InspectionAndWriteScans, recorder.Scopes.ToArray());
    }

    [TestMethod]
    public async Task EarlierCenteredSchemaAdoptionScansAgainInsideItsTransaction()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP INDEX ix_processing_outputs_astrometry_lookup;";
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(InspectionAndWriteScans, recorder.Scopes.ToArray());
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_processing_outputs_astrometry_lookup';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentSchemaForeignKeyViolationBlocksRestartWithoutMutation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            // Disable enforcement on this setup connection only, so the orphan commits as external damage would.
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                INSERT INTO processing_graph_delivery_facts(
                    fact_id, proposal_id, fact_kind, occurred_unix_ms, delivery_state, attempt_count, next_attempt_unix_ms)
                VALUES('11111111111111111111111111111111', '22222222222222222222222222222222', 'Accepted', 0, 'Pending', 0, 0);
                """;
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
        }
        var filesBefore = ReadDatabaseFiles(fixture.DatabasePath);
        var recorder = new ValidationRecorder();

        using (var rejected = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await rejected.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            StringAssert.Contains(failure.Message, "foreign-key validation failed", StringComparison.Ordinal);
        }

        CollectionAssert.AreEqual(InspectionScanOnly, recorder.Scopes.ToArray());
        var filesAfter = ReadDatabaseFiles(fixture.DatabasePath);
        CollectionAssert.AreEquivalent(filesBefore.Keys, filesAfter.Keys);
        foreach (var file in filesBefore.Where(static file => !file.Key.EndsWith("-shm", StringComparison.Ordinal)))
            CollectionAssert.AreEqual(file.Value, filesAfter[file.Key], file.Key);
    }

    private sealed class ValidationRecorder : ICaptureProcessingFaultInjector
    {
        internal ConcurrentQueue<string> Scopes { get; } = new();

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.BeforeDatabaseValidation)
                Scopes.Enqueue(nodeId);
        }
    }
}
