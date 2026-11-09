using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalEvidencePipelineIntegrationTests
{
    [TestMethod]
    [DataRow("terminal")]
    [DataRow("quarantined")]
    [DataRow("malformed")]
    public async Task UndeliverableInputsExposeTheDerivativeForDurableOperatorAbandonment(string failure)
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            using (var store = new SqliteEnvironmentalObservationOutbox(clock))
            {
                foreach (var input in inputs) await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false);
                await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
                if (failure == "malformed")
                {
                    using var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")};Pooling=False");
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var corrupt = connection.CreateCommand();
                    corrupt.CommandText = "UPDATE environmental_observation_outbox SET payload = zeroblob(payload_bytes) WHERE record_id IN (SELECT outbox_record_id FROM environmental_observation_central_projection WHERE local_record_id IN (SELECT record_id FROM environmental_observation_journal WHERE observation_kind != 'CloudCover'));";
                    await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false);
                    Assert.IsNull(await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
                }
                else
                {
                    for (var index = 0; index < 2; index++)
                    {
                        var input = await ClaimAsync(store, root).ConfigureAwait(false);
                        Assert.AreNotEqual(EnvironmentalObservationKind.CloudCover, input.Record.Observation.Value.Kind);
                        if (failure == "terminal") await store.TerminalAsync(root, input, "maximum-attempts-exceeded", CancellationToken.None).ConfigureAwait(false);
                        else await store.QuarantineAsync(root, input, "http-400", CancellationToken.None).ConfigureAwait(false);
                    }
                }
                var letters = await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(3, letters, "The blocked derivative must be visible even though it has never been leased.");
                var blocked = letters.Single(item => item.Reason == "lineage-input-undeliverable");
                Assert.AreEqual(0, blocked.AttemptCount);
                foreach (var input in letters.Where(item => item.RecordId != blocked.RecordId))
                    await store.ResolveOperationsAsync(root, input.RecordId, OutboxOperationAction.Abandon,
                        Guid.NewGuid().ToString("N"), "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
                clock.Now = Epoch.AddDays(30);
                Assert.IsNull(await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
                Assert.AreEqual(0, await store.ProjectWaitingAsync(root, 100, CancellationToken.None).ConfigureAwait(false), "Discarded v3 inputs must not be recreated.");
                var snapshot = await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(0, snapshot.PendingCount);
                Assert.IsNull(snapshot.OldestPendingUtc);
                var operations = await store.ReadOperationsPageAsync(root, 10, null, CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, operations.Items);
                Assert.IsTrue(operations.Items[0].CanAbandon && operations.Items[0].CanReplay);
                var key = Guid.NewGuid().ToString("N");
                Assert.AreEqual(OutboxOperationDisposition.Applied, await store.ResolveOperationsAsync(root, blocked.RecordId,
                    OutboxOperationAction.Abandon, key, "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false));
                Assert.AreEqual(OutboxOperationDisposition.Duplicate, await store.ResolveOperationsAsync(root, blocked.RecordId,
                    OutboxOperationAction.Abandon, key, "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false));
                Assert.HasCount(3, (await store.ReadLocalPageAsync(root, null, 10, null, CancellationToken.None).ConfigureAwait(false)).Items);
            }
            using var restarted = new SqliteEnvironmentalObservationOutbox(clock);
            Assert.AreEqual(0, await restarted.ProjectWaitingAsync(root, 100, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(0, (await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
            Assert.AreEqual(EnvironmentalObservationDeliveryAvailability.Healthy, EnvironmentalObservationDeliveryService.DetermineAvailability(
                await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)));
            Assert.IsEmpty(await restarted.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false));
            var duplicate = await restarted.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(duplicate.DeliveryObservation, "A duplicate publication cannot undo an explicit discard.");
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task ReplayingInputsAndDerivativePreservesAcknowledgementOrderingAndPayloadIdentity()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            foreach (var input in inputs) await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false);
            var committed = await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            var hash = EnvironmentalObservationJson.ComputeContentSha256(committed.DeliveryObservation!);
            var failedInput = await ClaimAsync(store, root).ConfigureAwait(false);
            await store.TerminalAsync(root, failedInput, "maximum-attempts-exceeded", CancellationToken.None).ConfigureAwait(false);
            var blocked = (await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false))
                .Single(item => item.Reason == "lineage-input-undeliverable");
            await store.ResolveOperationsAsync(root, failedInput.Record.RecordId, OutboxOperationAction.Replay,
                "replay-input", "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue((await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false)).Any(item => item.RecordId == blocked.RecordId),
                "Input replay must not silently revive its terminal derivative.");
            await store.ResolveOperationsAsync(root, blocked.RecordId, OutboxOperationAction.Replay,
                "replay-cloud", "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false);
            for (var index = 0; index < 2; index++)
            {
                var input = await ClaimAsync(store, root).ConfigureAwait(false);
                Assert.AreNotEqual(EnvironmentalObservationKind.CloudCover, input.Record.Observation.Value.Kind);
                await AckAsync(store, root, input, clock.Now).ConfigureAwait(false);
            }
            var cloud = await ClaimAsync(store, root).ConfigureAwait(false);
            Assert.AreEqual(derived.ObservationId, cloud.Record.Observation.ObservationId);
            Assert.AreEqual(hash, cloud.Record.ContentSha256);
            Assert.AreEqual(1, cloud.Record.AttemptCount);
            await AckAsync(store, root, cloud, clock.Now).ConfigureAwait(false);
            Assert.AreEqual(0, (await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task RestartRepairsBlockedDescendantsWithoutBlockingIndependentDelivery()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var descendant = derived with
            {
                ObservationId = Guid.NewGuid(),
                Lineage = [new(
                EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(derived), derived.ObservationId)]
            };
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            var independent = EnvironmentalAdapterContractsTests.Fact("independent", EnvironmentalObservationKind.AirTemperature, 20);
            using (var store = new SqliteEnvironmentalObservationOutbox(clock))
            {
                foreach (var input in inputs) await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false);
                await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
                await store.CommitLocalAsync(root, descendant, target, CancellationToken.None).ConfigureAwait(false);
                for (var index = 0; index < 2; index++)
                    await store.TerminalAsync(root, await ClaimAsync(store, root).ConfigureAwait(false), "maximum-attempts-exceeded", CancellationToken.None).ConfigureAwait(false);
                var letters = await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(2, letters.Count(item => item.Reason == "lineage-input-undeliverable"));
                foreach (var input in letters.Where(item => item.Reason != "lineage-input-undeliverable"))
                    await store.ResolveOperationsAsync(root, input.RecordId, OutboxOperationAction.Abandon,
                        Guid.NewGuid().ToString("N"), "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
                await store.CommitLocalAsync(root, independent, target, CancellationToken.None).ConfigureAwait(false);
            }
            // Recreate the pending state written by the earlier implementation, without changing its immutable payloads.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE environmental_observation_outbox SET status = 'pending', last_reason = NULL WHERE last_reason = 'lineage-input-undeliverable';";
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            using var restarted = new SqliteEnvironmentalObservationOutbox(clock);
            var repaired = await restarted.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, repaired);
            Assert.IsTrue(repaired.All(item => item.Reason == "lineage-input-undeliverable" && item.AttemptCount == 0));
            Assert.AreEqual(independent.ObservationId, (await ClaimAsync(restarted, root).ConfigureAwait(false)).Record.Observation.ObservationId);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task CapacityWaitingDerivativeCanBeAbandonedAndItsHistoryCanExpire()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock, maximumRecords: 2);
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            foreach (var input in inputs) await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false);
            var result = await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalObservationProjectionDisposition.Waiting, result.ProjectionDisposition);
            for (var index = 0; index < 2; index++)
            {
                var input = await ClaimAsync(store, root).ConfigureAwait(false);
                await store.TerminalAsync(root, input, "maximum-attempts-exceeded", CancellationToken.None).ConfigureAwait(false);
                await store.AbandonAsync(root, input.Record.RecordId, "owner", "accepted-loss", CancellationToken.None).ConfigureAwait(false);
            }
            Assert.AreEqual(1, await store.ProjectWaitingAsync(root, 100, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
            var letter = (await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false)).Single();
            Assert.AreEqual("lineage-input-undeliverable", letter.Reason);
            await store.AbandonAsync(root, letter.RecordId, "owner", "accepted-loss", CancellationToken.None).ConfigureAwait(false);
            clock.Now = Epoch.AddDays(40);
            var first = await store.RetainLocalAsync(root, Epoch.AddDays(1), 10, CancellationToken.None).ConfigureAwait(false);
            var second = await store.RetainLocalAsync(root, Epoch.AddDays(1), 10, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(3, first.RemovedCount + second.RemovedCount);
            Assert.IsEmpty((await store.ReadLocalPageAsync(root, null, 10, null, CancellationToken.None).ConfigureAwait(false)).Items);
            Assert.AreEqual(0, (await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task DependencyFailurePreservesAnActiveLeaseThenSettlesItAfterExpiry()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            foreach (var input in inputs) await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            var first = await ClaimAsync(store, root).ConfigureAwait(false);
            var second = await ClaimAsync(store, root).ConfigureAwait(false);
            // Represent a still-active lease written by an earlier sender that did not gate claims on acknowledgement.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var lease = connection.CreateCommand();
                lease.CommandText = "UPDATE environmental_observation_outbox SET status='leased', attempt_count=1, lease_owner='older-worker', lease_token='older-token', lease_expires_unix_ms=$expires WHERE observation_id=$observation;";
                lease.Parameters.AddWithValue("$expires", Epoch.AddSeconds(10).ToUnixTimeMilliseconds());
                lease.Parameters.AddWithValue("$observation", derived.ObservationId.ToString("D"));
                Assert.AreEqual(1, await lease.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            await store.TerminalAsync(root, first, "maximum-attempts-exceeded", CancellationToken.None).ConfigureAwait(false);
            await store.RetryAsync(root, second, Epoch.AddMinutes(1), "http-503", CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(1, (await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).LeasedCount);
            clock.Now = Epoch.AddSeconds(11);
            Assert.IsNull(await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
            var letters = await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, letters);
            Assert.IsTrue(letters.Any(item => item.Reason == "lineage-input-undeliverable"));
            Assert.AreEqual(0, (await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).LeasedCount);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    [DataRow("site")]
    [DataRow("agent")]
    [DataRow("rig")]
    public async Task InputsBoundToAnotherTargetCannotLeaveADerivativePending(string scope)
    {
        var root = Root();
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(new Clock());
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var original = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid(), "rig-a");
            var changed = scope switch
            {
                "site" => original with { ObservatoryId = Guid.NewGuid() },
                "agent" => original with { DevicePublicId = Guid.NewGuid() },
                _ => original with { RigId = "rig-b" }
            };
            foreach (var input in inputs) await store.CommitLocalAsync(root, input, original, CancellationToken.None).ConfigureAwait(false);
            for (var index = 0; index < 2; index++)
                await AckAsync(store, root, await ClaimAsync(store, root).ConfigureAwait(false), Epoch).ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived, changed, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
            var letters = await store.ReadDeadLettersAsync(root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, letters);
            Assert.AreEqual("lineage-input-undeliverable", letters[0].Reason);
            Assert.AreEqual(0, (await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).PendingCount);
        }
        finally { Delete(root); }
    }
}
