using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A full planned half-open UTC axis. The earliest actual sample in each bin is displayed.</summary>
public sealed record PlannedKeogramAxis(DateTimeOffset StartUtc, DateTimeOffset EndUtc, TimeSpan ColumnDuration)
{
    public int Width(int maximumColumnCount)
    {
        if (StartUtc.Offset != TimeSpan.Zero || EndUtc.Offset != TimeSpan.Zero || EndUtc <= StartUtc ||
            ColumnDuration < TimeSpan.FromSeconds(1) || maximumColumnCount is < 1 or > KeogramComposer.MaximumColumnLimit)
        {
            throw new ArgumentException("The planned keogram axis is invalid.");
        }
        var ticks = (EndUtc - StartUtc).Ticks;
        var width = (ticks - 1) / ColumnDuration.Ticks + 1;
        return width <= maximumColumnCount ? (int)width
            : throw new ArgumentException("The full planned axis exceeds its column bound.");
    }
}

/// <summary>Preserves planned duration and explicit missing bins when assembling real sampled columns.</summary>
public static class PlannedKeogramComposer
{
    public const string AlgorithmVersion = "keogram-planned-utc-bins-earliest-actual-v1";

    public static KeogramResult Assemble(IReadOnlyList<KeogramSegment> segments, KeogramCompositionOptions options,
        PlannedKeogramAxis axis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(options);
        // Existing validation checks geometry, layouts, ordered actual times, and segment column bounds.
        _ = KeogramComposer.ComputeAssemblyTimeAxis(segments, options with
        { MaximumColumnCount = KeogramComposer.MaximumColumnLimit, MaximumGapColumnCount = 1 });
        var width = axis.Width(options.MaximumColumnCount);
        var height = options.SamplePath.Count;
        var format = segments[0].PixelFormat;
        var bytesPerPixel = ImageLayout.BytesPerPixel(format);
        var stride = checked(width * bytesPerPixel);
        var length = checked((long)stride * height);
        if (length > 256L * 1024 * 1024) throw new ArgumentException("The planned keogram exceeds its output byte bound.", nameof(axis));
        var output = new byte[(int)length];
        var occupied = new bool[width];
        foreach (var segment in segments)
        {
            foreach (var frame in segment.FrameColumns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.TimestampUtc < axis.StartUtc || frame.TimestampUtc >= axis.EndUtc)
                {
                    throw new ArgumentException("An actual keogram source lies outside the planned period.", nameof(segments));
                }
                var column = (int)((frame.TimestampUtc - axis.StartUtc).Ticks / axis.ColumnDuration.Ticks);
                if (occupied[column]) continue;
                occupied[column] = true;
                for (var row = 0; row < height; row++)
                {
                    segment.PixelData.Span.Slice(row * segment.StrideBytes + frame.Column * bytesPerPixel, bytesPerPixel)
                        .CopyTo(output.AsSpan(row * stride + column * bytesPerPixel, bytesPerPixel));
                }
            }
        }
        var gaps = new List<KeogramGap>();
        for (var column = 0; column < width; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occupied[column]) continue;
            var first = column;
            while (column < width && !occupied[column])
            {
                for (var row = 0; row < height; row++)
                {
                    var value = (byte)((column + row) % 8 < 4 ? 32 : 96);
                    output.AsSpan(row * stride + column * bytesPerPixel, bytesPerPixel).Fill(value);
                }
                column++;
            }
            var start = axis.StartUtc.AddTicks(first * axis.ColumnDuration.Ticks);
            var end = column == width ? axis.EndUtc : axis.StartUtc.AddTicks(column * axis.ColumnDuration.Ticks);
            gaps.Add(new(start, end, first, column - first));
            column--;
        }
        return new(width, height, stride, format, output, occupied.Count(static value => value), gaps,
            axis.ColumnDuration.TotalSeconds, options.SamplePath.Count(static point => point is not null), AlgorithmVersion);
    }
}
