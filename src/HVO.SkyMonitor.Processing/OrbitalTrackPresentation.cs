using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.Processing;

/// <summary>Draws already-resolved exposure predictions without reading pixels or evaluating orbital geometry.</summary>
public static class OrbitalTrackPresentation
{
    public const string LayerKind = "PredictedSatelliteTracks";
    public const string ProducerVersion = "predicted-satellite-tracks-v1";
    public const string Legend = "PREDICTED SATELLITES - NOT DETECTIONS";
    public const string PartialLegend = "PARTIAL - GAPS / UNCOVERED TIME OMITTED";
    public const string UncertaintyLegend = "PHYSICAL UNCERTAINTY MAY BE UNKNOWN";
    public const string StaleLegend = "STALE ELEMENTS";
    public const string UnknownSourceValidityLegend = "SOURCE VALIDITY UNKNOWN";
    public const string PartlyKnownSourceValidityLegend = "SOURCE VALIDITY PARTLY KNOWN";
    public const string ConfiguredGeometryLegend = "CONFIGURED GEOMETRY";
    public const string ZeroEarthOrientationLegend = "ZERO EOP - DIAGNOSTIC";
    public const int MaximumTracks = 32;
    public const int MaximumSamplesPerTrack = 1024;
    public const int MaximumTotalSamples = 8192;
    public const int MaximumLabels = 16;
    public const int MaximumDisplayNameCharacters = 32;
    public const int MaximumPrimitives = PresentationLayerCompositor.MaximumLayerPrimitives;
    public const long MaximumWork = PresentationLayerCompositor.MaximumGeometryWork / 8;

    private const int EndpointRadius = 2;
    private const int Thickness = 1;
    private static readonly PresentationColor Color = new(255, 166, 77);
    private static readonly PresentationStrokeV2 Stroke = new(6, 4, 1_000_000);

    /// <summary>
    /// Converts one through <see cref="MaximumTracks"/> canonical products for the same exposure, observer,
    /// geometry, timing, model and source snapshot/policy into an independently selectable layer. The caller
    /// resolves the exact requested products and authenticates any measured calibration evidence beforehand.
    /// Samples already contain output pixels: readout, crop, binning and display transforms are never reapplied.
    /// </summary>
    /// <remarks>
    /// Duplicate objects, forged identities, incompatible dimensions/provenance and exceeded input/rendering
    /// budgets throw <see cref="ArgumentException"/> rather than truncating geometry. Only consecutive pixels
    /// inside declared segments are joined. Single or stationary samples retain endpoint circles. Physical
    /// visibility and brightness are never inferred, including from unknown illumination. Optional object
    /// labels are omitted when crowded, off-frame or past their count limit; their supplied names still bind
    /// source identity. The mandatory caption retains applicable partial, stale, source-validity and diagnostic
    /// qualifications independently of object labels. It must fit in full, otherwise the raster is rejected.
    /// Empty query results and query-level incompleteness remain the caller's responsibility.
    /// </remarks>
    public static PresentationLayerPayloadV1 CreatePayload(
        int sourceWidth, int sourceHeight, IReadOnlyList<OrbitalExposureTrackV1> tracks,
        IReadOnlyDictionary<OrbitalObjectIdentity, string>? displayNames = null)
        => CreatePayloadAndSourceFacts(sourceWidth, sourceHeight, tracks, displayNames).Payload;

