using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class RetainedAstrometryBrowserAcceptanceTests
{
    private static async Task<List<AstrometryReadSample>> RecordAstrometryReadBoundsAsync(
        CameraAgentKestrelFixture host, IBrowserContext context, AstrometryBrowserFixture identity, string output)
    {
        var counted = new AstrometryCountedArtifacts(host.Services.GetRequiredService<ICameraAgentArtifactService>());
        var store = host.Services.GetRequiredService<SqliteCaptureProcessingStore>();
        using var retained = new CameraAgentRetainedAstrometryService(store, counted);
        var baseline = new CameraAgentLayeredPresentationService(store, counted);
        var enabled = new CameraAgentLayeredPresentationService(store, counted,
            persistence: host.Services.GetRequiredService<CaptureProcessingPersistence>(), astrometry: retained);
        var samples = new List<AstrometryReadSample>();
        using var process = Process.GetCurrentProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var operation in new[] { "base-composition", "retained-mapping-diagnostics", "measured-composition", "authenticated-astrometry", "authenticated-presentation" })
        {
            for (var sample = 0; sample < 6; sample++)
            {
                counted.Reset();
                process.Refresh();
                var rssBefore = process.WorkingSet64;
                var cpuBefore = process.TotalProcessorTime;
                var allocationsBefore = GC.GetTotalAllocatedBytes(true);
                var ioBefore = ReadAstrometryProcessIo();
                var start = Stopwatch.GetTimestamp();
                var responseBytes = 0;
                if (operation == "retained-mapping-diagnostics")
                {
                    var result = await retained.GetAsync(identity.CaptureId, timeout.Token).ConfigureAwait(false);
                    Assert.AreEqual("MeasuredFit", result.GeometryBasis, result.Reason);
                    Assert.IsNotNull(result.Mapping);
                    Assert.IsNotNull(result.Diagnostics);
                    Assert.AreEqual(identity.CaptureId, result.CaptureId);
                    Assert.AreEqual(identity.RawArtifactId, result.SourceDescriptor!.Artifact.ArtifactId);
                    Assert.AreEqual(identity.RawChecksum, result.Solution!.Assessment.Frame.SourcePayloadSha256);
                    Assert.IsTrue(counted.Opens <= 5, "Retained projection must open at most five authenticated source artifacts.");
                    Assert.IsTrue(counted.Bytes <= 44L * 1024 * 1024, "Retained source bytes must fit the production 44MiB bound.");
                    Assert.IsTrue(counted.Bytes <= 4L * 1024 * 1024, "The fixed 512-square workload must fit a tighter 4MiB authenticated payload envelope.");
                }
                else if (operation is "base-composition" or "measured-composition")
                {
                    var result = await (operation == "base-composition" ? baseline : enabled)
                        .GetAsync(identity.CaptureId, timeout.Token).ConfigureAwait(false);
                    Assert.IsNotNull(result.Presentation, result.Reason);
                    Assert.AreEqual(identity.BaseArtifactId, result.Presentation.BaseArtifactId);
                    Assert.AreEqual(operation == "base-composition" ? 0 : 2, result.Presentation.Layers.Count);
                    if (operation == "measured-composition") Assert.AreEqual(identity.ManifestIdentitySha256, result.Presentation.ManifestIdentitySha256);
                    responseBytes = result.Presentation.Svg.Length;
                }
                else
                {
                    var suffix = operation == "authenticated-astrometry" ? "astrometry" : "presentation";
                    var response = await context.APIRequest.GetAsync($"/api/v1/operations/gallery/{identity.CaptureId:D}/{suffix}").ConfigureAwait(false);
                    try
                    {
                        Assert.AreEqual(200, response.Status);
                        var bytes = await response.BodyAsync().ConfigureAwait(false);
                        responseBytes = bytes.Length;
                        using var json = JsonDocument.Parse(bytes);
                        Assert.AreEqual(identity.CaptureId, json.RootElement.GetProperty("captureId").GetGuid());
                        if (suffix == "astrometry") Assert.AreEqual("MeasuredFit", json.RootElement.GetProperty("geometryBasis").GetString());
                        else Assert.AreEqual(identity.ManifestIdentitySha256, json.RootElement.GetProperty("manifestIdentitySha256").GetString());
                    }
                    finally { await response.DisposeAsync().ConfigureAwait(false); }
                }
                var wallMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocationsBefore;
                var ioAfter = ReadAstrometryProcessIo();
                process.Refresh();
                samples.Add(new(operation, sample, sample == 0 ? "first-instrumented-call" : "warm",
                    wallMilliseconds, cpuMilliseconds, allocatedBytes, rssBefore, process.WorkingSet64,
                    counted.Opens, counted.Bytes, ioAfter.Logical - ioBefore.Logical,
                    ioAfter.Physical - ioBefore.Physical, ioAfter.LogicalWritten - ioBefore.LogicalWritten,
                    ioAfter.PhysicalWritten - ioBefore.PhysicalWritten, ioAfter.ReadOperations - ioBefore.ReadOperations,
                    ioAfter.WriteOperations - ioBefore.WriteOperations, responseBytes));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "read-resources.json"), JsonSerializer.Serialize(new
        {
            schema = "issue-1644-read-resources-v1",
            measuredUtc = DateTimeOffset.UtcNow,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            identity,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            configuration = typeof(RetainedAstrometryBrowserAcceptanceTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
            concurrency = 1,
            warmSamplesPerOperation = 5,
            structuralLimits = new
            {
                CameraAgentRetainedAstrometryService.MaximumRawBytes,
                CameraAgentRetainedAstrometryService.MaximumReadBytes,
                CameraAgentLayeredPresentationService.MaximumCacheEntries,
                CameraAgentLayeredPresentationService.MaximumCacheBytes,
                CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
                CameraAgentLayeredPresentationService.MaximumSvgBytes,
                CameraAgentLayeredPresentationService.MaximumSvgElements,
                reconstructionConcurrency = 1,
                queuedRawBuffers = 0
            },
            ceilings = new { warmWallMilliseconds = 500, warmCpuMilliseconds = 500, warmAllocatedBytes = 100 * 1024 * 1024, warmRssGrowthBytes = 128 * 1024 * 1024, responseBytes = 1024 * 1024, warmLogicalReadBytes = 64 * 1024 * 1024, warmPhysicalReadBytes = 64 * 1024 * 1024, artifactOpens = 16, artifactDeclaredBytes = 16 * 1024 * 1024 },
            attribution = "Process-wide managed allocation/CPU/RSS includes host infrastructure and the test caller, excludes Chromium/Playwright driver. Logical/physical I/O is Linux /proc/self/io process-wide, not just artifact payload. Artifact counts/declared bytes instrument the real service boundary for the first three operations, not the host HTTP operations or persistence direct-restore I/O. First-call samples are not cold OS cache or cold host samples: login already loaded Current Sky. Base composition omits optional astrometry on the same immutable base; this is a same-head feature-cost comparison, not a historical-version speedup.",
            samples
        }, AstrometryBrowserJson)).ConfigureAwait(false);
        foreach (var value in samples.Where(value => value.Temperature == "warm"))
        {
            Assert.IsLessThan(500d, value.WallMilliseconds, value.Operation);
            Assert.IsLessThan(500d, value.CpuMilliseconds, value.Operation);
            Assert.IsLessThan(100L * 1024 * 1024, value.AllocatedBytes, value.Operation);
            Assert.IsLessThan(128L * 1024 * 1024, value.RssAfter - value.RssBefore, value.Operation);
            Assert.IsLessThan(1024 * 1024, value.ResponseBytes, value.Operation);
            Assert.IsLessThan(64L * 1024 * 1024, value.LogicalReadBytes, value.Operation);
            Assert.IsLessThan(64L * 1024 * 1024, value.PhysicalReadBytes, value.Operation);
            Assert.IsTrue(value.ArtifactOpens <= 16, value.Operation);
            Assert.IsTrue(value.ArtifactDeclaredBytes <= 16L * 1024 * 1024, value.Operation);
        }
        return samples;
    }

    private static (long Logical, long Physical, long LogicalWritten, long PhysicalWritten, long ReadOperations, long WriteOperations) ReadAstrometryProcessIo()
    {
        if (!OperatingSystem.IsLinux()) return (0, 0, 0, 0, 0, 0);
        var fields = File.ReadLines("/proc/self/io").Select(line => line.Split(':', 2))
            .ToDictionary(value => value[0], value => long.Parse(value[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return (fields["rchar"], fields["read_bytes"], fields["wchar"], fields["write_bytes"], fields["syscr"], fields["syscw"]);
    }

    private sealed record AstrometryReadSample(string Operation, int Sample, string Temperature,
        double WallMilliseconds, double CpuMilliseconds, long AllocatedBytes, long RssBefore, long RssAfter,
        int ArtifactOpens, long ArtifactDeclaredBytes, long LogicalReadBytes, long PhysicalReadBytes,
        long LogicalWriteBytes, long PhysicalWriteBytes, long ReadSystemCalls, long WriteSystemCalls, int ResponseBytes);

    // Delegates every read to the production checksum/sidecar boundary; counts logical authenticated
    // content opens and their declared lengths. No replacement streams or canned projections.
    private sealed class AstrometryCountedArtifacts(ICameraAgentArtifactService inner) : ICameraAgentArtifactService
    {
        internal int Opens { get; private set; }
        internal long Bytes { get; private set; }
        internal void Reset() { Opens = 0; Bytes = 0; }
        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            var result = await inner.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            Opens++;
            Bytes += result.Content?.ByteLength ?? 0;
            return result;
        }
        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(Guid executionId, Guid artifactId, CancellationToken cancellationToken)
            => inner.OpenReplayOutputContentAsync(executionId, artifactId, cancellationToken);
        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken, Guid? displayReference = null)
            => inner.GetPreviewAsync(artifactId, cancellationToken, displayReference);
    }
}
