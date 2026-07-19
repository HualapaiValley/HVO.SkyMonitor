using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Integrated optical and sensor-stage electrons for one sensor sample.</summary>
public readonly record struct VirtualTransientPixelSignal(
    double RedSkyElectrons,
    double GreenSkyElectrons,
    double BlueSkyElectrons,
    double SensorElectrons)
{
    internal double SkyElectrons(int channel) => channel switch
    {
        0 => RedSkyElectrons,
        1 => GreenSkyElectrons,
        2 => BlueSkyElectrons,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    internal double MonochromeSkyElectrons =>
        (RedSkyElectrons + GreenSkyElectrons + BlueSkyElectrons) / 3;
}

/// <summary>Measured raster support for one generic scenario primitive.</summary>
public sealed record VirtualTransientSignalGeometry(
    string PrimitiveId,
    bool SensorStage,
    int TemporalSamples,
    double ExpectedElectrons,
    double DepositedElectrons,
    PixelPoint? DepositedCentroid,
    int MinimumX,
    int MinimumY,
    int MaximumX,
    int MaximumY);

/// <summary>Sparse integrated transient signal for one exposure.</summary>
public sealed class VirtualTransientFrameSignal
{
    internal VirtualTransientFrameSignal(
        IReadOnlyDictionary<int, VirtualTransientPixelSignal> pixels,
        IReadOnlyList<VirtualTransientSignalGeometry> geometry)
    {
        Pixels = pixels;
        Geometry = geometry;
    }

    public IReadOnlyDictionary<int, VirtualTransientPixelSignal> Pixels { get; }
    public IReadOnlyList<VirtualTransientSignalGeometry> Geometry { get; }
    public int ActivePixelCount => Pixels.Count;

    internal bool TryGet(int index, out VirtualTransientPixelSignal signal) => Pixels.TryGetValue(index, out signal);
}

/// <summary>Deterministically integrates and rasterizes generic sky and sensor transient primitives.</summary>
public static class VirtualTransientSignalRenderer
{
    public static VirtualTransientFrameSignal Render(
        VisibleScene scene,
        ImageLayout layout,
        VirtualTransientRenderContext context,
        double magnitudeZeroElectronsPerSecond,
        double minimumPsfSigmaPixels,
        double minimumPsfRadiusPixels,
        VirtualCloudRenderContext? cloud = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(context);
        layout.Validate();
        context.Validate();
        cloud?.Validate();
        if (cloud is not null &&
            (cloud.IntegrationStartUtc != context.IntegrationStartUtc ||
             cloud.IntegrationDuration != context.IntegrationDuration))
        {
            throw new ArgumentException("Cloud and transient intervals must match.", nameof(cloud));
        }
        if (!double.IsFinite(magnitudeZeroElectronsPerSecond) || magnitudeZeroElectronsPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(magnitudeZeroElectronsPerSecond));
        }
        if (!double.IsFinite(minimumPsfSigmaPixels) || minimumPsfSigmaPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPsfSigmaPixels));
        }
        if (!double.IsFinite(minimumPsfRadiusPixels) || minimumPsfRadiusPixels <= 0 || minimumPsfRadiusPixels > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPsfRadiusPixels));
        }
        if (scene.Request.Projection.WidthPixels != layout.Width ||
            scene.Request.Projection.HeightPixels != layout.Height)
        {
            throw new ArgumentException("Scene projection dimensions must match the image layout.", nameof(layout));
        }

        var definition = context.Scenario.Definition;
        definition.ValidateSensorBounds(layout.Width, layout.Height);
        var pixels = new Dictionary<int, VirtualTransientPixelSignal>();
        var geometry = new List<VirtualTransientSignalGeometry>(
            definition.SkyTracks.Count + definition.SensorTracks.Count);
        var projector = ProjectorFactory.Create(scene.Request.Projection);
        foreach (var track in definition.SkyTracks)
        {
            geometry.Add(RenderSkyTrack(
                track, definition, context, cloud, scene.Request.Projection, projector, layout, pixels,
                magnitudeZeroElectronsPerSecond, minimumPsfSigmaPixels, minimumPsfRadiusPixels));
        }
        foreach (var track in definition.SensorTracks)
        {
            geometry.Add(RenderSensorTrack(track, definition, context, layout, pixels));
        }
        return new VirtualTransientFrameSignal(pixels, geometry);
    }

    private static VirtualTransientSignalGeometry RenderSkyTrack(
        VirtualTransientSkyTrack track,
        VirtualTransientScenarioDefinition definition,
        VirtualTransientRenderContext context,
        VirtualCloudRenderContext? cloud,
        ProjectionContext projection,
        IImageProjector projector,
        ImageLayout layout,
        Dictionary<int, VirtualTransientPixelSignal> pixels,
        double magnitudeZeroElectronsPerSecond,
        double minimumPsfSigmaPixels,
        double minimumPsfRadiusPixels)
    {
        var overlap = ResolveOverlap(track.Keyframes[0].OffsetSeconds, track.Keyframes[^1].OffsetSeconds,
            definition.EpochUtc, context);
        if (overlap is null)
        {
            return EmptyGeometry(track.PrimitiveId, sensorStage: false);
        }

        var accumulator = new GeometryAccumulator(track.PrimitiveId, sensorStage: false, layout.Width, layout.Height);
        var sampleSeconds = overlap.Value.Duration.TotalSeconds / definition.TemporalSampleCount;
        for (var sample = 0; sample < definition.TemporalSampleCount; sample++)
        {
            var utc = Midpoint(overlap.Value.StartUtc, overlap.Value.Duration, sample, definition.TemporalSampleCount);
            var state = ResolveSkyState(track.Keyframes, (utc - definition.EpochUtc).TotalSeconds);
            var pixel = projector.Project(state.Direction);
            if (pixel is null)
            {
                accumulator.AddSample(0, 0, null);
                continue;
            }

            var transmission = cloud?.Field.Evaluate(state.Direction, utc).Transmission ?? 1;
            var baseElectrons = Mono16SceneRenderer.RelativeFlux(state.Magnitude) *
                magnitudeZeroElectronsPerSecond * sampleSeconds * transmission;
            var expected = baseElectrons * (state.RedWeight + state.GreenWeight + state.BlueWeight) / 3;
            var sigma = Math.Max(minimumPsfSigmaPixels,
                ResolveProjectedSigma(projector, state.Direction, pixel.Value, state.AngularWidthDegrees));
            var radius = Math.Min(64, Math.Max(minimumPsfRadiusPixels, sigma * 4));
            if (!double.IsFinite(baseElectrons) || !double.IsFinite(expected) || !double.IsFinite(sigma) ||
                !double.IsFinite(radius))
            {
                throw new ArgumentOutOfRangeException(nameof(magnitudeZeroElectronsPerSecond),
                    "Transient signal scaling must remain finite.");
            }
            var deposited = DepositGaussian(
                pixels, layout, projection, pixel.Value, sigma, radius,
                new VirtualTransientPixelSignal(
                    baseElectrons * state.RedWeight,
                    baseElectrons * state.GreenWeight,
                    baseElectrons * state.BlueWeight,
                    0),
                sensorStage: false,
                out var centroid,
                out var bounds);
            accumulator.AddSample(expected, deposited, centroid, bounds);
        }
        return accumulator.Create();
    }

    private static VirtualTransientSignalGeometry RenderSensorTrack(
        VirtualTransientSensorTrack track,
        VirtualTransientScenarioDefinition definition,
        VirtualTransientRenderContext context,
        ImageLayout layout,
        Dictionary<int, VirtualTransientPixelSignal> pixels)
    {
        var overlap = ResolveOverlap(track.Keyframes[0].OffsetSeconds, track.Keyframes[^1].OffsetSeconds,
            definition.EpochUtc, context);
        if (overlap is null)
        {
            return EmptyGeometry(track.PrimitiveId, sensorStage: true);
        }

        var accumulator = new GeometryAccumulator(track.PrimitiveId, sensorStage: true, layout.Width, layout.Height);
        var sampleSeconds = overlap.Value.Duration.TotalSeconds / definition.TemporalSampleCount;
        for (var sample = 0; sample < definition.TemporalSampleCount; sample++)
        {
            var utc = Midpoint(overlap.Value.StartUtc, overlap.Value.Duration, sample, definition.TemporalSampleCount);
            var state = ResolveSensorState(track.Keyframes, (utc - definition.EpochUtc).TotalSeconds);
            var electrons = state.ElectronsPerSecond * sampleSeconds;
            if (!double.IsFinite(electrons))
            {
                throw new ArgumentOutOfRangeException(nameof(context), "Transient sensor charge must remain finite.");
            }
            var deposited = DepositGaussian(
                pixels, layout, default, new PixelPoint(state.PixelX, state.PixelY),
                state.SigmaPixels, Math.Min(64, Math.Max(1, state.SigmaPixels * 4)),
                new VirtualTransientPixelSignal(0, 0, 0, electrons),
                sensorStage: true,
                out var centroid,
                out var bounds);
            accumulator.AddSample(electrons, deposited, centroid, bounds);
        }
        return accumulator.Create();
    }

    private static double DepositGaussian(
        Dictionary<int, VirtualTransientPixelSignal> pixels,
        ImageLayout layout,
        ProjectionContext projection,
        PixelPoint center,
        double sigma,
        double radius,
        VirtualTransientPixelSignal signal,
        bool sensorStage,
        out PixelPoint? centroid,
        out (int MinimumX, int MinimumY, int MaximumX, int MaximumY) bounds)
    {
        var minimumX = (int)Math.Ceiling(center.X - radius - 0.5);
        var maximumX = (int)Math.Floor(center.X + radius - 0.5);
        var minimumY = (int)Math.Ceiling(center.Y - radius - 0.5);
        var maximumY = (int)Math.Floor(center.Y + radius - 0.5);
        var radiusSquared = radius * radius;
        var denominator = 0d;
        for (var y = minimumY; y <= maximumY; y++)
        {
            for (var x = minimumX; x <= maximumX; x++)
            {
                var distanceSquared = Square(x + 0.5 - center.X) + Square(y + 0.5 - center.Y);
                if (distanceSquared <= radiusSquared)
                {
                    denominator += Math.Exp(-distanceSquared / (2 * sigma * sigma));
                }
            }
        }

        var deposited = 0d;
        var weightedX = 0d;
        var weightedY = 0d;
        var boundedMinimumX = layout.Width;
        var boundedMinimumY = layout.Height;
        var boundedMaximumX = -1;
        var boundedMaximumY = -1;
        for (var y = Math.Max(0, minimumY); y <= Math.Min(layout.Height - 1, maximumY); y++)
        {
            for (var x = Math.Max(0, minimumX); x <= Math.Min(layout.Width - 1, maximumX); x++)
            {
                var distanceSquared = Square(x + 0.5 - center.X) + Square(y + 0.5 - center.Y);
                if (distanceSquared > radiusSquared || !sensorStage && !projection.ContainsSample(x + 0.5, y + 0.5))
                {
                    continue;
                }

                var weight = Math.Exp(-distanceSquared / (2 * sigma * sigma)) / denominator;
                var value = Scale(signal, weight);
                var index = y * layout.Width + x;
                pixels[index] = pixels.TryGetValue(index, out var current) ? Add(current, value) : value;
                var scalar = AverageElectrons(value);
                deposited += scalar;
                weightedX += scalar * (x + 0.5);
                weightedY += scalar * (y + 0.5);
                boundedMinimumX = Math.Min(boundedMinimumX, x);
                boundedMinimumY = Math.Min(boundedMinimumY, y);
                boundedMaximumX = Math.Max(boundedMaximumX, x);
                boundedMaximumY = Math.Max(boundedMaximumY, y);
            }
        }
        centroid = deposited > 0 ? new PixelPoint(weightedX / deposited, weightedY / deposited) : null;
        bounds = (boundedMinimumX, boundedMinimumY, boundedMaximumX, boundedMaximumY);
        return deposited;
    }

    private static (DateTimeOffset StartUtc, TimeSpan Duration)? ResolveOverlap(
        double firstOffsetSeconds,
        double lastOffsetSeconds,
        DateTimeOffset epochUtc,
        VirtualTransientRenderContext context)
    {
        var primitiveStart = epochUtc + TimeSpan.FromSeconds(firstOffsetSeconds);
        var primitiveEnd = epochUtc + TimeSpan.FromSeconds(lastOffsetSeconds);
        var exposureEnd = context.IntegrationStartUtc + context.IntegrationDuration;
        var start = primitiveStart > context.IntegrationStartUtc ? primitiveStart : context.IntegrationStartUtc;
        var end = primitiveEnd < exposureEnd ? primitiveEnd : exposureEnd;
        return end <= start ? null : (start, end - start);
    }

    private static DateTimeOffset Midpoint(DateTimeOffset start, TimeSpan duration, int sample, int sampleCount)
        => start.AddTicks(checked((long)(duration.Ticks * ((sample + 0.5) / sampleCount))));

    private static SkyState ResolveSkyState(IReadOnlyList<VirtualTransientSkyKeyframe> keyframes, double offset)
    {
        var (lower, upper, fraction) = ResolvePair(keyframes, offset, static item => item.OffsetSeconds);
        var from = CameraBasis.FromHorizontal(new AltAzPoint(lower.AltitudeDegrees, lower.AzimuthDegrees));
        var to = CameraBasis.FromHorizontal(new AltAzPoint(upper.AltitudeDegrees, upper.AzimuthDegrees));
        var direction = CameraBasis.ToHorizontal(EnuVector.SphericalInterpolate(from, to, fraction));
        return new SkyState(
            direction,
            Lerp(lower.Magnitude, upper.Magnitude, fraction),
            Lerp(lower.AngularWidthDegrees, upper.AngularWidthDegrees, fraction),
            Lerp(lower.RedWeight, upper.RedWeight, fraction),
            Lerp(lower.GreenWeight, upper.GreenWeight, fraction),
            Lerp(lower.BlueWeight, upper.BlueWeight, fraction));
    }

    private static SensorState ResolveSensorState(
        IReadOnlyList<VirtualTransientSensorKeyframe> keyframes,
        double offset)
    {
        var (lower, upper, fraction) = ResolvePair(keyframes, offset, static item => item.OffsetSeconds);
        return new SensorState(
            Lerp(lower.PixelX, upper.PixelX, fraction),
            Lerp(lower.PixelY, upper.PixelY, fraction),
            Lerp(lower.ElectronsPerSecond, upper.ElectronsPerSecond, fraction),
            Lerp(lower.SigmaPixels, upper.SigmaPixels, fraction));
    }

    private static (T Lower, T Upper, double Fraction) ResolvePair<T>(
        IReadOnlyList<T> keyframes,
        double offset,
        Func<T, double> getOffset)
    {
        for (var index = 1; index < keyframes.Count; index++)
        {
            var upper = keyframes[index];
            var upperOffset = getOffset(upper);
            if (offset <= upperOffset)
            {
                var lower = keyframes[index - 1];
                var lowerOffset = getOffset(lower);
                return (lower, upper, Math.Clamp((offset - lowerOffset) / (upperOffset - lowerOffset), 0, 1));
            }
        }
        return (keyframes[^2], keyframes[^1], 1);
    }

    private static VirtualTransientSignalGeometry EmptyGeometry(string id, bool sensorStage)
        => new(id, sensorStage, 0, 0, 0, null, 0, 0, -1, -1);

    private static VirtualTransientPixelSignal Add(
        VirtualTransientPixelSignal left,
        VirtualTransientPixelSignal right)
        => new(
            left.RedSkyElectrons + right.RedSkyElectrons,
            left.GreenSkyElectrons + right.GreenSkyElectrons,
            left.BlueSkyElectrons + right.BlueSkyElectrons,
            left.SensorElectrons + right.SensorElectrons);

    private static VirtualTransientPixelSignal Scale(VirtualTransientPixelSignal value, double scale)
        => new(
            value.RedSkyElectrons * scale,
            value.GreenSkyElectrons * scale,
            value.BlueSkyElectrons * scale,
            value.SensorElectrons * scale);

    private static double AverageElectrons(VirtualTransientPixelSignal value)
        => value.SensorElectrons + (value.RedSkyElectrons + value.GreenSkyElectrons + value.BlueSkyElectrons) / 3;

    private static double Lerp(double lower, double upper, double fraction) => lower + (upper - lower) * fraction;
    private static double Square(double value) => value * value;

    private static double ResolveProjectedSigma(
        IImageProjector projector,
        AltAzPoint direction,
        PixelPoint center,
        double angularWidthDegrees)
    {
        var source = CameraBasis.FromHorizontal(direction);
        var reference = Math.Abs(source.Up) < 0.9
            ? new EnuVector(0, 0, 1)
            : new EnuVector(0, 1, 0);
        var tangent = EnuVector.Cross(reference, source).Normalize();
        var angle = angularWidthDegrees * Math.PI / 180;
        var offsetDirection = (source * Math.Cos(angle) + tangent * Math.Sin(angle)).Normalize();
        var offsetPixel = projector.Project(CameraBasis.ToHorizontal(offsetDirection));
        if (offsetPixel is null)
        {
            return 0;
        }
        var projectedWidth = Math.Sqrt(Square(offsetPixel.Value.X - center.X) + Square(offsetPixel.Value.Y - center.Y));
        return projectedWidth / 2.355;
    }

    private readonly record struct SkyState(
        AltAzPoint Direction,
        double Magnitude,
        double AngularWidthDegrees,
        double RedWeight,
        double GreenWeight,
        double BlueWeight);

    private readonly record struct SensorState(
        double PixelX,
        double PixelY,
        double ElectronsPerSecond,
        double SigmaPixels);

    private sealed class GeometryAccumulator(string primitiveId, bool sensorStage, int width, int height)
    {
        private int _sampleCount;
        private double _expected;
        private double _deposited;
        private double _weightedX;
        private double _weightedY;
        private int _minimumX = width;
        private int _minimumY = height;
        private int _maximumX = -1;
        private int _maximumY = -1;

        public void AddSample(
            double expected,
            double deposited,
            PixelPoint? centroid,
            (int MinimumX, int MinimumY, int MaximumX, int MaximumY)? bounds = null)
        {
            _sampleCount++;
            _expected += expected;
            _deposited += deposited;
            if (centroid is { } value)
            {
                _weightedX += value.X * deposited;
                _weightedY += value.Y * deposited;
            }
            if (bounds is { MaximumX: >= 0, MaximumY: >= 0 } valueBounds)
            {
                _minimumX = Math.Min(_minimumX, valueBounds.MinimumX);
                _minimumY = Math.Min(_minimumY, valueBounds.MinimumY);
                _maximumX = Math.Max(_maximumX, valueBounds.MaximumX);
                _maximumY = Math.Max(_maximumY, valueBounds.MaximumY);
            }
        }

        public VirtualTransientSignalGeometry Create()
            => new(
                primitiveId,
                sensorStage,
                _sampleCount,
                _expected,
                _deposited,
                _deposited > 0 ? new PixelPoint(_weightedX / _deposited, _weightedY / _deposited) : null,
                _maximumX < 0 ? 0 : _minimumX,
                _maximumY < 0 ? 0 : _minimumY,
                _maximumX,
                _maximumY);
    }
}
