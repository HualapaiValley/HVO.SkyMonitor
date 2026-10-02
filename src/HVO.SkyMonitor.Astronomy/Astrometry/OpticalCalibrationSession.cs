using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Fits one bounded session optical model (focal scale, principal point and an optional single radial term) for a
/// known projection family with fixed parity, jointly over several frames whose sky has rotated against the
/// camera. Each frame keeps its own pose. Starting values are the nominal optics; acquisition seeds every pose by
/// the ordinary blind search. Withheld frames are then solved independently with the derived readout views.
/// This API proposes evidence only; activation, review and persistence belong to hosts.
/// </summary>
public static class OpticalCalibrationSession
{
    private static readonly double[] AssociationRadii = [7.5, 4, 2.5, 1.5];
    private const double VerificationRadius = 1.5;
    private const double IsolationPixels = 12;
    private const double EdgeMarginPixels = 6;
    private const string CandidateVersion = "optical-calibration-candidate";

    private sealed record StarRay(CelestialCatalogObject Catalog, EnuVector Ray);
    private sealed record Association(StarRay Star, CoreDetection Detection, PixelPoint Predicted, double Distance);
    private sealed class FrameWork(OpticalCalibrationFrame input, CoreDetection[] detections, StarRay[] training, StarRay[] verification)
    {
        public OpticalCalibrationFrame Input { get; } = input;
        public FrameReadoutDescriptor Readout => Input.Readout;
        public CoreDetection[] Detections { get; } = detections;
        public AstrometricSolverCore.CoreDetectionGrid Grid { get; } = new(detections);
        public StarRay[] Training { get; } = training;
        public StarRay[] Verification { get; } = verification;
        public AstrometricRotation Rotation { get; set; }
        public double AcquiredScale { get; set; } = 1;
        public int Candidates { get; set; }
        public string Status { get; set; } = "pending";
        public int FitStars { get; set; }
        public int HeldStars { get; set; }
        public double? FitRms { get; set; }
        public double? HeldRms { get; set; }
    }

    /// <summary>Shared session optics in the native sensor frame.</summary>
    internal readonly record struct Shared(double LogScale, double Cx, double Cy, double K1)
    {
        public double this[int index] => index switch { 0 => LogScale, 1 => Cx, 2 => Cy, _ => K1 };
        public Shared With(int index, double value) => index switch
        {
            0 => this with { LogScale = value },
            1 => this with { Cx = value },
            2 => this with { Cy = value },
            _ => this with { K1 = value }
        };
    }

    public static OpticalCalibrationResult Fit(ProjectionContext nominalNative, IReadOnlyList<OpticalCalibrationFrame> frames,
        IReadOnlyList<OpticalCalibrationFrame> validationFrames, AstrometricCatalogData catalog,
        OpticalCalibrationOptions? options = null, AstrometricSolverOptions? solverOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames); ArgumentNullException.ThrowIfNull(validationFrames); ArgumentNullException.ThrowIfNull(catalog);
        options ??= new(); solverOptions ??= new();
        options.Validate(); solverOptions.Validate();
        nominalNative.Validate();
        if (!nominalNative.EnforceSensorBounds || (long)nominalNative.WidthPixels * nominalNative.HeightPixels > 16_777_216)
            throw new ArgumentException("Session calibration requires a sensor-bounded native model of at most16M pixels.", nameof(nominalNative));
        if (options.FitRadialDistortion && Math.Abs(nominalNative.RadialDistortionK1) > options.MaximumAbsoluteRadialDistortionK1)
            throw new ArgumentException("The nominal radial coefficient lies outside the declared fit bounds.", nameof(nominalNative));
        if (frames.Count > options.MaximumFrames || validationFrames.Count > options.MaximumFrames)
            throw new ArgumentException("Frame count exceeds the declared session bound.", nameof(frames));
        var all = frames.Concat(validationFrames).ToArray();
        foreach (var frame in all) ValidateFrame(frame, nominalNative);
        if (all.Select(f => f.Frame.CaptureId).Distinct().Count() != all.Length)
            throw new ArgumentException("Fit and validation frames must have distinct capture identities.", nameof(validationFrames));

        var inputIdentity = InputIdentitySha256(frames, validationFrames);
        var watch = Stopwatch.StartNew();
        var control = new AstrometricWorkControl(options.BudgetMilliseconds, cancellationToken);
        var shared0 = new Shared(0, nominalNative.PrincipalPointX, nominalNative.PrincipalPointY, nominalNative.RadialDistortionK1);
        var works = new List<FrameWork>();
        double acquisitionMs = 0, fitMs = 0, validationMs = 0;
        var skyRotation = SkyRotationDegrees(frames);

        OpticalCalibrationResult Result(OpticalCalibrationStatus status, string code, string reason, IReadOnlyList<string> rejections,
            Shared? shared = null, double?[]? errors = null, ProjectionContext? calibrated = null, Diagnostics? diagnostics = null,
            IEnumerable<OpticalCalibrationValidation>? validations = null) =>
            new(status, code, reason, rejections, nominalNative, calibrated,
                Parameters(nominalNative, options, shared ?? shared0, errors, shared is not null),
                works.Count > 0 ? works.Select(FrameSummary) : frames.Select(f => new OpticalCalibrationFrameResult(f.Frame.CaptureId, f.ReadoutIdentitySha256, "not-attempted", 0, null, null, null, 0, 0, null, null)),
                validations ?? [], (diagnostics ?? new Diagnostics()).ToContract(skyRotation, works.Count(w => w.Status == "fitted")),
                inputIdentity, catalog.IdentitySha256, catalog.SelectionIdentitySha256, options.IdentitySha256, solverOptions.IdentitySha256,
                new(watch.Elapsed.TotalMilliseconds, acquisitionMs, fitMs, validationMs));

