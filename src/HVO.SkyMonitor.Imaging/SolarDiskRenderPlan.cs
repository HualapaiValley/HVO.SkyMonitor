using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Bounded uniform disk/illuminated-hemisphere raster in incident electron-rate space.</summary>
public sealed class SolarDiskRenderPlan
{
    public const string AlgorithmVersion = "solar-lunar-geometric-disk-bilinear-v1";
    private readonly Dictionary<int, double> _rates = [];

    public SolarDiskRenderPlan(ProjectionContext projection, IReadOnlyList<SolarDiskAppearance> disks,
        double magnitudeZeroElectronsPerSecond, CancellationToken cancellationToken = default)
    {
        projection.Validate();
        ArgumentNullException.ThrowIfNull(disks);
        if (disks.Count > 2 || disks.Any(disk => disk is null) || disks.Select(disk => disk.Body).Distinct().Count() != disks.Count ||
            !double.IsFinite(magnitudeZeroElectronsPerSecond) || magnitudeZeroElectronsPerSecond is < 0 or > 1e12)
            throw new ArgumentOutOfRangeException(nameof(disks));
        Projection = projection;
        Disks = Array.AsReadOnly(disks.ToArray());
        var projector = ProjectorFactory.Create(projection);
        foreach (var disk in Disks)
        {
            if (disk.Body is not (SolarSystemBody.Sun or SolarSystemBody.Moon) ||
                !double.IsFinite(disk.AngularRadiusDegrees) || disk.AngularRadiusDegrees is <= 0 or > 1 ||
                !double.IsFinite(disk.VisualMagnitude) || disk.VisualMagnitude is < -30 or > 30 ||
                !double.IsFinite(disk.IlluminatedFraction) || disk.IlluminatedFraction is < 0 or > 1 ||
                !double.IsFinite(disk.BrightLimbAngleDegrees) ||
                !double.IsFinite(disk.Direction.AltitudeDegrees) || disk.Direction.AltitudeDegrees is < -90 or > 90 ||
                !double.IsFinite(disk.Direction.AzimuthDegrees)) throw new ArgumentOutOfRangeException(nameof(disks));
            if (disk.Direction.AltitudeDegrees + disk.AngularRadiusDegrees <= 0 || disk.IlluminatedFraction == 0) continue;
            var radius = disk.AngularRadiusDegrees * Math.PI / 180;
            var altitude = disk.Direction.AltitudeDegrees * Math.PI / 180;
            var azimuth = disk.Direction.AzimuthDegrees * Math.PI / 180;
            var limb = disk.BrightLimbAngleDegrees * Math.PI / 180;
            var phaseCosine = 2 * disk.IlluminatedFraction - 1;
            var phaseSine = Math.Sqrt(Math.Max(0, 1 - phaseCosine * phaseCosine));
            var samples = Math.Clamp((int)Math.Ceiling(8 * radius * Math.Max(projection.FocalLengthXPixels, projection.FocalLengthYPixels)), 32, 512);
            var points = new List<PixelPoint>();
            var illuminated = 0;
            for (var y = 0; y < samples; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var v = 2 * (y + .5) / samples - 1;
                for (var x = 0; x < samples; x++)
                {
                    var u = 2 * (x + .5) / samples - 1;
                    var squared = u * u + v * v;
                    if (squared >= 1) continue;
                    var z = Math.Sqrt(1 - squared);
                    if ((u * Math.Sin(limb) + v * Math.Cos(limb)) * phaseSine + z * phaseCosine <= 0) continue;
                    illuminated++;
                    var offset = radius * Math.Sqrt(squared);
                    var scale = squared == 0 ? radius : Math.Sin(offset) / Math.Sqrt(squared);
                    var forward = Math.Cos(offset);
                    var east = forward * Math.Cos(altitude) * Math.Sin(azimuth) + scale * (u * Math.Cos(azimuth) - v * Math.Sin(altitude) * Math.Sin(azimuth));
                    var north = forward * Math.Cos(altitude) * Math.Cos(azimuth) + scale * (-u * Math.Sin(azimuth) - v * Math.Sin(altitude) * Math.Cos(azimuth));
                    var up = forward * Math.Sin(altitude) + scale * v * Math.Cos(altitude);
                    if (up < 0) continue;
                    var direction = new AltAzPoint(Math.Asin(Math.Clamp(up, -1, 1)) * 180 / Math.PI,
                        (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360);
                    if (projector.Project(direction) is { } pixel) points.Add(pixel);
                }
            }
            if (illuminated == 0) continue;
            // Normalize before horizon/sensor clipping; cropping never restores lost source flux.
            var rate = magnitudeZeroElectronsPerSecond * Math.Pow(10, -.4 * disk.VisualMagnitude) / illuminated;
            foreach (var point in points)
            {
                var x = (int)Math.Floor(point.X - .5);
                var y = (int)Math.Floor(point.Y - .5);
                var fx = point.X - .5 - x;
                var fy = point.Y - .5 - y;
                Add(x, y, rate * (1 - fx) * (1 - fy));
                Add(x + 1, y, rate * fx * (1 - fy));
                Add(x, y + 1, rate * (1 - fx) * fy);
                Add(x + 1, y + 1, rate * fx * fy);
            }
        }
        MaximumElectronRate = _rates.Count == 0 ? 0 : _rates.Values.Max();
    }

    public ProjectionContext Projection { get; }
    public IReadOnlyList<SolarDiskAppearance> Disks { get; }
    public double MaximumElectronRate { get; }
    public double ElectronRate(int x, int y) => x < 0 || x >= Projection.WidthPixels || y < 0 || y >= Projection.HeightPixels
        ? 0 : _rates.GetValueOrDefault(y * Projection.WidthPixels + x);

    private void Add(int x, int y, double rate)
    {
        if (x < 0 || x >= Projection.WidthPixels || y < 0 || y >= Projection.HeightPixels ||
            !Projection.ContainsSample(x + .5, y + .5) || rate == 0) return;
        var index = y * Projection.WidthPixels + x;
        _rates[index] = _rates.GetValueOrDefault(index) + rate;
        if (_rates.Count > 524288) throw new InvalidOperationException("Solar disk sparse pixel budget exceeded.");
    }
}
