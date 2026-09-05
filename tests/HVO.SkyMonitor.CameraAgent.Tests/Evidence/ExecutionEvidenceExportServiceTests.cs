using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>
/// Export-lane behaviour against the in-process conformance sink: ordering, drain, outage recovery, partial and
/// conflicting acknowledgement, gap resynchronization, bounded pressure, cancellation, restart, and the standalone
/// deny-sink case in which nothing is enlisted at all.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ExecutionEvidenceExportServiceTests
{
    [TestMethod]
    public async Task ARevisionIsExportedBeforeItsExecutionsAndTheBatchDrainsInSequenceOrder()
    {
        using var harness = new Harness();
        harness.Seed(3);

        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(
            1,
            harness.State.Snapshot.InFlightRequests,
            "The in-flight high-water mark for the drain cycle is exactly one request.");
        await harness.RunCycleAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4, 5, 6, 7 },
            harness.Sink.AcceptedSequences.ToArray());
        Assert.AreEqual(
            ExecutionEvidenceBodyKind.GraphRevision,
            harness.Sink.AcceptedEnvelopes[0].Kind,
            "The canonical graph body must precede the executions that depend on it.");
        Assert.AreEqual(ExecutionEvidenceBodyKind.GraphExecution, harness.Sink.AcceptedEnvelopes[1].Kind);
        Assert.AreEqual(ExecutionEvidenceBodyKind.ArtifactAvailability, harness.Sink.AcceptedEnvelopes[2].Kind);
        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Healthy, snapshot.Availability);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.Drained, snapshot.ReasonCode);
        Assert.AreEqual(0, snapshot.Backlog.PendingCount + snapshot.Backlog.RetryCount);
        Assert.AreEqual(7, snapshot.DrainedUnits);
        Assert.AreEqual(1, harness.Sink.MaximumConcurrentRequests, "Exactly one request is in flight at a time.");
        Assert.IsNotNull(snapshot.LastAcknowledgementUtc);
    }

    [TestMethod]
    public async Task AnExecutionCarriesNoImagePayloadAndItsOperatorIdentityIsRedacted()
    {
        using var harness = new Harness();
        harness.Seed(1);
        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var execution = harness.Sink.AcceptedEnvelopes
            .Single(static envelope => envelope.Kind == ExecutionEvidenceBodyKind.GraphExecution).Execution!;
        StringAssert.StartsWith(
            execution.TriggerReference!, GraphExecutionEvidenceJson.RedactionPrefix, StringComparison.Ordinal);
        StringAssert.StartsWith(
            execution.Nodes[0].Attempts[0].LeaseOwner!,
            GraphExecutionEvidenceJson.RedactionPrefix,
            StringComparison.Ordinal);
        var artifact = execution.Nodes[0].Outputs[0].Artifact;
        Assert.AreEqual(
            ProcessingIdentity.CreateArtifactId(artifact.OutputIdentitySha256!),
            artifact.ArtifactId,
            "A processing output reconciles by content address, never by carrying bytes.");
        Assert.IsNull(artifact.PayloadLength);
    }

    [TestMethod]
    public async Task MoreThanTheSourceReadWindowAccumulatesDuringAnOutageAndDrainsWithoutGaps()
    {
        using var harness = new Harness(new ExecutionEvidenceExportOptions
        {
            DiscoveryBatchSize = 64,
            MaximumDiscoveryBatchesPerCycle = 16,
            MaximumRequestUnits = 64
        });
        harness.Seed(300);
        harness.Sink.Mode = ConformanceSinkMode.Deny;

        await harness.RunCycleAsync().ConfigureAwait(false);
        var outage = harness.State.Snapshot;
        Assert.IsGreaterThan(256, outage.Backlog.PendingCount + outage.Backlog.RetryCount);
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Degraded, outage.Availability);
        Assert.AreEqual(0, harness.Sink.AcceptedSequences.Count);

        harness.Sink.Mode = ConformanceSinkMode.Accept;
        harness.Clock.Advance(TimeSpan.FromHours(1));
        for (var cycle = 0; cycle < 40 && harness.State.Snapshot.Backlog.PendingCount +
            harness.State.Snapshot.Backlog.RetryCount > 0; cycle++)
        {
            await harness.RunCycleAsync().ConfigureAwait(false);
        }

        var drained = harness.Sink.AcceptedSequences.ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(1, drained.Length).Select(static value => (long)value).ToArray(),
            drained,
            "The drained sequence must be contiguous from one with no gap.");
        Assert.AreEqual(601, drained.Length, "One revision plus an execution and availability unit per capture.");
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Healthy, harness.State.Snapshot.Availability);
    }

    [TestMethod]
    public async Task DuplicateSubmissionIsIdempotentAndAConflictingSequenceIsQuarantinedWithItsStoredHash()
    {
        using var harness = new Harness();
        harness.Seed(1);
        harness.Sink.SeedConflict(1);

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(1, snapshot.ConflictUnits);
        Assert.AreEqual(1, snapshot.Backlog.ConflictCount);
        Assert.IsGreaterThanOrEqualTo(1, snapshot.RejectedUnits);
        Assert.IsGreaterThanOrEqualTo(1, snapshot.Backlog.QuarantinedCount);
        Assert.AreEqual(
            ExecutionEvidenceExportAvailability.Degraded,
            snapshot.Availability,
            "Terminal evidence is never silently replaced; the conflict is visible, not hidden.");

        // Re-submitting the same accepted unit returns the same facts marked duplicate and rewrites nothing.
        var before = harness.Sink.AcceptedSequences.Count;
        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(before, harness.Sink.AcceptedSequences.Count);
    }

    [TestMethod]
    public async Task AnAcknowledgementNamingAHashThisOriginNeverSentIsQuarantinedNotHonoured()
    {
        using var harness = new Harness();
        harness.Seed(2);
        harness.Sink.Mode = ConformanceSinkMode.MisacknowledgeHash;

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.IsGreaterThanOrEqualTo(1, snapshot.Backlog.QuarantinedCount);
        Assert.IsGreaterThanOrEqualTo(1, snapshot.ConflictUnits);
        Assert.AreEqual(0, snapshot.Backlog.AcknowledgedCount, "Retention is never released by a hash we never sent.");
        Assert.AreEqual(0, snapshot.DrainedUnits);
        // One bad acknowledgement must not stall the lane: the cycle completed and reported a bounded state.
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Degraded, snapshot.Availability);
    }

    [TestMethod]
    public async Task APartiallyAcknowledgedBatchKeepsTheUnsettledUnitsAndConverges()
    {
        using var harness = new Harness();
        harness.Seed(2);
        harness.Sink.Mode = ConformanceSinkMode.PartiallyAcknowledge;

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.IsGreaterThan(0, harness.State.Snapshot.Backlog.RetryCount);

        harness.Sink.Mode = ConformanceSinkMode.Accept;
        for (var cycle = 0; cycle < 12 && harness.State.Snapshot.Backlog.PendingCount +
            harness.State.Snapshot.Backlog.RetryCount > 0; cycle++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
            await harness.RunCycleAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(0, harness.State.Snapshot.Backlog.PendingCount + harness.State.Snapshot.Backlog.RetryCount);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, harness.Sink.AcceptedSequences.Count).Select(static value => (long)value).ToArray(),
            harness.Sink.AcceptedSequences.ToArray());
    }

    [TestMethod]
    public async Task ReorderedAndStaleFeedbackFactsSettleEachUnitExactlyOnce()
    {
        using var harness = new Harness();
        harness.Seed(3);

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        harness.Sink.ReorderAndRepeatFacts = true;
        harness.Source.Add(ExecutionEvidenceTestFactory.CreateDetail(4));
        for (var cycle = 0; cycle < 6; cycle++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
            await harness.RunCycleAsync().ConfigureAwait(false);
        }

        var accepted = harness.Sink.AcceptedSequences.ToArray();
        CollectionAssert.AreEqual(
            accepted.Order().ToArray(), accepted.Distinct().Order().ToArray(),
            "A repeated terminal fact must never accept a sequence twice.");
        CollectionAssert.AreEqual(
            Enumerable.Range(1, accepted.Length).Select(static value => (long)value).ToArray(),
            accepted.Order().ToArray());
        Assert.AreEqual(0, harness.State.Snapshot.Backlog.PendingCount + harness.State.Snapshot.Backlog.RetryCount);
        Assert.AreEqual(0, harness.State.Snapshot.Backlog.QuarantinedCount);
    }

    [TestMethod]
    public async Task ARequestTimeoutDefersEveryUnitAndTheBacklogConvergesOnRecovery()
    {
        using var harness = new Harness();
        harness.Seed(2);
        harness.Sink.Mode = ConformanceSinkMode.Timeout;

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        var timedOut = harness.State.Snapshot;
        Assert.AreEqual(0, harness.Sink.AcceptedSequences.Count);
        Assert.IsGreaterThan(0, timedOut.Backlog.RetryCount);
        Assert.AreEqual(0, timedOut.Backlog.QuarantinedCount, "A timeout is recoverable, not terminal.");

        harness.Sink.Mode = ConformanceSinkMode.Accept;
        for (var cycle = 0; cycle < 8 && harness.State.Snapshot.Backlog.PendingCount +
            harness.State.Snapshot.Backlog.RetryCount > 0; cycle++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
            await harness.RunCycleAsync().ConfigureAwait(false);
        }
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4, 5 }, harness.Sink.AcceptedSequences.Order().ToArray());
    }

    [TestMethod]
    public async Task AReceiverGapDrivesABoundedResynchronizationThatClosesIt()
    {
        using var harness = new Harness();
        harness.Seed(3);
        harness.Sink.DropSequences(3, 5);

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(1, harness.State.Snapshot.ResyncRequests);

        harness.Sink.StopDropping();
        for (var cycle = 0; cycle < 12 && harness.State.Snapshot.Backlog.PendingCount +
            harness.State.Snapshot.Backlog.RetryCount > 0; cycle++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
            await harness.RunCycleAsync().ConfigureAwait(false);
        }
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4, 5, 6, 7 },
            harness.Sink.AcceptedSequences.Order().ToArray());
    }

    [TestMethod]
    public async Task AuthenticationBlockedAndTerminalRejectionAreBoundedAndDoNotRetryForever()
    {
        using var harness = new Harness(new ExecutionEvidenceExportOptions { MaximumAttempts = 2 });
        harness.Seed(1);
        harness.Sink.Mode = ConformanceSinkMode.AuthenticationBlocked;

        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Degraded, harness.State.Snapshot.Availability);
        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.IsGreaterThanOrEqualTo(1, snapshot.Backlog.QuarantinedCount);
        Assert.AreEqual(
            0,
            snapshot.Backlog.PendingCount,
            "A unit that spent its attempt budget stops consuming request capacity but stays durable.");
        Assert.AreEqual(0, snapshot.Backlog.AbandonedCount, "Only an operator may abandon evidence.");
    }

    [TestMethod]
    public async Task StoragePressurePausesEnlistmentExplicitlyWithoutDroppingAnything()
    {
        using var harness = new Harness();
        harness.Seed(2);
        harness.SetPressure(true);

        await harness.RunCycleAsync().ConfigureAwait(false);
        var pressured = harness.State.Snapshot;
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Degraded, pressured.Availability);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.StoragePressure, pressured.ReasonCode);
        Assert.IsTrue(pressured.StoragePressure);
        Assert.AreEqual(0, pressured.Backlog.PendingCount);

        harness.SetPressure(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(5, harness.Sink.AcceptedSequences.Count, "Nothing was lost while enlistment was paused.");
    }

    [TestMethod]
    public async Task BacklogSaturationRefusesNewEnlistmentAndRecordsAnExplicitDegradedState()
    {
        using var harness = new Harness(new ExecutionEvidenceExportOptions { MaximumPendingUnits = 2 });
        harness.Seed(3);
        harness.Sink.Mode = ConformanceSinkMode.Deny;

        await harness.RunCycleAsync().ConfigureAwait(false);
        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.BacklogSaturated, snapshot.ReasonCode);
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Degraded, snapshot.Availability);
        Assert.IsLessThanOrEqualTo(2, snapshot.Backlog.PendingCount + snapshot.Backlog.RetryCount);
    }

    [TestMethod]
    public async Task SourceRetentionRemovingUnenlistedEvidenceIsReportedRatherThanSkipped()
    {
        using var harness = new Harness(new ExecutionEvidenceExportOptions { MaximumPendingUnits = 2 });
        harness.Seed(3);
        harness.Sink.Mode = ConformanceSinkMode.Deny;
        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.BacklogSaturated, harness.State.Snapshot.ReasonCode);

        // The source's own retention removes everything the exporter had deferred.
        harness.Source.RemoveThrough(long.MaxValue);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.SourcePruned, snapshot.ReasonCode);
        Assert.AreEqual(1, snapshot.Backlog.SourcePrunedEvents);
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Degraded, snapshot.Availability);
    }

    [TestMethod]
    public async Task AnExecutionWhoseRevisionIsNoLongerPersistedIsSkippedWithoutStallingTheSweep()
    {
        using var harness = new Harness();
        harness.Seed(2);
        harness.Source.RevisionMissing = true;

        await harness.RunCycleAsync().ConfigureAwait(false);

        var stalled = harness.State.Snapshot;
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.ProjectionRejected, stalled.ReasonCode);
        Assert.AreEqual(0, stalled.Backlog.PendingCount, "Nothing may ship without the canonical body it names.");

        // The cursor advanced past the unexportable rows, so a later execution still exports normally. The two
        // skipped executions are a recorded loss rather than a stall.
        harness.Source.RevisionMissing = false;
        harness.Source.Add(ExecutionEvidenceTestFactory.CreateDetail(3));
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await harness.RunCycleAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(
            3,
            harness.Sink.AcceptedSequences.Count,
            "One revision plus the execution and availability unit of the capture that could be exported.");
        Assert.AreEqual(
            ExecutionEvidenceBodyKind.GraphRevision,
            harness.Sink.AcceptedEnvelopes[0].Kind,
            "The revision must still precede the execution once it is persisted again.");
        Assert.AreEqual(2, harness.State.Snapshot.Backlog.ProjectionRejectedEvents);
    }

    [TestMethod]
    public async Task TheSweepStopsAtTheOldestStillRunningExecutionAndResumesWhenItCompletes()
    {
        using var harness = new Harness();
        harness.Seed(3);
        // The second capture is still running, so its acceptance key is the barrier: the sweep may export the first
        // capture and must not pass the second, even though the third is already terminal.
        harness.Source.OldestActiveKey =
            ExecutionEvidenceTestFactory.BaseUtc.AddSeconds(2).ToUnixTimeMilliseconds();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await harness.RunCycleAsync().ConfigureAwait(false);
        }

        var executions = harness.Sink.AcceptedEnvelopes
            .Where(static envelope => envelope.Kind == ExecutionEvidenceBodyKind.GraphExecution)
            .Select(static envelope => envelope.Execution!.ExecutionId)
            .ToArray();
        CollectionAssert.AreEqual(new[] { ExecutionEvidenceTestFactory.ExecutionId(1) }, executions);

        // Once nothing is running the barrier lifts and the sweep resumes from exactly where it stopped.
        harness.Source.OldestActiveKey = null;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await harness.RunCycleAsync().ConfigureAwait(false);
        }
        CollectionAssert.AreEqual(
            new[]
            {
                ExecutionEvidenceTestFactory.ExecutionId(1),
                ExecutionEvidenceTestFactory.ExecutionId(2),
                ExecutionEvidenceTestFactory.ExecutionId(3)
            },
            harness.Sink.AcceptedEnvelopes
                .Where(static envelope => envelope.Kind == ExecutionEvidenceBodyKind.GraphExecution)
                .Select(static envelope => envelope.Execution!.ExecutionId)
                .ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(1, harness.Sink.AcceptedSequences.Count).Select(static value => (long)value).ToArray(),
            harness.Sink.AcceptedSequences.Order().ToArray());
    }

    [TestMethod]
    public async Task AnExecutionIsSweptExactlyOnceAndIsNeverReReadAfterTheCursorPassesIt()
    {
        using var harness = new Harness();
        harness.Seed(2);

        for (var cycle = 0; cycle < 4; cycle++)
        {
            await harness.RunCycleAsync().ConfigureAwait(false);
        }
        var afterDrain = harness.Source.ReadDetailCallCount;
        await harness.RunCycleAsync().ConfigureAwait(false);

        Assert.AreEqual(
            afterDrain,
            harness.Source.ReadDetailCallCount,
            "A settled execution is never re-projected, so the sweep cannot manufacture conflicts or re-enlist it.");
        Assert.AreEqual(0, harness.State.Snapshot.Backlog.ConflictCount);
        Assert.AreEqual(5, harness.Sink.AcceptedSequences.Count);
    }

    [TestMethod]
    public async Task ADurableRowThatCannotBeSealedIsRecordedAsARejectionWithoutStallingTheLaneOrFaultingTheHost()
    {
        using var harness = new Harness();
        harness.Source.Add(ExecutionEvidenceTestFactory.CreateOversizedDetail(1));
        harness.Source.Add(ExecutionEvidenceTestFactory.CreateDetail(2));

        // The sweep must complete: an unsealable row is a bounded refusal, never an exception out of the cycle.
        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(1, snapshot.Backlog.ProjectionRejectedEvents);
        Assert.AreEqual(
            ExecutionEvidenceTestFactory.ExecutionId(2),
            harness.Sink.AcceptedEnvelopes
                .Single(static envelope => envelope.Kind == ExecutionEvidenceBodyKind.GraphExecution)
                .Execution!.ExecutionId,
            "The cursor advanced past the unexportable row and the next execution exported normally.");
    }

    [TestMethod]
    public async Task AUnitAboveTheConfiguredByteBoundIsRejectedRatherThanWedgingTheSweep()
    {
        using var harness = new Harness(new ExecutionEvidenceExportOptions { MaximumUnitBytes = 4096 });
        harness.Source.Add(ExecutionEvidenceTestFactory.CreateLargeDetail(1));

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(
            1,
            snapshot.Backlog.ProjectionRejectedEvents,
            "One unexportable execution counts once, not once per bounded re-read.");
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.ProjectionRejected, snapshot.ReasonCode);
        Assert.AreEqual(0, snapshot.Backlog.PendingCount, "Nothing above the bound is enlisted.");
        Assert.AreEqual(0, harness.Sink.AcceptedSequences.Count);
        Assert.IsGreaterThan(0, harness.Source.ReadTerminalCallCount);
    }

    [TestMethod]
    public async Task AnAcknowledgementAndARejectionForTheSameSequenceSettleAsTheRejection()
    {
        using var harness = new Harness();
        harness.Seed(1);
        harness.Sink.Mode = ConformanceSinkMode.AcknowledgeThenReject;

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        var snapshot = harness.State.Snapshot;
        Assert.IsGreaterThanOrEqualTo(1, snapshot.Backlog.QuarantinedCount);
        Assert.AreEqual(0, snapshot.Backlog.AcknowledgedCount, "A rejection wins regardless of fact order.");
        Assert.AreEqual(0, snapshot.DrainedUnits);
    }

    [TestMethod]
    public async Task NoConfiguredSinkMeansNoSourceSweepAndAnIndefinitelyHealthyStandaloneLane()
    {
        using var harness = new Harness();
        harness.Seed(5);
        harness.Sink.Configured = false;

        for (var cycle = 0; cycle < 5; cycle++)
        {
            harness.Clock.Advance(TimeSpan.FromHours(6));
            await harness.RunCycleAsync().ConfigureAwait(false);
        }

        var snapshot = harness.State.Snapshot;
        Assert.AreEqual(ExecutionEvidenceExportAvailability.Healthy, snapshot.Availability);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.TransportUnconfigured, snapshot.ReasonCode);
        Assert.AreEqual(
            0,
            snapshot.Backlog.PendingCount + snapshot.Backlog.RetryCount + snapshot.Backlog.QuarantinedCount);
        Assert.AreEqual(0, harness.Source.ReadTerminalCallCount, "A deny-sink standalone agent performs no sweep.");
        Assert.AreEqual(0, harness.Sink.SubmissionCount);
    }

    [TestMethod]
    public async Task DisabledExportPerformsNoWorkAndReportsDisabled()
    {
        using var harness = new Harness(new ExecutionEvidenceExportOptions { Enabled = false });
        harness.Seed(2);

        await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        Assert.AreEqual(ExecutionEvidenceExportAvailability.Disabled, harness.State.Snapshot.Availability);
        Assert.AreEqual(ExecutionEvidenceExportReasonCodes.Disabled, harness.State.Snapshot.ReasonCode);
        Assert.AreEqual(0, harness.Source.ReadTerminalCallCount);
        Assert.IsFalse(Directory.Exists(Path.Combine(harness.Root.Path, "evidence")));
    }

    [TestMethod]
    public async Task AnUnsupportedNegotiationStopsSendingWithoutPartiallyInterpretingAnything()
    {
        using var harness = new Harness();
        harness.Seed(1);
        harness.Sink.NegotiationDisposition = ExecutionEvidenceNegotiationDisposition.Unsupported;

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        Assert.AreEqual(
            ExecutionEvidenceExportReasonCodes.NegotiationRejected, harness.State.Snapshot.ReasonCode);
        Assert.AreEqual(0, harness.Sink.SubmissionCount);
        Assert.AreEqual(
            1, harness.Sink.NegotiationCount, "A refused negotiation is terminal for this process, not a retry loop.");
    }

    [TestMethod]
    public async Task ThePublishedLimitsAreAppliedAsTheMinimumOfPublishedAndLocal()
    {
        var stricter = ExecutionEvidenceLimitsV1.Current with { MaximumResyncUnits = 2 };
        using var harness = new Harness(new ExecutionEvidenceExportOptions { MaximumRequestUnits = 64 });
        harness.Seed(6);
        harness.Sink.PublishedLimits = stricter;

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);

        Assert.IsLessThanOrEqualTo(2, harness.Sink.LargestRequestUnits);
        Assert.AreEqual(
            ExecutionEvidenceLimitsV1.Current.MaximumResyncUnits,
            ExecutionEvidenceExportService.Minimum(ExecutionEvidenceLimitsV1.Current, ExecutionEvidenceLimitsV1.Current)
                .MaximumResyncUnits);
        Assert.AreEqual(
            2,
            ExecutionEvidenceExportService.Minimum(ExecutionEvidenceLimitsV1.Current, stricter).MaximumResyncUnits);
    }

    [TestMethod]
    public async Task ARestartResumesTheDurableBacklogUnderANewOriginSequenceSpace()
    {
        using var root = new TemporaryRoot();
        using (var first = new Harness(root: root))
        {
            first.Seed(2);
            first.Sink.Mode = ConformanceSinkMode.Deny;
            await first.RunCycleAsync().ConfigureAwait(false);
            Assert.IsGreaterThan(0, first.State.Snapshot.Backlog.PendingCount);
        }

        using var second = new Harness(root: root, bootSessionId: new("66666666-6666-4666-8666-666666666666"));
        // Executions the first boot never saw, so the restart's own origin genuinely enlists.
        second.Source.Add(ExecutionEvidenceTestFactory.CreateDetail(3));
        second.Source.Add(ExecutionEvidenceTestFactory.CreateDetail(4));
        for (var cycle = 0; cycle < 12 && (second.State.Snapshot.Backlog.PendingCount +
            second.State.Snapshot.Backlog.RetryCount > 0 || cycle < 3); cycle++)
        {
            second.Clock.Advance(TimeSpan.FromMinutes(10));
            await second.RunCycleAsync().ConfigureAwait(false);
        }

        // The first boot's units are still durable and drain; the restart's own units use a fresh sequence space.
        Assert.AreEqual(0, second.State.Snapshot.Backlog.PendingCount + second.State.Snapshot.Backlog.RetryCount);
        var origins = second.Sink.AcceptedEnvelopes
            .Select(static envelope => envelope.Origin.IdentitySha256)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(2, origins.Length, "Each boot session exports under its own origin identity.");
        foreach (var origin in origins)
        {
            var perOrigin = second.Sink.AcceptedEnvelopes
                .Where(envelope => string.Equals(
                    envelope.Origin.IdentitySha256, origin, StringComparison.Ordinal))
                .OrderBy(static envelope => envelope.OriginSequence)
                .ToArray();
            Assert.AreEqual(1, perOrigin[0].OriginSequence, "Every origin starts its own sequence space at one.");
            Assert.AreEqual(
                ExecutionEvidenceBodyKind.GraphRevision,
                perOrigin[0].Kind,
                "The restart re-exports the canonical body under its new origin before any execution.");
            CollectionAssert.AreEqual(
                Enumerable.Range(1, perOrigin.Length).Select(static value => (long)value).ToArray(),
                perOrigin.Select(static envelope => envelope.OriginSequence).ToArray());
        }
    }

    [TestMethod]
    public async Task CancellationStopsTheCycleWithoutLosingDurableWork()
    {
        using var harness = new Harness();
        harness.Seed(2);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await harness.Service.RunCycleOnceAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);

        await harness.RunCycleAsync().ConfigureAwait(false);
        await harness.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(5, harness.Sink.AcceptedSequences.Count);
    }

    private sealed class Harness : IDisposable
    {
        private readonly bool _ownsRoot;

        internal Harness(
            ExecutionEvidenceExportOptions? exportOptions = null,
            TemporaryRoot? root = null,
            Guid? bootSessionId = null)
        {
            _ownsRoot = root is null;
            Root = root ?? new TemporaryRoot();
            Clock = new MutableTimeProvider(ExecutionEvidenceTestFactory.BaseUtc);
            exportOptions ??= new ExecutionEvidenceExportOptions();
            Options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Root.Path,
                ExecutionEvidenceExport = exportOptions
            });
            Outbox = new SqliteExecutionEvidenceOutbox(Clock);
            Sink = new ExecutionEvidenceConformanceSink(Clock);
            Source = new FakeExecutionEvidenceSource();
            State = new ExecutionEvidenceExportState();
            Telemetry = new ExecutionEvidenceExportTelemetry(State, Clock);
            Pressure = new StoragePressureState();
            var configuration = new CameraAgentConfigurationAccessor();
            configuration.SetConfiguration(CreateConfiguration());
            Service = new ExecutionEvidenceExportService(
                Outbox,
                Sink,
                new FixedOriginProvider(bootSessionId ?? new Guid("33333333-3333-4333-8333-333333333333")),
                Source,
                configuration,
                State,
                Telemetry,
                new ExecutionEvidenceExportWakeup(),
                Pressure,
                Options,
                Clock,
                NullLogger<ExecutionEvidenceExportService>.Instance);
        }

        internal TemporaryRoot Root { get; }

        internal MutableTimeProvider Clock { get; }

        internal SqliteExecutionEvidenceOutbox Outbox { get; }

        internal ExecutionEvidenceConformanceSink Sink { get; }

        internal FakeExecutionEvidenceSource Source { get; }

        internal ExecutionEvidenceExportState State { get; }

        internal ExecutionEvidenceExportTelemetry Telemetry { get; }

        internal StoragePressureState Pressure { get; }

        internal IOptions<CameraAgentHostOptions> Options { get; }

        internal ExecutionEvidenceExportService Service { get; }

        internal void Seed(int count)
        {
            for (var ordinal = 1; ordinal <= count; ordinal++)
            {
                Source.Add(ExecutionEvidenceTestFactory.CreateDetail(ordinal));
            }
        }

        internal void SetPressure(bool underPressure)
            => Pressure.Set(new(
                Path.GetFullPath(Root.Path),
                new StorageCapacity(1_000_000, underPressure ? 1_000 : 900_000),
                underPressure,
                1,
                Clock.GetUtcNow(),
                null));

        internal ValueTask RunCycleAsync() => Service.RunCycleOnceAsync(CancellationToken.None);

        public void Dispose()
        {
            Service.Dispose();
            Telemetry.Dispose();
            Outbox.Dispose();
            if (_ownsRoot)
            {
                Root.Dispose();
            }
        }

        private static CameraModuleConfig CreateConfiguration()
        {
            using var moduleOptions = System.Text.Json.JsonDocument.Parse("{}");
            return new(
                new ObservatoryLocation(20, -155, 1000, "Pacific/Honolulu"),
                new CameraModuleDescriptor("VirtualSky", moduleOptions.RootElement.Clone()),
                new CameraRigConfig(
                    new SensorProfile("evidence-sensor", 10, 10, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                    new OpticsProfile("EquidistantFisheye", 2, 180, 2),
                    new RigOrientation(90, 0, 0),
                    new PipelineExposureProfile(
                        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
                CapturePipelineConfig.Empty,
                AgentId: "agent-evidence-export");
        }

        private sealed class FixedOriginProvider(Guid bootSessionId) : IExecutionEvidenceOriginProvider
        {
            public ValueTask<ExecutionEvidenceOriginDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
                => ValueTask.FromResult(new ExecutionEvidenceOriginDescriptor(
                    new("11111111-1111-4111-8111-111111111111"),
                    new("22222222-2222-4222-8222-222222222222"),
                    "1.0.0-export-test"));

            internal Guid BootSessionId => bootSessionId;
        }
    }
}
