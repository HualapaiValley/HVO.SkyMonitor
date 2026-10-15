using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing
{
    public sealed partial class DurableCaptureProcessingTests
    {
        [TestMethod]
        [TestCategory("Manual")]
        public async Task Issue587_MetadataRetryCosts()
        {
            var output = Issue587RetryCostEvidence.Output("metadata");
            var root = Path.Combine(output, "work");
            Directory.CreateDirectory(root);
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var step = new MetadataProducingStep();
            var result = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item,
                new CaptureProcessingGraph([CreateMetadataNode(step)]), persistence, telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
            Assert.IsNotNull(step.Product);
            var destination = Path.Combine(root, "retry");
            async Task CopyAsync(int _)
            {
                var copied = await CaptureProcessingPersistence.CopyMetadataProductAsync(destination,
                    fixture.Manifest.Descriptor, "metadata", step.Product, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(step.Product.OutputIdentitySha256, copied.OutputIdentitySha256);
            }
            await CopyAsync(0).ConfigureAwait(false);
            var before = Issue587RetryCostEvidence.Hashes(destination);
            var metrics = await Issue587RetryCostEvidence.MeasureAsync(CopyAsync).ConfigureAwait(false);
            CollectionAssert.AreEqual(before, Issue587RetryCostEvidence.Hashes(destination));
            await Issue587RetryCostEvidence.SaveAsync(output, new { metrics, hashes = before,
                step.Product.OutputIdentitySha256, step.Product.ChecksumSha256 }).ConfigureAwait(false);
        }
    }
}

namespace HVO.SkyMonitor.CameraAgent.Tests
{
    public sealed partial class ProjectedSceneStagingStoreTests
    {
        internal static async Task MeasureStagingRetryCostsAsync()
        {
            var output = Issue587RetryCostEvidence.Output("staging");
            var root = Path.Combine(output, "work");
            Directory.CreateDirectory(root);
            using var store = CreateStore(root);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            static string Key(int index) => index.ToString("X64", CultureInfo.InvariantCulture);
            var publication = await Issue587RetryCostEvidence.MeasureAsync(async index =>
                await store.StageAsync(Key(index), new string('A', 64), scene, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var before = Issue587RetryCostEvidence.Hashes(root);
            var completion = await Issue587RetryCostEvidence.MeasureAsync(async index =>
                await store.MarkCompletedAsync(Key(index), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var retry = await Issue587RetryCostEvidence.MeasureAsync(async index =>
                await store.MarkCompletedAsync(Key(index), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var deletion = await Issue587RetryCostEvidence.MeasureAsync(async index =>
                await store.DeleteCompletedAsync(Key(index), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var absentRetry = await Issue587RetryCostEvidence.MeasureAsync(async index =>
                await store.DeleteCompletedAsync(Key(index), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsEmpty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            await Issue587RetryCostEvidence.SaveAsync(output, new { publication, completion, retry, deletion, absentRetry, hashes = before }).ConfigureAwait(false);
        }
    }

    [TestClass]
    [TestCategory("Manual")]
    public sealed class Issue587StagingCostTests
    {
        [TestMethod]
        public async Task Issue587_StagingPublicationRetryCosts()
            => await ProjectedSceneStagingStoreTests.MeasureStagingRetryCostsAsync().ConfigureAwait(false);
    }

    internal static class Issue587RetryCostEvidence
    {
        internal static string Output(string name)
        {
            var root = Environment.GetEnvironmentVariable("HVO_ISSUE587_RETRY_COST_OUTPUT");
            if (string.IsNullOrEmpty(root)) Assert.Inconclusive("Requires an isolated HVO_ISSUE587_RETRY_COST_OUTPUT.");
            var path = Path.Combine(root, name);
            Assert.IsFalse(Directory.Exists(path));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static async Task<object> MeasureAsync(Func<int, Task> operation)
        {
            for (var i = 0; i < 5; i++) await operation(i).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var rss = Environment.WorkingSet;
            var loh = Loh();
            var io = Io();
            var samples = new double[30];
            for (var i = 0; i < samples.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await operation(i + 5).ConfigureAwait(false);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            return new { SamplesMilliseconds = samples, CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, RssBeforeBytes = rss, RssAfterBytes = Environment.WorkingSet,
                LohBeforeBytes = loh, LohAfterBytes = Loh(), IoBefore = io, IoAfter = Io(), Warmups = 5, Operations = 30, Concurrency = 1 };
        }
        private static long Loh() => GC.GetGCMemoryInfo().GenerationInfo.Length > 3 ? GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes : 0;
        private static string Io() => OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/io") : "unavailable";
        internal static string[] Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        internal static async Task SaveAsync(string output, object measurement)
            => await File.WriteAllTextAsync(Path.Combine(output, "costs.json"), JsonSerializer.Serialize(new {
                Revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Environment.ProcessorCount, measurement
            })).ConfigureAwait(false);
    }
}