    /// <summary>
    /// Returns the payload and its exact source-identity bytes for retention through existing metadata/artifact
    /// lineage. The caller owns the returned bytes and retains the referenced canonical tracks as source artifacts.
    /// No pixels or captured source facts are changed. The source envelope is versioned by the producer identity.
    /// </summary>
    public static (PresentationLayerPayloadV1 Payload, byte[] SourceFactsJson) CreatePayloadAndSourceFacts(
        int sourceWidth, int sourceHeight, IReadOnlyList<OrbitalExposureTrackV1> tracks,
        IReadOnlyDictionary<OrbitalObjectIdentity, string>? displayNames = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (sourceWidth is < 1 or > 65_536 || sourceHeight is < 1 or > 65_536 ||
            (long)sourceWidth * sourceHeight > PresentationLayerCompositor.MaximumCompositionPixels)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Raster dimensions exceed the presentation budget.");
        if (tracks.Count is < 1 or > MaximumTracks)
            throw new ArgumentException("Predicted track count is outside its bound.", nameof(tracks));

        var frozen = new List<OrbitalExposureTrackV1>();
        var objects = new HashSet<OrbitalObjectIdentity>();
        var samples = 0;
        foreach (var track in tracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            if (track.Samples is null || track.Segments is null ||
                track.Samples.Count is < 1 or > MaximumSamplesPerTrack ||
                track.Segments.Count > MaximumSamplesPerTrack ||
                (samples += track.Samples.Count) > MaximumTotalSamples)
                throw new ArgumentException("Predicted sample count exceeds its presentation bound.", nameof(tracks));
            var copy = OrbitalPredictionJson.CreateTrack(track);
            if (!string.Equals(copy.ProductSha256, track.ProductSha256, StringComparison.Ordinal))
                throw new ArgumentException("Predicted product identity does not match its canonical content.", nameof(tracks));
            if (!objects.Add(copy.Lineage.ObjectIdentity) ||
                copy.Geometry.ImageTransform.OutputWidthPixels != sourceWidth ||
                copy.Geometry.ImageTransform.OutputHeightPixels != sourceHeight ||
                frozen.Count > 0 && !Compatible(frozen[0], copy))
                throw new ArgumentException("Predicted tracks do not describe one compatible exposure and raster.", nameof(tracks));
            frozen.Add(copy);
        }
        var ordered = frozen.OrderBy(static t => t.Lineage.ObjectIdentity.CatalogNumber, StringComparer.Ordinal).ToArray();
        var names = FreezeNames(displayNames, objects);
        var scale = Math.Min(3, PresentationFont.StarFrameScale(sourceWidth, sourceHeight));
        var caption = Caption(sourceWidth, sourceHeight, scale, ordered);
        var texts = new List<PresentationTextBlockV1> { caption };
        var markers = new List<PresentationMarkerV1>();
        var segments = new List<PresentationSegmentV1>();
        var anchors = new List<(OrbitalExposureTrackV1 Track, PixelPoint Point)>();

        foreach (var track in ordered)
        {
            var endpoints = new HashSet<PixelPoint>();
            PixelPoint? anchor = null;
            foreach (var segment in track.Segments)
            {
                var first = track.Samples[segment.StartSampleIndex].Pixel!.Value;
                var last = track.Samples[segment.StartSampleIndex + segment.SampleCount - 1].Pixel!.Value;
                anchor ??= first;
                if (endpoints.Add(first)) markers.Add(new(first, EndpointRadius, Color));
                if (endpoints.Add(last)) markers.Add(new(last, EndpointRadius, Color));
                var phase = 0d;
                for (var index = segment.StartSampleIndex + 1; index < segment.StartSampleIndex + segment.SampleCount; index++)
                {
                    // Canonical validation rejects null pixels inside a declared segment. No sample outside
                    // that segment participates, even when its pixel is nonnull or adjacent in time.
                    var from = track.Samples[index - 1].Pixel!.Value;
                    var to = track.Samples[index].Pixel!.Value;
                    if (from == to) continue;
                    segments.Add(new(from, to, Thickness, Color,
                        Stroke with { DashOffsetPixels = Math.Round(phase, 3, MidpointRounding.AwayFromZero) }));
                    var dx = to.X - from.X;
                    var dy = to.Y - from.Y;
                    phase = (phase + Math.Sqrt(dx * dx + dy * dy)) % (Stroke.DashPixels + Stroke.GapPixels);
                }
            }
            if (anchor is { } point) anchors.Add((track, point));
        }

        var occupied = new List<SKRect> { Bounds(caption, sourceWidth, sourceHeight) };
        occupied.AddRange(markers.Select(static marker => new SKRect(
            (float)marker.Center.X - EndpointRadius - 2, (float)marker.Center.Y - EndpointRadius - 2,
            (float)marker.Center.X + EndpointRadius + 2, (float)marker.Center.Y + EndpointRadius + 2)));
        var appearance = Appearance(9_000 * scale);
        foreach (var (track, anchor) in anchors)
        {
            if (texts.Count == MaximumLabels + 1) break;
            var lines = Label(track, names.GetValueOrDefault(track.Lineage.ObjectIdentity));
            var template = new PresentationTextBlockV1(PresentationTextAnchor.Point, default, lines,
                scale, 0, 1, Color, Appearance: appearance);
            var templateBounds = Bounds(template, sourceWidth, sourceHeight);
            var y = Math.Round(anchor.Y - templateBounds.Height - EndpointRadius - 5, MidpointRounding.AwayFromZero);
            foreach (var x in (ReadOnlySpan<double>)[Math.Ceiling(anchor.X + EndpointRadius + 5),
                Math.Floor(anchor.X - EndpointRadius - 5 - templateBounds.Width)])
            {
                var block = template with { Point = new(x, y) };
                var bounds = Bounds(block, sourceWidth, sourceHeight);
                if (!Inside(bounds, sourceWidth, sourceHeight) || occupied.Any(other => Overlaps(other, bounds))) continue;
                texts.Add(block);
                occupied.Add(bounds);
                break;
            }
        }

        var primitives = markers.Count + segments.Count + texts.Sum(static block => block.Lines.Count);
        var work = segments.Sum(static s => (Math.Abs(s.To.X - s.From.X) + Math.Abs(s.To.Y - s.From.Y) + 1) * s.Thickness) +
            markers.Count * 16d * (EndpointRadius + 4) +
            texts.Sum(static block => block.Lines.Sum(static line => line.Length) * 49L * block.Scale * block.Scale);
        if (primitives > MaximumPrimitives || work > MaximumWork)
            throw new ArgumentException("Predicted tracks exceed the presentation primitive/work budget.", nameof(tracks));

        // Include names even if a label cannot fit. Returning the exact bytes lets an artifact adapter retain
        // the same inputs the payload hashes, rather than inventing an unrelated source descriptor identity.
        var sourceFacts = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ProducerVersion,
            LayerKind,
            sourceWidth,
            sourceHeight,
            Tracks = ordered.Select(track => new
            {
                track.ProductSha256,
                DisplayName = names.GetValueOrDefault(track.Lineage.ObjectIdentity)
            }).ToArray()
        });
        var sourceIdentity = Convert.ToHexString(SHA256.HashData(sourceFacts));
        return (PresentationLayerPayloadJson.Create(sourceIdentity, sourceWidth, sourceHeight,
            markers: markers, segments: segments, textBlocks: texts), sourceFacts);
    }

    private static bool Compatible(OrbitalExposureTrackV1 a, OrbitalExposureTrackV1 b) =>
        a.RequestedInterval == b.RequestedInterval && a.Observer == b.Observer && a.Geometry == b.Geometry &&
        a.Timing == b.Timing && a.Model == b.Model &&
        a.Lineage.SnapshotManifestSha256 == b.Lineage.SnapshotManifestSha256 &&
        a.Lineage.NormalizedPayloadSha256 == b.Lineage.NormalizedPayloadSha256 &&
        a.Lineage.SelectionPolicySha256 == b.Lineage.SelectionPolicySha256 &&
        a.Lineage.SourceDataset == b.Lineage.SourceDataset &&
        a.Lineage.PopulationCoverage.RequestedPopulation == b.Lineage.PopulationCoverage.RequestedPopulation &&
        a.Lineage.PopulationCoverage.Completeness == b.Lineage.PopulationCoverage.Completeness &&
        a.Lineage.PopulationCoverage.KnownExclusions.SequenceEqual(b.Lineage.PopulationCoverage.KnownExclusions, StringComparer.Ordinal);

    private static Dictionary<OrbitalObjectIdentity, string> FreezeNames(
        IReadOnlyDictionary<OrbitalObjectIdentity, string>? supplied, HashSet<OrbitalObjectIdentity> objects)
    {
        var names = new Dictionary<OrbitalObjectIdentity, string>();
        if (supplied is null) return names;
        if (supplied.Count > MaximumTracks)
            throw new ArgumentException("Too many predicted display names.", nameof(supplied));
        foreach (var (identity, name) in supplied)
        {
            if (identity is null || !objects.Contains(identity) || string.IsNullOrWhiteSpace(name) ||
                name.Length > MaximumDisplayNameCharacters || name.Any(static c => char.IsControl(c) || c is '\u2028' or '\u2029') ||
                !names.TryAdd(identity, name))
                throw new ArgumentException("Predicted display names must be bounded and belong to the supplied tracks.", nameof(supplied));
        }
        return names;
    }

    private static PresentationTextBlockV1 Caption(int width, int height, int scale, IReadOnlyList<OrbitalExposureTrackV1> tracks)
    {
        // At most eight fixed lines, independent of object count, names and optional label placement.
        // Do not infer additional physical or freshness facts from source validity or geometry provenance.
        var lines = new List<string> { Legend };
        if (tracks.Any(static t => t.Status == OrbitalExecutionStatus.Partial)) lines.Add(PartialLegend);
        lines.Add(UncertaintyLegend);
        if (tracks.Any(static t => t.Lineage.Freshness == OrbitalElementFreshness.Stale)) lines.Add(StaleLegend);
        if (tracks.Any(static t => t.Lineage.SourceValidity == OrbitalSourceValidity.Unknown)) lines.Add(UnknownSourceValidityLegend);
        if (tracks.Any(static t => t.Lineage.SourceValidity == OrbitalSourceValidity.PartlyKnown)) lines.Add(PartlyKnownSourceValidityLegend);
        if (tracks.Any(static t => t.Geometry.Basis == OrbitalGeometryBasis.ConfiguredRig)) lines.Add(ConfiguredGeometryLegend);
        if (tracks.Any(static t => t.Model.NominalZeroEarthOrientation)) lines.Add(ZeroEarthOrientationLegend);
        for (var size = 9_000 * scale; size >= 7_000; size -= 1_000)
        {
            var block = new PresentationTextBlockV1(PresentationTextAnchor.BottomLeft, default, lines,
                scale, 4, 1, Color, Appearance: Appearance(size));
            if (Inside(Bounds(block, width, height), width, height)) return block;
        }
        throw new ArgumentException("Raster cannot carry the mandatory predicted track legend and qualifications.", nameof(width));
    }

    private static string[] Label(OrbitalExposureTrackV1 track, string? name)
    {
        var lineage = track.Lineage;
        var age = Math.Max(Math.Abs(lineage.StartEpochAgeHours), Math.Abs(lineage.EndEpochAgeHours));
        var validity = lineage.SourceValidity switch
        {
            OrbitalSourceValidity.Known => "KNOWN",
            OrbitalSourceValidity.PartlyKnown => "PARTLY KNOWN",
            _ => "UNKNOWN"
        };
        return
        [
            $"Predicted NORAD {lineage.ObjectIdentity.CatalogNumber}{(name is null ? string.Empty : " | " + name)}",
            string.Create(CultureInfo.InvariantCulture, $"EPOCH AGE <= {Math.Ceiling(age * 10) / 10:0.0}H | {lineage.Freshness.ToString().ToUpperInvariant()} | SOURCE {validity}"),
            $"{(track.Status == OrbitalExecutionStatus.Partial ? "PARTIAL: " + track.Reason : "COMPLETE")} | {(track.Geometry.Basis == OrbitalGeometryBasis.ConfiguredRig ? "CONFIGURED" : "MEASURED BASIS")}{(track.Model.NominalZeroEarthOrientation ? " | ZERO EOP" : string.Empty)}"
        ];
    }

    private static PresentationTextAppearanceV3 Appearance(int size) => new(new(
        PresentationFontFaceV3.MonoRegular, size, 0, Color, new(3, 8, 14), 950_000, 2_000));

    private static SKRect Bounds(PresentationTextBlockV1 block, int width, int height)
    {
        using var font = PresentationFont.Create(block, 0);
        var bounds = PresentationFont.BackplateBounds(block, width, height, font);
        bounds.Inflate(2, 2); // Includes the halo and a pixel of collision clearance for either renderer.
        return bounds;
    }

    private static bool Inside(SKRect bounds, int width, int height) =>
        bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width && bounds.Bottom <= height;

    private static bool Overlaps(SKRect a, SKRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
