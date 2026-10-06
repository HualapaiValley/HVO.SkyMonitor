using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>Cartesian reference equations, separate from production astronomy/projector implementations.</summary>
internal static class VirtualAstrometryReference
{
    internal readonly record struct Vector(double X, double Y, double Z)
    {
        internal double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        internal Vector Unit => this / Length;
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector operator *(Vector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public static Vector operator /(Vector a, double scale) => new(a.X / scale, a.Y / scale, a.Z / scale);
        internal static double Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static Vector Cross(Vector a, Vector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
    internal readonly record struct Basis(Vector Right, Vector Up, Vector Forward);
    internal static double Radians(double degrees) => degrees * Math.PI / 180;
    private static Vector RotateZ(Vector value, double angle) => new(
        Math.Cos(angle) * value.X - Math.Sin(angle) * value.Y,
        Math.Sin(angle) * value.X + Math.Cos(angle) * value.Y, value.Z);
    private static Vector RotateY(Vector value, double angle) => new(
        Math.Cos(angle) * value.X + Math.Sin(angle) * value.Z, value.Y,
        -Math.Sin(angle) * value.X + Math.Cos(angle) * value.Z);
    private static double Days(DateTimeOffset utc) => (utc - DateTimeOffset.UnixEpoch).TotalDays;
    private static (double Zeta, double Z, double Theta) Angles(DateTimeOffset utc)
    {
        var t = (2440587.5 + Days(utc) - 2451545) / 36525;
        var t2 = t * t; var t3 = t2 * t;
        return (Radians((2306.2181 * t + .30188 * t2 + .017998 * t3) / 3600),
            Radians((2306.2181 * t + 1.09468 * t2 + .018203 * t3) / 3600),
            Radians((2004.3109 * t - .42665 * t2 - .041833 * t3) / 3600));
    }
    private static Vector Precess(Vector j2000, DateTimeOffset utc)
    {
        var (zeta, z, theta) = Angles(utc);
        return RotateZ(RotateY(RotateZ(j2000, zeta), -theta), z);
    }
    private static Vector Unprecess(Vector ofDate, DateTimeOffset utc)
    {
        var (zeta, z, theta) = Angles(utc);
        return RotateZ(RotateY(RotateZ(ofDate, -z), theta), -zeta);
    }
    private static double Sidereal(DateTimeOffset utc, ObserverLocation site) => Radians(
        (280.46061837 + 360.98564736629 * (2440587.5 + Days(utc) - 2451545) + site.LongitudeDegrees) % 360);
    internal static Vector J2000(CelestialCatalogObject star)
    {
        var ra = star.RightAscensionHours * Math.PI / 12; var dec = Radians(star.DeclinationDegrees);
        return new(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
    }
    internal static Vector ToEnu(Vector j2000, DateTimeOffset utc, ObserverLocation site)
    {
        var value = Precess(j2000, utc); var l = Sidereal(utc, site); var p = Radians(site.LatitudeDegrees);
        return new(-Math.Sin(l) * value.X + Math.Cos(l) * value.Y,
            -Math.Sin(p) * Math.Cos(l) * value.X - Math.Sin(p) * Math.Sin(l) * value.Y + Math.Cos(p) * value.Z,
            Math.Cos(p) * Math.Cos(l) * value.X + Math.Cos(p) * Math.Sin(l) * value.Y + Math.Sin(p) * value.Z);
    }
    internal static Vector FromEnu(Vector enu, DateTimeOffset utc, ObserverLocation site)
    {
        var l = Sidereal(utc, site); var p = Radians(site.LatitudeDegrees);
        return Unprecess(new(-Math.Sin(l) * enu.X - Math.Sin(p) * Math.Cos(l) * enu.Y + Math.Cos(p) * Math.Cos(l) * enu.Z,
            Math.Cos(l) * enu.X - Math.Sin(p) * Math.Sin(l) * enu.Y + Math.Cos(p) * Math.Sin(l) * enu.Z,
            Math.Cos(p) * enu.Y + Math.Sin(p) * enu.Z), utc);
    }
    internal static EquatorialPoint MeanOfDate(CelestialCatalogObject star, DateTimeOffset utc)
    {
        var v = Precess(J2000(star), utc);
        return new((Math.Atan2(v.Y, v.X) * 12 / Math.PI + 24) % 24, Math.Asin(Math.Clamp(v.Z, -1, 1)) * 180 / Math.PI);
    }
    internal static AltAzPoint Horizontal(Vector enu) => new(
        Math.Asin(Math.Clamp(enu.Z, -1, 1)) * 180 / Math.PI, (Math.Atan2(enu.X, enu.Y) * 180 / Math.PI + 360) % 360);
    internal static Basis Pose(double altitude, double azimuth, double roll, bool flip)
    {
        var a = Radians(altitude); var z = Radians(azimuth); var rho = Radians(roll);
        var forward = new Vector(Math.Cos(a) * Math.Sin(z), Math.Cos(a) * Math.Cos(z), Math.Sin(a));
        var up = new Vector(0, 0, 1) - forward * forward.Z;
        up = up.Length < 1e-12 ? new(0, 1, 0) : up.Unit;
        var right = Vector.Cross(up, forward).Unit;
        return new((right * Math.Cos(rho) + up * Math.Sin(rho)) * (flip ? -1 : 1),
            up * Math.Cos(rho) - right * Math.Sin(rho), forward);
    }
    internal static Basis Pose(CameraRigConfig rig) => Pose(rig.Orientation.BoresightAltitudeDegrees,
        rig.Orientation.BoresightAzimuthDegrees, rig.Orientation.RollAdjustmentDegrees, rig.Optics.HorizontalFlip);
    internal static ProjectionModel Model(CameraRigConfig rig) => RigProjectionContextFactory.ParseModel(rig.Optics.ProjectionModel);
    internal static double NativeFocal(CameraRigConfig rig)
    {
        if (rig.Optics.FocalLengthXPixels is { } focal) return focal;
        if (rig.Optics.FocalLengthMillimeters > 0) return rig.Optics.FocalLengthMillimeters / (rig.Sensor.PixelSizeMicrons / 1000);
        var radius = rig.Optics.ImageCircleRadiusPixels!.Value; var half = Radians(rig.Optics.FieldOfViewDegrees / 2);
        return Model(rig) switch
        {
            ProjectionModel.EquidistantFisheye => radius / half,
            ProjectionModel.EquisolidFisheye => radius / (2 * Math.Sin(half / 2)),
            ProjectionModel.OrthographicFisheye => radius / Math.Sin(half),
            ProjectionModel.StereographicFisheye => radius / (2 * Math.Tan(half / 2)),
            _ => throw new NotSupportedException("A perspective reference requires a declared focal length.")
        };
    }
    /// <summary>Ideal normalized radius g(theta) of each family; null outside its forward domain.</summary>
    internal static double? IdealRadius(ProjectionModel model, double theta) => model switch
    {
        ProjectionModel.EquidistantFisheye => theta,
        ProjectionModel.EquisolidFisheye => 2 * Math.Sin(theta / 2),
        ProjectionModel.OrthographicFisheye => theta <= Math.PI / 2 ? Math.Sin(theta) : null,
        ProjectionModel.StereographicFisheye => theta < Math.PI ? 2 * Math.Tan(theta / 2) : null,
        ProjectionModel.Perspective => theta < Math.PI / 2 ? Math.Tan(theta) : null,
        _ => throw new NotSupportedException()
    };
    private static double? Theta(ProjectionModel model, double ideal) => model switch
    {
        ProjectionModel.EquidistantFisheye => ideal,
        ProjectionModel.EquisolidFisheye => ideal <= 2 ? 2 * Math.Asin(ideal / 2) : null,
        ProjectionModel.OrthographicFisheye => ideal <= 1 ? Math.Asin(ideal) : null,
        ProjectionModel.StereographicFisheye => 2 * Math.Atan(ideal / 2),
        ProjectionModel.Perspective => Math.Atan(ideal),
        _ => throw new NotSupportedException()
    };
    /// <summary>Independent Newton inverse of d = g(1 + k1 g^2) on the monotonic branch that starts at the origin.</summary>
    private static double? IdealFromDistorted(double distorted, double k1)
    {
        if (k1 == 0) return distorted;
        var g = distorted;
        for (var i = 0; i < 100; i++)
        {
            var slope = 1 + 3 * k1 * g * g;
            if (slope <= 0) return null;
            var step = (g * (1 + k1 * g * g) - distorted) / slope;
            g -= step;
            if (Math.Abs(step) < 1e-15 * Math.Max(1, g)) return g;
        }
        return null;
    }
    internal static bool IsCircular(CameraRigConfig rig) => Model(rig) != ProjectionModel.Perspective;
    internal static PixelPoint? Project(CameraRigConfig rig, Vector enu)
    {
        var basis = Pose(rig); var model = Model(rig);
        var qx = Vector.Dot(enu, basis.Right); var qy = Vector.Dot(enu, basis.Up); var qz = Vector.Dot(enu, basis.Forward);
        var theta = Math.Acos(Math.Clamp(qz, -1, 1)); var length = Math.Sqrt(qx * qx + qy * qy);
        if (IdealRadius(model, theta) is not { } g) return null;
        // Family normalized radius g(theta), scaled by the one-coefficient radial term 1 + k1 g^2.
        var distance = NativeFocal(rig) * g * (1 + rig.Optics.RadialDistortionK1 * g * g);
        if (model != ProjectionModel.Perspective && distance > rig.Optics.ImageCircleRadiusPixels!.Value) return null;
        var x = rig.Optics.PrincipalPointX!.Value + (length < 1e-12 ? 0 : distance * qx / length);
        var y = rig.Optics.PrincipalPointY!.Value - (length < 1e-12 ? 0 : distance * qy / length);
        var readout = rig.Readout!;
        x = (x - readout.Roi.X) / readout.BinX; y = (y - readout.Roi.Y) / readout.BinY;
        return x >= 0 && y >= 0 && x < readout.Roi.Width / readout.BinX && y < readout.Roi.Height / readout.BinY ? new(x, y) : null;
    }
    internal static Vector? Unproject(CameraRigConfig rig, PixelPoint pixel)
    {
        var readout = rig.Readout!; var model = Model(rig);
        if (pixel.X < 0 || pixel.Y < 0 || pixel.X >= readout.Roi.Width / readout.BinX || pixel.Y >= readout.Roi.Height / readout.BinY) return null;
        var dx = pixel.X * readout.BinX + readout.Roi.X - rig.Optics.PrincipalPointX!.Value;
        var dy = rig.Optics.PrincipalPointY!.Value - pixel.Y * readout.BinY - readout.Roi.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (model != ProjectionModel.Perspective && length > rig.Optics.ImageCircleRadiusPixels!.Value) return null;
        if (IdealFromDistorted(length / NativeFocal(rig), rig.Optics.RadialDistortionK1) is not { } ideal ||
            Theta(model, ideal) is not { } theta) return null;
        var basis = Pose(rig);
        return length < 1e-12 ? basis.Forward : basis.Right * (Math.Sin(theta) * dx / length)
            + basis.Up * (Math.Sin(theta) * dy / length) + basis.Forward * Math.Cos(theta);
    }
    /// <summary>Readout pixel at least <paramref name="margin"/> inside a circular aperture; rectangular apertures defer to the caller's frame border.</summary>
    internal static bool IsApertureInterior(CameraRigConfig rig, PixelPoint pixel, double margin)
    {
        if (!IsCircular(rig)) return true;
        var cx = (rig.Optics.PrincipalPointX!.Value - rig.Readout!.Roi.X) / rig.Readout.BinX;
        var cy = (rig.Optics.PrincipalPointY!.Value - rig.Readout.Roi.Y) / rig.Readout.BinY;
        var interiorRadius = rig.Optics.ImageCircleRadiusPixels!.Value / rig.Readout.BinX - margin;
        return Math.Pow(pixel.X - cx, 2) + Math.Pow(pixel.Y - cy, 2) < interiorRadius * interiorRadius;
    }
    internal static double SeparationDegrees(Vector a, Vector b) =>
        Math.Atan2(Vector.Cross(a, b).Length, Vector.Dot(a, b)) * 180 / Math.PI;
    internal static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    internal static double PoseError(CameraRigConfig rig, DateTimeOffset sceneUtc, AstrometricFrameAssessment assessment)
    {
        var truth = Pose(rig); var p = assessment.Parameters!;
        var fitted = Pose(p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees, rig.Optics.HorizontalFlip);
        Vector Transport(Vector value) => ToEnu(FromEnu(value, sceneUtc, assessment.Frame.Observer), assessment.Frame.MidpointUtc, assessment.Frame.Observer);
        return new[] { SeparationDegrees(Transport(truth.Right), fitted.Right), SeparationDegrees(Transport(truth.Up), fitted.Up),
            SeparationDegrees(Transport(truth.Forward), fitted.Forward) }.Max();
    }

    internal static object Score(CameraRigConfig truth, DateTimeOffset sceneUtc, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, IReadOnlyList<AstrometricDetection> measurements,
        AstrometricSolveResult solved, List<string> failures, string caseId)
    {
        var mapping = new AstrometricMapping(nominal, solved.Assessment);
        var expected = catalog.Stars.Select(star => (Star: star, Ray: ToEnu(J2000(star), sceneUtc, VirtualAstrometryFixture.Observer)))
            .Where(item => item.Ray.Z > 0).Select(item => (item.Star, Pixel: Project(truth, item.Ray)))
            .Where(item => item.Pixel is not null).Select(item => (item.Star, Pixel: item.Pixel!.Value)).ToArray();
        var associationRows = solved.Associations.Select(association =>
        {
            var measured = measurements.Single(d => d.Index == association.DetectionIndex);
            var nearest = expected.MinBy(item => Distance(measured.Pixel, item.Pixel));
            var actual = expected.Single(item => item.Star.Id == association.CatalogId);
            return new
            {
                association.CatalogId,
                association.DetectionIndex,
                association.Verification,
                expectedNearestId = nearest.Star.Id,
                residualPixels = Distance(measured.Pixel, actual.Pixel)
            };
        }).ToArray();
        var precision = associationRows.Count(row => row.CatalogId == row.expectedNearestId) / (double)associationRows.Length;
        var associatedIds = solved.Associations.Select(a => a.CatalogId).ToHashSet(StringComparer.Ordinal);
        var withheld = expected.Where(item => !associatedIds.Contains(item.Star.Id) &&
            item.Pixel.X > 12 && item.Pixel.Y > 12 && item.Pixel.X < nominal.Projection.WidthPixels - 12 &&
            item.Pixel.Y < nominal.Projection.HeightPixels - 12 &&
            IsApertureInterior(truth, item.Pixel, 12)).Select(item =>
        {
            var mapped = mapping.SkyToPixel(MeanOfDate(item.Star, solved.Assessment.Frame.MidpointUtc));
            return new
            {
                item.Star.Id,
                expected = item.Pixel,
                measuredMapping = mapped,
                residualPixels = mapped is null ? (double?)null : Distance(mapped.Value, item.Pixel)
            };
        }).ToArray();
        var missing = withheld.Count(row => row.residualPixels is null);
        var sorted = withheld.Where(row => row.residualPixels is not null).Select(row => row.residualPixels!.Value).Order().ToArray();
        var rms = Math.Sqrt(sorted.Sum(value => value * value) / Math.Max(1, sorted.Length));
        var p95 = sorted.Length == 0 ? (double?)null : sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1];
        var poseError = PoseError(truth, sceneUtc, solved.Assessment);
        var fittedFocal = nominal.Projection.FocalLengthXPixels * solved.Assessment.Parameters!.FocalScale;
        var focalError = Math.Abs(fittedFocal / (NativeFocal(truth) / truth.Readout!.BinX) - 1);
        if (precision != 1) failures.Add($"{caseId}: association precision {precision:R}");
        if (withheld.Length < 4 || missing > 0 || rms > .5 || p95 is null or > .75)
            failures.Add($"{caseId}: independent withheld mapping {withheld.Length} stars, {missing} missing, RMS {rms:R}, p95 {p95:R}");
        if (poseError > .06 || focalError > .001) failures.Add($"{caseId}: pose {poseError:R} deg, focal relative error {focalError:R}");
        if (solved.Assessment.Mode == AstrometricSolveMode.Warm && solved.Metrics.Hypotheses != 0) failures.Add($"{caseId}: warm search ran hypotheses");
        if (solved.Associations.Select(a => a.DetectionIndex).Distinct().Count() != solved.Associations.Count)
            failures.Add($"{caseId}: non-unique measured associations");
        return new
        {
            precision,
            poseErrorDegrees = poseError,
            focalRelativeError = focalError,
            withheldCount = withheld.Length,
            missingMappings = missing,
            withheldRmsPixels = rms,
            withheldP95Pixels = p95,
            associationRows,
            withheld
        };
    }
}
