using System.Diagnostics;

namespace HVO.SkyMonitor.Astronomy;

internal sealed record CoreSolverOptions(double MinimumScale = .90, double MaximumScale = 1.10,
    double ScaleStep = .01, int DetectionTriangleStars = 28, int SampledTriangles = 192,
    int MaximumHypotheses = 200000, int MaximumCandidates = 32, double MaximumCatalogMagnitude = 7);
internal sealed record CoreAssociation(string CatalogId, int DetectionIndex, double ResidualPixels, bool Verification);
/// <summary>A refined acquisition hypothesis before verification and quality gates.</summary>
internal sealed record CoreCandidate(AstrometricRotation Rotation, double Scale, CoreAssociation[] Matches, double RmsPixels);
internal sealed record CoreQuality(double Score0To100, string Status, bool IsCalibratedProbability,
    int FittingStars, int VerificationStars, int ExpectedFittingStars, double FittingRmsPixels,
    double VerificationRmsPixels, double WidthCoverage, double HeightCoverage, string[] Reasons)
;
internal sealed record CoreResult(bool Accepted, string Status, string Reason, SolverOptics? Solution,
    double? FocalScale, CoreQuality? Quality, CoreAssociation[] Associations, int CatalogIndexStars,
    int CatalogTriangles, int ImageTriangles, int Hypotheses, int DistinctCandidates,
    bool BudgetExhausted, double ElapsedMilliseconds, string SearchDomain);

/// <summary>Proper rotation maps parity-corrected camera rays to local ENU. No Euler singularity in fitting.</summary>
internal readonly record struct AstrometricRotation(EnuVector X, EnuVector Y, EnuVector Z)
{
    public static AstrometricRotation Identity => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
    public EnuVector Apply(EnuVector p) => X * p.East + Y * p.North + Z * p.Up;
    public EnuVector Inverse(EnuVector p) => new(EnuVector.Dot(X, p), EnuVector.Dot(Y, p), EnuVector.Dot(Z, p));
    public static AstrometricRotation FromPairs(EnuVector a, EnuVector b, EnuVector u, EnuVector v)
    {
        var ay = (b - a * EnuVector.Dot(a, b)).Normalize(); var az = EnuVector.Cross(a, ay);
        var uy = (v - u * EnuVector.Dot(u, v)).Normalize(); var uz = EnuVector.Cross(u, uy);
        EnuVector Map(EnuVector p) => u * EnuVector.Dot(a, p) + uy * EnuVector.Dot(ay, p) + uz * EnuVector.Dot(az, p);
        return new(Map(new(1, 0, 0)), Map(new(0, 1, 0)), Map(new(0, 0, 1)));
    }
    public AstrometricRotation Increment(double x, double y, double z)
    {
        var w = new EnuVector(x, y, z); var t = w.Length; if (t < 1e-16) return this;
        var n = w / t;
        EnuVector Turn(EnuVector p) => p * Math.Cos(t) + EnuVector.Cross(n, p) * Math.Sin(t) + n * (EnuVector.Dot(n, p) * (1 - Math.Cos(t)));
        return new(Turn(X), Turn(Y), Turn(Z));
    }
    public CorePose ToPose()
    {
        var h = CameraBasis.ToHorizontal(Z); var basis = CameraBasis.Create(h.AltitudeDegrees, h.AzimuthDegrees);
        return new(h.AltitudeDegrees, h.AzimuthDegrees, Math.Atan2(EnuVector.Dot(X, basis.ImageUp), EnuVector.Dot(X, basis.Right)) * 180 / Math.PI);
    }
    public static AstrometricRotation FromPose(CorePose pose)
    { var b = CameraBasis.Create(pose.Altitude, pose.Azimuth, pose.Roll); return new(b.Right, b.ImageUp, b.Boresight); }
    public double SeparationDegrees(AstrometricRotation other) => Math.Acos(Math.Clamp((EnuVector.Dot(X, other.X) + EnuVector.Dot(Y, other.Y) + EnuVector.Dot(Z, other.Z) - 1) / 2, -1, 1)) * 180 / Math.PI;
}

internal sealed class AstrometricRayCamera
{
    public SolverOptics Configuration { get; }
    private readonly IImageProjector local;
    public AstrometricRayCamera(SolverOptics configuration, double scale = 1)
    {
        Configuration = configuration with { FocalX = configuration.FocalX * scale, FocalY = configuration.FocalY * scale, BoresightAltitude = 90, BoresightAzimuth = 0, Roll = 0 };
        local = Configuration.Projector();
    }
    public EnuVector? Ray(PixelPoint pixel) => local.Unproject(pixel) is { } h ? CameraBasis.FromHorizontal(h) : null;
    public PixelPoint? Pixel(EnuVector world, AstrometricRotation rotation) => local.Project(CameraBasis.ToHorizontal(rotation.Inverse(world)));
}

