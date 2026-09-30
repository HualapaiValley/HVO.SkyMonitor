using System.Diagnostics;

namespace HVO.SkyMonitor.Astronomy;

internal sealed record CorePose(double Altitude, double Azimuth, double Roll);
internal sealed record CoreSite(double Latitude, double Longitude);
internal sealed record CoreDetection(int Index, double X, double Y, double Flux, double Peak)
{
    internal PixelPoint Pixel => new(X, Y);
}
internal sealed record SolverOptics(ProjectionModel Model, int Width, int Height,
    double PrincipalX, double PrincipalY, double FocalX, double FocalY, double? CircleRadius,
    double BoresightAltitude, double BoresightAzimuth, double Roll, bool HorizontalFlip)
{
    internal static SolverOptics From(ProjectionContext p) => new(p.Model, p.WidthPixels, p.HeightPixels,
        p.PrincipalPointX, p.PrincipalPointY, p.FocalLengthXPixels, p.FocalLengthYPixels, p.ImageCircleRadiusPixels,
        p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees, p.HorizontalFlip);
    internal ProjectionContext Context() => new(Model, PrincipalX, PrincipalY, FocalX, FocalY, Width, Height,
        Model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
        CircleRadius, BoresightAltitude, BoresightAzimuth, Roll, HorizontalFlip);
    internal IImageProjector Projector() { var context = Context(); context.Validate(); return ProjectorFactory.Create(context); }
}
internal static class AstrometricMath
{
    internal static AltAzPoint Horizontal(CelestialCatalogObject star, DateTimeOffset utc, CoreSite site) =>
        CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(new(star.RightAscensionHours, star.DeclinationDegrees), utc), utc, site.Latitude, site.Longitude);
    internal static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    internal static double Rms(IEnumerable<double> values) { var v = values.ToArray(); return v.Length == 0 ? double.NaN : Math.Sqrt(v.Average(n => n * n)); }
}
internal sealed class AstrometricWorkControl(double budgetMilliseconds, CancellationToken cancellationToken)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    internal double ElapsedMilliseconds => watch.Elapsed.TotalMilliseconds;
    internal void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (watch.Elapsed.TotalMilliseconds > budgetMilliseconds) throw new TimeoutException("Astrometric work budget exceeded.");
    }
}
