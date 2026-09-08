using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Fleet;

[TestClass]
[TestCategory("Integration")]
public sealed class SqliteFleetStatusOutboxTests
{
    private string? _root;
    private readonly Guid _agentInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly Guid _bootSessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fleet-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Enqueue_PreservesTransitionsAndCoalescesOnlyTrailingRoutineReport()
    {
        using var outbox = new SqliteFleetStatusOutbox();

        await EnqueueAsync(outbox, transition: true, "healthy").ConfigureAwait(false);
        await EnqueueAsync(outbox, transition: true, "degraded").ConfigureAwait(false);
        await EnqueueAsync(outbox, transition: true, "recovered").ConfigureAwait(false);
        var periodic1 = await EnqueueAsync(outbox, transition: false, "recovered").ConfigureAwait(false);
        var periodic2 = await EnqueueAsync(outbox, transition: false, "recovered").ConfigureAwait(false);

        Assert.AreEqual(4, periodic1.Sequence);
        Assert.AreEqual(4, periodic2.Sequence);
        Assert.AreEqual(4, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).PendingCount);
    }

    [TestMethod]
    public async Task Retry_FreezesAttemptedPayloadAndBlocksNewerSequenceUntilDue()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero));
        using var outbox = new SqliteFleetStatusOutbox(time);
        var first = await EnqueueAsync(outbox, transition: false, "stable").ConfigureAwait(false);
        var lease = await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await outbox.RetryAsync(_root!, lease!, time.GetUtcNow().AddMinutes(5), "offline", CancellationToken.None).ConfigureAwait(false);

        var second = await EnqueueAsync(outbox, transition: false, "stable").ConfigureAwait(false);
        var coalesced = await EnqueueAsync(outbox, transition: false, "stable").ConfigureAwait(false);

        Assert.AreEqual(1, first.Sequence);
        Assert.AreEqual(2, second.Sequence);
        Assert.AreEqual(2, coalesced.Sequence);
        Assert.IsNull(await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false),
            "The oldest retry deadline must fence newer status delivery.");
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!.Record.Report.Sequence);
    }

    [TestMethod]
    public async Task Restart_PreservesSequenceAndRequiresExactAcknowledgement()
    {
        using (var firstProcess = new SqliteFleetStatusOutbox())
        {
            await EnqueueAsync(firstProcess, transition: true, "boot").ConfigureAwait(false);
        }
        using var restarted = new SqliteFleetStatusOutbox();
        var second = await EnqueueAsync(restarted, transition: true, "new-boot").ConfigureAwait(false);
        Assert.AreEqual(2, second.Sequence);
        var lease = await restarted.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var incorrect = new FleetHeartbeatAcknowledgement(
            _agentInstanceId, _bootSessionId, 99, FleetHeartbeatDisposition.Advanced, DateTimeOffset.UtcNow, 60);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await restarted.AcknowledgeAsync(_root!, lease!, incorrect, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(2, (await restarted.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).PendingCount);
        var exact = new FleetHeartbeatAcknowledgement(
            lease!.Record.Report.AgentInstanceId,
            lease.Record.Report.BootSessionId,
            lease.Record.Report.Sequence,
            FleetHeartbeatDisposition.Advanced,
            DateTimeOffset.UtcNow,
            60);
        await restarted.AcknowledgeAsync(_root!, lease, exact, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, (await restarted.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).PendingCount);
    }

    [TestMethod]
    public async Task Reconnect_DrainsOutageTransitionsInDurableSequenceOrder()
    {
        using var outbox = new SqliteFleetStatusOutbox();
        await EnqueueAsync(outbox, transition: true, "healthy").ConfigureAwait(false);
        await EnqueueAsync(outbox, transition: true, "degraded").ConfigureAwait(false);
        await EnqueueAsync(outbox, transition: true, "recovered").ConfigureAwait(false);
        var delivered = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var lease = await outbox.ClaimAsync(
                _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            delivered.Add(lease.Record.Report.Sequence);
            await outbox.AcknowledgeAsync(
                _root!,
                lease,
                new FleetHeartbeatAcknowledgement(
                    lease.Record.Report.AgentInstanceId,
                    lease.Record.Report.BootSessionId,
                    lease.Record.Report.Sequence,
                    FleetHeartbeatDisposition.Advanced,
                    DateTimeOffset.UtcNow,
                    60),
                CancellationToken.None).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, delivered);
        Assert.AreEqual(0, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).PendingCount);
    }

    [TestMethod]
    public async Task ExpiredLease_IsRecoveredAfterWorkerRestart()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero));
        using var outbox = new SqliteFleetStatusOutbox(time);
        await EnqueueAsync(outbox, transition: true, "boot").ConfigureAwait(false);
        var abandoned = await outbox.ClaimAsync(
            _root!, "worker-1", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(abandoned);
        time.Advance(TimeSpan.FromMinutes(1));

        var recovered = await outbox.ClaimAsync(
            _root!, "worker-2", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(abandoned.Record.RecordId, recovered.Record.RecordId);
        Assert.AreEqual(2, recovered.Record.AttemptCount);
    }

    [TestMethod]
    public async Task Enqueue_DifferentAgentCannotReuseBoundDatabase()
    {
        using var outbox = new SqliteFleetStatusOutbox();
        await EnqueueAsync(outbox, transition: true, "boot").ConfigureAwait(false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await outbox.EnqueueAsync(
                _root!,
                Guid.NewGuid(),
                sequence => FleetStatusTestData.CreateReport(Guid.NewGuid(), sequence, _bootSessionId),
                "different",
                true,
                CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "different agent", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CapacityLimit_IsBoundedAndPersistsOverflowEvidence()
    {
        using var outbox = new SqliteFleetStatusOutbox(maximumRecords: 2);
        await EnqueueAsync(outbox, transition: true, "one").ConfigureAwait(false);
        await EnqueueAsync(outbox, transition: true, "two").ConfigureAwait(false);

        await Assert.ThrowsAsync<FleetStatusOutboxCapacityException>(async () =>
            await EnqueueAsync(outbox, transition: true, "three").ConfigureAwait(false)).ConfigureAwait(false);
        var snapshot = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, snapshot.PendingCount);
        Assert.AreEqual(0, snapshot.QuarantineCount);
        Assert.AreEqual(1, snapshot.OverflowCount);
    }

    private ValueTask<FleetStatusReportV1> EnqueueAsync(
        SqliteFleetStatusOutbox outbox,
        bool transition,
        string fingerprint)
        => outbox.EnqueueAsync(
            _root!,
            _agentInstanceId,
            sequence => FleetStatusTestData.CreateReport(_agentInstanceId, sequence, _bootSessionId),
            fingerprint,
            transition,
            CancellationToken.None);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
