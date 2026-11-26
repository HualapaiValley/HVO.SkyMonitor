using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>Projects only declared connected samples, without interpolation or orbital inference.</summary>
public sealed partial class SatellitePassArc : ComponentBase
{
    // A fixed zenith-centred hemisphere, independent of configured sensor optics and readout transforms.
    private static readonly IImageProjector FullSky = ProjectorFactory.Create(new(
        ProjectionModel.EquidistantFisheye, 256, 256, 480 / Math.PI, 480 / Math.PI,
        512, 512, ProjectionAperture.Circular, 240));

    private IReadOnlyList<string> _fullPaths = [];
    private IReadOnlyList<string> _rigPaths = [];
    private IReadOnlyList<PredictionPoint> _fullPoints = [];
    private IReadOnlyList<PredictionPoint> _rigPoints = [];
    private IReadOnlyList<Anchor> _anchors = [];
    private HorizonObstructionPredictionProduct? _projectedObstruction;
    private OrbitalPassArcV1? _projectedArc;
    private CameraAgentSatellitePlanningContext? _projectedContext;
    private bool _projectedAngularHorizon;
    private string? _solarModelVersion;

    [Inject] internal IServiceProvider Services { get; set; } = default!;

    [Parameter, EditorRequired] public OrbitalPassArcV1 Arc { get; set; } = default!;
    [Parameter, EditorRequired] public CameraAgentSatellitePlanningContext Context { get; set; } = default!;
    [Parameter] public bool ShowPath { get; set; } = true;
    [Parameter] public bool ShowLabels { get; set; } = true;
    [Parameter] public bool ShowRigPreview { get; set; }
    [Parameter] public bool ApplyHorizonToAngularView { get; set; } = true;

    private sealed record Anchor(string Name, OrbitalTrackSample? Sample, PixelPoint? Point, string? LabelX, string? LabelY,
        SolarAltitudeClassification? Solar, SolarDiskAppearance? Moon, int? SampleIndex);
    private readonly record struct PredictionPoint(DateTimeOffset Utc, PixelPoint Point);

    protected override void OnParametersSet()
    {
        var sameInputs = ReferenceEquals(_projectedArc, Arc) && ReferenceEquals(_projectedContext, Context) &&
            ReferenceEquals(_projectedObstruction, Obstruction);
        if (!sameInputs || _projectedAngularHorizon != ApplyHorizonToAngularView)
        {
            (_fullPaths, _fullPoints) = ProjectSegments(FullSky, ApplyHorizonToAngularView ? Obstruction : null);
            _projectedAngularHorizon = ApplyHorizonToAngularView;
        }
        if (sameInputs) return;
        _projectedArc = Arc;
        _projectedObstruction = Obstruction;
        _projectedContext = Context;
        var ephemeris = Services.GetService<IPlanetEphemeris>();
        _solarModelVersion = ephemeris?.ModelVersion;
        (_rigPaths, _rigPoints) = ProjectSegments(ProjectorFactory.Create(Context.RigProjection), Obstruction);
        var anchors = new List<Anchor>(3);
        AddAnchor(Boundary(Arc.Pass.StartBoundary, true), Arc.Anchors.StartSampleIndex);
        AddAnchor("Peak", Arc.Anchors.PeakSampleIndex);
        AddAnchor(Boundary(Arc.Pass.EndBoundary, false), Arc.Anchors.EndSampleIndex);
        _anchors = anchors;

        void AddAnchor(string name, int? index)
        {
            var sample = index is { } i && i >= 0 && i < Arc.Samples.Count &&
                Arc.Segments.Any(segment => i >= segment.StartSampleIndex && (long)i < (long)segment.StartSampleIndex + segment.SampleCount)
                ? Arc.Samples[i] : null;
            var point = sample is null ? null : ProjectValid(FullSky, sample);
            string? x = null;
            string? y = null;
            if (point is { } p)
            {
                var labelX = Math.Clamp(p.X + 9, 12, 330);
                var labelY = Math.Clamp(p.Y - 10, 22, 490);
                // At most three labels, with conservative fixed screen-space collision boxes.
                if (!anchors.Any(anchor => anchor.LabelX is not null && anchor.LabelY is not null &&
                    Math.Abs(double.Parse(anchor.LabelX, CultureInfo.InvariantCulture) - labelX) < 170 &&
                    Math.Abs(double.Parse(anchor.LabelY, CultureInfo.InvariantCulture) - labelY) < 24))
                {
                    x = Number(labelX);
                    y = Number(labelY);
                }
            }
            anchors.Add(new(name, sample, point, x, y, SolarAt(ephemeris, sample), MoonAt(sample), index));
        }
    }

