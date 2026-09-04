using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class GraphExecutionEvidenceContractTests
{
    /// <summary>
    /// Name fragments that would indicate the contract carries image bytes, filesystem locations, or host
    /// credentials. <c>FieldPath</c>-style validation members and the published byte limits are excluded by the
    /// type filter below, because they describe the contract rather than carry payload.
    /// </summary>
    private static readonly string[] ForbiddenMemberNameFragments =
    [
        "payloadbytes", "contentbytes", "imagebytes", "base64", "relativepath", "filepath", "directory",
        "filename", "sidecar", "secret", "token", "credential", "password", "connectionstring",
        "uri", "url", "endpoint"
    ];

    private static readonly long[] ExpectedGapStarts = [5L, 8L];
    private static readonly long[] ExpectedGapEnds = [5L, 9L];
    private static readonly long[] ExpectedResyncStarts = [4L, 6L];
    private static readonly long[] ExpectedResyncEnds = [4L, 8L];

    [TestMethod]
    public async Task LocalOnlyRevisionGoldenMatchesCanonicalBytes()
        => await AssertEnvelopeGoldenAsync(
            GraphExecutionEvidenceFixtures.LocalRevisionFixture,
            GraphExecutionEvidenceFixtures.CreateLocalRevisionEnvelope()).ConfigureAwait(false);

    [TestMethod]
    public async Task CentrallyAssignedRevisionGoldenMatchesCanonicalBytes()
        => await AssertEnvelopeGoldenAsync(
            GraphExecutionEvidenceFixtures.AssignedRevisionFixture,
            GraphExecutionEvidenceFixtures.CreateAssignedRevisionEnvelope()).ConfigureAwait(false);

    [TestMethod]
    public async Task LiveExecutionGoldenMatchesCanonicalBytes()
        => await AssertEnvelopeGoldenAsync(
            GraphExecutionEvidenceFixtures.LiveExecutionFixture,
            GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope()).ConfigureAwait(false);

    [TestMethod]
    public async Task ReplayExecutionGoldenMatchesCanonicalBytes()
        => await AssertEnvelopeGoldenAsync(
            GraphExecutionEvidenceFixtures.ReplayExecutionFixture,
            GraphExecutionEvidenceFixtures.CreateReplayExecutionEnvelope()).ConfigureAwait(false);

    [TestMethod]
    public async Task CorrectionGoldenMatchesCanonicalBytes()
        => await AssertEnvelopeGoldenAsync(
            GraphExecutionEvidenceFixtures.CorrectionFixture,
            GraphExecutionEvidenceFixtures.CreateCorrectionEnvelope()).ConfigureAwait(false);

    [TestMethod]
    public async Task AvailabilityGoldenMatchesCanonicalBytes()
        => await AssertEnvelopeGoldenAsync(
            GraphExecutionEvidenceFixtures.AvailabilityFixture,
            GraphExecutionEvidenceFixtures.CreateAvailabilityEnvelope()).ConfigureAwait(false);

    [TestMethod]
    public async Task FeedbackGoldenMatchesCanonicalBytes()
    {
        var feedback = GraphExecutionEvidenceFixtures.CreateFeedback();
        await AssertGoldenAsync(
            GraphExecutionEvidenceFixtures.FeedbackFixture,
            GraphExecutionEvidenceJson.Serialize(feedback),
            static bytes => GraphExecutionEvidenceJson.ParseFeedback(bytes).Value,
            GraphExecutionEvidenceJson.Serialize).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ResyncRequestGoldenMatchesCanonicalBytes()
    {
        var request = GraphExecutionEvidenceFixtures.CreateResyncRequest();
        await AssertGoldenAsync(
            GraphExecutionEvidenceFixtures.ResyncFixture,
            GraphExecutionEvidenceJson.Serialize(request),
            static bytes => GraphExecutionEvidenceJson.ParseResyncRequest(bytes).Value,
            GraphExecutionEvidenceJson.Serialize).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task NegotiationResponseGoldenMatchesCanonicalBytes()
    {
        var response = GraphExecutionEvidenceFixtures.CreateNegotiationResponse();
        await AssertGoldenAsync(
            GraphExecutionEvidenceFixtures.NegotiationFixture,
            GraphExecutionEvidenceJson.Serialize(response),
            static bytes => GraphExecutionEvidenceJson.ParseNegotiationResponse(bytes).Value,
            GraphExecutionEvidenceJson.Serialize).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnknownFutureVersionGoldenIsRejectedWhole()
    {
        var fixture = await ReadFixtureAsync(GraphExecutionEvidenceFixtures.UnknownFutureFixture)
            .ConfigureAwait(false);
        CollectionAssert.AreEqual(fixture, GraphExecutionEvidenceFixtures.CreateUnknownFuturePayload());
        var parsed = GraphExecutionEvidenceJson.ParseEnvelope(fixture);
        Assert.IsNull(parsed.Value);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, parsed.Validation.ReasonCode);
        Assert.AreEqual("schemaVersion", parsed.Validation.FieldPath);
        Assert.IsFalse(GraphExecutionEvidenceSchemaVersions.IsSupported(
            "hvo-cameraagent-execution-evidence-v2"));
    }

    [TestMethod]
    public void CanonicalPayloadHashExcludesItselfAndCoversEveryOtherMember()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        Assert.AreEqual(envelope.PayloadSha256, GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(envelope));
        Assert.AreEqual(
            envelope.PayloadSha256,
            GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(
                envelope with { PayloadSha256 = new string('A', 64) }));
        Assert.AreNotEqual(
            envelope.PayloadSha256,
            GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(envelope with { OriginSequence = 99 }));
        Assert.AreNotEqual(
            envelope.PayloadSha256,
            GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(
                envelope with { ProducedAtUtc = envelope.ProducedAtUtc.AddTicks(1) }));
        Assert.AreNotEqual(
            envelope.PayloadSha256,
            GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(envelope with
            {
                Execution = envelope.Execution! with { AttemptCount = 3 }
            }));
    }

    [TestMethod]
    public void OriginIdentityIsContentAddressedAndVerifiedOnValidation()
    {
        var origin = GraphExecutionEvidenceFixtures.Origin;
        Assert.AreEqual(origin.IdentitySha256, GraphExecutionEvidenceJson.ComputeOriginIdentitySha256(origin));
        Assert.AreNotEqual(
            origin.IdentitySha256,
            GraphExecutionEvidenceJson.ComputeOriginIdentitySha256(origin with { BootSessionId = Guid.NewGuid() }));

        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var tampered = envelope with { Origin = origin with { SoftwareVersion = "9.9.9" } };
        var validation = GraphExecutionEvidenceJson.Validate(tampered);
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidOrigin, validation.ReasonCode);
        Assert.AreEqual("origin.identitySha256", validation.FieldPath);
    }

    [TestMethod]
    public void RevisionDefinitionIdentityIsContentAddressed()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLocalRevisionEnvelope();
        var revision = envelope.GraphRevision!;
        Assert.AreEqual(
            revision.DefinitionIdentitySha256,
            ProcessingGraphJson.ComputeDefinitionIdentity(GraphExecutionEvidenceFixtures.CreateDefinition()));
        Assert.AreEqual(
            revision.DefinitionIdentitySha256,
            CaptureContractJson.ComputeCanonicalJsonSha256(revision.CanonicalDefinition));

        using var other = JsonDocument.Parse("""{"schemaVersion":"hvo-processing-graph-v1"}""");
        var validation = GraphExecutionEvidenceJson.Validate(envelope with
        {
            GraphRevision = revision with { CanonicalDefinition = other.RootElement.Clone() }
        });
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidHash, validation.ReasonCode);
        Assert.AreEqual("graphRevision.definitionIdentitySha256", validation.FieldPath);
    }

    [TestMethod]
    public void ProcessingOutputArtifactIdentityReconcilesWithoutImageBytes()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var output = envelope.Execution!.Nodes[0].Outputs[0];
        Assert.AreEqual(
            ProcessingIdentity.CreateArtifactId(output.Artifact.OutputIdentitySha256!),
            output.Artifact.ArtifactId);

        var validation = GraphExecutionEvidenceJson.Validate(Rebind(
            envelope,
            output.Artifact with { ArtifactId = Guid.NewGuid() }));
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidIdentity, validation.ReasonCode);

        var rawInput = envelope.Execution.Nodes[0].Inputs[0];
        Assert.IsNull(rawInput.Artifact.OutputIdentitySha256);
        Assert.IsNotNull(rawInput.Artifact.DescriptorSha256);
        Assert.IsNotNull(rawInput.Artifact.PayloadSha256);
    }

    [TestMethod]
    public void ContractSurfaceCarriesNoImageBytesPathsOrHostCredentials()
    {
        var forbidden = ForbiddenMemberNameFragments;
        var evidenceTypes = typeof(ExecutionEvidenceEnvelopeV1).Assembly.ExportedTypes
            .Where(static type => type.Namespace == typeof(ExecutionEvidenceEnvelopeV1).Namespace &&
                type != typeof(ExecutionEvidenceLimitsV1) &&
                type != typeof(ExecutionEvidenceValidationResult) &&
                (type.Name.StartsWith("ExecutionEvidence", StringComparison.Ordinal) ||
                    type.Name.StartsWith("GraphExecutionEvidence", StringComparison.Ordinal) ||
                    type.Name.StartsWith("GraphRevisionEvidence", StringComparison.Ordinal) ||
                    type.Name.StartsWith("ArtifactAvailability", StringComparison.Ordinal)))
            .ToArray();
        var members = evidenceTypes
            .SelectMany(static type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();
        Assert.IsTrue(members.Length > 0);
        var offending = members
            .Where(member => forbidden.Any(name =>
                member.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                !member.EndsWith(".FieldPath", StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), offending, string.Join(", ", offending));

        Assert.IsFalse(evidenceTypes
            .SelectMany(static type => type.GetProperties())
            .Any(static property => property.PropertyType == typeof(byte[]) ||
                property.PropertyType == typeof(ReadOnlyMemory<byte>) ||
                property.PropertyType == typeof(Stream)));
    }

    [TestMethod]
    public void DeterministicRoundTripPreservesCanonicalBytesForEveryFixture()
    {
        foreach (var envelope in AllEnvelopes())
        {
            var first = GraphExecutionEvidenceJson.Serialize(envelope);
            var parsed = GraphExecutionEvidenceJson.ParseEnvelope(first);
            Assert.IsNotNull(parsed.Value, envelope.EvidenceId.ToString());
            var second = GraphExecutionEvidenceJson.Serialize(parsed.Value);
            CollectionAssert.AreEqual(first, second, envelope.EvidenceId.ToString());
            Assert.AreEqual(envelope.PayloadSha256, parsed.Value.PayloadSha256);
            Assert.AreEqual(
                envelope.PayloadSha256,
                GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(parsed.Value));
        }
    }

    [TestMethod]
    public void TamperedPayloadHashIsRejectedOnParse()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var bytes = GraphExecutionEvidenceJson.Serialize(envelope);
        var text = System.Text.Encoding.UTF8.GetString(bytes)
            .Replace(envelope.PayloadSha256, new string('B', 64), StringComparison.Ordinal);
        var parsed = GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.IsNull(parsed.Value);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidHash, parsed.Validation.ReasonCode);
    }

    [TestMethod]
    public void DuplicateSubmissionIsIdempotentAndConflictIsRejected()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var now = GraphExecutionEvidenceFixtures.BaseUtc.AddMinutes(4);

        var first = GraphExecutionEvidenceJson.CreateReceiverFacts(envelope, null, now);
        CollectionAssert.AreEqual(
            new[]
            {
                ExecutionEvidenceFactKind.Received,
                ExecutionEvidenceFactKind.Validated,
                ExecutionEvidenceFactKind.Accepted,
                ExecutionEvidenceFactKind.Acknowledged
            },
            first.Select(static fact => fact.Kind).ToArray());
        Assert.IsTrue(first.All(static fact => !fact.Duplicate && fact.ReasonCode is null));

        var repeat = GraphExecutionEvidenceJson.CreateReceiverFacts(envelope, envelope.PayloadSha256, now);
        Assert.AreEqual(4, repeat.Length);
        Assert.IsTrue(repeat.All(static fact => fact.Duplicate));
        Assert.AreEqual(ExecutionEvidenceFactKind.Acknowledged, repeat[^1].Kind);

        var conflicting = GraphExecutionEvidenceFixtures.CreateReplayExecutionEnvelope() with
        {
            OriginSequence = envelope.OriginSequence
        };
        var sealedConflict = GraphExecutionEvidenceJson.Seal(conflicting with
        {
            PayloadSha256 = GraphExecutionEvidenceJson.UnhashedPayloadSha256
        });
        var conflict = GraphExecutionEvidenceJson.CreateReceiverFacts(
            sealedConflict, envelope.PayloadSha256, now);
        Assert.AreEqual(1, conflict.Length);
        Assert.AreEqual(ExecutionEvidenceFactKind.Rejected, conflict[0].Kind);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.SequenceConflict, conflict[0].ReasonCode);
        Assert.AreEqual(envelope.PayloadSha256, conflict[0].StoredPayloadSha256);
        Assert.IsFalse(conflict[0].Duplicate);
    }

    [TestMethod]
    public void InvalidSubmissionIsReceivedThenRejectedWithItsReason()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope() with
        {
            PayloadSha256 = new string('C', 64)
        };
        var facts = GraphExecutionEvidenceJson.CreateReceiverFacts(
            envelope, null, GraphExecutionEvidenceFixtures.BaseUtc);
        Assert.AreEqual(2, facts.Length);
        Assert.AreEqual(ExecutionEvidenceFactKind.Received, facts[0].Kind);
        Assert.AreEqual(ExecutionEvidenceFactKind.Rejected, facts[1].Kind);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidHash, facts[1].ReasonCode);
        Assert.IsNull(facts[1].StoredPayloadSha256);
    }

    [TestMethod]
    public void GapDetectionIsOrderedInclusiveAndBounded()
    {
        var ranges = GraphExecutionEvidenceJson.DetectGaps(3, [4, 6, 7, 10], out var truncated);
        Assert.IsFalse(truncated);
        CollectionAssert.AreEqual(ExpectedGapStarts, ranges.Select(static range => range.FromSequence).ToArray());
        CollectionAssert.AreEqual(ExpectedGapEnds, ranges.Select(static range => range.ToSequence).ToArray());

        Assert.AreEqual(0, GraphExecutionEvidenceJson.DetectGaps(3, [4, 5, 6], out truncated).Length);
        Assert.IsFalse(truncated);
        Assert.AreEqual(0, GraphExecutionEvidenceJson.DetectGaps(0, [], out truncated).Length);

        var wide = Enumerable.Range(0, GraphExecutionEvidenceLimits.MaximumMissingRanges + 5)
            .Select(static index => 2L + (index * 2L))
            .ToArray();
        var bounded = GraphExecutionEvidenceJson.DetectGaps(0, wide, out truncated);
        Assert.IsTrue(truncated);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumMissingRanges, bounded.Length);
    }

    [TestMethod]
    public void ResynchronizationRequestIsBoundedByRangesAndUnits()
    {
        var feedback = GraphExecutionEvidenceFixtures.CreateFeedback();
        var request = GraphExecutionEvidenceJson.CreateResyncRequest(feedback);
        Assert.IsNotNull(request);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.SequenceGap, request.ReasonCode);
        Assert.AreEqual(feedback.OriginIdentitySha256, request.OriginIdentitySha256);
        CollectionAssert.AreEqual(ExpectedResyncStarts, request.Ranges.Select(static r => r.FromSequence).ToArray());
        CollectionAssert.AreEqual(ExpectedResyncEnds, request.Ranges.Select(static r => r.ToSequence).ToArray());
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(request).IsValid);

        var huge = feedback with
        {
            MissingRanges =
            [
                new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 4, 4 + 10_000)
            ]
        };
        var clipped = GraphExecutionEvidenceJson.CreateResyncRequest(huge);
        Assert.IsNotNull(clipped);
        Assert.AreEqual(1, clipped.Ranges.Length);
        Assert.AreEqual(
            GraphExecutionEvidenceLimits.MaximumResyncUnits,
            clipped.Ranges[0].ToSequence - clipped.Ranges[0].FromSequence + 1);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(clipped).IsValid);

        Assert.IsNull(GraphExecutionEvidenceJson.CreateResyncRequest(feedback with { MissingRanges = [] }));
    }

    [TestMethod]
    public void AcknowledgementRetentionIsExplicitAndBounded()
    {
        var feedback = GraphExecutionEvidenceFixtures.CreateFeedback();
        Assert.AreEqual(feedback.ContiguousThroughSequence, feedback.Retention.AcknowledgedThroughSequence);
        Assert.IsTrue(feedback.Retention.AcknowledgementsRetainedUntilUtc > feedback.ServerTimeUtc);
        Assert.IsTrue(feedback.Retention.MaximumRetainedAcknowledgements > 0);
        AssertInvalid(
            feedback with { Retention = feedback.Retention with { MaximumRetainedAcknowledgements = 0 } },
            GraphExecutionEvidenceReasonCodes.InvalidRetention,
            "retention");
    }

    [TestMethod]
    public void AppendOnlyCorrectionRequiresAStrictlyGreaterSuccessorSequence()
    {
        var correction = GraphExecutionEvidenceFixtures.CreateCorrectionEnvelope();
        var live = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        Assert.AreEqual(live.EvidenceId, correction.Correction!.CorrectsEvidenceId);
        Assert.AreEqual(live.OriginSequence, correction.Correction.CorrectsOriginSequence);
        Assert.IsTrue(correction.OriginSequence > live.OriginSequence);
        Assert.AreNotEqual(live.PayloadSha256, correction.PayloadSha256);

        AssertInvalid(
            correction with { Correction = correction.Correction with { CorrectsOriginSequence = 6 } },
            GraphExecutionEvidenceReasonCodes.InvalidCorrection,
            "correction.correctsOriginSequence");
        AssertInvalid(
            correction with
            {
                Correction = correction.Correction with { CorrectsEvidenceId = correction.EvidenceId }
            },
            GraphExecutionEvidenceReasonCodes.InvalidCorrection,
            "correction.correctsEvidenceId");
    }

    [TestMethod]
    public void AvailabilityObservationsAreSeparateFromImmutableProductionFacts()
    {
        var availability = GraphExecutionEvidenceFixtures.CreateAvailabilityEnvelope();
        Assert.AreEqual(ExecutionEvidenceBodyKind.ArtifactAvailability, availability.Kind);
        Assert.IsNull(availability.Execution);
        Assert.IsNull(availability.GraphRevision);
        Assert.IsTrue(availability.Availability!.Observations.All(static observation =>
            observation.ObservedAtUtc != default));
        CollectionAssert.AreEqual(
            new[]
            {
                ExecutionEvidenceAvailabilityState.Available,
                ExecutionEvidenceAvailabilityState.Missing,
                ExecutionEvidenceAvailabilityState.Quarantined
            },
            availability.Availability.Observations.Select(static observation => observation.State).ToArray());

        AssertInvalid(
            availability with
            {
                Correction = new(
                    ExecutionEvidenceCorrectionV1.CurrentSchemaVersion,
                    Guid.NewGuid(),
                    1,
                    "evidence.late-terminal-outcome")
            },
            GraphExecutionEvidenceReasonCodes.InvalidCorrection,
            "correction");

        var execution = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        Assert.IsNull(execution.Availability);
    }

    [TestMethod]
    public void ExactlyOneBodyMemberMayBePopulated()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        AssertInvalid(
            envelope with { GraphRevision = GraphExecutionEvidenceFixtures.CreateLocalRevisionEnvelope().GraphRevision },
            GraphExecutionEvidenceReasonCodes.InvalidBody,
            "$");
        AssertInvalid(
            envelope with { Kind = ExecutionEvidenceBodyKind.GraphRevision },
            GraphExecutionEvidenceReasonCodes.InvalidBody,
            "graphRevision");
        AssertInvalid(envelope with { Execution = null }, GraphExecutionEvidenceReasonCodes.InvalidBody, "$");
    }

    [TestMethod]
    public void RedactionReplacesOperatorIdentityAndChangesTheEvidenceIdentity()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var redacted = GraphExecutionEvidenceJson.Seal(GraphExecutionEvidenceJson.Redact(
            envelope, ExecutionEvidenceRedactionPolicyV1.OperatorIdentity));

        Assert.AreEqual(
            GraphExecutionEvidenceJson.RedactionToken("capture:0001"),
            redacted.Execution!.TriggerReference);
        Assert.AreEqual(
            GraphExecutionEvidenceJson.RedactionToken("capture-loop"),
            redacted.Execution.Nodes[0].Attempts[0].LeaseOwner);
        Assert.IsTrue(redacted.Execution.TriggerReference!.StartsWith(
            GraphExecutionEvidenceJson.RedactionPrefix, StringComparison.Ordinal));
        Assert.AreNotEqual(envelope.PayloadSha256, redacted.PayloadSha256);
        Assert.IsTrue(redacted.Redaction.RedactTriggerReferences);
        Assert.IsTrue(redacted.Redaction.RedactLeaseOwners);

        var reapplied = GraphExecutionEvidenceJson.Seal(GraphExecutionEvidenceJson.Redact(
            redacted, ExecutionEvidenceRedactionPolicyV1.OperatorIdentity));
        Assert.AreEqual(redacted.PayloadSha256, reapplied.PayloadSha256);
        Assert.IsFalse(envelope.Redaction.RedactTriggerReferences);
    }

    [TestMethod]
    public void VersionNegotiationSelectsOneVersionAndPublishesTheLimits()
    {
        var response = GraphExecutionEvidenceFixtures.CreateNegotiationResponse();
        Assert.AreEqual(ExecutionEvidenceNegotiationDisposition.Supported, response.Disposition);
        Assert.AreEqual(GraphExecutionEvidenceSchemaVersions.V1, response.SelectedSchemaVersion);
        Assert.IsNull(response.ReasonCode);
        Assert.AreEqual(ExecutionEvidenceLimitsV1.Current, response.Limits);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(response).IsValid);

        var unsupported = GraphExecutionEvidenceJson.Negotiate(
            GraphExecutionEvidenceFixtures.CreateNegotiationRequest() with
            {
                SupportedSchemaVersions = ["hvo-cameraagent-execution-evidence-v2"]
            },
            GraphExecutionEvidenceFixtures.BaseUtc);
        Assert.AreEqual(ExecutionEvidenceNegotiationDisposition.Unsupported, unsupported.Disposition);
        Assert.IsNull(unsupported.SelectedSchemaVersion);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, unsupported.ReasonCode);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(unsupported).IsValid);
    }

    [TestMethod]
    public void PublishedLimitsMatchTheEnforcedConstants()
    {
        var limits = ExecutionEvidenceLimitsV1.Current;
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumEnvelopeBytes, limits.MaximumEnvelopeBytes);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumDefinitionBytes, limits.MaximumDefinitionBytes);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumFrozenPlanBytes, limits.MaximumFrozenPlanBytes);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumNodeCount, limits.MaximumNodeCount);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumAttemptsPerNode, limits.MaximumAttemptsPerNode);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumInputsPerExecution, limits.MaximumInputsPerExecution);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumOutputsPerExecution, limits.MaximumOutputsPerExecution);
        Assert.AreEqual(
            GraphExecutionEvidenceLimits.MaximumAvailabilityObservations, limits.MaximumAvailabilityObservations);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumFactsPerFeedback, limits.MaximumFactsPerFeedback);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumMissingRanges, limits.MaximumMissingRanges);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumResyncRanges, limits.MaximumResyncRanges);
        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumResyncUnits, limits.MaximumResyncUnits);
    }

    [TestMethod]
    public void CardinalityLimitsAreEnforcedPerNodeAndPerExecution()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var execution = envelope.Execution!;
        var node = execution.Nodes[0];

        AssertInvalid(
            envelope with
            {
                Execution = execution with
                {
                    Nodes = [.. Enumerable.Range(0, GraphExecutionEvidenceLimits.MaximumNodeCount + 1)
                        .Select(index => node with { NodeId = $"node-{index}" })]
                }
            },
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes");

        AssertInvalid(
            envelope with
            {
                Execution = execution with
                {
                    Nodes = [node with
                    {
                        Attempts = [.. Enumerable.Range(0, GraphExecutionEvidenceLimits.MaximumAttemptsPerNode + 1)
                            .Select(index => node.Attempts[0] with { AttemptNumber = index + 1 })]
                    }]
                }
            },
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes[0].inputs");

        AssertInvalid(
            envelope with
            {
                Execution = execution with { Nodes = [node, node] }
            },
            GraphExecutionEvidenceReasonCodes.InvalidNode,
            "execution.nodes");
    }

    [TestMethod]
    public void PayloadSizeLimitIsEnforcedOnSerializeAndParse()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLocalRevisionEnvelope();
        var revision = envelope.GraphRevision!;
        using var oversized = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = ProcessingGraphSchemaVersions.V1,
            filler = new string('x', GraphExecutionEvidenceLimits.MaximumDefinitionBytes)
        }));
        var validation = GraphExecutionEvidenceJson.Validate(envelope with
        {
            GraphRevision = revision with { CanonicalDefinition = oversized.RootElement.Clone() }
        });
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.LimitExceeded, validation.ReasonCode);
        Assert.AreEqual("graphRevision.canonicalDefinition", validation.FieldPath);

        var oversizedBytes = new byte[GraphExecutionEvidenceLimits.MaximumEnvelopeBytes + 1];
        var parsed = GraphExecutionEvidenceJson.ParseEnvelope(oversizedBytes);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.PayloadTooLarge, parsed.Validation.ReasonCode);
    }

    [TestMethod]
    public void UnknownMembersDuplicateKeysAndNumericEnumsAreRejected()
    {
        var bytes = GraphExecutionEvidenceJson.Serialize(
            GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope());
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        var unknown = text.Insert(1, "\"unknownMember\":1,");
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(unknown))
                .Validation.ReasonCode);

        var duplicate = text.Insert(1, "\"originSequence\":3,");
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(duplicate))
                .Validation.ReasonCode);

        var numericEnum = text.Replace("\"kind\":\"GraphExecution\"", "\"kind\":1", StringComparison.Ordinal);
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(numericEnum))
                .Validation.ReasonCode);

        var payloadSha256 = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope().PayloadSha256;
        var lowercaseHash = text.Replace(
            payloadSha256,
            Convert.ToHexStringLower(Convert.FromHexString(payloadSha256)),
            StringComparison.Ordinal);
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidHash,
            GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(lowercaseHash))
                .Validation.ReasonCode);
    }

    [TestMethod]
    public void NonUtcAndZeroSequenceEnvelopesAreRejected()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        AssertInvalid(
            envelope with { OriginSequence = 0 },
            GraphExecutionEvidenceReasonCodes.InvalidSequence,
            "originSequence");
        AssertInvalid(
            envelope with { ProducedAtUtc = new DateTimeOffset(2026, 8, 31, 1, 0, 0, TimeSpan.FromHours(-7)) },
            GraphExecutionEvidenceReasonCodes.InvalidTime,
            "producedAtUtc");
        AssertInvalid(
            envelope with { EvidenceId = Guid.Empty },
            GraphExecutionEvidenceReasonCodes.InvalidIdentity,
            "evidenceId");
    }

    [TestMethod]
    public void RejectedFactsRequireAReasonAndConflictRequiresTheStoredHash()
    {
        var feedback = GraphExecutionEvidenceFixtures.CreateFeedback();
        var conflict = feedback.Facts[^1];
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(feedback).IsValid);

        AssertInvalid(
            feedback with { Facts = [conflict with { ReasonCode = null }] },
            GraphExecutionEvidenceReasonCodes.InvalidFact,
            "facts[0].reasonCode");
        AssertInvalid(
            feedback with { Facts = [conflict with { StoredPayloadSha256 = null }] },
            GraphExecutionEvidenceReasonCodes.InvalidFact,
            "facts[0].storedPayloadSha256");
        AssertInvalid(
            feedback with { Facts = [conflict with { Duplicate = true }] },
            GraphExecutionEvidenceReasonCodes.InvalidFact,
            "facts[0].reasonCode");
        AssertInvalid(
            feedback with
            {
                Facts = [feedback.Facts[0] with { ReasonCode = GraphExecutionEvidenceReasonCodes.SequenceGap }]
            },
            GraphExecutionEvidenceReasonCodes.InvalidFact,
            "facts[0].reasonCode");
    }

    [TestMethod]
    public void MissingRangesMustBeOrderedAboveTheContiguousPrefix()
    {
        var feedback = GraphExecutionEvidenceFixtures.CreateFeedback();
        AssertInvalid(
            feedback with
            {
                MissingRanges = [new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 3, 4)]
            },
            GraphExecutionEvidenceReasonCodes.InvalidRange,
            "missingRanges[0]");
        AssertInvalid(
            feedback with
            {
                MissingRanges =
                [
                    new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 6, 8),
                    new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 4, 4)
                ]
            },
            GraphExecutionEvidenceReasonCodes.InvalidRange,
            "missingRanges[1]");
        AssertInvalid(
            feedback with
            {
                MissingRanges = [new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 6, 5)]
            },
            GraphExecutionEvidenceReasonCodes.InvalidRange,
            "missingRanges[0]");
    }

    private static ExecutionEvidenceEnvelopeV1 Rebind(
        ExecutionEvidenceEnvelopeV1 envelope,
        ExecutionEvidenceArtifactReferenceV1 artifact)
    {
        var node = envelope.Execution!.Nodes[0];
        return envelope with
        {
            Execution = envelope.Execution with
            {
                Nodes = [node with
                {
                    Outputs = [node.Outputs[0] with { Artifact = artifact }]
                }, .. envelope.Execution.Nodes.Skip(1)]
            }
        };
    }

    private static ImmutableArray<ExecutionEvidenceEnvelopeV1> AllEnvelopes() =>
    [
        GraphExecutionEvidenceFixtures.CreateLocalRevisionEnvelope(),
        GraphExecutionEvidenceFixtures.CreateAssignedRevisionEnvelope(),
        GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope(),
        GraphExecutionEvidenceFixtures.CreateReplayExecutionEnvelope(),
        GraphExecutionEvidenceFixtures.CreateAvailabilityEnvelope(),
        GraphExecutionEvidenceFixtures.CreateCorrectionEnvelope()
    ];

    private static void AssertInvalid(
        ExecutionEvidenceEnvelopeV1 envelope,
        string expectedReasonCode,
        string expectedFieldPath)
    {
        var validation = GraphExecutionEvidenceJson.Validate(envelope);
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(expectedReasonCode, validation.ReasonCode);
        Assert.AreEqual(expectedFieldPath, validation.FieldPath);
    }

    private static void AssertInvalid(
        ExecutionEvidenceFeedbackV1 feedback,
        string expectedReasonCode,
        string expectedFieldPath)
    {
        var validation = GraphExecutionEvidenceJson.Validate(feedback);
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(expectedReasonCode, validation.ReasonCode);
        Assert.AreEqual(expectedFieldPath, validation.FieldPath);
    }

    private static async Task AssertEnvelopeGoldenAsync(string fileName, ExecutionEvidenceEnvelopeV1 envelope)
        => await AssertGoldenAsync(
            fileName,
            GraphExecutionEvidenceJson.Serialize(envelope),
            static bytes => GraphExecutionEvidenceJson.ParseEnvelope(bytes).Value,
            GraphExecutionEvidenceJson.Serialize).ConfigureAwait(false);

    private static async Task AssertGoldenAsync<T>(
        string fileName,
        byte[] actual,
        Func<ReadOnlyMemory<byte>, T?> parse,
        Func<T, byte[]> serialize)
        where T : class
    {
        var expected = await ReadFixtureAsync(fileName).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, actual, fileName);
        var parsed = parse(expected);
        Assert.IsNotNull(parsed, fileName);
        CollectionAssert.AreEqual(expected, serialize(parsed), fileName);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(expected)),
            Convert.ToHexString(SHA256.HashData(actual)),
            fileName);
    }

    private static async Task<byte[]> ReadFixtureAsync(string fileName)
    {
        var fixture = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName)).ConfigureAwait(false);
        Assert.AreEqual((byte)'\n', fixture[^1], fileName);
        return fixture[..^1];
    }
}
