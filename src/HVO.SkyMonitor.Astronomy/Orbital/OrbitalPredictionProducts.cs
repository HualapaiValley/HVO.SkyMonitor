using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy;

public enum OrbitalPredictionUse { Diagnostic, MeasuredAssociation }

/// <summary>Bounded local inputs only. The caller resolves the immutable snapshot before frame processing.</summary>
public sealed class OrbitalPredictionRequest
{
    public OrbitalPredictionUse Use { get; }
    public OrbitalSnapshot Snapshot { get; }
    public OrbitalSelectionPolicy SelectionPolicy { get; }
    public OrbitalUtcInterval Interval { get; }
    public IReadOnlyList<OrbitalObjectIdentity> Objects { get; }
    public ObserverLocation Observer { get; }
    public OrbitalPredictionModelFacts Model { get; }
    public OrbitalQueryBounds Bounds { get; }

    public OrbitalPredictionRequest(OrbitalPredictionUse use, OrbitalSnapshot snapshot, OrbitalSelectionPolicy selectionPolicy, OrbitalUtcInterval interval,
        IEnumerable<OrbitalObjectIdentity> objects, ObserverLocation observer, OrbitalPredictionModelFacts model, OrbitalQueryBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(selectionPolicy);
        ArgumentNullException.ThrowIfNull(interval); ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(bounds);
        interval.Validate(); observer.Validate(); model.Validate(); bounds.Validate();
        ValidateModelInterval(model, interval);
        OrbitalContract.Require(Enum.IsDefined(use) && (use == OrbitalPredictionUse.Diagnostic ||
            !model.NominalZeroEarthOrientation && !selectionPolicy.AllowStaleDiagnostics), "diagnostic-only-model-or-policy");
        Use = use;
        var identities = objects.Take(bounds.MaximumObjects + 1).ToArray();
        OrbitalContract.Require(identities.Length is > 0 && identities.Length <= bounds.MaximumObjects &&
            identities.All(OrbitalContract.IsObjectIdentity) && identities.Distinct().Count() == identities.Length &&
            (interval.EndUtc - interval.StartUtc).TotalSeconds <= bounds.MaximumWindowSeconds, "prediction-request-bounds");
        Snapshot = snapshot; SelectionPolicy = selectionPolicy; Interval = interval; Observer = observer; Model = model; Bounds = bounds;
        Objects = Array.AsReadOnly(identities.OrderBy(static i => i.CatalogNumber, StringComparer.Ordinal).ToArray());
    }

    internal static void ValidateModelInterval(OrbitalPredictionModelFacts model, OrbitalUtcInterval interval)
        => OrbitalContract.Require(interval.StartUtc >= model.LeapTableCoverage.StartUtc && interval.EndUtc <= model.LeapTableCoverage.EndUtc &&
            (model.NominalZeroEarthOrientation || model.EarthOrientationCoverage is { } eop &&
                interval.StartUtc >= eop.StartUtc && interval.EndUtc <= eop.EndUtc), "time-input-coverage");
}

public sealed class OrbitalExposureQuery
{
    private readonly AstrometricCalibration? calibration;
    private readonly AstrometricFrameAssessment? assessment;
    public OrbitalPredictionRequest Request { get; }
    public OrbitalGeometryFacts Geometry { get; }
    public OrbitalTimingFacts Timing { get; }

    public OrbitalExposureQuery(OrbitalPredictionRequest request, OrbitalGeometryFacts geometry, OrbitalTimingFacts timing,
        AstrometricCalibration? calibration = null, AstrometricFrameAssessment? assessment = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(timing);
        Request = request; Geometry = geometry; Timing = timing;
        this.calibration = calibration;
        this.assessment = assessment is null ? null : AstrometricEvidenceJson.Parse(AstrometricEvidenceJson.Serialize(assessment));
        Validate();
    }