    private SolarAltitudeClassification? SolarAt(IPlanetEphemeris? ephemeris, OrbitalTrackSample? sample)
    {
        if (ephemeris is null || sample is null) return null;
        try
        {
            // Use the original pass observer and exact declared anchor time, independently of orbital illumination.
            var solar = SolarAltitudeClassifier.Classify(ephemeris, sample.Utc,
                Arc.Pass.Observer.LatitudeDegrees, Arc.Pass.Observer.LongitudeDegrees, 0, -18);
            return double.IsFinite(solar.AltitudeDegrees) ? solar : null;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or InvalidOperationException or ArithmeticException)
        {
            return null;
        }
    }

    private static string SolarContext(Anchor anchor) => anchor.Solar is { } solar
        ? $"Sun geometric altitude {Number(solar.AltitudeDegrees)}°; sky context {solar.Regime}."
        : "Sun geometric altitude Unknown; sky context Unknown.";

    private SolarDiskAppearance? MoonAt(OrbitalTrackSample? sample)
    {
        if (sample is null) return null;
        try
        {
            var moon = SolarDiskEphemeris.Get(SolarSystemBody.Moon, sample.Utc, Arc.Pass.Observer);
            return double.IsFinite(moon.Direction.AltitudeDegrees) && moon.IlluminatedFraction is >= 0 and <= 1
                ? moon : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            return null;
        }
    }

    private static string MoonContext(Anchor anchor) => anchor.Moon is { } moon
        ? $"Moon geometric altitude {Number(moon.Direction.AltitudeDegrees)}°; lunar disk {Number(moon.IlluminatedFraction * 100)}% illuminated."
        : "Moon geometric altitude Unknown; lunar disk illumination Unknown.";

    [Parameter] public HorizonObstructionPredictionProduct? Obstruction { get; set; }

    private bool ShowAngularAnchor(Anchor anchor) => anchor.SampleIndex is { } index &&
        (!ApplyHorizonToAngularView || IsClear(Obstruction, index));

    private bool IsClear(HorizonObstructionPredictionProduct? obstruction, int index) =>
        obstruction is null || (obstruction.Binding.SourceIdentitySha256 == Arc.ProductSha256 &&
            index < obstruction.Eligibility.Count && obstruction.Eligibility[index] == HorizonObstructionEligibility.Clear);

    private (List<string> Paths, List<PredictionPoint> Points) ProjectSegments(IImageProjector projector,
        HorizonObstructionPredictionProduct? obstruction)
    {
        var paths = new List<string>();
        var points = new List<PredictionPoint>();
        foreach (var segment in Arc.Segments)
        {
            // Products are validated by the facade. Still refuse malformed ranges at this presentation boundary.
            if (segment.StartSampleIndex < 0 || segment.SampleCount < 1 ||
                (long)segment.StartSampleIndex + segment.SampleCount > Arc.Samples.Count) continue;
            var run = new List<PredictionPoint>();
            DateTimeOffset? previous = null;
            for (var i = segment.StartSampleIndex; i < segment.StartSampleIndex + segment.SampleCount; i++)
            {
                var sample = Arc.Samples[i];
                var point = IsClear(obstruction, i) ? ProjectValid(projector, sample) : null;
                if (point is null || previous is { } utc && sample.Utc <= utc)
                {
                    Flush();
                    previous = null;
                    continue;
                }
                if (obstruction is not null && run.Count > 0 &&
                    (i - 1 >= obstruction.ClearSegments.Count || !obstruction.ClearSegments[i - 1])) Flush();
                run.Add(new(sample.Utc, point.Value));
                previous = sample.Utc;
            }
            Flush();

            void Flush()
            {
                if (run.Count >= 2) paths.Add(string.Join(' ', run.Select(value => $"{Number(value.Point.X)},{Number(value.Point.Y)}")));
                else if (run.Count == 1) points.Add(run[0]);
                run.Clear();
            }
        }
        return (paths, points);
    }

    private static PixelPoint? ProjectValid(IImageProjector projector, OrbitalTrackSample sample)
    {
        var direction = sample.Horizontal;
        if (!double.IsFinite(direction.AltitudeDegrees) || direction.AltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(direction.AzimuthDegrees)) return null;
        return projector.Project(direction) is { } point && double.IsFinite(point.X) && double.IsFinite(point.Y) ? point : null;
    }

    private string Timestamp(DateTimeOffset utc)
        => new CameraAgentSiteTime(ObservingDayCalendar.Create(Context.Observer.TimeZoneId)).Format(utc) +
            " · " + utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Boundary(OrbitalPassBoundary boundary, bool start) => boundary switch
    {
        OrbitalPassBoundary.ResolvedCrossing => start ? "Rise" : "Set",
        OrbitalPassBoundary.SearchWindow => start ? "Window start (clipped)" : "Window end (clipped)",
        _ => start ? "Work-limited start" : "Work-limited end"
    };
}
