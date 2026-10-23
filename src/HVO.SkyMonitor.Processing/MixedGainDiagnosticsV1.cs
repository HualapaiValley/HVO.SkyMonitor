using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Checksum and identity binding for one versioned support raster.</summary>
public sealed record MixedGainSupportReference(string Encoding, string OutputIdentitySha256, string ChecksumSha256, int ByteLength);

/// <summary>Versioned row-major UInt8 rasters, one byte per original photosite; metadata role excludes image windows.</summary>
public static class MixedGainSupportV1
{
    public const string ValidCountsEncoding = "valid-counts-u8-row-major-v1";
    public const string NoCoverageEncoding = "no-coverage-u8-row-major-v1";
    public const string MediaType = "application/vnd.hvo.mixed-gain-support";
    // The combined UInt16 product must also fit one checked Int32-sized byte array.
    public const int MaximumPhotosites = int.MaxValue / 2;

    public static string VariantFor(string outputVariant, string kind)
        => kind + "-" + ProcessingIdentity.ComputePayloadSha256(System.Text.Encoding.UTF8.GetBytes(outputVariant))[..32];

    public static FrameLayoutDescriptor CreateLayout(int width, int height)
    {
        var count = checked(width * height);
        if (width < 1 || height < 1 || count > MaximumPhotosites) throw new ArgumentOutOfRangeException(nameof(width));
        return new(width, height, width, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable,
            8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, count);
    }
}

/// <summary>Sample exclusions for one selected light; saturation is counted separately from other invalidity.</summary>
public sealed record MixedGainInputDiagnostics(Guid ArtifactId, int SaturatedSampleCount, int InvalidSampleCount);

/// <summary>
/// Immutable companion to a mixed-gain pixel product. Counts refer to original photosites, never demosaiced pixels.
/// No coverage means no measurement; its zero output code must not be interpreted as measured zero flux.
/// </summary>
public sealed record MixedGainDiagnosticsV1(
    string SchemaVersion,
    string PlanIdentitySha256,
    string CombinedOutputIdentitySha256,
    string CombinedChecksumSha256,
    int Width,
    int Height,
    int PhotositeCount,
    int ClippedLowCount,
    int ClippedHighCount,
    int NoCoverageCount,
    IReadOnlyList<MixedGainInputDiagnostics> Inputs,
    IReadOnlyList<MixedGainSupportReference> Support)
{
    public const string CurrentSchemaVersion = "mixed-gain-diagnostics-v1";
    public const string MediaType = "application/vnd.hvo.mixed-gain-diagnostics+json";
    public const string Variant = "mixed-gain-diagnostics-v1";
    public const int MaximumPayloadBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(this, Options)));

    /// <summary>Accepts bounded canonical evidence only; missing or corrupt evidence never becomes zero counts.</summary>
    public static MixedGainDiagnosticsV1? Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumPayloadBytes) return null;
        try
        {
            var value = JsonSerializer.Deserialize<MixedGainDiagnosticsV1>(bytes.Span, Options);
            if (value is null || value.SchemaVersion != CurrentSchemaVersion || value.PhotositeCount is <= 0 or > MixedGainSupportV1.MaximumPhotosites ||
                value.Width <= 0 || value.Height <= 0 || (long)value.Width * value.Height != value.PhotositeCount ||
                value.Support is not { Count: 2 } ||
                value.Support[0] is null || value.Support[1] is null ||
                value.Support[0].Encoding != MixedGainSupportV1.ValidCountsEncoding ||
                value.Support[1].Encoding != MixedGainSupportV1.NoCoverageEncoding ||
                value.Support.Any(map => map is null || !Sha256(map.OutputIdentitySha256) || !Sha256(map.ChecksumSha256) ||
                    map.ByteLength != value.PhotositeCount) ||
                !Sha256(value.PlanIdentitySha256) || !Sha256(value.CombinedOutputIdentitySha256) ||
                !Sha256(value.CombinedChecksumSha256) ||
                value.ClippedLowCount < 0 || value.ClippedHighCount < 0 || value.NoCoverageCount < 0 ||
                (long)value.ClippedLowCount + value.ClippedHighCount + value.NoCoverageCount > value.PhotositeCount ||
                value.Inputs is not { Count: > 0 and <= NormalizationPlanV1.MaximumInputs } ||
                value.Inputs.Any(input => input is null || input.ArtifactId == Guid.Empty ||
                    input.SaturatedSampleCount < 0 || input.InvalidSampleCount < 0 ||
                    (long)input.SaturatedSampleCount + input.InvalidSampleCount > value.PhotositeCount) ||
                value.Inputs.Select(static input => input.ArtifactId).Distinct().Count() != value.Inputs.Count ||
                !bytes.Span.SequenceEqual(value.Serialize())) return null;
            return value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
