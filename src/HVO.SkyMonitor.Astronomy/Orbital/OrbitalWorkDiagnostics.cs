namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Frozen execution facts for one query, including failed and geometrically excluded work.
/// Attempts count actual operation boundaries, not returned products or population completeness.
/// These diagnostics are excluded from canonical product and model identities.
/// </summary>
public sealed class OrbitalWorkDiagnostics
{
    public int RequestedObjectSlots { get; }
    public int VisitedObjectSlots { get; }
    public int SelectedObjectSlots { get; }
    public int StaticSupportRejectedObjects { get; }
    /// <summary>Objects with at least one actual initialization core call, including failed initialization.</summary>
    public int InitializationAttemptedObjects { get; }
    /// <summary>SGP4 initialization (including its internal epoch call) and separate epoch-state validation calls.</summary>
    public int InitializationCoreCalls { get; }
    /// <summary>Objects with at least one actual query-time SGP4 point call, regardless of success.</summary>
    public int PointPropagationAttemptedObjects { get; }
    public int PointPropagationCoreCalls { get; }
    /// <summary>Point calls that returned a valid TEME state, before coordinate conversion or geometric exclusion.</summary>
    public int PointPropagationSuccesses { get; }
    public int PointPropagationFailures { get; }
    /// <summary>Objects with at least one actual sky-to-native-image projector call.</summary>
    public int ProjectionAttemptedObjects { get; }
    /// <summary>All bounded, unbounded and chart IImageProjector.Project calls, including null results.</summary>
    public long ProjectionAttempts { get; }
    /// <summary>Projector calls returning native coordinates, even if later crop/horizon/output rules exclude them.</summary>
    public long ProjectionSuccesses { get; }

    internal OrbitalWorkDiagnostics(int requested, OrbitalWorkCounter work)
    {
        RequestedObjectSlots = requested; VisitedObjectSlots = work.VisitedObjectSlots;
        SelectedObjectSlots = work.SelectedObjectSlots; StaticSupportRejectedObjects = work.StaticSupportRejectedObjects;
        InitializationAttemptedObjects = work.InitializationAttemptedObjects; InitializationCoreCalls = work.InitializationCoreCalls;
        PointPropagationAttemptedObjects = work.PointPropagationAttemptedObjects; PointPropagationCoreCalls = work.PointPropagationCoreCalls;
        PointPropagationSuccesses = work.PointPropagationSuccesses; PointPropagationFailures = work.PointPropagationFailures;
        ProjectionAttemptedObjects = work.ProjectionAttemptedObjects;
        ProjectionAttempts = work.ProjectionAttempts; ProjectionSuccesses = work.ProjectionSuccesses;
    }
}

public sealed partial class OrbitalPredictionResult<T>
{
    /// <summary>Actual query work, or null for results constructed through the original public constructors.</summary>
    public OrbitalWorkDiagnostics? WorkDiagnostics { get; }

    internal OrbitalPredictionResult(OrbitalExposureQuery query, OrbitalExecutionStatus status, OrbitalPredictionReason reason,
        int evaluatedObjects, int propagationEvaluations, double elapsedMilliseconds, IEnumerable<T> products,
        IEnumerable<OrbitalObjectFailure> failures, OrbitalWorkDiagnostics diagnostics)
        : this(query, status, reason, evaluatedObjects, propagationEvaluations, elapsedMilliseconds, products, failures)
        => WorkDiagnostics = ValidateDiagnostics(diagnostics);

    internal OrbitalPredictionResult(OrbitalPassQuery query, OrbitalExecutionStatus status, OrbitalPredictionReason reason,
        int evaluatedObjects, int propagationEvaluations, double elapsedMilliseconds, IEnumerable<T> products,
        IEnumerable<OrbitalObjectFailure> failures, OrbitalWorkDiagnostics diagnostics)
        : this(query, status, reason, evaluatedObjects, propagationEvaluations, elapsedMilliseconds, products, failures)
        => WorkDiagnostics = ValidateDiagnostics(diagnostics);

    internal OrbitalPredictionResult(OrbitalPassArcQuery query, OrbitalExecutionStatus status, OrbitalPredictionReason reason,
        int evaluatedObjects, int propagationEvaluations, double elapsedMilliseconds, IEnumerable<T> products,
        IEnumerable<OrbitalObjectFailure> failures, Action checkpoint, OrbitalWorkDiagnostics diagnostics)
        : this(query, status, reason, evaluatedObjects, propagationEvaluations, elapsedMilliseconds, products, failures, checkpoint)
        => WorkDiagnostics = ValidateDiagnostics(diagnostics);

    private OrbitalWorkDiagnostics ValidateDiagnostics(OrbitalWorkDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        OrbitalContract.Require(diagnostics.RequestedObjectSlots == RequestedObjects &&
            diagnostics.VisitedObjectSlots == EvaluatedObjects &&
            diagnostics.InitializationCoreCalls + diagnostics.PointPropagationCoreCalls == PropagationEvaluations &&
            diagnostics.PointPropagationSuccesses + diagnostics.PointPropagationFailures == diagnostics.PointPropagationCoreCalls,
            "result-work-diagnostics");
        return diagnostics;
    }
}

/// <summary>Constant-space counters owned by one sequential query; flags reset only on the next object visit.</summary>
internal sealed class OrbitalWorkCounter
{
    private bool initialized, pointPropagated, projected;
    internal int VisitedObjectSlots { get; private set; }
    internal int SelectedObjectSlots { get; private set; }
    internal int StaticSupportRejectedObjects { get; private set; }
    internal int InitializationAttemptedObjects { get; private set; }
    internal int InitializationCoreCalls { get; private set; }
    internal int PointPropagationAttemptedObjects { get; private set; }
    internal int PointPropagationCoreCalls { get; private set; }
    internal int PointPropagationSuccesses { get; private set; }
    internal int PointPropagationFailures { get; private set; }
    internal int ProjectionAttemptedObjects { get; private set; }
    internal long ProjectionAttempts { get; private set; }
    internal long ProjectionSuccesses { get; private set; }

    internal void VisitObject()
    {
        VisitedObjectSlots++;
        initialized = pointPropagated = projected = false;
    }

    internal void SelectObject() => SelectedObjectSlots++;
    internal void RejectStaticSupport() => StaticSupportRejectedObjects++;

    internal void Initialize()
    {
        if (!initialized) { InitializationAttemptedObjects++; initialized = true; }
        InitializationCoreCalls++;
    }

    internal void PropagatePoint()
    {
        if (!pointPropagated) { PointPropagationAttemptedObjects++; pointPropagated = true; }
        PointPropagationCoreCalls++;
    }

    internal void PointCompleted(bool success)
    {
        if (success) PointPropagationSuccesses++;
        else PointPropagationFailures++;
    }

    internal void Project()
    {
        if (!projected) { ProjectionAttemptedObjects++; projected = true; }
        ProjectionAttempts++;
    }

    internal void ProjectionSucceeded() => ProjectionSuccesses++;
    internal OrbitalWorkDiagnostics Snapshot(int requested) => new(requested, this);
}
