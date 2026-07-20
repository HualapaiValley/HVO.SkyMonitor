using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

public sealed record Linear16TransientExtractionOptions(
    ushort MinimumResidualAdu,
    int MinimumComponentPixels,
    long MinimumIntegratedSignalAdu,
    int MaximumCandidates,
    int ProfileSampleCount,
    int MaximumSaturationBridgePixels,
    int MaximumForegroundPixels,
    double MaximumFragmentGapPixels,
    double MinimumFragmentAlignmentCosine);

public readonly record struct Linear16TransientProfileSample(
    int PositionMillionths,
    double Value);

/// <summary>One deterministic residual component in detector pixel-edge coordinates.</summary>
public sealed record Linear16TransientComponent(
    int FirstPixelIndex,
    double BoundsX,
    double BoundsY,
    double BoundsWidth,
    double BoundsHeight,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double LengthPixels,
    double MeanWidthPixels,
    double MaximumWidthPixels,
    long IntegratedSignalAdu,
    ushort PeakSignalAdu,
    int SaturatedSampleCount,
    int FragmentCount,
    IReadOnlyList<Linear16TransientProfileSample> WidthProfile,
    IReadOnlyList<Linear16TransientProfileSample> BrightnessProfile);

public sealed record Linear16TransientExtractionResult(
    IReadOnlyList<Linear16TransientComponent> Components,
    int ForegroundPixelCount,
    int HardMaskedPixelCount,
    int SaturatedPixelCount,
    bool CandidateLimitExceeded,
    Linear16TransientExtractionLimit? Limit,
    long BytesScanned,
    string AlgorithmVersion);

public enum Linear16TransientExtractionLimit
{
    ForegroundPixels,
    RawComponents,
    Candidates
}

/// <summary>Extracts bounded positive residual components without retaining a full residual frame.</summary>
public static class Linear16TransientExtraction
{
    public const string AlgorithmVersion = "linear16-transient-components-pca-v1";
    public const int MaximumDetectorPixels = 10_000_000;

    private const byte Foreground = 1;
    private const byte Saturated = 2;
    private const byte SaturationBridge = 4;
    private const byte Visited = 8;
    private const byte SaturationVisited = 16;

