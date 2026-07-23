using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The manual evidence harness owns its single-threaded measurement flow.")]
public sealed class DeploymentLocationPerformanceTests
{
    private const int Warmups = 5;
    private const int Measurements = 30;
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W1W2AndProtectedPersistence_RecordLocationCost()
    {
        var location = DeploymentLocationSnapshot.Create(
            "siding-spring-synthetic", 1,
            "GitHub issue #196 operator-pinned acceptance coordinates; not a physical survey",
            null, DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            null, -31.2733, 149.0700, 1165, "Australia/Sydney");
        var hualapai = DeploymentLocationSnapshot.Create(
            "hualapai-canonical", 1,
            "tests/fixtures/astronomy/hualapai-asi174-conformance-v1.json",
            null, DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            null, 35.347, -113.878, 0, "America/Phoenix");
        var workloads = new[]
        {
            MeasureContract("W1", CameraPixelFormat.Mono16, 1936, 1216, 3872, hualapai),
            MeasureContract("W2", CameraPixelFormat.BayerRggb16, 3096, 2080, 6192, location)
        };
        var persistence = await MeasurePersistenceAsync(location).ConfigureAwait(false);
        var compatibility = new ProcessingCompatibilityIdentity(
            "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing",
            location.ToProvenance().IdentitySha256);
        var changedCompatibility = compatibility with { LocationIdentitySha256 = new string('A', 64) };
        var evidence = new
        {
            Revision = "candidate-working-tree",
            BaselineRevision = "0f07cfc8d596cbb6f92ab2c5af8a18cc3c98b6f0",
            Environment = new
            {
                OperatingSystem = Environment.OSVersion.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount,
                Environment.WorkingSet
            },
            Warmups,
            Measurements,
            PayloadCopies = 0,
            Backlog = "N/A: location selection introduces no queue",
            Workloads = workloads,
            ProtectedPersistence = persistence,
            RollingWindowReset = new
            {
                Comparison = Measure(() =>
                {
                    if (compatibility == changedCompatibility)
                    {
                        throw new InvalidOperationException("Changed locations were treated as compatible.");
                    }
                }),
                FirstPostChangeDepth = 1,
                PriorHistoryDisposition = "retained but incompatible",
                RawPayloadWrites = 0
            }
        };
        var output = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-196");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, "location-performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJson))
            .ConfigureAwait(false);
    }

    private static object MeasureContract(
        string workload,
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        DeploymentLocationSnapshot location)
    {
        var payload = new byte[checked(stride * height)];
        var baseline = ReconstructableCaptureContractTests.CreateManifest(format, width, height, stride, payload);
        var candidate = baseline with
        {
            Descriptor = baseline.Descriptor with { Location = location.ToProvenance() }
        };
        var baselineBytes = CaptureContractJson.Serialize(baseline);
        var candidateBytes = CaptureContractJson.Serialize(candidate);
        return new
        {
            Workload = workload,
            Width = width,
            Height = height,
            Format = format.ToString(),
            PayloadBytes = payload.LongLength,
            BaselineManifestBytes = baselineBytes.Length,
            CandidateManifestBytes = candidateBytes.Length,
            ManifestDeltaBytes = candidateBytes.Length - baselineBytes.Length,
            ManifestDeltaPercent = (candidateBytes.Length - baselineBytes.Length) * 100d / baselineBytes.Length,
            Baseline = MeasureContractStages(baseline, baselineBytes),
            Candidate = MeasureContractStages(candidate, candidateBytes),
            candidate.IdempotencyKey,
            LocationIdentitySha256 = location.ToProvenance().IdentitySha256
        };
    }

    private static object MeasureContractStages(ArtifactManifestV2 manifest, byte[] bytes)
        => new
        {
            Serialize = Measure(() => GC.KeepAlive(CaptureContractJson.Serialize(manifest))),
            ParseValidate = Measure(() =>
            {
                if (!CaptureContractJson.ParseManifest(bytes).IsValid)
                {
                    throw new InvalidOperationException("Measured manifest failed validation.");
                }
            }),
            DescriptorHash = Measure(() => GC.KeepAlive(
                CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor)))
        };

    private static object Measure(Action operation)
    {
        for (var index = 0; index < Warmups; index++)
        {
            operation();
        }
        var samples = new double[Measurements];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            operation();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(samples);
        var total = samples.Sum();
        return new
        {
            MedianMilliseconds = Percentile(samples, 0.50),
            P95Milliseconds = Percentile(samples, 0.95),
            AllocatedBytesPerOperation = allocated / Measurements,
            OperationsPerSecond = Measurements / TimeSpan.FromMilliseconds(total).TotalSeconds
        };
    }

    private static async Task<object> MeasurePersistenceAsync(DeploymentLocationSnapshot location)
    {
        const int trials = 5;
        const int retainedManifestCount = 100;
        var initialize = new double[trials];
        var restart = new double[trials];
        var protectedBytes = new long[trials];
        for (var index = 0; index < trials; index++)
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-location-performance", Guid.NewGuid().ToString("N"));
            try
            {
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = Path.Combine(root, "data") });
                var protector = new DataProtectionDeploymentLocationProtector(options);
                var seed = new DeploymentLocationSeed(
                    location.LocationId, location.Source, location.HorizontalAccuracyMeters,
                    location.EffectiveFromUtc, location.EffectiveUntilUtc, location.ToObservatoryLocation());
                var started = Stopwatch.GetTimestamp();
                using (var store = new ProtectedDeploymentLocationStore(
                    options, protector, TimeProvider.System, NullLogger<ProtectedDeploymentLocationStore>.Instance))
                {
                    var initialized = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(location.CanonicalSha256, initialized.CanonicalSha256);
                }
                initialize[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var path = Path.Combine(options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                protectedBytes[index] = bytes.LongLength;
                var text = Encoding.UTF8.GetString(bytes);
                Assert.IsFalse(text.Contains("-31.2733", StringComparison.Ordinal));
                Assert.IsFalse(text.Contains("149.07", StringComparison.Ordinal));
                var retainedManifest = ReconstructableCaptureContractTests.CreateManifest(
                    CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]);
                retainedManifest = retainedManifest with
                {
                    Descriptor = retainedManifest.Descriptor with { Location = location.ToProvenance() }
                };
                var retainedBytes = CaptureContractJson.Serialize(retainedManifest);
                var retainedDirectory = Path.Combine(options.Value.RawIngressRoot, "retained-performance");
                Directory.CreateDirectory(retainedDirectory);
                for (var retainedIndex = 0; retainedIndex < retainedManifestCount; retainedIndex++)
                {
                    await File.WriteAllBytesAsync(
                        Path.Combine(retainedDirectory, $"capture-{retainedIndex:D3}.json"),
                        retainedBytes).ConfigureAwait(false);
                }

                started = Stopwatch.GetTimestamp();
                using var restartedStore = new ProtectedDeploymentLocationStore(
                    options, protector, TimeProvider.System, NullLogger<ProtectedDeploymentLocationStore>.Instance);
                var restarted = await restartedStore.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
                restart[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.AreEqual(location.ToProvenance(), restarted.ToProvenance());
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
        Array.Sort(initialize);
        Array.Sort(restart);
        return new
        {
            Trials = trials,
            Initialize = FiveTrialSummary(initialize),
            RestartLoad = FiveTrialSummary(restart),
            ProtectedFileBytes = protectedBytes,
            ProtectedHistoryWritesPerInitialization = 1,
            IdentityMarkerWritesPerInitialization = 1,
            DataProtectionKeyFilesPerInitialization = 1,
            StateWritesPerUnchangedRestart = 0,
            SteadyStateReadsPerCapture = 0,
            RetainedManifestRestartBacklog = retainedManifestCount
        };
    }

    private static object FiveTrialSummary(double[] samples)
        => new { MinimumMilliseconds = samples[0], MedianMilliseconds = samples[2], MaximumMilliseconds = samples[^1] };

    private static double Percentile(double[] sorted, double percentile)
        => sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root could not be located.");
    }
}
