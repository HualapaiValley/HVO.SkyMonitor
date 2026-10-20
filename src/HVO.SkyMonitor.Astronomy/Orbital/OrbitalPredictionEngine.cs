namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Bounded host-neutral numerical predictions from pre-resolved immutable snapshots and time data.
/// No network, persistence, ambient wall clock, physical visibility or image classification.
/// </summary>
public sealed partial class OrbitalPredictionEngine : IOrbitalPredictionEngine, IOrbitalPassArcEngine
{
    private readonly OrbitalTimeData timeData;
    private readonly TimeProvider timeProvider;

    public OrbitalPredictionEngine(OrbitalTimeData timeData, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(timeData);
        this.timeData = timeData;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<OrbitalPredictionResult<OrbitalExposureTrackV1>> PredictExposureAsync(
        OrbitalExposureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return ValueTask.FromResult(Run<OrbitalExposureTrackV1>(query.Request, query.Geometry.Basis,
            (_, lineage, evaluator, budget) =>
            {
                // Reserve before work so a useful partial prefix survives an exhausted budget.
                // Completely excluded objects release this unused slot for the next object.
                if (!budget.TryTrack()) return new ObjectOutput<OrbitalExposureTrackV1>([], budget.Reason, "track-result-limit");
                var sampled = OrbitalExposureSampler.Sample(query, lineage, evaluator.Evaluate, budget);
                if (sampled.Track is null) budget.ReleaseUnusedTrack();
                return new(sampled.Track is { } track ? [track] : [], sampled.Reason, sampled.Detail);
            },
            (status, reason, objects, evaluations, elapsed, products, failures, diagnostics) =>
                new(query, status, reason, objects, evaluations, elapsed, products, failures, diagnostics), cancellationToken));
    }

    public ValueTask<OrbitalPredictionResult<OrbitalPassSummaryV1>> FindPassesAsync(
        OrbitalPassQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return ValueTask.FromResult(Run<OrbitalPassSummaryV1>(query.Request, OrbitalGeometryBasis.ConfiguredRig,
            (record, lineage, evaluator, budget) =>
            {
                var found = OrbitalPassSearch.Search(query, lineage, record.Elements, evaluator.Evaluate, budget);
                return new ObjectOutput<OrbitalPassSummaryV1>(found.Passes, found.Reason, found.Detail);
            },
            (status, reason, objects, evaluations, elapsed, products, failures, diagnostics) =>
                new(query, status, reason, objects, evaluations, elapsed, products, failures, diagnostics), cancellationToken));
    }

    private OrbitalPredictionResult<T> Run<T>(OrbitalPredictionRequest request, OrbitalGeometryBasis geometryBasis,
        Func<OrbitalRecordV1, OrbitalProductLineage, OrbitalObjectEvaluator, OrbitalWorkBudget, ObjectOutput<T>> produce,
        Func<OrbitalExecutionStatus, OrbitalPredictionReason, int, int, double, IEnumerable<T>, IEnumerable<OrbitalObjectFailure>, OrbitalWorkDiagnostics, OrbitalPredictionResult<T>> finish,
        CancellationToken cancellationToken)
    {
        var budget = new OrbitalWorkBudget(request.Bounds, cancellationToken, timeProvider);
        var products = new List<T>();
        var failures = new List<OrbitalObjectFailure>();
        var evaluated = 0;
        var globalReason = OrbitalPredictionReason.None;
        // Cancellation is checked before model validation and again before/after result freezing.
        budget.CanContinue();
        if (request.Model.Propagator != OrbitalPredictionModel.Propagator || request.Model.SolarShadowModelSha256 is not null)
        {
            globalReason = OrbitalPredictionReason.PropagationFailure;
            failures.Add(new(request.Objects[0], globalReason, "unsupported-propagator-or-solar-model"));
            return Finish();
        }
        try { timeData.ValidateModel(request.Model, request.Interval, request.Use, geometryBasis); }
        catch (ArgumentException exception)
        {
            globalReason = OrbitalObjectEvaluator.TimeFailure(exception);
            failures.Add(new(request.Objects[0], globalReason, BoundedDetail(exception.Message)));
            return Finish();
        }
        foreach (var identity in request.Objects)
        {
            if (!budget.CanContinue()) break;
            evaluated++;
            budget.Work.VisitObject();
            var selection = OrbitalRecordSelector.Select(request.Snapshot, identity, request.Interval, request.SelectionPolicy);
            if (selection.SelectedRecord is not { } record)
            {
                var missing = selection.Eligibility == OrbitalSelectionEligibility.MissingObject;
                failures.Add(new(identity, missing ? OrbitalPredictionReason.ObjectMissing : OrbitalPredictionReason.SelectionWithheld,
                    $"selection-{selection.Eligibility}"));
                continue;
            }
            budget.Work.SelectObject();
            var evaluator = OrbitalObjectEvaluator.Create(request, record, timeData, budget, out var reason, out var detail);
            if (evaluator is null)
            {
                failures.Add(new(identity, reason, BoundedDetail(detail)));
                continue;
            }
            var candidate = selection.Candidates.Single(c => c.RecordSha256 == record.RecordSha256);
            var lineage = new OrbitalProductLineage(request.Snapshot.Manifest.ManifestSha256, request.Snapshot.Manifest.NormalizedPayloadSha256,
                record.RecordSha256, identity, record.SourceDataset, request.SelectionPolicy.IdentitySha256, record.EpochUtc,
                candidate.StartEpochAgeHours, candidate.EndEpochAgeHours, candidate.Freshness, candidate.SourceValidity,
                request.Snapshot.Manifest.Coverage);
            var output = produce(record, lineage, evaluator, budget);
            products.AddRange(output.Products);
            // A point failure carries more specific evidence than a downstream unresolved interval.
            reason = evaluator.FailureReason != OrbitalPredictionReason.None ? evaluator.FailureReason : output.Reason;
            if (reason != OrbitalPredictionReason.None)
                failures.Add(new(identity, reason, BoundedDetail(evaluator.FailureReason != OrbitalPredictionReason.None ? evaluator.FailureDetail : output.Detail)));
        }
        return Finish();

        OrbitalPredictionResult<T> Finish()
        {
            budget.CanContinue();
            var elapsed = budget.ElapsedMilliseconds;
            var reason = budget.Reason != OrbitalPredictionReason.None ? budget.Reason : globalReason;
            if (reason == OrbitalPredictionReason.None && failures.Count > 0) reason = failures[0].Reason;
            if (reason == OrbitalPredictionReason.None && evaluated < request.Objects.Count) reason = OrbitalPredictionReason.IncompleteInterval;
            var status = reason == OrbitalPredictionReason.None ? OrbitalExecutionStatus.Complete :
                products.Count == 0 ? OrbitalExecutionStatus.Unavailable : OrbitalExecutionStatus.Partial;
            var diagnostics = budget.Work.Snapshot(request.Objects.Count);
            var result = finish(status, reason, evaluated, budget.Evaluations, elapsed, products, failures, diagnostics);
            // Contract validation, defensive freezing and hashing are charged to the deadline too.
            budget.CanContinue();
            if (budget.Reason == OrbitalPredictionReason.Deadline && result.Reason != OrbitalPredictionReason.Deadline)
                result = finish(products.Count == 0 ? OrbitalExecutionStatus.Unavailable : OrbitalExecutionStatus.Partial,
                    OrbitalPredictionReason.Deadline, evaluated, budget.Evaluations, budget.ElapsedMilliseconds, products, failures, diagnostics);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static string BoundedDetail(string detail) => detail.Length <= 512 ? detail : detail[..512];
    private sealed record ObjectOutput<T>(IReadOnlyList<T> Products, OrbitalPredictionReason Reason, string Detail);
}
