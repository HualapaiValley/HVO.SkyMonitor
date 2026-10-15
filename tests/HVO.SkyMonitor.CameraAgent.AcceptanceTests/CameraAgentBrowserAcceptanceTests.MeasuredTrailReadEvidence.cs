using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    private static async Task RecordMeasuredTrailReadEvidenceAsync(string path, Guid captureId,
        ICameraAgentTransientOperatorProjection projection, ICameraAgentLayeredPresentationService presentations,
        string expectedManifestIdentity)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var results = new List<object>();
        foreach (var operation in new[] { "retained-sqlite-projection", "composed-presentation" })
        {
            foreach (var concurrency in new[] { 1, 4, 8 })
            {
                var firstStart = Stopwatch.GetTimestamp();
                await ReadAsync(operation).ConfigureAwait(false);
                var firstMilliseconds = Stopwatch.GetElapsedTime(firstStart).TotalMilliseconds;
                for (var index = 0; index < 20; index++) await ReadAsync(operation).ConfigureAwait(false);
                var samples = new double[200];
                using var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var allocationsStart = GC.GetTotalAllocatedBytes(true);
                var started = Stopwatch.GetTimestamp();
                for (var offset = 0; offset < samples.Length; offset += concurrency)
                {
                    await Task.WhenAll(Enumerable.Range(offset, Math.Min(concurrency, samples.Length - offset))
                        .Select(async index =>
                        {
                            var readStart = Stopwatch.GetTimestamp();
                            await ReadAsync(operation).ConfigureAwait(false);
                            samples[index] = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                        })).ConfigureAwait(false);
                }
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var allocations = GC.GetTotalAllocatedBytes(true) - allocationsStart;
                var cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                Array.Sort(samples);
                results.Add(new
                {
                    operation, concurrency, warmups = 20, reads = samples.Length,
                    firstMilliseconds, elapsedMilliseconds = elapsed,
                    medianMilliseconds = samples[99], p95Milliseconds = samples[189],
                    maximumMilliseconds = samples[^1], readsPerSecond = samples.Length * 1000 / elapsed,
                    allocatedBytes = allocations, cpuMilliseconds, workingSetBytes = process.WorkingSet64
                });
            }
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = "issue-1005-retained-read-performance-v1",
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            measuredUtc = DateTimeOffset.UtcNow, captureId, expectedManifestIdentity,
            limitation = "Real SQLite projection and composition from seven 64x48 VirtualSky captures; one actual candidate. Browser pages closed and background capture/finalization paused. Supplements W1 raster composition; not physical detector throughput or a before/after comparison.",
            results
        }, MeasuredTrailEvidenceJsonOptions), deadline.Token).ConfigureAwait(false);

        async Task ReadAsync(string operation)
        {
            if (operation == "retained-sqlite-projection")
            {
                var result = await projection.GetCaptureTrailsAsync(captureId, deadline.Token).ConfigureAwait(false);
                Assert.AreEqual("MeasuredEvidence", result.State, result.Reason);
                Assert.IsFalse(result.Incomplete);
                Assert.AreEqual(1, result.Trails.Count);
            }
            else
            {
                var result = await presentations.GetAsync(captureId, deadline.Token).ConfigureAwait(false);
                Assert.IsNotNull(result.Presentation);
                Assert.AreEqual(expectedManifestIdentity, result.Presentation.ManifestIdentitySha256);
            }
        }
    }
}
