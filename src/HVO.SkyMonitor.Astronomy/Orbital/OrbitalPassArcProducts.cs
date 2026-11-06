namespace HVO.SkyMonitor.Astronomy;

public sealed record OrbitalPassArcBounds(int MaximumEvaluations, int MaximumSamples,
    double MaximumElapsedMilliseconds, double MaximumSampleStepSeconds, double MaximumAngularErrorDegrees)
{
    public void Validate() => OrbitalContract.Require(MaximumEvaluations is > 0 and <= 10_000_000 &&
        MaximumSamples is > 0 and <= 1_000_000 &&
        double.IsFinite(MaximumElapsedMilliseconds) && MaximumElapsedMilliseconds is > 0 and <= 600_000 &&
        double.IsFinite(MaximumSampleStepSeconds) && MaximumSampleStepSeconds is > 0 and <= 1 &&
        double.IsFinite(MaximumAngularErrorDegrees) && MaximumAngularErrorDegrees is > 0 and <= .25, "arc-work-bounds");
}

/// <summary>Immutable original-search binding. Selection is validated once, over the original search interval.</summary>
public sealed class OrbitalPassArcQuery
{
    public const string SchemaVersion = "hvo-orbital-pass-arc-query-v1";
    public const string SearchIdentityVersion = "hvo-orbital-pass-search-identity-v1";
    public OrbitalPassQuery SearchQuery { get; }
    public OrbitalPassSummaryV1 Pass { get; }
    public OrbitalPassArcBounds Bounds { get; }
    public string SearchQuerySha256 { get; }
    public string IdentitySha256 { get; }
    internal OrbitalRecordV1 SelectedRecord { get; }

    public OrbitalPassArcQuery(OrbitalPassQuery searchQuery, OrbitalPassSummaryV1 pass, OrbitalPassArcBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(searchQuery); ArgumentNullException.ThrowIfNull(pass); ArgumentNullException.ThrowIfNull(bounds);
        searchQuery.Validate(); bounds.Validate();
        var frozen = OrbitalPredictionJson.CreatePass(pass);
        OrbitalContract.Require(frozen.ProductSha256 == pass.ProductSha256, "arc-parent-identity");
        var request = searchQuery.Request;
        var lineage = frozen.Lineage;
        OrbitalContract.Require(request.Interval == frozen.SearchInterval && request.Observer == frozen.Observer &&
            request.Model == frozen.Model && searchQuery.MinimumElevationDegrees == frozen.MinimumElevationDegrees &&
            request.Objects.Contains(lineage.ObjectIdentity) &&
            request.Snapshot.Manifest.ManifestSha256 == lineage.SnapshotManifestSha256 &&
            request.Snapshot.Manifest.NormalizedPayloadSha256 == lineage.NormalizedPayloadSha256 &&
            request.SelectionPolicy.IdentitySha256 == lineage.SelectionPolicySha256 &&
            OrbitalContract.Hash(request.Snapshot.Manifest.Coverage) == OrbitalContract.Hash(lineage.PopulationCoverage) &&
            (lineage.Freshness != OrbitalElementFreshness.Stale || request.Use == OrbitalPredictionUse.Diagnostic &&
                request.SelectionPolicy.AllowStaleDiagnostics), "arc-search-parent-provenance");
        var choice = OrbitalRecordSelector.Select(request.Snapshot, lineage.ObjectIdentity, request.Interval, request.SelectionPolicy);
        OrbitalContract.Require(choice.SelectedRecord is { } selected && selected.RecordSha256 == lineage.RecordSha256 &&
            selected.SourceDataset == lineage.SourceDataset && selected.EpochUtc == lineage.EpochUtc &&
            choice.Candidates.Single(c => c.RecordSha256 == lineage.RecordSha256).SourceValidity == lineage.SourceValidity,
            "arc-original-record-selection");
        SelectedRecord = choice.SelectedRecord!;
        SearchQuery = searchQuery; Pass = frozen; Bounds = bounds;
        SearchQuerySha256 = OrbitalContract.Hash(SearchIdentityContent(searchQuery));
        IdentitySha256 = OrbitalContract.Hash(new { SchemaVersion, SearchQuerySha256, ParentPassSha256 = Pass.ProductSha256, Bounds });
    }

    public void Validate() => Bounds.Validate(); // All other inputs were frozen/validated at construction.

    internal static object SearchIdentityContent(OrbitalPassQuery query) => new
    {
        SchemaVersion = SearchIdentityVersion,
        query.Request.Use,
        SnapshotManifestSha256 = query.Request.Snapshot.Manifest.ManifestSha256,
        query.Request.Snapshot.Manifest.NormalizedPayloadSha256,
        SelectionPolicySha256 = query.Request.SelectionPolicy.IdentitySha256,
        query.Request.Interval,
        query.Request.Objects,
        query.Request.Observer,
        query.Request.Model,
        query.Request.Bounds,
        query.MinimumElevationDegrees
    };
}

/// <summary>Only consecutive samples in the same segment have a resolved interpolation estimate.</summary>
public sealed record OrbitalSkyArcSegmentV1(int StartSampleIndex, int SampleCount);
public sealed record OrbitalPassArcAnchorsV1(int? StartSampleIndex, int? PeakSampleIndex, int? EndSampleIndex);
public sealed record OrbitalAngularSamplingFactsV1(double MaximumStepSeconds, double MaximumAngularErrorDegrees,
    double AchievedMaximumStepSeconds, double? AchievedEstimatedAngularErrorDegrees,
    string InterpolationVersion, string EstimatorVersion);