        if (!catalog.IsCompleteForRequestedMagnitude || catalog.CompletenessMagnitudeLimit < solverOptions.MaximumCatalogMagnitude)
            return Result(OpticalCalibrationStatus.Unavailable, "catalog-incomplete", "Catalog source does not declare complete coverage through the requested magnitude ceiling; no fit was attempted.", ["catalog-incomplete"]);
        if (catalog.CoordinateModel != AstrometricConventions.CoordinateModel)
            return Result(OpticalCalibrationStatus.Unavailable, "coordinate-model-unsupported", "Only explicitly declared fixed-position J2000 precession is supported.", ["coordinate-model-unsupported"]);
        if (frames.Count < options.MinimumFrames)
            return Result(OpticalCalibrationStatus.Rejected, "insufficient-frames", $"At least {options.MinimumFrames} fit frames are required.", ["insufficient-frames"]);
        if (validationFrames.Count < options.MinimumValidationFrames)
            return Result(OpticalCalibrationStatus.Rejected, "insufficient-validation-frames", $"At least {options.MinimumValidationFrames} withheld frames are required.", ["insufficient-validation-frames"]);
        if (skyRotation < options.MinimumSkyRotationDegrees)
            return Result(OpticalCalibrationStatus.Rejected, "insufficient-sky-rotation", "Fit frames do not span the declared sky rotation, so pose and optics cannot be separated.", ["insufficient-sky-rotation"]);

