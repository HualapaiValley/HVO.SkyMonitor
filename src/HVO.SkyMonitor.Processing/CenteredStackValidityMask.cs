using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Canonical raw-grid validity materialization for a pinned pair of raw and qualified linear pixels.</summary>
public static class CenteredStackValidityMask
{
    public const string SchemaVersion = "centered-stack-validity-mask-v1";
    public const string MediaType = "application/x-hvo-centered-source-validity";

    /// <summary>
    /// Capture the supplied aperture/calibration support before closure. Use one byte per delivered sample (0 or 1).
    /// The recipe additionally applies the independently declared raw and linear white thresholds.
    /// </summary>
    public static ProcessingArtifact Create(ProcessingArtifact raw, ProcessingArtifact linear, ReadOnlyMemory<byte> validity)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(linear);
        var size = (long)(raw.Layout?.Width ?? 0) * (raw.Layout?.Height ?? 0);
        if (size is < 1 or > LinearRegisteredMean.MaximumPixels || validity.Length != size ||
            validity.Span.ContainsAnyExcept((byte)0, (byte)1) ||
            raw.Role != FrameArtifactRole.Raw || linear.Role != FrameArtifactRole.Calibrated ||
            raw.CaptureId is null || raw.CaptureId != linear.CaptureId)
        {
            throw new ArgumentException("Centered validity requires an exact raw-grid mask and matching capture.", nameof(validity));
        }

        var bytes = validity.ToArray();
        var hash = ProcessingIdentity.ComputePayloadSha256(bytes);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            SchemaVersion, "1.0.0", SchemaVersion, JsonSerializer.SerializeToElement(new { payloadSha256 = hash })));
        Guid[] lineage = [raw.ArtifactId, linear.ArtifactId];
        var output = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, SchemaVersion, recipe.IdentitySha256, lineage);
        return new(ProcessingIdentity.CreateArtifactId(output), FrameArtifactRole.Metadata, SchemaVersion, recipe.IdentitySha256,
            MediaType, null, bytes, linear.CreatedUtc, raw.Integration, linear.Compatibility, raw.CaptureSequence, lineage)
        {
            CaptureId = raw.CaptureId,
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = SchemaVersion,
            ContentIdentitySha256 = hash,
            RecipeDescriptor = recipe.Descriptor
        };
    }

    internal static bool Matches(ProcessingArtifact mask, CenteredStackArtifactPin pin) =>
        mask.Role == FrameArtifactRole.Metadata && mask.ProductKind == ProcessingProductKind.Metadata &&
        mask.Layout is null && mask.MediaType == MediaType && mask.Variant == SchemaVersion && mask.SchemaVersion == SchemaVersion &&
        mask.ContentIdentitySha256 == pin.PayloadSha256 && mask.RecipeDescriptor is { Name: SchemaVersion } recipe &&
        ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256 == mask.RecipeIdentitySha256 &&
        recipe.Options.TryGetProperty("payloadSha256", out var content) && content.GetString() == pin.PayloadSha256 &&
        mask.SourceArtifactIds is { Count: 2 } lineage &&
        mask.ArtifactId == ProcessingIdentity.CreateArtifactId(
            ProcessingIdentity.CreateOutputIdentity(mask.Role, mask.Variant, mask.RecipeIdentitySha256, lineage));
}
