using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;

internal sealed class PlanningTestInputs
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    internal readonly Clock Time = new();
    internal readonly Settings Options = new();
    internal readonly Config Configuration = new();
    internal readonly Reader Snapshots = new();
    internal readonly ResolvedTime Times = new();
    internal readonly Engines Engine = new();
    internal readonly Map Sky;
    internal readonly CameraAgentSatellitePlanner Planner;

    internal PlanningTestInputs(bool realEngine = false, bool reader = true)
    {
        Sky = new(Configuration, Time);
        Planner = new(Sky, Configuration, Times,
            realEngine ? new CameraAgentOrbitalPlanningEngineFactory(Time) : Engine,
            Options, Time, reader ? Snapshots : null);
    }

    internal async Task<CameraAgentSatellitePassSearchRequest> RequestAsync(string filter = "25544")
    {
        var scene = await Sky.ProjectAsync(Start, CancellationToken.None).ConfigureAwait(false);
        return new(Start, Start.AddHours(1), filter, 0, scene.Observer.CanonicalSha256, scene.Geometry.RigProfileHashSha256);
    }

    internal static byte[] Bytes(params string[] path)
        => File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "Fixtures", "orbital-prediction-v1", .. path]));
    internal static OrbitalSnapshot Snapshot(string name = "six-facts")
    {
        var parsed = OrbitalSnapshot.Parse(Bytes("virtual-camera", "snapshots", name, "manifest.json"),
            Bytes("virtual-camera", "snapshots", name, "records.json"));
        return parsed.Snapshot ?? throw new InvalidOperationException(parsed.Reason);
    }
    internal static OrbitalTimeData TimeData() => OrbitalTimeData.FromFinals2000A(Bytes("time", "predicted-2026.finals2000A"), Bytes("time", "leap-seconds.list"));
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Settings : IOptionsMonitor<CameraAgentSatellitePlanningOptions>
    {
        public CameraAgentSatellitePlanningOptions CurrentValue { get; set; } = new() { Enabled = true };
        public CameraAgentSatellitePlanningOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CameraAgentSatellitePlanningOptions, string?> listener) => null;
    }
    internal sealed class Config : ICameraAgentConfigurationAccessor
    {
        internal CameraModuleConfig Value = new(new(47.6062, -122.3321, 50, "America/Los_Angeles"), new("VirtualSky"),
            new(new("fixture", 640, 640, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, CalibrationVersion: "fixture-v1"),
                new(90, 0, 0), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                ProfileVersion: "fixture-rig-v1"), CapturePipelineConfig.Empty)
        {
            DeploymentLocation = DeploymentLocationSnapshot.Create("fixture-site", 1, "startup-seed", null,
                DateTimeOffset.UnixEpoch, null, 47.6062, -122.3321, 50, "America/Los_Angeles")
        };
        public bool IsConfigured => true;
        public void SetConfiguration(CameraModuleConfig config) => Value = config;
        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Value);
    }
    internal sealed class Map(Config configuration, TimeProvider clock) : ICameraAgentSkyMapProjection
    {
        internal Func<CameraAgentSkyMapProjectionResult, CameraAgentSkyMapProjectionResult>? Transform;
        public async ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(DateTimeOffset? atUtc, CancellationToken cancellationToken)
        {
            var scene = await new CameraAgentSkyMapProjection(configuration, new Catalog(), clock)
                .ProjectAsync(atUtc, cancellationToken).ConfigureAwait(false);
            return Transform?.Invoke(scene) ?? scene;
        }
    }
    private sealed class Catalog : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        public CatalogMetadata Metadata { get; } = new("fixture", "1", new("https://example.invalid/catalog"), new string('A', 64), "test", "1");
        public string PreprocessingVersion => "1";
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => [];
        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(CatalogCandidateQuery query,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<CelestialCatalogObject>>([]);
    }
    internal sealed class Reader : IOrbitalSnapshotReader
    {
        internal OrbitalSnapshot? Current = Snapshot();
        internal int Reads;
        public ValueTask<OrbitalSnapshotReadResult> ReadCurrentAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Current is { } snapshot ? OrbitalSnapshotReadResult.Available(snapshot) :
                OrbitalSnapshotReadResult.Unavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot));
        }
        public ValueTask<OrbitalSnapshotReadResult> ReadExactAsync(string snapshotSha256, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The current identity check must never substitute an exact/current read.");
    }
    internal sealed class ResolvedTime : ICameraAgentOrbitalTimeSource
    {
        internal OrbitalTimeData? Current = TimeData();
        internal CameraAgentOrbitalTimeDataSource Source = CameraAgentOrbitalTimeDataSource.Override;
        internal CameraAgentOrbitalTimeDataProblem Unavailable = CameraAgentOrbitalTimeDataProblem.OverrideInvalid;
        public ValueTask<CameraAgentOrbitalTimeResolution> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new CameraAgentOrbitalTimeResolution(Source,
                Current is null ? Unavailable : CameraAgentOrbitalTimeDataProblem.None, Current));
    }
    internal sealed class Engines : ICameraAgentOrbitalPlanningEngineFactory, IOrbitalPredictionEngine, IOrbitalPassArcEngine
    {
        internal Func<OrbitalPassQuery, CancellationToken, ValueTask<OrbitalPredictionResult<OrbitalPassSummaryV1>>>? Search;
        internal Func<OrbitalPassArcQuery, CancellationToken, ValueTask<OrbitalPredictionResult<OrbitalPassArcV1>>>? Arc;
        internal OrbitalPassQuery? LastSearch;
        internal OrbitalPassArcQuery? LastArc;
        internal OrbitalTimeData? BoundTime;
        internal int Searches;
        public CameraAgentOrbitalPlanningEngines Create(OrbitalTimeData timeData) { BoundTime = timeData; return new(this, this); }
        public ValueTask<OrbitalPredictionResult<OrbitalExposureTrackV1>> PredictExposureAsync(OrbitalExposureQuery query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Exposure prediction is outside the planning facade.");
        public ValueTask<OrbitalPredictionResult<OrbitalPassSummaryV1>> FindPassesAsync(OrbitalPassQuery query, CancellationToken cancellationToken)
        {
            LastSearch = query; Searches++;
            return Search?.Invoke(query, cancellationToken) ?? ValueTask.FromResult(Result(query));
        }
        public ValueTask<OrbitalPredictionResult<OrbitalPassArcV1>> PredictPassArcAsync(OrbitalPassArcQuery query, CancellationToken cancellationToken)
        {
            LastArc = query;
            return Arc?.Invoke(query, cancellationToken) ?? ValueTask.FromResult(new OrbitalPredictionResult<OrbitalPassArcV1>(
                query, OrbitalExecutionStatus.Unavailable, OrbitalPredictionReason.IncompleteInterval, 1, 0, 0, [], []));
        }
        internal static OrbitalPredictionResult<OrbitalPassSummaryV1> Result(OrbitalPassQuery query, bool partial = false)
        {
            var request = query.Request;
            var record = OrbitalRecordSelector.Select(request.Snapshot, request.Objects[0], request.Interval, request.SelectionPolicy).SelectedRecord;
            if (record is null)
                return new(query, OrbitalExecutionStatus.Unavailable, OrbitalPredictionReason.SelectionWithheld, 1, 0, 0, [], []);
            var choice = OrbitalRecordSelector.Select(request.Snapshot, record.ObjectIdentity, request.Interval, request.SelectionPolicy)
                .Candidates.Single(candidate => candidate.RecordSha256 == record.RecordSha256);
            var lineage = new OrbitalProductLineage(request.Snapshot.Manifest.ManifestSha256, request.Snapshot.Manifest.NormalizedPayloadSha256,
                record.RecordSha256, record.ObjectIdentity, record.SourceDataset, request.SelectionPolicy.IdentitySha256, record.EpochUtc,
                choice.StartEpochAgeHours, choice.EndEpochAgeHours, choice.Freshness, choice.SourceValidity, request.Snapshot.Manifest.Coverage);
            var pass = OrbitalPredictionJson.CreatePass(new(OrbitalPredictionJson.PassSchemaVersion, "", lineage, request.Interval,
                request.Interval, request.Interval.StartUtc.AddTicks((request.Interval.EndUtc - request.Interval.StartUtc).Ticks / 2),
                60, query.MinimumElevationDegrees, OrbitalPassBoundary.SearchWindow,
                partial ? OrbitalPassBoundary.WorkLimit : OrbitalPassBoundary.SearchWindow, request.Observer, request.Model,
                partial ? OrbitalExecutionStatus.Partial : OrbitalExecutionStatus.Complete,
                partial ? OrbitalPredictionReason.EvaluationBudget : OrbitalPredictionReason.None));
            return new(query, partial ? OrbitalExecutionStatus.Partial : OrbitalExecutionStatus.Complete,
                partial ? OrbitalPredictionReason.EvaluationBudget : OrbitalPredictionReason.None,
                request.Objects.Count, 0, 0, [pass], []);
        }
    }
}
