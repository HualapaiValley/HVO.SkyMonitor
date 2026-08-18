using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Processing;

public enum TransientCandidateSubmissionDisposition
{
    Accepted,
    Duplicate,
    Retired
}

/// <summary>Transport-neutral Hybrid request carrying canonical edge evidence and central execution intent.</summary>
public sealed record TransientCandidateSubmissionEnvelopeV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid CandidateId,
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] TransientCandidateV1 Candidate,
    [property: JsonRequired] string RequestedRecipeIdentitySha256,
    [property: JsonRequired] string RequestedProcessingProfileIdentity,
    [property: JsonRequired] string SubmissionIdentitySha256)
{
    public const string CurrentSchemaVersion = "transient-candidate-submission-v1";
}

/// <summary>Durable central settlement of exactly one canonical Hybrid submission identity.</summary>
public sealed record TransientCandidateSubmissionAcknowledgementV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid CandidateId,
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] string SubmissionIdentitySha256,
    [property: JsonRequired] DateTimeOffset ReceivedAtUtc,
    [property: JsonRequired] TransientCandidateSubmissionDisposition Disposition)
{
    public const string CurrentSchemaVersion = "transient-candidate-submission-acknowledgement-v1";
    public const string RetirementSchemaVersion = "transient-candidate-submission-acknowledgement-v2";
}

/// <summary>Canonical local finalization result used to resume Edge terminal persistence after restart.</summary>
public sealed record TransientFinalizationReceiptV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid CandidateId,
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] TransientEventV1 Event,
    [property: JsonRequired] string ReceiptIdentitySha256)
{
    public const string CurrentSchemaVersion = "transient-finalization-receipt-v1";
}
