using System.Security.Cryptography;
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
        var source = CenteredStackArtifactHeader.FromArtifact(raw);
        var qualified = CenteredStackArtifactHeader.FromArtifact(linear);
        if (validity.Length != SampleCount(source, qualified) || validity.Span.ContainsAnyExcept((byte)0, (byte)1))
        {
            throw new ArgumentException("Centered validity requires an exact raw-grid mask and matching capture.", nameof(validity));
        }

        var bytes = validity.ToArray();
        return Header(source, qualified, ProcessingIdentity.ComputePayloadSha256(bytes)).WithPayload(bytes, linear.CreatedUtc);
    }

    /// <summary>
    /// Pins the implicit complete calibrated lattice without opening files or building a full-size mask in closure.
    /// Use only when the qualified producer establishes that no additional source-domain exclusions are required.
    /// </summary>
    public static CenteredStackArtifactPin PinAllValid(CenteredStackArtifactHeader raw, CenteredStackArtifactHeader linear,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = SampleCount(raw, linear);
        Span<byte> block = stackalloc byte[4096];
        block.Fill(1);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(remaining, block.Length);
            hasher.AppendData(block[..count]);
            remaining -= count;
        }
        var hash = Convert.ToHexString(hasher.GetHashAndReset());
        return CenteredStackArtifactPin.FromHeader(Header(raw, linear, hash), hash);
    }

    /// <summary>Materializes the implicit mask after the corresponding raw and linear custody has been authenticated.</summary>
    public static ProcessingArtifact CreateAllValid(CenteredStackArtifactHeader raw, CenteredStackArtifactHeader linear,
        DateTimeOffset createdUtc)
    {
        var bytes = new byte[SampleCount(raw, linear)];
        bytes.AsSpan().Fill(1);
        return Header(raw, linear, ProcessingIdentity.ComputePayloadSha256(bytes)).WithPayload(bytes, createdUtc);
    }

    /// <summary>Restores an implicit mask and checks its original canonical pin from the closed window.</summary>
    public static ProcessingArtifact OpenAllValid(CenteredStackArtifactHeader raw, CenteredStackArtifactHeader linear,
        CenteredStackArtifactPin expected, DateTimeOffset createdUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (PinAllValid(raw, linear, cancellationToken) != expected)
        {
            throw new InvalidDataException("The reopened validity mask differs from the frozen registration input.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[SampleCount(raw, linear)];
        bytes.AsSpan().Fill(1);
        return Header(raw, linear, expected.PayloadSha256).WithPayload(bytes, createdUtc);
    }

    private static int SampleCount(CenteredStackArtifactHeader raw, CenteredStackArtifactHeader linear)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(linear);
        if (raw.Layout is not { } rawLayout || linear.Layout is not { } linearLayout ||
            raw.Role != FrameArtifactRole.Raw || linear.Role != FrameArtifactRole.Calibrated ||
            raw.CaptureId is null || raw.CaptureId == Guid.Empty || raw.CaptureId != linear.CaptureId ||
            raw.ArtifactId == Guid.Empty || linear.ArtifactId == Guid.Empty || raw.ArtifactId == linear.ArtifactId ||
            linear.SourceArtifactIds is not { Count: > 0 } lineage || lineage[0] != raw.ArtifactId ||
            !rawLayout.Validate().IsValid || !linearLayout.Validate().IsValid ||
            rawLayout.Width != linearLayout.Width || rawLayout.Height != linearLayout.Height ||
            (long)rawLayout.Width * rawLayout.Height is < 1 or > LinearRegisteredMean.MaximumPixels)
        {
            throw new ArgumentException("Centered validity requires matching raw and linear grids.");
        }
        return rawLayout.Width * rawLayout.Height;
    }

    private static CenteredStackArtifactHeader Header(CenteredStackArtifactHeader raw, CenteredStackArtifactHeader linear, string hash)
    {
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            SchemaVersion, "1.0.0", SchemaVersion, JsonSerializer.SerializeToElement(new { payloadSha256 = hash })));
        Guid[] lineage = [raw.ArtifactId, linear.ArtifactId];
        var output = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, SchemaVersion, recipe.IdentitySha256, lineage);
        return new(ProcessingIdentity.CreateArtifactId(output), FrameArtifactRole.Metadata, SchemaVersion,
            recipe.IdentitySha256, MediaType, null, raw.CaptureId, raw.CaptureSequence, raw.Integration, null, null,
            null, linear.Compatibility, lineage, ProcessingProductKind.Metadata, SchemaVersion, recipe.Descriptor, null, hash);
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
