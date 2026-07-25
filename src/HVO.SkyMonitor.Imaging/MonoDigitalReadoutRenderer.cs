using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Applies deterministic post-ADC ROI binning and output quantization to native Mono16 samples.</summary>
public static class MonoDigitalReadoutRenderer
{
    public static SceneRenderResult Apply(
        SceneRenderResult native,
        ImageLayout nativeLayout,
        ImageLayout outputLayout,
        SensorReadoutProfile readout,
        int nativeSampleDepthBits,
        ProjectionContext? activeProjection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(readout);
        ImageBuffer.Validate(nativeLayout, native.Pixels);
        nativeLayout.Validate();
        outputLayout.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (nativeLayout.PixelFormat != CameraPixelFormat.Mono16 ||
            outputLayout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16) ||
            readout.Packing != FrameSamplePacking.ByteAligned ||
            readout.BinningAlgorithm == FrameBinningAlgorithm.ChargeSumV1 ||
            nativeSampleDepthBits is < 1 or > 16 || nativeSampleDepthBits < readout.SampleDepthBits ||
            nativeLayout.Width % readout.BinX != 0 || nativeLayout.Height % readout.BinY != 0 ||
            outputLayout.Width != nativeLayout.Width / readout.BinX ||
            outputLayout.Height != nativeLayout.Height / readout.BinY)
        {
            throw new NotSupportedException("The configured monochrome digital readout is not supported.");
        }

        var output = new byte[outputLayout.RequiredByteLength];
        var binSamples = checked(readout.BinX * readout.BinY);
        var sampleMaximum = (1 << readout.SampleDepthBits) - 1;
        var containerMaximum = (1 << readout.ContainerDepthBits) - 1;
        var preserveNativeClipping = readout.BinningAlgorithm == FrameBinningAlgorithm.IdentityV1 &&
            readout.BinX == 1 && readout.BinY == 1 && nativeSampleDepthBits == readout.SampleDepthBits;
        long count = 0;
        long total = 0;
        var minimum = int.MaxValue;
        var maximum = int.MinValue;
        long clippedHigh = 0;
        for (var outputY = 0; outputY < outputLayout.Height; outputY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var outputX = 0; outputX < outputLayout.Width; outputX++)
            {
                long sum = 0;
                for (var binY = 0; binY < readout.BinY; binY++)
                {
                    var nativeY = outputY * readout.BinY + binY;
                    for (var binX = 0; binX < readout.BinX; binX++)
                    {
                        var nativeX = outputX * readout.BinX + binX;
                        var offset = nativeY * nativeLayout.StrideBytes + nativeX * 2;
                        var nativeCode = native.Pixels.Span[offset] | native.Pixels.Span[offset + 1] << 8;
                        sum += ReduceDepth(nativeCode, nativeSampleDepthBits, readout.SampleDepthBits, sampleMaximum);
                    }
                }

                var meaningfulCode = readout.BinningAlgorithm switch
                {
                    FrameBinningAlgorithm.IdentityV1 => checked((int)sum),
                    FrameBinningAlgorithm.DigitalSumV1 => checked((int)Math.Min(sum, sampleMaximum)),
                    FrameBinningAlgorithm.DigitalAverageV1 => checked((int)((sum + binSamples / 2) / binSamples)),
                    _ => throw new NotSupportedException("The configured bin algorithm requires a charge-domain renderer.")
                };
                var isActive = activeProjection is null ||
                    activeProjection.Value.ContainsSample(outputX + 0.5, outputY + 0.5);
                if (isActive && sum > sampleMaximum && readout.BinningAlgorithm == FrameBinningAlgorithm.DigitalSumV1)
                {
                    clippedHigh++;
                }
                var storedCode = ToStoredCode(meaningfulCode, readout, sampleMaximum, containerMaximum);
                Write(output, outputLayout, outputX, outputY, storedCode, readout.ByteOrder);
                if (isActive)
                {
                    count++;
                    minimum = Math.Min(minimum, storedCode);
                    maximum = Math.Max(maximum, storedCode);
                    total += storedCode;
                }
            }
        }

        return native with
        {
            Pixels = output,
            AlgorithmVersion = $"{native.AlgorithmVersion}+native-readout-{AlgorithmName(readout.BinningAlgorithm)}-v1",
            Statistics = new RenderStatistics(
                count,
                count == 0 ? 0 : minimum,
                count == 0 ? 0 : maximum,
                count == 0 ? 0 : total / (double)count,
                preserveNativeClipping ? native.Statistics.ClippedLow : 0,
                preserveNativeClipping ? native.Statistics.ClippedHigh : clippedHigh),
            Objects = native.Objects.Select(item => item with
            {
                SourcePixel = Scale(item.SourcePixel, readout.BinX, readout.BinY),
                DepositedCentroid = Scale(item.DepositedCentroid, readout.BinX, readout.BinY),
                MinimumX = item.MinimumX / readout.BinX,
                MinimumY = item.MinimumY / readout.BinY,
                MaximumX = item.MaximumX / readout.BinX,
                MaximumY = item.MaximumY / readout.BinY
            }).ToArray()
        };
    }

    private static int ReduceDepth(int value, int sourceDepth, int targetDepth, int targetMaximum)
    {
        if (sourceDepth == targetDepth)
        {
            return Math.Min(value, targetMaximum);
        }
        var shift = sourceDepth - targetDepth;
        return Math.Min((value + (1 << (shift - 1))) >> shift, targetMaximum);
    }

    private static int ToStoredCode(
        int meaningfulCode,
        SensorReadoutProfile readout,
        int sampleMaximum,
        int containerMaximum)
        => readout.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.IdentityV1 or FrameStoredCodeTransform.RightAlignedV1 => meaningfulCode,
            FrameStoredCodeTransform.LeftShiftedV1 => meaningfulCode << (readout.ContainerDepthBits - readout.SampleDepthBits),
            FrameStoredCodeTransform.FullRangeScaledV1 => checked((int)Math.Round(
                meaningfulCode * containerMaximum / (double)sampleMaximum,
                MidpointRounding.AwayFromZero)),
            _ => throw new ArgumentOutOfRangeException(nameof(readout))
        };

    private static void Write(
        Span<byte> output,
        ImageLayout layout,
        int x,
        int y,
        int value,
        SampleByteOrder byteOrder)
    {
        var offset = y * layout.StrideBytes + x * ImageLayout.BytesPerPixel(layout.PixelFormat);
        if (layout.PixelFormat == CameraPixelFormat.Mono8)
        {
            output[offset] = checked((byte)value);
            return;
        }
        if (byteOrder == SampleByteOrder.LittleEndian)
        {
            output[offset] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }
        else
        {
            output[offset] = (byte)(value >> 8);
            output[offset + 1] = (byte)value;
        }
    }

    private static string AlgorithmName(FrameBinningAlgorithm value) => value switch
    {
        FrameBinningAlgorithm.IdentityV1 => "identity",
        FrameBinningAlgorithm.DigitalSumV1 => "digital-sum",
        FrameBinningAlgorithm.DigitalAverageV1 => "digital-average",
        _ => throw new NotSupportedException("The configured bin algorithm requires a charge-domain renderer.")
    };

    private static PixelPoint Scale(PixelPoint value, int binX, int binY)
        => new(value.X / binX, value.Y / binY);
}
