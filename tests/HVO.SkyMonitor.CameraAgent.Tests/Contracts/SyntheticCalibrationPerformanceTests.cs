using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class SyntheticCalibrationPerformanceTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W1W2ReferencePublication_RecordIoCpuMemoryAndRestartCost()
    {
        var measurements = new[]
        {
            await MeasureAsync("W1", 1936, 1216, CameraPixelFormat.Mono16).ConfigureAwait(false),
            await MeasureAsync("W2", 3096, 2080, CameraPixelFormat.BayerRggb16).ConfigureAwait(false)
        };
        var evidence = new
        {
            Revision = "candidate-working-tree",
            BaselineRevision = "f9fa50b63b06cb4092a16fc4e1afa69390e4c189",
            Environment = new
            {
                OperatingSystem = Environment.OSVersion.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount
            },
            Workloads = measurements,
            Scope = "Cold publication, in-memory cache lookup, and restart validation of one immutable synthetic reference bundle",
            Backlog = "N/A: immutable references are generated synchronously once per configured profile",
            RawPayloadCopies = "N/A: this reference-publication harness does not render or ingest light frames"
        };
        var output = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-195");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, "synthetic-reference-performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions))
            .ConfigureAwait(false);
    }

    private static async Task<object> MeasureAsync(
        string workload,
        int width,
        int height,
        CameraPixelFormat format)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration-performance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[checked(width * height * 2)];
            var manifest = ReconstructableCaptureContractTests.CreateManifest(format, width, height, width * 2, payload);
            var model = new SyntheticCalibrationModelV1
            {
                Gain = 99.5,
                TemperatureC = -9.5,
                Defects = [new SyntheticCalibrationDefect(width / 2, height / 2)]
            };
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var loader = new CameraAgentClearReferenceLoader(options);
            var store = new SyntheticCalibrationReferenceStore(options, loader);
            var descriptor = manifest.Descriptor with
            {
                Profiles = manifest.Descriptor.Profiles with
                {
                    Calibration = new ProfileIdentityDescriptor(
                        "synthetic-calibration-model",
                        model.SchemaVersion,
                        SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
                }
            };
            var process = Process.GetCurrentProcess();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var cpuBefore = process.TotalProcessorTime;
            var rssBefore = Environment.WorkingSet;
            var started = Stopwatch.GetTimestamp();
            var first = await store.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false);
            var initializeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var initializeCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var initializeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var rssAfter = Environment.WorkingSet;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
            var persistedBytes = files.Sum(static path => new FileInfo(path).Length);
            Assert.HasCount(10, files);
            Assert.HasCount(4, first.References);

            started = Stopwatch.GetTimestamp();
            var cached = await store.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false);
            var cachedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.AreSame(first, cached);

            var restartedLoader = new CameraAgentClearReferenceLoader(options);
            var restartedStore = new SyntheticCalibrationReferenceStore(options, restartedLoader);
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            cpuBefore = process.TotalProcessorTime;
            started = Stopwatch.GetTimestamp();
            var restarted = await restartedStore.GetOrCreateAsync(
                descriptor, model, CancellationToken.None).ConfigureAwait(false);
            var restartMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var restartCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var restartAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.AreEqual(first.ProfileIdentitySha256, restarted.ProfileIdentitySha256);

            return new
            {
                Workload = workload,
                Width = width,
                Height = height,
                PixelFormat = format.ToString(),
                ReferencePayloadBytes = checked((long)payload.Length * 4),
                PersistedBytes = persistedBytes,
                InitialPublication = new
                {
                    LatencyMilliseconds = initializeMilliseconds,
                    CpuMilliseconds = initializeCpuMilliseconds,
                    AllocatedBytes = initializeAllocatedBytes,
                    RssBeforeBytes = rssBefore,
                    RssAfterBytes = rssAfter,
                    FileWrites = 10,
                    AtomicRenames = 10,
                    FilesHashed = 20
                },
                CachedLookup = new
                {
                    LatencyMilliseconds = cachedMilliseconds,
                    FilesHashed = 10,
                    MetadataValidations = 10,
                    FileWrites = 0
                },
                RestartValidation = new
                {
                    LatencyMilliseconds = restartMilliseconds,
                    CpuMilliseconds = restartCpuMilliseconds,
                    AllocatedBytes = restartAllocatedBytes,
                    FilesHashed = 30,
                    MetadataValidations = 10,
                    FileWrites = 0
                },
                ProfileIdentitySha256 = first.ProfileIdentitySha256,
                ReferenceChecksums = first.Profile.References.Select(static reference => reference.PayloadSha256).ToArray(),
                Complexity = "O(width*height)",
                GeneratedAndRetainedFullFrameBuffers = 4,
                AdditionalFullFrameCopies = 0
            };
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root could not be located.");
    }
}
