using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

internal static class TransientDeliveryTestData
{
    internal static TransientCandidateSubmissionEnvelopeV1 Submission(string agentId = "device-1", int identity = 1)
    {
        var candidateId = Guid.Parse($"10000000-0000-0000-0000-{identity:D12}");
        var eventId = Guid.Parse($"20000000-0000-0000-0000-{identity:D12}");
        var evidenceId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var candidate = new TransientCandidateV1(
            TransientCandidateV1.CurrentSchemaVersion,
            candidateId,
            eventId,
            agentId,
            TransientCandidateState.PendingContext,
            DateTimeOffset.UnixEpoch.AddSeconds(3),
            evidenceId,
            [new TransientSourceEvidenceReferenceV1(
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
                new TransientTimingProvenanceV1("capture-manifest", "v1"))],
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
        var submission = new TransientCandidateSubmissionEnvelopeV1(
            TransientCandidateSubmissionEnvelopeV1.CurrentSchemaVersion,
            candidateId,
            eventId,
            candidate,
            new string('E', 64),
            "profile-v1",
            new string('0', 64));
        return submission with
        {
            SubmissionIdentitySha256 = TransientCandidateDeliveryJson.ComputeSubmissionIdentitySha256(submission)
        };
    }

    internal static TransientCandidateSubmissionAcknowledgementV1 Acknowledgement(
        TransientCandidateSubmissionEnvelopeV1 submission,
        TransientCandidateSubmissionDisposition disposition = TransientCandidateSubmissionDisposition.Accepted)
        => new(
            disposition == TransientCandidateSubmissionDisposition.Retired
                ? TransientCandidateSubmissionAcknowledgementV1.RetirementSchemaVersion
                : TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion,
            submission.CandidateId,
            submission.EventId,
            submission.SubmissionIdentitySha256,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            disposition);

    internal static TransientCandidateJournalEntry Entry(
        TransientCandidateSubmissionEnvelopeV1 submission,
        DateTimeOffset? createdUtc = null)
        => new(
            submission.CandidateId,
            submission.EventId,
            submission.Candidate.AgentId,
            TransientEventState.Provisional,
            createdUtc ?? DateTimeOffset.UnixEpoch,
            createdUtc ?? DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(10),
            submission.Candidate.ContextSources,
            TransientOperatingMode.Hybrid,
            true,
            TransientCandidateWorkflowPhase.HandoffPending,
            new string('F', 64),
            null,
            null,
            submission.SubmissionIdentitySha256,
            null,
            false,
            null,
            submission.Candidate,
            null,
            submission,
            null);
}
