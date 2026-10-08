using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Optical and scene settings that a resolved Sun/Moon raster binds to.</summary>
/// <param name="PsfSigmaPixels">Gaussian sigma of the configured PSF, the same value point sources use.</param>
/// <param name="PsfRadiusPixels">Circular cutoff of the configured PSF kernel.</param>
/// <param name="HorizonPolicy">The scene horizon policy applied to every surface sample.</param>
/// <param name="Refraction">The scene refraction applied to every surface sample.</param>
public sealed record SolarDiskRenderSettings(
    double PsfSigmaPixels = 1,
    double PsfRadiusPixels = 4,
    HorizonPolicy HorizonPolicy = HorizonPolicy.GeometricHorizon,
    RefractionOptions Refraction = default)
{
    public void Validate()
    {
        if (!double.IsFinite(PsfSigmaPixels) || PsfSigmaPixels <= 0 || !double.IsFinite(PsfRadiusPixels) ||
            PsfRadiusPixels is <= 0 or > 64 || !Enum.IsDefined(HorizonPolicy))
            throw new ArgumentOutOfRangeException(nameof(SolarDiskRenderSettings));
        Refraction.Validate();
    }
}

/// <summary>
/// Bounded resolved disk/illuminated-hemisphere raster in incident electron-rate space.
/// Surface samples share the resolved-source frame and scene projector with the projected footprint,
/// are deposited bilinearly onto a grid padded by the PSF radius, convolved with the configured PSF,
/// and only then clipped to the sensor and aperture.
/// </summary>
public sealed class SolarDiskRenderPlan
{
    public const string AlgorithmVersion = "solar-lunar-resolved-psf-v4-resolved-footprints";
    public const int MaximumSparsePixels = 524288;
    public const long MaximumKernelCellVisits = 67_108_864;
    private readonly Dictionary<int, double> _rates = [];

    public SolarDiskRenderPlan(ProjectionContext projection, IReadOnlyList<SolarDiskAppearance> disks,
        double magnitudeZeroElectronsPerSecond, CancellationToken cancellationToken = default)
        : this(projection, disks, magnitudeZeroElectronsPerSecond, new SolarDiskRenderSettings(), cancellationToken)
    {
    }

