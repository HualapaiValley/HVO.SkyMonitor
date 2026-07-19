using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Manifest v2 for one reconstructable immutable artifact.</summary>
public sealed record ArtifactManifestV2(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] ReconstructionDescriptor Descriptor,
    [property: JsonRequired] string RelativeArtifactPath,
    SceneProvenance? Scene = null)
{
    public const string CurrentSchemaVersion = "v2";

    public string IdempotencyKey => CaptureContractJson.ComputeDescriptorSha256(Descriptor);

    public CaptureContractValidationResult Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return CaptureContractValidationResult.Failure(CaptureContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (!IsSafeRelativePath(RelativeArtifactPath))
        {
            return CaptureContractValidationResult.Failure(CaptureContractReasonCodes.InvalidPath, "relativeArtifactPath");
        }
        var descriptorValidation = Descriptor?.Validate() ?? CaptureContractValidationResult.Failure(
            CaptureContractReasonCodes.InvalidIdentity, "descriptor");
        if (!descriptorValidation.IsValid)
        {
            return descriptorValidation;
        }
        return Scene?.TransientScenario is not { } transient || transient.IsValid()
            ? CaptureContractValidationResult.Success
            : CaptureContractValidationResult.Failure(CaptureContractReasonCodes.InvalidIdentity, "scene.transientScenario");
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) || path.StartsWith('/') || path.StartsWith('\\') ||
            path.Length >= 2 && path[1] == ':')
        {
            return false;
        }
        return !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
    }
}

/// <summary>Parsed v1 or v2 artifact manifest without fabricated legacy facts.</summary>
public sealed record ArtifactManifestDocument(
    CaptureManifestCompleteness Completeness,
    ArtifactUploadManifest? LegacyManifest,
    ArtifactManifestV2? Manifest)
{
    public static ArtifactManifestDocument FromLegacy(ArtifactUploadManifest manifest)
        => new(CaptureManifestCompleteness.LegacyIncomplete, manifest, null);

    public static ArtifactManifestDocument FromCurrent(ArtifactManifestV2 manifest)
        => new(CaptureManifestCompleteness.Complete, null, manifest);
}

/// <summary>Manifest parse result with a stable validation failure when parsing does not succeed.</summary>
public sealed record ArtifactManifestParseResult(
    ArtifactManifestDocument? Document,
    CaptureContractValidationResult Validation)
{
    public bool IsValid => Document is not null && Validation.IsValid;
}
