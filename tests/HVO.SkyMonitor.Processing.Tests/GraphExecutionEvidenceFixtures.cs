using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// Deterministic builders for the canonical graph-execution evidence golden fixtures. Every identifier, hash, and
/// timestamp is fixed, so the produced canonical bytes are stable across runs, machines, and cultures.
/// </summary>
internal static class GraphExecutionEvidenceFixtures
{
    internal const string LocalRevisionFixture = "cameraagent-execution-evidence-revision-local-v1.json";
    internal const string AssignedRevisionFixture = "cameraagent-execution-evidence-revision-assigned-v1.json";
    internal const string LiveExecutionFixture = "cameraagent-execution-evidence-execution-live-v1.json";
    internal const string ReplayExecutionFixture = "cameraagent-execution-evidence-execution-replay-v1.json";
    internal const string CorrectionFixture = "cameraagent-execution-evidence-correction-v1.json";
    internal const string AvailabilityFixture = "cameraagent-execution-evidence-availability-v1.json";
    internal const string FeedbackFixture = "cameraagent-execution-evidence-feedback-v1.json";
    internal const string ResyncFixture = "cameraagent-execution-evidence-resync-v1.json";
    internal const string NegotiationFixture = "cameraagent-execution-evidence-negotiation-v1.json";
    internal const string UnknownFutureFixture = "cameraagent-execution-evidence-unknown-future-v1.json";

    internal static readonly DateTimeOffset BaseUtc = new(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);

