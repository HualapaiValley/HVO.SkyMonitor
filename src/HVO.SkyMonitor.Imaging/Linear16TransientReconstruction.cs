using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A positive-size detector-space reconstruction region in pixel-edge coordinates.</summary>
public readonly record struct Linear16TransientReconstructionBounds(
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>One target, its frozen clean background, exclusion mask, and measured event bounds.</summary>
public sealed record Linear16TransientReconstructionObservation(
    Linear16Frame Target,
    Linear16Frame Background,
    Linear16PixelMask HardExclusionMask,
    Linear16TransientReconstructionBounds Bounds);

/// <summary>A packed reconstruction and LSB-first mask of pixels receiving positive event residual.</summary>
public sealed record Linear16TransientReconstructionResult(
    Linear16Frame Reconstruction,
    Linear16PixelMask EventMask,
    int ObservationCount,
    int EventPixelCount,
    int SaturatedOutputPixelCount,
    long InputByteFootprint,
    string AlgorithmVersion);

/// <summary>Reconstructs one still event without temporal interpolation or saturated-signal inference.</summary>
public static class Linear16TransientReconstruction
{
    public const string AlgorithmVersion = "linear16-transient-reconstruction-v1";
    public const int MaximumDetectorPixels = 16_000_000;

    public static Linear16TransientReconstructionResult Reconstruct(
        IReadOnlyList<Linear16TransientReconstructionObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        if (observations.Count is < 1 or > 2)
        {
            throw new ArgumentException("V1 reconstruction requires exactly one or two observations.", nameof(observations));
        }

        var first = observations[0] ?? throw new ArgumentException("Observations must not contain null entries.", nameof(observations));
        Validate(first, nameof(observations));
        var width = first.Target.Width;
        var height = first.Target.Height;
        var pixelCount = checked(width * height);
        if (pixelCount > MaximumDetectorPixels)
        {
            throw new ArgumentException("The detector exceeds the bounded V1 reconstruction size.", nameof(observations));
        }
        for (var index = 1; index < observations.Count; index++)
        {
            var observation = observations[index]
                ?? throw new ArgumentException("Observations must not contain null entries.", nameof(observations));
            Validate(observation, nameof(observations));
            if (observation.Target.Width != width || observation.Target.Height != height ||
                observation.Target.PixelFormat != first.Target.PixelFormat)
            {
                throw new ArgumentException("All reconstruction observations must use one detector layout.", nameof(observations));
            }
        }

        var output = new byte[checked(pixelCount * 2)];
        var eventMask = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                uint total = 0;
                for (var observationIndex = 0; observationIndex < observations.Count; observationIndex++)
                {
                    var observation = observations[observationIndex];
                    total += Read(observation.Background, x, y);
                }
                Write(output, y * width + x, (ushort)(total / observations.Count));
            }
        }

        var eventPixelCount = 0;
        var saturatedOutputPixelCount = 0;
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var minimumX = Math.Clamp((int)Math.Floor(observation.Bounds.X), 0, width);
            var maximumX = Math.Clamp((int)Math.Ceiling(observation.Bounds.X + observation.Bounds.Width), 0, width);
            var minimumY = Math.Clamp((int)Math.Floor(observation.Bounds.Y), 0, height);
            var maximumY = Math.Clamp((int)Math.Ceiling(observation.Bounds.Y + observation.Bounds.Height), 0, height);
            for (var y = minimumY; y < maximumY; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = minimumX; x < maximumX; x++)
                {
                    var pixel = y * width + x;
                    if (Contains(observation.HardExclusionMask.Bits.Span, pixel))
                    {
                        continue;
                    }
                    var target = Read(observation.Target, x, y);
                    var background = Read(observation.Background, x, y);
                    if (target <= background)
                    {
                        continue;
                    }
                    var outputValue = Read(output, pixel);
                    var sum = outputValue + (uint)(target - background);
                    if (sum > ushort.MaxValue)
                    {
                        sum = ushort.MaxValue;
                        if (outputValue != ushort.MaxValue)
                        {
                            saturatedOutputPixelCount++;
                        }
                    }
                    Write(output, pixel, (ushort)sum);
                    if (!Contains(eventMask, pixel))
                    {
                        Set(eventMask, pixel);
                        eventPixelCount++;
                    }
                }
            }
        }

        var inputByteFootprint = observations.Sum(observation =>
            checked((long)observation.Target.StrideBytes * height +
                (long)observation.Background.StrideBytes * height + observation.HardExclusionMask.Bits.Length));
        return new(
            new Linear16Frame(width, height, checked(width * 2), first.Target.PixelFormat, output),
            new Linear16PixelMask(width, height, eventMask),
            observations.Count,
            eventPixelCount,
            saturatedOutputPixelCount,
            inputByteFootprint,
            AlgorithmVersion);
    }

    private static void Validate(Linear16TransientReconstructionObservation observation, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(observation.Target, parameterName);
        ArgumentNullException.ThrowIfNull(observation.Background, parameterName);
        ArgumentNullException.ThrowIfNull(observation.HardExclusionMask, parameterName);
        ValidateFrame(observation.Target, parameterName);
        ValidateFrame(observation.Background, parameterName);
        Linear16MaskOperations.Validate(observation.HardExclusionMask, parameterName);
        if (observation.Target.Width != observation.Background.Width ||
            observation.Target.Height != observation.Background.Height ||
            observation.Target.PixelFormat != observation.Background.PixelFormat ||
            observation.Target.Width != observation.HardExclusionMask.Width ||
            observation.Target.Height != observation.HardExclusionMask.Height ||
            !double.IsFinite(observation.Bounds.X) || !double.IsFinite(observation.Bounds.Y) ||
            !double.IsFinite(observation.Bounds.Width) || !double.IsFinite(observation.Bounds.Height) ||
            observation.Bounds.X < 0 || observation.Bounds.Y < 0 ||
            observation.Bounds.Width <= 0 || observation.Bounds.Height <= 0 ||
            observation.Bounds.X + observation.Bounds.Width > observation.Target.Width ||
            observation.Bounds.Y + observation.Bounds.Height > observation.Target.Height)
        {
            throw new ArgumentException("Reconstruction observation layout or bounds are invalid.", parameterName);
        }
    }

    private static void ValidateFrame(Linear16Frame frame, string parameterName)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("Reconstruction supports only linear 16-bit detector frames.", parameterName);
        }
        try
        {
            var layout = new ImageLayout(frame.Width, frame.Height, frame.PixelFormat, frame.StrideBytes);
            layout.Validate();
            if (frame.PixelData.Length < layout.RequiredByteLength)
            {
                throw new ArgumentException("Frame buffer is shorter than its declared layout.", parameterName);
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Frame layout is invalid.", parameterName, exception);
        }
    }

    private static ushort Read(Linear16Frame frame, int x, int y)
    {
        var offset = y * frame.StrideBytes + x * 2;
        var pixels = frame.PixelData.Span;
        return (ushort)(pixels[offset] | pixels[offset + 1] << 8);
    }

    private static ushort Read(ReadOnlySpan<byte> pixels, int pixel)
    {
        var offset = pixel * 2;
        return (ushort)(pixels[offset] | pixels[offset + 1] << 8);
    }

    private static void Write(Span<byte> pixels, int pixel, ushort value)
    {
        var offset = pixel * 2;
        pixels[offset] = (byte)value;
        pixels[offset + 1] = (byte)(value >> 8);
    }

    private static bool Contains(ReadOnlySpan<byte> bits, int pixel)
        => (bits[pixel >> 3] & 1 << (pixel & 7)) != 0;

    private static void Set(Span<byte> bits, int pixel)
        => bits[pixel >> 3] |= (byte)(1 << (pixel & 7));
}