/// <summary>Unprojected geometry over one immutable parent pass. This product makes no optical visibility claim.</summary>
public sealed record OrbitalPassArcV1(string SchemaVersion, string ProductSha256, string QuerySha256,
    string SearchQuerySha256, OrbitalPassSummaryV1 Pass, OrbitalUtcInterval CoveredInterval,
    IReadOnlyList<OrbitalTrackSample> Samples, IReadOnlyList<OrbitalSkyArcSegmentV1> Segments,
    OrbitalPassArcAnchorsV1 Anchors, OrbitalAngularSamplingFactsV1 Sampling, OrbitalErrorComponent OrbitUncertainty,
    OrbitalExecutionStatus Status, OrbitalPredictionReason Reason);

public interface IOrbitalPassArcEngine
{
    ValueTask<OrbitalPredictionResult<OrbitalPassArcV1>> PredictPassArcAsync(OrbitalPassArcQuery query, CancellationToken cancellationToken);
}

public sealed partial class OrbitalPredictionResult<T>
{
    /// <summary>One-object route, retaining original search lineage without narrowing/reselecting its record.</summary>
    public OrbitalPredictionResult(OrbitalPassArcQuery query, OrbitalExecutionStatus status, OrbitalPredictionReason reason,
        int evaluatedObjects, int propagationEvaluations, double elapsedMilliseconds, IEnumerable<T> products,
        IEnumerable<OrbitalObjectFailure> failures)
        : this(query, status, reason, evaluatedObjects, propagationEvaluations, elapsedMilliseconds, products, failures, null)
    { }

    internal OrbitalPredictionResult(OrbitalPassArcQuery query, OrbitalExecutionStatus status, OrbitalPredictionReason reason,
        int evaluatedObjects, int propagationEvaluations, double elapsedMilliseconds, IEnumerable<T> products,
        IEnumerable<OrbitalObjectFailure> failures, Action? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(failures);
        query.Validate();
        OrbitalContract.Require(typeof(T) == typeof(OrbitalPassArcV1), "result-query-product-kind");
        var items = products.Take(2).ToArray(); var errors = failures.Take(2).ToArray();
        var request = query.SearchQuery.Request; var bounds = query.Bounds;
        var identity = query.Pass.Lineage.ObjectIdentity;
        OrbitalContract.Require(Enum.IsDefined(status) && Enum.IsDefined(reason) &&
            (status == OrbitalExecutionStatus.Complete ? reason == OrbitalPredictionReason.None && evaluatedObjects == 1 && items.Length == 1 && errors.Length == 0 :
                reason != OrbitalPredictionReason.None) && evaluatedObjects is >= 0 and <= 1 &&
            propagationEvaluations >= 0 && propagationEvaluations <= bounds.MaximumEvaluations &&
            double.IsFinite(elapsedMilliseconds) && elapsedMilliseconds >= 0 &&
            (elapsedMilliseconds <= bounds.MaximumElapsedMilliseconds || status != OrbitalExecutionStatus.Complete && reason == OrbitalPredictionReason.Deadline) &&
            items.Length <= evaluatedObjects && (status != OrbitalExecutionStatus.Unavailable || items.Length == 0) &&
            (status != OrbitalExecutionStatus.Partial || items.Length == 1) && errors.Length <= 1 &&
            errors.All(f => f is not null && f.ObjectIdentity == identity && Enum.IsDefined(f.Reason) &&
                f.Reason != OrbitalPredictionReason.None && OrbitalContract.Text(f.Detail, 512)), "arc-result");
        var frozen = items.Select(item =>
        {
            OrbitalContract.Require(item is OrbitalPassArcV1, "result-query-product-kind");
            var arc = (OrbitalPassArcV1)(object)item!;
            var copy = OrbitalPassArcJson.Create(arc, checkpoint);
            OrbitalContract.Require(copy.ProductSha256 == arc.ProductSha256 && copy.QuerySha256 == query.IdentitySha256 &&
                copy.SearchQuerySha256 == query.SearchQuerySha256 && copy.Pass.ProductSha256 == query.Pass.ProductSha256 &&
                copy.Samples.Count <= bounds.MaximumSamples && copy.Sampling.MaximumStepSeconds == bounds.MaximumSampleStepSeconds &&
                copy.Sampling.MaximumAngularErrorDegrees == bounds.MaximumAngularErrorDegrees &&
                (status != OrbitalExecutionStatus.Complete || copy.Status == OrbitalExecutionStatus.Complete), "arc-result-query");
            return (T)(object)copy;
        }).ToArray();
        Status = status; Reason = reason; RequestedObjectIdentities = Array.AsReadOnly(new[] { identity });
        RequestedInterval = query.Pass.PassInterval;
        SnapshotManifestSha256 = request.Snapshot.Manifest.ManifestSha256;
        SelectionPolicySha256 = request.SelectionPolicy.IdentitySha256; Observer = request.Observer; Model = request.Model;
        EvaluatedObjects = evaluatedObjects; PropagationEvaluations = propagationEvaluations; ElapsedMilliseconds = elapsedMilliseconds;
        Products = Array.AsReadOnly(frozen); Failures = Array.AsReadOnly(errors);
        var coverage = request.Snapshot.Manifest.Coverage;
        PopulationCoverage = coverage with { KnownExclusions = Array.AsReadOnly(coverage.KnownExclusions.ToArray()) };
    }
}
