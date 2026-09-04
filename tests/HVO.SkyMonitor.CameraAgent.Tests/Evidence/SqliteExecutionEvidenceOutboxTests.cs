using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>
/// Durable behaviour of the graph-execution evidence outbox: sequencing, idempotency, exact acknowledgement, bounded
/// overflow without loss, quarantine and operator disposition, retention, restart recovery, and schema pinning.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SqliteExecutionEvidenceOutboxTests
{
    [TestMethod]
    public async Task InitializeCreatesTheCanonicalSchemaBesideTheRawIngressJournalAndIsIdempotent()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        await outbox.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        await outbox.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);

        var databasePath = Path.Combine(root.Path, "evidence", "execution-evidence-outbox.db");
        Assert.IsTrue(File.Exists(databasePath), databasePath);
        // The raw-ingress journal is untouched: a rollback to a baseline image that predates this lane still opens
        // every store it knows about, and simply never opens this separate file.
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "journal", "raw-ingress.db")));

        using var connection = OpenDatabase(root.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM execution_evidence_schema WHERE schema_key = 1;";
        Assert.AreEqual(
            SqliteExecutionEvidenceOutbox.CurrentSchemaVersion,
            Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task ExistingStoreWithADriftedSchemaIsRejectedRatherThanMigrated()
    {
        using var root = new TemporaryRoot();
        using (var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System))
        {
            await outbox.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        }
        using (var connection = OpenDatabase(root.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE execution_evidence_drift(value TEXT NOT NULL) STRICT;";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        using var reopened = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await reopened.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EnlistmentAssignsStrictlyIncreasingSequencesInOfferedOrderAndIsIdempotentByUnitKey()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);

        var first = await outbox.EnlistAsync(
            root.Path,
            origin.IdentitySha256,
            Guid.NewGuid(),
            [
                ExecutionEvidenceTestFactory.RevisionUnit(origin),
                ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)
            ],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceEnlistmentDisposition.Enlisted, first.Disposition);
        Assert.AreEqual(2, first.EnlistedCount);

        var pending = await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 16, 8 * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, pending.Count);
        // The revision must reach the receiver before or with the execution that depends on it.
        Assert.AreEqual(ExecutionEvidenceBodyKind.GraphRevision, pending[0].Kind);
        Assert.AreEqual(1, pending[0].OriginSequence);
        Assert.AreEqual(ExecutionEvidenceBodyKind.GraphExecution, pending[1].Kind);
        Assert.AreEqual(2, pending[1].OriginSequence);
        Assert.AreEqual(1, pending[0].AttemptCount);

        var repeat = await outbox.EnlistAsync(
            root.Path,
            origin.IdentitySha256,
            Guid.NewGuid(),
            [
                ExecutionEvidenceTestFactory.RevisionUnit(origin),
                ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)
            ],
            new(2, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceEnlistmentDisposition.Duplicate, repeat.Disposition);
        Assert.AreEqual(0, repeat.EnlistedCount);
        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, backlog.PendingCount + backlog.RetryCount);
    }

    [TestMethod]
    public async Task TheDiscoveryCursorAdvancesOnlyForwardAndSurvivesRestart()
    {
        using var root = new TemporaryRoot();
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        using (var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System))
        {
            await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
            await outbox.EnlistAsync(
                root.Path, origin.IdentitySha256, Guid.Empty, [],
                new(500, "0000000000000000000000000000000a", 400, 0),
                ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await outbox.EnlistAsync(
                    root.Path, origin.IdentitySha256, Guid.Empty, [],
                    new(499, "0000000000000000000000000000000a", 0, 0),
                    ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        using var reopened = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var cursor = await reopened.ReadDiscoveryCursorAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(500, cursor.TerminalUnixMs);
        Assert.AreEqual("0000000000000000000000000000000a", cursor.ExecutionId);
        Assert.AreEqual(400, cursor.DeferredTerminalUnixMs);
    }

    [TestMethod]
    public async Task MoreThanTheSourceReadWindowAccumulatesAndDrainsInOrderWithoutGaps()
    {
        // The delivered operator view of executions is capped at 256 rows; an outage must be able to accumulate
        // more than that and still drain contiguously.
        const int Units = 300;
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        for (var ordinal = 1; ordinal <= Units; ordinal++)
        {
            await outbox.EnlistAsync(
                root.Path,
                origin.IdentitySha256,
                Guid.NewGuid(),
                [ExecutionEvidenceTestFactory.ExecutionUnit(origin, ordinal)],
                new(ordinal, ExecutionEvidenceTestFactory.ExecutionId(ordinal).ToString("N"), 0, 0),
                ExecutionEvidenceTestFactory.UnboundedLimits,
                CancellationToken.None).ConfigureAwait(false);
        }

        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Units, backlog.PendingCount);
        Assert.AreEqual(Units, backlog.HighestSequence);

        var drained = new List<long>(Units);
        while (true)
        {
            var page = await outbox.ReadPendingAsync(
                root.Path, origin.IdentitySha256, 64, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
                CancellationToken.None).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }
            foreach (var unit in page)
            {
                drained.Add(unit.OriginSequence);
                await outbox.AcknowledgeAsync(
                    root.Path, origin.IdentitySha256, unit.OriginSequence, unit.PayloadSha256,
                    ExecutionEvidenceTestFactory.BaseUtc, CancellationToken.None).ConfigureAwait(false);
            }
        }

        CollectionAssert.AreEqual(Enumerable.Range(1, Units).Select(static value => (long)value).ToArray(), drained);
        var after = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, after.PendingCount + after.RetryCount);
        Assert.AreEqual(Units, after.AcknowledgedCount);
    }

    [TestMethod]
    public async Task AReadIsBoundedByBothTheUnitCountAndTheByteBudgetAndAlwaysMakesProgress()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        for (var ordinal = 1; ordinal <= 5; ordinal++)
        {
            await outbox.EnlistAsync(
                root.Path, origin.IdentitySha256, Guid.NewGuid(),
                [ExecutionEvidenceTestFactory.ExecutionUnit(origin, ordinal)],
                new(ordinal, ExecutionEvidenceTestFactory.ExecutionId(ordinal).ToString("N"), 0, 0),
                ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
        }

        var byCount = await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 2, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, byCount.Count);

        // A byte budget smaller than one unit still yields exactly one unit, so a tiny budget cannot deadlock.
        var byBytes = await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 5, 1, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, byBytes.Count);
    }

    [TestMethod]
    public async Task OverflowRefusesNewEnlistmentWithoutDroppingOrAdvancingTheCursor()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, origin.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);

        var saturated = await outbox.EnlistAsync(
            root.Path,
            origin.IdentitySha256,
            Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 2)],
            new(2, ExecutionEvidenceTestFactory.ExecutionId(2).ToString("N"), 0, 0),
            new(1, long.MaxValue, long.MaxValue, GraphExecutionEvidenceLimits.MaximumEnvelopeBytes),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceEnlistmentDisposition.Saturated, saturated.Disposition);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.BacklogSaturated, saturated.ReasonCode);

        var cursor = await outbox.ReadDiscoveryCursorAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, cursor.TerminalUnixMs, "A refused enlistment must not advance the sweep cursor.");
        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, backlog.PendingCount, "Nothing already durable may be discarded by overflow.");

        var storageSaturated = await outbox.EnlistAsync(
            root.Path,
            origin.IdentitySha256,
            Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 3)],
            new(3, ExecutionEvidenceTestFactory.ExecutionId(3).ToString("N"), 0, 0),
            new(long.MaxValue, long.MaxValue, 1, GraphExecutionEvidenceLimits.MaximumEnvelopeBytes),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceEnlistmentDisposition.Saturated, storageSaturated.Disposition);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.StorageSaturated, storageSaturated.ReasonCode);
    }

    [TestMethod]
    public async Task AcknowledgementRequiresTheExactStoredPayloadHashAndIsIdempotent()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, origin.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
        var unit = (await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 1, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false))[0];

        var other = new string('A', 64);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await outbox.AcknowledgeAsync(
                root.Path, origin.IdentitySha256, unit.OriginSequence, other, ExecutionEvidenceTestFactory.BaseUtc,
                CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        var stillPending = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, stillPending.PendingCount);

        await outbox.AcknowledgeAsync(
            root.Path, origin.IdentitySha256, unit.OriginSequence, unit.PayloadSha256,
            ExecutionEvidenceTestFactory.BaseUtc, CancellationToken.None).ConfigureAwait(false);
        await outbox.AcknowledgeAsync(
            root.Path, origin.IdentitySha256, unit.OriginSequence, unit.PayloadSha256,
            ExecutionEvidenceTestFactory.BaseUtc, CancellationToken.None).ConfigureAwait(false);
        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, backlog.AcknowledgedCount);
        Assert.AreEqual(0, backlog.PendingCount);
    }

    [TestMethod]
    public async Task RetryDefersAndQuarantineRetainsWhileOperatorDispositionIsAuditedAndIdempotent()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(ExecutionEvidenceTestFactory.BaseUtc);
        using var outbox = new SqliteExecutionEvidenceOutbox(clock);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, origin.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);

        await outbox.RetryAsync(
            root.Path, origin.IdentitySha256, 1, ExecutionEvidenceTestFactory.BaseUtc.AddMinutes(5),
            "http-503", CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 4, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1, (await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 4, 8L * 1024 * 1024,
            ExecutionEvidenceTestFactory.BaseUtc.AddMinutes(6), CancellationToken.None).ConfigureAwait(false)).Count);

        await outbox.QuarantineAsync(
            root.Path, origin.IdentitySha256, 1, GraphExecutionEvidenceReasonCodes.SequenceConflict,
            CancellationToken.None).ConfigureAwait(false);
        var page = await outbox.ReadOperationsPageAsync(root.Path, 10, null, CancellationToken.None)
            .ConfigureAwait(false);
        var record = page.Items.Single();
        Assert.AreEqual(nameof(ExecutionEvidenceUnitStatus.Quarantined), record.Status);
        Assert.IsTrue(record.CanReplay);
        Assert.IsTrue(record.CanAbandon);

        var applied = await outbox.ResolveOperationsAsync(
            root.Path, record.RecordId, OutboxOperationAction.Replay, "op-1", "owner", "evidence-restored",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OutboxOperationDisposition.Applied, applied);
        var duplicate = await outbox.ResolveOperationsAsync(
            root.Path, record.RecordId, OutboxOperationAction.Replay, "op-1", "owner", "evidence-restored",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OutboxOperationDisposition.Duplicate, duplicate);
        await Assert.ThrowsExactlyAsync<OutboxOperationCollisionException>(async () =>
            await outbox.ResolveOperationsAsync(
                root.Path, record.RecordId, OutboxOperationAction.Abandon, "op-1", "owner", "invalid-source",
                CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        var audit = await outbox.ReadOperationsAuditAsync(
            root.Path, record.RecordId, 10, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, audit.Items.Count);
        Assert.AreEqual("replay", audit.Items[0].Action);
        Assert.AreEqual("quarantine", audit.Items[1].Action);

        var replayed = await outbox.ReadOperationsDetailAsync(root.Path, record.RecordId, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(nameof(ExecutionEvidenceUnitStatus.Pending), replayed!.Status);
    }

    [TestMethod]
    public async Task AbandonIsTerminalAndOnlyQuarantinedUnitsCanBeResolved()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, origin.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
        var record = (await outbox.ReadOperationsPageAsync(root.Path, 10, null, CancellationToken.None)
            .ConfigureAwait(false)).Items.Single();
        Assert.IsFalse(record.CanReplay, "A pending unit is not an operator disposition target.");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await outbox.ResolveOperationsAsync(
                root.Path, record.RecordId, OutboxOperationAction.Abandon, "op-1", "owner", "invalid-source",
                CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        await outbox.QuarantineAsync(
            root.Path, origin.IdentitySha256, 1, "http-400", CancellationToken.None).ConfigureAwait(false);
        await outbox.ResolveOperationsAsync(
            root.Path, record.RecordId, OutboxOperationAction.Abandon, "op-2", "owner", "operator-approved-loss",
            CancellationToken.None).ConfigureAwait(false);
        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, backlog.AbandonedCount);
        Assert.AreEqual(0, backlog.QuarantinedCount);
    }

    [TestMethod]
    public async Task RetentionPrunesOnlyAcknowledgedUnitsAndKeepsTheNewestWindow()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(ExecutionEvidenceTestFactory.BaseUtc);
        using var outbox = new SqliteExecutionEvidenceOutbox(clock);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            await outbox.EnlistAsync(
                root.Path, origin.IdentitySha256, Guid.NewGuid(),
                [ExecutionEvidenceTestFactory.ExecutionUnit(origin, ordinal)],
                new(ordinal, ExecutionEvidenceTestFactory.ExecutionId(ordinal).ToString("N"), 0, 0),
                ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
        }
        var units = await outbox.ReadPendingAsync(
            root.Path, origin.IdentitySha256, 4, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false);
        foreach (var unit in units.Take(3))
        {
            await outbox.AcknowledgeAsync(
                root.Path, origin.IdentitySha256, unit.OriginSequence, unit.PayloadSha256,
                ExecutionEvidenceTestFactory.BaseUtc, CancellationToken.None).ConfigureAwait(false);
        }
        await outbox.QuarantineAsync(
            root.Path, origin.IdentitySha256, units[3].OriginSequence, "http-400", CancellationToken.None)
            .ConfigureAwait(false);

        var removed = await outbox.RetainAsync(root.Path, TimeSpan.FromHours(1), 1, origin.IdentitySha256, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(2, removed);
        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, backlog.AcknowledgedCount);
        Assert.AreEqual(1, backlog.QuarantinedCount, "Quarantined evidence is never pruned by retention.");

        clock.Advance(TimeSpan.FromHours(2));
        await outbox.RetainAsync(root.Path, TimeSpan.FromHours(1), 100, origin.IdentitySha256, CancellationToken.None).ConfigureAwait(false);
        var aged = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, aged.AcknowledgedCount);
        Assert.AreEqual(1, aged.QuarantinedCount);
    }

    [TestMethod]
    public async Task ARestartCreatesANewOriginSequenceSpaceWhileEarlierUnitsKeepTheirs()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var first = ExecutionEvidenceTestFactory.CreateOrigin(new("44444444-4444-4444-8444-444444444444"));
        var second = ExecutionEvidenceTestFactory.CreateOrigin(new("55555555-5555-4555-8555-555555555555"));
        Assert.AreNotEqual(first.IdentitySha256, second.IdentitySha256);
        await outbox.EnsureOriginAsync(root.Path, first, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, first.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(first, 1)],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);

        await outbox.EnsureOriginAsync(root.Path, second, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, second.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(second, 2)],
            new(2, ExecutionEvidenceTestFactory.ExecutionId(2).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);

        var origins = await outbox.ReadOriginsWithWorkAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, origins.Count);
        foreach (var origin in origins)
        {
            var pending = await outbox.ReadPendingAsync(
                root.Path, origin.IdentitySha256, 4, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(1, pending[0].OriginSequence, "Each origin owns its own sequence space from 1.");
        }
    }

    [TestMethod]
    public async Task BoundedResynchronizationReadsOnlyTheRequestedUnsettledRanges()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            await outbox.EnlistAsync(
                root.Path, origin.IdentitySha256, Guid.NewGuid(),
                [ExecutionEvidenceTestFactory.ExecutionUnit(origin, ordinal)],
                new(ordinal, ExecutionEvidenceTestFactory.ExecutionId(ordinal).ToString("N"), 0, 0),
                ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
        }

        var ranges = new[]
        {
            new ExecutionEvidenceSequenceRangeV1(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 2, 3),
            new ExecutionEvidenceSequenceRangeV1(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 5, 6)
        };
        var resynchronized = await outbox.ReadRangeAsync(
            root.Path, origin.IdentitySha256, ranges, 3, 8L * 1024 * 1024, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new long[] { 2, 3, 5 },
            resynchronized.Select(static unit => unit.OriginSequence).ToArray());
        Assert.IsTrue(
            resynchronized.All(static unit => unit.AttemptCount == 1),
            "A resynchronized unit spends an attempt exactly like a normal send.");

        // A byte budget below one unit still yields exactly one unit, and never more than the budget beyond that.
        var bounded = await outbox.ReadRangeAsync(
            root.Path, origin.IdentitySha256, ranges, 3, 1, ExecutionEvidenceTestFactory.BaseUtc,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, bounded.Count);
    }

    [TestMethod]
    public async Task ConflictsAndSourcePrunedEventsAreRecordedAndBounded()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        await outbox.RecordConflictAsync(
            root.Path, origin.IdentitySha256, 7, new string('B', 64), new string('C', 64),
            GraphExecutionEvidenceReasonCodes.SequenceConflict, CancellationToken.None).ConfigureAwait(false);
        await outbox.RecordConflictAsync(
            root.Path, origin.IdentitySha256, 8, new string('B', 64), null,
            GraphExecutionEvidenceReasonCodes.SequenceConflict, CancellationToken.None).ConfigureAwait(false);
        await outbox.RecordSourcePrunedAsync(
            root.Path, new(900, "0000000000000000000000000000000b", 800, 0), CancellationToken.None)
            .ConfigureAwait(false);

        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, backlog.ConflictCount);
        Assert.AreEqual(1, backlog.SourcePrunedEvents);
        Assert.IsGreaterThan(0, backlog.DatabaseBytes);

        var cursor = await outbox.ReadDiscoveryCursorAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(900, cursor.TerminalUnixMs);
        Assert.AreEqual(0, cursor.DeferredTerminalUnixMs, "Recording the event clears the deferred key.");
    }

    [TestMethod]
    public async Task AcknowledgedThroughAdvancesMonotonicallyPerOrigin()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnlistAsync(
            root.Path, origin.IdentitySha256, Guid.NewGuid(),
            [ExecutionEvidenceTestFactory.ExecutionUnit(origin, 1)],
            new(1, ExecutionEvidenceTestFactory.ExecutionId(1).ToString("N"), 0, 0),
            ExecutionEvidenceTestFactory.UnboundedLimits, CancellationToken.None).ConfigureAwait(false);
        await outbox.RecordAcknowledgedThroughAsync(root.Path, origin.IdentitySha256, 5, CancellationToken.None)
            .ConfigureAwait(false);
        await outbox.RecordAcknowledgedThroughAsync(root.Path, origin.IdentitySha256, 3, CancellationToken.None)
            .ConfigureAwait(false);
        var backlog = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(5, backlog.AcknowledgedThroughSequence);
    }

    private static SqliteConnection OpenDatabase(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "evidence", "execution-evidence-outbox.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
