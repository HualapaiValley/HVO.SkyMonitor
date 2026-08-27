using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum CentralArtifactObjectState
{
    Pending,
    Available,
    Quarantined,
    Expired
}

internal enum CentralReconstructionState
{
    LegacyIncomplete,
    PendingReference,
    Complete,
    Quarantined
}

internal enum CentralProfileKind
{
    Rig,
    Calibration,
    Mask,
    Sensor,
    Processing
}

internal sealed class CentralCaptureTiming
{
    public Guid CentralFrameId { get; set; }
    public CentralFrame? Frame { get; set; }
    public DateTimeOffset RequestedStartUtc { get; set; }
    public DateTimeOffset ExposureStartedUtc { get; set; }
    public DateTimeOffset ExposureEndedUtc { get; set; }
    public DateTimeOffset ReadoutCompletedUtc { get; set; }
    public DateTimeOffset DurableIngressUtc { get; set; }
    public DateTimeOffset? SetpointAppliedUtc { get; set; }
}

internal sealed class CentralCaptureControl
{
    public Guid CentralFrameId { get; set; }
    public CentralFrame? Frame { get; set; }
    public long RequestedExposureTicks { get; set; }
    public long EffectiveExposureTicks { get; set; }
    public double RequestedGain { get; set; }
    public double EffectiveGain { get; set; }
    public double? RequestedOffset { get; set; }
    public double? EffectiveOffset { get; set; }
    public double? TemperatureSetpointC { get; set; }
    public double? EffectiveTemperatureC { get; set; }
}

internal sealed class CentralCaptureProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralFrameId { get; set; }
    public CentralFrame? Frame { get; set; }
    public CentralProfileKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public Guid? DeviceRigProfileId { get; set; }
    public DeviceRigProfile? DeviceRigProfile { get; set; }
}

internal sealed class CentralArtifactLayout
{
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int StrideBytes { get; set; }
    public string PixelFormat { get; set; } = string.Empty;
    public string ByteOrder { get; set; } = string.Empty;
    public int SampleDepthBits { get; set; }
    public int ContainerDepthBits { get; set; }
    public string Packing { get; set; } = string.Empty;
    public string CfaPattern { get; set; } = string.Empty;
    public double? BlackLevel { get; set; }
    public double? WhiteLevel { get; set; }
    public string? StoredCodeTransform { get; set; }
    public string? LevelCodeSpace { get; set; }
    public int? NativeWidth { get; set; }
    public int? NativeHeight { get; set; }
    public int? RoiX { get; set; }
    public int? RoiY { get; set; }
    public int? RoiWidth { get; set; }
    public int? RoiHeight { get; set; }
    public int? BinX { get; set; }
    public int? BinY { get; set; }
    public string? BinningAlgorithm { get; set; }
    public int? CfaOriginX { get; set; }
    public int? CfaOriginY { get; set; }
    public long ByteLength { get; set; }
}

internal sealed class CentralArtifactRecipe
{
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SemanticVersion { get; set; } = string.Empty;
    public string ImplementationVersion { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = string.Empty;
    public string OptionsSha256 { get; set; } = string.Empty;
}

internal sealed class CentralStructuredProcessingProduct
{
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public string OutputIdentitySha256 { get; set; } = string.Empty;
    public string ProductKind { get; set; } = string.Empty;
    public string ProductSchemaVersion { get; set; } = string.Empty;
    public string ContentIdentitySha256 { get; set; } = string.Empty;
    public string AlgorithmsJson { get; set; } = string.Empty;
    public string CompatibilityJson { get; set; } = string.Empty;
    public string DescriptorJson { get; set; } = string.Empty;
    public long TotalIntegrationTicks { get; set; }

    public string? SourceIdentitySha256 { get; set; }
    public int? PresentationWidthPixels { get; set; }
    public int? PresentationHeightPixels { get; set; }
}

internal sealed class CentralArtifactSource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public int Ordinal { get; set; }
    public Guid SourceArtifactId { get; set; }
    public FrameArtifactRole? ExpectedRole { get; set; }
    public string? ExpectedVariant { get; set; }
    public string? ExpectedRecipeIdentitySha256 { get; set; }
    public string? ExpectedProductIdentitySha256 { get; set; }
    public string? ExpectedMediaType { get; set; }
    public int? ExpectedWidthPixels { get; set; }
    public int? ExpectedHeightPixels { get; set; }
    public string? ExpectedLayoutIdentitySha256 { get; set; }
    public string? ExpectedCoordinateIdentitySha256 { get; set; }
    public Guid? ResolvedCentralArtifactId { get; set; }
    public CentralArtifact? ResolvedArtifact { get; set; }
}

internal sealed class CentralArtifactIngestIdentity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public string ManifestSchemaVersion { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}
