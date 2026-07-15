namespace HVO.SkyMonitor.AgentCore;

/// <summary>Stable machine-readable reason codes returned by capture contract validation.</summary>
public static class CaptureContractReasonCodes
{
    public const string InvalidJson = "json.invalid";
    public const string UnsupportedSchema = "schema.unsupported";
    public const string InvalidIdentity = "identity.invalid";
    public const string InvalidCaptureSequence = "capture-sequence.invalid";
    public const string InvalidTimingOrder = "timing.invalid-order";
    public const string InvalidControls = "controls.invalid";
    public const string InvalidCadence = "cadence.invalid";
    public const string InvalidMetering = "metering.invalid";
    public const string InvalidDimensions = "layout.invalid-dimensions";
    public const string InvalidStride = "layout.invalid-stride";
    public const string UnsupportedFormat = "layout.unsupported-format";
    public const string InvalidByteOrder = "layout.invalid-byte-order";
    public const string InvalidSampleDepth = "layout.invalid-sample-depth";
    public const string InvalidPacking = "layout.invalid-packing";
    public const string InvalidCfa = "layout.invalid-cfa";
    public const string InvalidLevels = "layout.invalid-levels";
    public const string InvalidArtifactRole = "artifact.invalid-role";
    public const string InvalidArtifactVariant = "artifact.invalid-variant";
    public const string InvalidLineage = "lineage.invalid";
    public const string InvalidProfile = "profile.invalid";
    public const string InvalidRecipe = "recipe.invalid";
    public const string RecipeHashMismatch = "recipe.hash-mismatch";
    public const string InvalidChecksum = "checksum.invalid";
    public const string InvalidPath = "path.invalid";
    public const string PayloadLengthMismatch = "payload.length-mismatch";
    public const string PayloadChecksumMismatch = "payload.checksum-mismatch";
}

/// <summary>Result of deterministic capture-contract validation.</summary>
/// <param name="ReasonCode">Stable reason code, or <see langword="null"/> on success.</param>
/// <param name="FieldPath">Contract field associated with the failure, or <see langword="null"/> on success.</param>
public readonly record struct CaptureContractValidationResult(string? ReasonCode, string? FieldPath)
{
    public static CaptureContractValidationResult Success => default;

    public bool IsValid => ReasonCode is null;

    public static CaptureContractValidationResult Failure(string reasonCode, string fieldPath)
        => new(reasonCode, fieldPath);
}

/// <summary>Indicates whether a parsed manifest contains complete reconstruction facts.</summary>
public enum CaptureManifestCompleteness
{
    Complete,
    LegacyIncomplete
}
