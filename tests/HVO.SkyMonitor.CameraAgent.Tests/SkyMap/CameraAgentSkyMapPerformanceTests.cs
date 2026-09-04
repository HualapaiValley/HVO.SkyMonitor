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
public sealed class CameraAgentSkyMapPerformanceTests
{
    private const int Iterations = 60;
    private const double P95BudgetMilliseconds = 250;
    private const long AllocationBudgetBytes = 8L * 1024 * 1024;

    [TestMethod]
    public async Task ProjectAsync_StaysWithinInteractiveBudgetAcrossInstantsAsync()
    {
        var projection = CameraAgentSkyMapProjectionTests.Project(
            CameraAgentSkyMapProjectionTests.CreateCatalog(CameraAgentSkyMapProjectionTests.BrightStars()));
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
            var before = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            var result = await projection.ProjectAsync(instant, CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            elapsed[index] = stopwatch.Elapsed.TotalMilliseconds;
            allocated[index] = GC.GetAllocatedBytesForCurrentThread() - before;
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
        Assert.IsTrue(p95 <= P95BudgetMilliseconds, $"p95 {p95:F2} ms exceeded the {P95BudgetMilliseconds} ms budget.");
        Assert.IsTrue(p95Allocated <= AllocationBudgetBytes, $"p95 allocation {p95Allocated} bytes exceeded the {AllocationBudgetBytes} byte budget.");
    }
}
