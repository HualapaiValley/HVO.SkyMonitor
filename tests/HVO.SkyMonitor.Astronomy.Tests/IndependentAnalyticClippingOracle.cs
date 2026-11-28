// Independent analytic / root-based clipping oracle for issue #524 / carried-over #1191.
// INTENTIONALLY HAS ZERO PRODUCTION PROJECTOR, COORDINATE, OR CLIPPING IMPORTS.
// Uses only System primitives and verified primary projection/astronomy equations from first principles.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HVO.SkyMonitor.Astronomy.Tests.IndependentOracle;

internal enum OracleProjectionModel
{
    EquidistantFisheye,
    EquisolidFisheye,
    OrthographicFisheye,
    StereographicFisheye,
    Perspective
}

internal enum OracleAperture
{
    Circular,
    Rectangular
}

internal enum OracleHorizonPolicy
{
    GeometricHorizon,
    ProjectionOnly
}

internal readonly record struct OraclePoint(double X, double Y);

internal readonly record struct OracleVec3(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public OracleVec3 Normalized()
    {
        var len = Length;
        return new OracleVec3(X / len, Y / len, Z / len);
    }
    public static double Dot(OracleVec3 a, OracleVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static OracleVec3 Cross(OracleVec3 a, OracleVec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);
    public static OracleVec3 operator +(OracleVec3 a, OracleVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static OracleVec3 operator -(OracleVec3 a, OracleVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static OracleVec3 operator *(OracleVec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
}

internal sealed record OracleCamera(
    OracleProjectionModel Model,
    OracleAperture Aperture,
    double PrincipalX,
    double PrincipalY,
    double FocalX,
    double FocalY,
    int Width,
    int Height,
    double? ImageCircleRadius,
    double BoresightAltDeg,
    double BoresightAzDeg,
    double RollDeg,
    bool HorizontalFlip,
    bool EnforceSensorBounds,
    double RadialDistortionK1,
    OracleHorizonPolicy HorizonPolicy,
    bool RefractionEnabled,
    double RefractionMinAltDeg);

internal sealed record OracleTransform(
    int SourceWidth,
    int SourceHeight,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    int BinX,
    int BinY,
    bool HorizontalMirror,
    bool VerticalMirror,
    int QuarterRotationDegrees,
    double PreviewScaleX = 1,
    double PreviewScaleY = 1)
{
    public static OracleTransform Identity(int width, int height) =>
        new(width, height, 0, 0, width, height, 1, 1, false, false, 0, 1, 1);

    public bool IsIdentity =>
        CropX == 0 && CropY == 0 && CropWidth == SourceWidth && CropHeight == SourceHeight &&
        BinX == 1 && BinY == 1 && !HorizontalMirror && !VerticalMirror &&
        QuarterRotationDegrees == 0 && Math.Abs(PreviewScaleX - 1) <= 1e-15 && Math.Abs(PreviewScaleY - 1) <= 1e-15;
}

internal sealed record OracleVisibleArc(bool Closed, OraclePoint[] Points);

internal sealed record OracleComparisonMetrics(
    double ForwardHausdorffPixels,
    double ReverseHausdorffPixels,
    double SymmetricHausdorffPixels,
    double MaxVertexToReferencePixels,
    double MaxEndpointBoundaryResidualPixels,
    int ActualPartCount,
    int ReferencePartCount,
    int ActualClosedCount,
    int ReferenceClosedCount,
    bool TopologyMatches);

/// <summary>
/// Completely independent analytic/root-based sky-curve clipping oracle with documented error budget.
/// </summary>
internal static class IndependentAnalyticClippingOracle
{
    // Documented numerical error budget derived from contract + root residual + chord sagitta:
    // - Chord sagitta contract bound: 0.25 px (ResolvedFootprintSampler.MaximumChordErrorPixels)
    // - Bisection root precision: 52 iterations -> <= 2^-52 * 2 deg ~ 1e-14 rad (< 1e-9 px at 100k px/rad)
    // - Floating-point + precession/projection rounding budget: <= 1e-4 px
    public const double VertexOnCurveTolerancePixels = 2e-3;
    public const double ContinuousHausdorffBudgetPixels = 0.27;
    public const double BoundaryEndpointResidualBudgetPixels = 2e-3;
    public const double TransformParityBudgetPixels = 1e-8;
    public const double ReferenceSagittaTolerancePixels = 1e-4;

    private const int BracketSamplesPerCurve = 2048;
    private const int BisectionIterations = 52;
    private const int GoldenIterations = 60;
    private const int ReferenceAdaptiveMaxDepth = 14;

    /// <summary>
    /// Converts J2000 equatorial coordinates to geometric ENU unit vector using independent matrix algebra:
    /// IAU 1976 Lieske et al. (1977) precession matrix P(T) = R_z(-z) R_y(theta) R_z(-zeta), followed by
    /// Earth-rotation and latitude rotation to local East-North-Up.
    /// </summary>
    public static OracleVec3 J2000ToGeometricEnu(
        double raHours,
        double decDeg,
        DateTimeOffset utc,
        double latDeg,
        double lonDeg)
    {
        var jd = 2440587.5d + (utc.ToUniversalTime() - DateTimeOffset.UnixEpoch).TotalDays;
        var T = (jd - 2451545d) / 36525d;
        var t2 = T * T;
        var t3 = t2 * T;
        const double arcsecToRad = Math.PI / (180d * 3600d);
        var zeta = (2306.2181d * T + 0.30188d * t2 + 0.017998d * t3) * arcsecToRad;
        var z = (2306.2181d * T + 1.09468d * t2 + 0.018203d * t3) * arcsecToRad;
        var theta = (2004.3109d * T - 0.42665d * t2 - 0.041833d * t3) * arcsecToRad;

        var ra = raHours * Math.PI / 12d;
        var dec = decDeg * Math.PI / 180d;
        var eq0 = new OracleVec3(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));

        // Apply R_z(-z) * R_y(+theta) * R_z(-zeta) (note active vector rotation matching Lieske 1977 eq. 4).
        var eq1 = RotateZ(eq0, zeta);
        var eq2 = RotateY(eq1, -theta);
        var eqOfDate = RotateZ(eq2, z);

        // Local mean sidereal time (IAU 1982 GMST linear term matching AstronomyTime).
        var d = jd - 2451545d;
        var gmstDeg = (((280.46061837d + 360.98564736629d * d) % 360d) + 360d) % 360d;
        var lmstRad = (gmstDeg + lonDeg) * Math.PI / 180d;

        // Rotate equatorial-of-date unit vector to meridian frame (x = south-meridian, y = west, z = north pole).
        var cosL = Math.Cos(lmstRad);
        var sinL = Math.Sin(lmstRad);
        var xMer = cosL * eqOfDate.X + sinL * eqOfDate.Y;
        var yWest = sinL * eqOfDate.X - cosL * eqOfDate.Y;
        var zPole = eqOfDate.Z;

        // Rotate by observer latitude phi into local ENU:
        // East = -yWest; North = -sin(phi)*xMer + cos(phi)*zPole; Up = cos(phi)*xMer + sin(phi)*zPole.
        var phi = latDeg * Math.PI / 180d;
        var sinPhi = Math.Sin(phi);
        var cosPhi = Math.Cos(phi);
        return new OracleVec3(
            -yWest,
            -sinPhi * xMer + cosPhi * zPole,
            cosPhi * xMer + sinPhi * zPole).Normalized();
    }

    internal enum OracleRefractionBranch
    {
        Default,
        ForceRefracted,
        ForceUnrefracted
    }

    /// <summary>Independent Bennett (1982) standard atmosphere refraction.</summary>
    public static double ApplyBennettRefraction(
        double geomAltDeg,
        bool enabled,
        double minAltDeg,
        OracleRefractionBranch branch = OracleRefractionBranch.Default)
    {
        if (!enabled || branch == OracleRefractionBranch.ForceUnrefracted) return geomAltDeg;
        if (branch == OracleRefractionBranch.Default && geomAltDeg < minAltDeg) return geomAltDeg;
        var evalAlt = branch == OracleRefractionBranch.ForceRefracted ? Math.Max(geomAltDeg, minAltDeg) : geomAltDeg;
        var argRad = (evalAlt + 10.3d / (evalAlt + 5.11d)) * Math.PI / 180d;
        return evalAlt + (1.02d / Math.Tan(argRad)) / 60d;
    }

    public static (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) BuildCameraBasis(OracleCamera cam)
    {
        var alt = cam.BoresightAltDeg * Math.PI / 180d;
        var az = cam.BoresightAzDeg * Math.PI / 180d;
        var forward = new OracleVec3(Math.Cos(alt) * Math.Sin(az), Math.Cos(alt) * Math.Cos(az), Math.Sin(alt)).Normalized();
        var worldUp = new OracleVec3(0, 0, 1);
        var imgUp = worldUp - forward * OracleVec3.Dot(worldUp, forward);
        imgUp = imgUp.Length < 1e-12 ? new OracleVec3(0, 1, 0) : imgUp.Normalized();
        var right = OracleVec3.Cross(imgUp, forward).Normalized();
        var roll = cam.RollDeg * Math.PI / 180d;
        var rolledRight = right * Math.Cos(roll) + imgUp * Math.Sin(roll);
        var rolledUp = imgUp * Math.Cos(roll) - right * Math.Sin(roll);
        if (cam.HorizontalFlip) rolledRight *= -1d;
        return (rolledRight, rolledUp, forward);
    }

    /// <summary>
    /// Evaluates primary projection formulas directly from a geometric ENU unit vector, returning unclipped pixel
    /// coordinates plus the signed domain/visibility margins for every active constraint.
    /// </summary>
    public static bool TryEvaluateGeometry(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleVec3 geomEnu,
        out OraclePoint pixel,
        out double minConstraintMargin,
        OracleRefractionBranch branch = OracleRefractionBranch.Default)
    {
        pixel = default;
        minConstraintMargin = double.NegativeInfinity;

        // 1. Geometric horizon
        if (cam.HorizonPolicy == OracleHorizonPolicy.GeometricHorizon && geomEnu.Z < 0)
        {
            minConstraintMargin = geomEnu.Z;
            return false;
        }

        // 2. Bennett refraction (preserving azimuth, shifting altitude)
        var geomAltDeg = Math.Asin(Math.Clamp(geomEnu.Z, -1d, 1d)) * 180d / Math.PI;
        var appAltDeg = ApplyBennettRefraction(geomAltDeg, cam.RefractionEnabled, cam.RefractionMinAltDeg, branch);
        var appAltRad = appAltDeg * Math.PI / 180d;
        var horizLen = Math.Sqrt(geomEnu.X * geomEnu.X + geomEnu.Y * geomEnu.Y);
        var appEnu = horizLen <= 1e-15
            ? new OracleVec3(0, 0, Math.Sin(appAltRad))
            : new OracleVec3(
                Math.Cos(appAltRad) * (geomEnu.X / horizLen),
                Math.Cos(appAltRad) * (geomEnu.Y / horizLen),
                Math.Sin(appAltRad));

        // 3. Camera coordinates
        var cx = OracleVec3.Dot(appEnu, basis.Right);
        var cy = OracleVec3.Dot(appEnu, basis.Up);
        var cz = OracleVec3.Dot(appEnu, basis.Forward);

        double nx, ny;
        if (cam.Model == OracleProjectionModel.Perspective)
        {
            if (cz <= 1e-12) return false;
            nx = cx / cz;
            ny = cy / cz;
        }
        else
        {
            var theta = Math.Acos(Math.Clamp(cz, -1d, 1d));
            if (cam.Model == OracleProjectionModel.OrthographicFisheye && theta > Math.PI / 2 + 1e-12) return false;
            if (cam.Model == OracleProjectionModel.StereographicFisheye && theta >= Math.PI - 1e-12) return false;

            var rho = cam.Model switch
            {
                OracleProjectionModel.EquidistantFisheye => theta,
                OracleProjectionModel.EquisolidFisheye => 2d * Math.Sin(theta / 2d),
                OracleProjectionModel.OrthographicFisheye => Math.Sin(theta),
                OracleProjectionModel.StereographicFisheye => 2d * Math.Tan(theta / 2d),
                _ => double.NaN
            };
            var planar = Math.Sqrt(cx * cx + cy * cy);
            if (planar <= 1e-15)
            {
                if (theta > 1e-12) return false;
                nx = 0;
                ny = 0;
            }
            else
            {
                nx = rho * cx / planar;
                ny = rho * cy / planar;
            }
        }

        // 4. Radial distortion (1 + k1 * r^2) with invertibility domain 1 + 3 k1 r^2 >= 0.5
        var rIdealSq = nx * nx + ny * ny;
        var rIdeal = Math.Sqrt(rIdealSq);
        var maxIdeal = cam.Model switch
        {
            OracleProjectionModel.EquidistantFisheye => Math.PI,
            OracleProjectionModel.EquisolidFisheye => 2d,
            OracleProjectionModel.OrthographicFisheye => 1d,
            _ => double.PositiveInfinity
        };
        if (cam.RadialDistortionK1 < 0)
        {
            maxIdeal = Math.Min(maxIdeal, Math.Sqrt(-0.5 / (3d * cam.RadialDistortionK1)));
        }
        if (rIdeal > maxIdeal * (1d + 1e-12)) return false;

        var scale = 1d + cam.RadialDistortionK1 * rIdealSq;
        var px = cam.PrincipalX + cam.FocalX * nx * scale;
        var py = cam.PrincipalY - cam.FocalY * ny * scale;
        if (!double.IsFinite(px) || !double.IsFinite(py)) return false;
        pixel = new OraclePoint(px, py);

        // Compute minimum signed pixel-space margin across sensor and circular aperture boundaries.
        minConstraintMargin = double.PositiveInfinity;
        if (cam.EnforceSensorBounds)
        {
            minConstraintMargin = Math.Min(minConstraintMargin, px);
            minConstraintMargin = Math.Min(minConstraintMargin, cam.Width - px);
            minConstraintMargin = Math.Min(minConstraintMargin, py);
            minConstraintMargin = Math.Min(minConstraintMargin, cam.Height - py);
        }
        if (cam.Aperture == OracleAperture.Circular)
        {
            var r = cam.ImageCircleRadius!.Value;
            var dist = Math.Sqrt((px - cam.PrincipalX) * (px - cam.PrincipalX) + (py - cam.PrincipalY) * (py - cam.PrincipalY));
            minConstraintMargin = Math.Min(minConstraintMargin, r - dist);
        }
        return true;
    }

    /// <summary>Applies the independent emitted-image transform + preview scale.</summary>
    public static OraclePoint ApplyTransform(OracleTransform transform, OraclePoint source)
    {
        var width = (double)transform.CropWidth / transform.BinX;
        var height = (double)transform.CropHeight / transform.BinY;
        var x = (source.X - transform.CropX) / transform.BinX;
        var y = (source.Y - transform.CropY) / transform.BinY;
        if (transform.HorizontalMirror) x = width - x;
        if (transform.VerticalMirror) y = height - y;
        var rotated = transform.QuarterRotationDegrees switch
        {
            0 => new OraclePoint(x, y),
            90 => new OraclePoint(height - y, x),
            180 => new OraclePoint(width - x, height - y),
            270 => new OraclePoint(y, width - x),
            _ => throw new ArgumentOutOfRangeException(nameof(transform))
        };
        return new OraclePoint(rotated.X * transform.PreviewScaleX, rotated.Y * transform.PreviewScaleY);
    }

    /// <summary>
    /// Computes exact signed margin of a sky direction under BOTH optical camera constraints AND crop rectangle.
    /// Positive or zero iff visible; negative iff clipped.
    /// </summary>
    public static double SignedVisibilityMargin(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleTransform transform,
        OracleVec3 geomEnu,
        out OraclePoint outputPixel,
        OracleRefractionBranch branch = OracleRefractionBranch.Default)
    {
        outputPixel = default;
        if (!TryEvaluateGeometry(cam, basis, geomEnu, out var sourcePixel, out var camMargin, branch))
        {
            return -1e3;
        }
        var margin = camMargin;
        if (!transform.IsIdentity)
        {
            margin = Math.Min(margin, sourcePixel.X - transform.CropX);
            margin = Math.Min(margin, transform.CropX + transform.CropWidth - sourcePixel.X);
            margin = Math.Min(margin, sourcePixel.Y - transform.CropY);
            margin = Math.Min(margin, transform.CropY + transform.CropHeight - sourcePixel.Y);
        }
        if (margin >= -1e-9)
        {
            var clamped = ClampToCrop(transform, sourcePixel);
            outputPixel = ApplyTransform(transform, clamped);
        }
        return margin;
    }

    /// <summary>
    /// Solves the exact closed-form roots of A*cos(s) + B*sin(s) = C on s in [0, angle], plus interior critical points
    /// where -A*sin(s) + B*cos(s) = 0, used to certify horizon/domain/aperture roots on great-circle arcs.
    /// </summary>
    public static IReadOnlyList<double> SolveAnalyticGreatCirclePlaneRoots(
        OracleVec3 start,
        OracleVec3 end,
        OracleVec3 planeNormal,
        double thresholdC)
    {
        var dot = Math.Clamp(OracleVec3.Dot(start, end), -1d, 1d);
        var angle = Math.Acos(dot);
        if (angle <= 1e-12 || angle >= Math.PI - 1e-10) return Array.Empty<double>();

        // Orthonormal basis in the great-circle plane: u = start, v = (end - dot*start)/sin(angle).
        var v = (end - start * dot) * (1d / Math.Sin(angle));
        var a = OracleVec3.Dot(start, planeNormal);
        var b = OracleVec3.Dot(v, planeNormal);
        var amp = Math.Sqrt(a * a + b * b);
        var roots = new List<double>();
        if (amp <= 1e-15) return roots;

        var phi = Math.Atan2(b, a);
        // Critical points where derivative vanishes: s = phi + k*pi (catches exact tangencies & short incursions!).
        for (var k = -2; k <= 2; k++)
        {
            var sc = phi + k * Math.PI;
            if (sc > 0 && sc < angle) roots.Add(sc / angle);
        }

        var ratio = thresholdC / amp;
        if (Math.Abs(ratio) <= 1d + 1e-12)
        {
            var delta = Math.Acos(Math.Clamp(ratio, -1d, 1d));
            foreach (var raw in new[] { phi - delta, phi + delta })
            {
                for (var k = -2; k <= 2; k++)
                {
                    var s = raw + 2d * Math.PI * k;
                    if (s > 0 && s < angle) roots.Add(s / angle);
                }
            }
        }
        roots.Sort();
        return roots;
    }

    /// <summary>
    /// Independent analytic/root-based clipping of an exponential-map ellipse on the unit sphere.
    /// </summary>
    public static IReadOnlyList<OracleVisibleArc> ClipEllipse(
        OracleCamera cam,
        OracleTransform transform,
        double centerRaHours,
        double centerDecDeg,
        double semiMajorDeg,
        double semiMinorDeg,
        double paJ2000Deg,
        DateTimeOffset utc,
        double latDeg,
        double lonDeg,
        int maxAdaptiveDepth = ReferenceAdaptiveMaxDepth)
    {
        var basis = BuildCameraBasis(cam);
        var center = J2000ToGeometricEnu(centerRaHours, centerDecDeg, utc, latDeg, lonDeg);
        var phi = latDeg * Math.PI / 180d;
        var pole = new OracleVec3(0, Math.Cos(phi), Math.Sin(phi));
        var northTan = pole - center * OracleVec3.Dot(pole, center);
        OracleVec3 north;
        if (northTan.Length < 1e-12)
        {
            var alt = Math.Asin(Math.Clamp(center.Z, -1d, 1d));
            var az = Math.Atan2(center.X, center.Y);
            north = new OracleVec3(-Math.Sin(alt) * Math.Sin(az), -Math.Sin(alt) * Math.Cos(az), Math.Cos(alt)).Normalized();
        }
        else
        {
            north = northTan.Normalized();
        }
        var east = OracleVec3.Cross(north, center).Normalized();

        // Independently transport the J2000 PA tangent vector through the exact precession+ENU rotation!
        var paJ2000Rad = paJ2000Deg * Math.PI / 180d;
        const double stepDeg = 1e-2;
        var decRad = centerDecDeg * Math.PI / 180d;
        var raRad = centerRaHours * Math.PI / 12d;
        var stepRad = stepDeg * Math.PI / 180d;
        var offDec = Math.Asin(Math.Clamp(
            Math.Sin(decRad) * Math.Cos(stepRad) + Math.Cos(decRad) * Math.Sin(stepRad) * Math.Cos(paJ2000Rad), -1d, 1d));
        var offRa = raRad + Math.Atan2(
            Math.Sin(paJ2000Rad) * Math.Sin(stepRad) * Math.Cos(decRad),
            Math.Cos(stepRad) - Math.Sin(decRad) * Math.Sin(offDec));
        var offRaHours = (((offRa * 12d / Math.PI) % 24d) + 24d) % 24d;
        var offEnu = J2000ToGeometricEnu(offRaHours, offDec * 180d / Math.PI, utc, latDeg, lonDeg);
        var majorDir = (offEnu - center * OracleVec3.Dot(offEnu, center)).Normalized();
        // Enforce minorDir right-handed matching (CelestialEast cos PA - CelestialNorth sin PA):
        var cosPa = OracleVec3.Dot(majorDir, north);
        var sinPa = OracleVec3.Dot(majorDir, east);
        var minorDir = (east * cosPa - north * sinPa).Normalized();

        var a = semiMajorDeg * Math.PI / 180d;
        var b = semiMinorDeg * Math.PI / 180d;
        OracleVec3 Curve(double t)
        {
            var tan = majorDir * (a * Math.Cos(t)) + minorDir * (b * Math.Sin(t));
            var ang = tan.Length;
            return ang < 1e-15 ? center : (center * Math.Cos(ang) + tan * (Math.Sin(ang) / ang)).Normalized();
        }

        return ClipParameterizedCurve(
            cam, basis, transform, Curve, 0d, 2d * Math.PI,
            maxAngularSpeedRadPerParam: Math.Max(1e-9, a),
            periodic: true,
            analyticHorizonHints: null,
            analyticRefractionFloorHints: null,
            analyticDomainHints: null,
            maxAdaptiveDepth: maxAdaptiveDepth);
    }

    /// <summary>
    /// Independent analytic/root-based clipping of a closed spherical polygon ring (great-circle arcs).
    /// </summary>
    public static IReadOnlyList<OracleVisibleArc> ClipGreatCircleRing(
        OracleCamera cam,
        OracleTransform transform,
        IReadOnlyList<(double RaDeg, double DecDeg)> ringJ2000,
        DateTimeOffset utc,
        double latDeg,
        double lonDeg,
        int maxAdaptiveDepth = ReferenceAdaptiveMaxDepth)
    {
        var basis = BuildCameraBasis(cam);
        var verts = ringJ2000
            .Select(p => J2000ToGeometricEnu(p.RaDeg / 15d, p.DecDeg, utc, latDeg, lonDeg))
            .ToArray();
        var rawArcs = new List<BranchedOracleArc>();
        var allEdgesFullyVisible = true;
        var needsFloorSplit = RequiresRefractionFloorSplit(cam);
        var sinFloor = needsFloorSplit ? Math.Sin(cam.RefractionMinAltDeg * Math.PI / 180d) : double.NaN;

        // Compute undistorted circular aperture angular radius for analytic cone root hints when applicable.
        double? analyticApertureCosine = null;
        if (cam.Aperture == OracleAperture.Circular && !cam.RefractionEnabled && cam.RadialDistortionK1 == 0)
        {
            var r = cam.ImageCircleRadius!.Value / cam.FocalX;
            var thetaMax = cam.Model switch
            {
                OracleProjectionModel.EquidistantFisheye => r,
                OracleProjectionModel.EquisolidFisheye => 2d * Math.Asin(Math.Clamp(r / 2d, -1d, 1d)),
                OracleProjectionModel.OrthographicFisheye => Math.Asin(Math.Clamp(r, -1d, 1d)),
                OracleProjectionModel.StereographicFisheye => 2d * Math.Atan(r / 2d),
                _ => double.NaN
            };
            if (double.IsFinite(thetaMax)) analyticApertureCosine = Math.Cos(thetaMax);
        }

        var minDomainCosine = ComputeMinimumDomainCosine(cam);

        for (var i = 0; i < verts.Length - 1; i++)
        {
            var start = verts[i];
            var end = verts[i + 1];
            var angle = Math.Acos(Math.Clamp(OracleVec3.Dot(start, end), -1d, 1d));
            if (angle >= Math.PI - 1e-5)
            {
                throw new InvalidOperationException("Antipodal edge cannot be clipped uniquely.");
            }
            var horizonHints = new List<double>();
            var refractionFloorHints = new List<double>();
            var domainHints = new List<double>();
            // Closed-form analytic roots for horizon (z=0), refraction floor, optical domain cone, and circular aperture cone.
            if (cam.HorizonPolicy == OracleHorizonPolicy.GeometricHorizon)
            {
                horizonHints.AddRange(SolveAnalyticGreatCirclePlaneRoots(start, end, new OracleVec3(0, 0, 1), 0d));
            }
            if (needsFloorSplit)
            {
                refractionFloorHints.AddRange(SolveAnalyticGreatCirclePlaneRoots(start, end, new OracleVec3(0, 0, 1), sinFloor));
            }
            if (!cam.RefractionEnabled && minDomainCosine > -1d + 1e-12)
            {
                domainHints.AddRange(SolveAnalyticGreatCirclePlaneRoots(start, end, basis.Forward, minDomainCosine));
            }
            if (analyticApertureCosine is { } cosAp)
            {
                domainHints.AddRange(SolveAnalyticGreatCirclePlaneRoots(start, end, basis.Forward, cosAp));
            }

            OracleVec3 Arc(double t)
            {
                if (angle < 1e-12) return (start * (1d - t) + end * t).Normalized();
                var sinA = Math.Sin(angle);
                return (start * (Math.Sin((1d - t) * angle) / sinA) + end * (Math.Sin(t * angle) / sinA)).Normalized();
            }

            var edgeArcs = ClipParameterizedCurveBranched(
                cam, basis, transform, Arc, 0d, 1d,
                maxAngularSpeedRadPerParam: Math.Max(1e-9, angle),
                periodic: false,
                analyticHorizonHints: horizonHints,
                analyticRefractionFloorHints: refractionFloorHints,
                analyticDomainHints: domainHints,
                maxAdaptiveDepth: maxAdaptiveDepth,
                out var edgeAllVisible);
            if (!edgeAllVisible || edgeArcs.Count != 1 ||
                Distance(edgeArcs[0].Arc.Points[0], EvaluatePoint(cam, basis, transform, start, edgeArcs[0].Branch)) > 1e-4 ||
                Distance(edgeArcs[0].Arc.Points[^1], EvaluatePoint(cam, basis, transform, end, edgeArcs[0].Branch)) > 1e-4)
            {
                allEdgesFullyVisible = false;
            }
            rawArcs.AddRange(edgeArcs);
        }

        return MergeContiguousArcs(rawArcs, allEdgesFullyVisible);
    }

    /// <summary>
    /// Computes true continuous bidirectional Hausdorff distance between actual output polylines and reference arcs,
    /// using dense sampling plus golden-section peak refinement, bidirectional open-endpoint residuals, and topology checks.
    /// </summary>
    public static OracleComparisonMetrics ComparePolylines(
        IReadOnlyList<(bool Closed, OraclePoint[] Points)> actualParts,
        IReadOnlyList<OracleVisibleArc> referenceArcs,
        OracleCamera cam,
        OracleTransform transform)
    {
        var refSegments = ExtractSegments(referenceArcs.Select(static a => (a.Closed, a.Points)).ToArray());
        var actualSegments = ExtractSegments(actualParts);
        var actualClosedCount = actualParts.Count(static p => p.Closed);
        var refClosedCount = referenceArcs.Count(static a => a.Closed);
        var topologyMatches = actualParts.Count == referenceArcs.Count && actualClosedCount == refClosedCount;

        if (refSegments.Count == 0 && actualSegments.Count == 0)
        {
            return new OracleComparisonMetrics(0, 0, 0, 0, 0, actualParts.Count, referenceArcs.Count,
                actualClosedCount, refClosedCount, topologyMatches);
        }
        if (refSegments.Count == 0 || actualSegments.Count == 0)
        {
            return new OracleComparisonMetrics(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                double.PositiveInfinity, double.PositiveInfinity, actualParts.Count, referenceArcs.Count,
                actualClosedCount, refClosedCount, false);
        }

        var forwardMax = 0d;
        var maxVertexDist = 0d;
        foreach (var seg in actualSegments)
        {
            var da = DistancePointToSegments(seg.A, refSegments);
            var db = DistancePointToSegments(seg.B, refSegments);
            maxVertexDist = Math.Max(maxVertexDist, Math.Max(da, db));
            var segMax = MaxDistanceSegmentToSegments(seg.A, seg.B, refSegments);
            if (segMax > forwardMax) forwardMax = segMax;
        }

        var reverseMax = 0d;
        foreach (var seg in refSegments)
        {
            var segMax = MaxDistanceSegmentToSegments(seg.A, seg.B, actualSegments);
            if (segMax > reverseMax) reverseMax = segMax;
        }

        // Bidirectional open-arc endpoint residual catches missing interior gap endpoints as well as shifted endpoints.
        var refEndpoints = referenceArcs
            .Where(static a => !a.Closed && a.Points.Length >= 2)
            .SelectMany(static a => new[] { a.Points[0], a.Points[^1] })
            .ToArray();
        var actualEndpoints = actualParts
            .Where(static p => !p.Closed && p.Points.Length >= 2)
            .SelectMany(static p => new[] { p.Points[0], p.Points[^1] })
            .ToArray();
        var maxEndpointDist = 0d;
        if (refEndpoints.Length == 0 && actualEndpoints.Length == 0)
        {
            maxEndpointDist = 0d;
        }
        else if (refEndpoints.Length == 0 || actualEndpoints.Length == 0)
        {
            maxEndpointDist = double.PositiveInfinity;
        }
        else
        {
            foreach (var ep in actualEndpoints)
            {
                var minD = refEndpoints.Min(r => Distance(ep, r));
                if (minD > maxEndpointDist) maxEndpointDist = minD;
            }
            foreach (var r in refEndpoints)
            {
                var minD = actualEndpoints.Min(ep => Distance(r, ep));
                if (minD > maxEndpointDist) maxEndpointDist = minD;
            }
        }

        return new OracleComparisonMetrics(
            forwardMax,
            reverseMax,
            Math.Max(forwardMax, reverseMax),
            maxVertexDist,
            maxEndpointDist,
            actualParts.Count,
            referenceArcs.Count,
            actualClosedCount,
            refClosedCount,
            topologyMatches);
    }

    private static double MaxDistanceSegmentToSegments(
        OraclePoint a,
        OraclePoint b,
        List<OracleSegment> targetSegments)
    {
        var len = Distance(a, b);
        var steps = Math.Max(8, (int)Math.Ceiling(len / 0.02));
        var ds = new double[steps + 1];
        var best = 0d;
        for (var s = 0; s <= steps; s++)
        {
            var t = (double)s / steps;
            var d = Eval(t);
            ds[s] = d;
            if (d > best) best = d;
        }
        // Refine every local maximum bracket with golden-section search so gap midpoints are measured accurately.
        for (var s = 0; s <= steps; s++)
        {
            var isPeak = (s == 0 || ds[s] >= ds[s - 1]) && (s == steps || ds[s] >= ds[s + 1]);
            if (!isPeak) continue;
            var lo = s == 0 ? 0d : (double)(s - 1) / steps;
            var hi = s == steps ? 1d : (double)(s + 1) / steps;
            var peakT = MaximizeGolden(Eval, lo, hi, iterations: 28);
            var peakD = Eval(peakT);
            if (peakD > best) best = peakD;
        }
        return best;

        double Eval(double t)
        {
            var p = new OraclePoint(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
            return DistancePointToSegments(p, targetSegments);
        }
    }

    private static double ComputeMinimumDomainCosine(OracleCamera cam)
    {
        var maxIdeal = cam.Model switch
        {
            OracleProjectionModel.EquidistantFisheye => Math.PI,
            OracleProjectionModel.EquisolidFisheye => 2d,
            OracleProjectionModel.OrthographicFisheye => 1d,
            _ => double.PositiveInfinity
        };
        if (cam.RadialDistortionK1 < 0)
        {
            maxIdeal = Math.Min(maxIdeal, Math.Sqrt(-0.5 / (3d * cam.RadialDistortionK1)));
        }
        return cam.Model switch
        {
            OracleProjectionModel.Perspective => double.IsFinite(maxIdeal)
                ? 1d / Math.Sqrt(1d + maxIdeal * maxIdeal)
                : 1e-12,
            OracleProjectionModel.OrthographicFisheye => Math.Cos(Math.Asin(Math.Clamp(maxIdeal, 0d, 1d))),
            OracleProjectionModel.EquidistantFisheye => Math.Cos(Math.Min(Math.PI, maxIdeal)),
            OracleProjectionModel.EquisolidFisheye => Math.Cos(2d * Math.Asin(Math.Clamp(maxIdeal / 2d, 0d, 1d))),
            OracleProjectionModel.StereographicFisheye => Math.Cos(double.IsFinite(maxIdeal) ? 2d * Math.Atan(maxIdeal / 2d) : Math.PI - 1e-12),
            _ => -1d
        };
    }

    private readonly record struct BranchedOracleArc(OracleVisibleArc Arc, OracleRefractionBranch Branch);

    private static bool RequiresRefractionFloorSplit(OracleCamera cam) =>
        cam.RefractionEnabled &&
        cam.RefractionMinAltDeg > -90d &&
        !(cam.HorizonPolicy == OracleHorizonPolicy.GeometricHorizon && cam.RefractionMinAltDeg <= 0d);

    private static double OpticalDomainMargin(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleVec3 geomEnu,
        double minDomainCosine,
        OracleRefractionBranch branch = OracleRefractionBranch.Default)
    {
        var appEnu = geomEnu;
        if (cam.RefractionEnabled && branch != OracleRefractionBranch.ForceUnrefracted)
        {
            var geomAltDeg = Math.Asin(Math.Clamp(geomEnu.Z, -1d, 1d)) * 180d / Math.PI;
            var appAltRad = ApplyBennettRefraction(geomAltDeg, true, cam.RefractionMinAltDeg, branch) * Math.PI / 180d;
            var horizLen = Math.Sqrt(geomEnu.X * geomEnu.X + geomEnu.Y * geomEnu.Y);
            appEnu = horizLen <= 1e-15
                ? new OracleVec3(0, 0, Math.Sin(appAltRad))
                : new OracleVec3(
                    Math.Cos(appAltRad) * (geomEnu.X / horizLen),
                    Math.Cos(appAltRad) * (geomEnu.Y / horizLen),
                    Math.Sin(appAltRad));
        }
        return OracleVec3.Dot(appEnu, basis.Forward) - minDomainCosine;
    }

    private static List<(double From, double To)> IsolatePositiveIntervals(
        Func<double, double> f,
        double tStart,
        double tEnd,
        int baseSteps,
        double lipschitzBound,
        List<double>? analyticHints,
        out bool fullyPositive,
        double extraExclusionAllowance = 0d)
    {
        var grid = new List<double>(baseSteps + 1 + (analyticHints?.Count ?? 0));
        for (var i = 0; i <= baseSteps; i++)
        {
            grid.Add(tStart + (tEnd - tStart) * i / baseSteps);
        }
        if (analyticHints is not null)
        {
            foreach (var h in analyticHints)
            {
                if (h > tStart && h < tEnd) grid.Add(h);
            }
        }
        grid.Sort();
        var uniqueGrid = Deduplicate(grid, 1e-13 * Math.Max(1d, tEnd - tStart));
        var values = uniqueGrid.Select(f).ToArray();
        var rawIntervals = new List<(double From, double To)>();
        var anyOutside = false;

        for (var i = 0; i < uniqueGrid.Count - 1; i++)
        {
            var a = uniqueGrid[i];
            var b = uniqueGrid[i + 1];
            var fa = values[i];
            var fb = values[i + 1];
            var inA = fa >= 0d;
            var inB = fb >= 0d;
            var maxVariation = lipschitzBound * 0.5 * (b - a) + extraExclusionAllowance;

            if (inA && inB)
            {
                var mid = 0.5 * (a + b);
                var fm = f(mid);
                if (fm < 0d || fm - maxVariation <= 1e-12)
                {
                    var dipT = MinimizeGolden(f, a, b, GoldenIterations);
                    if (f(dipT) < 0d)
                    {
                        anyOutside = true;
                        var exitRoot = BisectVisibilityRoot(f, a, dipT, aIsInside: true);
                        var enterRoot = BisectVisibilityRoot(f, dipT, b, aIsInside: false);
                        if (exitRoot > a) rawIntervals.Add((a, exitRoot));
                        if (b > enterRoot) rawIntervals.Add((enterRoot, b));
                        continue;
                    }
                }
                rawIntervals.Add((a, b));
            }
            else if (inA != inB)
            {
                anyOutside = true;
                var root = BisectVisibilityRoot(f, a, b, aIsInside: inA);
                if (inA && root > a) rawIntervals.Add((a, root));
                else if (inB && b > root) rawIntervals.Add((root, b));
            }
            else
            {
                anyOutside = true;
                var mid = 0.5 * (a + b);
                var fm = f(mid);
                if (fm >= 0d || fm + maxVariation >= -1e-12)
                {
                    var peakT = MaximizeGolden(f, a, b, GoldenIterations);
                    if (f(peakT) > 0d)
                    {
                        var enterRoot = BisectVisibilityRoot(f, a, peakT, aIsInside: false);
                        var exitRoot = BisectVisibilityRoot(f, peakT, b, aIsInside: true);
                        if (exitRoot > enterRoot) rawIntervals.Add((enterRoot, exitRoot));
                    }
                }
            }
        }

        fullyPositive = !anyOutside;
        if (rawIntervals.Count <= 1) return rawIntervals;
        var merged = new List<(double From, double To)> { rawIntervals[0] };
        var mergeTol = 1e-13 * Math.Max(1d, tEnd - tStart);
        for (var i = 1; i < rawIntervals.Count; i++)
        {
            var prev = merged[^1];
            var cur = rawIntervals[i];
            if (cur.From <= prev.To + mergeTol)
            {
                merged[^1] = (prev.From, Math.Max(prev.To, cur.To));
            }
            else
            {
                merged.Add(cur);
            }
        }
        return merged;
    }

    private static OracleVisibleArc[] ClipParameterizedCurve(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleTransform transform,
        Func<double, OracleVec3> curve,
        double tMin,
        double tMax,
        double maxAngularSpeedRadPerParam,
        bool periodic,
        List<double>? analyticHorizonHints,
        List<double>? analyticRefractionFloorHints,
        List<double>? analyticDomainHints,
        int maxAdaptiveDepth = ReferenceAdaptiveMaxDepth)
    {
        var arcs = ClipParameterizedCurveBranched(
            cam, basis, transform, curve, tMin, tMax,
            maxAngularSpeedRadPerParam, periodic,
            analyticHorizonHints, analyticRefractionFloorHints, analyticDomainHints,
            maxAdaptiveDepth, out var allVisible);
        if (periodic && allVisible && arcs.Count > 0)
        {
            return MergeContiguousArcs(arcs, closedCandidate: true);
        }
        return MergeContiguousArcs(arcs, closedCandidate: false);
    }

    private static List<BranchedOracleArc> ClipParameterizedCurveBranched(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleTransform transform,
        Func<double, OracleVec3> curve,
        double tMin,
        double tMax,
        double maxAngularSpeedRadPerParam,
        bool periodic,
        List<double>? analyticHorizonHints,
        List<double>? analyticRefractionFloorHints,
        List<double>? analyticDomainHints,
        int maxAdaptiveDepth,
        out bool allVisible)
    {
        var intervals = new List<(double From, double To)> { (tMin, tMax) };
        allVisible = true;
        var fullSpan = Math.Max(1e-15, tMax - tMin);

        // Stage 1a: Isolate geometric horizon (Up >= 0) independently so domain failures never mask narrow horizon grazes.
        if (cam.HorizonPolicy == OracleHorizonPolicy.GeometricHorizon)
        {
            var horizonIntervals = new List<(double From, double To)>();
            foreach (var (a, b) in intervals)
            {
                var steps = Math.Max(64, (int)Math.Ceiling(BracketSamplesPerCurve * (b - a) / fullSpan));
                var sub = IsolatePositiveIntervals(
                    t => curve(t).Z,
                    a, b, steps,
                    Math.Max(1e-9, maxAngularSpeedRadPerParam),
                    analyticHorizonHints,
                    out var fullyAbove);
                if (!fullyAbove) allVisible = false;
                horizonIntervals.AddRange(sub);
            }
            intervals = horizonIntervals;
            if (intervals.Count == 0) return [];
        }

        // Stage 1b: Split across the Bennett refraction altitude floor discontinuity (RefractionMinAltDeg)
        // whenever both sides are reachable, pinning each piece to its smooth one-sided limit.
        var branchedIntervals = new List<(double From, double To, OracleRefractionBranch Branch)>();
        if (RequiresRefractionFloorSplit(cam))
        {
            var sinFloor = Math.Sin(cam.RefractionMinAltDeg * Math.PI / 180d);
            var hasAbove = false;
            var hasBelow = false;
            foreach (var (a, b) in intervals)
            {
                var steps = Math.Max(64, (int)Math.Ceiling(BracketSamplesPerCurve * (b - a) / fullSpan));
                var above = IsolatePositiveIntervals(
                    t => curve(t).Z - sinFloor,
                    a, b, steps,
                    Math.Max(1e-9, maxAngularSpeedRadPerParam),
                    analyticRefractionFloorHints,
                    out _);
                foreach (var sub in above)
                {
                    if (sub.To - sub.From <= 1e-14 * fullSpan) continue;
                    hasAbove = true;
                    branchedIntervals.Add((sub.From, sub.To, OracleRefractionBranch.ForceRefracted));
                }

                var below = IsolatePositiveIntervals(
                    t => sinFloor - curve(t).Z,
                    a, b, steps,
                    Math.Max(1e-9, maxAngularSpeedRadPerParam),
                    analyticRefractionFloorHints,
                    out _);
                foreach (var sub in below)
                {
                    if (sub.To - sub.From <= 1e-14 * fullSpan) continue;
                    var midBelow = sinFloor - curve(0.5 * (sub.From + sub.To)).Z;
                    if (midBelow <= 1e-14)
                    {
                        var peakT = MaximizeGolden(t => sinFloor - curve(t).Z, sub.From, sub.To, GoldenIterations);
                        if (sinFloor - curve(peakT).Z <= 1e-14) continue;
                    }
                    hasBelow = true;
                    branchedIntervals.Add((sub.From, sub.To, OracleRefractionBranch.ForceUnrefracted));
                }
            }
            if (hasAbove && hasBelow) allVisible = false;
            branchedIntervals.Sort(static (left, right) => left.From.CompareTo(right.From));
            if (branchedIntervals.Count == 0) return [];
        }
        else
        {
            foreach (var (a, b) in intervals)
            {
                branchedIntervals.Add((a, b, OracleRefractionBranch.Default));
            }
        }

        // Stage 2: Isolate optical projection domain (cz >= minDomainCosine) independently per branch.
        var minDomainCosine = ComputeMinimumDomainCosine(cam);
        if (minDomainCosine > -1d + 1e-12)
        {
            var domainIntervals = new List<(double From, double To, OracleRefractionBranch Branch)>();
            foreach (var (a, b, branch) in branchedIntervals)
            {
                var branchRefracted = cam.RefractionEnabled && branch != OracleRefractionBranch.ForceUnrefracted;
                var domainSpeed = Math.Max(1e-9, maxAngularSpeedRadPerParam * (branchRefracted ? 1.25 : 1d));
                var refractionAllowance = branchRefracted ? 0.015 : 0d;
                var steps = Math.Max(64, (int)Math.Ceiling(BracketSamplesPerCurve * (b - a) / fullSpan));
                var sub = IsolatePositiveIntervals(
                    t => OpticalDomainMargin(cam, basis, curve(t), minDomainCosine, branch),
                    a, b, steps,
                    domainSpeed,
                    analyticDomainHints,
                    out var fullyInDomain,
                    extraExclusionAllowance: refractionAllowance);
                if (!fullyInDomain) allVisible = false;
                foreach (var piece in sub)
                {
                    domainIntervals.Add((piece.From, piece.To, branch));
                }
            }
            branchedIntervals = domainIntervals;
            if (branchedIntervals.Count == 0) return [];
        }

        // Stage 3: Isolate sensor, circular aperture, and crop boundaries on each domain-feasible branch interval.
        var arcs = new List<BranchedOracleArc>();
        foreach (var (segStart, segEnd, branch) in branchedIntervals)
        {
            if (segEnd - segStart <= 1e-14 * fullSpan) continue;
            double Margin(double t) => SignedVisibilityMargin(cam, basis, transform, curve(t), out _, branch);

            var steps = Math.Max(64, (int)Math.Ceiling(BracketSamplesPerCurve * (segEnd - segStart) / fullSpan));
            var grid = new List<double>(steps + 1 + (analyticDomainHints?.Count ?? 0));
            for (var i = 0; i <= steps; i++)
            {
                grid.Add(segStart + (segEnd - segStart) * i / steps);
            }
            if (analyticDomainHints is not null)
            {
                foreach (var h in analyticDomainHints)
                {
                    if (h > segStart && h < segEnd) grid.Add(h);
                }
            }
            grid.Sort();
            var uniqueGrid = Deduplicate(grid, 1e-14 * Math.Max(1d, segEnd - segStart));
            var values = uniqueGrid.Select(Margin).ToArray();
            var transitions = new List<double> { segStart, segEnd };

            for (var i = 0; i < uniqueGrid.Count - 1; i++)
            {
                var a = uniqueGrid[i];
                var b = uniqueGrid[i + 1];
                var fa = values[i];
                var fb = values[i + 1];
                var inA = fa >= 0d;
                var inB = fb >= 0d;

                if (inA != inB)
                {
                    allVisible = false;
                    transitions.Add(BisectVisibilityRoot(Margin, a, b, inA));
                }
                else if (!inA)
                {
                    allVisible = false;
                    var midVal = Margin(0.5 * (a + b));
                    if (midVal >= 0d || Math.Max(fa, Math.Max(fb, midVal)) > -250d)
                    {
                        var peakT = MaximizeGolden(Margin, a, b, GoldenIterations);
                        if (Margin(peakT) > 1e-12)
                        {
                            transitions.Add(BisectVisibilityRoot(Margin, a, peakT, false));
                            transitions.Add(BisectVisibilityRoot(Margin, peakT, b, true));
                        }
                    }
                }
                else
                {
                    var midVal = Margin(0.5 * (a + b));
                    if (midVal < 0d || Math.Min(fa, Math.Min(fb, midVal)) < 250d)
                    {
                        var dipT = MinimizeGolden(Margin, a, b, GoldenIterations);
                        if (Margin(dipT) < -1e-12)
                        {
                            allVisible = false;
                            transitions.Add(BisectVisibilityRoot(Margin, a, dipT, true));
                            transitions.Add(BisectVisibilityRoot(Margin, dipT, b, false));
                        }
                    }
                }
            }

            transitions.Sort();
            var knots = Deduplicate(transitions, 1e-14 * Math.Max(1d, segEnd - segStart));
            for (var i = 0; i < knots.Count - 1; i++)
            {
                var a = knots[i];
                var b = knots[i + 1];
                if (b - a <= 1e-14 * fullSpan) continue;
                var mid = 0.5 * (a + b);
                if (Margin(mid) < 0d)
                {
                    allVisible = false;
                    continue;
                }
                var pts = SampleVisibleArcAdaptively(cam, basis, branch, transform, curve, a, b, fullSpan, maxAdaptiveDepth);
                if (pts.Count >= 2)
                {
                    arcs.Add(new BranchedOracleArc(new OracleVisibleArc(false, pts.ToArray()), branch));
                }
            }
        }

        _ = periodic;
        return arcs;
    }

    private static OracleVisibleArc[] MergeContiguousArcs(List<BranchedOracleArc> arcs, bool closedCandidate)
    {
        if (arcs.Count == 0) return Array.Empty<OracleVisibleArc>();
        var runs = new List<(List<OraclePoint> Points, OracleRefractionBranch Branch)>();
        foreach (var (arc, branch) in arcs)
        {
            if (arc.Points.Length < 2) continue;
            if (runs.Count > 0 &&
                runs[^1].Branch == branch &&
                Distance(runs[^1].Points[^1], arc.Points[0]) <= 1e-3)
            {
                for (var i = 1; i < arc.Points.Length; i++) runs[^1].Points.Add(arc.Points[i]);
            }
            else
            {
                runs.Add((new List<OraclePoint>(arc.Points), branch));
            }
        }
        if (runs.Count == 0) return Array.Empty<OracleVisibleArc>();
        if (runs.Count > 1 &&
            runs[^1].Branch == runs[0].Branch &&
            Distance(runs[^1].Points[^1], runs[0].Points[0]) <= 1e-3)
        {
            var last = runs[^1].Points;
            for (var i = 1; i < runs[0].Points.Count; i++) last.Add(runs[0].Points[i]);
            runs[0] = (last, runs[0].Branch);
            runs.RemoveAt(runs.Count - 1);
        }
        if (runs.Count == 1 && closedCandidate && Distance(runs[0].Points[0], runs[0].Points[^1]) <= 1e-3 && runs[0].Points.Count >= 4)
        {
            runs[0].Points.RemoveAt(runs[0].Points.Count - 1);
            return [new OracleVisibleArc(true, runs[0].Points.ToArray())];
        }
        return runs.Select(static r => new OracleVisibleArc(false, r.Points.ToArray())).ToArray();
    }

    private static double BisectVisibilityRoot(Func<double, double> margin, double a, double b, bool aIsInside)
    {
        var lo = a;
        var hi = b;
        for (var i = 0; i < BisectionIterations; i++)
        {
            var mid = 0.5 * (lo + hi);
            var midIn = margin(mid) >= 0;
            if (midIn == aIsInside) lo = mid;
            else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    private static double MaximizeGolden(Func<double, double> f, double a, double b, int iterations = GoldenIterations)
    {
        const double invPhi = 0.6180339887498949;
        var c = b - invPhi * (b - a);
        var d = a + invPhi * (b - a);
        var fc = f(c);
        var fd = f(d);
        for (var i = 0; i < iterations; i++)
        {
            if (fc < fd)
            {
                a = c;
                c = d;
                fc = fd;
                d = a + invPhi * (b - a);
                fd = f(d);
            }
            else
            {
                b = d;
                d = c;
                fd = fc;
                c = b - invPhi * (b - a);
                fc = f(c);
            }
        }
        return 0.5 * (a + b);
    }

    private static double MinimizeGolden(Func<double, double> f, double a, double b, int iterations = GoldenIterations) =>
        MaximizeGolden(t => -f(t), a, b, iterations);

    private static OraclePoint EvaluatePoint(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleTransform transform,
        OracleVec3 enu,
        OracleRefractionBranch branch = OracleRefractionBranch.Default)
    {
        return SignedVisibilityMargin(cam, basis, transform, enu, out var p, branch) >= -1e-6
            ? p
            : new OraclePoint(double.NaN, double.NaN);
    }

    private static OraclePoint ClampToCrop(OracleTransform transform, OraclePoint p) =>
        transform.IsIdentity
            ? p
            : new OraclePoint(
                Math.Clamp(p.X, transform.CropX, transform.CropX + transform.CropWidth),
                Math.Clamp(p.Y, transform.CropY, transform.CropY + transform.CropHeight));

    private static List<double> Deduplicate(List<double> sorted, double tol)
    {
        var result = new List<double>(sorted.Count);
        foreach (var v in sorted)
        {
            if (result.Count == 0 || v - result[^1] > tol) result.Add(v);
        }
        return result;
    }

    private static List<OraclePoint> SampleVisibleArcAdaptively(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleRefractionBranch branch,
        OracleTransform transform,
        Func<double, OracleVec3> curve,
        double a,
        double b,
        double fullSpan,
        int maxAdaptiveDepth)
    {
        var baseSamples = Math.Max(32, (int)Math.Ceiling(128d * (b - a) / fullSpan));
        var pts = new List<OraclePoint>(baseSamples + 1);
        var eps = 1e-12 * (b - a);
        var hasPrev = false;
        var prevT = 0d;
        OraclePoint prevP = default;

        for (var s = 0; s <= baseSamples; s++)
        {
            var t = s == 0 ? Math.Min(b, a + eps)
                : s == baseSamples ? Math.Max(a, b - eps)
                : a + (b - a) * s / baseSamples;
            if (SignedVisibilityMargin(cam, basis, transform, curve(t), out var p, branch) < -1e-6)
            {
                hasPrev = false;
                continue;
            }
            if (!hasPrev)
            {
                pts.Add(p);
                prevT = t;
                prevP = p;
                hasPrev = true;
            }
            else
            {
                AppendAdaptiveReferenceChord(cam, basis, branch, transform, curve, prevT, prevP, t, p, 0, maxAdaptiveDepth, pts);
                prevT = t;
                prevP = p;
            }
        }
        return pts;
    }

    private static void AppendAdaptiveReferenceChord(
        OracleCamera cam,
        (OracleVec3 Right, OracleVec3 Up, OracleVec3 Forward) basis,
        OracleRefractionBranch branch,
        OracleTransform transform,
        Func<double, OracleVec3> curve,
        double t0,
        OraclePoint p0,
        double t1,
        OraclePoint p1,
        int depth,
        int maxAdaptiveDepth,
        List<OraclePoint> pts)
    {
        var tMid = 0.5 * (t0 + t1);
        var midVisible = SignedVisibilityMargin(cam, basis, transform, curve(tMid), out var pMid, branch) >= -1e-6;
        if (!midVisible)
        {
            throw new InvalidOperationException(
                $"Independent clipping oracle encountered an invisible midpoint at depth {depth} (t=[{t0:R}, {t1:R}]).");
        }
        var chordMid = new OraclePoint(0.5 * (p0.X + p1.X), 0.5 * (p0.Y + p1.Y));
        var sagitta = Distance(pMid, chordMid);
        var needsSplit = sagitta > ReferenceSagittaTolerancePixels;
        if (!needsSplit && depth == 0)
        {
            // Guard against inflection-point coincidence where midpoint hits chord while quarter points bow.
            var tQ1 = 0.5 * (t0 + tMid);
            var tQ3 = 0.5 * (tMid + t1);
            if (SignedVisibilityMargin(cam, basis, transform, curve(tQ1), out var pQ1, branch) >= -1e-6 &&
                Distance(pQ1, new OraclePoint(0.75 * p0.X + 0.25 * p1.X, 0.75 * p0.Y + 0.25 * p1.Y)) > ReferenceSagittaTolerancePixels)
            {
                needsSplit = true;
            }
            else if (SignedVisibilityMargin(cam, basis, transform, curve(tQ3), out var pQ3, branch) >= -1e-6 &&
                Distance(pQ3, new OraclePoint(0.25 * p0.X + 0.75 * p1.X, 0.25 * p0.Y + 0.75 * p1.Y)) > ReferenceSagittaTolerancePixels)
            {
                needsSplit = true;
            }
        }
        if (needsSplit)
        {
            if (depth >= maxAdaptiveDepth || t1 - t0 <= 1e-14)
            {
                throw new InvalidOperationException(
                    $"Independent clipping oracle failed to satisfy sagitta error bound ({sagitta:E4} px > {ReferenceSagittaTolerancePixels:E4} px) at depth {depth}.");
            }
            AppendAdaptiveReferenceChord(cam, basis, branch, transform, curve, t0, p0, tMid, pMid, depth + 1, maxAdaptiveDepth, pts);
            AppendAdaptiveReferenceChord(cam, basis, branch, transform, curve, tMid, pMid, t1, p1, depth + 1, maxAdaptiveDepth, pts);
        }
        else
        {
            pts.Add(p1);
        }
    }

    private readonly record struct OracleSegment(
        OraclePoint A,
        OraclePoint B,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double Dx,
        double Dy,
        double LenSq);

    private static List<OracleSegment> ExtractSegments(IReadOnlyList<(bool Closed, OraclePoint[] Points)> parts)
    {
        var segs = new List<OracleSegment>();
        foreach (var (closed, pts) in parts)
        {
            if (pts.Length < 2) continue;
            var count = closed ? pts.Length : pts.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                segs.Add(new OracleSegment(
                    a, b,
                    Math.Min(a.X, b.X), Math.Max(a.X, b.X),
                    Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y),
                    dx, dy, dx * dx + dy * dy));
            }
        }
        return segs;
    }

    private static double DistancePointToSegments(OraclePoint p, List<OracleSegment> segments)
    {
        var bestSq = double.PositiveInfinity;
        var bestDist = double.PositiveInfinity;
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (p.X < seg.MinX - bestDist || p.X > seg.MaxX + bestDist ||
                p.Y < seg.MinY - bestDist || p.Y > seg.MaxY + bestDist)
            {
                continue;
            }
            double dSq;
            if (seg.LenSq <= 1e-24)
            {
                dSq = (p.X - seg.A.X) * (p.X - seg.A.X) + (p.Y - seg.A.Y) * (p.Y - seg.A.Y);
            }
            else
            {
                var t = Math.Clamp(((p.X - seg.A.X) * seg.Dx + (p.Y - seg.A.Y) * seg.Dy) / seg.LenSq, 0d, 1d);
                var rx = seg.A.X + t * seg.Dx - p.X;
                var ry = seg.A.Y + t * seg.Dy - p.Y;
                dSq = rx * rx + ry * ry;
            }
            if (dSq < bestSq)
            {
                bestSq = dSq;
                bestDist = Math.Sqrt(bestSq);
            }
        }
        return bestDist;
    }

    private static double Distance(OraclePoint a, OraclePoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static OracleVec3 RotateZ(OracleVec3 v, double angleRad)
    {
        var c = Math.Cos(angleRad);
        var s = Math.Sin(angleRad);
        return new OracleVec3(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }

    private static OracleVec3 RotateY(OracleVec3 v, double angleRad)
    {
        var c = Math.Cos(angleRad);
        var s = Math.Sin(angleRad);
        return new OracleVec3(c * v.X + s * v.Z, v.Y, -s * v.X + c * v.Z);
    }
}
