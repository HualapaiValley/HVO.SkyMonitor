namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>Stores capture-level identity and provenance shared by all artifacts in a frame.</summary>
internal sealed class CentralFrame
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid RegistrationId { get; set; }

    public Guid DevicePublicId { get; set; }

    public Guid ObservatoryId { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public Guid FrameId { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }

    public DateTimeOffset FirstReceivedAtUtc { get; set; }

    public int? RigProfileVersion { get; set; }

    public Guid? DeviceRigProfileId { get; set; }

    public DeviceRigProfile? DeviceRigProfile { get; set; }

    public string? RigId { get; set; }

    public long? CaptureSequence { get; set; }

    public string? CycleEvidenceJson { get; set; }

    public string? SceneProvenanceJson { get; set; }

    public ICollection<CentralArtifact> Artifacts { get; } = [];

    public CentralCaptureTiming? Timing { get; set; }

    public CentralCaptureControl? Control { get; set; }

    public ICollection<CentralCaptureProfile> Profiles { get; } = [];
}