    public void Validate()
    {
        Geometry.Validate(); Timing.Validate();
        OrbitalContract.Require(Request.Interval == Timing.Exposure &&
            (!Request.Model.NominalZeroEarthOrientation || Geometry.Basis == OrbitalGeometryBasis.ConfiguredRig) &&
            (Request.Use != OrbitalPredictionUse.MeasuredAssociation || Geometry.Basis == OrbitalGeometryBasis.AcceptedMeasured), "exposure-query-interval-or-basis");
        if (Geometry.Basis == OrbitalGeometryBasis.AcceptedMeasured)
        {
            OrbitalContract.Require(calibration is not null && assessment is not null, "missing-measured-evidence");
            Geometry.ValidateMeasuredEvidence(calibration!, assessment!);
        }
    }
}

public sealed record OrbitalPassQuery(OrbitalPredictionRequest Request, double MinimumElevationDegrees)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Request);
        OrbitalContract.Require(double.IsFinite(MinimumElevationDegrees) && MinimumElevationDegrees is >= 0 and < 90, "pass-elevation");
    }
}

public enum OrbitalIllumination { Unknown, Sunlit, Penumbra, Umbra }
public enum OrbitalClipReason { WindowBoundary, Horizon, Aperture, Sensor, ProjectionFailure, SamplingBudget }
public enum OrbitalExecutionStatus { Complete, Partial, Unavailable }
public enum OrbitalPredictionReason
{
    None, ObjectMissing, SelectionWithheld, PropagationFailure, InvalidGeometry, MissingEarthOrientation,
    UnsupportedLeapCrossing, EvaluationBudget, SampleBudget, ResultBudget, Deadline, IncompleteInterval
}

public sealed record OrbitalTrackSample(DateTimeOffset Utc, AltAzPoint Horizontal, PixelPoint? Pixel,
    double? RangeKilometers, double? RangeRateKilometersPerSecond, double? AngularSpeedDegreesPerSecond,
    OrbitalIllumination Illumination);

/// <summary>Only samples within the same segment can be joined. Gaps must never be interpolated by a viewer.</summary>
public sealed record OrbitalTrackSegment(int StartSampleIndex, int SampleCount, OrbitalClipReason StartReason, OrbitalClipReason EndReason);
public sealed record OrbitalErrorComponent(double? Value, string Unit, string Reason);
public sealed record OrbitalPredictionUncertainty(OrbitalErrorComponent Orbit, OrbitalErrorComponent Clock,
    OrbitalErrorComponent Calibration, OrbitalErrorComponent Sampling);

public sealed record OrbitalProductLineage(string SnapshotManifestSha256, string NormalizedPayloadSha256,
    string RecordSha256, OrbitalObjectIdentity ObjectIdentity, string SourceDataset, string SelectionPolicySha256,
    DateTimeOffset EpochUtc, double StartEpochAgeHours, double EndEpochAgeHours, OrbitalElementFreshness Freshness,
    OrbitalSourceValidity SourceValidity, OrbitalPopulationCoverage PopulationCoverage);

public sealed record OrbitalSamplingFacts(double MaximumStepSeconds, double MaximumChordErrorPixels,
    double AchievedMaximumStepSeconds, double? AchievedChordErrorPixels);

public sealed record OrbitalExposureTrackV1(string SchemaVersion, string ProductSha256, OrbitalProductLineage Lineage,
    OrbitalUtcInterval RequestedInterval, OrbitalUtcInterval CoveredInterval, ObserverLocation Observer,
    OrbitalGeometryFacts Geometry, OrbitalTimingFacts Timing, OrbitalPredictionModelFacts Model,
    IReadOnlyList<OrbitalTrackSample> Samples, IReadOnlyList<OrbitalTrackSegment> Segments,
    OrbitalPredictionUncertainty Uncertainty, OrbitalSamplingFacts Sampling, OrbitalExecutionStatus Status, OrbitalPredictionReason Reason);