/// <summary>
/// Bounded local spherical-triangle index. Inputs have no pointing seed or known correspondences.
/// Stable FNV-1a ID-hash verification partition are excluded from index, candidate ranking, and refinement. They are used only after ranking.
/// </summary>
internal static class AstrometricSolverCore
{
    private sealed record Star(CelestialCatalogObject Catalog, EnuVector Ray);
    private readonly record struct Triangle(int A, int B, int C, double X, double Y, double Z);
    private const int MaximumIndexTriangles = 2000000;
    private sealed record Pair(Star Star, CoreDetection CoreDetection, PixelPoint Predicted, double Distance);
    private sealed record Candidate(AstrometricRotation Rotation, double Scale, List<Pair> Matches);
    public static CoreResult Solve(IReadOnlyList<CoreDetection> detections, IReadOnlyList<CelestialCatalogObject> catalog,
        SolverOptics configuration, CoreSite site, DateTimeOffset utc, CoreSolverOptions? options = null, AstrometricWorkControl? control = null)
        => Run(detections, catalog, configuration, site, utc, options, control, null);

    /// <summary>
    /// Runs the identical bounded blind search and per-candidate refinement, then returns every distinct refined
    /// hypothesis without verification or quality gates. Callers own all acceptance decisions; a non-accepted
    /// result with status <c>acquired</c> carries the search metrics.
    /// </summary>
    internal static CoreResult Acquire(IReadOnlyList<CoreDetection> detections, IReadOnlyList<CelestialCatalogObject> catalog,
        SolverOptics configuration, CoreSite site, DateTimeOffset utc, List<CoreCandidate> candidates, CoreSolverOptions? options = null,
        AstrometricWorkControl? control = null)
        => Run(detections, catalog, configuration, site, utc, options, control, candidates);