    private static readonly Guid OriginInstallationId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid AgentInstanceId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid BootSessionId = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid ObservatoryId = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid LogicalCameraInstallationId = new("55555555-5555-4555-8555-555555555555");
    private static readonly Guid InstallationPublicId = new("66666666-6666-4666-8666-666666666666");
    private static readonly Guid LiveExecutionId = new("77777777-7777-4777-8777-777777777777");
    private static readonly Guid ReplayExecutionId = new("88888888-8888-4888-8888-888888888888");
    private static readonly Guid CaptureId = new("99999999-9999-4999-8999-999999999999");
    private static readonly Guid RawArtifactId = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid LiveEvidenceId = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid ReplayEvidenceId = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid LocalRevisionEvidenceId = new("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly Guid AssignedRevisionEvidenceId = new("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    private static readonly Guid AvailabilityEvidenceId = new("ffffffff-ffff-4fff-8fff-ffffffffffff");
    private static readonly Guid CorrectionEvidenceId = new("12121212-1212-4212-8212-121212121212");
    private static readonly Guid ProposalId = new("13131313-1313-4313-8313-131313131313");
    private static readonly Guid CatalogRevisionId = new("14141414-1414-4414-8414-141414141414");
    private static readonly Guid AssignmentId = new("15151515-1515-4515-8515-151515151515");
    private static readonly Guid RegistrationId = new("16161616-1616-4616-8616-161616161616");

    private const string PreviewOutputIdentity =
        "1A2B3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF";
    private const string AnnotatedOutputIdentity =
        "2B3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A";
    private const string MetadataOutputIdentity =
        "3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B";
    private const string RawDescriptorSha256 =
        "4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C";
    private const string RawPayloadSha256 =
        "5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D";
    private const string LocalPlanIdentity =
        "6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E";
    private const string LocalRevisionId =
        "70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F";
    private const string AssignedLocalPlanIdentity =
        "819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F70";
    private const string AssignedRevisionId =
        "9293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F7081";
    private const string CapabilitySnapshotSha256 =
        "A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F70819293";
    private const string NodePlanSha256 =
        "B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F7081929300";
    private const string AnnotateNodePlanSha256 =
        "D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F70819293001122";
    private const string SharedPlanIdentityLocal =
        "C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F708192930011";

    internal static ExecutionEvidenceRedactionPolicyV1 Redaction => ExecutionEvidenceRedactionPolicyV1.None;

    internal static ExecutionEvidenceOriginV1 Origin { get; } = GraphExecutionEvidenceJson.BindIdentity(new(
        ExecutionEvidenceOriginV1.CurrentSchemaVersion,
        OriginInstallationId,
        AgentInstanceId,
        BootSessionId,
        "1.0.0-fixture",
        GraphExecutionEvidenceJson.UnhashedPayloadSha256,
        ObservatoryId,
        LogicalCameraInstallationId,
        InstallationPublicId));

    /// <summary>A locally compiled revision with no central assignment.</summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateLocalRevisionEnvelope()
        => Seal(
            LocalRevisionEvidenceId,
            originSequence: 1,
            ExecutionEvidenceBodyKind.GraphRevision,
            revision: CreateRevision(
                LocalRevisionId,
                "configured-basic",
                "r1",
                LocalPlanIdentity,
                assignment: null));

    /// <summary>A revision accepted from a central assignment proposal, carrying its provenance.</summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateAssignedRevisionEnvelope()
        => Seal(
            AssignedRevisionEvidenceId,
            originSequence: 2,
            ExecutionEvidenceBodyKind.GraphRevision,
            revision: CreateRevision(
                AssignedRevisionId,
                "observatory-standard",
                "r7",
                AssignedLocalPlanIdentity,
                assignment: new(
                    ExecutionEvidenceAssignmentProvenanceV1.CurrentSchemaVersion,
                    ProposalId,
                    CatalogRevisionId,
                    AssignmentId,
                    RegistrationId,
                    LogicalCameraInstallationId,
                    InstallationPublicId,
                    CapabilitySnapshotSha256,
                    BaseUtc,
                    BaseUtc.AddSeconds(30))));

    /// <summary>
    /// A completed live execution: a required node that produced after one retryable attempt, and an optional
    /// node that skipped with a reason.
    /// </summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateLiveExecutionEnvelope()
        => Seal(
            LiveEvidenceId,
            originSequence: 3,
            ExecutionEvidenceBodyKind.GraphExecution,
            execution: new(
                GraphExecutionEvidenceV1.CurrentSchemaVersion,
                LiveExecutionId,
                ExecutionEvidenceExecutionClass.Live,
                ExecutionEvidenceExecutionStatus.Completed,
                CaptureId,
                RawArtifactId,
                LocalRevisionId,
                DefinitionIdentity,
                SharedPlanIdentityLocal,
                LocalPlanIdentity,
                "capture",
                [
                    new(
                        ExecutionEvidenceNodeV1.CurrentSchemaVersion,
                        "preview",
                        Required: true,
                        NodePlanSha256,
                        ExecutionEvidenceNodeStatus.Completed,
                        [RawInput(ordinal: 0)],
                        [
                            new(
                                ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                                1,
                                ExecutionEvidenceAttemptStatus.RetryableFailure,
                                BaseUtc.AddSeconds(1),
                                BaseUtc.AddSeconds(2),
                                ProcessingOutcomeStatus.RetryableFailure,
                                "processing.transient-io",
                                TimeSpan.FromSeconds(1).Ticks,
                                "capture-loop"),
                            new(
                                ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                                2,
                                ExecutionEvidenceAttemptStatus.Completed,
                                BaseUtc.AddSeconds(3),
                                BaseUtc.AddSeconds(4),
                                ProcessingOutcomeStatus.Produced,
                                null,
                                TimeSpan.FromSeconds(1).Ticks,
                                "capture-loop")
                        ],
                        [
                            Output(0, PreviewOutputIdentity, FrameArtifactRole.Preview, "encoded-preview"),
                            Output(1, AnnotatedOutputIdentity, FrameArtifactRole.AnnotatedPreview, "annotated")
                        ],
                        null,
                        BaseUtc.AddSeconds(1),
                        BaseUtc.AddSeconds(4)),
                    new(
                        ExecutionEvidenceNodeV1.CurrentSchemaVersion,
                        "annotate",
                        Required: false,
                        AnnotateNodePlanSha256,
                        ExecutionEvidenceNodeStatus.TerminalFailure,
                        [
                            ProcessingOutputInput(0, 0, PreviewOutputIdentity),
                            ProcessingOutputInput(1, -1, AnnotatedOutputIdentity)
                        ],
                        [
                            new(
                                ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                                1,
                                ExecutionEvidenceAttemptStatus.TerminalFailure,
                                BaseUtc.AddSeconds(5),
                                BaseUtc.AddSeconds(5),
                                ProcessingOutcomeStatus.TerminalFailure,
                                "processing.optional-node-failed",
                                0,
                                "capture-loop")
                        ],
                        [],
                        "processing.optional-node-failed",
                        BaseUtc.AddSeconds(5),
                        BaseUtc.AddSeconds(5))
                ],
                BaseUtc,
                BaseUtc.AddSeconds(2),
                BaseUtc.AddMinutes(5),
                BaseUtc.AddHours(1),
                CancellationRequested: false,
                AttemptCount: 2,
                "capture:0001",
                Priority: 0,
                BaseUtc.AddSeconds(1),
                BaseUtc.AddSeconds(5),
                null));

    /// <summary>A cancelled replay execution whose required node ended in a terminal failure.</summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateReplayExecutionEnvelope()
        => Seal(
            ReplayEvidenceId,
            originSequence: 4,
            ExecutionEvidenceBodyKind.GraphExecution,
            execution: new(
                GraphExecutionEvidenceV1.CurrentSchemaVersion,
                ReplayExecutionId,
                ExecutionEvidenceExecutionClass.Replay,
                ExecutionEvidenceExecutionStatus.Cancelled,
                CaptureId,
                RawArtifactId,
                AssignedRevisionId,
                DefinitionIdentity,
                SharedPlanIdentityLocal,
                AssignedLocalPlanIdentity,
                "operator",
                [
                    new(
                        ExecutionEvidenceNodeV1.CurrentSchemaVersion,
                        "preview",
                        Required: true,
                        NodePlanSha256,
                        ExecutionEvidenceNodeStatus.TerminalFailure,
                        [DescribedRawInput(ordinal: 0)],
                        [
                            new(
                                ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                                1,
                                ExecutionEvidenceAttemptStatus.Interrupted,
                                BaseUtc.AddSeconds(10),
                                BaseUtc.AddSeconds(11),
                                null,
                                "processing.lease-expired",
                                TimeSpan.FromSeconds(1).Ticks,
                                "replay-runner"),
                            new(
                                ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                                2,
                                ExecutionEvidenceAttemptStatus.TerminalFailure,
                                BaseUtc.AddSeconds(12),
                                BaseUtc.AddSeconds(13),
                                ProcessingOutcomeStatus.TerminalFailure,
                                "processing.cancelled",
                                TimeSpan.FromSeconds(1).Ticks,
                                "replay-runner")
                        ],
                        [],
                        "processing.cancelled",
                        BaseUtc.AddSeconds(10),
                        BaseUtc.AddSeconds(13)),
                    new(
                        ExecutionEvidenceNodeV1.CurrentSchemaVersion,
                        "annotate",
                        Required: false,
                        AnnotateNodePlanSha256,
                        ExecutionEvidenceNodeStatus.Skipped,
                        [],
                        [
                            new(
                                ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                                1,
                                ExecutionEvidenceAttemptStatus.Skipped,
                                BaseUtc.AddSeconds(13),
                                BaseUtc.AddSeconds(13),
                                ProcessingOutcomeStatus.Skipped,
                                "processing.required-producer-failed",
                                0,
                                "replay-runner")
                        ],
                        [],
                        "processing.required-producer-failed",
                        BaseUtc.AddSeconds(13),
                        BaseUtc.AddSeconds(13))
                ],
                BaseUtc.AddSeconds(9),
                BaseUtc.AddSeconds(11),
                BaseUtc.AddMinutes(15),
                BaseUtc.AddHours(2),
                CancellationRequested: true,
                AttemptCount: 2,
                "operator:replay-0002",
                Priority: -10,
                BaseUtc.AddSeconds(10),
                BaseUtc.AddSeconds(13),
                "processing.cancelled"));

    /// <summary>
    /// An append-only correction of the live execution: the predecessor is retained, the successor carries a
    /// strictly greater origin sequence and an explicit reason.
    /// </summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateCorrectionEnvelope()
    {
        var live = CreateLiveExecutionEnvelope();
        var corrected = live.Execution! with
        {
            Status = ExecutionEvidenceExecutionStatus.Failed,
            FailureReasonCode = "processing.late-terminal-failure"
        };
        return GraphExecutionEvidenceJson.Seal(live with
        {
            EvidenceId = CorrectionEvidenceId,
            OriginSequence = 6,
            ProducedAtUtc = BaseUtc.AddMinutes(10),
            Execution = corrected,
            Correction = new(
                ExecutionEvidenceCorrectionV1.CurrentSchemaVersion,
                LiveEvidenceId,
                3,
                "evidence.late-terminal-outcome"),
            PayloadSha256 = GraphExecutionEvidenceJson.UnhashedPayloadSha256
        });
    }

    /// <summary>Availability observations covering an available, a missing, and a quarantined artifact.</summary>
    internal static ExecutionEvidenceEnvelopeV1 CreateAvailabilityEnvelope()
        => Seal(
            AvailabilityEvidenceId,
            originSequence: 5,
            ExecutionEvidenceBodyKind.ArtifactAvailability,
            availability: new(
                ArtifactAvailabilityReportV1.CurrentSchemaVersion,
                LiveExecutionId,
                [
                    new(
                        ArtifactAvailabilityObservationV1.CurrentSchemaVersion,
                        Artifact(PreviewOutputIdentity, FrameArtifactRole.Preview, "encoded-preview"),
                        ExecutionEvidenceAvailabilityState.Available,
                        BaseUtc.AddMinutes(1),
                        null),
                    new(
                        ArtifactAvailabilityObservationV1.CurrentSchemaVersion,
                        Artifact(AnnotatedOutputIdentity, FrameArtifactRole.AnnotatedPreview, "annotated"),
                        ExecutionEvidenceAvailabilityState.Missing,
                        BaseUtc.AddMinutes(1),
                        "retention.deleted"),
                    new(
                        ArtifactAvailabilityObservationV1.CurrentSchemaVersion,
                        Artifact(MetadataOutputIdentity, FrameArtifactRole.Metadata, "facts"),
                        ExecutionEvidenceAvailabilityState.Quarantined,
                        BaseUtc.AddMinutes(1),
                        "reconciliation.checksum-mismatch")
                ]));

    /// <summary>Receiver feedback carrying every fact kind, a detected gap, and acknowledgement retention.</summary>
    internal static ExecutionEvidenceFeedbackV1 CreateFeedback()
    {
        var live = CreateLiveExecutionEnvelope();
        var replay = CreateReplayExecutionEnvelope();
        return new(
            ExecutionEvidenceFeedbackV1.CurrentSchemaVersion,
            Origin.IdentitySha256,
            BaseUtc.AddMinutes(2),
            ContiguousThroughSequence: 3,
            [
                new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 4, 4),
                new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 6, 8)
            ],
            [
                Fact(ExecutionEvidenceFactKind.Received, live, duplicate: false),
                Fact(ExecutionEvidenceFactKind.Validated, live, duplicate: false),
                Fact(ExecutionEvidenceFactKind.Accepted, live, duplicate: false),
                Fact(ExecutionEvidenceFactKind.Acknowledged, live, duplicate: false),
                Fact(ExecutionEvidenceFactKind.Received, live, duplicate: true),
                new(
                    ExecutionEvidenceFactV1.CurrentSchemaVersion,
                    ExecutionEvidenceFactKind.Rejected,
                    replay.EvidenceId,
                    replay.OriginSequence,
                    replay.PayloadSha256,
                    BaseUtc.AddMinutes(2),
                    Duplicate: false,
                    GraphExecutionEvidenceReasonCodes.SequenceConflict,
                    "originSequence",
                    live.PayloadSha256)
            ],
            new(
                ExecutionEvidenceRetentionV1.CurrentSchemaVersion,
                AcknowledgedThroughSequence: 3,
                BaseUtc.AddDays(30),
                MaximumRetainedAcknowledgements: 10_000),
            MissingRangesTruncated: false);