    /// <summary>Preserves persistent/no-support exclusions while removing saturation-only exclusion.</summary>
    public static Linear16PixelMask CreateHardExclusionMask(
        Linear16PixelMask effectiveMask,
        Linear16PixelMask saturationMask,
        Linear16PixelMask noSupportMask,
        IReadOnlyList<Linear16PixelMask> persistentMasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effectiveMask);
        ArgumentNullException.ThrowIfNull(saturationMask);
        ArgumentNullException.ThrowIfNull(noSupportMask);
        ArgumentNullException.ThrowIfNull(persistentMasks);
        cancellationToken.ThrowIfCancellationRequested();
        Linear16MaskOperations.Validate(effectiveMask, nameof(effectiveMask));
        Linear16MaskOperations.Validate(saturationMask, nameof(saturationMask));
        Linear16MaskOperations.Validate(noSupportMask, nameof(noSupportMask));
        if (effectiveMask.Width != saturationMask.Width || effectiveMask.Height != saturationMask.Height ||
            effectiveMask.Width != noSupportMask.Width || effectiveMask.Height != noSupportMask.Height ||
            persistentMasks.Count == 0)
        {
            throw new ArgumentException("Effective, saturation, and persistent masks must be compatible.");
        }
        var persistent = Linear16MaskOperations.Combine(persistentMasks, cancellationToken);
        if (persistent.Width != effectiveMask.Width || persistent.Height != effectiveMask.Height)
        {
            throw new ArgumentException("Persistent masks must match the effective mask.", nameof(persistentMasks));
        }
        var output = new byte[effectiveMask.Bits.Length];
        var effectiveBits = effectiveMask.Bits.Span;
        var saturationBits = saturationMask.Bits.Span;
        var noSupportBits = noSupportMask.Bits.Span;
        var persistentBits = persistent.Bits.Span;
        for (var index = 0; index < output.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            output[index] = (byte)(persistentBits[index] | noSupportBits[index] |
                (effectiveBits[index] & ~saturationBits[index]));
        }
        return new Linear16PixelMask(effectiveMask.Width, effectiveMask.Height, output);
    }

    public static Linear16TransientExtractionResult Extract(
        Linear16Frame target,
        Linear16Frame background,
        Linear16PixelMask hardExclusionMask,
        Linear16PixelMask saturationMask,
        Linear16TransientExtractionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(hardExclusionMask);
        ArgumentNullException.ThrowIfNull(saturationMask);
        ArgumentNullException.ThrowIfNull(options);
        Validate(target, background, hardExclusionMask, saturationMask, options);
        cancellationToken.ThrowIfCancellationRequested();

        var width = target.Width;
        var height = target.Height;
        var pixelCount = checked(width * height);
        var state = new byte[pixelCount];
        var queue = new int[pixelCount];
        var targetPixels = target.PixelData.Span;
        var backgroundPixels = background.PixelData.Span;
        var hardBits = hardExclusionMask.Bits.Span;
        var saturationBits = saturationMask.Bits.Span;
        var foregroundPixels = 0;
        var hardMaskedPixels = 0;
        var saturatedPixels = 0;

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetRow = y * target.StrideBytes;
            var backgroundRow = y * background.StrideBytes;
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (Contains(hardBits, index))
                {
                    hardMaskedPixels++;
                    continue;
                }
                if (Contains(saturationBits, index))
                {
                    state[index] = Saturated;
                    saturatedPixels++;
                    continue;
                }

                var targetValue = Read(targetPixels, targetRow + x * 2);
                var backgroundValue = Read(backgroundPixels, backgroundRow + x * 2);
                var residual = targetValue > backgroundValue ? targetValue - backgroundValue : 0;
                if (residual >= options.MinimumResidualAdu)
                {
                    state[index] = Foreground;
                    foregroundPixels++;
                    if (foregroundPixels > options.MaximumForegroundPixels)
                    {
                        return LimitExceeded(
                            foregroundPixels,
                            hardMaskedPixels,
                            saturatedPixels,
                            target,
                            background,
                            hardExclusionMask,
                            saturationMask,
                            Linear16TransientExtractionLimit.ForegroundPixels);
                    }
                }
            }
        }

        MarkBoundedSaturationBridges(
            state,
            queue,
            width,
            height,
            options.MaximumSaturationBridgePixels,
            cancellationToken);

        var preliminary = new List<RawComponent>();
        var rawComponentLimitExceeded = false;
        for (var seed = 0; seed < pixelCount; seed++)
        {
            if ((seed & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if ((state[seed] & Foreground) == 0 || (state[seed] & Visited) != 0)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var count = CollectComponent(state, queue, seed, width, height, cancellationToken);
            var supportCount = 0;
            long integratedSignal = 0;
            for (var index = 0; index < count; index++)
            {
                var pixel = queue[index];
                if ((state[pixel] & Foreground) == 0)
                {
                    continue;
                }
                supportCount++;
                integratedSignal = checked(integratedSignal + Residual(target, background, pixel));
            }
            if (supportCount < options.MinimumComponentPixels ||
                integratedSignal < options.MinimumIntegratedSignalAdu)
            {
                continue;
            }
            if (preliminary.Count == checked(options.MaximumCandidates * 16))
            {
                rawComponentLimitExceeded = true;
                continue;
            }
            var componentPixels = queue.AsSpan(0, count).ToArray();
            preliminary.Add(new RawComponent(componentPixels, CreateComponent(
                state,
                target,
                background,
                componentPixels,
                width,
                options.ProfileSampleCount,
                cancellationToken)));
        }

        if (rawComponentLimitExceeded)
        {
            return LimitExceeded(
                foregroundPixels,
                hardMaskedPixels,
                saturatedPixels,
                target,
                background,
                hardExclusionMask,
                saturationMask,
                Linear16TransientExtractionLimit.RawComponents);
        }

        var grouped = GroupFragments(
            preliminary,
            state,
            target,
            background,
            width,
            options.ProfileSampleCount,
            options.MaximumFragmentGapPixels,
            options.MinimumFragmentAlignmentCosine,
            cancellationToken);
        var candidateLimitExceeded = grouped.Length > options.MaximumCandidates;
        var components = candidateLimitExceeded ? [] : grouped;

        return new Linear16TransientExtractionResult(
            components,
            foregroundPixels,
            hardMaskedPixels,
            saturatedPixels,
            candidateLimitExceeded,
            candidateLimitExceeded ? Linear16TransientExtractionLimit.Candidates : null,
            checked(target.PixelData.Length + background.PixelData.Length +
                hardExclusionMask.Bits.Length + saturationMask.Bits.Length),
            AlgorithmVersion);
    }

    private static Linear16TransientExtractionResult LimitExceeded(
        int foregroundPixels,
        int hardMaskedPixels,
        int saturatedPixels,
        Linear16Frame target,
        Linear16Frame background,
        Linear16PixelMask hardExclusionMask,
        Linear16PixelMask saturationMask,
        Linear16TransientExtractionLimit limit)
        => new(
            [],
            foregroundPixels,
            hardMaskedPixels,
            saturatedPixels,
            true,
            limit,
            checked(target.PixelData.Length + background.PixelData.Length +
                hardExclusionMask.Bits.Length + saturationMask.Bits.Length),
            AlgorithmVersion);

    private static Linear16TransientComponent[] GroupFragments(
        List<RawComponent> components,
        byte[] state,
        Linear16Frame target,
        Linear16Frame background,
        int width,
        int profileSampleCount,
        double maximumGap,
        double minimumAlignmentCosine,
        CancellationToken cancellationToken)
    {
        if (components.Count < 2 || maximumGap == 0)
        {
            return components.Select(static value => value.Component).ToArray();
        }
        var parent = Enumerable.Range(0, components.Count).ToArray();
        for (var first = 0; first < components.Count; first++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var second = first + 1; second < components.Count; second++)
            {
                if (CanGroup(
                    components[first].Component,
                    components[second].Component,
                    maximumGap,
                    minimumAlignmentCosine))
                {
                    Union(parent, first, second);
                }
            }
        }
        return components
            .Select((component, index) => (Root: Find(parent, index), Component: component))
            .GroupBy(static value => value.Root)
            .Select(group =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pixels = group.SelectMany(static value => value.Component.Pixels).ToArray();
                return CreateComponent(
                    state,
                    target,
                    background,
                    pixels,
                    width,
                    profileSampleCount,
                    cancellationToken);
            })
            .OrderBy(static component => component.FirstPixelIndex)
            .ToArray();
    }

    private static bool CanGroup(
        Linear16TransientComponent first,
        Linear16TransientComponent second,
        double maximumGap,
        double minimumAlignmentCosine)
    {
        var gapX = Math.Max(0, Math.Max(
            second.BoundsX - (first.BoundsX + first.BoundsWidth),
            first.BoundsX - (second.BoundsX + second.BoundsWidth)));
        var gapY = Math.Max(0, Math.Max(
            second.BoundsY - (first.BoundsY + first.BoundsHeight),
            first.BoundsY - (second.BoundsY + second.BoundsHeight)));
        if (Math.Sqrt(gapX * gapX + gapY * gapY) > maximumGap)
        {
            return false;
        }
        var firstAxis = Axis(first);
        var secondAxis = Axis(second);
        if (Math.Abs(firstAxis.X * secondAxis.X + firstAxis.Y * secondAxis.Y) < minimumAlignmentCosine)
        {
            return false;
        }
        var firstCenter = (X: first.BoundsX + first.BoundsWidth / 2, Y: first.BoundsY + first.BoundsHeight / 2);
        var secondCenter = (X: second.BoundsX + second.BoundsWidth / 2, Y: second.BoundsY + second.BoundsHeight / 2);
        var deltaX = secondCenter.X - firstCenter.X;
        var deltaY = secondCenter.Y - firstCenter.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        return distance <= double.Epsilon ||
            Math.Abs((deltaX * firstAxis.X + deltaY * firstAxis.Y) / distance) >= minimumAlignmentCosine &&
            Math.Abs((deltaX * secondAxis.X + deltaY * secondAxis.Y) / distance) >= minimumAlignmentCosine;
    }

    private static (double X, double Y) Axis(Linear16TransientComponent component)
    {
        var x = component.EndX - component.StartX;
        var y = component.EndY - component.StartY;
        var length = Math.Sqrt(x * x + y * y);
        return length <= double.Epsilon ? (1, 0) : (x / length, y / length);
    }

    private static void Union(Span<int> parent, int first, int second)
    {
        var firstRoot = Find(parent, first);
        var secondRoot = Find(parent, second);
        if (firstRoot != secondRoot)
        {
            parent[Math.Max(firstRoot, secondRoot)] = Math.Min(firstRoot, secondRoot);
        }
    }

    private static int Find(Span<int> parent, int value)
    {
        while (parent[value] != value)
        {
            parent[value] = parent[parent[value]];
            value = parent[value];
        }
        return value;
    }

    private static void MarkBoundedSaturationBridges(
        byte[] state,
        int[] queue,
        int width,
        int height,
        int maximumPixels,
        CancellationToken cancellationToken)
    {
        for (var seed = 0; seed < state.Length; seed++)
        {
            if ((state[seed] & Saturated) == 0 || (state[seed] & SaturationVisited) != 0)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var read = 0;
            var write = 1;
            var adjacentToForeground = false;
            queue[0] = seed;
            state[seed] |= SaturationVisited;
            while (read < write)
            {
                if ((read & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var pixel = queue[read++];
                var x = pixel % width;
                var y = pixel / width;
                for (var neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(height - 1, y + 1); neighborY++)
                {
                    for (var neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(width - 1, x + 1); neighborX++)
                    {
                        if (neighborX == x && neighborY == y)
                        {
                            continue;
                        }
                        var neighbor = neighborY * width + neighborX;
                        adjacentToForeground |= (state[neighbor] & Foreground) != 0;
                        if ((state[neighbor] & Saturated) != 0 && (state[neighbor] & SaturationVisited) == 0)
                        {
                            state[neighbor] |= SaturationVisited;
                            queue[write++] = neighbor;
                        }
                    }
                }
            }
            if (adjacentToForeground && write <= maximumPixels)
            {
                for (var index = 0; index < write; index++)
                {
                    state[queue[index]] |= SaturationBridge;
                }
            }
        }
    }

    private static int CollectComponent(
        byte[] state,
        int[] queue,
        int seed,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var read = 0;
        var write = 1;
        queue[0] = seed;
        state[seed] |= Visited;
        while (read < write)
        {
            if ((read & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var pixel = queue[read++];
            var x = pixel % width;
            var y = pixel / width;
            for (var neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(height - 1, y + 1); neighborY++)
            {
                for (var neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(width - 1, x + 1); neighborX++)
                {
                    if (neighborX == x && neighborY == y)
                    {
                        continue;
                    }
                    var neighbor = neighborY * width + neighborX;
                    if ((state[neighbor] & (Foreground | SaturationBridge)) != 0 &&
                        (state[neighbor] & Visited) == 0)
                    {
                        state[neighbor] |= Visited;
                        queue[write++] = neighbor;
                    }
                }
            }
        }
        return write;
    }

    private static Linear16TransientComponent CreateComponent(
        byte[] state,
        Linear16Frame target,
        Linear16Frame background,
        ReadOnlySpan<int> pixels,
        int width,
        int profileSampleCount,
        CancellationToken cancellationToken)
    {
        var firstPixel = int.MaxValue;
        var minimumX = int.MaxValue;
        var minimumY = int.MaxValue;
        var maximumX = int.MinValue;
        var maximumY = int.MinValue;
        double weightedX = 0;
        double weightedY = 0;
        double weightTotal = 0;
        long integratedSignal = 0;
        ushort peakSignal = 0;
        var saturationCount = 0;
        var support = new List<int>(pixels.Length);
        foreach (var pixel in pixels)
        {
            if ((support.Count & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var x = pixel % width;
            var y = pixel / width;
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
            if ((state[pixel] & SaturationBridge) != 0)
            {
                saturationCount++;
            }
            if ((state[pixel] & Foreground) == 0)
            {
                continue;
            }
            firstPixel = Math.Min(firstPixel, pixel);
            support.Add(pixel);
            var weight = Residual(target, background, pixel);
            integratedSignal = checked(integratedSignal + weight);
            peakSignal = Math.Max(peakSignal, weight);
            weightTotal += weight;
            weightedX += (x + 0.5) * weight;
            weightedY += (y + 0.5) * weight;
        }

        var centerX = weightedX / weightTotal;
        var centerY = weightedY / weightTotal;
        double covarianceXX = 0;
        double covarianceXY = 0;
        double covarianceYY = 0;
        foreach (var pixel in support)
        {
            var x = pixel % width + 0.5 - centerX;
            var y = pixel / width + 0.5 - centerY;
            var weight = Residual(target, background, pixel);
            covarianceXX += weight * x * x;
            covarianceXY += weight * x * y;
            covarianceYY += weight * y * y;
        }
        var (axisX, axisY) = PrincipalAxis(covarianceXX, covarianceXY, covarianceYY);
        var perpendicularX = -axisY;
        var perpendicularY = axisX;
        var minimumProjection = double.PositiveInfinity;
        var maximumProjection = double.NegativeInfinity;
        var minimumPerpendicular = double.PositiveInfinity;
        var maximumPerpendicular = double.NegativeInfinity;
        foreach (var pixel in support)
        {
            var x = pixel % width + 0.5 - centerX;
            var y = pixel / width + 0.5 - centerY;
            var projection = x * axisX + y * axisY;
            var perpendicular = x * perpendicularX + y * perpendicularY;
            minimumProjection = Math.Min(minimumProjection, projection);
            maximumProjection = Math.Max(maximumProjection, projection);
            minimumPerpendicular = Math.Min(minimumPerpendicular, perpendicular);
            maximumPerpendicular = Math.Max(maximumPerpendicular, perpendicular);
        }
        var projectionRange = maximumProjection - minimumProjection;
        var length = projectionRange + 1;
        var maximumWidth = maximumPerpendicular - minimumPerpendicular + 1;
        var meanWidth = support.Count / length;
        var (widthProfile, brightnessProfile) = CreateProfiles(
            target,
            background,
            support,
            width,
            centerX,
            centerY,
            axisX,
            axisY,
            minimumProjection,
            projectionRange,
            profileSampleCount);

        return new Linear16TransientComponent(
            firstPixel,
            minimumX,
            minimumY,
            maximumX - minimumX + 1,
            maximumY - minimumY + 1,
            centerX + minimumProjection * axisX,
            centerY + minimumProjection * axisY,
            centerX + maximumProjection * axisX,
            centerY + maximumProjection * axisY,
            length,
            meanWidth,
            maximumWidth,
            integratedSignal,
            peakSignal,
            saturationCount,
            CountFragments(state, support, width, cancellationToken),
            widthProfile,
            brightnessProfile);
    }

    private static (IReadOnlyList<Linear16TransientProfileSample> Width,
        IReadOnlyList<Linear16TransientProfileSample> Brightness) CreateProfiles(
        Linear16Frame target,
        Linear16Frame background,
        IReadOnlyList<int> support,
        int width,
        double centerX,
        double centerY,
        double axisX,
        double axisY,
        double minimumProjection,
        double projectionRange,
        int sampleCount)
    {
        var brightness = new long[sampleCount];
        var minimumWidth = Enumerable.Repeat(double.PositiveInfinity, sampleCount).ToArray();
        var maximumWidth = Enumerable.Repeat(double.NegativeInfinity, sampleCount).ToArray();
        var perpendicularX = -axisY;
        var perpendicularY = axisX;
        foreach (var pixel in support)
        {
            var x = pixel % width + 0.5 - centerX;
            var y = pixel / width + 0.5 - centerY;
            var projection = x * axisX + y * axisY;
            var normalized = projectionRange <= 0 ? 0 : (projection - minimumProjection) / projectionRange;
            var bin = Math.Clamp((int)Math.Floor(normalized * sampleCount), 0, sampleCount - 1);
            var perpendicular = x * perpendicularX + y * perpendicularY;
            brightness[bin] = checked(brightness[bin] + Residual(target, background, pixel));
            minimumWidth[bin] = Math.Min(minimumWidth[bin], perpendicular);
            maximumWidth[bin] = Math.Max(maximumWidth[bin], perpendicular);
        }
        var widths = new Linear16TransientProfileSample[sampleCount];
        var brightnesses = new Linear16TransientProfileSample[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var position = (int)((long)index * 1_000_000 / (sampleCount - 1));
            var measuredWidth = double.IsPositiveInfinity(minimumWidth[index])
                ? 0
                : maximumWidth[index] - minimumWidth[index] + 1;
            widths[index] = new Linear16TransientProfileSample(position, measuredWidth);
            brightnesses[index] = new Linear16TransientProfileSample(position, brightness[index]);
        }
        return (widths, brightnesses);
    }

    private static int CountFragments(
        byte[] state,
        IReadOnlyList<int> support,
        int width,
        CancellationToken cancellationToken)
    {
        var remaining = support.ToHashSet();
        var fragments = 0;
        var queue = new Queue<int>();
        foreach (var seed in support)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!remaining.Remove(seed))
            {
                continue;
            }
            fragments++;
            queue.Enqueue(seed);
            while (queue.TryDequeue(out var pixel))
            {
                if ((queue.Count & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var x = pixel % width;
                var y = pixel / width;
                var height = state.Length / width;
                for (var neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(height - 1, y + 1); neighborY++)
                {
                    for (var neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(width - 1, x + 1); neighborX++)
                    {
                        if (neighborX == x && neighborY == y)
                        {
                            continue;
                        }
                        var neighbor = neighborY * width + neighborX;
                        if ((state[neighbor] & Foreground) != 0 && remaining.Remove(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }
        }
        return fragments;
    }

    private static (double X, double Y) PrincipalAxis(double xx, double xy, double yy)
    {
        if (Math.Abs(xy) < double.Epsilon && Math.Abs(xx - yy) < double.Epsilon)
        {
            return (1, 0);
        }
        var angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
        var x = Math.Cos(angle);
        var y = Math.Sin(angle);
        if (x < 0 || x == 0 && y < 0)
        {
            x = -x;
            y = -y;
        }
        return (x, y);
    }

    private static void Validate(
        Linear16Frame target,
        Linear16Frame background,
        Linear16PixelMask hardExclusionMask,
        Linear16PixelMask saturationMask,
        Linear16TransientExtractionOptions options)
    {
        ValidateFrame(target, nameof(target));
        ValidateFrame(background, nameof(background));
        Linear16MaskOperations.Validate(hardExclusionMask, nameof(hardExclusionMask));
        Linear16MaskOperations.Validate(saturationMask, nameof(saturationMask));
        if (target.Width != background.Width || target.Height != background.Height ||
            hardExclusionMask.Width != target.Width || hardExclusionMask.Height != target.Height ||
            saturationMask.Width != target.Width || saturationMask.Height != target.Height)
        {
            throw new ArgumentException("Frames and masks must have compatible detector dimensions.");
        }
        if (checked((long)target.Width * target.Height) > MaximumDetectorPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Detector dimensions exceed the extraction limit.");
        }
        if (options.MinimumResidualAdu == 0 || options.MinimumComponentPixels <= 0 ||
            options.MinimumIntegratedSignalAdu <= 0 || options.MaximumCandidates is <= 0 or > 4096 ||
            options.ProfileSampleCount is < 2 or > 64 || options.MaximumSaturationBridgePixels is <= 0 or > 1_000_000 ||
            options.MaximumForegroundPixels is <= 0 or > 10_000_000 || options.MaximumCandidates > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (!double.IsFinite(options.MaximumFragmentGapPixels) || options.MaximumFragmentGapPixels is < 0 or > 1024 ||
            options.MaximumFragmentGapPixels == 0 && BitConverter.DoubleToInt64Bits(options.MaximumFragmentGapPixels) < 0 ||
            !double.IsFinite(options.MinimumFragmentAlignmentCosine) ||
            options.MinimumFragmentAlignmentCosine is < 0 or > 1 ||
            options.MinimumFragmentAlignmentCosine == 0 && BitConverter.DoubleToInt64Bits(options.MinimumFragmentAlignmentCosine) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static void ValidateFrame(Linear16Frame frame, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(frame, parameterName);
        if (frame.PixelFormat != CameraPixelFormat.Mono16)
        {
            throw new ArgumentException("Transient extraction requires Mono16 detector pixels.", parameterName);
        }
        try
        {
            var layout = new ImageLayout(frame.Width, frame.Height, frame.PixelFormat, frame.StrideBytes);
            layout.Validate();
            if (frame.PixelData.Length != layout.RequiredByteLength)
            {
                throw new ArgumentException("Frame storage does not exactly match its layout.", parameterName);
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Frame layout is invalid.", parameterName, exception);
        }
    }

    private static bool Contains(ReadOnlySpan<byte> bits, int index)
        => (bits[index >> 3] & 1 << (index & 7)) != 0;

    private static ushort Read(ReadOnlySpan<byte> source, int offset)
        => (ushort)(source[offset] | source[offset + 1] << 8);

    private static ushort Residual(Linear16Frame target, Linear16Frame background, int pixel)
    {
        var x = pixel % target.Width;
        var y = pixel / target.Width;
        var targetValue = Read(target.PixelData.Span, y * target.StrideBytes + x * 2);
        var backgroundValue = Read(background.PixelData.Span, y * background.StrideBytes + x * 2);
        return targetValue > backgroundValue ? (ushort)(targetValue - backgroundValue) : (ushort)0;
    }

    private sealed record RawComponent(int[] Pixels, Linear16TransientComponent Component);
}