        try
        {
            // Acquisition: the ordinary bounded blind search at nominal optics seeds each frame's pose.
            var phase = Stopwatch.StartNew();
            var search = new CoreSolverOptions(solverOptions.MinimumFocalScale, solverOptions.MaximumFocalScale, solverOptions.FocalScaleStep,
                solverOptions.TriangleDetectionCount, solverOptions.ImageTriangleLimit, solverOptions.HypothesisLimit, solverOptions.CandidateLimit,
                solverOptions.MaximumCatalogMagnitude);
            var acquisitionRejections = new List<string>();
            foreach (var frame in frames)
            {
                control.Check();
                var work = Prepare(frame, catalog, solverOptions.MaximumCatalogMagnitude);
                works.Add(work);
                var view = RigProjectionContextFactory.CreateReadoutView(nominalNative, frame.Readout);
                var candidates = new List<CoreCandidate>();
                var core = AstrometricSolverCore.Acquire(work.Detections, catalog.Stars, SolverOptics.From(view), Site(frame), frame.Frame.MidpointUtc, candidates, search, control);
                work.Candidates = candidates.Count;
                if (candidates.Count == 0)
                {
                    work.Status = core.BudgetExhausted ? "acquisition-budget" : "acquisition-failed";
                    acquisitionRejections.Add($"{work.Status}:{frame.Frame.CaptureId}");
                    continue;
                }
                var best = candidates[0];
                if (candidates.Skip(1).Any(c => c.Rotation.SeparationDegrees(best.Rotation) > options.AmbiguitySeparationDegrees &&
                    c.Matches.Length >= options.AmbiguityMatchFraction * best.Matches.Length))
                {
                    work.Status = "ambiguous";
                    acquisitionRejections.Add($"ambiguous:{frame.Frame.CaptureId}");
                    continue;
                }
                work.Rotation = best.Rotation; work.AcquiredScale = best.Scale; work.Status = "acquired";
            }
            acquisitionMs = phase.Elapsed.TotalMilliseconds;
            if (acquisitionRejections.Count > 0)
            {
                var code = acquisitionRejections.Any(r => r.StartsWith("ambiguous", StringComparison.Ordinal)) ? "ambiguous" :
                    acquisitionRejections.Any(r => r.StartsWith("acquisition-budget", StringComparison.Ordinal)) ? "acquisition-budget" : "acquisition-failed";
                return Result(code == "acquisition-budget" ? OpticalCalibrationStatus.BudgetExceeded : OpticalCalibrationStatus.Rejected, code,
                    "One or more fit frames had no unique acquisition at nominal optics; no session optics are proposed.", acquisitionRejections);
            }

            // Joint bounded fit: shared optics plus one rotation per frame.
            phase.Restart();
            var bounds = Bounds(nominalNative, options);
            var scales = works.Select(w => w.AcquiredScale).Order().ToArray();
            var start = shared0 with { LogScale = Math.Clamp(Math.Log(scales[scales.Length / 2]), bounds.Min[0], bounds.Max[0]) };
            var fit = Optimize(nominalNative, works, start, bounds, options, control);
            var evaluation = Evaluate(nominalNative, works, fit, bounds, options);
            fitMs = phase.Elapsed.TotalMilliseconds;
            var calibrated = Native(nominalNative, fit.Shared);
            if (evaluation.Rejections.Count > 0)
                return Result(OpticalCalibrationStatus.Rejected, Code(evaluation.Rejections[0]),
                    "Session support, conditioning, bound or residual gates failed; the previously accepted calibration remains authoritative.",
                    evaluation.Rejections, fit.Shared, evaluation.Errors, null, evaluation);

            // Withheld frames: ordinary single-frame solves through the derived readout views of the candidate.
            phase.Restart();
            var validations = new List<OpticalCalibrationValidation>();
            var validationRejections = new List<string>();
            foreach (var frame in validationFrames)
            {
                control.Check();
                var calibration = new AstrometricCalibration(RigProjectionContextFactory.CreateReadoutView(calibrated, frame.Readout), CandidateVersion, frame.ReadoutIdentitySha256);
                var remaining = Math.Max(1, options.BudgetMilliseconds - control.ElapsedMilliseconds);
                var solved = AstrometricSolver.Solve(frame.Frame, calibration, catalog, frame.Detections,
                    solverOptions with { ColdBudgetMilliseconds = Math.Min(solverOptions.ColdBudgetMilliseconds, remaining) }, cancellationToken);
                var a = solved.Assessment;
                validations.Add(new(frame.Frame.CaptureId, a.Status, a.ReasonCode, a.Parameters?.FocalScale, a.Quality?.FittingRmsPixels,
                    a.Quality?.VerificationRmsPixels, a.IdentitySha256));
                if (a.Status == AstrometricAssessmentStatus.BudgetExceeded) throw new TimeoutException("Validation solve exceeded its budget.");
                if (!a.HasMeasuredMapping) validationRejections.Add($"validation-failed:{frame.Frame.CaptureId}");
                else if (Math.Abs(a.Parameters!.FocalScale - 1) > options.MaximumValidationFocalScaleDeviation)
                    validationRejections.Add($"validation-focal-scale:{frame.Frame.CaptureId}");
            }
            validationMs = phase.Elapsed.TotalMilliseconds;
            if (validationRejections.Count > 0)
                return Result(OpticalCalibrationStatus.Rejected, Code(validationRejections[0]),
                    "A withheld frame did not solve independently under the candidate calibration; the previously accepted calibration remains authoritative.",
                    validationRejections, fit.Shared, evaluation.Errors, null, evaluation, validations);
            return Result(OpticalCalibrationStatus.Accepted, "accepted",
                "Joint session fit, support, conditioning, residual and withheld-frame gates passed; proposed calibration only.",
                [], fit.Shared, evaluation.Errors, calibrated, evaluation, validations);
        }
        catch (TimeoutException)
        {
            return Result(OpticalCalibrationStatus.BudgetExceeded, "time-budget",
                "Cooperative calibration budget exceeded; no proposed calibration replaces the accepted one.", ["time-budget"]);
        }
    }

    /// <summary>
    /// Identity of the offered evidence: each fit and withheld frame's complete context, readout declaration and
    /// identity, and ordered detections. Distinct measurements therefore never share a retained calibration identity.
    /// </summary>
    public static string InputIdentitySha256(IReadOnlyList<OpticalCalibrationFrame> frames, IReadOnlyList<OpticalCalibrationFrame> validationFrames)
    {
        ArgumentNullException.ThrowIfNull(frames); ArgumentNullException.ThrowIfNull(validationFrames);
        static object Entry(OpticalCalibrationFrame f) => new { f.Frame, f.Readout, f.ReadoutIdentitySha256, f.Detections };
        return AstrometricIdentity.Hash(new
        {
            schema = "optical-calibration-input-v1",
            fit = frames.Select(Entry).ToArray(),
            validation = validationFrames.Select(Entry).ToArray()
        });
    }

    /// <summary>Applies session optics to the nominal native model, keeping family, parity, aperture and orientation.</summary>
    private static ProjectionContext Native(ProjectionContext nominal, Shared shared) => nominal with
    {
        FocalLengthXPixels = nominal.FocalLengthXPixels * Math.Exp(shared.LogScale),
        FocalLengthYPixels = nominal.FocalLengthYPixels * Math.Exp(shared.LogScale),
        PrincipalPointX = shared.Cx,
        PrincipalPointY = shared.Cy,
        RadialDistortionK1 = shared.K1
    };

    private static void ValidateFrame(OpticalCalibrationFrame frame, ProjectionContext nominal)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(frame.Frame);
        ArgumentNullException.ThrowIfNull(frame.Readout); ArgumentNullException.ThrowIfNull(frame.Detections);
        frame.Frame.Validate();
        AstrometricIdentity.RequireSha256(frame.ReadoutIdentitySha256);
        var view = RigProjectionContextFactory.CreateReadoutView(nominal, frame.Readout);
        if (frame.Detections.Count > 10000) throw new ArgumentException("Detection count exceeds10000.", nameof(frame));
        if (frame.Detections.Any(d => d is null || d.Index < 0 || !double.IsFinite(d.Pixel.X) || !double.IsFinite(d.Pixel.Y) ||
            d.Pixel.X < 0 || d.Pixel.Y < 0 || d.Pixel.X > view.WidthPixels || d.Pixel.Y > view.HeightPixels || !double.IsFinite(d.Flux) || d.Flux < 0) ||
            frame.Detections.Select(d => d.Index).Distinct().Count() != frame.Detections.Count)
            throw new ArgumentException("Invalid or duplicate measured centroids.", nameof(frame));
    }

    private static CoreSite Site(OpticalCalibrationFrame frame) => new(frame.Frame.Observer.LatitudeDegrees, frame.Frame.Observer.LongitudeDegrees);

    private static FrameWork Prepare(OpticalCalibrationFrame frame, AstrometricCatalogData catalog, double maximumMagnitude)
    {
        var site = Site(frame); var utc = frame.Frame.MidpointUtc;
        var stars = catalog.Stars.Where(s => s.Magnitude <= maximumMagnitude)
            .Select(s => new StarRay(s, CameraBasis.FromHorizontal(AstrometricMath.Horizontal(s, utc, site)))).Where(s => s.Ray.Up > 0).ToArray();
        var detections = frame.Detections.OrderBy(d => d.Index).Select(d => new CoreDetection(d.Index, d.Pixel.X, d.Pixel.Y, d.Flux, d.Flux)).ToArray();
        return new(frame, detections, [.. stars.Where(s => !AstrometricSolverCore.IsVerification(s.Catalog.Id))],
            [.. stars.Where(s => AstrometricSolverCore.IsVerification(s.Catalog.Id))]);
    }

    /// <summary>Circular span of local mean sidereal time across fit frames: how far the sky turned against the camera.</summary>
    private static double SkyRotationDegrees(IReadOnlyList<OpticalCalibrationFrame> frames)
    {
        if (frames.Count < 2) return 0;
        var angles = frames.Select(f => AstronomyTime.LocalMeanSiderealDegrees(f.Frame.MidpointUtc, f.Frame.Observer.LongitudeDegrees))
            .Select(a => (a % 360 + 360) % 360).Order().ToArray();
        var gap = 360 - angles[^1] + angles[0];
        for (var i = 1; i < angles.Length; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
        return 360 - gap;
    }

    internal sealed record ParameterBounds(double[] Min, double[] Max, bool[] Fitted);

    internal static ParameterBounds Bounds(ProjectionContext nominal, OpticalCalibrationOptions o)
    {
        var p = o.MaximumPrincipalPointOffsetPixels;
        var k = o.FitRadialDistortion ? o.MaximumAbsoluteRadialDistortionK1 : 0;
        return new(
            [Math.Log(1 - o.MaximumFocalScaleDeviation), nominal.PrincipalPointX - p, nominal.PrincipalPointY - p, o.FitRadialDistortion ? -k : nominal.RadialDistortionK1],
            [Math.Log(1 + o.MaximumFocalScaleDeviation), nominal.PrincipalPointX + p, nominal.PrincipalPointY + p, o.FitRadialDistortion ? k : nominal.RadialDistortionK1],
            [true, true, true, o.FitRadialDistortion]);
    }

    private sealed record FitState(Shared Shared, int Iterations, bool Converged, string? Failure);

    private static AstrometricRayCamera? Camera(ProjectionContext nominal, Shared shared)
    {
        try { return new AstrometricRayCamera(SolverOptics.From(Native(nominal, shared))); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static PixelPoint? Predict(AstrometricRayCamera camera, FrameReadoutDescriptor readout, AstrometricRotation rotation, EnuVector ray)
        => camera.Pixel(ray, rotation) is { } native
            ? new PixelPoint((native.X - readout.RoiX) / readout.BinX, (native.Y - readout.RoiY) / readout.BinY)
            : null;

    private static List<Association> Associate(AstrometricRayCamera camera, FrameWork frame, AstrometricRotation rotation,
        IEnumerable<StarRay> stars, AstrometricSolverCore.CoreDetectionGrid grid, double radius)
    {
        // Same deterministic rule as single-frame acquisition: nearest within the radius, a clear 1.5x margin
        // over the runner-up, then one-to-one by ascending distance.
        var candidates = new List<Association>();
        foreach (var star in stars)
        {
            if (Predict(camera, frame.Readout, rotation, star.Ray) is not { } p) continue;
            var nearby = grid.Near(p, radius).OrderBy(d => AstrometricMath.Distance(d.Pixel, p)).ThenBy(d => d.Index).Take(2).ToArray();
            if (nearby.Length == 0) continue;
            var distance = AstrometricMath.Distance(nearby[0].Pixel, p);
            if (nearby.Length > 1 && AstrometricMath.Distance(nearby[1].Pixel, p) < distance * 1.5) continue;
            candidates.Add(new(star, nearby[0], p, distance));
        }
        var used = new HashSet<int>();
        return candidates.OrderBy(m => m.Distance).ThenBy(m => m.Star.Catalog.Id, StringComparer.Ordinal).Where(m => used.Add(m.Detection.Index)).ToList();
    }

    private static readonly double[] SharedSteps = [1e-6, 1e-3, 1e-3, 1e-6];
    private const double RotationStep = 1e-6;

    /// <summary>
    /// One finite-difference camera per fitted shared parameter: forward where the step stays inside its bound and the
    /// supported domain, otherwise backward. Null when neither direction is supported, as at a distortion-domain boundary
    /// that both principal-point perturbations leave; the caller reports that instead of differentiating.
    /// </summary>
    internal static (AstrometricRayCamera Camera, double Step)[]? DerivativeCameras(ProjectionContext nominal, Shared shared, ParameterBounds bounds)
    {
        var sharedIndex = Enumerable.Range(0, 4).Where(i => bounds.Fitted[i]).ToArray();
        var shifted = new (AstrometricRayCamera Camera, double Step)[sharedIndex.Length];
        for (var j = 0; j < sharedIndex.Length; j++)
        {
            var index = sharedIndex[j]; var step = SharedSteps[index];
            var forward = shared[index] + step <= bounds.Max[index] ? Camera(nominal, shared.With(index, shared[index] + step)) : null;
            if (forward is not null) shifted[j] = (forward, step);
            else if (Camera(nominal, shared.With(index, shared[index] - step)) is { } backward) shifted[j] = (backward, -step);
            else return null;
        }
        return shifted;
    }

    /// <summary>Accumulates Huber-weighted normal equations for shared optics followed by three rotation terms per frame; null when a fitted parameter has no supported derivative direction.</summary>
    private static (double[] Normal, double[] Gradient, double Cost, int Rows)? Normal(ProjectionContext nominal, IReadOnlyList<FrameWork> works,
        List<Association>[] associations, Shared shared, ParameterBounds bounds, bool robust)
    {
        if (DerivativeCameras(nominal, shared, bounds) is not { } shifted) return null;
        var sharedIndex = Enumerable.Range(0, 4).Where(i => bounds.Fitted[i]).ToArray();
        var n = sharedIndex.Length + 3 * works.Count;
        var normal = new double[n * n]; var gradient = new double[n]; var cost = 0d; var rows = 0;
        var camera = Camera(nominal, shared)!;
        var derivative = new double[2 * n];
        for (var f = 0; f < works.Count; f++)
        {
            var work = works[f]; var rotation = work.Rotation;
            var turns = new[] { rotation.Increment(RotationStep, 0, 0), rotation.Increment(0, RotationStep, 0), rotation.Increment(0, 0, RotationStep) };
            var offset = sharedIndex.Length + 3 * f;
            foreach (var m in associations[f])
            {
                Array.Clear(derivative);
                var valid = true;
                for (var j = 0; j < sharedIndex.Length && valid; j++)
                {
                    if (Predict(shifted[j].Camera, work.Readout, rotation, m.Star.Ray) is not { } q) { valid = false; break; }
                    derivative[2 * j] = (q.X - m.Predicted.X) / shifted[j].Step; derivative[2 * j + 1] = (q.Y - m.Predicted.Y) / shifted[j].Step;
                }
                for (var k = 0; k < 3 && valid; k++)
                {
                    if (Predict(camera, work.Readout, turns[k], m.Star.Ray) is not { } q) { valid = false; break; }
                    derivative[2 * (offset + k)] = (q.X - m.Predicted.X) / RotationStep; derivative[2 * (offset + k) + 1] = (q.Y - m.Predicted.Y) / RotationStep;
                }
                if (!valid) continue;
                var rx = m.Detection.X - m.Predicted.X; var ry = m.Detection.Y - m.Predicted.Y;
                var weight = robust ? Math.Min(1, .8 / Math.Max(.001, m.Distance)) : 1;
                cost += weight * (rx * rx + ry * ry); rows += 2;
                var active = sharedIndex.Length;
                for (var a = 0; a < n; a++)
                {
                    if (a >= active && (a < offset || a >= offset + 3)) continue;
                    var ax = derivative[2 * a]; var ay = derivative[2 * a + 1];
                    gradient[a] += weight * (ax * rx + ay * ry);
                    for (var b = 0; b < n; b++)
                    {
                        if (b >= active && (b < offset || b >= offset + 3)) continue;
                        normal[a * n + b] += weight * (ax * derivative[2 * b] + ay * derivative[2 * b + 1]);
                    }
                }
            }
        }
        return (normal, gradient, cost, rows);
    }

    private static double Cost(ProjectionContext nominal, IReadOnlyList<FrameWork> works, List<Association>[] associations,
        Shared shared, AstrometricRotation[] rotations, double penaltyRadius)
    {
        if (Camera(nominal, shared) is not { } camera) return double.PositiveInfinity;
        var cost = 0d;
        for (var f = 0; f < works.Count; f++)
            foreach (var m in associations[f])
            {
                var weight = Math.Min(1, .8 / Math.Max(.001, m.Distance));
                if (Predict(camera, works[f].Readout, rotations[f], m.Star.Ray) is not { } q) { cost += weight * penaltyRadius * penaltyRadius; continue; }
                cost += weight * (Math.Pow(m.Detection.X - q.X, 2) + Math.Pow(m.Detection.Y - q.Y, 2));
            }
        return cost;
    }

    private static FitState Optimize(ProjectionContext nominal, List<FrameWork> works, Shared start, ParameterBounds bounds,
        OpticalCalibrationOptions options, AstrometricWorkControl control)
    {
        var shared = start; var lambda = 1e-3; var converged = false; var iteration = 0;
        var sharedIndex = Enumerable.Range(0, 4).Where(i => bounds.Fitted[i]).ToArray();
        if (Camera(nominal, shared) is null) return new(shared, 0, false, "nominal-unsupported");
        for (; iteration < options.MaximumIterations; iteration++)
        {
            control.Check();
            var stage = Math.Min(iteration / 4, AssociationRadii.Length - 1);
            var radius = AssociationRadii[stage];
            var camera = Camera(nominal, shared)!;
            var associations = works.Select(w => Associate(camera, w, w.Rotation, w.Training, w.Grid, radius)).ToArray();
            if (associations.Any(a => a.Count < options.MinimumFrameFittingStars)) return new(shared, iteration, false, "insufficient-associations");
            if (Normal(nominal, works, associations, shared, bounds, robust: true) is not { } system) return new(shared, iteration, false, "derivative-unsupported");
            var (normal, gradient, cost0, _) = system;
            var n = gradient.Length; var accepted = false; var decrease = 0d;
            for (var attempt = 0; attempt < 10 && !accepted; attempt++)
            {
                var a = (double[])normal.Clone();
                for (var i = 0; i < n; i++) a[i * n + i] += lambda * Math.Max(a[i * n + i], 1e-12) + 1e-12;
                if (Solve(a, (double[])gradient.Clone(), n) is not { } delta) { lambda *= 4; continue; }
                var candidate = shared;
                for (var j = 0; j < sharedIndex.Length; j++)
                {
                    var index = sharedIndex[j];
                    candidate = candidate.With(index, Math.Clamp(shared[index] + delta[j], bounds.Min[index], bounds.Max[index]));
                }
                var rotations = works.Select((w, f) => w.Rotation.Increment(
                    Math.Clamp(delta[sharedIndex.Length + 3 * f], -.03, .03), Math.Clamp(delta[sharedIndex.Length + 3 * f + 1], -.03, .03),
                    Math.Clamp(delta[sharedIndex.Length + 3 * f + 2], -.03, .03))).ToArray();
                var cost1 = Cost(nominal, works, associations, candidate, rotations, radius);
                if (cost1 < cost0)
                {
                    accepted = true; decrease = (cost0 - cost1) / Math.Max(cost0, 1e-300);
                    shared = candidate;
                    for (var f = 0; f < works.Count; f++) works[f].Rotation = rotations[f];
                    lambda = Math.Max(1e-9, lambda / 3);
                }
                else lambda *= 4;
            }
            if (stage == AssociationRadii.Length - 1 && iteration >= 4 * stage + 1 && (!accepted || decrease < 1e-10))
            {
                converged = true; iteration++;
                break;
            }
        }
        return new(shared, iteration, converged, null);
    }

    private sealed class Diagnostics
    {
        public List<string> Rejections { get; } = [];
        public double?[] Errors { get; set; } = new double?[4];
        public int FittingStars { get; set; }
        public int VerificationStars { get; set; }
        public double? FittingRms { get; set; }
        public double? VerificationRms { get; set; }
        public int OccupiedRadialBins { get; set; }
        public int OccupiedAzimuthBins { get; set; }
        public double? ConditionNumber { get; set; }
        public int Iterations { get; set; }
        public bool Converged { get; set; }
        public List<OpticalCalibrationResidualBin> Bins { get; } = [];
        public OpticalCalibrationDiagnostics ToContract(double skyRotation, int fittedFrames) => new(skyRotation, fittedFrames,
            FittingStars, VerificationStars, FittingRms, VerificationRms, OccupiedRadialBins, OccupiedAzimuthBins, ConditionNumber,
            Iterations, Converged, Bins.AsReadOnly());
    }

    private static Diagnostics Evaluate(ProjectionContext nominal, List<FrameWork> works, FitState fit, ParameterBounds bounds, OpticalCalibrationOptions o)
    {
        var result = new Diagnostics { Iterations = fit.Iterations, Converged = fit.Converged };
        if (fit.Failure is not null) { result.Rejections.Add(fit.Failure); return result; }
        if (!fit.Converged) result.Rejections.Add("not-converged");
        var shared = fit.Shared; var camera = Camera(nominal, shared)!;
        var native = Native(nominal, shared);
        var associations = new List<Association>[works.Count];
        var allFit = new List<(FrameWork Work, Association Match)>(); var allHeld = new List<Association>();
        for (var f = 0; f < works.Count; f++)
        {
            var work = works[f];
            var fitted = Associate(camera, work, work.Rotation, work.Training, work.Grid, AssociationRadii[^1]);
            associations[f] = fitted;
            var eligible = Isolated(camera, work);
            var used = fitted.Select(m => m.Detection.Index).ToHashSet();
            var held = Associate(camera, work, work.Rotation, work.Verification.Where(s => eligible.Contains(s.Catalog.Id)),
                new AstrometricSolverCore.CoreDetectionGrid(work.Detections.Where(d => !used.Contains(d.Index))), VerificationRadius);
            work.Status = "fitted";
            work.FitStars = fitted.Count; work.HeldStars = held.Count;
            work.FitRms = fitted.Count > 0 ? AstrometricMath.Rms(fitted.Select(m => m.Distance)) : null;
            work.HeldRms = held.Count > 0 ? AstrometricMath.Rms(held.Select(m => m.Distance)) : null;
            if (fitted.Count < o.MinimumFrameFittingStars) result.Rejections.Add($"frame-support:{work.Input.Frame.CaptureId}");
            allFit.AddRange(fitted.Select(m => (work, m))); allHeld.AddRange(held);
        }
        result.FittingStars = allFit.Count; result.VerificationStars = allHeld.Count;
        result.FittingRms = allFit.Count > 0 ? AstrometricMath.Rms(allFit.Select(m => m.Match.Distance)) : null;
        result.VerificationRms = allHeld.Count > 0 ? AstrometricMath.Rms(allHeld.Select(m => m.Distance)) : null;

        // Residual structure over the native aperture: signed radial component by radius and azimuth bins.
        var edge = native.Aperture == ProjectionAperture.Circular ? native.ImageCircleRadiusPixels!.Value :
            Math.Sqrt(Math.Pow(Math.Max(native.PrincipalPointX, native.WidthPixels - native.PrincipalPointX), 2) +
                Math.Pow(Math.Max(native.PrincipalPointY, native.HeightPixels - native.PrincipalPointY), 2));
        var radial = Enumerable.Range(0, o.RadialBins).Select(_ => new List<(double Radial, double Total)>()).ToArray();
        var azimuth = Enumerable.Range(0, o.AzimuthBins).Select(_ => new List<(double Radial, double Total)>()).ToArray();
        foreach (var (work, m) in allFit)
        {
            var r = work.Readout;
            var dx = r.RoiX + m.Predicted.X * r.BinX - native.PrincipalPointX; var dy = r.RoiY + m.Predicted.Y * r.BinY - native.PrincipalPointY;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            var radialResidual = ((m.Detection.X - m.Predicted.X) * dx + (m.Detection.Y - m.Predicted.Y) * dy) / length;
            radial[Math.Min(o.RadialBins - 1, (int)Math.Floor(length / edge * o.RadialBins))].Add((radialResidual, m.Distance));
            var angle = (Math.Atan2(-dy, dx) * 180 / Math.PI + 360) % 360;
            azimuth[Math.Min(o.AzimuthBins - 1, (int)Math.Floor(angle / 360 * o.AzimuthBins))].Add((radialResidual, m.Distance));
        }
        void AddBins(string axis, List<(double Radial, double Total)>[] bins, double width)
        {
            for (var i = 0; i < bins.Length; i++)
            {
                var values = bins[i]; double? mean = null, error = null, rms = null;
                if (values.Count > 0)
                {
                    mean = values.Average(v => v.Radial); rms = AstrometricMath.Rms(values.Select(v => v.Total));
                    if (values.Count > 1)
                        error = Math.Sqrt(values.Sum(v => Math.Pow(v.Radial - mean.Value, 2)) / (values.Count - 1) / values.Count);
                }
                result.Bins.Add(new(axis, i, i * width, (i + 1) * width, values.Count, mean, error, rms, values.Count >= o.MinimumStarsPerBin));
            }
        }
        AddBins("radius-fraction", radial, 1d / o.RadialBins);
        AddBins("azimuth-degrees", azimuth, 360d / o.AzimuthBins);
        result.OccupiedRadialBins = result.Bins.Count(b => b.Axis == "radius-fraction" && b.Supported);
        result.OccupiedAzimuthBins = result.Bins.Count(b => b.Axis == "azimuth-degrees" && b.Supported);
        if (result.OccupiedRadialBins < o.MinimumOccupiedRadialBins) result.Rejections.Add("insufficient-radial-support");
        if (result.OccupiedAzimuthBins < o.MinimumOccupiedAzimuthBins) result.Rejections.Add("insufficient-azimuth-support");
        if (result.VerificationStars < o.MinimumVerificationStars) result.Rejections.Add("insufficient-verification-stars");
        if (result.FittingRms is not { } fitRms || fitRms > o.MaximumFittingRmsPixels) result.Rejections.Add("fitting-residual");
        if (result.VerificationRms is not { } heldRms || heldRms > o.MaximumVerificationRmsPixels) result.Rejections.Add("verification-residual");

        // Bound hits: a parameter pinned to its declared limit is not a measurement.
        for (var i = 0; i < 4; i++)
        {
            if (!bounds.Fitted[i]) continue;
            var tolerance = 1e-9 * Math.Max(1, Math.Abs(bounds.Max[i]));
            if (shared[i] <= bounds.Min[i] + tolerance || shared[i] >= bounds.Max[i] - tolerance) result.Rejections.Add($"parameter-bound:{ParameterNames[i]}");
        }

        // Conditioning from the unweighted final normal equations: marginal (Schur-complement) covariance of the
        // shared optics after every per-frame pose is eliminated.
        var final = allFit.Count > 0 ? Normal(nominal, works, associations, shared, bounds, robust: false) : null;
        if (allFit.Count > 0 && final is null) result.Rejections.Add("derivative-unsupported");
        if (final is { } solved)
        {
            var (normal, _, cost, rows) = solved;
            var n = (int)Math.Round(Math.Sqrt(normal.Length));
            var sharedIndex = Enumerable.Range(0, 4).Where(i => bounds.Fitted[i]).ToArray();
            var sigma2 = cost / Math.Max(1, rows - n);
            var covariance = Invert(normal, n);
            if (covariance is null) result.Rejections.Add("ill-conditioned");
            else
            {
                var m = sharedIndex.Length; var block = new double[m * m];
                for (var a = 0; a < m; a++) for (var b = 0; b < m; b++) block[a * m + b] = sigma2 * covariance[a * n + b];
                var correlation = new double[m * m];
                for (var a = 0; a < m; a++) for (var b = 0; b < m; b++)
                    correlation[a * m + b] = block[a * m + b] / Math.Sqrt(Math.Max(1e-300, block[a * m + a] * block[b * m + b]));
                var eigen = SymmetricEigenvalues(correlation, m);
                result.ConditionNumber = eigen.Min() > 0 ? eigen.Max() / eigen.Min() : double.PositiveInfinity;
                if (!(result.ConditionNumber <= o.MaximumConditionNumber)) result.Rejections.Add("ill-conditioned");
                var limits = new[] { o.MaximumFocalScaleStandardError, o.MaximumPrincipalPointStandardErrorPixels, o.MaximumPrincipalPointStandardErrorPixels, o.MaximumRadialDistortionStandardError };
                for (var a = 0; a < m; a++)
                {
                    var index = sharedIndex[a];
                    var error = Math.Sqrt(Math.Max(0, block[a * m + a]));
                    // Log-scale error is converted to the reported linear focal-scale error.
                    result.Errors[index] = index == 0 ? error * Math.Exp(shared.LogScale) : error;
                    if (!(result.Errors[index] <= limits[index])) result.Rejections.Add($"underconstrained:{ParameterNames[index]}");
                }
            }
        }

        // Unmodelled radial structure (omitted distortion or the wrong family) leaves a significant signed bias.
        foreach (var bin in result.Bins.Where(b => b.Axis == "radius-fraction" && b.Supported && b.MeanRadialResidualPixels is not null && b.StandardErrorPixels is not null))
            if (Math.Abs(bin.MeanRadialResidualPixels!.Value) > o.MaximumRadialBiasPixels &&
                Math.Abs(bin.MeanRadialResidualPixels.Value) > o.RadialBiasSignificance * bin.StandardErrorPixels!.Value)
                result.Rejections.Add($"radial-bias:{bin.Bin}");
        return result;
    }

    /// <summary>Catalog stars whose candidate projection lies inside the readout margin with no projected neighbour within 12 px.</summary>
    private static HashSet<string> Isolated(AstrometricRayCamera camera, FrameWork work)
    {
        var width = work.Readout.RoiWidth / work.Readout.BinX; var height = work.Readout.RoiHeight / work.Readout.BinY;
        var projected = work.Training.Concat(work.Verification)
            .Select(s => (Star: s, Pixel: Predict(camera, work.Readout, work.Rotation, s.Ray)))
            .Where(s => s.Pixel is { } p && p.X > EdgeMarginPixels && p.Y > EdgeMarginPixels && p.X < width - EdgeMarginPixels && p.Y < height - EdgeMarginPixels)
            .Select((s, i) => (s.Star, Point: new CoreDetection(i, s.Pixel!.Value.X, s.Pixel.Value.Y, 0, 0))).ToArray();
        var grid = new AstrometricSolverCore.CoreDetectionGrid(projected.Select(p => p.Point));
        return projected.Where(p => !grid.Near(p.Point.Pixel, IsolationPixels).Any(q => q.Index != p.Point.Index)).Select(p => p.Star.Catalog.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static readonly string[] ParameterNames = ["focal-scale", "principal-point-x", "principal-point-y", "radial-k1"];

    private static IEnumerable<OpticalCalibrationParameter> Parameters(ProjectionContext nominal, OpticalCalibrationOptions o, Shared shared,
        double?[]? errors, bool fitted)
    {
        var bounds = Bounds(nominal, o);
        for (var i = 0; i < 4; i++)
        {
            var isScale = i == 0;
            double Value(double v) => isScale ? Math.Exp(v) : v;
            var tolerance = 1e-9 * Math.Max(1, Math.Abs(bounds.Max[i]));
            var atBound = fitted && bounds.Fitted[i] && (shared[i] <= bounds.Min[i] + tolerance || shared[i] >= bounds.Max[i] - tolerance);
            yield return new(ParameterNames[i], isScale ? "ratio" : i < 3 ? "native-pixels" : "normalized", bounds.Fitted[i],
                isScale ? 1 : i == 1 ? nominal.PrincipalPointX : i == 2 ? nominal.PrincipalPointY : nominal.RadialDistortionK1,
                Value(shared[i]), Value(bounds.Min[i]), Value(bounds.Max[i]), errors?[i], atBound);
        }
    }

    private static OpticalCalibrationFrameResult FrameSummary(FrameWork work)
    {
        var pose = work.Status is "fitted" ? work.Rotation.ToPose() : null;
        return new(work.Input.Frame.CaptureId, work.Input.ReadoutIdentitySha256, work.Status, work.Candidates,
            pose?.Altitude, pose?.Azimuth, pose?.Roll, work.FitStars, work.HeldStars, work.FitRms, work.HeldRms);
    }

    private static string Code(string rejection) => rejection.Split(':')[0];

    private static double[]? Solve(double[] a, double[] b, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var pivot = j;
            for (var i = j + 1; i < n; i++) if (Math.Abs(a[i * n + j]) > Math.Abs(a[pivot * n + j])) pivot = i;
            if (!(Math.Abs(a[pivot * n + j]) > 1e-300)) return null;
            if (pivot != j) { for (var k = 0; k < n; k++) (a[j * n + k], a[pivot * n + k]) = (a[pivot * n + k], a[j * n + k]); (b[j], b[pivot]) = (b[pivot], b[j]); }
            var divisor = a[j * n + j];
            for (var k = j; k < n; k++) a[j * n + k] /= divisor;
            b[j] /= divisor;
            for (var i = 0; i < n; i++)
            {
                if (i == j) continue;
                var factor = a[i * n + j]; if (factor == 0) continue;
                for (var k = j; k < n; k++) a[i * n + k] -= factor * a[j * n + k];
                b[i] -= factor * b[j];
            }
        }
        return b.All(double.IsFinite) ? b : null;
    }

    private static double[]? Invert(double[] matrix, int n)
    {
        // Jacobi scaling keeps pixels, radians and the log scale comparable before inversion.
        var scale = new double[n];
        for (var i = 0; i < n; i++) { var d = matrix[i * n + i]; if (!(d > 0)) return null; scale[i] = 1 / Math.Sqrt(d); }
        var inverse = new double[n * n];
        for (var column = 0; column < n; column++)
        {
            var a = new double[n * n];
            for (var i = 0; i < n; i++) for (var k = 0; k < n; k++) a[i * n + k] = matrix[i * n + k] * scale[i] * scale[k];
            var e = new double[n]; e[column] = 1;
            if (Solve(a, e, n) is not { } x) return null;
            for (var i = 0; i < n; i++) inverse[i * n + column] = x[i] * scale[i] * scale[column];
        }
        return inverse;
    }

    private static double[] SymmetricEigenvalues(double[] matrix, int n)
    {
        var a = (double[])matrix.Clone();
        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0d;
            for (var p = 0; p < n; p++) for (var q = p + 1; q < n; q++) off += a[p * n + q] * a[p * n + q];
            if (off < 1e-24) break;
            for (var p = 0; p < n; p++) for (var q = p + 1; q < n; q++)
            {
                if (Math.Abs(a[p * n + q]) < 1e-300) continue;
                var theta = (a[q * n + q] - a[p * n + p]) / (2 * a[p * n + q]);
                var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                for (var k = 0; k < n; k++)
                {
                    var akp = a[k * n + p]; var akq = a[k * n + q];
                    a[k * n + p] = c * akp - s * akq; a[k * n + q] = s * akp + c * akq;
                }
                for (var k = 0; k < n; k++)
                {
                    var apk = a[p * n + k]; var aqk = a[q * n + k];
                    a[p * n + k] = c * apk - s * aqk; a[q * n + k] = s * apk + c * aqk;
                }
            }
        }
        return Enumerable.Range(0, n).Select(i => a[i * n + i]).ToArray();
    }
}
