namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralDerivativeJobInputRequirement
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CentralDerivativeJobId { get; set; }

    public CentralDerivativeJob? Job { get; set; }

    public int Ordinal { get; set; }

    public string BindingName { get; set; } = string.Empty;

    public CentralDerivativeInputSourceKind SourceKind { get; set; }

    public int? SequenceOffset { get; set; }

    public bool IsRequired { get; set; }

    public string SelectorJson { get; set; } = "{}";

    public CentralDerivativeCompatibilityMode CompatibilityMode { get; set; }

    public string ExpectedAgentId { get; set; } = string.Empty;

    public string? ExpectedRigId { get; set; }

    public long? ExpectedCaptureSequence { get; set; }

    public Guid? ExpectedCentralArtifactId { get; set; }

    public CentralArtifact? ExpectedArtifact { get; set; }

    public CentralDerivativeInputResolutionState ResolutionState { get; set; }

    public string? ResolutionReasonCode { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public CentralDerivativeJobInput? Input { get; set; }

    public CentralDerivativeJobCanonicalInput? CanonicalInput { get; set; }
}

internal sealed class CentralDerivativeJobInput
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CentralDerivativeJobId { get; set; }

    public CentralDerivativeJob? Job { get; set; }

    public Guid CentralDerivativeJobInputRequirementId { get; set; }

    public CentralDerivativeJobInputRequirement? Requirement { get; set; }

    public int Ordinal { get; set; }

    public Guid CentralArtifactId { get; set; }

    public CentralArtifact? Artifact { get; set; }

    public long? CaptureSequence { get; set; }

    public string CompatibilityJson { get; set; } = "{}";

    public string CompatibilitySha256 { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public DateTimeOffset SelectedAtUtc { get; set; }
}

internal enum CentralDerivativeInputSourceKind
{
    Artifact,
    EnvironmentalObservation
}

internal enum CentralDerivativeCompatibilityMode
{
    None,
    Exact
}

internal enum CentralDerivativeInputResolutionState
{
    Waiting,
    Resolved,
    Missing,
    Incompatible
}
