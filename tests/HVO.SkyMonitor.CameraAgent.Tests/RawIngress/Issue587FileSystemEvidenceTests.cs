using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressPerformanceTests
{
    private static readonly JsonSerializerOptions FixtureOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    [TestMethod]
    public async Task Issue587_EquivalentFixtureAndDurabilityCosts()
    {
        var mode = Environment.GetEnvironmentVariable("HVO_ISSUE587_MODE");
        if (mode is not ("fixture" or "measure")) Assert.Inconclusive("Set HVO_ISSUE587_MODE=fixture|measure with isolated fixture/output roots.");
        var selectedStage = Environment.GetEnvironmentVariable("HVO_ISSUE587_STAGE") ?? "all";
        Assert.IsTrue(selectedStage is "all" or "derivative" or "durability", "Choose all, derivative, or affected durability stages.");
        var fixtureRoot = RequiredEvidenceDirectory("HVO_ISSUE587_FIXTURE");
        var output = RequiredEvidenceDirectory("HVO_ISSUE587_OUTPUT");
        Directory.CreateDirectory(output);
        if (mode == "fixture")
        {
            Assert.IsFalse(Directory.Exists(fixtureRoot), "Freeze once on the exact #1055 baseline; never overwrite the shared fixture.");
            Directory.CreateDirectory(fixtureRoot);
            var configuration = await LoadCanonicalConfigurationAsync().ConfigureAwait(false);
            using var staging = new ProjectedSceneStagingStore(Options.Create(new CameraAgentHostOptions { RawIngressRoot = Path.Combine(fixtureRoot, "scene") }));
            var input = await RenderCanonicalW2InputAsync(configuration, staging).ConfigureAwait(false);
            Assert.IsNotNull(input.Metadata.Scene);
            await File.WriteAllBytesAsync(Path.Combine(fixtureRoot, "payload.bin"), input.Payload).ConfigureAwait(false);
            await SaveFixtureJsonAsync(fixtureRoot, "metadata", input.Metadata).ConfigureAwait(false);
            await SaveFixtureJsonAsync(fixtureRoot, "configuration", configuration).ConfigureAwait(false);
            await SaveFixtureJsonAsync(fixtureRoot, "location", configuration.DeploymentLocation).ConfigureAwait(false);
            await SaveFixtureJsonAsync(output, "fixture-hashes", await HashTreeAsync(fixtureRoot).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }
        var configurationBytes = await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "configuration.json")).ConfigureAwait(false);
        var configurationFromFixture = JsonSerializer.Deserialize<CameraModuleConfig>(configurationBytes, FixtureOptions)!;
        var location = JsonSerializer.Deserialize<DeploymentLocationSnapshot>(await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "location.json")).ConfigureAwait(false), FixtureOptions);
        configurationFromFixture = configurationFromFixture with { DeploymentLocation = location };
        var frozenInput = new W2Input(
            await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "payload.bin")).ConfigureAwait(false),
            JsonSerializer.Deserialize<FrameMetadata>(await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "metadata.json")).ConfigureAwait(false), FixtureOptions)!, FrozenExposure: true);
        Assert.AreEqual(12_879_360, frozenInput.Payload.Length);
        var work = Path.Combine(output, "work");
        Assert.IsFalse(Directory.Exists(work), "Each trial requires a new output root.");
        Directory.CreateDirectory(work);
        var clock = new EvidenceClock();
        var stages = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            if (selectedStage != "derivative")
            {
                var io = ReadIssue587Io();
                var w2 = await MeasureW2Async(Path.Combine(work, "w2"), frozenInput, configurationFromFixture, clock).ConfigureAwait(false);
                stages.Add("w2", new { Measurement = w2, IoDelta = IoDelta(io), Hashes = await HashTreeAsync(Path.Combine(work, "w2"), excludeSqlite: true).ConfigureAwait(false) });
                io = ReadIssue587Io();
                if (selectedStage == "all")
                {
                    var w3m = await MeasureW3MetadataAsync(Path.Combine(work, "w3m"), frozenInput, configurationFromFixture).ConfigureAwait(false);
                    stages.Add("w3m", new { Measurement = w3m, IoDelta = IoDelta(io) });
                }
                io = ReadIssue587Io();
                var w3p = await MeasureW3PayloadAsync(Path.Combine(work, "w3p"), frozenInput, configurationFromFixture, clock).ConfigureAwait(false);
                stages.Add("w3p", new { Measurement = w3p, IoDelta = IoDelta(io), Hashes = await HashTreeAsync(Path.Combine(work, "w3p"), excludeSqlite: true).ConfigureAwait(false) });
                var generatedSidecar = await File.ReadAllBytesAsync(Directory.EnumerateFiles(Path.Combine(work, "w2", "frames"), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First()).ConfigureAwait(false);
                var publicationFixture = Path.Combine(fixtureRoot, "publication-sidecar.json");
                if (!File.Exists(publicationFixture))
                {
                    Assert.AreEqual("71e3656aaf6a356e75c8481435bfef55a84c78fd", Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"));
                    await File.WriteAllBytesAsync(publicationFixture, generatedSidecar).ConfigureAwait(false);
                }
                var frozenSidecar = await File.ReadAllBytesAsync(publicationFixture).ConfigureAwait(false);
                CollectionAssert.AreEqual(frozenSidecar, generatedSidecar, "Publication isolation requires exact baseline sidecar bytes.");
                stages.Add("publication", await MeasurePublicationAsync(Path.Combine(work, "publication"), frozenInput, frozenSidecar).ConfigureAwait(false));
            }
            stages.Add("derivative", await MeasureDerivativeAsync(Path.Combine(work, "derivative"), frozenInput, configurationFromFixture).ConfigureAwait(false));
            await SaveFixtureJsonAsync(output, "evidence", new
            {
                SelectedStage = selectedStage,
                ProductionRevision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
                FixtureHashes = await HashTreeAsync(fixtureRoot).ConfigureAwait(false),
                HarnessSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(GetRepositoryRoot(), "tests/HVO.SkyMonitor.CameraAgent.Tests/RawIngress/Issue587FileSystemEvidenceTests.cs")).ConfigureAwait(false))),
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount,
                Workload = new { Width = W2Width, Height = W2Height, Bytes = frozenInput.Payload.Length, Seed = 2025, WarmupCount, MeasuredCount, Concurrency = 1, ArrivalPerSecond = .04, MinimumDrainPerSecond = 1, W3MetadataCount, W3PayloadCount },
                Stages = stages
            }).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<object> MeasurePublicationAsync(string root, W2Input input, byte[] sidecar)
    {
        var flushes = 0;
        var syncs = 0;
        var events = new PublicationEvents();
        var store = new RawIngressFileStore(root, events, () => flushes++, () => syncs++);
        async Task PublishAsync(int index)
        {
            var paths = store.GetPaths(DateTimeOffset.UnixEpoch.AddSeconds(index), new Guid(index + 1, 0, 0, new byte[8]));
            await store.PublishPayloadAsync(paths, input.Payload, CancellationToken.None).ConfigureAwait(false);
            await store.PublishSidecarAsync(paths, sidecar, CancellationToken.None).ConfigureAwait(false);
        }
        for (var index = 0; index < WarmupCount; index++) await PublishAsync(index).ConfigureAwait(false);
        flushes = syncs = events.Renames = 0;
        var io = ReadIssue587Io();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var rss = Environment.WorkingSet;
        var loh = GetLohSize();
        var samples = new double[MeasuredCount];
        for (var index = 0; index < MeasuredCount; index++)
        {
            var start = Stopwatch.GetTimestamp();
            await PublishAsync(index + WarmupCount).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        var metrics = new
        {
            SamplesMilliseconds = samples,
            CpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
            RssBeforeBytes = rss,
            RssAfterBytes = Environment.WorkingSet,
            LohBeforeBytes = loh,
            LohAfterBytes = GetLohSize(),
            FileFlushes = flushes,
            DirectorySyncs = syncs,
            events.Renames,
            IoDelta = IoDelta(io)
        };
        Assert.AreEqual(2 * MeasuredCount, flushes);
        Assert.AreEqual(2 * MeasuredCount, syncs);
        Assert.AreEqual(2 * MeasuredCount, events.Renames);
        foreach (var path in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
            CollectionAssert.AreEqual(input.Payload, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        return new { Metrics = metrics, Hashes = await HashTreeAsync(root).ConfigureAwait(false) };
    }

    private static async Task<object> MeasureDerivativeAsync(string root, W2Input input, CameraModuleConfig configuration)
    {
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var checksum = PayloadChecksum.ComputeSha256(input.Payload);
        var evidence = Enumerable.Range(0, WarmupCount + MeasuredCount).Select(index =>
        {
            var submission = CreateSubmission(index, input);
            var ids = RawCaptureDescriptorFactory.CreateStableIds(configuration, submission);
            var raw = RawCaptureDescriptorFactory.Create(configuration, submission,
                new RawCaptureIdentity(configuration.AgentId!, index + 1, ids.CaptureId, ids.ArtifactId), checksum, new EvidenceClock().GetUtcNow());
            var artifactId = new Guid(index + 1, 2, 0, new byte[8]);
            var recipe = RecipeIdentityDescriptor.Create("fixture-identity-calibration", "1.0.0", "fixture-pass-through-v1", JsonSerializer.SerializeToElement(new { }));
            var descriptor = raw with
            {
                Artifact = raw.Artifact with
                {
                    ArtifactId = artifactId,
                    Role = FrameArtifactRole.Calibrated,
                    Variant = "calibrated",
                    SourceArtifactIds = [ids.ArtifactId],
                    Recipe = recipe
                }
            };
            Assert.IsTrue(descriptor.Validate().IsValid, descriptor.Validate().FieldPath);
            return (Artifact: new FrameArtifact(artifactId, FrameArtifactRole.Calibrated, submission.Result.Frame!, [ids.ArtifactId], recipe.SemanticVersion), Descriptor: descriptor);
        }).ToArray();
        for (var index = 0; index < WarmupCount; index++)
            _ = await storage.SaveAsync(root, evidence[index].Artifact, evidence[index].Descriptor, "fixture-calibration", CancellationToken.None).ConfigureAwait(false);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var rss = Environment.WorkingSet;
        var loh = GetLohSize();
        var io = ReadIssue587Io();
        var samples = new double[MeasuredCount];
        for (var index = 0; index < MeasuredCount; index++)
        {
            var value = evidence[index + WarmupCount];
            var start = Stopwatch.GetTimestamp();
            _ = await storage.SaveAsync(root, value.Artifact, value.Descriptor, "fixture-calibration", CancellationToken.None).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        var metrics = new
        {
            SamplesMilliseconds = samples,
            CpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
            RssBeforeBytes = rss,
            RssAfterBytes = Environment.WorkingSet,
            LohBeforeBytes = loh,
            LohAfterBytes = GetLohSize(),
            IoDelta = IoDelta(io)
        };
        var hashes = await HashTreeAsync(root).ConfigureAwait(false);
        // Reopen the service and repeat every write: payload/sidecar/index bytes must not change.
        using var restarted = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        foreach (var value in evidence)
        {
            var saved = await restarted.SaveAsync(root, value.Artifact, value.Descriptor, "fixture-calibration", CancellationToken.None).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(Path.ChangeExtension(saved.AbsolutePath, ".json")).ConfigureAwait(false));
            Assert.IsTrue(parsed.IsValid, parsed.Validation.FieldPath);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(value.Descriptor), CaptureContractJson.ComputeDescriptorSha256(parsed.Document!.Manifest.Descriptor));
            Assert.AreEqual("fixture-calibration", parsed.Document.Manifest.ProducerStepId);
        }
        CollectionAssert.AreEqual(hashes.ToArray(), (await HashTreeAsync(root).ConfigureAwait(false)).ToArray());
        return new { Metrics = metrics, Hashes = hashes, RestartReplayBytesUnchanged = true };
    }

    private static string RequiredEvidenceDirectory(string variable)
        => Path.GetFullPath(Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"Missing {variable}."));

    private static Task SaveFixtureJsonAsync<T>(string root, string name, T value)
        => File.WriteAllTextAsync(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value, FixtureOptions));

    private static async Task<SortedDictionary<string, string>> HashTreeAsync(string root, bool excludeSqlite = false)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (excludeSqlite && file.Contains("raw-ingress.db", StringComparison.Ordinal)) continue;
            using var stream = File.OpenRead(file);
            hashes.Add(Path.GetRelativePath(root, file), Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)));
        }
        return hashes;
    }

    private static Dictionary<string, long> ReadIssue587Io()
        => File.ReadAllLines("/proc/self/io").Select(static line => line.Split(':', 2))
            .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture), StringComparer.Ordinal);

    private static Dictionary<string, long> IoDelta(Dictionary<string, long> before)
        => ReadIssue587Io().ToDictionary(static entry => entry.Key, entry => entry.Value - before[entry.Key], StringComparer.Ordinal);

    private sealed class EvidenceClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class PublicationEvents : IRawIngressFaultInjector
    {
        public int Renames { get; set; }
        public bool IsEnabled(RawIngressFaultPoint point) => false;
        public void Inject(RawIngressFaultPoint point)
        {
            if (point is RawIngressFaultPoint.PayloadPublished or RawIngressFaultPoint.SidecarPublished) Renames++;
        }
    }
}
