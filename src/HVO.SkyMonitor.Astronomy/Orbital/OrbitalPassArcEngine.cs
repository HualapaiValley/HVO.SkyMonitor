namespace HVO.SkyMonitor.Astronomy;

public sealed partial class OrbitalPredictionEngine
{
    public ValueTask<OrbitalPredictionResult<OrbitalPassArcV1>> PredictPassArcAsync(
        OrbitalPassArcQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var budget = new OrbitalWorkBudget(query.Bounds, cancellationToken, timeProvider);
        var request = query.SearchQuery.Request;
        var reason = OrbitalPredictionReason.None; var detail = "none";
        var evaluated = 0;
        OrbitalPassArcV1? arc = null;
        budget.CanContinue();
        if (request.Model.NominalZeroEarthOrientation || timeData.NominalZeroEarthOrientation)
        {
            reason = OrbitalPredictionReason.MissingEarthOrientation; detail = "arc-requires-resolved-non-nominal-eop";
            return ValueTask.FromResult(Finish());
        }
        if (request.Model.Propagator != OrbitalPredictionModel.Propagator || request.Model.SolarShadowModelSha256 is not null)
        {
            reason = OrbitalPredictionReason.PropagationFailure; detail = "unsupported-propagator-or-solar-model";
            return ValueTask.FromResult(Finish());
        }
        try { timeData.ValidateModel(request.Model, request.Interval, request.Use, OrbitalGeometryBasis.ConfiguredRig); }
        catch (ArgumentException exception)
        {
            reason = OrbitalObjectEvaluator.TimeFailure(exception); detail = BoundedDetail(exception.Message);
            return ValueTask.FromResult(Finish());
        }
        if (budget.CanContinue())
        {
            evaluated = 1;
            var evaluator = OrbitalObjectEvaluator.Create(request.Observer, query.Pass.PassInterval, query.SelectedRecord,
                timeData, budget, out reason, out detail);
            if (evaluator is not null)
            {
                // Verify the immutable parent at its exact peak UTC, never a second peak search.
                var peak = evaluator.Evaluate(query.Pass.PeakUtc);
                if (peak is null)
                {
                    reason = evaluator.FailureReason; detail = evaluator.FailureDetail;
                }
                else if (Math.Abs(peak.Horizontal.AltitudeDegrees - query.Pass.MaximumElevationDegrees) > 1e-8)
                {
                    reason = OrbitalPredictionReason.PropagationFailure; detail = "arc-parent-peak-altitude-mismatch";
                }
                else
                {
                    var sampled = OrbitalPassArcSampler.Sample(query, evaluator.Evaluate, budget);
                    arc = sampled.Arc;
                    reason = evaluator.FailureReason != OrbitalPredictionReason.None ? evaluator.FailureReason : sampled.Reason;
                    detail = evaluator.FailureReason != OrbitalPredictionReason.None ? evaluator.FailureDetail : sampled.Detail;
                }
            }
        }
        return ValueTask.FromResult(Finish());

        OrbitalPredictionResult<OrbitalPassArcV1> Finish()
        {
            budget.CanContinue();
            if (budget.Reason != OrbitalPredictionReason.None) reason = budget.Reason;
            if (reason == OrbitalPredictionReason.None && arc is null) reason = OrbitalPredictionReason.IncompleteInterval;
            var result = Freeze();
            budget.CanContinue();
            if (budget.Reason == OrbitalPredictionReason.Deadline && result.Reason != OrbitalPredictionReason.Deadline)
            {
                reason = OrbitalPredictionReason.Deadline;
                result = Freeze();
            }
            budget.CanContinue();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        OrbitalPredictionResult<OrbitalPassArcV1> Freeze()
        {
            budget.CanContinue();
            var elapsed = budget.ElapsedMilliseconds;
            if (elapsed > query.Bounds.MaximumElapsedMilliseconds) reason = OrbitalPredictionReason.Deadline;
            if (arc is not null && reason != OrbitalPredictionReason.None && (arc.Status != OrbitalExecutionStatus.Partial || arc.Reason != reason))
                arc = OrbitalPassArcJson.Create(arc with { Status = OrbitalExecutionStatus.Partial, Reason = reason }, () => budget.CanContinue());
            return new(query, reason == OrbitalPredictionReason.None ? OrbitalExecutionStatus.Complete :
                arc is null ? OrbitalExecutionStatus.Unavailable : OrbitalExecutionStatus.Partial,
                reason, evaluated, budget.Evaluations, elapsed,
                arc is null ? [] : [arc], reason == OrbitalPredictionReason.None ? [] :
                [new(query.Pass.Lineage.ObjectIdentity, reason, BoundedDetail(detail))], () => budget.CanContinue());
        }
    }
}
