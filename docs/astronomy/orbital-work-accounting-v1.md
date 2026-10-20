# Orbital actual-work accounting (#529)

`OrbitalPredictionResult<T>.WorkDiagnostics` is an immutable snapshot of one
real engine query. It measures execution, including unsuccessful operations;
it never estimates work from products, selection scans or snapshot population.
Original public result constructors leave this optional member null. A missing
diagnostic snapshot means unknown, not zero. Existing constructors and their
validation, interfaces, acquisition adapters, canonical JSON, product/model
hashes and fixture identities are unchanged.

## Counter boundaries

| Counter | Exact meaning |
| --- | --- |
| `RequestedObjectSlots` | Requested identities for this operation, including slots never visited. An arc requests its one parent-pass object, irrespective of the original search population. |
| `VisitedObjectSlots` | Object-loop entries after the continuation check, before selection/support checks; equals existing `EvaluatedObjects`. An arc enters at most one slot. |
| `SelectedObjectSlots` | Visited slots with a selected record. Arcs count their already validated original-search selection on visiting the parent object. Selection alone implies no numerical work. |
| `StaticSupportRejectedObjects` | Selected/visited objects rejected by the evaluator's static numerical-support profile before initialization. Time/selection/model rejection is separate. |
| `InitializationAttemptedObjects` | Objects whose first initialization core reservation succeeds, immediately before the actual call; includes failed or budget-interrupted initialization. Pre-core coefficient rejection and denied reservations do not count. |
| `InitializationCoreCalls` | Actual initialization calls: the pinned `sgp4init` operation (including its internal epoch propagation) plus the separate epoch-state validation `sgp4` call. Successful initialization costs two. Initialization failure or a work limit can stop sooner. |
| `PointPropagationAttemptedObjects` | Objects with at least one actual query-time `sgp4` point call, counted at its accepted reservation. Includes failed calls and geometrically excluded objects; excludes initialization-only objects. |
| `PointPropagationCoreCalls` | Actual query-time point calls. Cached UTC probes and denied reservations add zero. Initialization calls are excluded. |
| `PointPropagationSuccesses` / `PointPropagationFailures` | Point calls returning a valid finite TEME state / failing to return one. Success precedes coordinate conversion, range validation, horizon/camera exclusion and output emission. Work-limit refusal without a core call is not a failed point call. |
| `ProjectionAttemptedObjects` | Objects with at least one actual `IImageProjector.Project` call. This includes objects that emit no product or pixels. |
| `ProjectionAttempts` | Every actual bounded, unbounded or chart sky-to-native-image projector call, counted before invocation. Null results count. Classification-center and boundary-chart calls count alongside sample calls. |
| `ProjectionSuccesses` | Projector calls returning native coordinates. Subsequent crop checks or output rules may exclude them. This is not the count of emitted samples/pixels/products. |

For every returned engine result:

```text
InitializationCoreCalls + PointPropagationCoreCalls = PropagationEvaluations
PointPropagationSuccesses + PointPropagationFailures = PointPropagationCoreCalls
ProjectionSuccesses <= ProjectionAttempts
ProjectionAttemptedObjects <= PointPropagationAttemptedObjects
  <= InitializationAttemptedObjects <= SelectedObjectSlots <= VisitedObjectSlots
  <= RequestedObjectSlots
```

Object flags reset only on the next sequential object visit, so repeated probes
count an object once without a retained population-sized set. Counters use
constant space. Probe caches remain private to their existing object/query
scope. Snapshot construction and final product revalidation/hashing do not
repeat numerical/projection work or reset counters.

Geometric horizon rejection can avoid every image projection while still
performing point propagation. Native projection success can precede crop
exclusion; aperture/domain failure can perform multiple failed projector calls.
Derivative/tube bounds alone are not projector calls. Geometry-free pass search
and arc production perform zero image projections. Model/resolved-time rejection
before the loop can return zero visited objects; invalid public query input
continues to throw without returning a result. Cancellation continues to throw
`OperationCanceledException`, including during final freezing; no canceled
result snapshot is fabricated.

## Focused software qualification

`OrbitalWorkAccountingTests` exercises the real engine for complete, partial
and unavailable results; selection/static/pre-core rejection; initialization
and point limits; failed point calls; bounded population prefixes; horizon,
aperture and crop exclusion; internal probes/classification; unprojected pass
and arc routes; peak-cache reuse; legacy construction; immutable snapshots and
repeated canonical product bytes. Existing budget, evaluator, projector and
engine final-deadline/cancellation tests remain boundary regressions.

This instrumentation closes observability of these operation counts. It does
not establish full-population completion, physical accuracy, cadence, per-stage
latency, cache-hit counts, allocation improvements or host qualification. The
owner must rerun the retained bounded population workload and report actual
diagnostics alongside its resource evidence before claiming that acceptance.
