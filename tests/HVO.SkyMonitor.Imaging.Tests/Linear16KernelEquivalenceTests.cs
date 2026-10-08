using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

/// <summary>
/// Pins the #1170 rewrites of <see cref="Mono16DisplayStretch"/> and <see cref="Linear16ArithmeticMean"/> to the
/// exact bytes of the implementations they replaced. The references below are frozen copies of the previous
/// per-pixel code; the algorithm versions are unchanged because every output byte is.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded pixel fixtures are reproducible numerical test data, not security material.")]
public sealed class Linear16KernelEquivalenceTests
{
    private static readonly Mono16DisplayStretchOptions[] StretchPolicies =
    [
        new(),
        new(0.5, 0.9997, 8),
        new(0, 1, 0.25),
        new(0.25, 0.75, 12),
    ];

    [TestMethod]
    [DataRow(1, 1, 0, 7)]
    [DataRow(17, 3, 0, 65_535)]
    [DataRow(33, 9, 6, 4_096)]
    [DataRow(64, 32, 0, 300)]
    [DataRow(131, 47, 2, 65_535)]
    [DataRow(37, 5, 3, 900)]
    [DataRow(484, 304, 0, 1_200)]
    public void DisplayStretchMatchesThePerPixelTransferForEveryPolicy(int width, int height, int padding, int maximum)
    {
        var random = new Random(width * 7919 + height);
        var stride = width * 2 + padding;
        var pixels = new byte[stride * height];
        random.NextBytes(pixels);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // A sparse zero aperture plus a skewed sky with a few saturated stars exercises the black clamp,
                // both lookup and direct ranges, and samples above white.
                var roll = random.Next(100);
                var sample = roll < 5 ? 0 : roll > 97 ? maximum : random.Next(Math.Min(maximum, 1 + maximum / 4));
                pixels[y * stride + x * 2] = (byte)sample;
                pixels[y * stride + x * 2 + 1] = (byte)(sample >> 8);
            }
        }

        foreach (var policy in StretchPolicies)
        {
            CollectionAssert.AreEqual(
                ReferenceStretch(width, height, pixels, stride, policy),
                Mono16DisplayStretch.Apply(width, height, pixels, stride, policy),
                $"{width}x{height}+{padding} max {maximum} policy {policy}");
        }
    }

    [TestMethod]
    public void DisplayStretchMatchesForUniformAllZeroAndInvertedPercentileFrames()
    {
        foreach (var value in new ushort[] { 0, 1, 40_000, ushort.MaxValue })
        {
            var pixels = Pack(Enumerable.Repeat(value, 12).ToArray());
            CollectionAssert.AreEqual(ReferenceStretch(4, 3, pixels, 8, new()), Mono16DisplayStretch.Apply(4, 3, pixels));
        }

        var twoLevels = Pack([5, 5, 5, 5, 5, 5, 5, 9]);
        CollectionAssert.AreEqual(
            ReferenceStretch(8, 1, twoLevels, 16, new(0.5, 0.6, 4)),
            Mono16DisplayStretch.Apply(8, 1, twoLevels, options: new(0.5, 0.6, 4)));
    }

    [TestMethod]
    [DataRow(1, 1, 0, 1)]
    [DataRow(15, 2, 0, 2)]
    [DataRow(16, 2, 2, 5)]
    [DataRow(67, 5, 4, 7)]
    [DataRow(37, 4, 3, 9)]
    [DataRow(484, 13, 0, 30)]
    public void ArithmeticMeanMatchesTheWideAccumulator(int width, int height, int padding, int count)
    {
        var random = new Random(width * 104_729 + count);
        // Odd frames carry the padding, so an odd padding gives rows that start at odd byte offsets.
        var frames = Enumerable.Range(0, count).Select(index =>
        {
            var stride = width * 2 + padding * (index % 2);
            var pixels = new byte[stride * height];
            random.NextBytes(pixels);
            return new Linear16Frame(width, height, stride, CameraPixelFormat.Mono16, pixels);
        }).ToArray();

        var result = Linear16ArithmeticMean.Compute(frames);

        CollectionAssert.AreEqual(ReferenceMean(frames), result.PixelData.ToArray());
        Assert.AreEqual(width * 2, result.StrideBytes);
        Assert.AreEqual(count, result.SourceCount);
    }

    [TestMethod]
    public void ArithmeticMeanOfSaturatedFramesAtTheNarrowLimitIsExact()
    {
        var saturated = new Linear16Frame(19, 1, 38, CameraPixelFormat.BayerRggb16, Enumerable.Repeat((byte)0xff, 38).ToArray());
        var frames = Enumerable.Repeat(saturated, 65_537).ToArray();

        var result = Linear16ArithmeticMean.Compute(frames);

        CollectionAssert.AreEqual(Enumerable.Repeat((byte)0xff, 38).ToArray(), result.PixelData.ToArray());
    }

    private static byte[] ReferenceStretch(int width, int height, byte[] pixels, int stride, Mono16DisplayStretchOptions options)
    {
        var histogram = new int[ushort.MaxValue + 1];
        var activeCount = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 2;
                var sample = pixels[offset] | pixels[offset + 1] << 8;
                if (sample > 0)
                {
                    histogram[sample]++;
                    activeCount++;
                }
            }
        }

        var output = new byte[width * height];
        if (activeCount == 0)
        {
            return output;
        }

        var black = Percentile(histogram, activeCount, options.BlackPercentile);
        var white = Percentile(histogram, activeCount, options.WhitePercentile);
        if (white <= black)
        {
            white = black;
            black = 0;
        }

        var denominator = Math.Asinh(options.AsinhStrength);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 2;
                var sample = pixels[offset] | pixels[offset + 1] << 8;
                if (sample <= black)
                {
                    continue;
                }

                var normalized = Math.Clamp((sample - black) / (double)(white - black), 0, 1);
                var stretched = Math.Asinh(options.AsinhStrength * normalized) / denominator;
                output[y * width + x] = (byte)Math.Round(stretched * byte.MaxValue);
            }
        }
        return output;
    }

    private static int Percentile(int[] histogram, int sampleCount, double percentile)
    {
        var target = Math.Max(1, (int)Math.Ceiling(sampleCount * percentile));
        var cumulative = 0;
        for (var value = 1; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return value;
            }
        }
        return ushort.MaxValue;
    }

    private static byte[] ReferenceMean(Linear16Frame[] frames)
    {
        var first = frames[0];
        var totals = new ulong[first.Width * first.Height];
        foreach (var frame in frames)
        {
            var source = frame.PixelData.Span;
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    var offset = y * frame.StrideBytes + x * 2;
                    totals[y * frame.Width + x] += (ushort)(source[offset] | source[offset + 1] << 8);
                }
            }
        }

        var output = new byte[totals.Length * 2];
        for (var pixel = 0; pixel < totals.Length; pixel++)
        {
            var average = (ushort)(totals[pixel] / (ulong)frames.Length);
            output[pixel * 2] = (byte)average;
            output[pixel * 2 + 1] = (byte)(average >> 8);
        }
        return output;
    }

    private static byte[] Pack(ushort[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            bytes[index * 2] = (byte)samples[index];
            bytes[index * 2 + 1] = (byte)(samples[index] >> 8);
        }
        return bytes;
    }
}