public enum OrbitalPassBoundary { ResolvedCrossing, SearchWindow, WorkLimit }
public sealed record OrbitalPassSummaryV1(string SchemaVersion, string ProductSha256, OrbitalProductLineage Lineage,
    OrbitalUtcInterval SearchInterval, OrbitalUtcInterval PassInterval, DateTimeOffset PeakUtc,
    double MaximumElevationDegrees, double MinimumElevationDegrees, OrbitalPassBoundary StartBoundary,
    OrbitalPassBoundary EndBoundary, ObserverLocation Observer, OrbitalPredictionModelFacts Model,
    OrbitalExecutionStatus Status, OrbitalPredictionReason Reason);

public sealed record OrbitalObjectFailure(OrbitalObjectIdentity ObjectIdentity, OrbitalPredictionReason Reason, string Detail);

/// <summary>Computational completion is independent of population completeness. Counts cover this query only.</summary>
public sealed class OrbitalPredictionResult<T>
{
    public OrbitalExecutionStatus Status { get; }
    public OrbitalPredictionReason Reason { get; }
    public int RequestedObjects => RequestedObjectIdentities.Count;
    public IReadOnlyList<OrbitalObjectIdentity> RequestedObjectIdentities { get; }
    public OrbitalUtcInterval RequestedInterval { get; }
    public string SnapshotManifestSha256 { get; }
    public string SelectionPolicySha256 { get; }
    public ObserverLocation Observer { get; }
    public OrbitalPredictionModelFacts Model { get; }
    public int EvaluatedObjects { get; }
    public int PropagationEvaluations { get; }
    public double ElapsedMilliseconds { get; }
    public IReadOnlyList<T> Products { get; }
    public int ReturnedProducts => Products.Count;
    public IReadOnlyList<OrbitalObjectFailure> Failures { get; }
    public OrbitalPopulationCoverage PopulationCoverage { get; }

