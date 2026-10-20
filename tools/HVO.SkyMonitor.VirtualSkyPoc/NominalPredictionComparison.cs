using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using static HVO.SkyMonitor.VirtualSkyPoc.NominalCausalEvaluation;

namespace HVO.SkyMonitor.VirtualSkyPoc;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by strict JSON deserialization.")]
internal sealed record NominalTrackFile(string Path, string Sha256);
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by strict JSON deserialization.")]
internal sealed record NominalPredictionManifest(string Schema, int MaximumSegmentComparisons, IReadOnlyList<NominalTrackFile> Tracks);
internal sealed record NominalSegmentResidual(int Segment, double RmsPerpendicularPixels,
    double MaximumPerpendicularPixels, double UnorderedMaximumEndpointErrorPixels);
internal sealed record NominalHypothesis(string ProductSha256, bool Eligible, IReadOnlyList<string> Reasons,
    IReadOnlyList<NominalSegmentResidual> Segments);
internal sealed record NominalComponentComparison(int Component, Linear16TransientComponent Measured,
    PixelPoint RawStart, PixelPoint RawEnd, IReadOnlyList<NominalHypothesis> Hypotheses);
internal sealed record NominalFrameComparison(long CaptureSequence, string Interpretation,
    IReadOnlyList<string> Reasons, IReadOnlyList<NominalHypothesis> Coverage, IReadOnlyList<NominalComponentComparison> Components);
internal sealed record NominalTrackProvenance(string FileSha256, OrbitalExposureTrackV1 Product);
internal sealed record NominalComparisonReport(string Schema, string ToolVersion, string EvaluationSha256,
    string PredictionManifestSha256, string Status, string MotionFeatures, int MaximumSegmentComparisons, int ComparedSegments,
    IReadOnlyList<NominalFileRead> FilesRead, IReadOnlyList<NominalTrackProvenance> Predictions,
    IReadOnlyList<NominalFrameComparison> Frames);

internal static class NominalPredictionComparison
{
    internal const int MaximumTracks = 8;
    internal const int MaximumSegmentComparisons = 8192;

