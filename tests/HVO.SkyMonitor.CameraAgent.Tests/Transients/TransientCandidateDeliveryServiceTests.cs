using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
public sealed class TransientCandidateDeliveryServiceTests
{
    [TestMethod]
    public async Task RetryThenDuplicateAcknowledgementConvergesWithoutReleasingEarly()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(
            submission, TransientCandidateSubmissionDisposition.Duplicate);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [entry]);
        journal.Setup(value => value.AcknowledgeAsync(
                submission.CandidateId, submission.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry with
            {
                Phase = TransientCandidateWorkflowPhase.Acknowledged,
                SourceHoldReleased = true,
                Acknowledgement = acknowledgement
            });
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.SetupSequence(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                time.Advance(TimeSpan.FromSeconds(10));
                return ValueTask.FromResult(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.Retry, "http-503"));
            })
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "duplicate", acknowledgement));
        var outboxState = new ArtifactOutboxState();
        var artifactOutbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        artifactOutbox.Setup(value => value.GetAcknowledgedArtifactIdsAsync(
                "/archive", It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = CreateService(
            journal.Object,
            transport.Object,
            time,
            artifactOutboxState: outboxState,
            artifactOutbox: artifactOutbox.Object);

        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        journal.Verify(value => value.AcknowledgeAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
            It.IsAny<CancellationToken>()), Times.Never);

        outboxState.Update("/archive", new ArtifactOutboxSnapshot(1, 1, time.GetUtcNow(), 1, 0, 0, 0, 0, 0));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        journal.Verify(value => value.AcknowledgeAsync(
            submission.CandidateId, submission.EventId, acknowledgement, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CentralDependencyWaitRemainsHealthyUntilAcknowledged()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(submission);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [entry]);
        journal.Setup(value => value.AcknowledgeAsync(
                submission.CandidateId, submission.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry with
            {
                Phase = TransientCandidateWorkflowPhase.Acknowledged,
                SourceHoldReleased = true,
                Acknowledgement = acknowledgement
            });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.SetupSequence(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.DependencyWaiting, "http-404"))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "accepted", acknowledgement));
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(TransientCandidateDeliveryAvailability.Healthy, state.Snapshot.Availability);
        Assert.AreEqual("waiting-central-evidence", state.Snapshot.Reason);
        Assert.AreEqual(0, state.Snapshot.RetryingCount);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(TransientCandidateDeliveryAvailability.Healthy, state.Snapshot.Availability);
        Assert.AreEqual("ready", state.Snapshot.Reason);
    }

    [TestMethod]
    public async Task ArtifactUploadDependencyDefersFirstAttemptWithoutDegradingDelivery()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(submission);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [entry]);
        journal.Setup(value => value.AcknowledgeAsync(
                submission.CandidateId, submission.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry with
            {
                Phase = TransientCandidateWorkflowPhase.Acknowledged,
                SourceHoldReleased = true,
                Acknowledgement = acknowledgement
            });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "accepted", acknowledgement));
        var outboxState = new ArtifactOutboxState();
        var drainedSnapshot = new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 1, 0, 0);
        outboxState.Update("/archive", drainedSnapshot);
        IReadOnlyList<Guid> acknowledgedArtifactIds = [];
        var publicationLookupUnavailable = true;
        var artifactOutbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        artifactOutbox.Setup(value => value.GetAcknowledgedArtifactIdsAsync(
                "/archive", It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(() => publicationLookupUnavailable
                ? throw new NotSupportedException("Publication lookup is temporarily unavailable.")
                : ValueTask.FromResult(acknowledgedArtifactIds));
        var state = new TransientCandidateDeliveryState(TimeProvider.System);
        var service = CreateService(
            journal.Object,
            transport.Object,
            TimeProvider.System,
            state: state,
            artifactOutboxState: outboxState,
            artifactOutbox: artifactOutbox.Object);

        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(TransientCandidateDeliveryAvailability.Healthy, state.Snapshot.Availability);
        Assert.AreEqual("waiting-artifact-upload", state.Snapshot.Reason);
        transport.Verify(value => value.SendAsync(
            It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()), Times.Never);

        publicationLookupUnavailable = false;
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        acknowledgedArtifactIds = submission.Candidate.ContextSources
            .Select(source => source.Locator.Artifact.ArtifactId).ToArray();
        outboxState.Update("/archive", drainedSnapshot);
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        journal.Verify(value => value.AcknowledgeAsync(
            submission.CandidateId, submission.EventId, acknowledgement, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task HostedServiceDiscoversPersistedWorkAndDedicatedWakeupDeliversImmediately()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(submission);
        var available = false;
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        journal.Setup(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(),
                TransientCandidateDeliveryService.MaximumBatchCount,
                It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateDeliveryCursor? after, int _, CancellationToken _) =>
            {
                IReadOnlyList<TransientCandidateJournalEntry> entries = available && after is null ? [entry] : [];
                return ValueTask.FromResult(new TransientCandidateDeliveryPage(
                    entries,
                    entries.Count == 0 ? after : Cursor(entries[^1])));
            });
        journal.Setup(value => value.ReadDeliveryAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateDeliveryAggregate(1, 0, entry.CreatedUtc));
        journal.Setup(value => value.AcknowledgeAsync(
                submission.CandidateId, submission.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .Callback(() => acknowledged.TrySetResult())
            .ReturnsAsync(entry with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "accepted", acknowledgement));
        var wakeup = new TransientCandidateDeliveryWakeup();
        var service = CreateService(journal.Object, transport.Object, TimeProvider.System, wakeup, pollMilliseconds: 60_000);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Delay(50).ConfigureAwait(false);
            available = true;
            wakeup.Signal();
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            service.Dispose();
        }
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Off, CentralIntegrationMode.Enabled)]
    [DataRow(TransientOperatingMode.Edge, CentralIntegrationMode.Enabled)]
    [DataRow(TransientOperatingMode.Central, CentralIntegrationMode.Enabled)]
    [DataRow(TransientOperatingMode.Hybrid, CentralIntegrationMode.Disabled)]
    public async Task DeliveryIsDisabledOutsideHybridCentralIntegration(
        TransientOperatingMode mode,
        CentralIntegrationMode centralMode)
    {
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        var service = CreateService(journal.Object, transport.Object, TimeProvider.System, mode: mode, centralMode: centralMode);

        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PagedCursorPassesMoreThanSixtyFourBackedOffEntriesAndWrapsForRecovery()
    {
        var entries = Enumerable.Range(1, 65)
            .Select(index => TransientDeliveryTestData.Entry(
                TransientDeliveryTestData.Submission(identity: index), DateTimeOffset.UnixEpoch))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        foreach (var entry in entries[..64])
        {
            transport.Setup(value => value.SendAsync(entry.Submission!, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.AuthenticationBlocked, "credentials-unavailable"));
        }
        var last = entries[^1];
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(last.Submission!);
        transport.Setup(value => value.SendAsync(last.Submission!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "accepted", acknowledgement));
        journal.Setup(value => value.AcknowledgeAsync(
                last.CandidateId, last.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(last with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        var service = CreateService(journal.Object, transport.Object, new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.AreEqual(64, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        journal.Verify(value => value.AcknowledgeAsync(
            last.CandidateId, last.EventId, acknowledgement, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PermanentRejectionDurablyQuarantinesWithoutAcknowledgement()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [entry]);
        journal.Setup(value => value.QuarantineDeliveryAsync(
                entry.CandidateId, entry.EventId, "http-400", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry with
            {
                Phase = TransientCandidateWorkflowPhase.Quarantined,
                QuarantineReason = "http-400"
            });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Rejected, "http-400"));
        var service = CreateService(journal.Object, transport.Object, TimeProvider.System);

        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        journal.Verify(value => value.QuarantineDeliveryAsync(
            entry.CandidateId, entry.EventId, "http-400", It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(value => value.AcknowledgeAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task BatchUsesAtMostFourConcurrentSends()
    {
        var entries = Enumerable.Range(1, 8)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var transport = new ConcurrencyTrackingTransport();
        var service = CreateService(journal.Object, transport, TimeProvider.System);

        var delivery = service.DeliverBatchAsync(CancellationToken.None).AsTask();
        await transport.FirstWave.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateDeliveryService.MaximumConcurrentSends, transport.MaximumObserved);
        transport.Release();

        Assert.AreEqual(8, await delivery.ConfigureAwait(false));
        Assert.AreEqual(TransientCandidateDeliveryService.MaximumConcurrentSends, transport.MaximumObserved);
    }

    [TestMethod]
    public async Task AcceptedSiblingIsSettledBeforeStalledRequestCompletes()
    {
        var stalled = TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: 1));
        var accepted = TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: 2));
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(accepted.Submission!);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [stalled, accepted]);
        journal.Setup(value => value.AcknowledgeAsync(
                accepted.CandidateId, accepted.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .Callback(() => settled.TrySetResult())
            .ReturnsAsync(accepted with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        var transport = new StalledSiblingTransport(stalled.CandidateId, acknowledgement);
        var service = CreateService(journal.Object, transport, TimeProvider.System);

        var delivery = service.DeliverBatchAsync(CancellationToken.None).AsTask();
        await transport.Stalled.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(delivery.IsCompleted);

        transport.Release();
        Assert.AreEqual(2, await delivery.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OutOfOrderCompletionTimestampsPreserveMonotonicAttemptAndAcknowledgementMaximums()
    {
        var entries = new[]
        {
            TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: 1)),
            TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: 2))
        };
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        journal.Setup(value => value.AcknowledgeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(), It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, Guid _, TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
                CancellationToken _) => ValueTask.FromResult(entries.Single(entry => entry.CandidateId == candidateId) with
                {
                    Phase = TransientCandidateWorkflowPhase.Acknowledged,
                    Acknowledgement = acknowledgement
                }));
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateSubmissionEnvelopeV1 submission, CancellationToken _) =>
                ValueTask.FromResult(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.Acknowledged,
                    "accepted",
                    TransientDeliveryTestData.Acknowledgement(submission))));
        var expectedMaximum = DateTimeOffset.UnixEpoch.AddSeconds(20);
        var serviceTime = new SequenceTimeProvider(
            DateTimeOffset.UnixEpoch,
            expectedMaximum,
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            DateTimeOffset.UnixEpoch.AddSeconds(30));
        var state = new TransientCandidateDeliveryState(TimeProvider.System);
        var service = CreateService(journal.Object, transport.Object, serviceTime, state: state);

        Assert.AreEqual(2, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual(expectedMaximum, state.Snapshot.LastAttemptUtc);
        Assert.AreEqual(expectedMaximum, state.Snapshot.LastAcknowledgedUtc);
    }

    [TestMethod]
    public async Task DueRetryIsReloadedBeforeFreshCursorDiscovery()
    {
        var retry = TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: 1));
        var fresh = TransientDeliveryTestData.Entry(
            TransientDeliveryTestData.Submission(identity: 2), DateTimeOffset.UnixEpoch.AddSeconds(1));
        var retryAcknowledgement = TransientDeliveryTestData.Acknowledgement(retry.Submission!);
        var freshAcknowledgement = TransientDeliveryTestData.Acknowledgement(fresh.Submission!);
        var firstDiscovery = true;
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        journal.Setup(value => value.ReadAsync(retry.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(retry);
        journal.Setup(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateDeliveryCursor? after, int _, CancellationToken _) =>
            {
                var entries = firstDiscovery ? new[] { retry } : new[] { fresh };
                firstDiscovery = false;
                return ValueTask.FromResult(new TransientCandidateDeliveryPage(entries, Cursor(entries[0])));
            });
        journal.Setup(value => value.ReadDeliveryAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateDeliveryAggregate(2, 0, retry.CreatedUtc));
        journal.Setup(value => value.AcknowledgeAsync(
                retry.CandidateId, retry.EventId, retryAcknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(retry with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        journal.Setup(value => value.AcknowledgeAsync(
                fresh.CandidateId, fresh.EventId, freshAcknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.SetupSequence(value => value.SendAsync(retry.Submission!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(TransientCandidateTransportDisposition.Retry, "http-503"))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "accepted", retryAcknowledgement));
        transport.Setup(value => value.SendAsync(fresh.Submission!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "accepted", freshAcknowledgement));
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = CreateService(journal.Object, transport.Object, time, pollMilliseconds: 60_000);

        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(TimeSpan.FromSeconds(1), service.GetWaitDelay(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(TimeSpan.Zero, service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual(2, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        transport.Verify(value => value.SendAsync(retry.Submission!, It.IsAny<CancellationToken>()), Times.Exactly(2));
        transport.Verify(value => value.SendAsync(fresh.Submission!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DeliveryTelemetryUsesOnlyFixedStageAndOutcomeTags()
    {
        var entry = TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission());
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [entry]);
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(entry.Submission!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.DependencyWaiting, "dynamic-dependency-reason"));
        var samples = new System.Collections.Concurrent.ConcurrentQueue<(string Stage, string Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TransientWorkerTelemetry.MeterName &&
                    instrument.Name == "hvo.transient.worker.outcomes")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray();
            samples.Enqueue((
                Convert.ToString(
                    values.Single(value => value.Key == "stage").Value,
                    System.Globalization.CultureInfo.InvariantCulture)!,
                Convert.ToString(
                    values.Single(value => value.Key == "outcome").Value,
                    System.Globalization.CultureInfo.InvariantCulture)!));
        });
        listener.Start();
        using var telemetry = new TransientWorkerTelemetry();
        var service = CreateService(journal.Object, transport.Object, TimeProvider.System, telemetry: telemetry);

        _ = await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(samples.Contains(("delivery", "dependency-wait")));
        Assert.IsTrue(samples.All(sample => sample.Stage == "delivery"));
        Assert.IsTrue(samples.All(sample => sample.Outcome is
            "accepted" or "duplicate" or "dependency-wait" or "retry" or "authentication-blocked" or "rejected" or "scan-failed"));
    }

    [TestMethod]
    public void InfrastructureRegistersDeliveryHostedServiceAndDedicatedWakeup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());

        Assert.IsTrue(services.Any(value => value.ServiceType == typeof(TransientCandidateDeliveryWakeup)));
        Assert.IsTrue(services.Any(value => value.ServiceType == typeof(TransientCandidateDeliveryState)));
        Assert.IsTrue(services.Any(value => value.ServiceType == typeof(TransientCandidateDeliveryService)));
        Assert.IsTrue(services.Any(value => value.ServiceType == typeof(IHostedService) &&
            value.ImplementationFactory is not null));
    }

    private static TransientCandidateDeliveryService CreateService(
        ITransientCandidateJournal journal,
        ITransientCandidateTransport transport,
        TimeProvider timeProvider,
        TransientCandidateDeliveryWakeup? wakeup = null,
        int pollMilliseconds = 1_000,
        TransientOperatingMode mode = TransientOperatingMode.Hybrid,
        CentralIntegrationMode centralMode = CentralIntegrationMode.Enabled,
        TransientCandidateDeliveryState? state = null,
        TransientWorkerTelemetry? telemetry = null,
        ArtifactOutboxState? artifactOutboxState = null,
        IArtifactOutbox? artifactOutbox = null)
    {
        artifactOutboxState ??= new ArtifactOutboxState();
        artifactOutboxState.MarkInitialized();
        return new(
            journal,
            artifactOutboxState,
            artifactOutbox ?? Mock.Of<IArtifactOutbox>(),
            transport,
            wakeup ?? new TransientCandidateDeliveryWakeup(),
            state ?? new TransientCandidateDeliveryState(timeProvider),
            telemetry ?? new TransientWorkerTelemetry(),
            Options.Create(new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions { Mode = centralMode },
                TransientDetection = new TransientDetectionOptions
                {
                    Mode = mode,
                    WorkerPollIntervalMilliseconds = pollMilliseconds,
                    RetryInitialDelaySeconds = 1,
                    RetryMaximumDelaySeconds = 4,
                    MaximumAttempts = 2
                }
            }),
            timeProvider,
            NullLogger<TransientCandidateDeliveryService>.Instance);
    }

    private static void SetupPages(
        Mock<ITransientCandidateJournal> journal,
        TransientCandidateJournalEntry[] source)
    {
        journal.Setup(value => value.ReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, CancellationToken _) => ValueTask.FromResult(
                source.SingleOrDefault(entry => entry.CandidateId == candidateId)));
        journal.Setup(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateDeliveryCursor? after, int maximum, CancellationToken _) =>
            {
                var entries = source
                    .Where(entry => after is null || entry.CreatedUtc > after.CreatedUtc ||
                        entry.CreatedUtc == after.CreatedUtc && entry.CandidateId.CompareTo(after.CandidateId) > 0)
                    .OrderBy(entry => entry.CreatedUtc)
                    .ThenBy(entry => entry.CandidateId)
                    .Take(maximum)
                    .ToArray();
                return ValueTask.FromResult(new TransientCandidateDeliveryPage(
                    entries,
                    entries.Length == 0 ? after : Cursor(entries[^1])));
            });
        journal.Setup(value => value.ReadDeliveryAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateDeliveryAggregate(
                source.Length,
                0,
                source.Length == 0 ? null : source.Min(entry => entry.CreatedUtc)));
    }

    private static TransientCandidateDeliveryCursor Cursor(TransientCandidateJournalEntry entry)
        => new(entry.CreatedUtc, entry.CandidateId);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }

    private sealed class ConcurrencyTrackingTransport : ITransientCandidateTransport
    {
        private readonly TaskCompletionSource _firstWave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maximumObserved;

        internal Task FirstWave => _firstWave.Task;
        internal int MaximumObserved => Volatile.Read(ref _maximumObserved);

        public async ValueTask<TransientCandidateTransportResult> SendAsync(
            TransientCandidateSubmissionEnvelopeV1 submission,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            SetMaximum(active);
            if (active == TransientCandidateDeliveryService.MaximumConcurrentSends)
            {
                _firstWave.TrySetResult();
            }
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new(TransientCandidateTransportDisposition.Retry, "test-retry");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        internal void Release() => _release.TrySetResult();

        private void SetMaximum(int value)
        {
            var observed = Volatile.Read(ref _maximumObserved);
            while (value > observed)
            {
                var prior = Interlocked.CompareExchange(ref _maximumObserved, value, observed);
                if (prior == observed)
                {
                    return;
                }
                observed = prior;
            }
        }
    }

    private sealed class StalledSiblingTransport(
        Guid stalledCandidateId,
        TransientCandidateSubmissionAcknowledgementV1 acknowledgement) : ITransientCandidateTransport
    {
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Stalled => _stalled.Task;

        public async ValueTask<TransientCandidateTransportResult> SendAsync(
            TransientCandidateSubmissionEnvelopeV1 submission,
            CancellationToken cancellationToken)
        {
            if (submission.CandidateId != stalledCandidateId)
            {
                return new(TransientCandidateTransportDisposition.Acknowledged, "accepted", acknowledgement);
            }
            _stalled.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(TransientCandidateTransportDisposition.Retry, "stalled-retry");
        }

        internal void Release() => _release.TrySetResult();
    }
}