    private static CoreResult Run(IReadOnlyList<CoreDetection> detections, IReadOnlyList<CelestialCatalogObject> catalog,
        SolverOptics configuration, CoreSite site, DateTimeOffset utc, CoreSolverOptions? options, AstrometricWorkControl? control,
        List<CoreCandidate>? acquisition)
    {
        control?.Check();
        var watch = Stopwatch.StartNew(); var o = options ?? new(); configuration.Context();
        if (!double.IsFinite(o.MaximumCatalogMagnitude) || o.MinimumScale <= 0 || !double.IsFinite(o.MinimumScale) || !double.IsFinite(o.MaximumScale) || o.MaximumScale < o.MinimumScale ||
            !double.IsFinite(o.ScaleStep) || o.ScaleStep <= 0 || (o.MaximumScale - o.MinimumScale) / o.ScaleStep > 100 ||
            o.DetectionTriangleStars is < 3 or > 60 || o.SampledTriangles is < 1 or > 5000 || o.MaximumHypotheses is < 1 or > 200000 || o.MaximumCandidates is < 2 or > 100 || catalog.Count > 2500 || detections.Count > 10000)
            throw new ArgumentException("Invalid or excessive bounded blind-search request");
        if (catalog.Select(s => s.Id).Distinct().Count() != catalog.Count || detections.Select(d => d.Index).Distinct().Count() != detections.Count ||
            detections.Any(d => !double.IsFinite(d.X) || !double.IsFinite(d.Y) || !double.IsFinite(d.Flux) || d.Flux < 0)) throw new ArgumentException("Invalid or duplicate input identities");
        var stars = catalog.Where(s => s.Magnitude <= o.MaximumCatalogMagnitude).Select(s => new Star(s, CameraBasis.FromHorizontal(AstrometricMath.Horizontal(s, utc, site)))).Where(s => s.Ray.Up > 0).ToList();
        var training = stars.Where(s => !IsVerification(s.Catalog.Id)).ToList(); var verification = stars.Where(s => IsVerification(s.Catalog.Id)).ToList();
        var grid = new CoreDetectionGrid(detections); var hypotheses = 0; var triangleCount = 0; var imageCount = 0; var exhausted = false;
        const string domain = "No orientation seed; known projection/principal point/parity/site/UTC; bounded focal scale; bounded supplied catalog and sampled triangle search";
        CoreResult Reject(string reason, CoreQuality? q = null, int candidates = 0) => new(false, "rejected", reason, null, null, q, [], training.Count, triangleCount, imageCount, hypotheses, candidates, exhausted, watch.Elapsed.TotalMilliseconds, domain);
        if (detections.Count < 12) return Reject("Fewer than12 image detections");
        o = o with { MinimumScale = Math.Max(o.MinimumScale, MinimumPhysicalScale(configuration)) };
        if (o.MinimumScale > o.MaximumScale) return Reject("Requested scale range has no geometrically valid projection");
        var narrow = configuration.Model == ProjectionModel.Perspective;
        var maxSpan = narrow ? Math.Min(Math.PI, 2 * Math.Atan(Math.Sqrt(Math.Pow(Math.Max(configuration.PrincipalX, configuration.Width - configuration.PrincipalX) / configuration.FocalX, 2) + Math.Pow(Math.Max(configuration.PrincipalY, configuration.Height - configuration.PrincipalY) / configuration.FocalY, 2)) / o.MinimumScale)) : Math.PI;
        var tolerance = Math.Max(1.8 / (Math.Min(configuration.FocalX, configuration.FocalY) * o.MinimumScale), (narrow ? .0015 : .004) * maxSpan);
        // A narrow field indexes every training star, in training order exactly as solver v1 did, whenever that index fits
        // the triangle bound; a field too wide for that indexes the largest magnitude-ordered prefix that fits (IndexPrefix).
        var brightest = narrow ? training.OrderBy(s => s.Catalog.Magnitude).ToList() : [];
        var admitted = narrow ? IndexPrefix(brightest.Select(s => s.Ray).ToArray(), maxSpan, tolerance * .5, MaximumIndexTriangles, control) : 0;
        var indexed = (!narrow ? training.OrderBy(s => s.Catalog.Magnitude).Take(120) : admitted == training.Count ? training : brightest.Take(admitted)).ToList();
        if (indexed.Count > 1500) return Reject("Catalog exceeds the bounded1500-star in-memory index limit");
        var index = new Dictionary<(int, int, int), List<Triangle>>();
        foreach (var t in Triangles(indexed.Select(s => s.Ray).ToArray(), maxSpan, tolerance * .5, control))
        { var k = Key(t, tolerance); if (!index.TryGetValue(k, out var bucket)) index[k] = bucket = []; bucket.Add(t); triangleCount++; if (triangleCount > MaximumIndexTriangles) return Reject("Catalog triangle index exceeds its2million-entry bound"); }
        var bright = detections.OrderByDescending(d => d.Flux).Take(o.DetectionTriangleStars).ToArray();
        var rawCandidates = new List<Candidate>();
        var scales = Enumerable.Range(0, (int)Math.Ceiling((o.MaximumScale - o.MinimumScale) / o.ScaleStep) + 1)
            .Select(i => Math.Min(o.MaximumScale, o.MinimumScale + i * o.ScaleStep)).Distinct().OrderBy(s => Math.Abs(Math.Log(s))).ToArray();
        foreach (var scale in scales)
        {
            control?.Check();
            var camera = new AstrometricRayCamera(configuration, scale); var valid = bright.Where(d => camera.Ray(d.Pixel) != null).ToArray();
            var rays = valid.Select(d => camera.Ray(d.Pixel)!.Value).ToArray();
            // Deterministic dispersed subset; no catalog IDs or truth positions select image triangles.
            var patterns = Triangles(rays, maxSpan, tolerance * .5, control).OrderBy(t => unchecked((uint)(t.A * 73856093 ^ t.B * 19349663 ^ t.C * 83492791))).Take(o.SampledTriangles).ToArray();
            imageCount += patterns.Length;
            foreach (var p in patterns)
            {
                var key = Key(p, tolerance);
                for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                {
                    if (!index.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket)) continue;
                    foreach (var t in bucket)
                    {
                        control?.Check();
                        if (Math.Abs(t.X - p.X) > tolerance || Math.Abs(t.Y - p.Y) > tolerance || Math.Abs(t.Z - p.Z) > tolerance) continue;
                        if (++hypotheses > o.MaximumHypotheses) { exhausted = true; goto SearchFinished; }
                        var rotation = AstrometricRotation.FromPairs(rays[p.A], rays[p.B], indexed[t.A].Ray, indexed[t.B].Ray);
                        // Proper rotation + third vertex rejects reflection matches before expensive scoring.
                        if (Angle(rotation.Apply(rays[p.C]), indexed[t.C].Ray) > tolerance * 2) continue;
                        var matches = Match(training, grid, camera, rotation, 7.5);
                        if (matches.Count < 12) continue;
                        if (rawCandidates.Any(c => c.Rotation.SeparationDegrees(rotation) < .8 && Math.Abs(c.Scale - scale) < .015 && SameAssignments(c.Matches, matches))) continue;
                        rawCandidates.Add(new(rotation, scale, matches));
                        rawCandidates = rawCandidates.OrderByDescending(c => c.Matches.Count).ThenBy(c => AstrometricMath.Rms(c.Matches.Select(m => m.Distance))).Take(o.MaximumCandidates).ToList();
                    }
                }
            }
        }
    SearchFinished:
        if (exhausted) return Reject("Hypothesis budget exhausted; no configuration update is offered", candidates: rawCandidates.Count);
        var refined = new List<Candidate>();
        foreach (var candidate in rawCandidates)
        {
            var c = Refine(candidate, training, grid, configuration, o, control: control);
            if (refined.Any(r => r.Rotation.SeparationDegrees(c.Rotation) < .03 && Math.Abs(r.Scale - c.Scale) < .002 && SameAssignments(r.Matches, c.Matches))) continue;
            refined.Add(c);
        }
        refined = refined.OrderByDescending(c => c.Matches.Count).ThenBy(c => AstrometricMath.Rms(c.Matches.Select(m => m.Distance))).ToList();
        if (refined.Count == 0) return Reject("No catalog pattern met the acquisition gate");
        if (acquisition is not null)
        {
            acquisition.AddRange(refined.Select(c => new CoreCandidate(c.Rotation, c.Scale,
                [.. c.Matches.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, false))],
                AstrometricMath.Rms(c.Matches.Select(m => m.Distance)))));
            return new(false, "acquired", "Refined acquisition candidates returned without verification or quality gates", null, null, null, [],
                indexed.Count, triangleCount, imageCount, hypotheses, refined.Count, exhausted, watch.Elapsed.TotalMilliseconds, domain);
        }
        var evaluated = refined.Select(c => { control?.Check(); return Evaluate(c, stars, training, verification, detections, configuration); }).ToArray();
        var accepted = evaluated.Where(c => c.Quality.Status == "accepted").ToArray();
        if (accepted.Length > 1) return Reject("Ambiguous: multiple independently verified orientations", evaluated[0].Quality with { Status = "rejected", Score0To100 = 0, Reasons = ["Multiple distinct verified solutions"] }, refined.Count);
        if (accepted.Length == 0) return Reject("Independent verification, residual, count, or coverage gates failed", evaluated[0].Quality, refined.Count);
        var best = accepted[0]; var pose = best.Candidate.Rotation.ToPose();
        var solution = configuration with { BoresightAltitude = pose.Altitude, BoresightAzimuth = pose.Azimuth, Roll = pose.Roll, FocalX = configuration.FocalX * best.Candidate.Scale, FocalY = configuration.FocalY * best.Candidate.Scale };
        return new(true, "accepted", "Catalog-pattern acquisition and withheld-star verification passed; proposed configuration only", solution, best.Candidate.Scale, best.Quality,
            best.Candidate.Matches.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, false)).Concat(best.Held.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, true))).ToArray(),
            indexed.Count, triangleCount, imageCount, hypotheses, refined.Count, exhausted, watch.Elapsed.TotalMilliseconds, domain);
    }
    /// <summary>Trusted previous solution supplies the local seed. No triangle/index search is performed.</summary>
    public static CoreResult SolveWarm(IReadOnlyList<CoreDetection> detections, IReadOnlyList<CelestialCatalogObject> catalog,
        CoreResult previous, CoreSite site, DateTimeOffset utc, double budgetMilliseconds, double maximumCatalogMagnitude, double priorAbsoluteScale, double minimumAbsoluteScale, double maximumAbsoluteScale, AstrometricWorkControl? control = null)
    {
        if (!previous.Accepted || previous.Solution is not { } config) throw new ArgumentException("Warm start requires a previously accepted solution");
        if (!double.IsFinite(budgetMilliseconds) || budgetMilliseconds <= 0 || !double.IsFinite(maximumCatalogMagnitude)) throw new ArgumentException("Invalid warm budget/magnitude limit");
        if (!double.IsFinite(priorAbsoluteScale) || priorAbsoluteScale <= 0 || !double.IsFinite(minimumAbsoluteScale) || minimumAbsoluteScale <= 0 || !double.IsFinite(maximumAbsoluteScale) || maximumAbsoluteScale < minimumAbsoluteScale) throw new ArgumentException("Invalid absolute warm focal-scale interval");
        config.Context();
        if (catalog.Count > 2500 || detections.Count > 10000 || catalog.Select(s => s.Id).Distinct().Count() != catalog.Count || detections.Select(d => d.Index).Distinct().Count() != detections.Count || detections.Any(d => !double.IsFinite(d.X) || !double.IsFinite(d.Y) || !double.IsFinite(d.Flux) || d.Flux < 0)) throw new ArgumentException("Invalid warm inputs");
        var watch = Stopwatch.StartNew();
        const string domain = "Warm local refinement from prior accepted configuration; no global uniqueness search;500ms default cooperative budget";
        CoreResult Rejected(string reason, CoreQuality? quality = null, bool timeout = false) => new(false, "rejected", reason, null, null, quality, [], 0, 0, 0, 0, 1, timeout, watch.Elapsed.TotalMilliseconds, domain);
        if (detections.Count < 12) return Rejected("Warm quality failure: fewer than12 detections; retain last good configuration");
        var stars = catalog.Where(s => s.Magnitude <= maximumCatalogMagnitude).Select(s => new Star(s, CameraBasis.FromHorizontal(AstrometricMath.Horizontal(s, utc, site)))).Where(s => s.Ray.Up > 0).ToList();
        var training = stars.Where(s => !IsVerification(s.Catalog.Id)).ToList(); var verification = stars.Where(s => IsVerification(s.Catalog.Id)).ToList();
        var rotation = AstrometricRotation.FromPose(new(config.BoresightAltitude, config.BoresightAzimuth, config.Roll));
        // Scale is relative to the prior optical model here, but settings describe the immutable calibration.
        var minimumRelativeScale = Math.Max(Math.Max(.98, MinimumPhysicalScale(config)), minimumAbsoluteScale / priorAbsoluteScale);
        var maximumRelativeScale = Math.Min(1.02, maximumAbsoluteScale / priorAbsoluteScale);
        if (minimumRelativeScale > maximumRelativeScale) return Rejected("Warm focal-scale interval is empty; retain last good configuration and reacquire");
        var bounds = new CoreSolverOptions(MinimumScale: minimumRelativeScale, MaximumScale: maximumRelativeScale, MaximumCatalogMagnitude: maximumCatalogMagnitude);
        var candidate = Refine(new(rotation, Math.Clamp(1, minimumRelativeScale, maximumRelativeScale), []), training, new CoreDetectionGrid(detections), config, bounds, () => watch.Elapsed.TotalMilliseconds > budgetMilliseconds, control);
        if (watch.Elapsed.TotalMilliseconds > budgetMilliseconds) return Rejected("Warm time budget exceeded; retain last good configuration and attempt cold recovery", timeout: true);
        if (rotation.SeparationDegrees(candidate.Rotation) > 1) return Rejected("Warm correction exceeds1degree local trust region; retain last good configuration and reacquire");
        var evaluation = Evaluate(candidate, stars, training, verification, detections, config);
        if (watch.Elapsed.TotalMilliseconds > budgetMilliseconds) return Rejected("Warm verification exceeded time budget; retain last good configuration and attempt cold recovery", timeout: true);
        if (evaluation.Quality.Status != "accepted") return Rejected("Warm quality gates failed; retain last good configuration and attempt cold recovery", evaluation.Quality);
        var pose = candidate.Rotation.ToPose(); var solution = config with { BoresightAltitude = pose.Altitude, BoresightAzimuth = pose.Azimuth, Roll = pose.Roll, FocalX = config.FocalX * candidate.Scale, FocalY = config.FocalY * candidate.Scale };
        var associations = evaluation.Candidate.Matches.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, false)).Concat(evaluation.Held.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, true))).ToArray();
        return new(true, "accepted", "Warm local fit and withheld-star gates passed; proposed next configuration", solution, candidate.Scale, evaluation.Quality, associations, 0, 0, 0, 0, 1, false, watch.Elapsed.TotalMilliseconds, domain);
    }
    /// <summary>
    /// Number of leading rays whose index triangles fit <paramref name="maximumTriangles"/>, or every ray when they all
    /// do. Rays are admitted in order, counting the triangles each closes with the rays before it under the same
    /// acceptance rule as <see cref="Triangles"/>, so the enumeration stops as soon as the bound is passed rather than
    /// growing with the number of rays above the horizon.
    /// </summary>
    internal static int IndexPrefix(EnuVector[] rays, double maxSpan, double minimumSide, long maximumTriangles, AstrometricWorkControl? control = null)
    {
        var distances = new double[rays.Length * rays.Length]; var count = 0L;
        for (var c = 0; c < rays.Length; c++)
        {
            for (var a = 0; a < c; a++) distances[a * rays.Length + c] = distances[c * rays.Length + a] = Angle(rays[a], rays[c]);
            for (var a = 0; a < c; a++)
            {
                control?.Check();
                var ac = distances[a * rays.Length + c]; if (ac < minimumSide || ac > maxSpan) continue;
                for (var b = a + 1; b < c; b++)
                    if (IsIndexTriangle(rays[a], rays[b], rays[c], distances[a * rays.Length + b], ac, distances[b * rays.Length + c], maxSpan, minimumSide) &&
                        ++count > maximumTriangles) return c;
            }
        }
        return rays.Length;
    }
    /// <summary>Number of triangles <see cref="Triangles"/> enumerates; exposed for bound-check tests.</summary>
    internal static int CountTriangles(EnuVector[] rays, double maxSpan, double minimumSide) => Triangles(rays, maxSpan, minimumSide).Count();
    /// <summary>
    /// Smallest focal scale whose scaled aperture edge stays inside the family and distortion domain. Scaling focal
    /// length by <c>s</c> divides the normalized edge radius by <c>s</c>, for circular and rectangular apertures alike.
    /// </summary>
    internal static double MinimumPhysicalScale(SolverOptics config)
    {
        if (config.RadialDistortionK1 != 0)
        {
            var maximum = RadialDistortion.MaximumDistortedRadius(config.Model, config.RadialDistortionK1);
            return double.IsFinite(maximum) ? RadialDistortion.ApertureEdgeRadius(config.Context()) / maximum : 0;
        }
        return config.Model switch
        {
            ProjectionModel.EquidistantFisheye => config.CircleRadius!.Value / (Math.PI * config.FocalX),
            ProjectionModel.EquisolidFisheye => config.CircleRadius!.Value / (2 * config.FocalX),
            ProjectionModel.OrthographicFisheye => config.CircleRadius!.Value / config.FocalX,
            _ => 0
        };
    }
    private static (Candidate Candidate, List<Pair> Held, CoreQuality Quality) Evaluate(Candidate c, List<Star> stars, List<Star> training,
        List<Star> verification, IReadOnlyList<CoreDetection> detections, SolverOptics configuration)
    {
        var camera = new AstrometricRayCamera(configuration, c.Scale);
        // Candidate-projected isolation approximates the detector's12px close-source exclusion.
        // Catalog magnitude eligibility is an explicit input, not knowledge of rendered footprints.
        var projected = stars.Select(s => (Star: s, Pixel: camera.Pixel(s.Ray, c.Rotation))).Where(s => s.Pixel is { } p && p.X > 6 && p.Y > 6 && p.X < configuration.Width - 6 && p.Y < configuration.Height - 6).ToArray();
        var eligible = projected.Where(s => projected.All(t => t.Star.Catalog.Id == s.Star.Catalog.Id || AstrometricMath.Distance(t.Pixel!.Value, s.Pixel!.Value) > 12)).Select(s => s.Star.Catalog.Id).ToHashSet();
        var finalFit = c.Matches.Where(m => eligible.Contains(m.Star.Catalog.Id)).ToList();
        var used = finalFit.Select(m => m.CoreDetection.Index).ToHashSet();
        var held = Match(verification.Where(s => eligible.Contains(s.Catalog.Id)).ToList(), new CoreDetectionGrid(detections.Where(d => !used.Contains(d.Index))), camera, c.Rotation, 1.5);
        var expected = training.Count(s => eligible.Contains(s.Catalog.Id));
        return (c with { Matches = finalFit }, held, Quality(finalFit, held, expected, configuration));
    }
    public static bool IsVerification(string id)
    {
        uint hash = 2166136261;
        foreach (var ch in id) { hash ^= ch; hash = unchecked(hash * 16777619); }
        return hash % 5 == 0;
    }
    private static IEnumerable<Triangle> Triangles(EnuVector[] rays, double maxSpan, double minimumSide, AstrometricWorkControl? control = null)
    {
        var distances = new double[rays.Length * rays.Length];
        for (var i = 0; i < rays.Length; i++) for (var j = i + 1; j < rays.Length; j++) distances[i * rays.Length + j] = distances[j * rays.Length + i] = Angle(rays[i], rays[j]);
        for (var a = 0; a < rays.Length; a++) for (var b = a + 1; b < rays.Length; b++)
        {
            control?.Check();
            var ab = distances[a * rays.Length + b]; if (ab < minimumSide || ab > maxSpan) continue;
            for (var c = b + 1; c < rays.Length; c++)
            {
                var ac = distances[a * rays.Length + c]; var bc = distances[b * rays.Length + c];
                if (!IsIndexTriangle(rays[a], rays[b], rays[c], ab, ac, bc, maxSpan, minimumSide)) continue;
                // Ordering by opposite side gives rotation-invariant correspondence.
                var side = new[] { (Angle: bc, Vertex: a), (Angle: ac, Vertex: b), (Angle: ab, Vertex: c) }.OrderBy(s => s.Angle).ToArray();
                yield return new(side[0].Vertex, side[1].Vertex, side[2].Vertex, side[0].Angle, side[1].Angle, side[2].Angle);
            }
        }
    }
    private static bool IsIndexTriangle(EnuVector a, EnuVector b, EnuVector c, double ab, double ac, double bc, double maxSpan, double minimumSide)
    {
        if (ab < minimumSide || ac < minimumSide || bc < minimumSide || ab > maxSpan || ac > maxSpan || bc > maxSpan) return false;
        double low = Math.Min(ab, Math.Min(ac, bc)), high = Math.Max(ab, Math.Max(ac, bc)), middle = Math.Max(Math.Min(ab, ac), Math.Min(Math.Max(ab, ac), bc));
        if (middle - low < minimumSide || high - middle < minimumSide) return false;
        // Exclude near-collinear triangles.
        return Math.Abs(EnuVector.Dot(a, EnuVector.Cross(b, c))) >= Math.Sin(low) * Math.Sin(high) * .12;
    }
    private static (int, int, int) Key(Triangle t, double bin) => ((int)Math.Floor(t.X / bin), (int)Math.Floor(t.Y / bin), (int)Math.Floor(t.Z / bin));
    public static double Angle(EnuVector a, EnuVector b) => Math.Atan2(EnuVector.Cross(a, b).Length, EnuVector.Dot(a, b));
    private static List<Pair> Match(List<Star> stars, CoreDetectionGrid grid, AstrometricRayCamera camera, AstrometricRotation rotation, double radius)
    {
        var candidates = new List<Pair>();
        foreach (var star in stars)
        {
            if (camera.Pixel(star.Ray, rotation) is not { } p) continue;
            var nearby = grid.Near(p, radius).OrderBy(d => AstrometricMath.Distance(d.Pixel, p)).Take(2).ToArray();
            if (nearby.Length == 0) continue; var distance = AstrometricMath.Distance(nearby[0].Pixel, p);
            if (nearby.Length > 1 && AstrometricMath.Distance(nearby[1].Pixel, p) < distance * 1.5) continue;
            candidates.Add(new(star, nearby[0], p, distance));
        }
        var used = new HashSet<int>(); return candidates.OrderBy(m => m.Distance).Where(m => used.Add(m.CoreDetection.Index)).ToList();
    }
    private static Candidate Refine(Candidate candidate, List<Star> training, CoreDetectionGrid grid, SolverOptics config, CoreSolverOptions o, Func<bool>? expired = null, AstrometricWorkControl? control = null)
    {
        var rotation = candidate.Rotation; var scale = candidate.Scale;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            control?.Check();
            if (expired?.Invoke() == true) break;
            var camera = new AstrometricRayCamera(config, scale); var matches = Match(training, grid, camera, rotation, iteration < 5 ? 7.5 : 2);
            if (matches.Count < 8) break;
            var normal = new double[16]; var rhs = new double[4]; const double step = 1e-5;
            var turns = new[] { rotation.Increment(step, 0, 0), rotation.Increment(0, step, 0), rotation.Increment(0, 0, step), rotation };
            var scaleCamera = new AstrometricRayCamera(config, scale * Math.Exp(step));
            foreach (var m in matches)
            {
                var derivatives = new PixelPoint?[4];
                for (var k = 0; k < 4; k++) derivatives[k] = (k == 3 ? scaleCamera : camera).Pixel(m.Star.Ray, turns[k]);
                if (derivatives.Any(p => p is null)) continue;
                var dx = derivatives.Select(p => (p!.Value.X - m.Predicted.X) / step).ToArray(); var dy = derivatives.Select(p => (p!.Value.Y - m.Predicted.Y) / step).ToArray();
                var weight = Math.Min(1, .8 / Math.Max(.001, m.Distance));
                for (var j = 0; j < 4; j++)
                {
                    rhs[j] += weight * (dx[j] * (m.CoreDetection.X - m.Predicted.X) + dy[j] * (m.CoreDetection.Y - m.Predicted.Y));
                    for (var k = 0; k < 4; k++) normal[j * 4 + k] += weight * (dx[j] * dx[k] + dy[j] * dy[k]);
                }
            }
            var delta = LinearSolve(normal, rhs); rotation = rotation.Increment(Math.Clamp(delta[0], -.03, .03), Math.Clamp(delta[1], -.03, .03), Math.Clamp(delta[2], -.03, .03));
            scale = Math.Clamp(scale * Math.Exp(Math.Clamp(delta[3], -.02, .02)), o.MinimumScale, o.MaximumScale);
            if (delta.Sum(Math.Abs) < 1e-8) break;
        }
        return new(rotation, scale, Match(training, grid, new AstrometricRayCamera(config, scale), rotation, 1.5));
    }
    private static bool SameAssignments(List<Pair> a, List<Pair> b)
    {
        // Nearby pointings can be distinct solutions at high focal lengths. Never merge
        // candidates solely by angular distance when their catalog correspondences differ.
        var keys = a.Select(m => (m.Star.Catalog.Id, m.CoreDetection.Index)).ToHashSet();
        return b.Count(m => keys.Contains((m.Star.Catalog.Id, m.CoreDetection.Index))) >= .8 * Math.Max(a.Count, b.Count);
    }
    private static CoreQuality Quality(List<Pair> fit, List<Pair> held, int expected, SolverOptics config)
    {
        var rms = fit.Count > 0 ? AstrometricMath.Rms(fit.Select(m => m.Distance)) : 999; var hrms = held.Count > 0 ? AstrometricMath.Rms(held.Select(m => m.Distance)) : 999;
        var width = fit.Count > 0 ? (fit.Max(m => m.CoreDetection.X) - fit.Min(m => m.CoreDetection.X)) / config.Width : 0;
        var height = fit.Count > 0 ? (fit.Max(m => m.CoreDetection.Y) - fit.Min(m => m.CoreDetection.Y)) / config.Height : 0;
        var reasons = new List<string>();
        if (fit.Count < 12) reasons.Add("Fewer than12 fitting stars"); if (held.Count < 4) reasons.Add("Fewer than4 withheld stars");
        if (fit.Count / (double)Math.Max(1, expected) < .5) reasons.Add("Below50% expected catalog recovery");
        if (rms > .4 || hrms > .5) reasons.Add("Residual limit exceeded"); if (width < .35 || height < .35) reasons.Add("Insufficient two-dimensional image coverage");
        var score = 100 * (.3 * Math.Exp(-Math.Pow(rms / .3, 2)) + .3 * Math.Exp(-Math.Pow(hrms / .3, 2)) + .2 * Math.Min(1, fit.Count / 40d) + .2 * Math.Min(1, Math.Min(width, height) / .7));
        return new(Math.Round(reasons.Count == 0 ? score : Math.Min(24, score), 1), reasons.Count == 0 ? "accepted" : "rejected", false, fit.Count, held.Count, expected, rms, hrms, width, height, reasons.ToArray());
    }
    private static double[] LinearSolve(double[] a, double[] b)
    {
        var n = b.Length;
        for (var j = 0; j < n; j++)
        {
            a[j * n + j] += 1e-8; var pivot = Enumerable.Range(j, n - j).MaxBy(i => Math.Abs(a[i * n + j]));
            for (var k = j; k < n; k++) (a[j * n + k], a[pivot * n + k]) = (a[pivot * n + k], a[j * n + k]); (b[j], b[pivot]) = (b[pivot], b[j]);
            var divisor = a[j * n + j]; for (var k = j; k < n; k++) a[j * n + k] /= divisor; b[j] /= divisor;
            for (var i = 0; i < n; i++) if (i != j) { var f = a[i * n + j]; for (var k = j; k < n; k++) a[i * n + k] -= f * a[j * n + k]; b[i] -= f * b[j]; }
        }
        return b;
    }
    internal sealed class CoreDetectionGrid
    {
        private readonly Dictionary<(int, int), List<CoreDetection>> cells = [];
        public CoreDetectionGrid(IEnumerable<CoreDetection> detections)
        { foreach (var d in detections) { var key = ((int)Math.Floor(d.X / 8), (int)Math.Floor(d.Y / 8)); if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = []; cell.Add(d); } }
        public IEnumerable<CoreDetection> Near(PixelPoint p, double radius)
        {
            for (var y = (int)Math.Floor((p.Y - radius) / 8); y <= (int)Math.Floor((p.Y + radius) / 8); y++) for (var x = (int)Math.Floor((p.X - radius) / 8); x <= (int)Math.Floor((p.X + radius) / 8); x++)
                if (cells.TryGetValue((x, y), out var values)) foreach (var d in values) if (AstrometricMath.Distance(d.Pixel, p) < radius) yield return d;
        }
    }
}
