using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Tests.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>Consumer projection checks on retained prediction bytes, not numerical or optical-visibility oracles.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SatellitePassArcTests
{
    private static readonly ProjectionContext Hemisphere = new(ProjectionModel.EquidistantFisheye,
        256, 256, 480 / Math.PI, 480 / Math.PI, 512, 512, ProjectionAperture.Circular, 240);
    private static readonly OrbitalPassArcV1 RetainedArc = LoadArc();

    [TestMethod]
    public void Render_NarrowRigShowsOneRetainedNonAnchorPredictionPointWithoutPolyline()
    {
        var sample = RetainedArc.Samples[1];
        Assert.IsFalse(new[] { RetainedArc.Anchors.StartSampleIndex, RetainedArc.Anchors.PeakSampleIndex,
            RetainedArc.Anchors.EndSampleIndex }.Contains(1));
        var projection = PerspectiveAt(sample, 1_000_000);
        var projector = ProjectorFactory.Create(projection);
        var admitted = RetainedArc.Samples.Where(value => projector.Project(value.Horizontal) is not null).ToArray();
        Assert.HasCount(1, admitted, "The valid narrow rig must admit exactly one actual retained sample.");
        Assert.AreEqual(sample, admitted[0]);

        using var context = new BunitContext();
        var cut = Render(context, RetainedArc, projection);
        Assert.HasCount(1, cut.FindAll(".rig-preview .prediction-point"));
        Assert.IsEmpty(cut.FindAll(".rig-preview polyline"));
        AssertPoint(cut.Find(".rig-preview .prediction-point"), projector, sample);
        Assert.HasCount(1, cut.FindAll(".satellite-arc > svg .predicted-path"));
        Assert.IsEmpty(cut.FindAll(".satellite-arc > svg .prediction-point"));
        Assert.Contains("single prediction points with UTC titles", cut.Find(".rig-preview p").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_DeclaredNonAnchorSingletonShowsPredictionPointsWithoutEmptyGeometryMessage()
    {
        var arc = Partial([new(1, 1)]);
        var sample = arc.Samples[1];
        var projection = PerspectiveAt(sample, 1_000_000);
        using var context = new BunitContext();
        var cut = Render(context, arc, projection);

        Assert.IsEmpty(cut.FindAll("polyline, .arc-anchor, .arc-time-label"));
        Assert.HasCount(1, cut.FindAll(".satellite-arc > svg .prediction-point"));
        Assert.HasCount(1, cut.FindAll(".rig-preview .prediction-point"));
        AssertPoint(cut.Find(".satellite-arc > svg .prediction-point"), ProjectorFactory.Create(Hemisphere), sample);
        AssertPoint(cut.Find(".rig-preview .prediction-point"), ProjectorFactory.Create(projection), sample);
        Assert.DoesNotContain("No projected arc samples", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("No connected projected arc samples", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("orange dots mark single prediction points", cut.Find("figcaption").TextContent, StringComparison.Ordinal);
        Assert.Contains("point titles include UTC", cut.Find("figcaption").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_DeclaredGapsKeepSingletonsAndConnectedRunsSeparateInBothViews()
    {
        var arc = Partial([new(1, 1), new(3, 2), new(6, 1)]);
        var projector = ProjectorFactory.Create(Hemisphere);
        using var context = new BunitContext();
        var cut = Render(context, arc, Hemisphere);

        foreach (var view in new[] { ".satellite-arc > svg", ".rig-preview svg" })
        {
            var points = cut.FindAll(view + " .prediction-point");
            Assert.HasCount(2, points);
            AssertPoint(points[0], projector, arc.Samples[1]);
            AssertPoint(points[1], projector, arc.Samples[6]);
            Assert.HasCount(1, cut.FindAll(view + " polyline"));
            Assert.AreEqual(Polyline(projector, arc.Samples[3], arc.Samples[4]),
                cut.Find(view + " polyline").GetAttribute("points"));
        }
    }

    [TestMethod]
    [DataRow("nonfinite-altitude")]
    [DataRow("nonfinite-azimuth")]
    [DataRow("altitude-out-of-range")]
    [DataRow("below-horizon")]
    public void Render_ExcludedDirectionsBreakRunsWithoutConnectingTheRemainingPoints(string exclusion)
    {
        var samples = RetainedArc.Samples.ToArray();
        var direction = samples[2].Horizontal;
        samples[2] = samples[2] with
        {
            Horizontal = exclusion switch
            {
                "nonfinite-altitude" => direction with { AltitudeDegrees = double.NaN },
                "nonfinite-azimuth" => direction with { AzimuthDegrees = double.PositiveInfinity },
                "altitude-out-of-range" => direction with { AltitudeDegrees = 91 },
                _ => direction with { AltitudeDegrees = -1 }
            }
        };
        // Deliberately bypass canonical validation to exercise the component's defensive projection boundary.
        var arc = Partial([new(1, 3)]) with { Samples = samples };
        var projector = ProjectorFactory.Create(Hemisphere);
        using var context = new BunitContext();
        var cut = Render(context, arc, Hemisphere);

        Assert.IsEmpty(cut.FindAll("polyline"));
        foreach (var view in new[] { ".satellite-arc > svg", ".rig-preview svg" })
        {
            var points = cut.FindAll(view + " .prediction-point");
            Assert.HasCount(2, points);
            AssertPoint(points[0], projector, samples[1]);
            AssertPoint(points[1], projector, samples[3]);
        }
    }

    [TestMethod]
    public void Render_OffRigSampleBreaksOnlyTheRigRunWithoutInventingIntersections()
    {
        var samples = RetainedArc.Samples.ToArray();
        samples[2] = samples[2] with { Horizontal = new(90, 0) };
        var arc = Partial([new(1, 3)]) with { Samples = samples };
        var projection = PerspectiveAt(samples[1], 5_000);
        var projector = ProjectorFactory.Create(projection);
        Assert.IsNull(projector.Project(samples[2].Horizontal));
        using var context = new BunitContext();
        var cut = Render(context, arc, projection);

        Assert.HasCount(1, cut.FindAll(".satellite-arc > svg polyline"));
        Assert.IsEmpty(cut.FindAll(".satellite-arc > svg .prediction-point, .rig-preview polyline"));
        var points = cut.FindAll(".rig-preview .prediction-point");
        Assert.HasCount(2, points);
        AssertPoint(points[0], projector, samples[1]);
        AssertPoint(points[1], projector, samples[3]);
    }

    [TestMethod]
    public void Render_SingletonUsesConfiguredProjectorIncludingOrientationOnce()
    {
        var arc = Partial([new(3, 1)]);
        var projection = PerspectiveAt(arc.Samples[1], 5_000) with
        {
            WidthPixels = 400,
            HeightPixels = 300,
            PrincipalPointX = 190,
            PrincipalPointY = 130,
            FocalLengthYPixels = 4_300,
            RollDegrees = 37,
            HorizontalFlip = true
        };
        var projector = ProjectorFactory.Create(projection);
        using var context = new BunitContext();
        var cut = Render(context, arc, projection);

        Assert.HasCount(1, cut.FindAll(".rig-preview .prediction-point"));
        AssertPoint(cut.Find(".rig-preview .prediction-point"), projector, arc.Samples[3]);
        Assert.AreNotEqual(Number(projection.PrincipalPointX), cut.Find(".rig-preview .prediction-point").GetAttribute("cx"));
        Assert.IsEmpty(cut.FindAll("polyline"));
    }

    [TestMethod]
    public void Render_NonincreasingUtcBreaksRunsWithoutCreatingAnExtraPoint()
    {
        var samples = RetainedArc.Samples.ToArray();
        samples[2] = samples[2] with { Utc = samples[1].Utc };
        var arc = Partial([new(1, 3)]) with { Samples = samples };
        using var context = new BunitContext();
        var cut = Render(context, arc, Hemisphere);

        Assert.IsEmpty(cut.FindAll("polyline"));
        foreach (var view in new[] { ".satellite-arc > svg", ".rig-preview svg" })
        {
            var points = cut.FindAll(view + " .prediction-point");
            Assert.HasCount(2, points);
            AssertPoint(points[0], ProjectorFactory.Create(Hemisphere), samples[1]);
            AssertPoint(points[1], ProjectorFactory.Create(Hemisphere), samples[3]);
        }
    }

    [TestMethod]
    [DataRow(-1, 1)]
    [DataRow(0, 0)]
    [DataRow(0, -1)]
    [DataRow(int.MaxValue, 1)]
    [DataRow(1, int.MaxValue)]
    public void Render_MalformedSegmentDoesNotExposeUndeclaredSamples(int start, int count)
    {
        var arc = RetainedArc with { Segments = [new(start, count)], Anchors = new(null, null, null) };
        using var context = new BunitContext();
        var cut = Render(context, arc, Hemisphere);

        Assert.IsEmpty(cut.FindAll("polyline, .prediction-point, .arc-anchor"));
        Assert.Contains("No projected arc samples are available", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_PathLabelAndRigTogglesRemainIndependentWithSingletons()
    {
        var peak = RetainedArc.Anchors.PeakSampleIndex!.Value;
        var arc = Partial([new(0, 2), new(3, 1), new(peak, 1), new(RetainedArc.Samples.Count - 3, 2)]);
        using var context = new BunitContext();
        var cut = Render(context, arc, Hemisphere, showRig: false);
        AssertGeometry(".satellite-arc > svg");
        Assert.IsEmpty(cut.FindAll(".rig-preview"));
        var labels = cut.FindAll(".arc-time-label").Count;
        Assert.IsTrue(labels > 0);

        cut.Render(parameters => parameters.Add(component => component.ShowRigPreview, true));
        AssertGeometry(".satellite-arc > svg");
        AssertGeometry(".rig-preview svg");
        cut.Render(parameters => parameters.Add(component => component.ShowLabels, false));
        Assert.IsEmpty(cut.FindAll(".arc-time-label"));
        AssertGeometry(".satellite-arc > svg");
        AssertGeometry(".rig-preview svg");
        cut.Render(parameters => parameters.Add(component => component.ShowPath, false));
        Assert.IsEmpty(cut.FindAll("polyline, .prediction-point, .arc-anchor"));
        Assert.HasCount(1, cut.FindAll(".rig-preview"));
        cut.Render(parameters => parameters.Add(component => component.ShowLabels, true));
        Assert.HasCount(labels, cut.FindAll(".arc-time-label"));
        Assert.IsEmpty(cut.FindAll("polyline, .prediction-point, .arc-anchor"));
        cut.Render(parameters => parameters.Add(component => component.ShowRigPreview, false));
        Assert.IsEmpty(cut.FindAll(".rig-preview"));
        Assert.HasCount(labels, cut.FindAll(".arc-time-label"));
        cut.Render(parameters => parameters.Add(component => component.ShowPath, true));
        AssertGeometry(".satellite-arc > svg");
        Assert.IsEmpty(cut.FindAll(".rig-preview"));
        cut.Render(parameters => parameters.Add(component => component.ShowRigPreview, true));
        AssertGeometry(".satellite-arc > svg");
        AssertGeometry(".rig-preview svg");

        void AssertGeometry(string view)
        {
            Assert.HasCount(2, cut.FindAll(view + " polyline"));
            Assert.HasCount(2, cut.FindAll(view + " .prediction-point"));
        }
    }

    private static IRenderedComponent<SatellitePassArc> Render(BunitContext context, OrbitalPassArcV1 arc,
        ProjectionContext projection, bool showRig = true)
    {
        var sky = SkyMapTestData.Result(arc.Pass.SearchInterval.StartUtc);
        var planning = new CameraAgentSatellitePlanningContext(sky.Observer with
        {
            LatitudeDegrees = arc.Pass.Observer.LatitudeDegrees,
            LongitudeDegrees = arc.Pass.Observer.LongitudeDegrees,
            ElevationMeters = arc.Pass.Observer.ElevationMeters
        }, sky.Geometry, projection);
        return context.Render<SatellitePassArc>(parameters => parameters.Add(component => component.Arc, arc)
            .Add(component => component.Context, planning).Add(component => component.ShowRigPreview, showRig));
    }

    private static OrbitalPassArcV1 Partial(IReadOnlyList<OrbitalSkyArcSegmentV1> segments)
        => OrbitalPassArcJson.Create(RetainedArc with
        {
            Segments = segments,
            Status = OrbitalExecutionStatus.Partial,
            Reason = OrbitalPredictionReason.IncompleteInterval
        });

    private static ProjectionContext PerspectiveAt(OrbitalTrackSample sample, double focalLength)
        => new(ProjectionModel.Perspective, 50, 50, focalLength, focalLength, 100, 100, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: sample.Horizontal.AltitudeDegrees, BoresightAzimuthDegrees: sample.Horizontal.AzimuthDegrees);

    private static void AssertPoint(IElement element, IImageProjector projector, OrbitalTrackSample sample)
    {
        var point = projector.Project(sample.Horizontal) ?? throw new AssertFailedException("The declared sample must project in this view.");
        Assert.AreEqual(Number(point.X), element.GetAttribute("cx"));
        Assert.AreEqual(Number(point.Y), element.GetAttribute("cy"));
        var title = element.QuerySelector("title");
        Assert.IsNotNull(title);
        Assert.Contains("Single prediction point:", title.TextContent, StringComparison.Ordinal);
        Assert.Contains(sample.Utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            title.TextContent, StringComparison.Ordinal);
    }

    private static string Polyline(IImageProjector projector, params OrbitalTrackSample[] samples)
        => string.Join(' ', samples.Select(sample =>
        {
            var point = projector.Project(sample.Horizontal)!.Value;
            return $"{Number(point.X)},{Number(point.Y)}";
        }));

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static OrbitalPassArcV1 LoadArc()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx"))) root = root.Parent;
        Assert.IsNotNull(root, "Repository fixture root must be available for the consumer test.");
        return OrbitalPassArcJson.Parse(File.ReadAllBytes(Path.Combine(root.FullName, "docs", "validation",
            "satellite-planning-v1", "samples", "iss-25544-arc.json")));
    }
}
