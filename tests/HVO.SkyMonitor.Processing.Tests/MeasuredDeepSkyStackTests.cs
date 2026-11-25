using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class MeasuredDeepSkyStackTests
{
    private const int Width = 96;
    private const int Height = 96;
    private const int Pixels = Width * Height;
    private static readonly (int X, int Y)[] Shifts = [(-2, 1), (1, -2), (0, 0), (2, 1), (-1, -1)];
    private static readonly int[] Backgrounds = [1000, 1200, 900];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("training", "diffuse")]
    [DataRow("training", "blank")]
    [DataRow("training", "psf")]
    [DataRow("training", "blend")]
    [DataRow("training", "trail")]
    [DataRow("training", "seam")]
    [DataRow("held-out", "diffuse")]
    [DataRow("held-out", "blank")]
    [DataRow("held-out", "psf")]
    [DataRow("held-out", "blend")]
    [DataRow("held-out", "trail")]
    [DataRow("held-out", "seam")]
    public void FrozenIndependentOracleDiscriminatesCompactDiffuseEmission(string split, string scenario)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "measured-deep-sky-v1.json")));
        var oracle = manifest.RootElement.GetProperty(split + "-" + scenario);
        var center = oracle.GetProperty("centroid");
        var truth = new PixelPoint(center[0].GetDouble(), center[1].GetDouble());
        var bytes = RawOracle(scenario, truth, split == "training" ? 193u : 907u);
        Assert.AreEqual(oracle.GetProperty("sha256").GetString()!.ToUpperInvariant(), ProcessingIdentity.ComputePayloadSha256(bytes));

        var stack = Combine(bytes, scenario == "seam" ? truth.X : null);
        var extraction = Extract(stack);
        var accepted = extraction.Detections.Where(detection => detection.Rejection is null).ToArray();
        TestContext.WriteLine(JsonSerializer.Serialize(extraction));
        Assert.IsFalse(extraction.LimitExceeded);
        Assert.IsGreaterThan(0, extraction.SupportedPositions);
        if (scenario == "diffuse")
        {
            Assert.HasCount(1, accepted);
            var hit = accepted[0];
            Assert.IsLessThanOrEqualTo(2.0, double.Hypot(hit.Centroid.X - truth.X, hit.Centroid.Y - truth.Y));
            Assert.IsTrue(hit.RadiusRmsPixels is >= 1.8 and <= 5.5);
            Assert.IsTrue(hit.NetCodeUnits is >= 500 and <= 2000);
            Assert.AreEqual((byte)31, hit.SupportSlots);
        }
        else
        {
            Assert.IsEmpty(accepted);
        }
    }

    [TestMethod]
    public void ACountWithoutCompatibleLocalSlotsCannotQualify()
    {
        var stack = Combine(RawOracle("diffuse", new(42.5, 45.5), 193), limit: 2);
        Assert.AreEqual(0, Extract(stack).SupportedPositions);
        Assert.IsEmpty(Extract(stack).Detections);
        var supported = Combine(RawOracle("diffuse", new(42.5, 45.5), 193));
        var mixed = supported.Support.ToArray();
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = i % 2 == 0 ? (byte)15 : (byte)30;
        }
        Assert.AreEqual(0, Extract(supported with { Support = mixed }).SupportedPositions);
    }

    [TestMethod]
    public void UnsearchedApertureCannotSuppressTheFirstSearchablePeak()
    {
        var pixels = new float[Pixels];
        Array.Fill(pixels, 1000);
        var support = new byte[Pixels];
        Array.Fill(support, (byte)31);
        for (var y = 44; y <= 50; y++)
        {
            for (var x = 21; x <= 27; x++)
            {
                pixels[y * Width + x] += 30;
            }
        }

        var result = ExtendedStackSourceExtractor.Extract(pixels, support, Width, Height, out var at);
        Assert.AreEqual((byte)31, at(new(25.5, 47.5)));
        var peak = Assert.ContainsSingle(result.Detections.Where(detection => detection.Rejection is null));
        Assert.AreEqual(24.5, peak.Centroid.X, 0.75);
        Assert.AreEqual(47.5, peak.Centroid.Y, 0.75);
    }

    private static ExtendedStackExtraction Extract(RegisteredLinearSamples stack)
    {
        var pixels = new float[Pixels];
        for (var i = 0; i < pixels.Length; i++)
        {
            var rgb = stack.Codes.Span.Slice(i * 3, 3);
            pixels[i] = .2126f * rgb[0] + .7152f * rgb[1] + .0722f * rgb[2];
        }
        return ExtendedStackSourceExtractor.Extract(pixels, stack.Support.Span, Width, Height);
    }

    private static RegisteredLinearSamples Combine(byte[] bytes, double? seam = null, int limit = 5)
    {
        var mean = new LinearRegisteredMean(Width, Height, 3);
        foreach (var slot in Enumerable.Range(0, 5).OrderBy(slot => Math.Abs(slot - 2)).Take(limit))
        {
            var start = slot * 3 * Pixels * 2;
            var planes = Enumerable.Range(0, 3).Select(channel => Enumerable.Range(0, Pixels)
                .Select(pixel => (double)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + (channel * Pixels + pixel) * 2, 2)))
                .ToArray()).ToArray();
            var (dx, dy) = Shifts[slot];
            var valid = Enumerable.Range(0, Pixels)
                .Select(pixel => seam is null || slot is not (0 or 4) || pixel % Width + .5 - dx <= seam).ToArray();
            mean.Add(slot, new(Width, Height, valid, planes[0], planes[1], planes[2]),
                slot == 2 ? null : new ImageSimilarity(1, 0, -dx, -dy));
        }
        return mean.Finish();
    }

    // Portable reproduction of the frozen independent Python oracle, checked against every original byte hash.
    private static byte[] RawOracle(string scenario, PixelPoint truth, uint seed)
    {
        var bytes = new byte[5 * 3 * Pixels * 2];
        var index = 0;
        foreach (var (dx, dy) in Shifts)
        {
            foreach (var sky in Backgrounds)
            {
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var noise = -1530;
                        for (var trial = 0; trial < 12; trial++)
                        {
                            seed = unchecked(1664525 * seed + 1013904223);
                            noise += (int)(seed >> 24);
                        }
                        var u = x + .5 - dx - truth.X;
                        var v = y + .5 - dy - truth.Y;
                        var signal = scenario switch
                        {
                            "diffuse" => 18 * Math.Exp(-.5 * (Square(u / 3.4) + Square(v / 2.8))),
                            "psf" => 350 * Math.Exp(-.5 * (u * u + v * v) / Square(.95)),
                            "broad-psf" => 90 * Math.Exp(-.5 * (u * u + v * v) / Square(2.9)),
                            "blend" => 300 * Math.Exp(-.5 * (Square(u + 2.8) + Square(v + 1.4)) / Square(.95)) +
                                300 * Math.Exp(-.5 * (Square(u - 2.8) + Square(v - 1.4)) / Square(.95)),
                            "trail" => 110 * Math.Exp(-.5 * (Square(u / 13) + Square(v / 1.05))),
                            "seam" when u > 0 => 85,
                            _ => 0
                        };
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index, 2), (ushort)Math.Round(sky + noise / 9d + signal));
                        index += 2;
                    }
                }
            }
        }
        return bytes;

        static double Square(double value) => value * value;
    }
}