    public OrbitalPredictionResult(OrbitalPredictionRequest request, OrbitalExecutionStatus status, OrbitalPredictionReason reason,
        int evaluatedObjects, int propagationEvaluations, double elapsedMilliseconds, IEnumerable<T> products,
        IEnumerable<OrbitalObjectFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(request);
        var populationCoverage = request.Snapshot.Manifest.Coverage;
        var bounds = request.Bounds; var requestedObjects = request.Objects.Count;
        RequestedObjectIdentities = request.Objects; RequestedInterval = request.Interval;
        SnapshotManifestSha256 = request.Snapshot.Manifest.ManifestSha256; SelectionPolicySha256 = request.SelectionPolicy.IdentitySha256;
        Observer = request.Observer; Model = request.Model;
        var items = products.Take(Math.Max(bounds.MaximumTracks, bounds.MaximumPasses) + 1).ToArray();
        var errors = failures.Take(bounds.MaximumObjects + 1).ToArray();
        OrbitalContract.Require(Enum.IsDefined(status) && Enum.IsDefined(reason) &&
            (status == OrbitalExecutionStatus.Complete ? reason == OrbitalPredictionReason.None && evaluatedObjects == requestedObjects : reason != OrbitalPredictionReason.None) &&
            requestedObjects > 0 && requestedObjects <= bounds.MaximumObjects && evaluatedObjects >= 0 && evaluatedObjects <= requestedObjects &&
            propagationEvaluations >= 0 && propagationEvaluations <= bounds.MaximumEvaluations &&
            double.IsFinite(elapsedMilliseconds) && elapsedMilliseconds >= 0 &&
            items.Length <= (typeof(T) == typeof(OrbitalExposureTrackV1) ? bounds.MaximumTracks : bounds.MaximumPasses) &&
            items.All(static p => p is not null) && (status != OrbitalExecutionStatus.Unavailable || items.Length == 0) &&
            (typeof(T) == typeof(OrbitalExposureTrackV1) || typeof(T) == typeof(OrbitalPassSummaryV1)) && errors.Length <= requestedObjects &&
            errors.All(static f => f is not null && OrbitalContract.IsObjectIdentity(f.ObjectIdentity) &&
                Enum.IsDefined(f.Reason) && f.Reason != OrbitalPredictionReason.None && OrbitalContract.Text(f.Detail, 512)), "prediction-result");
        Status = status; Reason = reason; EvaluatedObjects = evaluatedObjects;
        PropagationEvaluations = propagationEvaluations; ElapsedMilliseconds = elapsedMilliseconds;
        var frozen = items.Select(FreezeProduct).ToArray();
        var requestedIds = request.Objects.ToHashSet();
        var records = request.Snapshot.Records.ToDictionary(static r => r.RecordSha256, StringComparer.Ordinal);
        var choices = new Dictionary<OrbitalObjectIdentity, OrbitalRecordSelection>();
        var returnedIds = new HashSet<OrbitalObjectIdentity>();
        var productIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in frozen)
        {
            var (lineage, interval, observer, model, identity) = item switch
            {
                OrbitalExposureTrackV1 t => (t.Lineage, t.RequestedInterval, t.Observer, t.Model, t.ProductSha256),
                OrbitalPassSummaryV1 p => (p.Lineage, p.SearchInterval, p.Observer, p.Model, p.ProductSha256),
                _ => throw new ArgumentException("Unsupported orbital product type.")
            };
            OrbitalContract.Require(requestedIds.Contains(lineage.ObjectIdentity) && productIds.Add(identity) &&
                records.TryGetValue(lineage.RecordSha256, out var record) && record.ObjectIdentity == lineage.ObjectIdentity &&
                record.SourceDataset == lineage.SourceDataset && record.EpochUtc == lineage.EpochUtc &&
                lineage.SnapshotManifestSha256 == SnapshotManifestSha256 && lineage.NormalizedPayloadSha256 == request.Snapshot.Manifest.NormalizedPayloadSha256 &&
                lineage.SelectionPolicySha256 == SelectionPolicySha256 && interval == RequestedInterval && observer == Observer && model == Model &&
                OrbitalContract.Hash(lineage.PopulationCoverage) == OrbitalContract.Hash(populationCoverage) &&
                (lineage.Freshness != OrbitalElementFreshness.Stale || request.Use == OrbitalPredictionUse.Diagnostic && request.SelectionPolicy.AllowStaleDiagnostics), "result-query-provenance");
            if (!choices.TryGetValue(lineage.ObjectIdentity, out var choice))
                choices.Add(lineage.ObjectIdentity, choice = OrbitalRecordSelector.Select(request.Snapshot, lineage.ObjectIdentity, RequestedInterval, request.SelectionPolicy));
            OrbitalContract.Require(choice.SelectedRecord?.RecordSha256 == lineage.RecordSha256 &&
                choice.Candidates.Single(c => c.RecordSha256 == lineage.RecordSha256).SourceValidity == lineage.SourceValidity, "result-record-selection");
            if (typeof(T) == typeof(OrbitalExposureTrackV1)) OrbitalContract.Require(returnedIds.Add(lineage.ObjectIdentity), "duplicate-object-track");
            else returnedIds.Add(lineage.ObjectIdentity);
        }
        OrbitalContract.Require(returnedIds.Count <= evaluatedObjects && errors.Select(static f => f.ObjectIdentity).Distinct().Count() == errors.Length &&
            errors.All(f => requestedIds.Contains(f.ObjectIdentity)), "result-object-count-or-population");
        OrbitalContract.Require(frozen.OfType<OrbitalExposureTrackV1>().Sum(static t => (long)t.Samples.Count) <= bounds.MaximumSamples &&
            (status != OrbitalExecutionStatus.Complete || !frozen.OfType<OrbitalExposureTrackV1>().Any(static p => p.Status != OrbitalExecutionStatus.Complete) &&
                !frozen.OfType<OrbitalPassSummaryV1>().Any(static p => p.Status != OrbitalExecutionStatus.Complete) &&
                !errors.Any(static f => f.Reason is OrbitalPredictionReason.EvaluationBudget or OrbitalPredictionReason.SampleBudget or
                    OrbitalPredictionReason.ResultBudget or OrbitalPredictionReason.Deadline or OrbitalPredictionReason.IncompleteInterval)), "partial-result-status-or-sample-budget");
        Products = Array.AsReadOnly(frozen); Failures = Array.AsReadOnly(errors);
        PopulationCoverage = populationCoverage with { KnownExclusions = Array.AsReadOnly(populationCoverage.KnownExclusions.ToArray()) };
    }

    private static T FreezeProduct(T item)
    {
        if (item is OrbitalExposureTrackV1 track)
        {
            var copy = OrbitalPredictionJson.CreateTrack(track);
            OrbitalContract.Require(copy.ProductSha256 == track.ProductSha256, "track-identity");
            return (T)(object)copy;
        }
        if (item is OrbitalPassSummaryV1 pass)
        {
            var copy = OrbitalPredictionJson.CreatePass(pass);
            OrbitalContract.Require(copy.ProductSha256 == pass.ProductSha256, "pass-identity");
            return (T)(object)copy;
        }
        throw new ArgumentException("Unsupported orbital product type.");
    }
}