    public SolarDiskRenderPlan(ProjectionContext projection, IReadOnlyList<SolarDiskAppearance> disks,
        double magnitudeZeroElectronsPerSecond, SolarDiskRenderSettings settings, CancellationToken cancellationToken = default)
    {
        projection.Validate();
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (disks.Count > 2 || disks.Any(disk => disk is null) || disks.Select(disk => disk.Body).Distinct().Count() != disks.Count ||
            !double.IsFinite(magnitudeZeroElectronsPerSecond) || magnitudeZeroElectronsPerSecond is < 0 or > 1e12)
            throw new ArgumentOutOfRangeException(nameof(disks));
        Projection = projection;
        Settings = settings;
        Disks = Array.AsReadOnly(disks.ToArray());
        var projector = new ResolvedSourceProjector(projection, settings.HorizonPolicy, settings.Refraction);
        var padding = (int)Math.Ceiling(settings.PsfRadiusPixels);
        var paddedWidth = (long)projection.WidthPixels + 2 * padding;
        // Insertion-ordered and never removed from, so iteration and summation order are deterministic.
        var deposits = new Dictionary<long, double>();
        foreach (var disk in Disks)
        {
            if (disk.Body is not (SolarSystemBody.Sun or SolarSystemBody.Moon) ||
                !double.IsFinite(disk.AngularRadiusDegrees) || disk.AngularRadiusDegrees is <= 0 or > 1 ||
                !double.IsFinite(disk.VisualMagnitude) || disk.VisualMagnitude is < -30 or > 30 ||
                !double.IsFinite(disk.IlluminatedFraction) || disk.IlluminatedFraction is < 0 or > 1 ||
                !double.IsFinite(disk.BrightLimbAngleDegrees) ||
                !double.IsFinite(disk.Direction.AltitudeDegrees) || disk.Direction.AltitudeDegrees is < -90 or > 90 ||
                !double.IsFinite(disk.Direction.AzimuthDegrees)) throw new ArgumentOutOfRangeException(nameof(disks));
            if (settings.HorizonPolicy == HorizonPolicy.GeometricHorizon &&
                disk.Direction.AltitudeDegrees + disk.AngularRadiusDegrees <= 0 || disk.IlluminatedFraction == 0) continue;
            var radius = disk.AngularRadiusDegrees * Math.PI / 180;
            var limb = disk.BrightLimbAngleDegrees * Math.PI / 180;
            var phaseCosine = 2 * disk.IlluminatedFraction - 1;
            var phaseSine = Math.Sqrt(Math.Max(0, 1 - phaseCosine * phaseCosine));
            var angularScale = ProjectionSamplingBounds.MaximumPixelsPerRadian(projection, disk.Direction, radius);
            if (angularScale == 0) continue;
            // At least one quadrature sample per projected pixel across either grid axis; prefer four.
            // Refuse excessive magnification rather than silently capping into a visibly perforated disk.
            var minimumSamples = Math.Ceiling(2 * radius * angularScale);
            if (!double.IsFinite(minimumSamples) || minimumSamples > 512)
                throw new InvalidOperationException("Solar disk projection sampling budget exceeded.");
            var samples = (int)Math.Clamp(Math.Ceiling(8 * radius * angularScale), 32, 512);
            // Only the local up/east axes are used, and they do not depend on the observer latitude.
            var frame = ResolvedSourceFrame.Create(disk.Direction, 0);
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
                    // u runs along local east and v along local up, matching the bright-limb angle's frame.
                    if (projector.TryProject(frame.DirectionFromLocal(radius * v, radius * u), out var pixel)) points.Add(pixel);
                }
            }
            if (illuminated == 0) continue;
            // Normalize before horizon/PSF/sensor clipping; cropping never restores lost source flux.
            var source = magnitudeZeroElectronsPerSecond * Math.Pow(10, -.4 * disk.VisualMagnitude);
            SourceElectronRate += source;
            var rate = source / illuminated;
            foreach (var point in points)
            {
                VisibleElectronRate += rate;
                var x = (long)Math.Floor(point.X - .5);
                var y = (long)Math.Floor(point.Y - .5);
                var fx = point.X - .5 - x;
                var fy = point.Y - .5 - y;
                Deposit(x, y, rate * (1 - fx) * (1 - fy));
                Deposit(x + 1, y, rate * fx * (1 - fy));
                Deposit(x, y + 1, rate * (1 - fx) * fy);
                Deposit(x + 1, y + 1, rate * fx * fy);
            }
        }

        var kernel = CreateKernel(settings.PsfSigmaPixels, settings.PsfRadiusPixels);
        foreach (var (key, value) in deposits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KernelCellVisits += kernel.Length;
            if (KernelCellVisits > MaximumKernelCellVisits)
                throw new InvalidOperationException("Solar disk PSF kernel budget exceeded.");
            var sourceX = (int)(key % paddedWidth) - padding;
            var sourceY = (int)(key / paddedWidth) - padding;
            foreach (var (dx, dy, weight) in kernel) Add(sourceX + dx, sourceY + dy, value * weight);
        }
        RetainedElectronRate = _rates.Values.Sum();
        MaximumElectronRate = _rates.Count == 0 ? 0 : _rates.Values.Max();

        void Deposit(long x, long y, double value)
        {
            if (value == 0 || x < -padding || x >= projection.WidthPixels + padding ||
                y < -padding || y >= projection.HeightPixels + padding) return;
            var key = (y + padding) * paddedWidth + x + padding;
            deposits[key] = deposits.GetValueOrDefault(key) + value;
            if (deposits.Count > MaximumSparsePixels) throw new InvalidOperationException("Solar disk sparse pixel budget exceeded.");
        }
    }

    public ProjectionContext Projection { get; }
    public SolarDiskRenderSettings Settings { get; }
    public IReadOnlyList<SolarDiskAppearance> Disks { get; }
    public double MaximumElectronRate { get; }

    /// <summary>Magnitude-derived rate of every disk with an illuminated, possibly visible surface.</summary>
    public double SourceElectronRate { get; private set; }

    /// <summary>Rate of the samples the horizon policy and projection admit, before the PSF and before clipping.</summary>
    public double VisibleElectronRate { get; private set; }

    /// <summary>Rate that lands inside the sensor and aperture after the PSF. Lost light is never renormalized.</summary>
    public double RetainedElectronRate { get; }

    public long KernelCellVisits { get; private set; }

    public double ElectronRate(int x, int y) => x < 0 || x >= Projection.WidthPixels || y < 0 || y >= Projection.HeightPixels
        ? 0 : _rates.GetValueOrDefault(y * Projection.WidthPixels + x);

    /// <summary>
    /// The point-source PSF, sampled at integer pixel offsets inside its circular cutoff and normalized to
    /// unit sum, so convolution conserves every deposited electron before the sensor and aperture clip.
    /// </summary>
    internal static (int Dx, int Dy, double Weight)[] CreateKernel(double sigma, double cutoff)
    {
        var reach = (int)Math.Floor(cutoff);
        var cells = new List<(int, int, double)>();
        var total = 0d;
        for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                var distanceSquared = (double)dx * dx + dy * dy;
                if (distanceSquared > cutoff * cutoff) continue;
                var weight = Math.Exp(-distanceSquared / (2 * sigma * sigma));
                cells.Add((dx, dy, weight));
                total += weight;
            }
        return cells.Select(cell => (cell.Item1, cell.Item2, cell.Item3 / total)).ToArray();
    }

    private void Add(int x, int y, double rate)
    {
        if (x < 0 || x >= Projection.WidthPixels || y < 0 || y >= Projection.HeightPixels ||
            !Projection.ContainsSample(x + .5, y + .5) || rate == 0) return;
        var index = y * Projection.WidthPixels + x;
        _rates[index] = _rates.GetValueOrDefault(index) + rate;
        if (_rates.Count > MaximumSparsePixels) throw new InvalidOperationException("Solar disk sparse pixel budget exceeded.");
    }
}
