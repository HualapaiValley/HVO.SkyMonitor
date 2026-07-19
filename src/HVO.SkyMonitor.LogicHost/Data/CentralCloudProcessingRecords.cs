namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralClearReferenceDesignation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RegistrationId { get; set; }
    public DeviceRegistration? Registration { get; set; }
    public string RigId { get; set; } = string.Empty;
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}

internal sealed class CentralDerivativeJobCanonicalInput
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralDerivativeJobId { get; set; }
    public CentralDerivativeJob? Job { get; set; }
    public Guid CentralDerivativeJobInputRequirementId { get; set; }
    public CentralDerivativeJobInputRequirement? Requirement { get; set; }
    public int Ordinal { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string IdentitySha256 { get; set; } = string.Empty;
    public string CanonicalJson { get; set; } = string.Empty;
    public int ByteLength { get; set; }
    public Guid? EnvironmentalObservationRecordId { get; set; }
    public EnvironmentalObservationRecord? EnvironmentalObservation { get; set; }
    public DateTimeOffset SelectedAtUtc { get; set; }
}