        ExecutionEvidenceFactV1 Fact(
            ExecutionEvidenceFactKind kind,
            ExecutionEvidenceEnvelopeV1 envelope,
            bool duplicate)
            => new(
                ExecutionEvidenceFactV1.CurrentSchemaVersion,
                kind,
                envelope.EvidenceId,
                envelope.OriginSequence,
                envelope.PayloadSha256,
                BaseUtc.AddMinutes(2),
                duplicate);
    }

    /// <summary>The bounded resynchronization request derived from <see cref="CreateFeedback"/>.</summary>
    internal static ExecutionEvidenceResyncRequestV1 CreateResyncRequest()
        => GraphExecutionEvidenceJson.CreateResyncRequest(CreateFeedback())
            ?? throw new InvalidOperationException("The feedback fixture must report a gap.");

    /// <summary>The negotiation response for a producer that also offers an unknown future version.</summary>
    internal static ExecutionEvidenceNegotiationResponseV1 CreateNegotiationResponse()
        => GraphExecutionEvidenceJson.Negotiate(CreateNegotiationRequest(), BaseUtc.AddMinutes(3));

    internal static ExecutionEvidenceNegotiationRequestV1 CreateNegotiationRequest()
        => new(
            ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion,
            Origin,
            ["hvo-cameraagent-execution-evidence-v2", GraphExecutionEvidenceSchemaVersions.V1]);

    /// <summary>
    /// A payload declaring an unknown future schema version. It exists so both hosts can prove that a
    /// forward-incompatible unit is rejected whole rather than partially interpreted.
    /// </summary>
    internal static byte[] CreateUnknownFuturePayload()
    {
        var current = JsonSerializer.SerializeToElement(
            JsonDocument.Parse(GraphExecutionEvidenceJson.Serialize(CreateLiveExecutionEnvelope())).RootElement);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
                {
                    writer.WriteString("schemaVersion", "hvo-cameraagent-execution-evidence-v2");
                    continue;
                }
                property.WriteTo(writer);
            }
            writer.WriteString("futureOnlyMember", "unknown");
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    internal static string DefinitionIdentity { get; } =
        ProcessingGraphJson.ComputeDefinitionIdentity(CreateDefinition());

    internal static ProcessingGraphDefinition CreateDefinition()
        => new(
            ProcessingGraphSchemaVersions.V1,
            "configured-basic",
            "r1",
            [
                new("$raw", [
                    new(FrameArtifactRole.Raw, "raw", ProcessingProductKind.PixelData)
                ])
            ],
            [
                new(
                    "preview",
                    "Preview",
                    "1.0.0",
                    ProcessingOperationKind.Transform,
                    enabled: true,
                    ProcessingGraphNodeFailurePolicy.Required,
                    order: 1,
                    JsonSerializer.SerializeToElement(new { quality = 85 }),
                    [new("$raw")],
                    [
                        new(
                            [FrameArtifactRole.Raw],
                            [ProcessingProductKind.PixelData],
                            [],
                            [],
                            [])
                    ],
                    [new(FrameArtifactRole.Preview, "encoded-preview", ProcessingProductKind.PixelData)],
                    null,
                    [],
                    [ProcessingGraphHosts.CameraAgent])
            ]);

    private static GraphRevisionEvidenceV1 CreateRevision(
        string revisionId,
        string name,
        string revision,
        string localPlanIdentity,
        ExecutionEvidenceAssignmentProvenanceV1? assignment)
    {
        var definition = CreateDefinition();
        using var definitionDocument = JsonDocument.Parse(ProcessingGraphJson.SerializeCanonical(definition));
        // The coordinator persists the frozen plan through CaptureContractJson (web defaults, camelCase); using
        // the default serializer here would pin PascalCase keys no receiver would ever see.
        var frozenPlan = CaptureContractJson.SerializeToElement(new
        {
            SchemaVersion = "cameraagent-processing-frozen-plan-v1",
            RevisionId = revisionId,
            DefinitionIdentitySha256 = DefinitionIdentity,
            SharedPlanIdentitySha256 = SharedPlanIdentityLocal,
            LocalPlanIdentitySha256 = localPlanIdentity,
            // Mirrors the persisted cameraagent-processing-frozen-plan-v1 node seed exactly, so a regression that
            // drops one of the eight members changes these bytes.
            Nodes = new[]
            {
                new
                {
                    NodeId = "preview",
                    Required = true,
                    PlanSha256 = NodePlanSha256,
                    SharedPlanNodeIdentitySha256 = NodePlanSha256,
                    DependenciesJson = """[{"producerId":"$raw","kind":"Artifact","required":true}]""",
                    InputsJson = """[{"roles":["Raw"],"required":true,"bindingName":"input"}]""",
                    OutputsJson = """[{"role":"Preview","variant":"encoded-preview","productKind":"PixelData"}]""",
                    WindowJson = (string?)null
                },
                new
                {
                    NodeId = "annotate",
                    Required = false,
                    PlanSha256 = AnnotateNodePlanSha256,
                    SharedPlanNodeIdentitySha256 = AnnotateNodePlanSha256,
                    DependenciesJson = """[{"producerId":"preview","kind":"Artifact","required":false}]""",
                    InputsJson = """[{"roles":["Preview"],"required":false,"bindingName":"input"}]""",
                    OutputsJson = """[]""",
                    WindowJson = (string?)"""{"kind":"Trailing","minimumInputCount":1,"maximumInputCount":2}"""
                }
            }
        });
        return new(
            GraphRevisionEvidenceV1.CurrentSchemaVersion,
            revisionId,
            name,
            revision,
            assignment is null
                ? ExecutionEvidenceRevisionOrigin.LocalOnly
                : ExecutionEvidenceRevisionOrigin.CentrallyAssigned,
            DefinitionIdentity,
            SharedPlanIdentityLocal,
            localPlanIdentity,
            definitionDocument.RootElement.Clone(),
            frozenPlan,
            BaseUtc,
            BaseUtc.AddSeconds(1),
            BaseUtc.AddSeconds(2),
            null,
            assignment);
    }

    private static ExecutionEvidenceInputV1 RawInput(int ordinal)
        => new(
            ExecutionEvidenceInputV1.CurrentSchemaVersion,
            ordinal,
            WindowPosition: 0,
            ExecutionEvidenceInputKind.RawCapture,
            new(
                ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
                RawArtifactId,
                CaptureId,
                null,
                FrameArtifactRole.Raw,
                null,
                RawPayloadSha256,
                null,
                null,
                RawDescriptorSha256));

    /// <summary>A raw input enriched with the optional reconciliation metadata a receiver may match on.</summary>
    private static ExecutionEvidenceInputV1 DescribedRawInput(int ordinal)
        => new(
            ExecutionEvidenceInputV1.CurrentSchemaVersion,
            ordinal,
            WindowPosition: 0,
            ExecutionEvidenceInputKind.RawCapture,
            new(
                ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
                RawArtifactId,
                CaptureId,
                null,
                FrameArtifactRole.Raw,
                "raw",
                RawPayloadSha256,
                25_233_408L,
                "application/octet-stream",
                RawDescriptorSha256));

    /// <summary>
    /// A processing-output input exactly as the CameraAgent projection produces it: the durable input row carries
    /// the producing output identity but no role or variant, so neither is invented here.
    /// </summary>
    private static ExecutionEvidenceInputV1 ProcessingOutputInput(
        int ordinal,
        int windowPosition,
        string outputIdentity)
        => new(
            ExecutionEvidenceInputV1.CurrentSchemaVersion,
            ordinal,
            windowPosition,
            ExecutionEvidenceInputKind.ProcessingOutput,
            new(
                ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
                ProcessingIdentity.CreateArtifactId(outputIdentity),
                CaptureId,
                outputIdentity));

    private static ExecutionEvidenceOutputV1 Output(
        int ordinal,
        string outputIdentity,
        FrameArtifactRole role,
        string variant)
        => new(
            ExecutionEvidenceOutputV1.CurrentSchemaVersion,
            ordinal,
            Artifact(outputIdentity, role, variant));

    private static ExecutionEvidenceArtifactReferenceV1 Artifact(
        string outputIdentity,
        FrameArtifactRole role,
        string variant)
        => new(
            ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            CaptureId,
            outputIdentity,
            role,
            variant);

    private static ExecutionEvidenceEnvelopeV1 Seal(
        Guid evidenceId,
        long originSequence,
        ExecutionEvidenceBodyKind kind,
        GraphRevisionEvidenceV1? revision = null,
        GraphExecutionEvidenceV1? execution = null,
        ArtifactAvailabilityReportV1? availability = null)
        => GraphExecutionEvidenceJson.Seal(new(
            ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion,
            evidenceId,
            Origin,
            originSequence,
            BaseUtc.AddSeconds(originSequence),
            kind,
            GraphExecutionEvidenceJson.UnhashedPayloadSha256,
            Redaction,
            revision,
            execution,
            availability));
}