    internal static NominalComparisonReport Compare(string evaluationPath, string predictionRoot, Action<string>? onRead = null)
    {
        var reads = new List<NominalFileRead>();
        var evaluationFullPath = Path.GetFullPath(evaluationPath);
        var evaluationBytes = Read(Path.GetDirectoryName(evaluationFullPath)!, Path.GetFileName(evaluationFullPath),
            MaximumReportBytes, null, reads, onRead);
        var evaluation = Parse<NominalEvaluationReport>(evaluationBytes);
        Require(evaluation.Schema == "nominal-causal-evaluation-v1" && evaluation.ToolVersion == ToolVersion &&
            evaluation.Frames is { Count: > 0 and <= MaximumFrames } && evaluation.Options == Options &&
            evaluation.TemporalKind == TransientTemporalBackgroundKind.CausalProvisional &&
            evaluation.Frames.All(frame => frame is not null && frame.Raw is not null && frame.Input is not null &&
                frame.Temporal is not null && (frame.Extraction is null || frame.Extraction.Components is { Count: <= 32 })),
            "evaluation-report-boundary");
        ValidateManifest(new("nominal-causal-raw-v1", evaluation.MaximumAdjacentStartIntervalSeconds,
            evaluation.Frames.Select(frame => frame.Raw).ToArray()));
        foreach (var frame in evaluation.Frames)
        {
            Require(TransientContractJson.Validate(frame.Input).IsValid, "evaluation-input-descriptor");
            TransientTemporalBackgroundJson.Validate(frame.Temporal);
            var source = frame.Input.Source;
            Require(source.EvidenceId == frame.Raw.CaptureId && source.Locator.Artifact.ArtifactId == frame.Raw.CaptureId &&
                source.Locator.Artifact.ChecksumSha256 == frame.Raw.Sha256 &&
                source.Locator.Artifact.RecipeIdentitySha256 == frame.Raw.RecipeIdentitySha256 &&
                source.Locator.Artifact.Role == FrameArtifactRole.Raw && source.Locator.Artifact.Variant == "raw-v1" &&
                source.ObservationStartedUtc == frame.Raw.ObservationStartedUtc && source.ObservationEndedUtc == frame.Raw.ObservationEndedUtc &&
                source.TimingQuality == frame.Raw.TimingQuality &&
                frame.Input.Compatibility == frame.Raw.Compatibility && frame.Input.Levels == frame.Raw.Levels &&
                frame.Input.Layout.Width == frame.Raw.Layout.Width * frame.Input.SourceToDetectorTransform.ScaleX &&
                frame.Input.Layout.Height == frame.Raw.Layout.Height * frame.Input.SourceToDetectorTransform.ScaleY &&
                frame.Temporal.Sources.Any(lineage => lineage.Position == TransientTemporalPosition.N &&
                    lineage.CaptureSequence == frame.Raw.CaptureSequence && lineage.DetectorInputIdentitySha256 == frame.Input.InputIdentitySha256),
                "evaluation-source-lineage-mismatch");
            Require((frame.Temporal.Status == TransientTemporalBackgroundStatus.Produced) == (frame.Extraction is not null),
                "evaluation-extraction-availability");
        }
        var manifestBytes = Read(predictionRoot, "predictions.json", MaximumManifestBytes, null, reads, onRead);
        var manifest = Parse<NominalPredictionManifest>(manifestBytes);
        Require(manifest.Schema == "nominal-canonical-predictions-v1" &&
            manifest.MaximumSegmentComparisons is > 0 and <= MaximumSegmentComparisons && manifest.Tracks is { Count: <= MaximumTracks } &&
            manifest.Tracks.All(file => file is not null && Sha(file.Sha256) && file.Path == "tracks/" + file.Sha256 + ".json") &&
            manifest.Tracks.Select(file => file.Sha256).Distinct(StringComparer.Ordinal).Count() == manifest.Tracks.Count,
            "prediction-manifest-bounds");
        foreach (var file in manifest.Tracks) _ = Resolve(predictionRoot, file.Path);
        var tracks = manifest.Tracks.OrderBy(file => file.Sha256, StringComparer.Ordinal).Select(file =>
        {
            var bytes = Read(predictionRoot, file.Path, 1024 * 1024, file.Sha256, reads, onRead);
            var track = OrbitalPredictionJson.ParseTrack(bytes);
            Require(track.Samples.Count <= 1024 && track.Segments.Count <= 16, "prediction-sample-bound");
            return new NominalTrackProvenance(file.Sha256, track);
        }).ToArray();
        var budget = manifest.MaximumSegmentComparisons;
        var limited = false;
        var frames = evaluation.Frames.Select(frame =>
        {
            var frameReasons = new List<string> { "nominal-configured-geometry-only", "measured-clock-uncertainty-unavailable",
                "unidentified-within-available-coverage" };
            if (tracks.Length == 0) frameReasons.Add("prediction-coverage-absent");
            if (frame.Temporal.Status != TransientTemporalBackgroundStatus.Produced) frameReasons.Add("temporal-context-unavailable");
            if (frame.Extraction?.CandidateLimitExceeded == true) frameReasons.Add("extraction-work-limit");
            var coverage = tracks.Select(entry =>
            {
                var reasons = new List<string>();
                var eligible = Eligible(frame, entry.Product, reasons);
                return new NominalHypothesis(entry.Product.ProductSha256, eligible, reasons, []);
            }).ToArray();
            var components = (frame.Extraction?.Components ?? []).Select((component, index) =>
            {
                var transform = frame.Input.SourceToDetectorTransform;
                var start = ToSource(component.StartX, component.StartY, transform);
                var end = ToSource(component.EndX, component.EndY, transform);
                var hypotheses = tracks.Select((entry, trackIndex) =>
                {
                    var track = entry.Product;
                    var reasons = coverage[trackIndex].Reasons.ToList();
                    var eligible = coverage[trackIndex].Eligible;
                    var residuals = new List<NominalSegmentResidual>();
                    if (eligible)
                    {
                        for (var segmentIndex = 0; segmentIndex < track.Segments.Count; segmentIndex++)
                        {
                            var segment = track.Segments[segmentIndex];
                            if (segment.SampleCount < 2) { reasons.Add("segment-insufficient-samples"); continue; }
                            if (budget == 0)
                            {
                                limited = true;
                                if (!reasons.Contains("comparison-work-limit", StringComparer.Ordinal)) reasons.Add("comparison-work-limit");
                                continue;
                            }
                            budget--;
                            var points = track.Samples.Skip(segment.StartSampleIndex).Take(segment.SampleCount).Select(sample => sample.Pixel).ToArray();
                            if (points.Any(point => point is null)) { reasons.Add("segment-pixel-unavailable"); continue; }
                            residuals.Add(Residual(segmentIndex, start, end, points.Select(point => point!.Value).ToArray()));
                        }
                        if (track.Segments.Count == 0) reasons.Add("no-visible-segments");
                    }
                    return new NominalHypothesis(track.ProductSha256, eligible, reasons.Distinct(StringComparer.Ordinal).ToArray(), residuals);
                }).ToArray();
                return new NominalComponentComparison(index + 1, component, start, end, hypotheses);
            }).ToArray();
            return new NominalFrameComparison(frame.Raw.CaptureSequence, "research-residuals-only", frameReasons, coverage, components);
        }).ToArray();
        return new("nominal-prediction-comparison-v1", ToolVersion, Hash(evaluationBytes), Hash(manifestBytes),
            limited ? "partial-comparison-work-limit" : "compared-supplied-components-with-explicit-coverage",
            "direction-and-angular-speed-unavailable-from-single-integrated-streak", manifest.MaximumSegmentComparisons,
            manifest.MaximumSegmentComparisons - budget, reads, tracks, frames);
    }

