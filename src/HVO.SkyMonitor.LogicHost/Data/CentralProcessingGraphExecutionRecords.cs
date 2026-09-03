using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum CentralProcessingGraphExecutionClass
{
    Live,
    Replay
}

internal enum CentralProcessingGraphExecutionStatus
{
    Pending,
    Running,
    Completed,
    CompletedWithOptionalFailures,
    Failed,
    CancelRequested,
    Canceled,
    Superseded
}

internal enum CentralProcessingGraphTrigger
{
    Ingest,
    Replay,
    Reprocess
}

internal sealed class CentralProcessingGraphExecution
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public CentralProcessingGraphExecutionClass ExecutionClass { get; set; }
    public CentralProcessingGraphExecutionStatus Status { get; set; }
    public string RequestIdentitySha256 { get; set; } = string.Empty;
    public Guid RevisionId { get; set; }
    public CentralProcessingGraphRevision? Revision { get; set; }
    public Guid? AssignmentId { get; set; }
    public CentralProcessingGraphAssignment? Assignment { get; set; }
    public string DefinitionIdentitySha256 { get; set; } = string.Empty;
    public string FrozenDefinitionJson { get; set; } = string.Empty;
    public string CentralPlanIdentitySha256 { get; set; } = string.Empty;
    public string FrozenCentralPlanJson { get; set; } = string.Empty;
    public int ExpectedSourceCount { get; set; }
    public int ExpectedNodeCount { get; set; }
    public int ExpectedDependencyCount { get; set; }
    public int ExpectedOutputCount { get; set; }
    public DateTimeOffset? ExpandedAtUtc { get; set; }
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public Guid LogicalCameraId { get; set; }
    public LogicalCamera? LogicalCamera { get; set; }
    public Guid LogicalCameraInstallationId { get; set; }
    public LogicalCameraInstallation? LogicalCameraInstallation { get; set; }
    public Guid InstallationPublicId { get; set; }
    public Guid AnchorSourceCentralArtifactId { get; set; }
    public CentralArtifact? AnchorSourceArtifact { get; set; }
    public Guid AnchorSourceArtifactId { get; set; }
    public string AnchorSourceChecksumSha256 { get; set; } = string.Empty;
    public CentralProcessingGraphTrigger Trigger { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public Guid? PredecessorExecutionId { get; set; }
    public CentralProcessingGraphExecution? PredecessorExecution { get; set; }
    public CentralProcessingGraphExecution? SuccessorExecution { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<CentralProcessingGraphExecutionSource> Sources { get; } = [];
    public ICollection<CentralDerivativeJob> Jobs { get; } = [];
    public ICollection<CentralDerivativeJobDependency> Dependencies { get; } = [];
}

internal sealed class CentralProcessingGraphExecutionSource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ExecutionId { get; set; }
    public CentralProcessingGraphExecution? Execution { get; set; }
    public int Ordinal { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public int OutputOrdinal { get; set; }
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public Guid ArtifactId { get; set; }
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
    public long ArtifactByteLength { get; set; }
    public string SelectionEvidenceJson { get; set; } = string.Empty;
    public string SelectionEvidenceSha256 { get; set; } = string.Empty;
    public DateTimeOffset SelectedAtUtc { get; set; }
    public ICollection<CentralDerivativeJobDependency> Dependents { get; } = [];
}

internal sealed class CentralDerivativeJobDependency
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ExecutionId { get; set; }
    public CentralProcessingGraphExecution? Execution { get; set; }
    public Guid ConsumerJobId { get; set; }
    public CentralDerivativeJob? ConsumerJob { get; set; }
    public int Ordinal { get; set; }
    public ProcessingGraphDependencyKind Kind { get; set; }
    public bool Required { get; set; }
    public Guid? ProducerJobId { get; set; }
    public CentralDerivativeJob? ProducerJob { get; set; }
    public Guid? ProducerSourceId { get; set; }
    public CentralProcessingGraphExecutionSource? ProducerSource { get; set; }
    public int? ProducerOutputOrdinal { get; set; }
    public CentralDerivativeJobOutput? ProducerOutput { get; set; }
    public int? ConsumerInputOrdinal { get; set; }
    public string? ConsumerBindingName { get; set; }
    public ProcessingGraphInputBindingKind? ConsumerBindingKind { get; set; }
    public ICollection<CentralDerivativeJobInputRequirement> InputRequirements { get; } = [];
}

internal sealed class CentralDerivativeJobOutput
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralDerivativeJobId { get; set; }
    public CentralDerivativeJob? Job { get; set; }
    public int Ordinal { get; set; }
    public FrameArtifactRole Role { get; set; }
    public string Variant { get; set; } = string.Empty;
    public ProcessingProductKind ProductKind { get; set; }
    public string ContractJson { get; set; } = string.Empty;
    public string ContractIdentitySha256 { get; set; } = string.Empty;
    public Guid? ResultCentralArtifactId { get; set; }
    public CentralArtifact? ResultArtifact { get; set; }
    public string? ResultOutputIdentitySha256 { get; set; }
    public DateTimeOffset? BoundAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<CentralDerivativeJobDependency> Dependents { get; } = [];

    internal static CentralDerivativeJobOutput CreateFromFrozenPlan(
        CentralDerivativeJob job,
        int ordinal,
        ProcessingGraphProductContract contract)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(contract);
        var contractJson = CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(contract)).GetRawText();
        return new()
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = ordinal,
            Role = contract.Role,
            Variant = contract.Variant,
            ProductKind = contract.ProductKind,
            ContractJson = contractJson,
            ContractIdentitySha256 = ComputeContractIdentitySha256(contractJson)
        };
    }

    internal static string ComputeContractIdentitySha256(string contractJson)
    {
        ArgumentNullException.ThrowIfNull(contractJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contractJson)));
    }
}
