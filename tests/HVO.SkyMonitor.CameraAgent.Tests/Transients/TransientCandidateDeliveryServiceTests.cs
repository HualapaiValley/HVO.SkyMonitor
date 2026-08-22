using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public async Task ProfileTransitionRetirementAcknowledgesAndReleasesWithoutQuarantine()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var retirement = TransientDeliveryTestData.Acknowledgement(
            submission, TransientCandidateSubmissionDisposition.Retired);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, [entry]);
        journal.Setup(value => value.AcknowledgeAsync(
                submission.CandidateId, submission.EventId, retirement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry with
            {
                Phase = TransientCandidateWorkflowPhase.Acknowledged,
                SourceHoldReleased = true,
                Acknowledgement = retirement
            });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged, "retired", retirement));
        var service = CreateService(journal.Object, transport.Object, TimeProvider.System);

        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        journal.Verify(value => value.AcknowledgeAsync(
            submission.CandidateId, submission.EventId, retirement, It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(value => value.QuarantineDeliveryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
                TransientCandidateTransportDisposition.DependencyWaiting, "hybrid-submission.evidence-missing"))
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
    public async Task ModeDisabledOpensSharedCircuitAndPollsOneCanaryWithSharedBackoff()
    {
        var entries = Enumerable.Range(1, 70)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.DependencyWaiting,
                TransientCandidateDeliveryService.ModeDisabledReason));
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var artifactState = new ArtifactOutboxState();
        artifactState.MarkInitialized();
        var artifactSnapshot = artifactState.Snapshot;
        var service = CreateService(
            journal.Object, transport.Object, time, state: state, artifactOutboxState: artifactState);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(4, calls);
        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(0, state.Snapshot.RetryingCount);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, state.Snapshot.LastAttemptUtc);
        Assert.AreEqual(artifactSnapshot.Availability, artifactState.Snapshot.Availability);
        Assert.AreEqual(artifactSnapshot.RetryCount, artifactState.Snapshot.RetryCount);
        Assert.AreEqual(artifactSnapshot.QuarantineCount, artifactState.Snapshot.QuarantineCount);

        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(4, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(5, calls);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(1), state.Snapshot.LastAttemptUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(2), service.GetWaitDelay(time.GetUtcNow()));

        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(6, calls);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(3), state.Snapshot.LastAttemptUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(4), service.GetWaitDelay(time.GetUtcNow()));
        journal.Verify(value => value.ReadPendingDeliveryPageAsync(
            It.IsAny<TransientCandidateDeliveryCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(value => value.AcknowledgeAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
            It.IsAny<CancellationToken>()), Times.Never);
        journal.Verify(value => value.QuarantineDeliveryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ModeDisabledRecoveryDrainsEveryRetainedCandidateExactlyOnce()
    {
        var entries = Enumerable.Range(1, 70)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var acknowledged = new HashSet<Guid>();
        var acknowledgementCounts = new Dictionary<Guid, int>();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupMutablePages(journal, entries, acknowledged);
        journal.Setup(value => value.AcknowledgeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, Guid _, TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
                CancellationToken _) =>
            {
                acknowledged.Add(candidateId);
                acknowledgementCounts[candidateId] = acknowledgementCounts.GetValueOrDefault(candidateId) + 1;
                return ValueTask.FromResult(entries.Single(entry => entry.CandidateId == candidateId) with
                {
                    Phase = TransientCandidateWorkflowPhase.Acknowledged,
                    SourceHoldReleased = true,
                    Acknowledgement = acknowledgement
                });
            });
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateSubmissionEnvelopeV1 submission, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(call <= 4
                    ? new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.DependencyWaiting,
                        TransientCandidateDeliveryService.ModeDisabledReason)
                    : new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.Acknowledged,
                        "accepted",
                        TransientDeliveryTestData.Acknowledgement(submission)));
            });
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, acknowledged.Count);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("ready", state.Snapshot.Reason);
        Assert.AreEqual(0, state.Snapshot.RetryingCount);
        for (var iteration = 0; acknowledged.Count < entries.Length && iteration < 10; iteration++)
        {
            _ = await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Assert.AreEqual(entries.Length, acknowledged.Count);
        Assert.IsTrue(acknowledgementCounts.Values.All(count => count == 1));
        Assert.AreEqual(entries.Length + 4, calls);
        journal.Verify(value => value.QuarantineDeliveryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ModeDisabledRecoveryRevisitsUndispatchedPageEntriesDuringContinuousIngress()
    {
        var original = Enumerable.Range(1, 70)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var entries = original.ToList();
        var acknowledged = new HashSet<Guid>();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupDynamicPages(journal, entries, acknowledged);
        journal.Setup(value => value.AcknowledgeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, Guid _, TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
                CancellationToken _) =>
            {
                acknowledged.Add(candidateId);
                return ValueTask.FromResult(entries.Single(entry => entry.CandidateId == candidateId) with
                {
                    Phase = TransientCandidateWorkflowPhase.Acknowledged,
                    SourceHoldReleased = true,
                    Acknowledgement = acknowledgement
                });
            });
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateSubmissionEnvelopeV1 submission, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(call <= 4
                    ? new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.DependencyWaiting,
                        TransientCandidateDeliveryService.ModeDisabledReason)
                    : new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.Acknowledged,
                        "accepted",
                        TransientDeliveryTestData.Acknowledgement(submission)));
            });
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = CreateService(journal.Object, transport.Object, time);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var identityBase = 1000 + iteration * 64;
            entries.AddRange(Enumerable.Range(identityBase, 64)
                .Select(identity => TransientDeliveryTestData.Entry(
                    TransientDeliveryTestData.Submission(identity: identity),
                    DateTimeOffset.UnixEpoch.AddMinutes(iteration + 1))));
            _ = await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Assert.IsTrue(original.All(entry => acknowledged.Contains(entry.CandidateId)));
        journal.Verify(value => value.QuarantineDeliveryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ModeDisabledDominatesMixedWaveRegardlessCompletionOrder(bool modeCompletesLast)
    {
        var entries = Enumerable.Range(1, 4)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var duplicate = TransientDeliveryTestData.Acknowledgement(
            entries[1].Submission!, TransientCandidateSubmissionDisposition.Duplicate);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        journal.Setup(value => value.AcknowledgeAsync(
                entries[1].CandidateId, entries[1].EventId, duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries[1] with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        var transport = new ControlledWaveTransport();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport, time, state: state);

        var delivery = service.DeliverBatchAsync(CancellationToken.None).AsTask();
        await transport.Started.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var mode = new TransientCandidateTransportResult(
            TransientCandidateTransportDisposition.DependencyWaiting,
            TransientCandidateDeliveryService.ModeDisabledReason);
        if (!modeCompletesLast)
        {
            transport.Complete(0, mode);
            await Task.Yield();
        }
        transport.Complete(1, new(TransientCandidateTransportDisposition.Acknowledged, "duplicate", duplicate));
        transport.Complete(2, new(TransientCandidateTransportDisposition.Retry, "http-503"));
        transport.Complete(3, new(TransientCandidateTransportDisposition.AuthenticationBlocked, "credentials-unavailable"));
        if (modeCompletesLast)
        {
            await Task.Yield();
            transport.Complete(0, mode);
        }

        Assert.AreEqual(4, await delivery.ConfigureAwait(false));
        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(4, transport.CallCount);
        Assert.AreEqual(TimeSpan.FromSeconds(1), service.GetWaitDelay(time.GetUtcNow()));
    }

    [TestMethod]
    public async Task ModeDisabledDominatesCandidateDependencyAndTimeoutSiblings()
    {
        var entries = Enumerable.Range(1, 4)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(entries[3].Submission!);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        journal.Setup(value => value.AcknowledgeAsync(
                entries[3].CandidateId, entries[3].EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries[3] with { Phase = TransientCandidateWorkflowPhase.Acknowledged });
        var results = new[]
        {
            new TransientCandidateTransportResult(TransientCandidateTransportDisposition.Retry, "request-timeout"),
            new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.DependencyWaiting,
                "hybrid-submission.evidence-missing"),
            new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.DependencyWaiting,
                TransientCandidateDeliveryService.ModeDisabledReason),
            new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.Acknowledged,
                "accepted",
                acknowledgement)
        };
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        foreach (var (entry, result) in entries.Zip(results))
        {
            transport.Setup(value => value.SendAsync(entry.Submission!, It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InconclusiveCanariesKeepCircuitAndAdvanceSharedBackoff()
    {
        var entries = Enumerable.Range(1, 8)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateSubmissionEnvelopeV1 _, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(call <= 4
                    ? new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.DependencyWaiting,
                        TransientCandidateDeliveryService.ModeDisabledReason)
                    : call == 5
                        ? new TransientCandidateTransportResult(TransientCandidateTransportDisposition.Retry, "http-503")
                        : new TransientCandidateTransportResult(
                            TransientCandidateTransportDisposition.AuthenticationBlocked,
                            "credentials-unavailable"));
            });
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(TimeSpan.FromSeconds(2), service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(TimeSpan.FromSeconds(4), service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(6, calls);
    }

    [TestMethod]
    public async Task CanaryStateReadFailureRestoresBoundedSharedDeadline()
    {
        var submission = TransientDeliveryTestData.Submission();
        var entry = TransientDeliveryTestData.Entry(submission);
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(submission);
        var aggregate = new TransientCandidateDeliveryAggregate(1, 0, entry.CreatedUtc);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        journal.Setup(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientCandidateDeliveryPage([entry], Cursor(entry)));
        journal.Setup(value => value.ReadAsync(entry.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        journal.SetupSequence(value => value.ReadDeliveryAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate)
            .ThrowsAsync(new IOException("Simulated delivery state read failure."));
        journal.Setup(value => value.AcknowledgeAsync(
                entry.CandidateId, entry.EventId, acknowledgement, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry with
            {
                Phase = TransientCandidateWorkflowPhase.Acknowledged,
                SourceHoldReleased = true,
                Acknowledgement = acknowledgement
            });
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.SetupSequence(value => value.SendAsync(submission, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                return ValueTask.FromResult(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.DependencyWaiting,
                    TransientCandidateDeliveryService.ModeDisabledReason));
            })
            .Returns(() =>
            {
                calls++;
                return ValueTask.FromResult(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.Acknowledged,
                    "accepted",
                    acknowledgement));
            });
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var logger = new CountingLogger<TransientCandidateDeliveryService>();
        var service = CreateService(journal.Object, transport.Object, time, logger: logger);

        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(async () =>
            await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(2, calls);
        Assert.AreEqual(TimeSpan.FromSeconds(2), service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual(2, logger.Count(2524));
        journal.Verify(value => value.AcknowledgeAsync(
            entry.CandidateId, entry.EventId, acknowledgement, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(TransientCandidateSubmissionDisposition.Duplicate)]
    [DataRow(TransientCandidateSubmissionDisposition.Retired)]
    public async Task DuplicateOrRetiredCanarySettlesWithoutClosingCircuit(
        TransientCandidateSubmissionDisposition disposition)
    {
        var entries = Enumerable.Range(1, 4)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        journal.Setup(value => value.AcknowledgeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, Guid _, TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
                CancellationToken _) => ValueTask.FromResult(entries.Single(entry => entry.CandidateId == candidateId) with
                {
                    Phase = TransientCandidateWorkflowPhase.Acknowledged,
                    SourceHoldReleased = true,
                    Acknowledgement = acknowledgement
                }));
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateSubmissionEnvelopeV1 submission, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(call <= 4
                    ? new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.DependencyWaiting,
                        TransientCandidateDeliveryService.ModeDisabledReason)
                    : new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.Acknowledged,
                        disposition == TransientCandidateSubmissionDisposition.Duplicate ? "duplicate" : "retired",
                        TransientDeliveryTestData.Acknowledgement(submission, disposition)));
            });
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(2), service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(5, calls);
        journal.Verify(value => value.AcknowledgeAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("http-400")]
    [DataRow("http-409")]
    [DataRow("http-413")]
    public async Task GenericRejectedCanaryQuarantinesWithoutClosingCircuit(string reason)
    {
        var entries = Enumerable.Range(1, 4)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        journal.Setup(value => value.QuarantineDeliveryAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), reason, It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, Guid _, string quarantineReason, CancellationToken _) =>
                ValueTask.FromResult(entries.Single(entry => entry.CandidateId == candidateId) with
                {
                    Phase = TransientCandidateWorkflowPhase.Quarantined,
                    QuarantineReason = quarantineReason
                }));
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(Interlocked.Increment(ref calls) <= 4
                ? new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.DependencyWaiting,
                    TransientCandidateDeliveryService.ModeDisabledReason)
                : new TransientCandidateTransportResult(TransientCandidateTransportDisposition.Rejected, reason)));
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(2), service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(5, calls);
        journal.Verify(value => value.QuarantineDeliveryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), reason, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EvidenceMissingCanaryClosesCircuitAndRetainsCandidateRetry()
    {
        var entries = Enumerable.Range(1, 4)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(Interlocked.Increment(ref calls) <= 4
                ? new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.DependencyWaiting,
                    TransientCandidateDeliveryService.ModeDisabledReason)
                : new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.DependencyWaiting,
                    TransientCandidateDeliveryService.EvidenceMissingReason)));
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport.Object, time, state: state);

        Assert.AreEqual(4, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual("waiting-central-evidence", state.Snapshot.Reason);
        Assert.AreEqual(TimeSpan.Zero, service.GetWaitDelay(time.GetUtcNow()));
        Assert.AreEqual(5, calls);
    }

    [TestMethod]
    public async Task CandidateSpecificDependencyDoesNotOpenSharedCircuit()
    {
        var entries = Enumerable.Range(1, 8)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var calls = 0;
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls))
            .ReturnsAsync(new TransientCandidateTransportResult(
                TransientCandidateTransportDisposition.DependencyWaiting,
                "hybrid-submission.evidence-missing"));
        var state = new TransientCandidateDeliveryState(TimeProvider.System);
        var service = CreateService(journal.Object, transport.Object, TimeProvider.System, state: state);

        Assert.AreEqual(entries.Length, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual(entries.Length, calls);
        Assert.AreEqual("waiting-central-evidence", state.Snapshot.Reason);
    }

    [TestMethod]
    public async Task HostedModeDisabledLoopIgnoresWakeupsUntilCanaryDeadline()
    {
        var entries = Enumerable.Range(1, 70)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var calls = 0;
        var initialWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var count = Interlocked.Increment(ref calls);
                if (count == 4)
                {
                    initialWave.TrySetResult();
                }
                if (count == 5)
                {
                    canary.TrySetResult();
                }
                return ValueTask.FromResult(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.DependencyWaiting,
                    TransientCandidateDeliveryService.ModeDisabledReason));
            });
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var wakeup = new TransientCandidateDeliveryWakeup();
        var service = CreateService(journal.Object, transport.Object, time, wakeup, pollMilliseconds: 60_000);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await initialWave.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await time.WaitForTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            for (var index = 0; index < 100; index++)
            {
                wakeup.Signal();
            }
            await Task.Yield();
            Assert.AreEqual(4, calls);

            time.Advance(TimeSpan.FromSeconds(1));
            await canary.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await time.WaitForTimerCountAsync(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(5, calls);
            journal.Verify(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            service.Dispose();
        }
    }

    [TestMethod]
    public async Task HostedCanaryReadFailuresUseBoundedSharedBackoffWithoutBusyLoop()
    {
        var entries = Enumerable.Range(1, 70)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        var readCalls = 0;
        journal.Setup(value => value.ReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref readCalls);
                throw new IOException("Simulated canary journal failure.");
            });
        var sendCalls = 0;
        var initialWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref sendCalls) == 4)
                {
                    initialWave.TrySetResult();
                }
                return ValueTask.FromResult(new TransientCandidateTransportResult(
                    TransientCandidateTransportDisposition.DependencyWaiting,
                    TransientCandidateDeliveryService.ModeDisabledReason));
            });
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var wakeup = new TransientCandidateDeliveryWakeup();
        var logger = new CountingLogger<TransientCandidateDeliveryService>();
        var service = CreateService(
            journal.Object,
            transport.Object,
            time,
            wakeup,
            pollMilliseconds: 60_000,
            logger: logger);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await initialWave.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await time.WaitForTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForTimerCountAsync(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(1, readCalls);
            Assert.AreEqual(4, sendCalls);
            Assert.AreEqual(2, logger.Count(2524));
            Assert.AreEqual(1, logger.Count(2522));

            for (var index = 0; index < 100; index++)
            {
                wakeup.Signal();
            }
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
            Assert.AreEqual(1, readCalls);

            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForTimerCountAsync(3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(2, readCalls);
            Assert.AreEqual(4, sendCalls);
            Assert.AreEqual(3, logger.Count(2524));
            Assert.AreEqual(2, logger.Count(2522));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            service.Dispose();
        }
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
    public async Task ScanCursorPassesFullNotDueRetryPageToReachContinuousIngress()
    {
        var original = Enumerable.Range(1, 65)
            .Select(index => TransientDeliveryTestData.Entry(
                TransientDeliveryTestData.Submission(identity: index), DateTimeOffset.UnixEpoch))
            .ToArray();
        var originalIds = original.Select(entry => entry.CandidateId).ToHashSet();
        var entries = original.ToList();
        var acknowledged = new HashSet<Guid>();
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupDynamicPages(journal, entries, acknowledged);
        journal.Setup(value => value.AcknowledgeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, Guid _, TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
                CancellationToken _) =>
            {
                acknowledged.Add(candidateId);
                return ValueTask.FromResult(entries.Single(entry => entry.CandidateId == candidateId) with
                {
                    Phase = TransientCandidateWorkflowPhase.Acknowledged,
                    Acknowledgement = acknowledgement
                });
            });
        var transport = new Mock<ITransientCandidateTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendAsync(
                It.IsAny<TransientCandidateSubmissionEnvelopeV1>(), It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateSubmissionEnvelopeV1 submission, CancellationToken _) =>
                ValueTask.FromResult(originalIds.Contains(submission.CandidateId)
                    ? new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.AuthenticationBlocked,
                        "credentials-unavailable")
                    : new TransientCandidateTransportResult(
                        TransientCandidateTransportDisposition.Acknowledged,
                        "accepted",
                        TransientDeliveryTestData.Acknowledgement(submission))));
        var service = CreateService(journal.Object, transport.Object, new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.AreEqual(64, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));

        var target = TransientDeliveryTestData.Entry(
            TransientDeliveryTestData.Submission(identity: 10_000), DateTimeOffset.UnixEpoch.AddSeconds(1));
        entries.Add(target);
        entries.AddRange(Enumerable.Range(10_001, 64)
            .Select(identity => TransientDeliveryTestData.Entry(
                TransientDeliveryTestData.Submission(identity: identity),
                DateTimeOffset.UnixEpoch.AddSeconds(2))));
        _ = await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(acknowledged.Contains(target.CandidateId));
        journal.Verify(value => value.AcknowledgeAsync(
            target.CandidateId,
            target.EventId,
            It.IsAny<TransientCandidateSubmissionAcknowledgementV1>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task SettlementFailureObservesWholeWaveAndModeSiblingOpensCircuit(bool quarantineFailure)
    {
        var entries = Enumerable.Range(1, 4)
            .Select(index => TransientDeliveryTestData.Entry(TransientDeliveryTestData.Submission(identity: index)))
            .ToArray();
        var acknowledgement = TransientDeliveryTestData.Acknowledgement(entries[0].Submission!);
        var journal = new Mock<ITransientCandidateJournal>(MockBehavior.Strict);
        SetupPages(journal, entries);
        if (quarantineFailure)
        {
            journal.Setup(value => value.QuarantineDeliveryAsync(
                    entries[0].CandidateId, entries[0].EventId, "http-400", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Simulated quarantine settlement failure."));
        }
        else
        {
            journal.Setup(value => value.AcknowledgeAsync(
                    entries[0].CandidateId, entries[0].EventId, acknowledgement, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Simulated acknowledgement settlement failure."));
        }
        var transport = new ControlledWaveTransport();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var state = new TransientCandidateDeliveryState(time);
        var service = CreateService(journal.Object, transport, time, state: state);

        var delivery = service.DeliverBatchAsync(CancellationToken.None).AsTask();
        await transport.Started.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        transport.Complete(0, quarantineFailure
            ? new(TransientCandidateTransportDisposition.Rejected, "http-400")
            : new(TransientCandidateTransportDisposition.Acknowledged, "accepted", acknowledgement));
        transport.Complete(2, new(
            TransientCandidateTransportDisposition.DependencyWaiting,
            TransientCandidateDeliveryService.ModeDisabledReason));
        transport.Complete(3, new(TransientCandidateTransportDisposition.Retry, "http-503"));
        await Task.Yield();
        Assert.IsFalse(delivery.IsCompleted);

        transport.Complete(1, new(TransientCandidateTransportDisposition.Retry, "request-timeout"));
        await Assert.ThrowsAsync<IOException>(async () =>
            await delivery.ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(0, transport.ActiveCount);
        Assert.IsTrue(transport.MaximumObserved <= TransientCandidateDeliveryService.MaximumConcurrentSends);
        Assert.AreEqual(0, await service.DeliverBatchAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("waiting-central-mode", state.Snapshot.Reason);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, state.Snapshot.LastAttemptUtc);
        Assert.AreEqual(4, transport.CallCount);
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
            "accepted" or "duplicate" or "retired" or "dependency-wait" or "retry" or "authentication-blocked" or
                "rejected" or "scan-failed"));
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
        IArtifactOutbox? artifactOutbox = null,
        ILogger<TransientCandidateDeliveryService>? logger = null)
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
                    MaximumAttempts = 8
                }
            }),
            timeProvider,
            logger ?? NullLogger<TransientCandidateDeliveryService>.Instance);
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

    private static void SetupMutablePages(
        Mock<ITransientCandidateJournal> journal,
        TransientCandidateJournalEntry[] source,
        HashSet<Guid> acknowledged)
    {
        journal.Setup(value => value.ReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, CancellationToken _) => ValueTask.FromResult(
                acknowledged.Contains(candidateId)
                    ? null
                    : source.SingleOrDefault(entry => entry.CandidateId == candidateId)));
        journal.Setup(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateDeliveryCursor? after, int maximum, CancellationToken _) =>
            {
                var entries = source
                    .Where(entry => !acknowledged.Contains(entry.CandidateId))
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
            .Returns(() =>
            {
                var pending = source.Where(entry => !acknowledged.Contains(entry.CandidateId)).ToArray();
                return ValueTask.FromResult(new TransientCandidateDeliveryAggregate(
                    pending.Length,
                    0,
                    pending.Length == 0 ? null : pending.Min(entry => entry.CreatedUtc)));
            });
    }

    private static void SetupDynamicPages(
        Mock<ITransientCandidateJournal> journal,
        List<TransientCandidateJournalEntry> source,
        HashSet<Guid> acknowledged)
    {
        journal.Setup(value => value.ReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid candidateId, CancellationToken _) => ValueTask.FromResult(
                acknowledged.Contains(candidateId)
                    ? null
                    : source.SingleOrDefault(entry => entry.CandidateId == candidateId)));
        journal.Setup(value => value.ReadPendingDeliveryPageAsync(
                It.IsAny<TransientCandidateDeliveryCursor?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns((TransientCandidateDeliveryCursor? after, int maximum, CancellationToken _) =>
            {
                var entries = source
                    .Where(entry => !acknowledged.Contains(entry.CandidateId))
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
            .Returns(() =>
            {
                var pending = source.Where(entry => !acknowledged.Contains(entry.CandidateId)).ToArray();
                return ValueTask.FromResult(new TransientCandidateDeliveryAggregate(
                    pending.Length,
                    0,
                    pending.Length == 0 ? null : pending.Min(entry => entry.CreatedUtc)));
            });
    }

    private static TransientCandidateDeliveryCursor Cursor(TransientCandidateJournalEntry entry)
        => new(entry.CreatedUtc, entry.CandidateId);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
        private long _timestamp;
        private DateTimeOffset _now = now;
        private int _timerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _timestamp;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            List<TaskCompletionSource> completed;
            ManualTimer timer;
            lock (_gate)
            {
                timer = new ManualTimer(this, callback, state, Deadline(dueTime), Period(period));
                _timers.Add(timer);
                _timerCount++;
                completed = _waiters.Where(waiter => waiter.Count <= _timerCount)
                    .Select(waiter => waiter.Completion)
                    .ToList();
                _waiters.RemoveAll(waiter => waiter.Count <= _timerCount);
            }
            foreach (var completion in completed)
            {
                completion.TrySetResult();
            }
            return timer;
        }

        internal Task WaitForTimerCountAsync(int count)
        {
            lock (_gate)
            {
                if (_timerCount >= count)
                {
                    return Task.CompletedTask;
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                return completion.Task;
            }
        }

        internal void Advance(TimeSpan duration)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += duration;
                _timestamp += duration.Ticks;
                due = _timers.Where(timer => timer.PrepareToFire(_timestamp)).ToList();
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private long Deadline(TimeSpan dueTime)
            => dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _timestamp + dueTime.Ticks;

        private static long Period(TimeSpan period)
            => period == Timeout.InfiniteTimeSpan ? long.MaxValue : period.Ticks;

        private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                timer.ChangeCore(Deadline(dueTime), Period(period));
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.DisposeCore();
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            long deadline,
            long period) : ITimer
        {
            private bool _disposed;
            private long _deadline = deadline;
            private long _period = period;

            public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
            {
                owner.Change(this, dueTime, nextPeriod);
                return !_disposed;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal bool PrepareToFire(long timestamp)
            {
                if (_disposed || timestamp < _deadline)
                {
                    return false;
                }
                _deadline = _period == long.MaxValue ? long.MaxValue : timestamp + _period;
                return true;
            }

            internal void Fire() => callback(state);

            internal void ChangeCore(long deadline, long period)
            {
                if (!_disposed)
                {
                    _deadline = deadline;
                    _period = period;
                }
            }

            internal void DisposeCore() => _disposed = true;
        }
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

    private sealed class ControlledWaveTransport : ITransientCandidateTransport
    {
        private readonly TaskCompletionSource<TransientCandidateTransportResult>[] _completions =
            Enumerable.Range(0, TransientCandidateDeliveryService.MaximumConcurrentSends)
                .Select(_ => new TaskCompletionSource<TransientCandidateTransportResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _callCount;
        private int _maximumObserved;

        internal Task Started => _started.Task;
        internal int ActiveCount => Volatile.Read(ref _active);
        internal int CallCount => Volatile.Read(ref _callCount);
        internal int MaximumObserved => Volatile.Read(ref _maximumObserved);

        public async ValueTask<TransientCandidateTransportResult> SendAsync(
            TransientCandidateSubmissionEnvelopeV1 submission,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var active = Interlocked.Increment(ref _active);
            SetMaximum(active);
            if (index == _completions.Length - 1)
            {
                _started.TrySetResult();
            }
            try
            {
                return await _completions[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        internal void Complete(int index, TransientCandidateTransportResult result)
            => _completions[index].TrySetResult(result);

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

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _counts = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _counts.AddOrUpdate(eventId.Id, 1, static (_, count) => count + 1);

        internal int Count(int eventId) => _counts.GetValueOrDefault(eventId);
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
