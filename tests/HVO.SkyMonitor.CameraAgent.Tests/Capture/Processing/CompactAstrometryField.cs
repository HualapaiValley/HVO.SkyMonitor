using System.Collections.Immutable;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

internal static class CompactAstrometryField
{
    internal const int Size = 256;
    internal const double FocalLength = 72;
    internal const double ImageCircleRadius = 116;

    // An irregular field with isolated stars in all quadrants, including seven withheld by the blind solver.
    private static readonly ImmutableArray<PixelPoint> Offsets =
    [
        new(30, -53), new(62, -18), new(-16, 21), new(-45, -38), new(-16, -19), new(28, 52),
        new(-60, -22), new(-61, 18), new(-49, 43), new(-22, 45), new(15, -40), new(4, 41),
        new(41, 11), new(20, 6), new(-70, -49), new(-84, -5), new(-21, -82), new(73, 46),
        new(54, 65), new(-78, 36), new(38, -75), new(-61, 60), new(13, -84), new(68, -41),
        new(-35, 72), new(1, 86)
    ];

    internal static CelestialCatalogObject[] CreateStars(
        ProjectionContext projection, ObserverLocation observer, DateTimeOffset midpoint)
    {
        var projector = ProjectorFactory.Create(projection);
        return Offsets.Select((offset, index) =>
        {
            var direction = projector.Unproject(new(projection.PrincipalPointX + offset.X, projection.PrincipalPointY + offset.Y));
            Assert.IsNotNull(direction);
            var ofDate = CoordinateTransforms.HorizontalToEquatorial(direction.Value, midpoint,
                observer.LatitudeDegrees, observer.LongitudeDegrees);
            var star = EquatorialPrecession.PrecessToJ2000(ofDate, midpoint);
            var id = FormattableString.Invariant($"field-star-{index:D2}");
            return new CelestialCatalogObject(id, id, star.RightAscensionHours, star.DeclinationDegrees, 1.8 + index * .08);
        }).ToArray();
    }
}
