using System.Diagnostics;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;

namespace HVO.SkyMonitor.CameraAgent.Tests.SkyMap;

/// <summary>
/// Bounded response evidence for the Operations sky map: one interactive projection over the
/// in-memory catalog must stay well inside a page-load budget and allocate a bounded amount.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CameraAgentSkyMapPerformanceTests
{
    private const int Iterations = 60;
    private const double P95BudgetMilliseconds = 250;
    private const long AllocationBudgetBytes = 8L * 1024 * 1024;

    [TestMethod]
    public async Task ProjectAsync_StaysWithinInteractiveBudgetAcrossInstantsAsync()
    {
        // A dense synthetic sky (well above the 200-object bound) so the bound and its cost are exercised.
        var projection = CameraAgentSkyMapProjectionTests.Project(
            CameraAgentSkyMapProjectionTests.CreateCatalog(CameraAgentSkyMapProjectionTests.BrightStars().Concat(SyntheticSky(4000))));
        var start = DateTimeOffset.UtcNow.AddHours(-12);
        for (var warmup = 0; warmup < 3; warmup++)
        {
            _ = await projection.ProjectAsync(start, CancellationToken.None).ConfigureAwait(false);
        }

        var elapsed = new double[Iterations];
        var allocated = new long[Iterations];
        var maximumObjects = 0;
        for (var index = 0; index < Iterations; index++)
        {
            var instant = start.AddMinutes(index * 10);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var result = await projection.ProjectAsync(instant, CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            elapsed[index] = stopwatch.Elapsed.TotalMilliseconds;
            allocated[index] = GC.GetTotalAllocatedBytes(precise: true) - before;
            maximumObjects = Math.Max(maximumObjects, result.Objects.Count);
            Assert.IsTrue(result.Objects.Count <= CameraAgentSkyMapProjection.MaximumObjects);
        }

        Array.Sort(elapsed);
        Array.Sort(allocated);
        var p95 = elapsed[(int)Math.Ceiling(Iterations * 0.95) - 1];
        var p95Allocated = allocated[(int)Math.Ceiling(Iterations * 0.95) - 1];
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"sky-map projection: iterations={Iterations} median={elapsed[Iterations / 2]:F2}ms p95={p95:F2}ms max={elapsed[^1]:F2}ms allocated-p95={p95Allocated} bytes objects-max={maximumObjects}"));
        Assert.AreEqual(CameraAgentSkyMapProjection.MaximumObjects, maximumObjects, "The dense synthetic sky must reach the object bound.");
        Assert.IsTrue(p95 <= P95BudgetMilliseconds, $"p95 {p95:F2} ms exceeded the {P95BudgetMilliseconds} ms budget.");
        Assert.IsTrue(p95Allocated <= AllocationBudgetBytes, $"p95 allocation {p95Allocated} bytes exceeded the {AllocationBudgetBytes} byte budget.");
    }

    /// <summary>Deterministic pseudo-stars spread over the whole sky between magnitude 1 and 6.</summary>
    private static IEnumerable<HVO.SkyMonitor.Astronomy.CelestialCatalogObject> SyntheticSky(int count)
    {
        for (var index = 0; index < count; index++)
        {
            var golden = index * 0.618033988749895;
            var rightAscensionHours = (golden - Math.Floor(golden)) * 24;
            var declinationDegrees = Math.Asin(2.0 * (index + 0.5) / count - 1) * 180 / Math.PI;
            var magnitude = 1 + 5.0 * ((index * 7919) % 1000) / 1000.0;
            yield return new(
                $"synthetic-{index}",
                $"Synthetic {index}",
                rightAscensionHours,
                declinationDegrees,
                magnitude,
                0.5,
                null);
        }
    }
}
