using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
public sealed class SqliteCameraAgentTransientOperatorProjectionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CandidateId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid EventId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly TransientCandidateExtractionOptionsV1 ExtractionOptions = new(
        MinimumResidualAdu: 20,
        MinimumComponentPixels: 2,
        MinimumIntegratedSignalAdu: 40,
        MaximumCandidates: 4,
        ProfileSampleCount: 4,
        MaximumSaturationBridgePixels: 16,
        MaximumForegroundPixels: 1_000,
        MaximumFragmentGapPixels: 0,
        MinimumFragmentAlignmentCosine: 0.95);

    [TestMethod]
    public async Task DetailResolvesJournalSourcesToRetainedCaptures()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        await fixture.ExecuteAsync("""
            CREATE TABLE raw_captures (
                raw_capture_row_id INTEGER PRIMARY KEY,
                capture_id TEXT NOT NULL,
                capture_sequence INTEGER NOT NULL,
                exposure_started_unix_ms INTEGER NOT NULL
            );
            CREATE TABLE transient_candidate_sources (
                candidate_id TEXT NOT NULL,
                source_ordinal INTEGER NOT NULL,
                evidence_id TEXT NOT NULL,
                raw_capture_row_id INTEGER NOT NULL,
                artifact_id TEXT NOT NULL,
                artifact_role INTEGER NOT NULL,
                observation_started_utc_ticks INTEGER NOT NULL,
                observation_ended_utc_ticks INTEGER NOT NULL
            );
            """).ConfigureAwait(false);
        var candidateId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var firstCapture = Guid.NewGuid();
        var secondCapture = Guid.NewGuid();
        var firstArtifact = Guid.NewGuid();
        var secondArtifact = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 9, 4, 6, 30, 0, TimeSpan.Zero);
        await fixture.InsertReservedAsync(candidateId, started.ToUnixTimeMilliseconds()).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "INSERT INTO raw_captures VALUES (11, $c1, 41, $e1), (12, $c2, 42, $e2);",
            ("$c1", firstCapture.ToString("N")), ("$e1", started.ToUnixTimeMilliseconds()),
            ("$c2", secondCapture.ToString("N")), ("$e2", started.AddSeconds(5).ToUnixTimeMilliseconds())).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            """
            INSERT INTO transient_candidate_sources VALUES
                ($candidate, 1, $ev2, 12, $a2, 1, $s2, $d2),
                ($candidate, 0, $ev1, 11, $a1, 0, $s1, $d1);
            """,
            ("$candidate", candidateId.ToString("N")),
            ("$ev1", Guid.NewGuid().ToString("N")), ("$a1", firstArtifact.ToString("N")),
            ("$s1", started.UtcTicks), ("$d1", started.AddSeconds(2).UtcTicks),
            ("$ev2", Guid.NewGuid().ToString("N")), ("$a2", secondArtifact.ToString("N")),
            ("$s2", started.AddSeconds(5).UtcTicks), ("$d2", started.AddSeconds(7).UtcTicks)).ConfigureAwait(false);

        var detail = await fixture.Projection.GetCandidateAsync(candidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail?.Sources);
        Assert.AreEqual(2, detail.Sources.Count);
        Assert.AreEqual(0, detail.Sources[0].Ordinal);
        Assert.AreEqual(firstCapture, detail.Sources[0].CaptureId);
        Assert.AreEqual(41, detail.Sources[0].CaptureSequence);
        Assert.AreEqual(firstArtifact, detail.Sources[0].ArtifactId);
        Assert.AreEqual(HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw, detail.Sources[0].Role);
        Assert.AreEqual(started, detail.Sources[0].ExposureStartedUtc);
        Assert.AreEqual(started, detail.Sources[0].ObservationStartedUtc);
        Assert.AreEqual(started.AddSeconds(2), detail.Sources[0].ObservationEndedUtc);
        Assert.AreEqual(secondCapture, detail.Sources[1].CaptureId);
        Assert.AreEqual(HVO.SkyMonitor.AgentCore.FrameArtifactRole.Calibrated, detail.Sources[1].Role);
    }

    [TestMethod]
    public async Task DetailReportsNoSourcesWithoutTheSourceSchema()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidateId = Guid.Parse("00000000-0000-0000-0000-000000000202");
        await fixture.InsertReservedAsync(candidateId, 100).ConfigureAwait(false);

        var detail = await fixture.Projection.GetCandidateAsync(candidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail?.Sources);
        Assert.AreEqual(0, detail.Sources.Count);
    }

    [TestMethod]
    public async Task PageUsesBoundedDescendingKeysetPagination()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var newest = Guid.Parse("00000000-0000-0000-0000-000000000103");
        var middle = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var oldest = Guid.Parse("00000000-0000-0000-0000-000000000101");
        await fixture.InsertReservedAsync(oldest, 100).ConfigureAwait(false);
        await fixture.InsertReservedAsync(newest, 300).ConfigureAwait(false);
        await fixture.InsertReservedAsync(middle, 200).ConfigureAwait(false);

        var first = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(2), CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(2, first.NextCursor), CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { newest, middle }, first.Items.Select(static item => item.CandidateId).ToArray());
        Assert.IsNotNull(first.NextCursor);
        CollectionAssert.AreEqual(new[] { oldest }, second.Items.Select(static item => item.CandidateId).ToArray());
        Assert.IsNull(second.NextCursor);
        await Assert.ThrowsExactlyAsync<CameraAgentTransientOperatorQueryException>(async () =>
            await fixture.Projection.GetPageAsync(
                new CameraAgentTransientOperatorQuery(101), CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PageUsesCandidateKeyForEqualTimestampAndDoesNotRequireRuntimeSchema()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var newest = Guid.Parse("00000000-0000-0000-0000-000000000103");
        var middle = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var oldest = Guid.Parse("00000000-0000-0000-0000-000000000101");
        await fixture.InsertReservedAsync(oldest, 100).ConfigureAwait(false);
        await fixture.InsertReservedAsync(newest, 100).ConfigureAwait(false);
        await fixture.InsertReservedAsync(middle, 100).ConfigureAwait(false);
        await fixture.ExecuteAsync("DROP TABLE transient_worker_candidates;").ConfigureAwait(false);

        var first = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(2), CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(2, first.NextCursor), CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { newest, middle }, first.Items.Select(static item => item.CandidateId).ToArray());
        CollectionAssert.AreEqual(new[] { oldest }, second.Items.Select(static item => item.CandidateId).ToArray());
    }

    [TestMethod]
    public async Task QuarantinedReservationWithoutCandidatePayloadRemainsVisible()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidateId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        await fixture.InsertReservedAsync(candidateId, 100).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "UPDATE transient_candidates SET state = 'needs_review', phase = 'quarantined';").ConfigureAwait(false);

        var page = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(), CancellationToken.None).ConfigureAwait(false);
        var detail = await fixture.Projection.GetCandidateAsync(candidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, page.Items);
        Assert.AreEqual("Unavailable", page.Items[0].CandidateState);
        Assert.AreEqual("Absent", detail!.CandidateEvidence.State);
        Assert.AreEqual("NeedsReview", detail.Candidate.EventState);
    }

    [TestMethod]
    public async Task DetailProjectsOnlySanitizedCandidateAndExplicitStageStates()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidate = CreateCandidate();
        await fixture.InsertCandidateAsync(candidate).ConfigureAwait(false);

        var detail = await fixture.Projection.GetCandidateAsync(
            candidate.CandidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual("PendingContext", detail!.Candidate.CandidateState);
        Assert.AreEqual("Provisional", detail.Candidate.EventState);
        Assert.AreEqual("Available", detail.CandidateEvidence.State);
        Assert.AreEqual(candidate.CenterEvidenceId, detail.CandidateEvidence.CenterEvidenceId);
        Assert.AreEqual(1, detail.CandidateEvidence.ContextSourceCount);
        Assert.AreEqual("Pending", detail.CausalEvidence.State);
        Assert.AreEqual("Pending", detail.CenteredEvidence.State);
        Assert.AreEqual("Pending", detail.AssessmentEvidence.State);
        Assert.AreEqual("Pending", detail.FinalEvidence.State);
        var json = System.Text.Json.JsonSerializer.Serialize(detail);
        Assert.IsFalse(json.Contains("agent-secret", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("/private/", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("payload", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CorruptCanonicalPayloadFailsClosedWithoutIncludingPayloadInError()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidate = CreateCandidate();
        await fixture.InsertCandidateAsync(candidate).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "UPDATE transient_candidates SET candidate_payload = X'7B7D';").ConfigureAwait(false);

        var page = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, page.Items);
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Projection.GetCandidateAsync(candidate.CandidateId, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.AreEqual("Durable transient operator evidence is corrupt.", exception.Message);
        Assert.IsFalse(exception.Message.Contains("{}", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FinalizedPhaseWithoutFinalizationPayloadFailsClosed()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidate = CreateCandidate();
        await fixture.InsertCandidateAsync(candidate).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "UPDATE transient_candidates SET state = 'validated', phase = 'finalized';").ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Projection.GetCandidateAsync(candidate.CandidateId, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FinalizedCandidateProjectsSanitizedFinalEvidence()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var evidence = CreateEvidence();
        await fixture.InsertFinalizedAsync(evidence.Candidate, evidence.Finalization).ConfigureAwait(false);

        var detail = await fixture.Projection.GetCandidateAsync(
            evidence.Candidate.CandidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual("Provisional", detail.Candidate.CandidateState);
        Assert.AreEqual("Validated", detail.Candidate.EventState);
        Assert.AreEqual("finalized", detail.Candidate.WorkflowPhase);
        Assert.AreEqual("Available", detail.FinalEvidence.State);
        Assert.AreEqual("Validated", detail.FinalEvidence.EventState);
        Assert.AreEqual(evidence.Finalization.Event.Version, detail.FinalEvidence.EventVersion);
        Assert.AreEqual(evidence.Finalization.Event.FirstObservedUtc, detail.FinalEvidence.FirstObservedUtc);
        Assert.AreEqual(evidence.Finalization.Event.LastObservedUtc, detail.FinalEvidence.LastObservedUtc);
        Assert.AreEqual(1, detail.FinalEvidence.ObservationCount);
        Assert.AreEqual(1, detail.FinalEvidence.AssessmentCount);
        Assert.AreEqual(evidence.Finalization.ReceiptIdentitySha256, detail.FinalEvidence.ReceiptIdentitySha256);
        var json = System.Text.Json.JsonSerializer.Serialize(detail);
        Assert.IsFalse(json.Contains("agent-secret", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("linear-component-extractor", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("payload", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RuntimeEvidenceProjectsBoundedStageFactsAndIdentities()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var evidence = CreateEvidence();
        await fixture.InsertCandidateAsync(evidence.Candidate).ConfigureAwait(false);
        await fixture.InsertRuntimeAsync(evidence).ConfigureAwait(false);

        var detail = await fixture.Projection.GetCandidateAsync(
            evidence.Candidate.CandidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual("Available", detail.CausalEvidence.State);
        Assert.AreEqual(false, detail.CausalEvidence.CenteredContextConverged);
        Assert.AreEqual(evidence.Causal.OrderedSources.Count, detail.CausalEvidence.SourceCount);
        Assert.AreEqual(1, detail.CausalEvidence.CandidateCount);
        Assert.AreEqual(evidence.Causal.ExtractionIdentitySha256, detail.CausalEvidence.IdentitySha256);
        Assert.AreEqual("Available", detail.CenteredEvidence.State);
        Assert.AreEqual(true, detail.CenteredEvidence.CenteredContextConverged);
        Assert.AreEqual(evidence.Centered.OrderedSources.Count, detail.CenteredEvidence.SourceCount);
        Assert.AreEqual(1, detail.CenteredEvidence.CandidateCount);
        Assert.AreEqual(evidence.Centered.ExtractionIdentitySha256, detail.CenteredEvidence.IdentitySha256);
        Assert.AreEqual("Available", detail.AssessmentEvidence.State);
        Assert.AreEqual(evidence.Assessment.Assessment.AssessmentId, detail.AssessmentEvidence.AssessmentId);
        Assert.AreEqual(evidence.Assessment.Assessment.Authority.ToString(), detail.AssessmentEvidence.Authority);
        Assert.AreEqual(evidence.Assessment.Assessment.Classification.ToString(), detail.AssessmentEvidence.Classification);
        Assert.AreEqual(evidence.Assessment.Assessment.ConfidenceMillionths, detail.AssessmentEvidence.ConfidenceMillionths);
        Assert.AreEqual(1, detail.AssessmentEvidence.EvidenceObservationCount);
        CollectionAssert.AreEqual(
            evidence.Assessment.Assessment.Reasons.Select(static reason => reason.Code).ToArray(),
            detail.AssessmentEvidence.ReasonCodes!.ToArray());
        Assert.AreEqual(evidence.Assessment.ExecutionIdentitySha256, detail.AssessmentEvidence.IdentitySha256);
    }

    [TestMethod]
    [DataRow("handoff_pending", "Pending")]
    [DataRow("acknowledged", "Absent")]
    public async Task ValidHybridHandoffChainDoesNotRequireLocalFinalization(string phase, string finalState)
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var evidence = CreateEvidence();
        await fixture.InsertCandidateAsync(evidence.Candidate).ConfigureAwait(false);
        await fixture.InsertHandoffAsync(evidence.Candidate, phase).ConfigureAwait(false);

        var detail = await fixture.Projection.GetCandidateAsync(
            evidence.Candidate.CandidateId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual(phase, detail.Candidate.WorkflowPhase);
        Assert.AreEqual(finalState, detail.FinalEvidence.State);
        Assert.IsNull(detail.FinalEvidence.ReceiptIdentitySha256);
    }

    [TestMethod]
    [DataRow("finalization-identity")]
    [DataRow("causal-identity")]
    [DataRow("centered-identity")]
    [DataRow("assessment-identity")]
    [DataRow("finalization-linkage")]
    [DataRow("causal-linkage")]
    [DataRow("centered-linkage")]
    [DataRow("assessment-linkage")]
    public async Task NewlyProjectedEvidenceCorruptionFailsClosed(string corruption)
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var evidence = CreateEvidence();
        var foreign = CreateEvidence(
            Guid.Parse("10000000-0000-0000-0000-000000000099"),
            Guid.Parse("20000000-0000-0000-0000-000000000099"));
        await fixture.InsertFinalizedAsync(evidence.Candidate, evidence.Finalization).ConfigureAwait(false);
        await fixture.InsertRuntimeAsync(evidence).ConfigureAwait(false);
        await fixture.CorruptAsync(corruption, foreign).ConfigureAwait(false);

        var page = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, page.Items);
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Projection.GetCandidateAsync(
                evidence.Candidate.CandidateId, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.AreEqual("Durable transient operator evidence is corrupt.", exception.Message);
    }

    [TestMethod]
    public async Task PageDoesNotMaterializeLargeDetailPayloads()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidate = CreateCandidate();
        await fixture.InsertCandidateAsync(candidate).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            """
            UPDATE transient_candidates
            SET candidate_payload = zeroblob(1048576),
                finalization_receipt_identity_sha256 = $identity,
                finalization_payload = zeroblob(4194304),
                submission_identity_sha256 = $identity,
                submission_payload = zeroblob(1048576),
                acknowledgement_payload_sha256 = $identity,
                acknowledgement_payload = zeroblob(4096);
            INSERT INTO transient_worker_candidates(
                candidate_id, causal_extraction_sha256, causal_extraction_json,
                observation_extraction_sha256, observation_extraction_json,
                assessment_execution_sha256, assessment_execution_json)
            VALUES ($candidate, $identity, zeroblob(1048576), $identity, zeroblob(1048576),
                    $identity, zeroblob(262144));
            """,
            ("$identity", new string('F', 64)),
            ("$candidate", candidate.CandidateId.ToString("N"))).ConfigureAwait(false);

        var page = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(100), CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, page.Items);
        Assert.AreEqual("Available", page.Items[0].CausalEvidenceState);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Projection.GetCandidateAsync(candidate.CandidateId, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DetailRejectsCandidateStateScalarMismatch()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var candidate = CreateCandidate();
        await fixture.InsertCandidateAsync(candidate).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            "UPDATE transient_candidates SET candidate_state = 'Rejected';").ConfigureAwait(false);

        var page = await fixture.Projection.GetPageAsync(
            new CameraAgentTransientOperatorQuery(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Rejected", page.Items.Single().CandidateState);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Projection.GetCandidateAsync(candidate.CandidateId, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DetailRejectsSwappedCausalAndCenteredEvidence()
    {
        using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var evidence = CreateEvidence();
        await fixture.InsertCandidateAsync(evidence.Candidate).ConfigureAwait(false);
        await fixture.InsertRuntimeAsync(evidence).ConfigureAwait(false);
        await fixture.ExecuteAsync(
            """
            UPDATE transient_worker_candidates
            SET causal_extraction_sha256 = observation_extraction_sha256,
                causal_extraction_json = observation_extraction_json,
                observation_extraction_sha256 = causal_extraction_sha256,
                observation_extraction_json = causal_extraction_json;
            """).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Projection.GetCandidateAsync(
                evidence.Candidate.CandidateId, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DetailAcceptsCausalObservationFallbackForNeedsReviewEvent()
    {
        foreach (var eventState in new[] { TransientEventState.NeedsReview, TransientEventState.Rejected })
        {
            using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
            var evidence = CreateEvidence(causalObservationFallbackState: eventState);
            await fixture.InsertFinalizedAsync(evidence.Candidate, evidence.Finalization).ConfigureAwait(false);
            await fixture.InsertRuntimeAsync(evidence).ConfigureAwait(false);

            var detail = await fixture.Projection.GetCandidateAsync(
                evidence.Candidate.CandidateId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(detail);
            Assert.AreEqual(eventState.ToString(), detail.Candidate.EventState);
            Assert.AreEqual(evidence.Causal.ExtractionIdentitySha256, detail.CenteredEvidence.IdentitySha256);
            Assert.IsFalse(detail.CenteredEvidence.CenteredContextConverged);
        }
    }

    private static EvidenceBundle CreateEvidence(
        Guid? candidateId = null,
        Guid? eventId = null,
        TransientEventState? causalObservationFallbackState = null)
    {
        var causal = CreateExtraction(
            TransientTemporalBackgroundKind.CausalProvisional,
            centeredContextConverged: false,
            candidateId ?? CandidateId,
            eventId ?? EventId);
        var centered = CreateExtraction(
            TransientTemporalBackgroundKind.CenteredFinal,
            centeredContextConverged: true,
            candidateId ?? CandidateId,
            eventId ?? EventId);
        var candidate = causal.Candidates.Single();
        var observationExtraction = causalObservationFallbackState is null ? centered : causal;
        var observation = TransientObservationFactory.CreateAssessmentObservation(
            new TransientObservationPromotionRequest(
                candidate.CandidateId,
                Guid.Parse(candidate.CandidateId == CandidateId
                    ? "50000000-0000-0000-0000-000000000001"
                    : "50000000-0000-0000-0000-000000000099"),
                0,
                observationExtraction));
        var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
            candidate.EventId,
            Guid.Parse(candidate.CandidateId == CandidateId
                ? "60000000-0000-0000-0000-000000000001"
                : "60000000-0000-0000-0000-000000000099"),
            Epoch.AddMinutes(3),
            TransientAssessmentAuthority.Authoritative,
            [observation],
            new TransientDeterministicAssessmentOptionsV1(
                2, 2, 4, 2, 1, 0.5, 4, 100, 3, 3, 3, 100, 20, 2),
            []));
        Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, assessment.Status, assessment.ReasonCode);
        var descriptor = assessment.Descriptor!;
        var transientEvent = new TransientEventV1(
            TransientEventV1.CurrentSchemaVersion,
            candidate.EventId,
            Guid.Parse(candidate.CandidateId == CandidateId
                ? "70000000-0000-0000-0000-000000000001"
                : "70000000-0000-0000-0000-000000000099"),
            1,
            null,
            null,
            candidate.AgentId,
            causalObservationFallbackState ?? TransientEventState.Validated,
            Epoch.AddMinutes(3),
            Epoch.AddMinutes(3),
            observation.Observation.Source.ObservationStartedUtc,
            observation.Observation.Source.ObservationEndedUtc,
            [observation.Observation],
            [descriptor.Assessment],
            [],
            [],
            []);
        var finalization = new TransientFinalizationReceiptV1(
            TransientFinalizationReceiptV1.CurrentSchemaVersion,
            candidate.CandidateId,
            candidate.EventId,
            transientEvent,
            new string('0', 64));
        finalization = finalization with
        {
            ReceiptIdentitySha256 = TransientCandidateDeliveryJson.ComputeFinalizationIdentitySha256(finalization)
        };
        _ = TransientCandidateDeliveryJson.Serialize(finalization);
        return new(candidate, causal, observationExtraction, descriptor, finalization);
    }

    private static TransientCandidateExtractionDescriptorV1 CreateExtraction(
        TransientTemporalBackgroundKind kind,
        bool centeredContextConverged,
        Guid candidateId,
        Guid eventId)
    {
        var positions = kind == TransientTemporalBackgroundKind.CausalProvisional
            ? new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1, TransientTemporalPosition.N }
            : new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
                TransientTemporalPosition.N, TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2 };
        var window = positions.ToDictionary(static position => position, CreateSource);
        var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            kind,
            window[TransientTemporalPosition.N],
            window.Values.Where(static source => source.Position != TransientTemporalPosition.N).ToArray(),
            [],
            TimeSpan.FromSeconds(30)));
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, background.Status, background.ReasonCode);
        var product = background.Product ?? throw new InvalidOperationException("Expected a produced background.");
        var byEvidence = window.Values.ToDictionary(static source => source.Input.Descriptor.Source.EvidenceId);
        var extraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            "agent-secret",
            Epoch.AddMinutes(2),
            window[TransientTemporalPosition.N],
            product,
            product.Descriptor.Sources.Select(source => byEvidence[source.EvidenceId]).ToArray(),
            Enumerable.Range(0, ExtractionOptions.MaximumCandidates).Select(index =>
                new TransientCandidateIdentitySlot(
                    index == 0 ? candidateId : Guid.Parse($"81000000-0000-0000-0000-{index:D12}"),
                    index == 0 ? eventId : Guid.Parse($"82000000-0000-0000-0000-{index:D12}"))).ToArray(),
            ExtractionOptions,
            centeredContextConverged));
        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, extraction.Status, extraction.ReasonCode);
        return extraction.Descriptor!;
    }

    private static TransientTemporalSource CreateSource(TransientTemporalPosition position)
    {
        const int width = 8;
        const int height = 3;
        var sequence = 100 + (int)position;
        var values = new ushort[width * height];
        if (position == TransientTemporalPosition.N)
        {
            values[width + 0] = 70;
            values[width + 1] = 80;
            values[width + 2] = 90;
            values[width + 3] = 100;
        }
        var payload = ToLittleEndianBytes(values);
        var started = Epoch.AddSeconds((int)position * 20 + 40);
        var ended = started.AddSeconds(10);
        var artifactId = Guid.Parse($"83000000-0000-0000-0000-{sequence:D12}");
        var evidenceId = Guid.Parse($"84000000-0000-0000-0000-{sequence:D12}");
        var layout = new FrameLayoutDescriptor(
            width,
            height,
            width * 2,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            ushort.MaxValue,
            payload.Length);
        var compatibility = new ProcessingCompatibilityIdentity(
            "rig-v1", "north-up-v1", "calibration-v1", "mask-v1", "sensor-v1", "night-v1", "profile-v1");
        var artifact = new ProcessingArtifact(
            artifactId,
            FrameArtifactRole.Raw,
            "native",
            new string('A', 64),
            "application/x-hvo-frame",
            layout,
            payload,
            ended,
            TimeSpan.FromSeconds(10),
            compatibility,
            sequence,
            ObservationStartedUtc: started,
            ObservationEndedUtc: ended);
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            evidenceId,
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    artifactId,
                    FrameArtifactRole.Raw,
                    "native",
                    new string('A', 64),
                    Convert.ToHexString(SHA256.HashData(payload)))),
            started,
            ended,
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("camera-module", "v1"));
        var input = TransientDetectorInputFactory.Create(
            artifact, source, new TransientLinearLevelsV1(0, ushort.MaxValue, ushort.MaxValue));
        Assert.IsTrue(input.Validation.IsValid, input.Validation.ReasonCode);
        var masks = new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(maskKind => TransientDetectorMask.Create(
            maskKind,
            new ProcessingAlgorithmIdentity($"test-{maskKind.ToString().ToUpperInvariant()}-mask", "v1"),
            Linear16MaskOperations.Empty(width, height))).ToArray();
        return new TransientTemporalSource(
            position,
            sequence,
            input.Input!,
            new TransientSensitivityV1("response-v1", 1, 1),
            masks);
    }

    private static byte[] ToLittleEndianBytes(ushort[] values)
    {
        var output = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            output[index * 2] = (byte)values[index];
            output[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return output;
    }

    private sealed record EvidenceBundle(
        TransientCandidateV1 Candidate,
        TransientCandidateExtractionDescriptorV1 Causal,
        TransientCandidateExtractionDescriptorV1 Centered,
        TransientAssessmentExecutionDescriptorV1 Assessment,
        TransientFinalizationReceiptV1 Finalization);

    private static TransientCandidateV1 CreateCandidate()
    {
        var candidateId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var eventId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var evidenceId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            evidenceId,
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    Guid.Parse("40000000-0000-0000-0000-000000000001"),
                    FrameArtifactRole.Raw,
                    "native",
                    new string('A', 64),
                    new string('B', 64))),
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("capture-manifest", "v1"));
        return new TransientCandidateV1(
            TransientCandidateV1.CurrentSchemaVersion,
            candidateId,
            eventId,
            "agent-secret",
            TransientCandidateState.PendingContext,
            DateTimeOffset.UnixEpoch.AddSeconds(3),
            evidenceId,
            [source],
            new TransientObservationProvenanceV1(new string('C', 64), "cal-v1", "mask-v1", "profile-v1"),
            new TransientObservationExtractionV1(
                candidateId,
                new TransientExtractionProducerV1(
                    TransientExtractionProducerV1.CurrentSchemaVersion,
                    TransientExtractionProducerKind.DeterministicAlgorithm,
                    "detector",
                    "v1"),
                new string('D', 64)),
            null,
            null,
            []);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _databasePath;

        private Fixture(string root)
        {
            _root = root;
            _databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var rawIngress = new Mock<IRawCaptureIngress>(MockBehavior.Strict);
            rawIngress.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
            Projection = new SqliteCameraAgentTransientOperatorProjection(
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                rawIngress.Object);
        }

        internal SqliteCameraAgentTransientOperatorProjection Projection { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("hvo-transient-operator-").FullName;
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            var fixture = new Fixture(root);
            await fixture.ExecuteAsync("""
                CREATE TABLE transient_candidates (
                    candidate_id TEXT PRIMARY KEY,
                    event_id TEXT NOT NULL,
                    state TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    candidate_state TEXT,
                    candidate_payload_sha256 TEXT,
                    candidate_payload BLOB,
                    finalization_receipt_identity_sha256 TEXT,
                    finalization_payload BLOB,
                    submission_identity_sha256 TEXT,
                    submission_payload BLOB,
                    acknowledgement_payload_sha256 TEXT,
                    acknowledgement_payload BLOB,
                    created_unix_ms INTEGER NOT NULL,
                    updated_unix_ms INTEGER NOT NULL
                );
                CREATE TABLE transient_worker_candidates (
                    candidate_id TEXT PRIMARY KEY,
                    causal_extraction_sha256 TEXT,
                    causal_extraction_json BLOB,
                    observation_extraction_sha256 TEXT,
                    observation_extraction_json BLOB,
                    assessment_execution_sha256 TEXT,
                    assessment_execution_json BLOB
                );
                """).ConfigureAwait(false);
            return fixture;
        }

        internal Task InsertReservedAsync(Guid candidateId, long created) => ExecuteAsync(
            """
            INSERT INTO transient_candidates(candidate_id, event_id, state, phase, created_unix_ms, updated_unix_ms)
            VALUES ($candidate, $event, 'pending', 'reserved', $created, $created);
            """,
            ("$candidate", candidateId.ToString("N")),
            ("$event", Guid.NewGuid().ToString("N")),
            ("$created", created));

        internal Task InsertCandidateAsync(TransientCandidateV1 candidate)
        {
            var payload = TransientContractJson.Serialize(candidate);
            return ExecuteAsync(
                """
                INSERT INTO transient_candidates(
                    candidate_id, event_id, state, phase, candidate_state, candidate_payload_sha256,
                    candidate_payload, created_unix_ms, updated_unix_ms)
                VALUES ($candidate, $event, 'provisional', 'candidate_persisted', $candidate_state,
                        $sha, $payload, $created, $created);
                """,
                ("$candidate", candidate.CandidateId.ToString("N")),
                ("$event", candidate.EventId.ToString("N")),
                ("$candidate_state", candidate.State.ToString()),
                ("$sha", Convert.ToHexString(SHA256.HashData(payload))),
                ("$payload", payload),
                ("$created", candidate.CreatedUtc.ToUnixTimeMilliseconds()));
        }

        internal async Task InsertFinalizedAsync(
            TransientCandidateV1 candidate,
            TransientFinalizationReceiptV1 finalization)
        {
            await InsertCandidateAsync(candidate).ConfigureAwait(false);
            var payload = TransientCandidateDeliveryJson.Serialize(finalization);
            await ExecuteAsync(
                """
                UPDATE transient_candidates
                SET state = $state, phase = 'finalized',
                    finalization_receipt_identity_sha256 = $identity,
                    finalization_payload = $payload, updated_unix_ms = $updated
                WHERE candidate_id = $candidate;
                """,
                ("$state", finalization.Event.State switch
                {
                    TransientEventState.NeedsReview => "needs_review",
                    TransientEventState.Rejected => "rejected",
                    _ => "validated"
                }),
                ("$identity", finalization.ReceiptIdentitySha256),
                ("$payload", payload),
                ("$updated", finalization.Event.VersionCreatedUtc.ToUnixTimeMilliseconds()),
                ("$candidate", candidate.CandidateId.ToString("N"))).ConfigureAwait(false);
        }

        internal Task InsertRuntimeAsync(EvidenceBundle evidence)
        {
            var causal = TransientCandidateExtractionJson.Serialize(evidence.Causal);
            var centered = TransientCandidateExtractionJson.Serialize(evidence.Centered);
            var assessment = TransientAssessmentJson.Serialize(evidence.Assessment);
            return ExecuteAsync(
                """
                INSERT INTO transient_worker_candidates(
                    candidate_id, causal_extraction_sha256, causal_extraction_json,
                    observation_extraction_sha256, observation_extraction_json,
                    assessment_execution_sha256, assessment_execution_json)
                VALUES ($candidate, $causal_sha, $causal, $centered_sha, $centered, $assessment_sha, $assessment);
                """,
                ("$candidate", evidence.Candidate.CandidateId.ToString("N")),
                ("$causal_sha", Convert.ToHexString(SHA256.HashData(causal))),
                ("$causal", causal),
                ("$centered_sha", Convert.ToHexString(SHA256.HashData(centered))),
                ("$centered", centered),
                ("$assessment_sha", Convert.ToHexString(SHA256.HashData(assessment))),
                ("$assessment", assessment));
        }

        internal Task InsertHandoffAsync(TransientCandidateV1 candidate, string phase)
        {
            var submission = new TransientCandidateSubmissionEnvelopeV1(
                TransientCandidateSubmissionEnvelopeV1.CurrentSchemaVersion,
                candidate.CandidateId,
                candidate.EventId,
                candidate,
                new string('E', 64),
                "profile-v1",
                new string('0', 64));
            submission = submission with
            {
                SubmissionIdentitySha256 = TransientCandidateDeliveryJson.ComputeSubmissionIdentitySha256(submission)
            };
            var submissionPayload = TransientCandidateDeliveryJson.Serialize(submission);
            if (phase == "handoff_pending")
            {
                return ExecuteAsync(
                    """
                    UPDATE transient_candidates
                    SET phase = 'handoff_pending', submission_identity_sha256 = $identity,
                        submission_payload = $payload
                    WHERE candidate_id = $candidate;
                    """,
                    ("$identity", submission.SubmissionIdentitySha256),
                    ("$payload", submissionPayload),
                    ("$candidate", candidate.CandidateId.ToString("N")));
            }
            var acknowledgement = new TransientCandidateSubmissionAcknowledgementV1(
                TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
                candidate.CandidateId,
                candidate.EventId,
                submission.SubmissionIdentitySha256,
                Epoch.AddMinutes(4),
                TransientCandidateSubmissionDisposition.Accepted);
            var acknowledgementPayload = TransientCandidateDeliveryJson.Serialize(acknowledgement);
            return ExecuteAsync(
                """
                UPDATE transient_candidates
                SET phase = 'acknowledged', submission_identity_sha256 = $submission_identity,
                    submission_payload = $submission, acknowledgement_payload_sha256 = $acknowledgement_sha,
                    acknowledgement_payload = $acknowledgement
                WHERE candidate_id = $candidate;
                """,
                ("$submission_identity", submission.SubmissionIdentitySha256),
                ("$submission", submissionPayload),
                ("$acknowledgement_sha", Convert.ToHexString(SHA256.HashData(acknowledgementPayload))),
                ("$acknowledgement", acknowledgementPayload),
                ("$candidate", candidate.CandidateId.ToString("N")));
        }

        internal Task CorruptAsync(string corruption, EvidenceBundle foreign)
        {
            var invalidIdentity = new string('F', 64);
            return corruption switch
            {
                "finalization-identity" => ExecuteAsync(
                    "UPDATE transient_candidates SET finalization_receipt_identity_sha256 = $value;",
                    ("$value", invalidIdentity)),
                "causal-identity" => ExecuteAsync(
                    "UPDATE transient_worker_candidates SET causal_extraction_sha256 = $value;",
                    ("$value", invalidIdentity)),
                "centered-identity" => ExecuteAsync(
                    "UPDATE transient_worker_candidates SET observation_extraction_sha256 = $value;",
                    ("$value", invalidIdentity)),
                "assessment-identity" => ExecuteAsync(
                    "UPDATE transient_worker_candidates SET assessment_execution_sha256 = $value;",
                    ("$value", invalidIdentity)),
                "finalization-linkage" => ReplaceFinalizationAsync(foreign.Finalization),
                "causal-linkage" => ReplaceRuntimeAsync("causal_extraction", TransientCandidateExtractionJson.Serialize(foreign.Causal)),
                "centered-linkage" => ReplaceRuntimeAsync(
                    "observation_extraction", TransientCandidateExtractionJson.Serialize(foreign.Centered)),
                "assessment-linkage" => ReplaceRuntimeAsync(
                    "assessment_execution", TransientAssessmentJson.Serialize(foreign.Assessment)),
                _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption case.")
            };
        }

        private Task ReplaceFinalizationAsync(TransientFinalizationReceiptV1 finalization)
        {
            var payload = TransientCandidateDeliveryJson.Serialize(finalization);
            return ExecuteAsync(
                """
                UPDATE transient_candidates
                SET finalization_receipt_identity_sha256 = $identity, finalization_payload = $payload;
                """,
                ("$identity", finalization.ReceiptIdentitySha256),
                ("$payload", payload));
        }

        private Task ReplaceRuntimeAsync(string column, byte[] payload) => ExecuteAsync(
            $"UPDATE transient_worker_candidates SET {column}_sha256 = $identity, {column}_json = $payload;",
            ("$identity", Convert.ToHexString(SHA256.HashData(payload))),
            ("$payload", payload));

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Test-only SQL is supplied by fixed test call sites and values remain parameterized.")]
        internal async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
