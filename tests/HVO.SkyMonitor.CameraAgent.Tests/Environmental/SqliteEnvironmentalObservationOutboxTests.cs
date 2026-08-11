using System.Text.Json;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class SqliteEnvironmentalObservationOutboxTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);
    private string? _root;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"environment-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublisherFreezesResolvedTargetBeforeDurableAcknowledgementAndRestart()
    {
        var firstTarget = new EnvironmentalObservationResolvedTarget(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var resolver = new MutableTargetResolver(firstTarget);
        using (var firstProcess = new SqliteEnvironmentalObservationOutbox())
        using (var telemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(),
            TimeProvider.System))
        {
            var publisher = new EnvironmentalObservationPublisher(
                resolver,
                firstProcess,
                new EnvironmentalObservationDeliveryWakeup(),
                new EnvironmentalObservationDeliveryState(),
                telemetry,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = _root!,
                    EnvironmentalDelivery = new EnvironmentalObservationDeliveryOptions { Enabled = false }
                }),
                TimeProvider.System);
            var published = await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, published.Disposition);
            resolver.Target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox();
        var lease = await restarted.ClaimAsync(_root!, "restart", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(lease);
        Assert.AreEqual(firstTarget.ObservatoryId, lease.Record.Observation.Target.SiteId);
        Assert.AreEqual(firstTarget.DevicePublicId, lease.Record.Observation.Target.AgentId);
        Assert.IsNull(lease.Record.Observation.Target.RigId);
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeContentSha256(lease.Record.Observation),
            lease.Record.ContentSha256);
    }

    [TestMethod]
    public async Task Publisher_WhenCentralIntegrationIsDisabled_CommitsTargetlessLocalHistoryWithoutDelivery()
    {
        var resolver = new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(
            Guid.NewGuid(), Guid.NewGuid()));
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            resolver,
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = _root!,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            }),
            TimeProvider.System);

        var fact = CreateFact(Guid.NewGuid());
        var result = await publisher.PublishAsync(fact).ConfigureAwait(false);
        var replay = await publisher.PublishAsync(fact).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, result.Disposition);
        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Duplicate, replay.Disposition);
        Assert.IsNull(result.Observation);
        Assert.AreEqual(0, resolver.ResolveCount);
        Assert.IsTrue(File.Exists(Path.Combine(_root!, ".environment", "environmental-observation-outbox.db")));
        Assert.AreEqual(1, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(0, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task Publisher_CentralResolutionFailureCannotRejectLocalHistory()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            new ThrowingTargetResolver(),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);

        var result = await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, result.Disposition);
        Assert.IsNull(result.Observation);
        Assert.AreEqual(1, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(0, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(EnvironmentalObservationDeliveryAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.AreEqual("central-projection-unavailable", state.Snapshot.Reason);
    }

    [TestMethod]
    public async Task CancellationDuringOptionalProjectionCannotRejectCommittedLocalHistory()
    {
        using var cancellation = new CancellationTokenSource();
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            new CancellingTargetResolver(cancellation),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);

        var result = await publisher.PublishAsync(CreateFact(Guid.NewGuid()), cancellation.Token).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, result.Disposition);
        Assert.AreEqual(1, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(0, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual("central-projection-unavailable", state.Snapshot.Reason);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ThrowingDeliveryMetricListenerCannotRejectCommittedLocalHistory()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EnvironmentalObservationDeliveryTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(static (
            Instrument instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state) => throw new InvalidOperationException("listener failure"));
        listener.Start();
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var deliveryState = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(deliveryState, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid())),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            deliveryState,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);

        var result = await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, result.Disposition);
        Assert.AreEqual(1, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(1, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task Publisher_ReplayAfterProvisioningFreezesTargetAndProjectsExistingLocalFact()
    {
        var resolver = new MutableTargetResolver(null);
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            resolver,
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);
        var fact = CreateFact(Guid.NewGuid());

        var unprovisioned = await publisher.PublishAsync(fact).ConfigureAwait(false);
        resolver.Target = new EnvironmentalObservationResolvedTarget(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var recovered = await publisher.PublishAsync(fact).ConfigureAwait(false);

        Assert.IsNull(unprovisioned.Observation);
        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Duplicate, recovered.Disposition);
        Assert.IsNotNull(recovered.Observation);
        Assert.AreEqual(1, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(1, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task FirstProjectionOfExistingLocalFactReportsActualEnqueueDisposition()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var fact = CreateFact(Guid.NewGuid());
        var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());

        var local = await outbox.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);
        var projected = await outbox.CommitLocalAsync(_root!, fact, target, CancellationToken.None).ConfigureAwait(false);
        var replay = await outbox.CommitLocalAsync(_root!, fact, target, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(local.DeliveryDisposition);
        Assert.AreEqual(LocalEnvironmentalObservationCommitDisposition.Duplicate, projected.Disposition);
        Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Enqueued, projected.DeliveryDisposition);
        Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Duplicate, replay.DeliveryDisposition);
    }

    [TestMethod]
    public async Task ProjectionRecoveryAssignsPreviouslyUnprovisionedFactsWithoutProducerReplay()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var fact = CreateFact(Guid.NewGuid());
        await outbox.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);

        var assigned = await outbox.AssignUnprojectedAsync(
            _root!,
            new EnvironmentalObservationResolvedTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            10,
            CancellationToken.None).ConfigureAwait(false);
        var lease = await outbox.ClaimAsync(
            _root!, "recovery", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, assigned);
        Assert.IsNotNull(lease);
        Assert.AreEqual(fact.ObservationId, lease.Record.Observation.ObservationId);
        Assert.AreEqual(1, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task AcknowledgedProjectionReplayReturnsTheFrozenDeliveryObservation()
    {
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        var fact = CreateFact(Guid.NewGuid());
        var target = new EnvironmentalObservationResolvedTarget(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "rig-original");
        await outbox.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, await outbox.AssignUnprojectedAsync(
            _root!, target, 10, CancellationToken.None).ConfigureAwait(false));
        var lease = await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var acknowledgement = new FaithfulCentralReceiver(time).Ingest(lease.Record.Observation);
        await outbox.AcknowledgeAsync(_root!, lease, acknowledgement, CancellationToken.None).ConfigureAwait(false);
        var replayTarget = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid(), "rig-changed");

        var replay = await outbox.CommitLocalAsync(
            _root!, fact, replayTarget, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalEnvironmentalObservationCommitDisposition.Duplicate, replay.Disposition);
        Assert.AreEqual(EnvironmentalObservationProjectionDisposition.Acknowledged, replay.ProjectionDisposition);
        Assert.IsNotNull(replay.DeliveryObservation);
        Assert.AreEqual(target.ObservatoryId, replay.DeliveryObservation.Target.SiteId);
        Assert.AreEqual(target.DevicePublicId, replay.DeliveryObservation.Target.AgentId);
        Assert.AreEqual(target.RigId, replay.DeliveryObservation.Target.RigId);
        Assert.AreEqual(fact.ObservationId, replay.DeliveryObservation.ObservationId);
        Assert.AreEqual(0, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        Assert.IsNull(await outbox.ClaimAsync(
            _root!, "worker-2", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalJournalRejectsConflictsAndCorruptCommittedDuplicates()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var fact = CreateFact(Guid.NewGuid());

        Assert.AreEqual(
            LocalEnvironmentalObservationCommitDisposition.Committed,
            (await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(
            LocalEnvironmentalObservationCommitDisposition.Duplicate,
            (await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false)).Disposition);
        await Assert.ThrowsAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await store.CommitLocalAsync(
                _root!,
                fact with { Value = fact.Value with { NumericValue = fact.Value.NumericValue + 1 } },
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE environmental_observation_journal SET payload = zeroblob(payload_bytes);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalJournalRejectsChangedContentForAnExistingSourceIdentity()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var first = CreateFact(Guid.NewGuid());
        await store.CommitLocalAsync(_root!, first, CancellationToken.None).ConfigureAwait(false);
        using var document = JsonDocument.Parse("{\"normalizer\":\"changed-v2\"}");
        var parameters = document.RootElement.Clone();
        var changed = first with
        {
            ObservationId = Guid.NewGuid(),
            Source = first.Source with
            {
                Provenance = first.Source.Provenance with
                {
                    Parameters = parameters,
                    ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters)
                }
            }
        };

        await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await store.CommitLocalAsync(_root!, changed, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CentralAcknowledgementRemovesDeliveryCopyButRetainsLocalHistory()
    {
        var time = new MutableTimeProvider(Epoch);
        using var store = new SqliteEnvironmentalObservationOutbox(time);
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, time);
        var publisher = new EnvironmentalObservationPublisher(
            new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            store,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            time);
        _ = await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false);
        var lease = (await store.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;

        var acknowledgement = new FaithfulCentralReceiver(time).Ingest(lease.Record.Observation);
        await store.AcknowledgeAsync(_root!, lease, acknowledgement, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, (await store.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(1, (await store.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task LocalJournalCapacityIsIndependentAndDurablyCounted()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(
            new MutableTimeProvider(Epoch), maximumLocalRecords: 1);
        await store.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsAsync<LocalEnvironmentalObservationCapacityException>(async () =>
            await store.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);

        var snapshot = await store.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.StoredCount);
        Assert.AreEqual(1, snapshot.OverflowCount);
    }

    [TestMethod]
    public async Task LocalPressureRetentionRemovesExpiredUnpinnedFactsBeforeRejectingNewHistory()
    {
        var time = new MutableTimeProvider(Epoch);
        using var store = new SqliteEnvironmentalObservationOutbox(
            time,
            maximumLocalRecords: 1,
            localRetentionDays: 31,
            localRetentionBatchSize: 1);
        var first = await store.CommitLocalAsync(
            _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromDays(1));

        var second = await store.CommitLocalAsync(
            _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, (await store.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.IsNull(await store.ReadLocalDetailAsync(
            _root!, first.Record.RecordId, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await store.ReadLocalDetailAsync(
            _root!, second.Record.RecordId, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalHistoryUsesBoundedKeysetPagingAndTemporalCandidates()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var facts = Enumerable.Range(1, 3)
            .Select(index => CreateFact(Guid.Parse($"20000000-0000-0000-0000-{index:D12}")) with
            {
                ObservedAtUtc = Epoch.AddSeconds(index)
            })
            .ToArray();
        foreach (var fact in facts)
        {
            await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);
        }

        var first = await store.ReadLocalPageAsync(
            _root!, EnvironmentalObservationKind.RelativeHumidity, 2, null, CancellationToken.None)
            .ConfigureAwait(false);
        var second = await store.ReadLocalPageAsync(
            _root!, EnvironmentalObservationKind.RelativeHumidity, 2, first.NextCursor, CancellationToken.None)
            .ConfigureAwait(false);
        var candidates = await store.ReadLocalCandidatesAsync(
            _root!, EnvironmentalObservationKind.RelativeHumidity, null, Epoch, Epoch.AddMinutes(1), 10,
            CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, first.Items);
        Assert.AreEqual(facts[2].ObservationId, first.Items[0].Fact.ObservationId);
        Assert.IsNotNull(first.NextCursor);
        Assert.HasCount(1, second.Items);
        Assert.AreEqual(facts[0].ObservationId, second.Items[0].Fact.ObservationId);
        Assert.IsNull(second.NextCursor);
        Assert.HasCount(3, candidates);
        var detail = await store.ReadLocalDetailAsync(
            _root!, first.Items[0].RecordId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(detail);
        Assert.AreEqual(first.Items[0].ContentSha256, detail!.ContentSha256);
        Assert.IsEmpty((await store.ReadLocalPageAsync(
            _root!, EnvironmentalObservationKind.AirTemperature, 10, null, CancellationToken.None)
            .ConfigureAwait(false)).Items);
    }

    [TestMethod]
    public async Task AcquisitionAttemptsAndSourceRuntimeSurviveRestartWithoutManufacturingObservations()
    {
        using var document = JsonDocument.Parse("{}");
        var source = new EnvironmentalSourceDescriptor(
            "temperature",
            "VirtualEnvironment",
            EnvironmentalObservationKind.AirTemperature,
            true,
            [EnvironmentalAcquisitionTrigger.Periodic],
            Epoch,
            30,
            3,
            120,
            45,
            null,
            document.RootElement.Clone());
        var observationId = Guid.NewGuid();
        using (var store = new SqliteEnvironmentalObservationOutbox())
        {
            await store.RecordAttemptAsync(
                _root!,
                source,
                new EnvironmentalAcquisitionReceipt(
                    source.Id,
                    EnvironmentalAcquisitionTrigger.Periodic,
                    EnvironmentalAcquisitionDisposition.Produced,
                    "produced",
                    observationId,
                    Epoch,
                    Epoch.AddMilliseconds(10),
                    Epoch,
                    Epoch.AddSeconds(45)),
                null,
                null,
                CancellationToken.None).ConfigureAwait(false);
            await store.UpdateSourceScheduleAsync(
                _root!, source, Epoch.AddSeconds(60), CancellationToken.None).ConfigureAwait(false);
            await store.RecordAttemptAsync(
                _root!,
                source,
                new EnvironmentalAcquisitionReceipt(
                    source.Id,
                    EnvironmentalAcquisitionTrigger.Periodic,
                    EnvironmentalAcquisitionDisposition.Missing,
                    "source-missing",
                    null,
                    Epoch.AddSeconds(30),
                    Epoch.AddSeconds(30).AddMilliseconds(10)),
                null,
                null,
                CancellationToken.None).ConfigureAwait(false);
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox();
        var state = (await restarted.ReadSourceStatesAsync(_root!, CancellationToken.None).ConfigureAwait(false)).Single();
        var attempts = await restarted.ReadAttemptsAsync(_root!, 10, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, state.LastDisposition);
        Assert.AreEqual(observationId, state.LastObservationId);
        Assert.AreEqual(Epoch, state.LastObservedUtc);
        Assert.AreEqual(Epoch.AddSeconds(45), state.LastStaleAfterUtc);
        Assert.AreEqual(1, state.ConsecutiveFailures);
        Assert.AreEqual(Epoch.AddSeconds(60), state.NextPollUtc);
        Assert.HasCount(2, attempts);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, attempts[0].Disposition);
        Assert.IsNull(attempts[0].ObservationId);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, attempts[1].Disposition);
    }

    [TestMethod]
    public async Task OnDemandCommandIsLeasedConflictCheckedAndDurablyReplayable()
    {
        const string key = "environment-command-1";
        var payload = new string('A', 64);
        var receipt = new EnvironmentalAcquisitionReceipt(
            "temperature",
            EnvironmentalAcquisitionTrigger.OnDemand,
            EnvironmentalAcquisitionDisposition.Missing,
            "source-missing",
            null,
            Epoch,
            Epoch.AddMilliseconds(10));
        using (var store = new SqliteEnvironmentalObservationOutbox(new MutableTimeProvider(Epoch)))
        {
            var claim = await store.ClaimOnDemandAsync(
                _root!, key, payload, "temperature", "owner-1", "manual-check", Epoch,
                TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Claimed, claim.Disposition);
            Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Busy, (await store.ClaimOnDemandAsync(
                _root!, key, payload, "temperature", "owner-1", "manual-check", Epoch.AddSeconds(1),
                TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)).Disposition);
            await Assert.ThrowsExactlyAsync<EnvironmentalOnDemandCommandConflictException>(async () =>
                await store.ClaimOnDemandAsync(
                    _root!, key, new string('B', 64), "temperature", "owner-1", "manual-check", Epoch,
                    TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await store.CompleteOnDemandAsync(
                _root!, key, claim.LeaseToken, receipt, CancellationToken.None).ConfigureAwait(false);
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox(new MutableTimeProvider(Epoch.AddMinutes(2)));
        var replay = await restarted.ClaimOnDemandAsync(
            _root!, key, payload, "temperature", "owner-1", "manual-check", Epoch.AddMinutes(2),
            TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Completed, replay.Disposition);
        Assert.AreEqual(receipt, replay.Receipt);
        Assert.AreEqual(Epoch, replay.ObservedAtUtc);
    }

    [TestMethod]
    public async Task CaptureRegimeChangeSurvivesRestartAndCaptureReplay()
    {
        var nightCapture = Guid.Parse("91000000-0000-0000-0000-000000000001");
        var twilightCapture = Guid.Parse("91000000-0000-0000-0000-000000000002");
        using (var store = new SqliteEnvironmentalObservationOutbox())
        {
            Assert.IsFalse(await store.RecordCaptureRegimeAsync(
                _root!, 1, nightCapture, CaptureSolarRegime.Night, Epoch, CancellationToken.None).ConfigureAwait(false));
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox();
        Assert.IsTrue(await restarted.RecordCaptureRegimeAsync(
            _root!, 2, twilightCapture, CaptureSolarRegime.Twilight, Epoch.AddMinutes(1), CancellationToken.None)
            .ConfigureAwait(false));
        Assert.IsFalse(await restarted.RecordCaptureRegimeAsync(
            _root!, 2, twilightCapture, CaptureSolarRegime.Twilight, Epoch.AddMinutes(1), CancellationToken.None)
            .ConfigureAwait(false));
        Assert.IsFalse(await restarted.RecordCaptureRegimeAsync(
            _root!, 1, nightCapture, CaptureSolarRegime.Night, Epoch, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await restarted.RecordCaptureRegimeAsync(
                _root!, 2, twilightCapture, CaptureSolarRegime.Day, Epoch.AddMinutes(1), CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EnqueueExactDuplicateConvergesAndConflictingIdentityIsRejected()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var observation = CreateObservation(Guid.NewGuid());

        var first = await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
        var conflict = observation with { Value = observation.Value with { NumericValue = 70 } };

        Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Enqueued, first);
        Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Duplicate, duplicate);
        await Assert.ThrowsAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await outbox.EnqueueAsync(_root!, conflict, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var snapshot = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.StoredCount);
        Assert.AreEqual(EnvironmentalObservationJson.Serialize(observation).Length, snapshot.StoredBytes);
    }

    [TestMethod]
    public async Task CorruptedCommittedDuplicateIsNotAcknowledgedAsDurable()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var observation = CreateObservation(Guid.NewGuid());
        await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE environmental_observation_outbox SET payload = zeroblob(payload_bytes);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CountAndByteCapacityAreDurablyBounded()
    {
        var first = CreateObservation(Guid.NewGuid());
        using (var countBound = new SqliteEnvironmentalObservationOutbox(maximumRecords: 1))
        {
            await countBound.EnqueueAsync(_root!, first, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsAsync<EnvironmentalObservationOutboxCapacityException>(async () =>
                await countBound.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            var snapshot = await countBound.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, snapshot.StoredCount);
            Assert.AreEqual(1, snapshot.OverflowCount);
        }

        var byteRoot = Path.Combine(_root!, "bytes");
        Directory.CreateDirectory(byteRoot);
        var payloadBytes = EnvironmentalObservationJson.Serialize(first).Length;
        using var byteBound = new SqliteEnvironmentalObservationOutbox(maximumRecords: 10, maximumBytes: payloadBytes);
        await byteBound.EnqueueAsync(byteRoot, first, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<EnvironmentalObservationOutboxCapacityException>(async () =>
            await byteBound.EnqueueAsync(byteRoot, CreateObservation(Guid.NewGuid()), CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(payloadBytes, (await byteBound.GetSnapshotAsync(byteRoot, CancellationToken.None)
            .ConfigureAwait(false)).StoredBytes);
    }

    [TestMethod]
    public async Task PublisherKeepsLocalHistoryWhenDeliveryCapacityRejectsProjection()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox(maximumRecords: 1);
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);
        var firstFact = CreateFact(Guid.NewGuid());
        var secondFact = CreateFact(Guid.NewGuid());
        await publisher.PublishAsync(firstFact).ConfigureAwait(false);

        var second = await publisher.PublishAsync(secondFact).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, second.Disposition);
        Assert.IsNull(second.Observation);
        Assert.AreEqual(EnvironmentalObservationDeliveryAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.AreEqual("capacity-exhausted", state.Snapshot.Reason);
        Assert.AreEqual(2, (await outbox.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(1, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);

        var firstLease = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.AcknowledgeAsync(
            _root!, firstLease, new FaithfulCentralReceiver(TimeProvider.System).Ingest(firstLease.Record.Observation),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, await outbox.ProjectWaitingAsync(_root!, 10, CancellationToken.None).ConfigureAwait(false));
        var recovered = await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(secondFact.ObservationId, recovered.Record.Observation.ObservationId);
    }

    [TestMethod]
    public async Task AbandoningLinkedDeliveryResetsProjectionForIndependentRecovery()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var state = new EnvironmentalObservationDeliveryState();
        using var deliveryTelemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            deliveryTelemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);
        var fact = CreateFact(Guid.NewGuid());
        await publisher.PublishAsync(fact).ConfigureAwait(false);
        var lease = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.TerminalAsync(_root!, lease, "operator-review", CancellationToken.None).ConfigureAwait(false);
        await outbox.AbandonAsync(
            _root!, lease.Record.RecordId, "owner", "accepted-loss", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, await outbox.ProjectWaitingAsync(
            _root!, 10, CancellationToken.None).ConfigureAwait(false));
        var recovered = await outbox.ClaimAsync(
            _root!, "recovery", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(fact.ObservationId, recovered.Record.Observation.ObservationId);
    }

    [TestMethod]
    public async Task SchemaUsesWalStrictVersionedFullDurabilityWithoutCredentialColumns()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(3L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA journal_mode;";
        Assert.AreEqual("wal", (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA synchronous;";
        Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_outbox';";
        StringAssert.Contains((string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!, "STRICT", StringComparison.Ordinal);
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('environmental_observation_outbox');";
        var columns = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        Assert.IsFalse(columns.Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(columns.Contains("credential", StringComparison.OrdinalIgnoreCase));
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_outbox';";
        var schema = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        StringAssert.Contains(schema, "payload_bytes = length(payload)", StringComparison.Ordinal);
        StringAssert.Contains(schema, "status = 'leased' AND lease_owner IS NOT NULL", StringComparison.Ordinal);
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_journal';";
        var journalSchema = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        StringAssert.Contains(journalSchema, "STRICT", StringComparison.Ordinal);
        StringAssert.Contains(journalSchema, "payload_bytes = length(payload)", StringComparison.Ordinal);
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('environmental_observation_journal');";
        var journalColumns = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        Assert.IsFalse(journalColumns.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Version2MigrationBackfillsSurvivingDeliveryRowsIntoLocalHistoryIdempotently()
    {
        var observation = CreateObservation(Guid.NewGuid());
        var movedTarget = observation with
        {
            Target = new EnvironmentalObservationTarget(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"))
        };
        using (var current = new SqliteEnvironmentalObservationOutbox())
        {
            await current.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
            await current.EnqueueAsync(_root!, movedTarget, CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_environment_delivery_local_record;
                ALTER TABLE environmental_observation_outbox DROP COLUMN local_record_id;
                DROP TABLE environmental_observation_central_projection;
                DROP TABLE environmental_capture_association_evidence;
                DROP TABLE environmental_capture_associations;
                DROP TABLE environmental_observation_local_lineage;
                DROP TABLE environmental_on_demand_commands;
                DROP TABLE environmental_acquisition_attempts;
                DROP TABLE environmental_capture_regime_state;
                DROP TABLE environmental_source_runtime;
                DROP TABLE environmental_observation_journal;
                DROP TABLE environmental_observation_journal_metadata;
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();

        using (var migrated = new SqliteEnvironmentalObservationOutbox())
        using (var concurrent = new SqliteEnvironmentalObservationOutbox())
        {
            var snapshots = await Task.WhenAll(
                migrated.GetLocalSnapshotAsync(_root!, CancellationToken.None).AsTask(),
                concurrent.GetLocalSnapshotAsync(_root!, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.IsTrue(snapshots.All(static snapshot => snapshot.StoredCount == 1));
            Assert.AreEqual(2, (await migrated.GetSnapshotAsync(_root!, CancellationToken.None)
                .ConfigureAwait(false)).StoredCount);
        }
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(3L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
            command.CommandText = "SELECT COUNT(*) FROM environmental_observation_outbox WHERE local_record_id IS NOT NULL;";
            Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        }
        SqliteConnection.ClearAllPools();
        using var restarted = new SqliteEnvironmentalObservationOutbox();
        Assert.AreEqual(1, (await restarted.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        var receiver = new FaithfulCentralReceiver(TimeProvider.System);
        for (var index = 0; index < 2; index++)
        {
            var lease = await restarted.ClaimAsync(
                _root!, "migration", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await restarted.AcknowledgeAsync(
                _root!, lease, receiver.Ingest(lease.Record.Observation), CancellationToken.None).ConfigureAwait(false);
        }
        Assert.AreEqual(0, (await restarted.GetSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task SymbolicLinkDatabaseDirectoryIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Symbolic-link creation is not generally available to unprivileged Windows test processes.");
        }
        var external = Path.Combine(Path.GetTempPath(), $"environment-outbox-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(Path.Combine(_root!, ".environment"), external);
        try
        {
            using var outbox = new SqliteEnvironmentalObservationOutbox();
            await Assert.ThrowsAsync<IOException>(async () =>
                await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeferredRetryDoesNotBlockNewerPendingWorkAndSettlementsAreFenced()
    {
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        var first = CreateObservation(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var second = CreateObservation(Guid.Parse("10000000-0000-0000-0000-000000000002"));
        await outbox.EnqueueAsync(_root!, first, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, second, CancellationToken.None).ConfigureAwait(false);
        var firstLease = (await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false))!;
        await outbox.RetryAsync(
            _root!, firstLease, time.GetUtcNow().AddMinutes(5), "offline", CancellationToken.None).ConfigureAwait(false);

        var secondLease = await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(secondLease);
        Assert.AreEqual(second.ObservationId, secondLease.Record.Observation.ObservationId);
        await outbox.QuarantineAsync(_root!, secondLease, "invalid", CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<EnvironmentalObservationLeaseLostException>(async () =>
            await outbox.TerminalAsync(_root!, secondLease, "stale-settlement", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsNull(await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(first.ObservationId, (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!.Record.Observation.ObservationId);
    }

    [TestMethod]
    public async Task ExpiredLeaseAfterRestartRedeliversAndAcknowledgementLossConverges()
    {
        var time = new MutableTimeProvider(Epoch);
        var observation = CreateObservation(Guid.NewGuid());
        var central = new FaithfulCentralReceiver(time);
        EnvironmentalObservationOutboxLease abandoned;
        EnvironmentalObservationAcknowledgement accepted;
        using (var firstProcess = new SqliteEnvironmentalObservationOutbox(time))
        {
            await firstProcess.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
            abandoned = (await firstProcess.ClaimAsync(
                _root!, "worker-1", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
            accepted = central.Ingest(abandoned.Record.Observation);
        }
        time.Advance(TimeSpan.FromMinutes(1));
        using var restarted = new SqliteEnvironmentalObservationOutbox(time);

        var recovered = (await restarted.ClaimAsync(
            _root!, "worker-2", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;

        Assert.AreEqual(abandoned.Record.RecordId, recovered.Record.RecordId);
        Assert.AreEqual(2, recovered.Record.AttemptCount);
        var duplicate = central.Ingest(recovered.Record.Observation);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Accepted, accepted.Disposition);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Duplicate, duplicate.Disposition);
        Assert.AreEqual(accepted.ReceivedAtUtc, duplicate.ReceivedAtUtc);
        Assert.AreEqual(1, central.Count);
        await Assert.ThrowsAsync<EnvironmentalObservationLeaseLostException>(async () =>
            await restarted.RetryAsync(
                _root!, abandoned, time.GetUtcNow().AddMinutes(1), "stale-worker", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        await restarted.AcknowledgeAsync(_root!, recovered, duplicate, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0, (await restarted.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task ExpiredLeaseCannotSettleWithoutAReclaim()
    {
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var lease = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<EnvironmentalObservationLeaseLostException>(async () =>
            await outbox.RetryAsync(
                _root!, lease, time.GetUtcNow().AddMinutes(1), "late", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AuthenticationFailureStopsBatchAndRetryLimitTerminalizes()
    {
        const string sensitive = "secret-device-site-payload-url";
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var authentication = new StubTransport(new(
            EnvironmentalObservationTransportDisposition.AuthenticationBlocked,
            sensitive,
            RetryAfter: TimeSpan.Zero));
        var logs = new CapturingLogger<EnvironmentalObservationDeliveryService>();
        var retryOptions = new EnvironmentalObservationDeliveryOptions
        {
            BatchSize = 100,
            RetryInitialDelaySeconds = 5,
            RetryMaximumDelaySeconds = 60,
            MaximumAttempts = 10
        };
        using (var telemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(),
            time))
        {
            var service = CreateDeliveryService(authentication, outbox, telemetry, time, retryOptions, logs);
            await service.DrainBatchAsync(_root!, retryOptions, CancellationToken.None).ConfigureAwait(false);
        }

        Assert.AreEqual(1, authentication.SendCount);
        var afterAuthentication = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, afterAuthentication.RetryCount);
        Assert.AreEqual(2, afterAuthentication.PendingCount);
        Assert.IsFalse(logs.Messages.Any(message => message.Contains(sensitive, StringComparison.Ordinal)));

        var pendingLease = await outbox.ClaimAsync(
            _root!, "test", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pendingLease);
        await outbox.TerminalAsync(_root!, pendingLease, "test-cleanup", CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(5));
        var retry = new StubTransport(new(EnvironmentalObservationTransportDisposition.Retry, "offline"));
        var terminalOptions = new EnvironmentalObservationDeliveryOptions
        {
            BatchSize = 1,
            MaximumAttempts = 1
        };
        using (var telemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(),
            time))
        {
            var service = CreateDeliveryService(retry, outbox, telemetry, time, terminalOptions);
            await service.DrainBatchAsync(_root!, terminalOptions, CancellationToken.None).ConfigureAwait(false);
        }
        var terminal = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, terminal.TerminalCount);
        Assert.AreEqual(0, terminal.RetryCount);
    }

    [TestMethod]
    public void HistoricalOverflowDoesNotPoisonRecoveredHealth()
    {
        var snapshot = new EnvironmentalObservationOutboxSnapshot(
            0, 0, 0, 0, 0, 0, 0, 0, 3, null, Epoch);

        Assert.AreEqual(
            EnvironmentalObservationDeliveryAvailability.Healthy,
            EnvironmentalObservationDeliveryService.DetermineAvailability(snapshot));
    }

    [TestMethod]
    public async Task DeadLettersCanBeInspectedReplayedAndAbandonedToRecoverCapacity()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox(maximumRecords: 2);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var quarantine = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.QuarantineAsync(_root!, quarantine, "invalid-envelope", CancellationToken.None).ConfigureAwait(false);
        var terminal = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.TerminalAsync(_root!, terminal, "http-client", CancellationToken.None).ConfigureAwait(false);

        var deadLetters = await outbox.ReadDeadLettersAsync(_root!, 10, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, deadLetters.Count);
        Assert.AreEqual(EnvironmentalObservationDeadLetterStatus.Quarantined, deadLetters[0].Status);
        Assert.AreEqual(EnvironmentalObservationDeadLetterStatus.Terminal, deadLetters[1].Status);
        await outbox.ReplayAsync(
            _root!, deadLetters[0].RecordId, "test-operator", "corrected", CancellationToken.None).ConfigureAwait(false);
        await outbox.AbandonAsync(
            _root!, deadLetters[1].RecordId, "test-operator", "accepted-loss", CancellationToken.None).ConfigureAwait(false);
        var snapshot = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.StoredCount);
        Assert.AreEqual(1, snapshot.PendingCount);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root!, ".environment", "environmental-observation-outbox.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT actor), COUNT(DISTINCT reason) FROM environmental_observation_outbox_audit;";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(2, reader.GetInt64(0));
        Assert.AreEqual(1, reader.GetInt64(1));
        Assert.AreEqual(2, reader.GetInt64(2));
    }

    [TestMethod]
    public async Task OperationsProjectionAndResolution_PreserveCapacityAuditAndDurableIdempotency()
    {
        const string replayKey = "environment-replay-1";
        const string abandonKey = "environment-abandon-1";
        long replayId;
        long abandonId;
        using (var outbox = new SqliteEnvironmentalObservationOutbox(maximumRecords: 2))
        {
            await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
            await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
            var firstLease = await outbox.ClaimAsync(
                _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(firstLease);
            await outbox.QuarantineAsync(_root!, firstLease, "invalid-envelope", CancellationToken.None).ConfigureAwait(false);
            var secondLease = await outbox.ClaimAsync(
                _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(secondLease);
            await outbox.TerminalAsync(_root!, secondLease, "upstream-rejected", CancellationToken.None).ConfigureAwait(false);

            var firstPage = await outbox.ReadOperationsPageAsync(_root!, 1, null, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, firstPage.Items);
            Assert.IsNotNull(firstPage.NextCursor);
            var secondPage = await outbox.ReadOperationsPageAsync(
                _root!, 1, firstPage.NextCursor, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, secondPage.Items);
            replayId = firstLease.Record.RecordId;
            abandonId = secondLease.Record.RecordId;

            Assert.AreEqual(OutboxOperationDisposition.Applied, await outbox.ResolveOperationsAsync(
                _root!, replayId, OutboxOperationAction.Replay, replayKey, "owner", "upstream-recovered",
                CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(OutboxOperationDisposition.Applied, await outbox.ResolveOperationsAsync(
                _root!, abandonId, OutboxOperationAction.Abandon, abandonKey, "owner", "operator-approved-loss",
                CancellationToken.None).ConfigureAwait(false));
            var snapshot = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, snapshot.StoredCount);
            Assert.AreEqual(1, snapshot.PendingCount);
            await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox(maximumRecords: 2);
        Assert.AreEqual(OutboxOperationDisposition.Duplicate, await restarted.ResolveOperationsAsync(
            _root!, abandonId, OutboxOperationAction.Abandon, abandonKey, "owner", "operator-approved-loss",
            CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<OutboxOperationCollisionException>(async () =>
            await restarted.ResolveOperationsAsync(
                _root!, abandonId, OutboxOperationAction.Abandon, abandonKey, "owner", "invalid-source",
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var audit = await restarted.ReadOperationsAuditAsync(
            _root!, abandonId, 10, null, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, audit.Items);
        Assert.AreEqual("owner", audit.Items[0].ActorKind);
        Assert.AreEqual("operator-approved-loss", audit.Items[0].ReasonCode);
    }

    [TestMethod]
    public async Task OperationsPagesFilterBeforeLimitAndRemainStableWhenRowsMutate()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox(maximumRecords: 100);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var firstLease = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.QuarantineAsync(_root!, firstLease, "invalid-envelope", CancellationToken.None).ConfigureAwait(false);
        var secondLease = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.TerminalAsync(_root!, secondLease, "upstream-rejected", CancellationToken.None).ConfigureAwait(false);
        foreach (var _ in Enumerable.Range(0, 55))
        {
            await outbox.EnqueueAsync(
                _root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        }

        var firstPage = await outbox.ReadOperationsPageAsync(
            _root!, 1, null, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, firstPage.Items);
        Assert.IsNotNull(firstPage.NextCursor);
        using (var connection = new SqliteConnection(
                   $"Data Source={Path.Combine(_root!, ".environment", "environmental-observation-outbox.db")}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE environmental_observation_outbox SET updated_unix_ms = updated_unix_ms + 100000 WHERE record_id = $id;";
            command.Parameters.AddWithValue("$id", firstPage.Items[0].RecordId);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var secondPage = await outbox.ReadOperationsPageAsync(
            _root!, 1, firstPage.NextCursor, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, secondPage.Items);
        CollectionAssert.AreEquivalent(
            new[] { firstLease.Record.RecordId, secondLease.Record.RecordId },
            firstPage.Items.Concat(secondPage.Items).Select(static item => item.RecordId).ToArray());
    }

    [TestMethod]
    public async Task OperationReceiptsAreTransactionallyCappedAndRecentReplaySurvives()
    {
        var clock = new MutableTimeProvider(Epoch.AddDays(31));
        using var outbox = new SqliteEnvironmentalObservationOutbox(clock);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var lease = await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        var claimed = lease!;
        await outbox.TerminalAsync(_root!, claimed, "upstream-rejected", CancellationToken.None).ConfigureAwait(false);
        var recordId = claimed.Record.RecordId;
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                WITH RECURSIVE values_to_seed(value) AS (
                    SELECT 0 UNION ALL SELECT value + 1 FROM values_to_seed WHERE value < $maximum)
                INSERT INTO environmental_observation_outbox_operations(
                    operation_key, record_id, action, actor_kind, reason, occurred_unix_ms)
                SELECT printf('seed-%05d', value), $record, 'replay', 'owner', 'upstream-recovered',
                       CASE WHEN value = 0 THEN $start ELSE $recent + value END
                FROM values_to_seed;
                INSERT INTO environmental_observation_outbox_audit(
                    record_id, previous_status, action, actor, reason, occurred_unix_ms, operation_key)
                SELECT record_id, 'terminal', action, actor_kind, reason, occurred_unix_ms, operation_key
                FROM environmental_observation_outbox_operations;
                """;
            seed.Parameters.AddWithValue("$maximum", SqliteEnvironmentalObservationOutbox.MaximumOperationReceipts);
            seed.Parameters.AddWithValue("$record", recordId);
            seed.Parameters.AddWithValue("$start", Epoch.ToUnixTimeMilliseconds());
            seed.Parameters.AddWithValue("$recent", Epoch.AddDays(30).ToUnixTimeMilliseconds());
            await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Assert.AreEqual(OutboxOperationDisposition.Applied, await outbox.ResolveOperationsAsync(
            _root!, recordId, OutboxOperationAction.Replay, "newest-operation",
            "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false));

        using (var verify = new SqliteConnection($"Data Source={databasePath}"))
        {
            await verify.OpenAsync().ConfigureAwait(false);
            using var counts = verify.CreateCommand();
            counts.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM environmental_observation_outbox_operations),
                    (SELECT COUNT(*) FROM environmental_observation_outbox_audit WHERE operation_key IS NOT NULL),
                    (SELECT COUNT(*) FROM environmental_observation_outbox_operations WHERE operation_key = 'seed-00000');
                """;
            using var reader = await counts.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(SqliteEnvironmentalObservationOutbox.MaximumOperationReceipts, reader.GetInt32(0));
            Assert.AreEqual(SqliteEnvironmentalObservationOutbox.MaximumOperationReceipts, reader.GetInt32(1));
            Assert.AreEqual(0, reader.GetInt32(2));
        }
        Assert.AreEqual(OutboxOperationDisposition.Duplicate, await outbox.ResolveOperationsAsync(
            _root!, recordId, OutboxOperationAction.Replay,
            $"seed-{SqliteEnvironmentalObservationOutbox.MaximumOperationReceipts:D5}",
            "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false));
    }

    private static EnvironmentalObservationFactV1 CreateFact(Guid observationId)
    {
        var observation = CreateObservation(observationId);
        return new EnvironmentalObservationFactV1(
            observation.SchemaVersion,
            observation.ObservationId,
            observation.Source,
            observation.ObservedAtUtc,
            observation.ObservedFromUtc,
            observation.ObservedThroughUtc,
            observation.ValidFromUtc,
            observation.ValidThroughUtc,
            observation.StaleAfterUtc,
            observation.Value,
            observation.Lineage,
            observation.Target.RigId);
    }

    private EnvironmentalObservationDeliveryService CreateDeliveryService(
        IEnvironmentalObservationTransport transport,
        IEnvironmentalObservationOutbox outbox,
        EnvironmentalObservationDeliveryTelemetry telemetry,
        TimeProvider timeProvider,
        EnvironmentalObservationDeliveryOptions deliveryOptions,
        ILogger<EnvironmentalObservationDeliveryService>? logger = null)
        => new(
            transport,
            new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            new EnvironmentalObservationDeliveryState(),
            telemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = _root!,
                EnvironmentalDelivery = deliveryOptions
            }),
            timeProvider,
            logger ?? NullLogger<EnvironmentalObservationDeliveryService>.Instance);

    private static EnvironmentalObservationV1 CreateObservation(Guid observationId)
    {
        using var document = JsonDocument.Parse("{\"normalizer\":\"test-v1\"}");
        var parameters = document.RootElement.Clone();
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            observationId,
            new EnvironmentalObservationTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            new EnvironmentalObservationSource(
                "test-provider",
                "weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch,
            null,
            null,
            Epoch.AddMinutes(-1),
            Epoch.AddMinutes(5),
            Epoch.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good,
                0.5),
            []);
    }

    private sealed class MutableTargetResolver(EnvironmentalObservationResolvedTarget? target)
        : IEnvironmentalObservationTargetResolver
    {
        public EnvironmentalObservationResolvedTarget? Target { get; set; } = target;
        public int ResolveCount { get; private set; }

        public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
        {
            ResolveCount++;
            return ValueTask.FromResult<EnvironmentalObservationResolvedTarget?>(Target);
        }
    }

    private sealed class ThrowingTargetResolver : IEnvironmentalObservationTargetResolver
    {
        public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("central provisioning unavailable");
    }

    private sealed class CancellingTargetResolver(CancellationTokenSource cancellation)
        : IEnvironmentalObservationTargetResolver
    {
        public async ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class StubTransport(EnvironmentalObservationTransportResult result)
        : IEnvironmentalObservationTransport
    {
        public int SendCount { get; private set; }

        public ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class FaithfulCentralReceiver(TimeProvider timeProvider)
    {
        private readonly Dictionary<(string Source, Guid Observation), EnvironmentalObservationAcknowledgement> _observations = [];

        public int Count => _observations.Count;

        public EnvironmentalObservationAcknowledgement Ingest(EnvironmentalObservationV1 observation)
        {
            var source = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
            var key = (source, observation.ObservationId);
            if (_observations.TryGetValue(key, out var existing))
            {
                return existing with { Disposition = EnvironmentalObservationDeliveryDisposition.Duplicate };
            }
            var accepted = new EnvironmentalObservationAcknowledgement(
                EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
                observation.ObservationId,
                source,
                EnvironmentalObservationJson.ComputeContentSha256(observation),
                timeProvider.GetUtcNow(),
                EnvironmentalObservationDeliveryDisposition.Accepted);
            _observations.Add(key, accepted);
            return accepted;
        }
    }
}
