using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>The still nightly products this CameraAgent generates. Time-lapse video is owned by issue #1130.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NightlyProductKind>))]
public enum NightlyProductKind
{
    /// <summary>A north-zenith-south meridian keogram with time on the horizontal axis.</summary>
    Keogram,

    /// <summary>A lighten star trail over quality-admitted night frames.</summary>
    StarTrail
}

/// <summary>Where a product sits in the hierarchical lineage of an observing day.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NightlyProductScope>))]
public enum NightlyProductScope
{
    /// <summary>An ordered part of one scheduled window, composed directly from published preview frames.</summary>
    Segment,

    /// <summary>An intermediate star-trail lighten over at most one recipe execution's worth of segments.</summary>
    Rollup,

    /// <summary>The observing-day product, composed from that day's current segments.</summary>
    Final
}

/// <summary>The recorded disposition of one evaluated window.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NightlyProductWindowDisposition>))]
public enum NightlyProductWindowDisposition
{
    /// <summary>Current products exist for the window's admitted sources.</summary>
    Produced,

    /// <summary>The window had no admitted source, so it has no product. Its exclusions are recorded.</summary>
    NoSources,

    /// <summary>The window exceeded a declared bound or failed a recipe contract, so it has no product.</summary>
    Rejected
}

/// <summary>Bounds and reason codes of the nightly product contract.</summary>
public static class NightlyProductContract
{
    /// <summary>The durable schema version of the nightly product store.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The schema label of the immutable provenance document written beside every product.</summary>
    public const string ProvenanceSchemaVersion = "hvo-still-product-provenance-v2";

    /// <summary>
    /// The most published preview candidates one window reads. A window above the bound is rejected with
    /// <see cref="WindowSourceBoundReasonCode"/> rather than truncated.
    /// </summary>
    public const int MaximumWindowCandidates = 4096;

    /// <summary>The most products one catalog listing returns.</summary>
    public const int MaximumListedProducts = 1024;

    /// <summary>The automation target that generates keograms.</summary>
    public const string KeogramTarget = "keogram";

    /// <summary>The automation target that generates star trails.</summary>
    public const string StarTrailTarget = "star-trail";

    public const string ExcludedRigReasonCode = "nightly.rig-mismatch";
    public const string ExcludedSolarAltitudeReasonCode = "nightly.solar-altitude";
    public const string ExcludedLocationReasonCode = "nightly.location-unresolved";
    public const string ExcludedUnsupportedSourceReasonCode = "nightly.unsupported-source";
    public const string WindowSourceBoundReasonCode = "nightly.window-source-bound";
    public const string FinalSegmentBoundReasonCode = "nightly.night-segment-bound";
    public const string ExcludedTransferReasonCode = "nightly.source-transfer-mismatch";
    public const string ExecutionBoundReasonCode = "nightly.execution-bound";
    public const string GeometryUnavailableReasonCode = "nightly.geometry-unavailable";

    /// <summary>The automation target of a product kind.</summary>
    public static string TargetFor(NightlyProductKind kind) => kind switch
    {
        NightlyProductKind.Keogram => KeogramTarget,
        NightlyProductKind.StarTrail => StarTrailTarget,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Parses an automation target into its product kind.</summary>
    public static bool TryParseTarget(string? target, out NightlyProductKind kind)
    {
        switch (target)
        {
            case KeogramTarget:
                kind = NightlyProductKind.Keogram;
                return true;
            case StarTrailTarget:
                kind = NightlyProductKind.StarTrail;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>What one lineage entry of a nightly product refers to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NightlyProductSourceKind>))]
public enum NightlyProductSourceKind
{
    /// <summary>A published preview processing output of one capture.</summary>
    PreviewFrame,

    /// <summary>Another published nightly product, such as a segment of a night product.</summary>
    NightlyProduct
}

/// <summary>One ordered source of a nightly product: a published preview output or another nightly product.</summary>
public sealed record NightlyProductSource(
    int Ordinal,
    NightlyProductSourceKind SourceKind,
    Guid ArtifactId,
    string OutputIdentitySha256,
    Guid? CaptureId,
    DateTimeOffset ObservationStartedUtc);

/// <summary>One immutable published nightly product and whether it is the current product of its window.</summary>
public sealed record NightlyProductSummary(
    Guid ProductId,
    NightlyProductKind Kind,
    NightlyProductScope Scope,
    DateOnly ObservingDate,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int PartOrdinal,
    bool IsCurrent,
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    int SourceCount,
    DateTimeOffset FirstObservationUtc,
    DateTimeOffset LastObservationUtc,
    TimeSpan TotalIntegration,
    DateTimeOffset CreatedUtc);

/// <summary>A product's full provenance: recipe and algorithm identities, compatibility, checksums, and ordered lineage.</summary>
public sealed record NightlyProductDetail(
    NightlyProductSummary Summary,
    string OutputIdentitySha256,
    string RecipeIdentitySha256,
    string RecipeName,
    string Variant,
    string RigProfileSha256,
    string PayloadSha256,
    long PayloadBytes,
    string RenditionSha256,
    long RenditionBytes,
    string ProvenanceSha256,
    IReadOnlyList<NightlyProductSource> Sources)
{
    public LocalAutomationOccurrence Occurrence { get; init; } = null!;
}

/// <summary>The recorded evaluation of one segment window or observing night.</summary>
public sealed record NightlyProductWindowStatus(
    NightlyProductKind Kind,
    NightlyProductScope Scope,
    DateOnly ObservingDate,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    NightlyProductWindowDisposition Disposition,
    string? ReasonCode,
    int CandidateCount,
    int AdmittedCount,
    IReadOnlyDictionary<string, int> Exclusions,
    DateTimeOffset EvaluatedUtc)
{
    public LocalAutomationOccurrence Occurrence { get; init; } = null!;
}

/// <summary>A verified JPEG rendition of one product.</summary>
public sealed record NightlyProductRendition(Guid ProductId, string MediaType, ReadOnlyMemory<byte> Content);

/// <summary>A checksum-verified canonical provenance document.</summary>
public sealed record NightlyProductProvenance(Guid ProductId, ReadOnlyMemory<byte> Content);

/// <summary>The read model of generated nightly products. Library and Product Detail pages consume it.</summary>
public interface INightlyProductCatalog
{
    /// <summary>Lists the products of one observing date, current and superseded, oldest window first.</summary>
    ValueTask<IReadOnlyList<NightlyProductSummary>> ListAsync(DateOnly observingDate, CancellationToken cancellationToken);

    /// <summary>Lists the recorded window evaluations of one observing date.</summary>
    ValueTask<IReadOnlyList<NightlyProductWindowStatus>> ListWindowsAsync(
        DateOnly observingDate,
        CancellationToken cancellationToken);

    /// <summary>Returns a product's provenance and ordered lineage, or null when it does not exist.</summary>
    ValueTask<NightlyProductDetail?> GetAsync(Guid productId, CancellationToken cancellationToken);

    ValueTask<NightlyProductProvenance?> OpenProvenanceAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Returns the checksum-verified JPEG rendition, or null when the product does not exist.</summary>
    ValueTask<NightlyProductRendition?> OpenRenditionAsync(Guid productId, CancellationToken cancellationToken);
}
