using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class HorizonObstructionPerformanceTests
{
    public TestContext TestContext { get; set; } = default!;

    [TestMethod]
    public void FullResolutionNumericalAndCostEvidence()
    {
        foreach (var (width, height) in new[] { (1936, 1216), (4096, 4096) })
        {
            var rig = HorizonObstructionTests.Rig(size: width);
            rig = rig with { Sensor = rig.Sensor with { HeightPixels = height } };
            var evaluator = new HorizonObstructionEvaluator(HorizonObstructionTests.Profile(rig), rig, HorizonObstructionTests.Observer);
            var projector = ProjectorFactory.Create(evaluator.Projection);
            var transform = ProjectedSceneImageTransformV1.Identity(width, height);
            TestContext.WriteLine($"workload={width}x{height}; profile={evaluator.Profile.ComputeIdentitySha256()}; no IO/cache/backlog; concurrency=1");
            // One warmup per path, followed by three equivalent trials of the same flat 30-degree horizon.
            _ = Baseline();
            _ = HorizonObstructionMask.Create(evaluator, transform);
            for (var trial = 0; trial < 3; trial++)
            {
                var baseline = Measure(Baseline);
                var after = Measure(() => HorizonObstructionMask.Create(evaluator, transform));
                var combined = Linear16MaskOperations.Combine([after.Value.Obstructed, after.Value.Unknown, after.Value.OutsideDomain]);
                CollectionAssert.AreEqual(baseline.Value, combined.Bits.ToArray());
                Assert.AreEqual(3 * Linear16MaskOperations.RequiredByteLength(width, height),
                    after.Value.Obstructed.Bits.Length + after.Value.Unknown.Bits.Length + after.Value.OutsideDomain.Bits.Length);
                using var process = Process.GetCurrentProcess();
                TestContext.WriteLine($"trial={trial}; baseline_ms={baseline.Milliseconds:F3}; after_ms={after.Milliseconds:F3}; " +
                    $"baseline_cpu_ms={baseline.CpuMilliseconds:F3}; after_cpu_ms={after.CpuMilliseconds:F3}; " +
                    $"baseline_alloc={baseline.Allocated}; after_alloc={after.Allocated}; peak_rss={process.PeakWorkingSet64}; " +
                    $"sha256={Convert.ToHexString(SHA256.HashData(combined.Bits.Span))}");
            }

            byte[] Baseline()
            {
                var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var direction = projector.Unproject(new(x + .5, y + .5));
                        if (direction is null or { AltitudeDegrees: <= 30 })
                        {
                            var pixel = y * width + x;
                            bits[pixel >> 3] |= (byte)(1 << (pixel & 7));
                        }
                    }
                return bits;
            }
        }
    }

    private static (T Value, double Milliseconds, double CpuMilliseconds, long Allocated) Measure<T>(Func<T> run)
    {
        using var process = Process.GetCurrentProcess();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var cpu = process.TotalProcessorTime;
        var start = Stopwatch.GetTimestamp();
        var result = run();
        return (result, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
