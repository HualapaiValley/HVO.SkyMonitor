using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    /// credentials. Only a member named <c>FieldPath</c> is exempt: it is a validation field path, not payload.
    /// The published byte limits are in scope, so a future limit named after a payload would fail this audit.
    /// </summary>
    private static readonly string[] ForbiddenMemberNameFragments =
    [
        "payloadbytes", "contentbytes", "imagebytes", "base64", "relativepath", "filepath", "directory",
        "filename", "sidecar", "secret", "token", "credential", "password", "connectionstring",
        "uri", "url", "endpoint"
    ];

    /// <summary>
    /// Pinned SHA-256 of each committed golden fixture's canonical bytes (the trailing newline excluded), so a
    /// fixture cannot be edited to match a changed serializer without the pin being updated deliberately.
    /// </summary>
    private static readonly Dictionary<string, string> FixtureSha256 = new(StringComparer.Ordinal)
    {
        ["cameraagent-execution-evidence-availability-v1.json"] =
            "62D55903E2FE18E6EA2E5FB348ED27F723EDD28A4E6AE44BA3DDC19FD7F743E5",
        ["cameraagent-execution-evidence-correction-v1.json"] =
            "E3FED8A57AF03030D8D17C1ADD1E8A5B81FA4B6B34A256AECA880B357AAA6D52",
        ["cameraagent-execution-evidence-execution-live-v1.json"] =
            "92E1B35BA8AA450EF9451EB7BB5FE8F2AC296E42362F6C1936A3372289786E02",
        ["cameraagent-execution-evidence-execution-replay-v1.json"] =
            "255C2E8D88CAB347D5839B897AABBFF2937FC06921DC9B91D36FC3D5B49621AB",
        ["cameraagent-execution-evidence-feedback-v1.json"] =
            "14CCDEF08F8C428868DD71D70201CFBAE8A893FAA2A22C34903DBFB341DE2CF6",
        ["cameraagent-execution-evidence-negotiation-v1.json"] =
            "C0ADBC980221DB62B2CC81E222C679E9709850AE042B01A81F55C5136161AAF2",
        ["cameraagent-execution-evidence-resync-v1.json"] =
            "00FA57D39B498D9388104CCC625C12A4A1D87121F658D6FFE176BB53041A2EFE",
        ["cameraagent-execution-evidence-revision-assigned-v1.json"] =
            "284C40C903D201DB6C25983151262A1D539D67C2989C43B57B8ECA025E083183",
        ["cameraagent-execution-evidence-revision-local-v1.json"] =
            "16A1E6FD9CA525703E4023D8D1C17D4A4A25AE0979B56FA621AD477B1421D1E9",
        ["cameraagent-execution-evidence-unknown-future-v1.json"] =
            "2271EE06AE4B3E1341F6345A1ECFC089D021A76230724E1F459EC165B8E523CF"
    };

    private static readonly int[] ExpectedOptionalInputOrdinals = [0, 1];
    private static readonly int[] ExpectedOptionalWindowPositions = [0, -1];
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
    public void CanonicalPayloadHashExcludesItselfAndChangesWithEveryProbedMember()
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
        // Every exported evidence type, with the count pinned so a newly exported type must be added to this
        // audit deliberately rather than slipping past a name filter.
        var evidenceTypes = typeof(ExecutionEvidenceEnvelopeV1).Assembly.ExportedTypes
            .Where(static type => type.Namespace == typeof(ExecutionEvidenceEnvelopeV1).Namespace &&
                (type.Name.StartsWith("ExecutionEvidence", StringComparison.Ordinal) ||
                    type.Name.StartsWith("GraphExecutionEvidence", StringComparison.Ordinal) ||
                    type.Name.StartsWith("GraphRevisionEvidence", StringComparison.Ordinal) ||
                    type.Name.StartsWith("ArtifactAvailability", StringComparison.Ordinal)))
            .ToArray();
        Assert.AreEqual(
            ExpectedEvidenceTypeCount,
            evidenceTypes.Length,
            string.Join(", ", evidenceTypes.Select(static type => type.Name).Order(StringComparer.Ordinal)));
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
    public void EveryGoldenFixtureIsPinnedAndPresent()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var present = Directory
            .EnumerateFiles(directory, "cameraagent-execution-evidence-*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            FixtureSha256.Keys.Order(StringComparer.Ordinal).ToArray(),
            present,
            "Every committed evidence fixture must have a pinned hash and vice versa.");
    }

    [TestMethod]
    public void PublishedLimitsAreAcceptedAtTheBoundaryAndRejectedOneOver()
    {
        var limits = ExecutionEvidenceLimitsV1.Current;
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var execution = envelope.Execution!;
        var node = execution.Nodes[0];

        AssertInvalid(
            WithNodes(envelope, Repeat(limits.MaximumNodeCount + 1, index =>
                node with { NodeId = $"node-{index}", Inputs = [], Outputs = [] })),
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes");
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, Repeat(
            limits.MaximumNodeCount,
            index => node with { NodeId = $"node-{index}", Inputs = [], Outputs = [] }))).IsValid);

        AssertInvalid(
            WithNodes(envelope, [node with
            {
                Attempts = Repeat(
                    limits.MaximumAttemptsPerNode + 1,
                    index => node.Attempts[0] with { AttemptNumber = index + 1 })
            }]),
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes[0].attempts");

        AssertInvalid(
            WithNodes(envelope, [node with
            {
                Inputs = Repeat(limits.MaximumInputsPerNode + 1, CreateRawInput)
            }]),
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes[0].inputs");

        AssertInvalid(
            WithNodes(envelope, [node with
            {
                Outputs = Repeat(limits.MaximumOutputsPerNode + 1, CreateOutput)
            }]),
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes[0].outputs");

        var fullInputNode = node with
        {
            Inputs = Repeat(limits.MaximumInputsPerNode, CreateRawInput),
            Outputs = []
        };
        var nodesOverExecutionInputs = (limits.MaximumInputsPerExecution / limits.MaximumInputsPerNode) + 1;
        AssertInvalid(
            WithNodes(envelope, Repeat(
                nodesOverExecutionInputs, index => fullInputNode with { NodeId = $"input-node-{index}" })),
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes");

        var fullOutputNode = node with { Inputs = [], Outputs = Repeat(limits.MaximumOutputsPerNode, CreateOutput) };
        var nodesOverExecutionOutputs = (limits.MaximumOutputsPerExecution / limits.MaximumOutputsPerNode) + 1;
        AssertInvalid(
            WithNodes(envelope, Repeat(
                nodesOverExecutionOutputs,
                index => fullOutputNode with
                {
                    NodeId = $"output-node-{index}",
                    Outputs = Repeat(
                        limits.MaximumOutputsPerNode,
                        ordinal => CreateOutput((index * limits.MaximumOutputsPerNode) + ordinal, ordinal))
                })),
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "execution.nodes");

        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, [node with
        {
            Attempts = Repeat(
                limits.MaximumAttemptsPerNode, index => node.Attempts[0] with { AttemptNumber = index + 1 })
        }])).IsValid);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, [node with
        {
            Inputs = Repeat(limits.MaximumInputsPerNode, CreateRawInput)
        }])).IsValid);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, [node with
        {
            Outputs = Repeat(limits.MaximumOutputsPerNode, CreateOutput)
        }])).IsValid);

        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, [node with
        {
            Attempts = Repeat(
                limits.MaximumAttemptsPerNode, index => node.Attempts[0] with { AttemptNumber = index + 1 })
        }])).IsValid);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, [node with
        {
            Inputs = Repeat(limits.MaximumInputsPerNode, CreateRawInput)
        }])).IsValid);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(WithNodes(envelope, [node with
        {
            Outputs = Repeat(limits.MaximumOutputsPerNode, CreateOutput)
        }])).IsValid);

        var availability = GraphExecutionEvidenceFixtures.CreateAvailabilityEnvelope();
        AssertInvalid(
            availability with
            {
                Availability = availability.Availability! with
                {
                    Observations = Repeat(
                        limits.MaximumAvailabilityObservations + 1,
                        index => availability.Availability.Observations[0] with
                        {
                            Artifact = CreateArtifact(index)
                        })
                }
            },
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "availability.observations");

        var feedback = GraphExecutionEvidenceFixtures.CreateFeedback();
        AssertInvalid(
            feedback with
            {
                Facts = Repeat(limits.MaximumFactsPerFeedback + 1, _ => feedback.Facts[0])
            },
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "facts");
        AssertInvalid(
            feedback with
            {
                MissingRanges = Repeat(
                    limits.MaximumMissingRanges + 1,
                    index => new ExecutionEvidenceSequenceRangeV1(
                        ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 4 + (index * 2), 4 + (index * 2)))
            },
            GraphExecutionEvidenceReasonCodes.LimitExceeded,
            "missingRanges");

        var resync = GraphExecutionEvidenceFixtures.CreateResyncRequest();
        var overRanges = resync with
        {
            Ranges = Repeat(
                limits.MaximumResyncRanges + 1,
                index => new ExecutionEvidenceSequenceRangeV1(
                    ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 4 + (index * 2), 4 + (index * 2)))
        };
        var overRangesValidation = GraphExecutionEvidenceJson.Validate(overRanges);
        Assert.IsFalse(overRangesValidation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.LimitExceeded, overRangesValidation.ReasonCode);
        Assert.AreEqual("ranges", overRangesValidation.FieldPath);

        var overUnits = resync with
        {
            Ranges =
            [
                new(
                    ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion,
                    4,
                    4 + limits.MaximumResyncUnits)
            ]
        };
        var overUnitsValidation = GraphExecutionEvidenceJson.Validate(overUnits);
        Assert.IsFalse(overUnitsValidation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.LimitExceeded, overUnitsValidation.ReasonCode);
        Assert.AreEqual("ranges", overUnitsValidation.FieldPath);

        Assert.AreEqual(GraphExecutionEvidenceLimits.MaximumEnvelopeBytes, limits.MaximumEnvelopeBytes);
        Assert.AreEqual(
            GraphExecutionEvidenceLimits.MaximumExecutionEnvelopeBytes, limits.MaximumExecutionEnvelopeBytes);
        Assert.AreEqual(
            GraphExecutionEvidenceLimits.MaximumAvailabilityEnvelopeBytes,
            limits.MaximumAvailabilityEnvelopeBytes);
        Assert.AreEqual(ProcessingGraphJson.MaximumDocumentBytes, limits.MaximumDefinitionBytes);
        Assert.AreEqual(ProcessingGraphJson.MaximumDocumentBytes, limits.MaximumFrozenPlanBytes);
        Assert.IsTrue(
            limits.MaximumEnvelopeBytes > limits.MaximumDefinitionBytes + limits.MaximumFrozenPlanBytes,
            "A maximum-size revision must fit inside one envelope.");
    }

    [TestMethod]
    public void DuplicateNodeIdentifiersAreRejected()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        AssertInvalid(
            WithNodes(envelope, [envelope.Execution!.Nodes[0], envelope.Execution.Nodes[0]]),
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
    public void PerKindEnvelopeCapsBindBeforeTheAbsoluteCap()
    {
        // A node id is bounded at 128 characters, so an execution is inflated with distinct nodes until its
        // canonical form exceeds the execution cap while staying far below the absolute cap.
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var node = envelope.Execution!.Nodes[0];
        var wide = WithNodes(envelope, Repeat(
            GraphExecutionEvidenceLimits.MaximumNodeCount,
            index => node with
            {
                NodeId = string.Create(
                    CultureInfo.InvariantCulture, $"{new string('n', 120)}{index:D4}"),
                Inputs = Repeat(
                    GraphExecutionEvidenceLimits.MaximumInputsPerExecution /
                        GraphExecutionEvidenceLimits.MaximumNodeCount,
                    CreateRawInput),
                Outputs = []
            }));
        var serialized = GraphExecutionEvidenceJson.Serialize(
            GraphExecutionEvidenceJson.Seal(wide with
            {
                Kind = ExecutionEvidenceBodyKind.GraphExecution,
                PayloadSha256 = GraphExecutionEvidenceJson.UnhashedPayloadSha256
            }));
        Assert.IsTrue(
            serialized.Length < GraphExecutionEvidenceLimits.MaximumEnvelopeBytes,
            serialized.Length.ToString(CultureInfo.InvariantCulture));
        Assert.IsTrue(
            serialized.Length < GraphExecutionEvidenceLimits.MaximumExecutionEnvelopeBytes,
            serialized.Length.ToString(CultureInfo.InvariantCulture));

        // The three caps are strictly ordered, so only a revision may approach the absolute cap.
        var published = ExecutionEvidenceLimitsV1.Current;
        Assert.IsTrue(
            published.MaximumAvailabilityEnvelopeBytes < published.MaximumExecutionEnvelopeBytes,
            $"{published.MaximumAvailabilityEnvelopeBytes} < {published.MaximumExecutionEnvelopeBytes}");
        Assert.IsTrue(
            published.MaximumExecutionEnvelopeBytes < published.MaximumEnvelopeBytes,
            $"{published.MaximumExecutionEnvelopeBytes} < {published.MaximumEnvelopeBytes}");

        // A payload longer than the kind's cap is refused before deserialization, with the pre-parse gate still
        // set to the absolute cap so a maximum-size revision remains receivable.
        var oversizedExecution = new byte[GraphExecutionEvidenceLimits.MaximumExecutionEnvelopeBytes + 1];
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(
            "{\"schemaVersion\":\"",
            ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion,
            "\",\"kind\":\"GraphExecution\",\"filler\":\"",
            new string('x', oversizedExecution.Length),
            "\"}"));
        var parsed = GraphExecutionEvidenceJson.ParseEnvelope(text);
        Assert.IsNull(parsed.Value);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.PayloadTooLarge, parsed.Validation.ReasonCode);
        Assert.AreEqual("$", parsed.Validation.FieldPath);
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
    public void EnumNamesAreCaseSensitiveOnEveryMessageIncludingTheUnhashedOnes()
    {
        var envelope = System.Text.Encoding.UTF8.GetString(GraphExecutionEvidenceJson.Serialize(
            GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope()));
        var lowered = envelope.Replace(
            "\"kind\":\"GraphExecution\"", "\"kind\":\"graphexecution\"", StringComparison.Ordinal);
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(lowered))
                .Validation.ReasonCode);

        // Feedback carries no payload hash, so only a strict converter can reject a lowercase enum here.
        var feedback = System.Text.Encoding.UTF8.GetString(GraphExecutionEvidenceJson.Serialize(
            GraphExecutionEvidenceFixtures.CreateFeedback()));
        var loweredFeedback = feedback.Replace(
            "\"kind\":\"Received\"", "\"kind\":\"received\"", StringComparison.Ordinal);
        Assert.AreNotEqual(feedback, loweredFeedback);
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseFeedback(System.Text.Encoding.UTF8.GetBytes(loweredFeedback))
                .Validation.ReasonCode);

        var negotiation = System.Text.Encoding.UTF8.GetString(GraphExecutionEvidenceJson.Serialize(
            GraphExecutionEvidenceFixtures.CreateNegotiationResponse()));
        var loweredNegotiation = negotiation.Replace(
            "\"disposition\":\"Supported\"", "\"disposition\":\"supported\"", StringComparison.Ordinal);
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseNegotiationResponse(
                System.Text.Encoding.UTF8.GetBytes(loweredNegotiation)).Validation.ReasonCode);

        // The unattributed shared enums are strict too: role and outcome travel in the same messages.
        var loweredRole = envelope.Replace("\"role\":\"Preview\"", "\"role\":\"preview\"", StringComparison.Ordinal);
        Assert.AreNotEqual(envelope, loweredRole);
        Assert.AreEqual(
            GraphExecutionEvidenceReasonCodes.InvalidJson,
            GraphExecutionEvidenceJson.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(loweredRole))
                .Validation.ReasonCode);
    }

    [TestMethod]
    public void NullArrayElementsFailBoundedInsteadOfThrowing()
    {
        var envelope = System.Text.Encoding.UTF8.GetString(GraphExecutionEvidenceJson.Serialize(
            GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope()));
        var start = envelope.IndexOf("\"nodes\":[", StringComparison.Ordinal);
        Assert.IsTrue(start > 0);
        var withNullNode = envelope.Insert(start + "\"nodes\":[".Length, "null,");
        var parsed = GraphExecutionEvidenceJson.ParseEnvelope(
            System.Text.Encoding.UTF8.GetBytes(withNullNode));
        Assert.IsNull(parsed.Value);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidNode, parsed.Validation.ReasonCode);
        Assert.AreEqual("execution.nodes", parsed.Validation.FieldPath);
    }

    [TestMethod]
    public void NegotiationAcceptsAPeerThatPublishesDifferentLimitsForTheSameVersion()
    {
        var response = GraphExecutionEvidenceFixtures.CreateNegotiationResponse();
        var peer = response with
        {
            Limits = response.Limits with { MaximumEnvelopeBytes = response.Limits.MaximumEnvelopeBytes / 2 }
        };
        Assert.IsTrue(
            GraphExecutionEvidenceJson.Validate(peer).IsValid,
            "Two builds of one schema version must negotiate even when a limit differs.");

        var invalid = response with { Limits = response.Limits with { MaximumNodeCount = 0 } };
        var validation = GraphExecutionEvidenceJson.Validate(invalid);
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidNegotiation, validation.ReasonCode);
        Assert.AreEqual("limits", validation.FieldPath);
    }

    [TestMethod]
    public void OptionalArtifactReconciliationMetadataIsCarriedAndBounded()
    {
        var replay = GraphExecutionEvidenceFixtures.CreateReplayExecutionEnvelope();
        var artifact = replay.Execution!.Nodes[0].Inputs[0].Artifact;
        Assert.AreEqual(25_233_408L, artifact.PayloadLength);
        Assert.AreEqual("application/octet-stream", artifact.MediaType);

        AssertInvalid(
            WithNodes(replay, [replay.Execution.Nodes[0] with
            {
                Inputs = [replay.Execution.Nodes[0].Inputs[0] with
                {
                    Artifact = artifact with
                    {
                        MediaType = new string('m', GraphExecutionEvidenceLimits.MaximumMediaTypeLength + 1)
                    }
                }]
            }]),
            GraphExecutionEvidenceReasonCodes.InvalidOutput,
            "execution.nodes[0].inputs[0].artifact.mediaType");
        AssertInvalid(
            WithNodes(replay, [replay.Execution.Nodes[0] with
            {
                Inputs = [replay.Execution.Nodes[0].Inputs[0] with
                {
                    Artifact = artifact with { PayloadLength = -1 }
                }]
            }]),
            GraphExecutionEvidenceReasonCodes.InvalidOutput,
            "execution.nodes[0].inputs[0].artifact.payloadLength");
    }

    [TestMethod]
    public void OptionalNodeTerminalFailureDoesNotFailTheExecution()
    {
        var envelope = GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope();
        var optional = envelope.Execution!.Nodes[1];
        Assert.IsFalse(optional.Required);
        Assert.AreEqual(ExecutionEvidenceNodeStatus.TerminalFailure, optional.Status);
        Assert.AreEqual(ExecutionEvidenceExecutionStatus.Completed, envelope.Execution.Status);
        Assert.IsNull(envelope.Execution.FailureReasonCode);
        Assert.AreNotEqual(envelope.Execution.Nodes[0].PlanSha256, optional.PlanSha256);
        CollectionAssert.AreEqual(
            ExpectedOptionalInputOrdinals, optional.Inputs.Select(static input => input.Ordinal).ToArray());
        CollectionAssert.AreEqual(
            ExpectedOptionalWindowPositions,
            optional.Inputs.Select(static input => input.WindowPosition).ToArray());
        CollectionAssert.AreEqual(
            ExpectedOptionalInputOrdinals,
            envelope.Execution.Nodes[0].Outputs.Select(static output => output.Ordinal).ToArray());
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

    private const int ExpectedEvidenceTypeCount = 38;

    private static ExecutionEvidenceEnvelopeV1 WithNodes(
        ExecutionEvidenceEnvelopeV1 envelope,
        ImmutableArray<ExecutionEvidenceNodeV1> nodes)
        => envelope with { Execution = envelope.Execution! with { Nodes = nodes } };

    private static ImmutableArray<T> Repeat<T>(int count, Func<int, T> create)
        => [.. Enumerable.Range(0, count).Select(create)];

    // Built once: the limit probes create thousands of inputs and outputs, and re-sealing the fixture envelope
    // for each one would dominate this project's unit run.
    private static readonly ExecutionEvidenceInputV1 RawInputTemplate =
        GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope().Execution!.Nodes[0].Inputs[0];

    private static readonly Guid ProbeCaptureId =
        GraphExecutionEvidenceFixtures.CreateLiveExecutionEnvelope().Execution!.CaptureId;

    private static ExecutionEvidenceInputV1 CreateRawInput(int ordinal)
        => RawInputTemplate with { Ordinal = ordinal };

    private static ExecutionEvidenceOutputV1 CreateOutput(int index)
        => CreateOutput(index, index);

    private static ExecutionEvidenceOutputV1 CreateOutput(int index, int ordinal)
        => new(ExecutionEvidenceOutputV1.CurrentSchemaVersion, ordinal, CreateArtifact(index));

    private static ExecutionEvidenceArtifactReferenceV1 CreateArtifact(int index)
    {
        var identity = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"issue-536-limit-probe-{index}")));
        return new(
            ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
            ProcessingIdentity.CreateArtifactId(identity),
            ProbeCaptureId,
            identity,
            FrameArtifactRole.Preview,
            "probe");
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
        var content = fixture[..^1];
        Assert.IsTrue(FixtureSha256.TryGetValue(fileName, out var pinned), fileName);
        Assert.AreEqual(pinned, Convert.ToHexString(SHA256.HashData(content)), fileName);
        return content;
    }
}
