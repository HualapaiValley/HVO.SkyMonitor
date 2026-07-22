namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralTransientReprocessingJob
{
    public Guid CentralDerivativeJobId { get; set; }
    public CentralDerivativeJob? Job { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid SourceEventVersionId { get; set; }
    public CentralTransientEventVersionRecord? SourceEventVersion { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CanonicalRequestJson { get; set; } = string.Empty;
    public string CanonicalRequestSha256 { get; set; } = string.Empty;
    public int CanonicalRequestByteLength { get; set; }
    public string RequestIdentitySha256 { get; set; } = string.Empty;
    public string ProducerName { get; set; } = string.Empty;
    public string ProducerVersion { get; set; } = string.Empty;
    public string RecipeIdentitySha256 { get; set; } = string.Empty;
    public string OptionsIdentitySha256 { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CommittedUtc { get; set; }
    public Guid? ResultAssessmentId { get; set; }
    public Guid? ResultEventVersionId { get; set; }
}

internal sealed class CentralTransientReprocessingRequestRecord
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid CentralDerivativeJobId { get; set; }
    public CentralTransientReprocessingJob? ReprocessingJob { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RecipeIdentitySha256 { get; set; } = string.Empty;
    public string OptionsIdentitySha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
}
