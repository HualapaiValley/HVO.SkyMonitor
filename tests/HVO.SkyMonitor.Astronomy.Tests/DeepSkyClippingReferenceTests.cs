using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy.Tests.IndependentOracle;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Independent analytic/root-based clipping qualification and antipodal-safety suite for #524 / carried-over #1191.
/// Compares production ellipse footprints and great-circle outlines against <see cref="IndependentAnalyticClippingOracle"/>
/// (which has zero production projector/coordinate/clipping imports) against the documented numerical error budget.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DeepSkyClippingReferenceTests
{
    private static readonly DateTimeOffset ReferenceUtc = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 1000);
    private static readonly CatalogMetadata Metadata = new(
        "HYG+OpenNGC", "4.4+openngc-fixture.1", new Uri("https://example.test/catalog"),
        new string('A', 64), "CC BY-SA 4.0", "4");
    private static readonly DeepSkySemantics Semantics = new(
        "OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
        new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0",
        "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote");

    [TestMethod]
    public void OracleSelfValidation_AnalyticRootsTangencyAndNegativeControls()
    {
        // Verify analytic closed-form horizon root on a meridian great-circle arc from alt -10 deg to +30 deg.
        var start = new OracleVec3(0, Math.Cos(-10d * Math.PI / 180d), Math.Sin(-10d * Math.PI / 180d));
        var end = new OracleVec3(0, Math.Cos(30d * Math.PI / 180d), Math.Sin(30d * Math.PI / 180d));
        var roots = IndependentAnalyticClippingOracle.SolveAnalyticGreatCirclePlaneRoots(
            start, end, new OracleVec3(0, 0, 1), 0d);
        Assert.IsTrue(roots.Any(r => Math.Abs(r - 0.25) < 1e-12),
            $"Expected analytic horizon root at t=0.25; got [{string.Join(", ", roots)}].");

        // Negative control 1: an extra connecting chord across an exterior gap MUST FAIL continuous Hausdorff check!
        var cam = CreateOracleCamera(ProjectionModel.EquidistantFisheye, 200, 200, 100, 95, k1: 0);
        var transform = OracleTransform.Identity(200, 200);
        var referenceArcs = new[]
        {
            new OracleVisibleArc(false, [new OraclePoint(10, 100), new OraclePoint(40, 100)]),
            new OracleVisibleArc(false, [new OraclePoint(160, 100), new OraclePoint(190, 100)])
        };
        var bridgedActual = new[]
        {
            (false, new[] { new OraclePoint(10, 100), new OraclePoint(40, 100), new OraclePoint(160, 100), new OraclePoint(190, 100) })
        };
        var metrics = IndependentAnalyticClippingOracle.ComparePolylines(bridgedActual, referenceArcs, cam, transform);
        Assert.IsTrue(metrics.ForwardHausdorffPixels > 50d,
            $"Negative control must catch extra connecting stroke across exterior gap; got {metrics.ForwardHausdorffPixels:F3} px.");

        // Negative control 2: empty output vs non-empty reference MUST FAIL!
        var emptyMetrics = IndependentAnalyticClippingOracle.ComparePolylines([], referenceArcs, cam, transform);
        Assert.IsTrue(double.IsPositiveInfinity(emptyMetrics.SymmetricHausdorffPixels));

        // Negative control 3: shifted polyline MUST FAIL budget!
        var shifted = new[]
        {
            (false, new[] { new OraclePoint(10, 102), new OraclePoint(40, 102) }),
            (false, new[] { new OraclePoint(160, 102), new OraclePoint(190, 102) })
        };
        var shiftedMetrics = IndependentAnalyticClippingOracle.ComparePolylines(shifted, referenceArcs, cam, transform);
        Assert.IsTrue(shiftedMetrics.SymmetricHausdorffPixels > 1.5d);
    }

    [TestMethod]
    [DataRow(ProjectionModel.EquidistantFisheye, 0.0)]
    [DataRow(ProjectionModel.EquisolidFisheye, 0.0)]
    [DataRow(ProjectionModel.OrthographicFisheye, 0.0)]
    [DataRow(ProjectionModel.StereographicFisheye, 0.0)]
    [DataRow(ProjectionModel.Perspective, 0.0)]
    [DataRow(ProjectionModel.EquidistantFisheye, 0.12)]
    [DataRow(ProjectionModel.EquidistantFisheye, -0.10)]
    [DataRow(ProjectionModel.Perspective, 0.08)]
    [DataRow(ProjectionModel.Perspective, -0.08)]
    public void EllipseFootprintMatchesIndependentOracleAcrossModelsAndDistortion(ProjectionModel model, double k1)
    {
        var focal = model == ProjectionModel.Perspective ? 1200d : 450d;
        var circleRadius = model == ProjectionModel.Perspective ? (double?)null : 260d;
        var projection = new ProjectionContext(
            model, 320, 240, focal, focal, 640, 480,
            model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
            circleRadius, BoresightAltitudeDegrees: 65, BoresightAzimuthDegrees: 140, RollDegrees: 17,
            HorizontalFlip: true, EnforceSensorBounds: true, RadialDistortionK1: k1);
        var request = new VisibleSceneRequest(ReferenceUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");

        // Place an oriented M31-like ellipse off-axis near boresight.
        var (raHours, decDeg) = HorizontalToJ2000(new AltAzPoint(63, 144), ReferenceUtc, Site);
        var centerGeom = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raHours, decDeg), ReferenceUtc),
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var paOfDate = DeepSkyOutlineSampler.PositionAngleOfDate(raHours, decDeg, 35, ReferenceUtc);
        var extent = new ResolvedFootprintExtent(ResolvedFootprintShape.Ellipse, 1.5, 0.6, paOfDate, null, null, null, "fixture");

        var actual = ResolvedFootprintSampler.Sample(request, "deep-sky:NGC0224", "M31",
            ResolvedFootprintSourceKind.DeepSkyObject, extent, centerGeom, null);
        Assert.IsNotNull(actual);
        Assert.IsFalse(actual.Clipped);

        var oracleCam = ToOracleCamera(request);
        var oracleArcs = IndependentAnalyticClippingOracle.ClipEllipse(
            oracleCam, OracleTransform.Identity(640, 480), raHours, decDeg, 1.5, 0.6, 35,
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        AssertHasAgreedGeometry(actual.Parts, oracleArcs, oracleCam, OracleTransform.Identity(640, 480));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HorizonClippingWithAndWithoutRefractionMatchesIndependentOracle(bool refractionEnabled)
    {
        var projection = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 500, 500, 500, 500, 1000, 1000,
            ProjectionAperture.Circular, 490, BoresightAltitudeDegrees: 45, BoresightAzimuthDegrees: 180,
            RollDegrees: 0, HorizontalFlip: false);
        var request = new VisibleSceneRequest(ReferenceUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(refractionEnabled), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");

        var (raHours, decDeg) = HorizontalToJ2000(new AltAzPoint(0.2, 180), ReferenceUtc, Site);
        var centerGeom = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raHours, decDeg), ReferenceUtc),
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var paOfDate = DeepSkyOutlineSampler.PositionAngleOfDate(raHours, decDeg, 50, ReferenceUtc);
        var actual = ResolvedFootprintSampler.Sample(request, "deep-sky:HORIZON", "HorizonTarget",
            ResolvedFootprintSourceKind.DeepSkyObject,
            new ResolvedFootprintExtent(ResolvedFootprintShape.Ellipse, 2.5, 1.5, paOfDate, null, null, null, "fixture"),
            centerGeom, null);
        Assert.IsNotNull(actual);
        Assert.IsTrue(actual.Clipped);
        Assert.IsTrue(actual.Parts.All(static p => !p.Closed));

        var oracleCam = ToOracleCamera(request);
        var oracleArcs = IndependentAnalyticClippingOracle.ClipEllipse(
            oracleCam, OracleTransform.Identity(1000, 1000), raHours, decDeg, 2.5, 1.5, 50,
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        AssertHasAgreedGeometry(actual.Parts, oracleArcs, oracleCam, OracleTransform.Identity(1000, 1000));
    }

    [TestMethod]
    public void ApertureBoundary_CrossingExteriorTangentAndEnclosingOutlinesMatchOracle()
    {
        // Circular fisheye aperture of radius 200 px around (300, 300) with focal length 300 px/rad.
        // Aperture angular radius = 200/300 rad = 38.197186342 deg.
        const double focal = 300d;
        const double apRad = 200d;
        var apAngleDeg = (apRad / focal) * 180d / Math.PI;
        var projection = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 300, 300, focal, focal, 600, 600,
            ProjectionAperture.Circular, apRad, BoresightAltitudeDegrees: 90, BoresightAzimuthDegrees: 0);
        var request = new VisibleSceneRequest(ReferenceUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.ProjectionOnly, projectionVersion: "cal-v1");
        var oracleCam = ToOracleCamera(request);

        // 1. Crossing aperture boundary
        var (raCross, decCross) = HorizontalToJ2000(new AltAzPoint(90 - apAngleDeg, 45), ReferenceUtc, Site);
        var crossGeom = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raCross, decCross), ReferenceUtc),
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var crossFootprint = ResolvedFootprintSampler.Sample(request, "deep-sky:CROSS", "Cross",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(4.0, "fixture"), crossGeom, null);
        Assert.IsNotNull(crossFootprint);
        Assert.IsTrue(crossFootprint.Clipped);
        var crossOracle = IndependentAnalyticClippingOracle.ClipEllipse(
            oracleCam, OracleTransform.Identity(600, 600), raCross, decCross, 4.0, 4.0, 0,
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        AssertHasAgreedGeometry(crossFootprint.Parts, crossOracle, oracleCam, OracleTransform.Identity(600, 600));

        // 2. Exterior near-tangent (0.05 deg outside aperture): MUST NOT fabricate any clipped chord stroke!
        var (raExt, decExt) = HorizontalToJ2000(new AltAzPoint(90 - (apAngleDeg + 4.05), 90), ReferenceUtc, Site);
        var extGeom = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raExt, decExt), ReferenceUtc),
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var extFootprint = ResolvedFootprintSampler.Sample(request, "deep-sky:EXTTANGENT", "ExtTangent",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(4.0, "fixture"), extGeom, null);
        Assert.IsNull(extFootprint, "An outline strictly outside the aperture must not fabricate a clipped chord stroke.");

        // 3. Enclosing outline surrounding the entire aperture (radius 50 deg > 38.2 deg aperture): MUST be empty!
        var (raEnc, decEnc) = HorizontalToJ2000(new AltAzPoint(90, 0), ReferenceUtc, Site);
        var encGeom = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raEnc, decEnc), ReferenceUtc),
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var encFootprint = ResolvedFootprintSampler.Sample(request, "deep-sky:ENCLOSE", "Enclose",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(50.0, "fixture"), encGeom, null);
        Assert.IsNull(encFootprint, "An enclosing outline outside the aperture must emit no spurious interior chords.");
    }

    [TestMethod]
    public void SensorAndDomainBoundaries_PerspectiveHemisphereAndOrthographicHorizonMatchOracle()
    {
        // Perspective camera tilted toward edge so part of a large great-circle outline crosses sensor edges.
        var persp = new ProjectionContext(
            ProjectionModel.Perspective, 200, 150, 500, 500, 400, 300,
            ProjectionAperture.Rectangular, null, BoresightAltitudeDegrees: 45, BoresightAzimuthDegrees: 210);
        var perspRequest = new VisibleSceneRequest(ReferenceUtc, Site, persp, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");

        // Build a great-circle polygon crossing the sensor right/top edge.
        var corners = new[]
        {
            HorizontalToJ2000(new AltAzPoint(48, 222), ReferenceUtc, Site),
            HorizontalToJ2000(new AltAzPoint(66, 222), ReferenceUtc, Site),
            HorizontalToJ2000(new AltAzPoint(66, 245), ReferenceUtc, Site),
            HorizontalToJ2000(new AltAzPoint(48, 245), ReferenceUtc, Site)
        };
        var ringPoints = corners
            .Concat([corners[0]])
            .Select(static p => new DeepSkyOutlinePoint(p.RaHours * 15d, p.DecDeg))
            .ToArray();
        var outline = new DeepSkyOutline("TEST_EDGE", 1, [new DeepSkyOutlineRing(ringPoints)]);
        var sampled = DeepSkyOutlineSampler.Sample(perspRequest, outline);
        Assert.AreEqual(DeepSkyOutlineSampleStatus.Valid, sampled.Status);
        Assert.IsNotNull(sampled.Outline);
        Assert.IsTrue(sampled.Outline.Clipped);

        var oracleCam = ToOracleCamera(perspRequest);
        var oracleArcs = IndependentAnalyticClippingOracle.ClipGreatCircleRing(
            oracleCam, OracleTransform.Identity(400, 300),
            ringPoints.Select(static p => (p.RightAscensionDegrees, p.DeclinationDegrees)).ToArray(),
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        AssertHasAgreedGeometry(sampled.Outline.Parts, oracleArcs, oracleCam, OracleTransform.Identity(400, 300));
    }

    [TestMethod]
    public void RaWrapAndCelestialPolesMatchIndependentOracleWithoutSeamArtifacts()
    {
        // 1. Outline straddling RA = 0h / 24h wrap boundary (358 deg <-> 2 deg) near equator
        var utcWrap = new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero);
        var lmstHours = AstronomyTime.LocalMeanSiderealDegrees(utcWrap, 0) / 15d;
        // Pick observer longitude so RA = 0h is directly on zenith meridian!
        var lonForZeroRa = (((0d - AstronomyTime.GreenwichMeanSiderealDegrees(utcWrap)) % 360d) + 360d) % 360d;
        if (lonForZeroRa > 180d) lonForZeroRa -= 360d;
        var eqSite = new ObserverLocation(0, lonForZeroRa, 0);
        var proj = new ProjectionContext(
            ProjectionModel.Perspective, 250, 250, 1500, 1500, 500, 500,
            ProjectionAperture.Rectangular, null, BoresightAltitudeDegrees: 90, BoresightAzimuthDegrees: 0);
        var wrapReq = new VisibleSceneRequest(utcWrap, eqSite, proj, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");

        var wrapRing = new DeepSkyOutlineRing(
        [
            new(358.5, -1.5),
            new(1.5, -1.5),
            new(1.5, 1.5),
            new(358.5, 1.5),
            new(358.5, -1.5)
        ]);
        var wrapResult = DeepSkyOutlineSampler.Sample(wrapReq, new DeepSkyOutline("RA_WRAP", 1, [wrapRing]));
        Assert.AreEqual(DeepSkyOutlineSampleStatus.Valid, wrapResult.Status);
        Assert.IsFalse(wrapResult.Outline!.Clipped);
        var wrapOracle = IndependentAnalyticClippingOracle.ClipGreatCircleRing(
            ToOracleCamera(wrapReq), OracleTransform.Identity(500, 500),
            wrapRing.Points.Select(static p => (p.RightAscensionDegrees, p.DeclinationDegrees)).ToArray(),
            utcWrap, eqSite.LatitudeDegrees, eqSite.LongitudeDegrees);
        AssertHasAgreedGeometry(wrapResult.Outline.Parts, wrapOracle, ToOracleCamera(wrapReq), OracleTransform.Identity(500, 500));

        // 2. North Celestial Pole (Dec = +89.5) and South Celestial Pole (Dec = -89.5)
        foreach (var sign in new[] { 1d, -1d })
        {
            var poleSite = new ObserverLocation(sign * 89.5, 0, 0);
            var poleReq = new VisibleSceneRequest(ReferenceUtc, poleSite, proj, new CatalogQuery(6, 10), Metadata,
                new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");
            var poleRing = new DeepSkyOutlineRing(
            [
                new(0, sign * 88.5),
                new(90, sign * 88.5),
                new(180, sign * 88.5),
                new(270, sign * 88.5),
                new(0, sign * 88.5)
            ]);
            var poleResult = DeepSkyOutlineSampler.Sample(poleReq, new DeepSkyOutline(sign > 0 ? "NCP" : "SCP", 1, [poleRing]));
            Assert.AreEqual(DeepSkyOutlineSampleStatus.Valid, poleResult.Status);
            Assert.IsFalse(poleResult.Outline!.Clipped);
            var poleOracle = IndependentAnalyticClippingOracle.ClipGreatCircleRing(
                ToOracleCamera(poleReq), OracleTransform.Identity(500, 500),
                poleRing.Points.Select(static p => (p.RightAscensionDegrees, p.DeclinationDegrees)).ToArray(),
                ReferenceUtc, poleSite.LatitudeDegrees, poleSite.LongitudeDegrees);
            AssertHasAgreedGeometry(poleResult.Outline.Parts, poleOracle, ToOracleCamera(poleReq), OracleTransform.Identity(500, 500));
        }

        // 3. Position angle precession near pole (Dec = +80 in 2026) agrees with independent oracle!
        var highDecSite = new ObserverLocation(80, 0, 0);
        var highDecReq = new VisibleSceneRequest(ReferenceUtc, highDecSite, proj, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");
        var raNearZenith = AstronomyTime.LocalMeanSiderealDegrees(ReferenceUtc, 0) / 15d;
        var hiGeom = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raNearZenith, 80), ReferenceUtc),
            ReferenceUtc, highDecSite.LatitudeDegrees, highDecSite.LongitudeDegrees);
        var hiPa = DeepSkyOutlineSampler.PositionAngleOfDate(raNearZenith, 80, 42, ReferenceUtc);
        var hiFp = ResolvedFootprintSampler.Sample(highDecReq, "deep-sky:POLE_PA", "PolePA",
            ResolvedFootprintSourceKind.DeepSkyObject,
            new ResolvedFootprintExtent(ResolvedFootprintShape.Ellipse, 2.0, 0.7, hiPa, null, null, null, "fixture"),
            hiGeom, null)!;
        var hiOracle = IndependentAnalyticClippingOracle.ClipEllipse(
            ToOracleCamera(highDecReq), OracleTransform.Identity(500, 500),
            raNearZenith, 80, 2.0, 0.7, 42, ReferenceUtc, highDecSite.LatitudeDegrees, highDecSite.LongitudeDegrees);
        AssertHasAgreedGeometry(hiFp.Parts, hiOracle, ToOracleCamera(highDecReq), OracleTransform.Identity(500, 500));
    }

    [TestMethod]
    public void AntipodalAndNearAntipodalOutlinesFailHonestlyWithoutAbortingSiblingObjects()
    {
        var projection = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 300, 300, 250, 250, 600, 600,
            ProjectionAperture.Circular, 290, BoresightAltitudeDegrees: 90, BoresightAzimuthDegrees: 0);
        var request = new VisibleSceneRequest(ReferenceUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.ProjectionOnly, projectionVersion: "cal-v1");

        // Catalog-valid ring containing an exact antipodal edge: (0, 0) -> (180, 0) -> (90, 45) -> (0, 0).
        var exactAntipodal = new DeepSkyOutline("ANTIPODAL_EXACT", 1,
        [
            new DeepSkyOutlineRing([new(0, 0), new(180, 0), new(90, 45), new(0, 0)])
        ]);
        // Catalog-valid ring containing a near-antipodal edge: (0, 89.9999) -> (180, -89.9999) (separation 179.9998 deg).
        var nearAntipodal = new DeepSkyOutline("ANTIPODAL_NEAR", 1,
        [
            new DeepSkyOutlineRing([new(0, 89.9999), new(180, -89.9999), new(45, 0), new(0, 89.9999)])
        ]);

        // Both rings are accepted by DeepSkyCatalog validation (no point repeats its predecessor, >=4 pts, closed),
        // yet DeepSkyOutlineSampler must refuse them cleanly without throwing or inventing an ambiguous route.
        var badObj = new DeepSkyObject("ANTIPODAL_EXACT", "ANTIPODAL_EXACT", "ANTIPODAL_EXACT", "Neb",
            0, 0, "Psc", null, null, null, null, null, null, null, null, null, null, null);
        var (sibRa, sibDec) = HorizontalToJ2000(new AltAzPoint(80, 0), ReferenceUtc, Site);
        var siblingObj = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
            sibRa, sibDec, "And", 120, 60, 35, 4.29, 3.44, 23.63, "Sb", 31, null, null, "Andromeda Galaxy");
        var catalog = new DeepSkyCatalog(
            Semantics,
            [badObj, siblingObj],
            [
                new(badObj.Designation, badObj.Id, DeepSkyAliasKinds.Designation),
                new(siblingObj.Designation, siblingObj.Id, DeepSkyAliasKinds.Designation),
                new("M31", siblingObj.Id, DeepSkyAliasKinds.Messier)
            ],
            [],
            [exactAntipodal]);

        var exactResult = DeepSkyOutlineSampler.Sample(request, exactAntipodal);
        Assert.AreEqual(DeepSkyOutlineSampleStatus.InvalidAntipodalEdge, exactResult.Status);
        Assert.IsNull(exactResult.Outline);
        Assert.IsNotEmpty(exactResult.RejectedEdges);
        Assert.AreEqual(180d, exactResult.RejectedEdges[0].SeparationDegrees, 1e-6);

        var nearResult = DeepSkyOutlineSampler.Sample(request, nearAntipodal);
        Assert.AreEqual(DeepSkyOutlineSampleStatus.InvalidAntipodalEdge, nearResult.Status);
        Assert.IsNull(nearResult.Outline);
        Assert.IsNotEmpty(nearResult.RejectedEdges);

        // Tangent-plane builder also refuses antipodal outlines honestly without throwing.
        var frame = ResolvedSourceFrame.Create(new AltAzPoint(80, 0), Site.LatitudeDegrees);
        Assert.IsFalse(DeepSkyOutlineSampler.TryProjectToGnomonicTangentPlane(
            exactAntipodal, frame, ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees, out _, out var tanStatus));
        Assert.AreEqual(DeepSkyOutlineSampleStatus.InvalidAntipodalEdge, tanStatus);

        // Sibling scene projection succeeds cleanly alongside the antipodal catalog entry!
        var visible = new VisibleScene(request, []).WithCuratedDeepSky(catalog, ProjectedSceneDeepSkySelection.Default);
        Assert.IsNotNull(visible.DeepSky);
        Assert.IsTrue(visible.DeepSky.Objects.Any(static o => o.CatalogObjectId == "NGC0224"));
        Assert.IsTrue(visible.ResolvedFootprints.Any(static fp => fp.Id == "deep-sky:NGC0224"));

        // Spatial query edge-reach check (#1191 R2): a region overlapping an antipodal edge still returns the object conservatively.
        var capQuery = catalog.Query(new DeepSkyQuery(10, Region: new J2000SphericalCap(6, 45, 5)));
        Assert.IsTrue(capQuery.Objects.Any(static o => o.Id == "ANTIPODAL_EXACT"));
    }

    [TestMethod]
    public void DeepSkyCatalogSpatialQueryEdgeReachFindsLongGreatCircleEdgeCrossingRegion()
    {
        // Place center at (RA=0h, Dec=0) and an outline ring whose edge arches over the north pole region (Dec~75 deg)
        // while its vertices are at (RA=0, Dec=60) and (RA=12h=180 deg, Dec=60) — separation 60 deg from pole,
        // so the great-circle edge passes directly over Dec=+90 deg (north pole)!
        // Vertices are 60 deg from center (RA=0, Dec=0) and 120 deg from center, so the edge reaches Dec=90 (90 deg from center)
        // and continues toward RA=180, Dec=60 (120 deg from center). Let's test a case where the edge's midpoint is FARTHER
        // from the object center than BOTH vertices: center at (RA=0h, Dec=-45), vertices at (RA=0, Dec=50) [95 deg] and (RA=180, Dec=50) [175 deg? wait 130+50].
        // Simpler: center at (0, 0), vertices at (100 deg, 0) [100 deg from center] and (260 deg, 0) [100 deg from center]
        // — no, shortest arc between 100 and 260 goes through 180 deg (180 deg from center!!), which is 80 deg FARTHER than either vertex!
        var item = new DeepSkyObject("LONG_EDGE", "LONG_EDGE", "LONG_EDGE", "Neb",
            0, 0, "Psc", null, null, null, null, null, null, null, null, null, null, null);
        var ring = new DeepSkyOutlineRing(
        [
            new(100, 0),
            new(260, 0), // shortest great-circle arc goes through (180 deg, 0) — 180 deg from center!
            new(180, 40),
            new(100, 0)
        ]);
        var catalog = new DeepSkyCatalog(
            Semantics, [item], [new("LONG_EDGE", "LONG_EDGE", DeepSkyAliasKinds.Designation)], [],
            [new DeepSkyOutline("LONG_EDGE", 1, [ring])]);

        // Region at RA=12h (180 deg), Dec=0 with radius 2 deg: vertex-only reach was 100 deg, missing 180-2=178 deg!
        var hit = catalog.Query(new DeepSkyQuery(10, Region: new J2000SphericalCap(12, 0, 2)));
        Assert.HasCount(1, hit.Objects, "Spatial reach must cover great-circle edge interiors, not only vertices.");
    }

    [TestMethod]
    [DataRow(100, 60, 400, 320, 2, 2, false, false, ProjectedSceneQuarterRotation.Degrees0, 1.0, 1.0)]
    [DataRow(80, 40, 480, 360, 2, 4, true, false, ProjectedSceneQuarterRotation.Degrees90, 0.5, 0.5)]
    [DataRow(120, 80, 360, 280, 3, 2, false, true, ProjectedSceneQuarterRotation.Degrees180, 0.75, 0.5)]
    [DataRow(160, 100, 320, 240, 2, 3, true, true, ProjectedSceneQuarterRotation.Degrees270, 0.5, 1.0)]
    public void RoiCropBinMirrorQuarterRotationAndPreviewTransformsMatchIndependentOracle(
        int cropX, int cropY, int cropW, int cropH, int binX, int binY,
        bool hMirror, bool vMirror, ProjectedSceneQuarterRotation rotation,
        double previewScaleX, double previewScaleY)
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 320, 240, 1400, 1400, 640, 480,
            ProjectionAperture.Rectangular, null, BoresightAltitudeDegrees: 70, BoresightAzimuthDegrees: 180);
        var request = new VisibleSceneRequest(ReferenceUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");
        var (raHours, decDeg) = HorizontalToJ2000(new AltAzPoint(69, 183), ReferenceUtc, Site);
        var obj = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
            raHours, decDeg, "And", 240, 120, 40, 4.29, 3.44, 23.63, "Sb", 31, null, null, "Andromeda Galaxy");
        var catalog = new DeepSkyCatalog(
            Semantics, [obj],
            [new("NGC 224", obj.Id, DeepSkyAliasKinds.Designation), new("M31", obj.Id, DeepSkyAliasKinds.Messier)], [], []);
        var visible = new VisibleScene(request, []).WithCuratedDeepSky(catalog, new ProjectedSceneDeepSkySelection(128, 32, 0, 1, 0.1));

        var swaps = rotation is ProjectedSceneQuarterRotation.Degrees90 or ProjectedSceneQuarterRotation.Degrees270;
        var outW = swaps ? cropH / binY : cropW / binX;
        var outH = swaps ? cropW / binX : cropH / binY;
        var imgTransform = new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion, 640, 480,
            cropX, cropY, cropW, cropH, binX, binY, hMirror, vMirror, rotation, outW, outH);

        var snapshot = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, imgTransform,
            new ProjectedSceneSource(Guid.NewGuid(), Guid.NewGuid(), new string('B', 64)), "cal-v1", "cal-v1");
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, snapshot.SchemaVersion);
        Assert.IsNotNull(snapshot.ResolvedFootprints);
        var fp = snapshot.ResolvedFootprints.Single();

        // Also apply preview scaling to output footprint parts.
        var scaledParts = fp.Parts
            .Select(p => (p.Closed, p.Points.Select(pt => new OraclePoint(pt.X * previewScaleX, pt.Y * previewScaleY)).ToArray()))
            .ToArray();

        var rotDeg = rotation switch
        {
            ProjectedSceneQuarterRotation.Degrees0 => 0,
            ProjectedSceneQuarterRotation.Degrees90 => 90,
            ProjectedSceneQuarterRotation.Degrees180 => 180,
            ProjectedSceneQuarterRotation.Degrees270 => 270,
            _ => throw new ArgumentOutOfRangeException(nameof(rotation))
        };
        var oracleCam = ToOracleCamera(request);
        var oracleTransform = new OracleTransform(640, 480, cropX, cropY, cropW, cropH, binX, binY,
            hMirror, vMirror, rotDeg, previewScaleX, previewScaleY);
        var oracleArcs = IndependentAnalyticClippingOracle.ClipEllipse(
            oracleCam, oracleTransform, raHours, decDeg, 2.0, 1.0, 40,
            ReferenceUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);

        var metrics = IndependentAnalyticClippingOracle.ComparePolylines(scaledParts, oracleArcs, oracleCam, oracleTransform);
        Assert.IsTrue(metrics.SymmetricHausdorffPixels <= IndependentAnalyticClippingOracle.ContinuousHausdorffBudgetPixels,
            $"Transformed footprint exceeded Hausdorff budget: sym={metrics.SymmetricHausdorffPixels:F4} px (fwd={metrics.ForwardHausdorffPixels:F4}, rev={metrics.ReverseHausdorffPixels:F4}).");
    }

    [TestMethod]
    public void ReadoutTransformAndSchemaV1V2FreezeWithFailClosedOldReaderOnV3()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 320, 240, 1200, 1200, 640, 480,
            ProjectionAperture.Rectangular, null, BoresightAltitudeDegrees: 70, BoresightAzimuthDegrees: 180);
        var request = new VisibleSceneRequest(ReferenceUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
            new RefractionOptions(false), HorizonPolicy.GeometricHorizon, projectionVersion: "cal-v1");
        var baseVisible = new VisibleScene(request, []);
        var source = new ProjectedSceneSource(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('B', 64));

        // 1. v1 scene has neither resolvedFootprints nor deepSky and preserves exact bytes.
        var v1 = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, baseVisible,
            ProjectedSceneImageTransformV1.Identity(640, 480), source, "cal-v1", "cal-v1");
        var v1Bytes = ProjectedSceneJson.Serialize(v1);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, v1.SchemaVersion);
        CollectionAssert.AreEqual(v1Bytes, ProjectedSceneJson.Serialize(ProjectedSceneJson.Parse(v1Bytes).Scene!));

        // 2. v3 scene with deep-sky object + readout binning transform.
        var (raHours, decDeg) = HorizontalToJ2000(new AltAzPoint(70, 180), ReferenceUtc, Site);
        var obj = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
            raHours, decDeg, "And", 120, 60, 35, 4.29, 3.44, 23.63, "Sb", 31, null, null, "Andromeda Galaxy");
        var catalog = new DeepSkyCatalog(
            Semantics, [obj],
            [new("NGC 224", obj.Id, DeepSkyAliasKinds.Designation), new("M31", obj.Id, DeepSkyAliasKinds.Messier)], [], []);
        var v3Visible = baseVisible.WithCuratedDeepSky(catalog, ProjectedSceneDeepSkySelection.Default);
        var binnedVisible = VisibleSceneReadoutTransform.ToOutput(
            v3Visible,
            projection with { PrincipalPointX = 160, PrincipalPointY = 120, FocalLengthXPixels = 600, FocalLengthYPixels = 600, WidthPixels = 320, HeightPixels = 240 },
            binX: 2, binY: 2);
        Assert.AreEqual(v3Visible.DeepSky!.Objects.Single().Pixel!.Value.X / 2d,
            binnedVisible.DeepSky!.Objects.Single().Pixel!.Value.X, 1e-12);

        var v3 = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, v3Visible,
            ProjectedSceneImageTransformV1.Identity(640, 480), source, "cal-v1", "cal-v1");
        var v3Bytes = ProjectedSceneJson.Serialize(v3);
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, v3.SchemaVersion);

        // Old reader (accepting only v1 and v2) MUST fail closed with $schemaVersion!
        var oldReaderResult = ProjectedSceneJson.Parse(v3Bytes,
            [ProjectedSceneV1.CurrentSchemaVersion, ProjectedSceneV1.ResolvedFootprintSchemaVersion]);
        Assert.IsFalse(oldReaderResult.IsValid);
        Assert.AreEqual("$schemaVersion", oldReaderResult.ErrorPath);

        // Current reader round-trips byte-identically.
        var currentParsed = ProjectedSceneJson.Parse(v3Bytes);
        Assert.IsTrue(currentParsed.IsValid, currentParsed.ErrorPath);
        CollectionAssert.AreEqual(v3Bytes, ProjectedSceneJson.Serialize(currentParsed.Scene!));
    }

    private static void AssertHasAgreedGeometry(
        IReadOnlyList<ResolvedFootprintPart> actualParts,
        IReadOnlyList<OracleVisibleArc> oracleArcs,
        OracleCamera cam,
        OracleTransform transform)
    {
        var mapped = actualParts
            .Select(static p => (p.Closed, p.Points.Select(static pt => new OraclePoint(pt.X, pt.Y)).ToArray()))
            .ToArray();
        var metrics = IndependentAnalyticClippingOracle.ComparePolylines(mapped, oracleArcs, cam, transform);
        Assert.IsTrue(metrics.MaxVertexToReferencePixels <= IndependentAnalyticClippingOracle.VertexOnCurveTolerancePixels,
            $"Vertex error {metrics.MaxVertexToReferencePixels:F6} px exceeded budget {IndependentAnalyticClippingOracle.VertexOnCurveTolerancePixels:F6} px.");
        Assert.IsTrue(metrics.SymmetricHausdorffPixels <= IndependentAnalyticClippingOracle.ContinuousHausdorffBudgetPixels,
            $"Continuous Hausdorff {metrics.SymmetricHausdorffPixels:F4} px (fwd={metrics.ForwardHausdorffPixels:F4}, rev={metrics.ReverseHausdorffPixels:F4}) exceeded budget {IndependentAnalyticClippingOracle.ContinuousHausdorffBudgetPixels:F4} px.");
        Assert.IsTrue(metrics.MaxEndpointBoundaryResidualPixels <= IndependentAnalyticClippingOracle.BoundaryEndpointResidualBudgetPixels,
            $"Boundary endpoint residual {metrics.MaxEndpointBoundaryResidualPixels:F6} px exceeded budget {IndependentAnalyticClippingOracle.BoundaryEndpointResidualBudgetPixels:F6} px.");
    }

    private static (double RaHours, double DecDeg) HorizontalToJ2000(AltAzPoint horizontal, DateTimeOffset utc, ObserverLocation site)
    {
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(horizontal, utc, site.LatitudeDegrees, site.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, utc);
        return (j2000.RightAscensionHours, j2000.DeclinationDegrees);
    }

    private static OracleCamera ToOracleCamera(VisibleSceneRequest request)
    {
        var p = request.Projection;
        return new OracleCamera(
            p.Model switch
            {
                ProjectionModel.EquidistantFisheye => OracleProjectionModel.EquidistantFisheye,
                ProjectionModel.EquisolidFisheye => OracleProjectionModel.EquisolidFisheye,
                ProjectionModel.OrthographicFisheye => OracleProjectionModel.OrthographicFisheye,
                ProjectionModel.StereographicFisheye => OracleProjectionModel.StereographicFisheye,
                ProjectionModel.Perspective => OracleProjectionModel.Perspective,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            },
            p.Aperture == ProjectionAperture.Circular ? OracleAperture.Circular : OracleAperture.Rectangular,
            p.PrincipalPointX, p.PrincipalPointY, p.FocalLengthXPixels, p.FocalLengthYPixels,
            p.WidthPixels, p.HeightPixels, p.ImageCircleRadiusPixels,
            p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees, p.HorizontalFlip,
            p.EnforceSensorBounds, p.RadialDistortionK1,
            request.HorizonPolicy == HorizonPolicy.GeometricHorizon ? OracleHorizonPolicy.GeometricHorizon : OracleHorizonPolicy.ProjectionOnly,
            request.Refraction.Enabled, request.Refraction.MinimumAltitudeDegrees);
    }

    private static OracleCamera CreateOracleCamera(ProjectionModel model, int w, int h, double focal, double? r, double k1) =>
        new(
            model == ProjectionModel.Perspective ? OracleProjectionModel.Perspective : OracleProjectionModel.EquidistantFisheye,
            model == ProjectionModel.Perspective ? OracleAperture.Rectangular : OracleAperture.Circular,
            w / 2d, h / 2d, focal, focal, w, h, r, 90, 0, 0, false, true, k1,
            OracleHorizonPolicy.GeometricHorizon, false, -1);
}