    private static bool Eligible(NominalFrameOutcome frame, OrbitalExposureTrackV1 track, List<string> reasons)
    {
        var eligible = true;
        if (track.RequestedInterval.StartUtc != frame.Raw.ObservationStartedUtc ||
            track.RequestedInterval.EndUtc != frame.Raw.ObservationEndedUtc ||
            track.Timing.Exposure.StartUtc != frame.Raw.ObservationStartedUtc || track.Timing.Exposure.EndUtc != frame.Raw.ObservationEndedUtc)
        { reasons.Add("exposure-interval-mismatch"); eligible = false; }
        if (frame.Raw.GeometryIdentitySha256 is null)
        { reasons.Add("capture-geometry-unknown"); eligible = false; }
        else if (frame.Raw.GeometryIdentitySha256 != GeometryHash(track.Geometry) ||
            frame.Raw.Compatibility.Rig != track.Geometry.RigIdentitySha256 ||
            frame.Raw.Layout.Width != track.Geometry.ImageTransform.OutputWidthPixels ||
            frame.Raw.Layout.Height != track.Geometry.ImageTransform.OutputHeightPixels ||
            track.Geometry.ProjectionCoordinates != OrbitalProjectionCoordinates.ReadoutView ||
            track.Geometry.ImageTransform != ProjectedSceneImageTransformV1.Identity(track.Geometry.Projection.WidthPixels,
                track.Geometry.Projection.HeightPixels))
        { reasons.Add("capture-geometry-mismatch-or-unsupported-transform"); eligible = false; }
        if (track.Geometry.Basis != OrbitalGeometryBasis.ConfiguredRig)
        { reasons.Add("accepted-measured-consumption-outside-tool-scope"); eligible = false; }
        if (track.Status == OrbitalExecutionStatus.Unavailable) { reasons.Add("prediction-unavailable"); eligible = false; }
        if (track.Status == OrbitalExecutionStatus.Partial || track.CoveredInterval != track.RequestedInterval)
            reasons.Add("prediction-interval-partial");
        if (track.Lineage.Freshness != OrbitalElementFreshness.Fresh) reasons.Add("prediction-freshness:" + track.Lineage.Freshness);
        if (track.Lineage.SourceValidity != OrbitalSourceValidity.Known) reasons.Add("prediction-source-validity:" + track.Lineage.SourceValidity);
        if (track.Lineage.PopulationCoverage.Completeness != OrbitalPopulationCompleteness.CompleteForRequestedPopulation) reasons.Add("prediction-population-partial-or-unknown");
        reasons.Add("uncertainty-retained-in-prediction-provenance");
        return eligible;
    }

    internal static string GeometryHash(OrbitalGeometryFacts geometry) => CaptureContractJson.ComputeCanonicalJsonSha256(geometry);

    internal static PixelPoint ToSource(double x, double y, TransientDetectorTransformV1 transform)
    {
        Require(transform is not null && double.IsFinite(transform.ScaleX) && double.IsFinite(transform.ScaleY) &&
            transform.ScaleX != 0 && transform.ScaleY != 0 && double.IsFinite(transform.OffsetX) && double.IsFinite(transform.OffsetY) &&
            double.IsFinite(x) && double.IsFinite(y), "invalid-source-to-detector-transform");
        return new((x - transform.OffsetX) / transform.ScaleX, (y - transform.OffsetY) / transform.ScaleY);
    }

    internal static NominalSegmentResidual Residual(int segment, PixelPoint start, PixelPoint end, IReadOnlyList<PixelPoint> points)
    {
        Require(points.Count >= 2, "segment-insufficient-samples");
        var length = double.Hypot(end.X - start.X, end.Y - start.Y);
        var residuals = points.Select(point => length == 0 ? Distance(start, point) :
            Math.Abs((end.X - start.X) * (start.Y - point.Y) - (start.X - point.X) * (end.Y - start.Y)) / length).ToArray();
        return new(segment, Math.Sqrt(residuals.Select(value => value * value).Average()), residuals.Max(),
            Math.Min(Math.Max(Distance(start, points[0]), Distance(end, points[^1])),
                Math.Max(Distance(start, points[^1]), Distance(end, points[0]))));
    }

    private static double Distance(PixelPoint left, PixelPoint right) => double.Hypot(left.X - right.X, left.Y - right.Y);
}
