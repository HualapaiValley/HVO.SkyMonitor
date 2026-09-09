using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// Covers the read-only projection from the delivered durable graph-execution records into the transport-neutral
/// evidence contract: identity preservation, immutable/observational separation, and redaction.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProcessingGraphEvidenceProjectionTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaptureId = new("99999999-9999-4999-8999-999999999999");
    private static readonly Guid ExecutionId = new("77777777-7777-4777-8777-777777777777");
    private static readonly Guid RawArtifactId = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private const string PreviewOutputIdentity =
        "1A2B3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF";
    private const string PlanSha256 =
        "B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F7081929300";
    private const string SharedPlanIdentity =
        "C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F708192930011";
    private const string LocalPlanIdentity =
        "6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E";
    private const string RevisionId =
        "70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F";
    private const string RawDescriptorSha256 =
        "4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C";
    private const string RawPayloadSha256 =
        "5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D";

    [TestMethod]
    public void ProjectedRevisionSealsIntoAValidContentAddressedEnvelope()
    {
        var envelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            CreateOrigin(),
            1,
            Guid.NewGuid(),
            BaseUtc,
            ProcessingGraphEvidenceProjection.CreateRevisionEvidence(CreateSnapshot(), assignment: null).Value!,
            ExecutionEvidenceRedactionPolicyV1.None);

        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(envelope).IsValid);
        Assert.AreEqual(ExecutionEvidenceRevisionOrigin.LocalOnly, envelope.GraphRevision!.Origin);
        Assert.AreEqual(RevisionId, envelope.GraphRevision.RevisionId);
        Assert.AreEqual(SharedPlanIdentity, envelope.GraphRevision.SharedPlanIdentitySha256);
        Assert.AreEqual(LocalPlanIdentity, envelope.GraphRevision.LocalPlanIdentitySha256);
        Assert.AreEqual(
            envelope.GraphRevision.DefinitionIdentitySha256,
            CaptureContractJson.ComputeCanonicalJsonSha256(envelope.GraphRevision.CanonicalDefinition));
        Assert.IsNull(envelope.GraphRevision.Assignment);
        Assert.AreEqual(
            envelope.PayloadSha256, GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(envelope));
    }

    [TestMethod]
    public void ProjectedRevisionCarriesCentralAssignmentProvenanceWhenSupplied()
    {
        var assignment = new ExecutionEvidenceAssignmentProvenanceV1(
            ExecutionEvidenceAssignmentProvenanceV1.CurrentSchemaVersion,
            new("13131313-1313-4313-8313-131313131313"),
            new("14141414-1414-4414-8414-141414141414"),
            new("15151515-1515-4515-8515-151515151515"),
            new("16161616-1616-4616-8616-161616161616"),
            new("55555555-5555-4555-8555-555555555555"),
            new("66666666-6666-4666-8666-666666666666"),
            SharedPlanIdentity,
            BaseUtc,
            BaseUtc.AddSeconds(10));

        var envelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            CreateOrigin(),
            1,
            Guid.NewGuid(),
            BaseUtc,
            ProcessingGraphEvidenceProjection.CreateRevisionEvidence(CreateSnapshot(), assignment).Value!,
            ExecutionEvidenceRedactionPolicyV1.None);

        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(envelope).IsValid);
        Assert.AreEqual(ExecutionEvidenceRevisionOrigin.CentrallyAssigned, envelope.GraphRevision!.Origin);
        Assert.AreEqual(assignment, envelope.GraphRevision.Assignment);
    }

    [TestMethod]
    public void ProjectedExecutionPreservesEveryImmutableProductionFact()
    {
        var detail = CreateDetail();
        var envelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            CreateOrigin(),
            2,
            Guid.NewGuid(),
            BaseUtc,
            ProcessingGraphEvidenceProjection.CreateExecutionEvidence(detail).Value!,
            ExecutionEvidenceRedactionPolicyV1.None);

        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(envelope).IsValid);
        var execution = envelope.Execution!;
        Assert.AreEqual(ExecutionId, execution.ExecutionId);
        Assert.AreEqual(ExecutionEvidenceExecutionClass.Live, execution.ExecutionClass);
        Assert.AreEqual(ExecutionEvidenceExecutionStatus.Completed, execution.Status);
        Assert.AreEqual(2, execution.Nodes.Length);

        var required = execution.Nodes[0];
        Assert.IsTrue(required.Required);
        Assert.AreEqual(ExecutionEvidenceNodeStatus.Completed, required.Status);
        Assert.AreEqual(2, required.Attempts.Length);
        Assert.AreEqual(ProcessingOutcomeStatus.RetryableFailure, required.Attempts[0].Outcome);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, required.Attempts[1].Outcome);
        Assert.AreEqual(
            ProcessingIdentity.CreateArtifactId(PreviewOutputIdentity), required.Outputs[0].Artifact.ArtifactId);
        Assert.AreEqual(ExecutionEvidenceInputKind.RawCapture, required.Inputs[0].Kind);
        Assert.IsNull(required.Inputs[0].Artifact.OutputIdentitySha256);
        Assert.AreEqual(RawDescriptorSha256, required.Inputs[0].Artifact.DescriptorSha256);
        Assert.AreEqual(RawPayloadSha256, required.Inputs[0].Artifact.PayloadSha256);

        var optional = execution.Nodes[1];
        Assert.IsFalse(optional.Required);
        Assert.AreEqual(ExecutionEvidenceNodeStatus.Skipped, optional.Status);
        Assert.AreEqual(ExecutionEvidenceInputKind.ProcessingOutput, optional.Inputs[0].Kind);
        Assert.AreEqual(PreviewOutputIdentity, optional.Inputs[0].Artifact.OutputIdentitySha256);
        Assert.IsEmpty(optional.Outputs);
    }

    [TestMethod]
    public void EveryDurableExecutionClassMapsAndAnUnknownOneFailsLoudly()
    {
        var detail = CreateDetail();
        foreach (var durable in Enum.GetValues<ProcessingGraphExecutionClass>())
        {
            var projected = ProcessingGraphEvidenceProjection.CreateExecutionEvidence(
                new(detail.Execution with { ExecutionClass = durable }, detail.Nodes));
            Assert.AreEqual(ProcessingGraphEvidenceProjectionOutcome.Projected, projected.Outcome);
            Assert.AreEqual(
                Enum.Parse<ExecutionEvidenceExecutionClass>(durable.ToString()),
                projected.Value!.ExecutionClass,
                durable.ToString());
        }

        // A future durable member must produce a bounded rejection rather than be silently exported as Live, and
        // rather than throwing: the exporter quarantines that one unit and keeps draining every other one.
        var rejected = ProcessingGraphEvidenceProjection.CreateExecutionEvidence(
            new(detail.Execution with { ExecutionClass = (ProcessingGraphExecutionClass)99 }, detail.Nodes));
        Assert.AreEqual(ProcessingGraphEvidenceProjectionOutcome.Rejected, rejected.Outcome);
        Assert.IsNull(rejected.Value);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidBody, rejected.ReasonCode);
        Assert.AreEqual("execution.executionClass", rejected.FieldPath);
    }

    [TestMethod]
    public void ProjectedExecutionCarriesNoAvailabilityAndAvailabilityIsSequencedSeparately()
    {
        var detail = CreateDetail();
        var executionEnvelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            CreateOrigin(),
            2,
            Guid.NewGuid(),
            BaseUtc,
            ProcessingGraphEvidenceProjection.CreateExecutionEvidence(detail).Value!,
            ExecutionEvidenceRedactionPolicyV1.None);
        Assert.IsNull(executionEnvelope.Availability);

        var availabilityResult = ProcessingGraphEvidenceProjection.CreateAvailabilityReport(detail, BaseUtc.AddHours(1));
        Assert.AreEqual(ProcessingGraphEvidenceProjectionOutcome.Projected, availabilityResult.Outcome);
        var availability = availabilityResult.Value!;
        var envelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            CreateOrigin(), 3, Guid.NewGuid(), BaseUtc.AddHours(1), availability,
            ExecutionEvidenceRedactionPolicyV1.None);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(envelope).IsValid);
        Assert.AreEqual(ExecutionEvidenceBodyKind.ArtifactAvailability, envelope.Kind);
        Assert.AreEqual(1, availability.Observations.Length);
        Assert.AreEqual(ExecutionEvidenceAvailabilityState.Quarantined, availability.Observations[0].State);
        Assert.AreEqual("reconciliation.checksum-mismatch", availability.Observations[0].ReasonCode);
        Assert.AreEqual(BaseUtc.AddHours(1), availability.Observations[0].ObservedAtUtc);

        Assert.AreEqual(
            ProcessingGraphEvidenceProjectionOutcome.Empty,
            ProcessingGraphEvidenceProjection.CreateAvailabilityReport(new(detail.Execution, []), BaseUtc).Outcome);

        // The durable schema permits a reason on an Available row; the contract reserves a reason for a
        // non-available observation, so the projection drops it rather than exporting an invalid observation.
        var available = new ProcessingGraphExecutionDetail(
            detail.Execution,
            [detail.Nodes[0] with
            {
                Outputs = [detail.Nodes[0].Outputs[0] with
                {
                    AvailabilityState = "Available",
                    AvailabilityReason = "reconciliation.restored"
                }]
            }]);
        var restored = ProcessingGraphEvidenceProjection.CreateAvailabilityReport(available, BaseUtc).Value!;
        Assert.IsNotNull(restored);
        Assert.AreEqual(ExecutionEvidenceAvailabilityState.Available, restored.Observations[0].State);
        Assert.IsNull(restored.Observations[0].ReasonCode);
        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(
            ProcessingGraphEvidenceProjection.CreateEnvelope(
                CreateOrigin(), 4, Guid.NewGuid(), BaseUtc, restored,
                ExecutionEvidenceRedactionPolicyV1.None)).IsValid);
    }

    [TestMethod]
    public void ProjectedExecutionRedactsOperatorIdentityWhenThePolicyRequiresIt()
    {
        var detail = CreateDetail();
        var evidence = ProcessingGraphEvidenceProjection.CreateExecutionEvidence(detail).Value!;
        var envelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            CreateOrigin(),
            2,
            Guid.NewGuid(),
            BaseUtc,
            evidence,
            ExecutionEvidenceRedactionPolicyV1.OperatorIdentity);

        Assert.IsTrue(GraphExecutionEvidenceJson.Validate(envelope).IsValid);
        Assert.AreEqual(
            GraphExecutionEvidenceJson.RedactionToken("capture:0001"), envelope.Execution!.TriggerReference);
        Assert.AreEqual(
            GraphExecutionEvidenceJson.RedactionToken("capture-loop"),
            envelope.Execution.Nodes[0].Attempts[0].LeaseOwner);
        Assert.AreEqual("capture:0001", evidence.TriggerReference);
    }

    /// <summary>
    /// The projection is the only production code that will ever build these envelopes, so it must reproduce the
    /// committed golden bytes exactly. A field-mapping regression (for example writing <c>availableUtc</c> where
    /// <c>acceptedUtc</c> belongs) changes these bytes even though every value is individually well-formed.
    /// </summary>
    [TestMethod]
    public async Task ProjectedLiveExecutionReproducesTheCommittedGoldenBytes()
    {
        var expected = await File.ReadAllBytesAsync(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "cameraagent-execution-evidence-execution-live-v1.json"))
            .ConfigureAwait(false);
        Assert.AreEqual((byte)'\n', expected[^1]);

        var envelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            GoldenOrigin(),
            originSequence: 3,
            new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            GoldenBaseUtc.AddSeconds(3),
            ProcessingGraphEvidenceProjection.CreateExecutionEvidence(CreateGoldenDetail()).Value!,
            ExecutionEvidenceRedactionPolicyV1.None);

        CollectionAssert.AreEqual(expected[..^1], GraphExecutionEvidenceJson.Serialize(envelope));
    }

    private static readonly DateTimeOffset GoldenBaseUtc = new(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);

    private const string GoldenRevisionId =
        "70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F";
    private const string GoldenDefinitionIdentity =
        "2AC312ECFB623F71C3765E683517B23ED35B0B1176EE96CFDF7D7B0BA64B7FD9";
    private const string GoldenSharedPlanIdentity =
        "C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F708192930011";
    private const string GoldenLocalPlanIdentity =
        "6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E";
    private const string GoldenPreviewIdentity =
        "1A2B3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF";
    private const string GoldenAnnotatedIdentity =
        "2B3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A";
    private const string GoldenPreviewPlanSha256 =
        "B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F7081929300";
    private const string GoldenAnnotatePlanSha256 =
        "D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F70819293001122";

    private static ExecutionEvidenceOriginV1 GoldenOrigin()
        => GraphExecutionEvidenceJson.BindIdentity(new(
            ExecutionEvidenceOriginV1.CurrentSchemaVersion,
            new("11111111-1111-4111-8111-111111111111"),
            new("22222222-2222-4222-8222-222222222222"),
            new("33333333-3333-4333-8333-333333333333"),
            "1.0.0-fixture",
            GraphExecutionEvidenceJson.UnhashedPayloadSha256,
            new("44444444-4444-4444-8444-444444444444"),
            new("55555555-5555-4555-8555-555555555555"),
            new("66666666-6666-4666-8666-666666666666")));

    private static ProcessingGraphExecutionDetail CreateGoldenDetail()
        => new(
            new(
                new("77777777-7777-4777-8777-777777777777"),
                ProcessingGraphExecutionClass.Live,
                ProcessingGraphExecutionStatus.Completed,
                CaptureId,
                RawArtifactId,
                GoldenRevisionId,
                GoldenDefinitionIdentity,
                GoldenSharedPlanIdentity,
                GoldenLocalPlanIdentity,
                "capture",
                "capture:0001",
                0,
                GoldenBaseUtc,
                GoldenBaseUtc.AddSeconds(2),
                GoldenBaseUtc.AddMinutes(5),
                GoldenBaseUtc.AddHours(1),
                GoldenBaseUtc.AddSeconds(1),
                GoldenBaseUtc.AddSeconds(5),
                null,
                false,
                2),
            [
                new(
                    "preview",
                    true,
                    GoldenPreviewPlanSha256,
                    "Completed",
                    null,
                    2,
                    GoldenBaseUtc.AddSeconds(1),
                    GoldenBaseUtc.AddSeconds(4),
                    [
                        new(
                            0, 0, ProcessingGraphExecutionInputKind.RawCapture, CaptureId, RawArtifactId,
                            RawDescriptorSha256, RawPayloadSha256, null)
                    ],
                    [
                        new(
                            1, "capture-loop", GoldenBaseUtc.AddSeconds(1), GoldenBaseUtc.AddSeconds(2),
                            "RetryableFailure", ProcessingOutcomeStatus.RetryableFailure,
                            "processing.transient-io", TimeSpan.FromSeconds(1), ProcessingNodeExecutionRoute.InProcess),
                        new(
                            2, "capture-loop", GoldenBaseUtc.AddSeconds(3), GoldenBaseUtc.AddSeconds(4),
                            "Completed", ProcessingOutcomeStatus.Produced, null, TimeSpan.FromSeconds(1),
                            ProcessingNodeExecutionRoute.InProcess)
                    ],
                    [
                        new(
                            0, GoldenPreviewIdentity,
                            ProcessingIdentity.CreateArtifactId(GoldenPreviewIdentity),
                            FrameArtifactRole.Preview, "encoded-preview", "Available", null),
                        new(
                            1, GoldenAnnotatedIdentity,
                            ProcessingIdentity.CreateArtifactId(GoldenAnnotatedIdentity),
                            FrameArtifactRole.AnnotatedPreview, "annotated", "Available", null)
                    ]),
                new(
                    "annotate",
                    false,
                    GoldenAnnotatePlanSha256,
                    "TerminalFailure",
                    "processing.optional-node-failed",
                    1,
                    GoldenBaseUtc.AddSeconds(5),
                    GoldenBaseUtc.AddSeconds(5),
                    [
                        new(
                            0, 0, ProcessingGraphExecutionInputKind.ProcessingOutput, CaptureId,
                            ProcessingIdentity.CreateArtifactId(GoldenPreviewIdentity), null, null,
                            GoldenPreviewIdentity),
                        new(
                            1, -1, ProcessingGraphExecutionInputKind.ProcessingOutput, CaptureId,
                            ProcessingIdentity.CreateArtifactId(GoldenAnnotatedIdentity), null, null,
                            GoldenAnnotatedIdentity)
                    ],
                    [
                        new(
                            1, "capture-loop", GoldenBaseUtc.AddSeconds(5), GoldenBaseUtc.AddSeconds(5),
                            "TerminalFailure", ProcessingOutcomeStatus.TerminalFailure,
                            "processing.optional-node-failed", TimeSpan.Zero, ProcessingNodeExecutionRoute.InProcess)
                    ],
                    [])
            ]);

    private static ExecutionEvidenceOriginV1 CreateOrigin()
        => GraphExecutionEvidenceJson.BindIdentity(new(
            ExecutionEvidenceOriginV1.CurrentSchemaVersion,
            new("11111111-1111-4111-8111-111111111111"),
            new("22222222-2222-4222-8222-222222222222"),
            new("33333333-3333-4333-8333-333333333333"),
            "1.0.0-projection",
            GraphExecutionEvidenceJson.UnhashedPayloadSha256));

    private static ProcessingGraphRevisionSnapshot CreateSnapshot()
    {
        var definition = ProcessingGraphJson.SerializeCanonical(new(
            ProcessingGraphSchemaVersions.V1,
            "configured-basic",
            "r1",
            [new("$raw", [new(FrameArtifactRole.Raw, "raw", ProcessingProductKind.PixelData)])],
            []));
        var frozenPlan = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "cameraagent-processing-frozen-plan-v1",
            revisionId = RevisionId,
            sharedPlanIdentitySha256 = SharedPlanIdentity,
            localPlanIdentitySha256 = LocalPlanIdentity
        }));
        using var definitionDocument = JsonDocument.Parse(definition);
        return new(
            new(
                RevisionId,
                "configured-basic",
                "r1",
                ProcessingGraphRevisionLifecycle.Active,
                CaptureContractJson.ComputeCanonicalJsonSha256(definitionDocument.RootElement),
                SharedPlanIdentity,
                LocalPlanIdentity,
                BaseUtc,
                BaseUtc.AddSeconds(1),
                BaseUtc.AddSeconds(2),
                null),
            new([]),
            Encoding.UTF8.GetBytes("{}"),
            definition,
            frozenPlan,
            ImmutableArray<ProcessingExecutionNodeSeed>.Empty);
    }

    private static ProcessingGraphExecutionDetail CreateDetail()
        => new(
            new(
                ExecutionId,
                ProcessingGraphExecutionClass.Live,
                ProcessingGraphExecutionStatus.Completed,
                CaptureId,
                RawArtifactId,
                RevisionId,
                RevisionId,
                SharedPlanIdentity,
                LocalPlanIdentity,
                "capture",
                "capture:0001",
                0,
                BaseUtc,
                BaseUtc,
                BaseUtc.AddMinutes(5),
                BaseUtc.AddHours(1),
                BaseUtc.AddSeconds(1),
                BaseUtc.AddSeconds(5),
                null,
                false,
                2),
            [
                new(
                    "preview",
                    true,
                    PlanSha256,
                    "Completed",
                    null,
                    2,
                    BaseUtc.AddSeconds(1),
                    BaseUtc.AddSeconds(4),
                    [
                        new(
                            0,
                            0,
                            ProcessingGraphExecutionInputKind.RawCapture,
                            CaptureId,
                            RawArtifactId,
                            RawDescriptorSha256,
                            RawPayloadSha256,
                            null)
                    ],
                    [
                        new(
                            1,
                            "capture-loop",
                            BaseUtc.AddSeconds(1),
                            BaseUtc.AddSeconds(2),
                            "RetryableFailure",
                            ProcessingOutcomeStatus.RetryableFailure,
                            "processing.transient-io",
                            TimeSpan.FromSeconds(1),
                            ProcessingNodeExecutionRoute.InProcess),
                        new(
                            2,
                            "capture-loop",
                            BaseUtc.AddSeconds(3),
                            BaseUtc.AddSeconds(4),
                            "Completed",
                            ProcessingOutcomeStatus.Produced,
                            null,
                            TimeSpan.FromSeconds(1),
                            ProcessingNodeExecutionRoute.InProcess)
                    ],
                    [
                        new(
                            0,
                            PreviewOutputIdentity,
                            ProcessingIdentity.CreateArtifactId(PreviewOutputIdentity),
                            FrameArtifactRole.Preview,
                            "encoded-preview",
                            "Quarantined",
                            "reconciliation.checksum-mismatch")
                    ]),
                new(
                    "annotate",
                    false,
                    PlanSha256,
                    "Skipped",
                    "processing.optional-input-missing",
                    1,
                    BaseUtc.AddSeconds(5),
                    BaseUtc.AddSeconds(5),
                    [
                        new(
                            0,
                            0,
                            ProcessingGraphExecutionInputKind.ProcessingOutput,
                            CaptureId,
                            ProcessingIdentity.CreateArtifactId(PreviewOutputIdentity),
                            null,
                            null,
                            PreviewOutputIdentity)
                    ],
                    [
                        new(
                            1,
                            "capture-loop",
                            BaseUtc.AddSeconds(5),
                            BaseUtc.AddSeconds(5),
                            "Skipped",
                            ProcessingOutcomeStatus.Skipped,
                            "processing.optional-input-missing",
                            TimeSpan.Zero,
                            ProcessingNodeExecutionRoute.Unknown)
                    ],
                    [])
            ]);
}
