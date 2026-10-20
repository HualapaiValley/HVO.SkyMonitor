using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>Operator-visible availability of a local, read-only planning request.</summary>
public enum CameraAgentSatellitePlanningOutcome
{
    Ready,
    NoSnapshot,
    TimeInputsUnavailable,
    Invalid,
    Busy,
    Expired,
    ContextChanged,
    Failed
}

/// <summary>
/// A bounded UTC planning request. Expected identities bind the site/rig displayed when the operator requested it.
/// ObjectFilter is a bounded name or catalog-number filter; selected-population limits remain visible in the result.
/// </summary>
public sealed record CameraAgentSatellitePassSearchRequest(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string ObjectFilter,
    double MinimumElevationDegrees,
    string ExpectedObserverSha256,
    string ExpectedRigSha256);

/// <summary>Immutable authoritative site/rig facts retained with a planning session; no captured geometry is implied.</summary>
public sealed record CameraAgentSatellitePlanningContext(
    CameraAgentSkyMapObserver Observer,
    CameraAgentSkyMapGeometry Rig,
    ProjectionContext RigProjection);

/// <summary>A source-named canonical pass. Its boundary kinds distinguish resolved crossings from clipped windows.</summary>
public sealed record CameraAgentSatellitePassRow(string DisplayName, OrbitalPassSummaryV1 Pass);

/// <summary>
/// One bounded search session. The facade privately retains its exact original query for selected-pass detail.
/// Computational status, population completeness and object-filter truncation are independent facts.
/// </summary>
public sealed record CameraAgentSatellitePassSearchView(
    string SearchIdentitySha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    CameraAgentSatellitePlanningContext Context,
    OrbitalPredictionResult<OrbitalPassSummaryV1> Prediction,
    IReadOnlyList<CameraAgentSatellitePassRow> Passes,
    int MatchingObjects,
    int SelectedObjects,
    DateTimeOffset? MostRecentSourceRetrievedUtc);

/// <summary>Detail of an exact previously returned parent pass, without a narrowed search or record reselection.</summary>
public sealed record CameraAgentSatellitePassArcView(
    string SearchIdentitySha256,
    string ParentPassSha256,
    CameraAgentSatellitePlanningContext Context,
    OrbitalPredictionResult<OrbitalPassArcV1> Prediction);

/// <summary>Sanitized local availability, separate from the producer's numerical completion/partial status.</summary>
public sealed record CameraAgentSatellitePlanningResponse<T>(
    CameraAgentSatellitePlanningOutcome Outcome,
    string Message,
    T? Value) where T : class;

/// <summary>
/// Local planning only: no downloads, capture dispatch or classification. Implementations bound concurrency, retained
/// sessions, population, search window and work; caller cancellation propagates and superseded UI generations are discarded.
/// </summary>
public interface ICameraAgentSatellitePlanner
{
    ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>> SearchAsync(
        CameraAgentSatellitePassSearchRequest request,
        CancellationToken cancellationToken);

    ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>> GetArcAsync(
        string searchIdentitySha256,
        string parentPassSha256,
        CancellationToken cancellationToken);
}
