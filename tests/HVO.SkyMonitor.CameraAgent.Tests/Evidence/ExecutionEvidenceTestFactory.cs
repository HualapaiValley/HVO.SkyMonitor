using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>
/// Deterministic durable-record and envelope factories shared by the execution-evidence export tests. Every value is
/// fixed so a sealed envelope's canonical bytes and payload hash are reproducible across runs.
/// </summary>
internal static class ExecutionEvidenceTestFactory
{
    internal static readonly DateTimeOffset BaseUtc = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);

    internal const string RevisionId =
        "70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F";
    internal const string SharedPlanIdentity =
        "C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F708192930011";
    internal const string LocalPlanIdentity =
        "6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E";
    internal const string PlanSha256 =
        "B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D5E6F7081929300";
    internal const string PreviewOutputIdentity =
        "1A2B3C4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF";
    internal const string RawDescriptorSha256 =
        "4D5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C";
    internal const string RawPayloadSha256 =
        "5E6F70819293A4B5C6D7E8F9001122334455667788990AABBCCDDEEF1A2B3C4D";

    internal static ExecutionEvidenceOriginV1 CreateOrigin(Guid? bootSessionId = null)
        => GraphExecutionEvidenceJson.BindIdentity(new(
            ExecutionEvidenceOriginV1.CurrentSchemaVersion,
            new("11111111-1111-4111-8111-111111111111"),
            new("22222222-2222-4222-8222-222222222222"),
            bootSessionId ?? new Guid("33333333-3333-4333-8333-333333333333"),
            "1.0.0-export-test",
            GraphExecutionEvidenceJson.UnhashedPayloadSha256));

    internal static Guid CaptureId(int ordinal)
        => new($"99999999-9999-4999-8999-{ordinal:D12}");

    internal static Guid ExecutionId(int ordinal)
        => new($"77777777-7777-4777-8777-{ordinal:D12}");

    internal static Guid RawArtifactId(int ordinal)
        => new($"aaaaaaaa-aaaa-4aaa-8aaa-{ordinal:D12}");

    internal static string OutputIdentity(int ordinal)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"execution-evidence-output-{ordinal}")));

    /// <summary>One terminal execution with a required node that produced one output after one retried attempt.</summary>
    internal static ProcessingGraphExecutionDetail CreateDetail(
        int ordinal,
        ProcessingGraphExecutionStatus status = ProcessingGraphExecutionStatus.Completed,
        string availabilityState = "Available")
    {
        var started = BaseUtc.AddSeconds(ordinal);
        var outputIdentity = OutputIdentity(ordinal);
        return new(
            new(
                ExecutionId(ordinal),
                ProcessingGraphExecutionClass.Live,
                status,
                CaptureId(ordinal),
                RawArtifactId(ordinal),
                RevisionId,
                DefinitionIdentity,
                SharedPlanIdentity,
                LocalPlanIdentity,
                "capture",
                $"capture:{ordinal:D4}",
                0,
                started,
                started.AddSeconds(1),
                started.AddMinutes(5),
                started.AddHours(1),
                started.AddSeconds(1),
                started.AddSeconds(3),
                null,
                false,
                1),
            [
                new(
                    "preview",
                    true,
                    PlanSha256,
                    "Completed",
                    null,
                    1,
                    started.AddSeconds(1),
                    started.AddSeconds(3),
                    [
                        new(
                            0, 0, ProcessingGraphExecutionInputKind.RawCapture, CaptureId(ordinal),
                            RawArtifactId(ordinal), RawDescriptorSha256, RawPayloadSha256, null)
                    ],
                    [
                        new(
                            1, "capture-loop", started.AddSeconds(1), started.AddSeconds(3),
                            "Completed", ProcessingOutcomeStatus.Produced, null, TimeSpan.FromSeconds(2),
                            ProcessingNodeExecutionRoute.InProcess)
                    ],
                    [
                        new(
                            0, outputIdentity, ProcessingIdentity.CreateArtifactId(outputIdentity),
                            FrameArtifactRole.Preview, "encoded-preview", availabilityState,
                            availabilityState == "Available" ? null : "reconciliation.checksum-mismatch",
                            true)
                    ])
            ]);
    }

    internal static string DefinitionIdentity { get; } = ComputeDefinitionIdentity();

    internal static ProcessingGraphRevisionSnapshot CreateSnapshot()
    {
        var definition = CanonicalDefinition();
        var frozenPlan = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "cameraagent-processing-frozen-plan-v1",
            revisionId = RevisionId,
            definitionIdentitySha256 = DefinitionIdentity,
            sharedPlanIdentitySha256 = SharedPlanIdentity,
            localPlanIdentitySha256 = LocalPlanIdentity,
            nodes = Array.Empty<object>()
        }));
        return new(
            new(
                RevisionId,
                "configured-basic",
                "r1",
                ProcessingGraphRevisionLifecycle.Active,
                DefinitionIdentity,
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

    /// <summary>Seals one execution envelope for the supplied origin sequence, exactly as the exporter does.</summary>
    internal static ExecutionEvidenceSealedUnit SealExecution(
        ExecutionEvidenceOriginV1 origin,
        long sequence,
        int ordinal)
        => Seal(ProcessingGraphEvidenceProjection.CreateEnvelope(
            origin,
            sequence,
            Guid.NewGuid(),
            BaseUtc,
            ProcessingGraphEvidenceProjection.CreateExecutionEvidence(CreateDetail(ordinal)).Value!,
            ExecutionEvidenceRedactionPolicyV1.OperatorIdentity));

    internal static ExecutionEvidenceEnlistmentUnit ExecutionUnit(
        ExecutionEvidenceOriginV1 origin,
        int ordinal)
        => new(
            ExecutionEvidenceBodyKind.GraphExecution,
            $"execution:{ExecutionId(ordinal):N}",
            sequence => SealExecution(origin, sequence, ordinal));

    internal static ExecutionEvidenceEnlistmentUnit RevisionUnit(ExecutionEvidenceOriginV1 origin)
        => new(
            ExecutionEvidenceBodyKind.GraphRevision,
            $"revision:{RevisionId}",
            sequence => Seal(ProcessingGraphEvidenceProjection.CreateEnvelope(
                origin,
                sequence,
                Guid.NewGuid(),
                BaseUtc,
                ProcessingGraphEvidenceProjection.CreateRevisionEvidence(CreateSnapshot(), assignment: null).Value!,
                ExecutionEvidenceRedactionPolicyV1.None)));

    private static ExecutionEvidenceSealedUnit Seal(ExecutionEvidenceEnvelopeV1 envelope)
        => new(envelope.EvidenceId, GraphExecutionEvidenceJson.Serialize(envelope), envelope.PayloadSha256);

    /// <summary>
    /// A detail with more nodes than the contract permits. Sealing it throws, which is how an unexportable durable
    /// row is simulated without corrupting a store.
    /// </summary>
    internal static ProcessingGraphExecutionDetail CreateOversizedDetail(int ordinal)
    {
        var baseline = CreateDetail(ordinal);
        var node = baseline.Nodes[0];
        var nodes = Enumerable
            .Range(0, GraphExecutionEvidenceLimits.MaximumNodeCount + 1)
            .Select(index => node with { NodeId = $"preview-{index:D3}" })
            .ToArray();
        return new(baseline.Execution, nodes);
    }

    /// <summary>
    /// A detail whose sealed execution envelope comfortably exceeds a few kilobytes, without breaching any contract
    /// cardinality limit, so the configured byte bound can be exercised on a legally expressible unit.
    /// </summary>
    internal static ProcessingGraphExecutionDetail CreateLargeDetail(int ordinal, int outputCount = 64)
    {
        var baseline = CreateDetail(ordinal);
        var node = baseline.Nodes[0];
        var outputs = Enumerable
            .Range(0, outputCount)
            .Select(index =>
            {
                var identity = OutputIdentity((ordinal * 1000) + index);
                return node.Outputs[0] with
                {
                    Ordinal = index,
                    OutputIdentitySha256 = identity,
                    ArtifactId = ProcessingIdentity.CreateArtifactId(identity)
                };
            })
            .ToArray();
        return new(baseline.Execution, [node with { Outputs = outputs }]);
    }

    internal static ExecutionEvidenceEnlistmentLimits UnboundedLimits { get; } =
        new(long.MaxValue, long.MaxValue, long.MaxValue, GraphExecutionEvidenceLimits.MaximumEnvelopeBytes);

    private static byte[] CanonicalDefinition()
        => ProcessingGraphJson.SerializeCanonical(new(
            ProcessingGraphSchemaVersions.V1,
            "configured-basic",
            "r1",
            [new("$raw", [new(FrameArtifactRole.Raw, "raw", ProcessingProductKind.PixelData)])],
            []));

    private static string ComputeDefinitionIdentity()
    {
        using var document = JsonDocument.Parse(CanonicalDefinition());
        return CaptureContractJson.ComputeCanonicalJsonSha256(document.RootElement);
    }
}

/// <summary>A mutable clock so retry, retention, and age behaviour is exercised without wall-clock waits.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
}

/// <summary>A temporary storage root removed when the test completes.</summary>
internal sealed class TemporaryRoot : IDisposable
{
    internal TemporaryRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-issue-537-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