public interface IOrbitalPredictionEngine
{
    ValueTask<OrbitalPredictionResult<OrbitalExposureTrackV1>> PredictExposureAsync(OrbitalExposureQuery query, CancellationToken cancellationToken);
    ValueTask<OrbitalPredictionResult<OrbitalPassSummaryV1>> FindPassesAsync(OrbitalPassQuery query, CancellationToken cancellationToken);
}

/// <summary>Small versioned product contracts; no propagation, projection or visibility estimator.</summary>
public static class OrbitalPredictionJson
{
    public const string TrackSchemaVersion = "hvo-orbital-exposure-track-v1";
    public const string PassSchemaVersion = "hvo-orbital-pass-v1";
    public const int MaximumProductBytes = 16 * 1024 * 1024;

    public static OrbitalExposureTrackV1 CreateTrack(OrbitalExposureTrackV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Samples); ArgumentNullException.ThrowIfNull(value.Segments);
        var copy = value with
        {
            ProductSha256 = string.Empty,
            Lineage = CopyLineage(value.Lineage),
            Samples = Array.AsReadOnly(value.Samples.Take(1_000_001).ToArray()),
            Segments = Array.AsReadOnly(value.Segments.Take(1_000_001).ToArray())
        };
        ValidateTrackContent(copy);
        copy = copy with { ProductSha256 = TrackIdentity(copy) };
        OrbitalContract.Require(OrbitalContract.SerializeCanonical(copy).Length <= MaximumProductBytes, "product-byte-bound");
        return copy;
    }

    public static OrbitalPassSummaryV1 CreatePass(OrbitalPassSummaryV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var copy = value with { ProductSha256 = string.Empty, Lineage = CopyLineage(value.Lineage) };
        ValidatePassContent(copy);
        return copy with { ProductSha256 = PassIdentity(copy) };
    }

    public static byte[] SerializeTrack(OrbitalExposureTrackV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var checkedValue = CreateTrack(value);
        OrbitalContract.Require(value.ProductSha256 == checkedValue.ProductSha256, "track-identity");
        return OrbitalContract.SerializeCanonical(checkedValue);
    }
    public static byte[] SerializePass(OrbitalPassSummaryV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var checkedValue = CreatePass(value);
        OrbitalContract.Require(value.ProductSha256 == checkedValue.ProductSha256, "pass-identity");
        return OrbitalContract.SerializeCanonical(checkedValue);
    }
    public static OrbitalExposureTrackV1 ParseTrack(ReadOnlySpan<byte> json)
    {
        OrbitalContract.Require(json.Length is > 0 and <= MaximumProductBytes, "product-byte-bound");
        var value = JsonSerializer.Deserialize<OrbitalExposureTrackV1>(json, OrbitalContract.JsonOptions) ?? throw new ArgumentException("missing-track");
        OrbitalContract.Require(json.SequenceEqual(SerializeTrack(value)), "noncanonical-track");
        return CreateTrack(value);
    }
    public static OrbitalPassSummaryV1 ParsePass(ReadOnlySpan<byte> json)
    {
        OrbitalContract.Require(json.Length is > 0 and <= MaximumProductBytes, "product-byte-bound");
        var value = JsonSerializer.Deserialize<OrbitalPassSummaryV1>(json, OrbitalContract.JsonOptions) ?? throw new ArgumentException("missing-pass");
        OrbitalContract.Require(json.SequenceEqual(SerializePass(value)), "noncanonical-pass");
        return CreatePass(value);
    }

    private static string TrackIdentity(OrbitalExposureTrackV1 v) => OrbitalContract.Hash(new
    {
        v.SchemaVersion, v.Lineage, v.RequestedInterval, v.CoveredInterval, v.Observer, v.Geometry, v.Timing,
        v.Model, v.Samples, v.Segments, v.Uncertainty, v.Sampling, v.Status, v.Reason
    });
    private static string PassIdentity(OrbitalPassSummaryV1 v) => OrbitalContract.Hash(new
    {
        v.SchemaVersion, v.Lineage, v.SearchInterval, v.PassInterval, v.PeakUtc, v.MaximumElevationDegrees,
        v.MinimumElevationDegrees, v.StartBoundary, v.EndBoundary, v.Observer, v.Model, v.Status, v.Reason
    });
    private static OrbitalProductLineage CopyLineage(OrbitalProductLineage value)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.PopulationCoverage);
        ArgumentNullException.ThrowIfNull(value.PopulationCoverage.KnownExclusions);
        return value with { PopulationCoverage = value.PopulationCoverage with { KnownExclusions = Array.AsReadOnly(value.PopulationCoverage.KnownExclusions.ToArray()) } };
    }
    private static void ValidateLineage(OrbitalProductLineage v, OrbitalUtcInterval interval)
    {
        OrbitalContract.Require(OrbitalContract.IsSha256(v.SnapshotManifestSha256) && OrbitalContract.IsSha256(v.NormalizedPayloadSha256) &&
            OrbitalContract.IsSha256(v.RecordSha256) && OrbitalContract.IsSha256(v.SelectionPolicySha256) &&
            OrbitalContract.IsObjectIdentity(v.ObjectIdentity) && OrbitalContract.Dataset(v.SourceDataset) && OrbitalContract.Utc(v.EpochUtc) &&
            double.IsFinite(v.StartEpochAgeHours) && double.IsFinite(v.EndEpochAgeHours) &&
            v.StartEpochAgeHours == (interval.StartUtc - v.EpochUtc).TotalHours && v.EndEpochAgeHours == (interval.EndUtc - v.EpochUtc).TotalHours &&
            v.StartEpochAgeHours >= -24 && v.EndEpochAgeHours <= 168 &&
            v.Freshness == (Math.Max(Math.Abs(v.StartEpochAgeHours), Math.Abs(v.EndEpochAgeHours)) > 48 ? OrbitalElementFreshness.Stale : OrbitalElementFreshness.Fresh) &&
            Enum.IsDefined(v.SourceValidity) &&
            OrbitalContract.Text(v.PopulationCoverage.RequestedPopulation) && Enum.IsDefined(v.PopulationCoverage.Completeness) &&
            v.PopulationCoverage.KnownExclusions.Count <= 64 && v.PopulationCoverage.KnownExclusions.All(static x => OrbitalContract.Text(x)), "product-lineage");
    }
    private static void ValidateStatus(OrbitalExecutionStatus status, OrbitalPredictionReason reason)
        => OrbitalContract.Require(Enum.IsDefined(status) && Enum.IsDefined(reason) &&
            (status == OrbitalExecutionStatus.Complete ? reason == OrbitalPredictionReason.None : reason != OrbitalPredictionReason.None), "product-status");
    private static bool Nonnegative(double? value) => value is null || double.IsFinite(value.Value) && value >= 0;
    private static void ValidateTrackContent(OrbitalExposureTrackV1 v)
    {
        ArgumentNullException.ThrowIfNull(v.RequestedInterval); ArgumentNullException.ThrowIfNull(v.CoveredInterval);
        ArgumentNullException.ThrowIfNull(v.Geometry); ArgumentNullException.ThrowIfNull(v.Timing); ArgumentNullException.ThrowIfNull(v.Model);
        ArgumentNullException.ThrowIfNull(v.Uncertainty); ArgumentNullException.ThrowIfNull(v.Sampling);
        v.RequestedInterval.Validate(); v.CoveredInterval.Validate(); v.Observer.Validate(); v.Geometry.Validate(); v.Timing.Validate(); v.Model.Validate();
        OrbitalPredictionRequest.ValidateModelInterval(v.Model, v.RequestedInterval);
        ValidateLineage(v.Lineage, v.RequestedInterval); ValidateStatus(v.Status, v.Reason);
        OrbitalContract.Require(v.SchemaVersion == TrackSchemaVersion && v.Timing.Exposure == v.RequestedInterval &&
            v.CoveredInterval.StartUtc >= v.RequestedInterval.StartUtc && v.CoveredInterval.EndUtc <= v.RequestedInterval.EndUtc &&
            v.Samples.Count is > 0 and <= 1_000_000 && v.Segments.Count <= v.Samples.Count &&
            v.Status != OrbitalExecutionStatus.Unavailable && (v.Status != OrbitalExecutionStatus.Complete || v.CoveredInterval == v.RequestedInterval), "track-interval-or-bound");
        OrbitalContract.Require(!v.Model.NominalZeroEarthOrientation || v.Geometry.Basis == OrbitalGeometryBasis.ConfiguredRig, "zero-eop-measured-geometry");
        var sf = v.Sampling;
        OrbitalContract.Require(double.IsFinite(sf.MaximumStepSeconds) && sf.MaximumStepSeconds is > 0 and <= 1 &&
            double.IsFinite(sf.MaximumChordErrorPixels) && sf.MaximumChordErrorPixels is > 0 and <= .25 &&
            double.IsFinite(sf.AchievedMaximumStepSeconds) && sf.AchievedMaximumStepSeconds >= 0 && Nonnegative(sf.AchievedChordErrorPixels) &&
            (v.Status != OrbitalExecutionStatus.Complete || sf.AchievedMaximumStepSeconds <= sf.MaximumStepSeconds &&
                sf.AchievedChordErrorPixels is not null && sf.AchievedChordErrorPixels <= sf.MaximumChordErrorPixels), "sampling-tolerance");
        DateTimeOffset? last = null;
        var maximumStep = 0d;
        foreach (var sample in v.Samples)
        {
            OrbitalContract.Require(sample is not null && OrbitalContract.Utc(sample.Utc) && (last is null || sample.Utc > last) &&
                sample.Utc >= v.CoveredInterval.StartUtc && sample.Utc <= v.CoveredInterval.EndUtc &&
                double.IsFinite(sample.Horizontal.AltitudeDegrees) && sample.Horizontal.AltitudeDegrees is >= -90 and <= 90 &&
                double.IsFinite(sample.Horizontal.AzimuthDegrees) && sample.Horizontal.AzimuthDegrees is >= 0 and < 360 &&
                (sample.Pixel is null || double.IsFinite(sample.Pixel.Value.X) && double.IsFinite(sample.Pixel.Value.Y) &&
                    sample.Pixel.Value.X >= 0 && sample.Pixel.Value.X <= v.Geometry.ImageTransform.OutputWidthPixels &&
                    sample.Pixel.Value.Y >= 0 && sample.Pixel.Value.Y <= v.Geometry.ImageTransform.OutputHeightPixels) &&
                (sample.RangeKilometers is null || double.IsFinite(sample.RangeKilometers.Value) && sample.RangeKilometers > 0) &&
                (sample.RangeRateKilometersPerSecond is null || double.IsFinite(sample.RangeRateKilometersPerSecond.Value)) &&
                Nonnegative(sample.AngularSpeedDegreesPerSecond) && Enum.IsDefined(sample.Illumination) &&
                (v.Model.SolarShadowModelSha256 is not null || sample.Illumination == OrbitalIllumination.Unknown), "track-sample");
            if (last is not null) maximumStep = Math.Max(maximumStep, (sample!.Utc - last.Value).TotalSeconds);
            last = sample!.Utc;
        }
        OrbitalContract.Require(v.CoveredInterval.StartUtc == v.Samples[0].Utc && v.CoveredInterval.EndUtc == v.Samples[^1].Utc, "track-coverage");
        OrbitalContract.Require(maximumStep == sf.AchievedMaximumStepSeconds && v.Uncertainty.Sampling.Value == sf.AchievedChordErrorPixels, "sampling-achieved-facts");
        var end = 0;
        foreach (var segment in v.Segments)
        {
            OrbitalContract.Require(segment is not null && segment.StartSampleIndex >= end && segment.SampleCount > 0 &&
                (long)segment.StartSampleIndex + segment.SampleCount <= v.Samples.Count && Enum.IsDefined(segment.StartReason) && Enum.IsDefined(segment.EndReason), "track-segment");
            end = segment!.StartSampleIndex + segment.SampleCount;
            OrbitalContract.Require(v.Samples.Skip(segment.StartSampleIndex).Take(segment.SampleCount).All(static s => s.Pixel is not null), "segment-crosses-projection-gap");
        }
        OrbitalContract.Require(v.Uncertainty.Orbit is { Unit: "degrees" } && v.Uncertainty.Clock is { Unit: "seconds" } &&
            v.Uncertainty.Calibration is { Unit: "pixels" } && v.Uncertainty.Sampling is { Unit: "pixels" }, "uncertainty-units");
        foreach (var error in new[] { v.Uncertainty.Orbit, v.Uncertainty.Clock, v.Uncertainty.Calibration, v.Uncertainty.Sampling })
            OrbitalContract.Require(error is not null && Nonnegative(error.Value) && OrbitalContract.Text(error.Unit, 64) && OrbitalContract.Text(error.Reason), "uncertainty-component");
    }
    private static void ValidatePassContent(OrbitalPassSummaryV1 v)
    {
        ArgumentNullException.ThrowIfNull(v.SearchInterval); ArgumentNullException.ThrowIfNull(v.PassInterval); ArgumentNullException.ThrowIfNull(v.Model);
        v.SearchInterval.Validate(); v.PassInterval.Validate(); v.Observer.Validate(); v.Model.Validate();
        OrbitalPredictionRequest.ValidateModelInterval(v.Model, v.SearchInterval);
        ValidateLineage(v.Lineage, v.SearchInterval); ValidateStatus(v.Status, v.Reason);
        OrbitalContract.Require(v.SchemaVersion == PassSchemaVersion && v.PassInterval.StartUtc >= v.SearchInterval.StartUtc &&
            v.PassInterval.EndUtc <= v.SearchInterval.EndUtc && OrbitalContract.Utc(v.PeakUtc) &&
            v.PeakUtc >= v.PassInterval.StartUtc && v.PeakUtc <= v.PassInterval.EndUtc &&
            double.IsFinite(v.MaximumElevationDegrees) && v.MaximumElevationDegrees is >= 0 and <= 90 &&
            double.IsFinite(v.MinimumElevationDegrees) && v.MinimumElevationDegrees >= 0 && v.MinimumElevationDegrees <= v.MaximumElevationDegrees &&
            (v.StartBoundary != OrbitalPassBoundary.SearchWindow || v.PassInterval.StartUtc == v.SearchInterval.StartUtc) &&
            (v.EndBoundary != OrbitalPassBoundary.SearchWindow || v.PassInterval.EndUtc == v.SearchInterval.EndUtc) &&
            Enum.IsDefined(v.StartBoundary) && Enum.IsDefined(v.EndBoundary) && v.Status != OrbitalExecutionStatus.Unavailable &&
            (v.Status != OrbitalExecutionStatus.Complete || v.StartBoundary != OrbitalPassBoundary.WorkLimit && v.EndBoundary != OrbitalPassBoundary.WorkLimit), "pass-boundaries");
    }
}
