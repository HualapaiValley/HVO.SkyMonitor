using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TwoStandaloneCameraAgentDockerAcceptanceTests
{
    private const string ExpectedCatalogSha256 = "B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2";
    private static readonly int WarmupCaptureCount = ReadPositiveInteger("HVO_ISSUE_197_WARMUP_CAPTURES", 5, 1);
    private static readonly int MeasuredCaptureCount = ReadPositiveInteger("HVO_ISSUE_197_MEASURED_CAPTURES", 30, 3);
    private static readonly FrameArtifactRole[] ExpectedRoles =
    [
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview
    ];
    private static readonly string[] ExpectedProcessingNodes =
    [
        "Calibration", "RollingCombination", "CalibratedPreview", "Preview", "Annotation", "LocalStorage", "Telemetry"
    ];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task TwoIsolatedAgentsRemainIndependentAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Issue #197 requires Linux process and filesystem evidence.");
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_ISSUE_197_HUALAPAI_BASE_URI")))
        {
            Assert.Inconclusive("Run through scripts/test:cameraagent-dual-197.");
        }

        var evidenceRoot = RequiredPath("HVO_ISSUE_197_EVIDENCE_ROOT");
        Directory.CreateDirectory(evidenceRoot);
        var hualapai = AgentContext.Create(
            "hualapai",
            "HVO_ISSUE_197_HUALAPAI",
            "cameraagent-hualapai-197",
            "hualapai-cameraagent-standalone-full",
            "issue-171-operator-pinned-smoke-configuration",
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
            "CameraAgent.Hualapai197.Auth");
        var siding = AgentContext.Create(
            "siding-spring",
            "HVO_ISSUE_197_SIDING",
            "cameraagent-siding-spring-197",
            "siding-spring-synthetic",
            "GitHub issue #196 operator-pinned acceptance coordinates; not a physical survey",
            new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero),
            "CameraAgent.SidingSpring197.Auth");
        var expectedImage = Required("HVO_ISSUE_197_CAMERA_IMAGE");
        var started = Stopwatch.StartNew();
        using var sampler = new ContainerResourceSampler(hualapai, siding);
        sampler.Start();

        await WaitForHealthyAsync(hualapai, sampler).ConfigureAwait(false);
        await WaitForHealthyAsync(siding, sampler).ConfigureAwait(false);
        var topology = await AssertTopologyAsync(hualapai, siding, expectedImage).ConfigureAwait(false);

        using var hualapaiSession = await LoginAsync(hualapai).ConfigureAwait(false);
        using var sidingSession = await LoginAsync(siding).ConfigureAwait(false);
        await AssertForeignCookieRejectedAsync(hualapaiSession, siding).ConfigureAwait(false);
        await AssertForeignCookieRejectedAsync(sidingSession, hualapai).ConfigureAwait(false);
        var hualapaiOwnerId = ReadOwnerId(hualapai);
        var sidingOwnerId = ReadOwnerId(siding);
        Assert.AreNotEqual(hualapaiOwnerId, sidingOwnerId);

        var warmups = await Task.WhenAll(
            WaitForCompleteCapturesAsync(hualapai, hualapaiSession.Client, WarmupCaptureCount, 0, sampler),
            WaitForCompleteCapturesAsync(siding, sidingSession.Client, WarmupCaptureCount, 0, sampler)).ConfigureAwait(false);
        var runtimeBeforeMeasured = await Task.WhenAll(
            CaptureRuntimeMetricsAsync(hualapaiSession.Client),
            CaptureRuntimeMetricsAsync(sidingSession.Client)).ConfigureAwait(false);
        sampler.SetPhase("measured-workload");
        var measuredWorkload = Stopwatch.StartNew();
        var measured = await Task.WhenAll(
            WaitForCompleteCapturesAsync(hualapai, hualapaiSession.Client, MeasuredCaptureCount, warmups[0][^1].CaptureSequence, sampler),
            WaitForCompleteCapturesAsync(siding, sidingSession.Client, MeasuredCaptureCount, warmups[1][^1].CaptureSequence, sampler)).ConfigureAwait(false);
        measuredWorkload.Stop();
        var hualapaiCaptures = measured[0];
        var sidingCaptures = measured[1];
        var runtimeAfterMeasured = await Task.WhenAll(
            CaptureRuntimeMetricsAsync(hualapaiSession.Client),
            CaptureRuntimeMetricsAsync(sidingSession.Client)).ConfigureAwait(false);
        var hualapaiMeasuredRuntime = RuntimeWorkloadEvidence.Create(
            runtimeBeforeMeasured[0], runtimeAfterMeasured[0], measuredWorkload.Elapsed.TotalMilliseconds);
        var sidingMeasuredRuntime = RuntimeWorkloadEvidence.Create(
            runtimeBeforeMeasured[1], runtimeAfterMeasured[1], measuredWorkload.Elapsed.TotalMilliseconds);
        sampler.SetPhase("crash-recovery");
        var hualapaiEvidence = await AssertAgentEvidenceAsync(hualapai, hualapaiSession.Client, hualapaiCaptures, evidenceRoot).ConfigureAwait(false);
        var sidingEvidence = await AssertAgentEvidenceAsync(siding, sidingSession.Client, sidingCaptures, evidenceRoot).ConfigureAwait(false);
        Assert.AreNotEqual(hualapaiEvidence.JpegSha256, sidingEvidence.JpegSha256);
        Assert.AreNotEqual(hualapaiEvidence.CatalogInode, sidingEvidence.CatalogInode);
        Assert.AreNotEqual(hualapaiEvidence.LocationIdentitySha256, sidingEvidence.LocationIdentitySha256);
        CollectionAssert.AreEquivalent(
            hualapaiEvidence.RecipeIdentities.Keys.ToArray(),
            sidingEvidence.RecipeIdentities.Keys.ToArray());
        Assert.IsTrue(hualapaiEvidence.RecipeIdentities.Any(recipe =>
            !string.Equals(recipe.Value, sidingEvidence.RecipeIdentities[recipe.Key], StringComparison.OrdinalIgnoreCase)));
        Assert.IsEmpty(hualapaiEvidence.MutableInodes.Intersect(sidingEvidence.MutableInodes).ToArray());
        Assert.IsEmpty(
            hualapaiCaptures.Select(static capture => capture.CaptureId)
                .Intersect(sidingCaptures.Select(static capture => capture.CaptureId)).ToArray());
        Assert.IsEmpty(
            hualapaiCaptures.SelectMany(static capture => capture.Artifacts).Select(static artifact => artifact.ArtifactId)
                .Intersect(sidingCaptures.SelectMany(static capture => capture.Artifacts).Select(static artifact => artifact.ArtifactId)).ToArray());

        var hualapaiChecksums = CaptureChecksums(hualapai, hualapaiCaptures);
        var sidingChecksums = CaptureChecksums(siding, sidingCaptures);
        var restartEvidence = await AssertIndependentCrashRecoveryAsync(
            hualapai,
            hualapaiSession.Client,
            hualapaiChecksums,
            siding,
            sidingSession.Client,
            sidingChecksums,
            sampler).ConfigureAwait(false);
        sampler.SetPhase("pause-isolation");
        var faultEvidence = await AssertPauseIsolationAsync(
            hualapai,
            hualapaiSession.Client,
            siding,
            sidingSession.Client,
            sampler).ConfigureAwait(false);

        var hualapaiMetrics = await CaptureMetricsAsync(hualapai, evidenceRoot).ConfigureAwait(false);
        var sidingMetrics = await CaptureMetricsAsync(siding, evidenceRoot).ConfigureAwait(false);
        AssertMetricSurface(hualapaiMetrics);
        AssertMetricSurface(sidingMetrics);
        var hualapaiRuntimeMetrics = ReadRuntimeMetrics(hualapaiMetrics);
        var sidingRuntimeMetrics = ReadRuntimeMetrics(sidingMetrics);
        sampler.SetPhase("final-pre-stop");
        await sampler.StopAsync().ConfigureAwait(false);
        await sampler.CaptureAsync().ConfigureAwait(false);

        var finalDrain = Stopwatch.StartNew();
        await DockerAsync("stop", "--timeout", "45", hualapai.Container, siding.Container).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var finalHualapai = ReadDurableSnapshot(hualapai);
        var finalSiding = ReadDurableSnapshot(siding);
        var hualapaiRetainedFilesystemBytes = DirectoryBytes(hualapai.RuntimeRoot);
        var sidingRetainedFilesystemBytes = DirectoryBytes(siding.RuntimeRoot);
        AssertDrained(finalHualapai, hualapai.Name);
        AssertDrained(finalSiding, siding.Name);
        finalDrain.Stop();
        AssertChecksums(hualapai, hualapaiChecksums);
        AssertChecksums(siding, sidingChecksums);
        await AssertTelemetryEvidenceAsync(hualapai).ConfigureAwait(false);
        await AssertTelemetryEvidenceAsync(siding).ConfigureAwait(false);
        var totalCentralTrafficAttempts = await CountCentralTrafficAttemptsAsync(hualapai, siding).ConfigureAwait(false);
        var centralTrafficAttempts = totalCentralTrafficAttempts - topology.CanaryProbeAttempts;
        Assert.AreEqual(0, centralTrafficAttempts);

        var resourceEvidence = sampler.Summarize();
        var environmentEvidence = new
        {
            RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            cpuModel = ReadProcValue("/proc/cpuinfo", "model name"),
            hostMemoryBytes = ReadProcKilobytes("/proc/meminfo", "MemTotal") * 1024,
            kernelRelease = (await ProcessAsync("uname", ["-r"]).ConfigureAwait(false)).Trim(),
            evidenceStorageType = (await ProcessAsync("stat", ["--file-system", "--format=%T", evidenceRoot]).ConfigureAwait(false)).Trim(),
            hualapaiRuntimeStorageType = (await ProcessAsync(
                "stat", ["--file-system", "--format=%T", hualapai.RuntimeRoot]).ConfigureAwait(false)).Trim(),
            sidingRuntimeStorageType = (await ProcessAsync(
                "stat", ["--file-system", "--format=%T", siding.RuntimeRoot]).ConfigureAwait(false)).Trim(),
            sdkVersion = Required("HVO_ISSUE_197_SDK_VERSION"),
            buildConfiguration = "Release",
            containerMode = "two isolated Docker Compose projects",
            dockerServerVersion = (await DockerAsync("version", "--format", "{{.Server.Version}}").ConfigureAwait(false)).Trim(),
            dockerStorageDriver = (await DockerAsync("info", "--format", "{{.Driver}}").ConfigureAwait(false)).Trim(),
            collectorImage = Required("HVO_OTEL_COLLECTOR_IMAGE"),
            centralDenyImage = Required("HVO_ISSUE_197_CENTRAL_DENY_IMAGE"),
            cameraImage = expectedImage
        };
        var evidencePath = Path.Combine(evidenceRoot, "issue-197-dual-agent.json");
        var evidence = new
        {
            schemaVersion = string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_197_EVIDENCE_MODE"), "final", StringComparison.Ordinal)
                ? "issue-197-dual-agent-final-v2"
                : "issue-197-dual-agent-diagnostic-v1",
            evidenceMode = Environment.GetEnvironmentVariable("HVO_ISSUE_197_EVIDENCE_MODE") ?? "diagnostic",
            trial = Environment.GetEnvironmentVariable("HVO_ISSUE_197_TRIAL") ?? "local",
            revision = new
            {
                commit = Environment.GetEnvironmentVariable("HVO_ISSUE_197_REVISION") ?? "working-tree",
                branch = Environment.GetEnvironmentVariable("HVO_ISSUE_197_BRANCH") ?? "unknown",
                dirtyStateSha256 = Environment.GetEnvironmentVariable("HVO_ISSUE_197_DIRTY_SHA256") ?? "unrecorded",
                clean = string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_197_TREE_CLEAN"), "true", StringComparison.Ordinal)
            },
            workload = new
            {
                id = "I197-DUAL-W1-v1",
                width = 1936,
                height = 1216,
                pixelFormat = CameraPixelFormat.Mono16.ToString(),
                exposureSeconds = 5,
                cadence = "MinimumStartInterval",
                warmupCapturesPerAgent = WarmupCaptureCount,
                measuredCapturesPerAgent = MeasuredCaptureCount,
                concurrency = 2,
                arrivalCapturesPerSecondPerAgent = 0.2,
                combinedArrivalCapturesPerSecond = 0.4,
                measuredRawPayloadBytes = MeasuredCaptureCount * 2L * 4_708_352L,
                initialBacklog = 0,
                samplingIntervalMilliseconds = 2000,
                faultDurationSeconds = 20
            },
            environment = environmentEvidence,
            topology = new
            {
                cameraImage = expectedImage,
                imageIdsEqual = topology.ImageIdsEqual,
                mutableStateIsolated = topology.MutableStateIsolated,
                topology.HualapaiProject,
                topology.SidingProject,
                topology.HualapaiNetwork,
                topology.SidingNetwork,
                hualapaiMounts = SanitizeMounts(topology.HualapaiMounts),
                sidingMounts = SanitizeMounts(topology.SidingMounts),
                hualapaiConfigurationSha256 = Required("HVO_ISSUE_197_HUALAPAI_CONFIG_SHA256"),
                sidingSpringConfigurationSha256 = Required("HVO_ISSUE_197_SIDING_CONFIG_SHA256"),
                collectorConfigurationSha256 = Required("HVO_ISSUE_197_COLLECTOR_CONFIG_SHA256"),
                hualapaiCameraConfigurationSha256 = Required("HVO_ISSUE_197_HUALAPAI_CAMERA_CONFIG_SHA256"),
                sidingSpringCameraConfigurationSha256 = Required("HVO_ISSUE_197_SIDING_CAMERA_CONFIG_SHA256"),
                sidingSpringFixtureSha256 = Required("HVO_ISSUE_197_SIDING_FIXTURE_SHA256"),
                catalogContentEqual = hualapaiEvidence.CatalogSha256 == sidingEvidence.CatalogSha256,
                catalogInodesDistinct = hualapaiEvidence.CatalogInode != sidingEvidence.CatalogInode,
                topology.CanaryProbeAttempts
            },
            agents = new
            {
                hualapai = AgentResult(hualapai, hualapaiOwnerId, hualapaiCaptures, hualapaiEvidence),
                sidingSpring = AgentResult(siding, sidingOwnerId, sidingCaptures, sidingEvidence)
            },
            restart = restartEvidence,
            fault = faultEvidence,
            performance = new
            {
                elapsedMilliseconds = started.Elapsed.TotalMilliseconds,
                finalDrainMilliseconds = finalDrain.Elapsed.TotalMilliseconds,
                processCounterBoundary = "CameraAgent container PID 1 lifetime through the final pre-stop sample; excludes collectors and the VSTest harness",
                resourceEvidence.HualapaiCpuSeconds,
                resourceEvidence.SidingCpuSeconds,
                resourceEvidence.HualapaiCollectorEstimatedCpuSeconds,
                resourceEvidence.SidingCollectorEstimatedCpuSeconds,
                resourceEvidence.HualapaiPeakRssBytes,
                resourceEvidence.SidingPeakRssBytes,
                resourceEvidence.HualapaiCollectorPeakMemoryBytes,
                resourceEvidence.SidingCollectorPeakMemoryBytes,
                resourceEvidence.CombinedPeakRssBytes,
                resourceEvidence.CombinedCollectorPeakMemoryBytes,
                resourceEvidence.CombinedTopologyPeakMemoryBytes,
                resourceEvidence.HualapaiStorageBytesRead,
                resourceEvidence.HualapaiStorageBytesWritten,
                resourceEvidence.SidingStorageBytesRead,
                resourceEvidence.SidingStorageBytesWritten,
                hualapaiRetainedFilesystemBytes,
                sidingRetainedFilesystemBytes,
                resourceEvidence.HualapaiCollectorBlockIoBytesRead,
                resourceEvidence.HualapaiCollectorBlockIoBytesWritten,
                resourceEvidence.SidingCollectorBlockIoBytesRead,
                resourceEvidence.SidingCollectorBlockIoBytesWritten,
                resourceEvidence.HualapaiContainerNetworkBytesRead,
                resourceEvidence.HualapaiContainerNetworkBytesWritten,
                resourceEvidence.SidingContainerNetworkBytesRead,
                resourceEvidence.SidingContainerNetworkBytesWritten,
                resourceEvidence.HualapaiCollectorNetworkBytesRead,
                resourceEvidence.HualapaiCollectorNetworkBytesWritten,
                resourceEvidence.SidingCollectorNetworkBytesRead,
                resourceEvidence.SidingCollectorNetworkBytesWritten,
                resourceEvidence.MedianSamplingIntervalMilliseconds,
                resourceEvidence.MaximumSamplingIntervalMilliseconds,
                resourceEvidence.SampleCount,
                resourceEvidence.UnavailableSampleAttempts,
                resourceEvidence.Phases,
                resourceEvidence.HualapaiBacklog,
                resourceEvidence.SidingBacklog,
                hualapaiMeasuredRuntime,
                sidingMeasuredRuntime,
                hualapaiFinalRuntimeMetrics = hualapaiRuntimeMetrics,
                sidingFinalRuntimeMetrics = sidingRuntimeMetrics
            },
            durable = new { hualapai = finalHualapai, sidingSpring = finalSiding },
            result = new
            {
                passed = true,
                drained = IsDrained(finalHualapai) && IsDrained(finalSiding),
                duplicateLogicalOutputs = finalHualapai.DuplicateLogicalOutputs + finalSiding.DuplicateLogicalOutputs,
                centralTrafficAttempts,
                disposition = "first absolute dual-agent topology baseline; issue #171 resource comparisons are limited to equivalent measurement boundaries"
            }
        };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, Json)).ConfigureAwait(false);
        TestContext.AddResultFile(evidencePath);
        TestContext.AddResultFile(hualapaiEvidence.JpegPath);
        TestContext.AddResultFile(sidingEvidence.JpegPath);
    }

    private static object AgentResult(
        AgentContext agent,
        string ownerId,
        IReadOnlyList<CameraAgentGalleryCapture> captures,
        AgentEvidence evidence) => new
        {
            agent.Name,
            agent.AgentId,
            agent.LocationId,
            ownerId,
            firstMeasuredSequence = captures[0].CaptureSequence,
            lastMeasuredSequence = captures[^1].CaptureSequence,
            measuredCaptureCount = captures.Count,
            evidence.CatalogSha256,
            evidence.CatalogInode,
            evidence.MutableInodes,
            evidence.LocationIdentitySha256,
            evidence.RetentionDays,
            evidence.RecipeIdentities,
            evidence.Artifacts,
            measuredRetentionAvailable = true,
            retainedJpeg = new
            {
                fileName = Path.GetFileName(evidence.JpegPath),
                sha256 = evidence.JpegSha256,
                evidence.JpegLength,
                width = 1936,
                height = 1216
            },
            evidence.StartIntervalsMilliseconds,
            evidence.CompletionLatenciesMilliseconds
        };

    private static async Task<AgentEvidence> AssertAgentEvidenceAsync(
        AgentContext agent,
        HttpClient client,
        IReadOnlyList<CameraAgentGalleryCapture> captures,
        string evidenceRoot)
    {
        Assert.HasCount(MeasuredCaptureCount, captures);
        Assert.IsTrue(captures.Zip(captures.Skip(1), static (previous, current) =>
            current.CaptureSequence == previous.CaptureSequence + 1).All(static contiguous => contiguous));
        var manifests = new ManifestReader(agent.RuntimeRoot);
        var retentionDays = AssertCameraConfiguration(agent);
        var rawManifests = new List<ArtifactManifestV2>(captures.Count);
        var completionLatencies = new List<double>(captures.Count);
        var recipeIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        var artifactEvidence = new List<ArtifactEvidence>(captures.Count * 6);
        foreach (var capture in captures)
        {
            Assert.AreEqual(agent.AgentId, capture.AgentId);
            CollectionAssert.AreEquivalent(ExpectedRoles, capture.Artifacts.Select(static artifact => artifact.Role).Distinct().ToArray());
            Assert.AreEqual(6, capture.Artifacts.Count);
            Assert.AreEqual(2, capture.Artifacts.Count(static artifact => artifact.Role == FrameArtifactRole.Preview));
            Assert.IsTrue(capture.ProcessingNodes.All(static node => node.Status == "Completed"));
            CollectionAssert.AreEquivalent(
                ExpectedProcessingNodes,
                capture.ProcessingNodes.Select(static node => node.NodeId).ToArray());
            Assert.AreEqual("Available", capture.Detail?.EvidenceAvailability);
            Assert.IsFalse(capture.Detail?.RawRetentionHold);
            var rawId = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw).ArtifactId;
            foreach (var artifact in capture.Artifacts)
            {
                var manifest = manifests.Read(artifact.ArtifactId);
                Assert.IsTrue(manifest.Descriptor.Validate().IsValid);
                Assert.AreEqual(capture.CaptureId, manifest.Descriptor.Capture.CaptureId);
                Assert.AreEqual(artifact.ChecksumSha256, HashFile(Path.Combine(
                    agent.RuntimeRoot,
                    manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar))), ignoreCase: true);
                if (artifact.Role != FrameArtifactRole.Raw)
                {
                    Assert.IsNotNull(artifact.Recipe);
                    Assert.AreEqual(manifest.Descriptor.Artifact.Recipe.Name, artifact.Recipe.Name);
                    Assert.AreEqual(manifest.Descriptor.Artifact.Recipe.SemanticVersion, artifact.Recipe.SemanticVersion);
                    Assert.AreEqual(manifest.Descriptor.Artifact.Recipe.ImplementationVersion, artifact.Recipe.ImplementationVersion);
                    Assert.AreEqual(manifest.Descriptor.Artifact.Recipe.OptionsSha256, artifact.Recipe.OptionsSha256, ignoreCase: true);
                    var recipeKey = $"{artifact.Role}:{artifact.Variant}";
                    if (!recipeIdentities.TryAdd(recipeKey, artifact.Recipe.IdentitySha256))
                    {
                        Assert.AreEqual(recipeIdentities[recipeKey], artifact.Recipe.IdentitySha256, ignoreCase: true);
                    }
                    AssertLineageReachesRaw(artifact, rawId, manifests);
                }
                artifactEvidence.Add(new ArtifactEvidence(
                    capture.CaptureId,
                    capture.CaptureSequence,
                    artifact.ArtifactId,
                    artifact.Role.ToString(),
                    artifact.Variant,
                    artifact.ChecksumSha256,
                    artifact.SourceArtifactIds,
                    artifact.Recipe?.IdentitySha256));
            }
            var raw = manifests.Read(rawId);
            rawManifests.Add(raw);
            completionLatencies.Add((capture.Detail!.ProcessingNodes.Max(static node => node.CompletedUtc) -
                raw.Descriptor.Timing.ExposureStartedUtc).TotalMilliseconds);
        }

        var lastRaw = rawManifests[^1];
        Assert.AreEqual(1936, lastRaw.Descriptor.Layout.Width);
        Assert.AreEqual(1216, lastRaw.Descriptor.Layout.Height);
        Assert.AreEqual(4_708_352L, lastRaw.Descriptor.Layout.ByteLength);
        Assert.AreEqual(CameraPixelFormat.Mono16, lastRaw.Descriptor.Layout.PixelFormat);
        Assert.AreEqual(agent.LocationId, lastRaw.Descriptor.Location?.LocationId);
        Assert.AreEqual(agent.LocationSource, lastRaw.Descriptor.Location?.Source);
        Assert.AreEqual(agent.SceneUtc, lastRaw.Scene?.SceneUtc);
        Assert.AreEqual(ExpectedCatalogSha256, lastRaw.Scene?.CatalogChecksumSha256, ignoreCase: true);
        var intervals = rawManifests.Zip(rawManifests.Skip(1), static (previous, current) =>
            (current.Descriptor.CycleEvidence!.ModuleCallStartedUtc - previous.Descriptor.CycleEvidence!.ModuleCallStartedUtc).TotalMilliseconds).ToArray();
        Assert.IsTrue(intervals.All(static interval => interval is >= 4900 and <= 5500),
            $"{agent.Name} did not sustain the declared five-second cadence.");

        var snapshot = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
            agent.CatalogRoot, "hyg-v42-production"));
        Assert.AreEqual(ExpectedCatalogSha256, snapshot.DatabaseSha256, ignoreCase: true);
        Assert.IsEmpty(Directory.EnumerateFiles(agent.CatalogRoot, "*.db-*", SearchOption.AllDirectories).ToArray());
        var catalogInode = await StatInodeAsync(snapshot.DatabasePath).ConfigureAwait(false);
        var mutableInodes = new[]
        {
            await StatInodeAsync(agent.RuntimeRoot).ConfigureAwait(false),
            await StatInodeAsync(Path.Combine(agent.RuntimeRoot, "identity", "cameraagent_identity.db")).ConfigureAwait(false),
            await StatInodeAsync(Path.Combine(agent.RuntimeRoot, "journal", "raw-ingress.db")).ConfigureAwait(false),
            await StatInodeAsync(Path.Combine(agent.Root, "dataprotection")).ConfigureAwait(false),
            await StatInodeAsync(Directory.EnumerateFiles(Path.Combine(agent.Root, "dataprotection")).Single()).ConfigureAwait(false),
            await StatInodeAsync(agent.PasswordFile).ConfigureAwait(false)
        };
        var summary = await client.GetFromJsonAsync<CameraAgentOperationsSummary>("/api/v1/operations/summary").ConfigureAwait(false);
        Assert.IsNotNull(summary);
        Assert.AreEqual(agent.AgentId, summary.Configuration.Value.AgentId);
        Assert.AreEqual("Disabled", summary.Configuration.Value.CentralIntegration);
        Assert.AreEqual("Disabled", summary.ArtifactOutbox.Value.Availability);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.PendingCount);
        Assert.AreEqual("Disabled", summary.Heartbeat.Value.Availability);
        Assert.AreEqual("Disabled", summary.EnvironmentalDelivery.Value.Availability);
        using var galleryPage = await client.GetAsync(new Uri("/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, galleryPage.StatusCode);
        using var detailPage = await client.GetAsync(new Uri($"/gallery/{captures[^1].CaptureId:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, detailPage.StatusCode);

        var annotated = captures[^1].Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview);
        using var preview = await client.GetAsync(new Uri(
            $"/api/v1/operations/artifacts/{annotated.ArtifactId:D}/preview",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
        var jpeg = await preview.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var decoded = JpegImageCodec.DecodeJpeg(jpeg);
        Assert.AreEqual(1936, decoded.Width);
        Assert.AreEqual(1216, decoded.Height);
        Assert.AreEqual(CameraPixelFormat.Mono8, decoded.PixelFormat);
        var jpegPath = Path.Combine(evidenceRoot, $"issue-197-{agent.Name}-annotated.jpg");
        await File.WriteAllBytesAsync(jpegPath, jpeg).ConfigureAwait(false);
        return new AgentEvidence(
            jpegPath,
            Convert.ToHexString(SHA256.HashData(jpeg)),
            jpeg.LongLength,
            snapshot.DatabaseSha256.ToUpperInvariant(),
            catalogInode,
            mutableInodes,
            lastRaw.Descriptor.Location!.IdentitySha256,
            retentionDays,
            recipeIdentities,
            artifactEvidence,
            intervals,
            completionLatencies);
    }

    private static int AssertCameraConfiguration(AgentContext agent)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(agent.Root, "config", "cameraagent.json")));
        var root = document.RootElement;
        Assert.AreEqual(agent.AgentId, root.GetProperty("agentId").GetString());
        Assert.AreEqual("VirtualSky", root.GetProperty("module").GetProperty("type").GetString());
        Assert.AreEqual(agent.Name == "hualapai" ? 2025 : 197,
            root.GetProperty("module").GetProperty("options").GetProperty("seed").GetInt32());
        Assert.AreEqual(agent.SceneUtc, root.GetProperty("module").GetProperty("options")
            .GetProperty("fixedSceneUtc").GetDateTimeOffset());
        var steps = root.GetProperty("processingSteps").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(ExpectedProcessingNodes, steps.Select(static step => step.GetProperty("id").GetString()).ToArray());
        var storage = steps.Single(static step => step.GetProperty("id").GetString() == "LocalStorage").GetProperty("options");
        Assert.AreEqual($"/var/lib/hvo/{agent.Name}-197", storage.GetProperty("storageRoot").GetString());
        Assert.IsFalse(storage.GetProperty("queueForUpload").GetBoolean());
        var retentionDays = storage.GetProperty("retentionDays").GetInt32();
        Assert.AreEqual(7, retentionDays);
        return retentionDays;
    }

    private static async Task<RestartEvidence> AssertIndependentCrashRecoveryAsync(
        AgentContext first,
        HttpClient firstClient,
        IReadOnlyDictionary<Guid, string> firstChecksums,
        AgentContext second,
        HttpClient secondClient,
        IReadOnlyDictionary<Guid, string> secondChecksums,
        ContainerResourceSampler sampler)
    {
        var secondBefore = ReadMaximumSequence(second);
        var firstContainerId = await DockerAsync("inspect", "--format", "{{.Id}}", first.Container).ConfigureAwait(false);
        var secondContainerId = await DockerAsync("inspect", "--format", "{{.Id}}", second.Container).ConfigureAwait(false);
        var firstPending = await WaitForPendingCaptureAsync(first).ConfigureAwait(false);
        var firstRecovery = Stopwatch.StartNew();
        sampler.SetPausedAgent(first.Name);
        await DockerAsync("kill", "--signal", "KILL", first.Container).ConfigureAwait(false);
        var firstKillMilliseconds = firstRecovery.Elapsed.TotalMilliseconds;
        var firstPostCrashState = ReadCaptureLaneState(first, firstPending.CaptureId);
        Assert.AreNotEqual("completed", firstPostCrashState, ignoreCase: true);
        var secondAfterKillBaseline = ReadMaximumSequence(second);
        var secondAfterFirstRestart = (await WaitForCompleteCapturesAsync(
            second, secondClient, 1, secondAfterKillBaseline, sampler).ConfigureAwait(false))[0].CaptureSequence;
        var firstStart = Stopwatch.StartNew();
        await DockerAsync("start", first.Container).ConfigureAwait(false);
        await WaitForHealthyAsync(first, sampler).ConfigureAwait(false);
        var firstStartToHealthyMilliseconds = firstStart.Elapsed.TotalMilliseconds;
        var firstCrashToHealthyMilliseconds = firstRecovery.Elapsed.TotalMilliseconds;
        sampler.SetPausedAgent(null);
        Assert.AreEqual(secondContainerId.Trim(), (await DockerAsync("inspect", "--format", "{{.Id}}", second.Container).ConfigureAwait(false)).Trim());
        var firstRecovered = (await WaitForCompleteCapturesAsync(first, firstClient, 1, firstPending.Sequence - 1, sampler).ConfigureAwait(false))[0];
        Assert.AreEqual(firstPending.Sequence, firstRecovered.CaptureSequence);
        Assert.AreEqual(firstPending.CaptureId, firstRecovered.CaptureId);
        var firstCrashToRecoveryMilliseconds = firstRecovery.Elapsed.TotalMilliseconds;
        Assert.IsGreaterThan(secondAfterKillBaseline, secondAfterFirstRestart);
        AssertChecksums(first, firstChecksums);

        var firstBeforeSecondRestart = ReadMaximumSequence(first);
        var secondPending = await WaitForPendingCaptureAsync(second).ConfigureAwait(false);
        var secondRecovery = Stopwatch.StartNew();
        sampler.SetPausedAgent(second.Name);
        await DockerAsync("kill", "--signal", "KILL", second.Container).ConfigureAwait(false);
        var secondKillMilliseconds = secondRecovery.Elapsed.TotalMilliseconds;
        var secondPostCrashState = ReadCaptureLaneState(second, secondPending.CaptureId);
        Assert.AreNotEqual("completed", secondPostCrashState, ignoreCase: true);
        var firstAfterKillBaseline = ReadMaximumSequence(first);
        var firstAfterSecondRestart = (await WaitForCompleteCapturesAsync(
            first, firstClient, 1, firstAfterKillBaseline, sampler).ConfigureAwait(false))[0].CaptureSequence;
        var secondStart = Stopwatch.StartNew();
        await DockerAsync("start", second.Container).ConfigureAwait(false);
        await WaitForHealthyAsync(second, sampler).ConfigureAwait(false);
        var secondStartToHealthyMilliseconds = secondStart.Elapsed.TotalMilliseconds;
        var secondCrashToHealthyMilliseconds = secondRecovery.Elapsed.TotalMilliseconds;
        sampler.SetPausedAgent(null);
        Assert.AreEqual(firstContainerId.Trim(), (await DockerAsync("inspect", "--format", "{{.Id}}", first.Container).ConfigureAwait(false)).Trim());
        var secondRecovered = (await WaitForCompleteCapturesAsync(second, secondClient, 1, secondPending.Sequence - 1, sampler).ConfigureAwait(false))[0];
        Assert.AreEqual(secondPending.Sequence, secondRecovered.CaptureSequence);
        Assert.AreEqual(secondPending.CaptureId, secondRecovered.CaptureId);
        var secondCrashToRecoveryMilliseconds = secondRecovery.Elapsed.TotalMilliseconds;
        Assert.IsGreaterThan(firstAfterKillBaseline, firstAfterSecondRestart);
        AssertChecksums(second, secondChecksums);
        return new RestartEvidence(
            firstPending.Sequence,
            firstRecovered.CaptureSequence,
            secondPending.Sequence,
            secondRecovered.CaptureSequence,
            firstAfterSecondRestart,
            secondAfterFirstRestart,
            firstAfterKillBaseline,
            secondAfterKillBaseline,
            firstPending.CaptureId,
            secondPending.CaptureId,
            firstPending.State,
            secondPending.State,
            firstPostCrashState,
            secondPostCrashState,
            firstKillMilliseconds,
            secondKillMilliseconds,
            firstStartToHealthyMilliseconds,
            secondStartToHealthyMilliseconds,
            firstCrashToHealthyMilliseconds,
            secondCrashToHealthyMilliseconds,
            firstCrashToRecoveryMilliseconds,
            secondCrashToRecoveryMilliseconds);
    }

    private static string ReadCaptureLaneState(AgentContext agent, Guid captureId)
    {
        using var connection = OpenJournal(agent.RuntimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.state
            FROM capture_lane_work AS w
            JOIN raw_captures AS r ON r.raw_capture_row_id = w.raw_capture_row_id
            WHERE r.capture_id = $capture_id
            ORDER BY w.work_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        return (string?)command.ExecuteScalar() ?? throw new AssertFailedException(
            $"{agent.Name} crash target {captureId:D} has no durable lane state.");
    }

    private static async Task<PendingCapture> WaitForPendingCaptureAsync(AgentContext agent)
    {
        var minimumSequence = ReadMaximumSequence(agent);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var connection = OpenJournal(agent.RuntimeRoot);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT r.capture_id, r.capture_sequence, w.state
                    FROM capture_lane_work AS w
                    JOIN raw_captures AS r ON r.raw_capture_row_id = w.raw_capture_row_id
                    WHERE r.capture_sequence > $minimum_sequence AND w.state <> 'completed'
                    ORDER BY r.capture_sequence, w.work_id
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$minimum_sequence", minimumSequence);
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    return new PendingCapture(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetInt64(1),
                        reader.GetString(2));
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
        Assert.Fail($"{agent.Name} did not expose pending durable lane work before the crash deadline.");
        throw new UnreachableException();
    }

    private static async Task<FaultEvidence> AssertPauseIsolationAsync(
        AgentContext first,
        HttpClient firstClient,
        AgentContext second,
        HttpClient secondClient,
        ContainerResourceSampler sampler)
    {
        var forward = await AssertOnePauseAsync(first, second, secondClient, sampler).ConfigureAwait(false);
        var forwardRecovery = Stopwatch.StartNew();
        await WaitForHealthyAsync(first, sampler).ConfigureAwait(false);
        var forwardHealthyMilliseconds = forwardRecovery.Elapsed.TotalMilliseconds;
        var forwardCapture = (await WaitForCompleteCapturesAsync(
            first, firstClient, 1, forward.PausedSequence, sampler).ConfigureAwait(false))[0];
        forward = forward with
        {
            UnpauseToHealthyMilliseconds = forwardHealthyMilliseconds,
            UnpauseToNextGraphMilliseconds = forwardRecovery.Elapsed.TotalMilliseconds,
            RecoveredCaptureId = forwardCapture.CaptureId,
            RecoveredCaptureSequence = forwardCapture.CaptureSequence
        };
        var reverse = await AssertOnePauseAsync(second, first, firstClient, sampler).ConfigureAwait(false);
        var reverseRecovery = Stopwatch.StartNew();
        await WaitForHealthyAsync(second, sampler).ConfigureAwait(false);
        var reverseHealthyMilliseconds = reverseRecovery.Elapsed.TotalMilliseconds;
        var reverseCapture = (await WaitForCompleteCapturesAsync(
            second, secondClient, 1, reverse.PausedSequence, sampler).ConfigureAwait(false))[0];
        reverse = reverse with
        {
            UnpauseToHealthyMilliseconds = reverseHealthyMilliseconds,
            UnpauseToNextGraphMilliseconds = reverseRecovery.Elapsed.TotalMilliseconds,
            RecoveredCaptureId = reverseCapture.CaptureId,
            RecoveredCaptureSequence = reverseCapture.CaptureSequence
        };
        return new FaultEvidence(forward, reverse);
    }

    private static async Task<PauseEvidence> AssertOnePauseAsync(
        AgentContext paused,
        AgentContext unaffected,
        HttpClient unaffectedClient,
        ContainerResourceSampler sampler)
    {
        sampler.SetPausedAgent(paused.Name);
        await DockerAsync("pause", paused.Container).ConfigureAwait(false);
        try
        {
            var pausedSequence = ReadMaximumSequence(paused);
            var unaffectedSequence = ReadMaximumSequence(unaffected);
            var beforeTree = SnapshotTree(paused.RuntimeRoot);
            var pausedInterval = Stopwatch.StartNew();
            var delay = Task.Delay(TimeSpan.FromSeconds(20));
            var progress = WaitForCompleteCapturesAsync(unaffected, unaffectedClient, 3, unaffectedSequence, sampler);
            await Task.WhenAll(delay, progress).ConfigureAwait(false);
            var completed = await progress.ConfigureAwait(false);
            var unaffectedManifests = new ManifestReader(unaffected.RuntimeRoot);
            var unaffectedStartIntervals = completed
                .Select(capture => unaffectedManifests.Read(capture.Artifacts.Single(
                    static artifact => artifact.Role == FrameArtifactRole.Raw).ArtifactId))
                .Zip(completed.Skip(1).Select(capture => unaffectedManifests.Read(capture.Artifacts.Single(
                    static artifact => artifact.Role == FrameArtifactRole.Raw).ArtifactId)),
                    static (previous, current) => (current.Descriptor.CycleEvidence!.ModuleCallStartedUtc -
                        previous.Descriptor.CycleEvidence!.ModuleCallStartedUtc).TotalMilliseconds)
                .ToArray();
            Assert.IsTrue(unaffectedStartIntervals.All(static interval => interval is >= 4900 and <= 5500));
            var afterTree = SnapshotTree(paused.RuntimeRoot);
            Assert.AreEqual(pausedSequence, ReadMaximumSequence(paused));
            Assert.AreEqual(beforeTree, afterTree);
            Assert.IsGreaterThanOrEqualTo(unaffectedSequence + 3, completed[^1].CaptureSequence);
            await WaitForHealthyAsync(unaffected, sampler).ConfigureAwait(false);
            pausedInterval.Stop();
            Assert.IsGreaterThanOrEqualTo(20_000d, pausedInterval.Elapsed.TotalMilliseconds);
            return new PauseEvidence(paused.Name, unaffected.Name, pausedSequence, unaffectedSequence,
                completed[^1].CaptureSequence, pausedInterval.Elapsed.TotalMilliseconds, beforeTree,
                0, 0, Guid.Empty, 0);
        }
        finally
        {
            await DockerAsync("unpause", paused.Container).ConfigureAwait(false);
            sampler.SetPausedAgent(null);
        }
    }

    private static async Task<IReadOnlyList<CameraAgentGalleryCapture>> WaitForCompleteCapturesAsync(
        AgentContext agent,
        HttpClient client,
        int count,
        long minimumSequence,
        ContainerResourceSampler sampler)
    {
        var captures = new Dictionary<long, CameraAgentGalleryCapture>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(8);
        while (captures.Count < count)
        {
            var page = await client.GetFromJsonAsync<CameraAgentGalleryPage>(
                $"/api/v1/operations/gallery/?pageSize=100&minimumSequence={minimumSequence + 1}&evidenceOrigin=Simulated").ConfigureAwait(false);
            Assert.IsNotNull(page);
            foreach (var candidate in page.Items.OrderBy(static item => item.CaptureSequence))
            {
                if (captures.ContainsKey(candidate.CaptureSequence) ||
                    !ExpectedRoles.All(role => candidate.Artifacts.Any(artifact => artifact.Role == role)) ||
                    candidate.ProcessingNodes.Any(static node => node.Status != "Completed"))
                {
                    continue;
                }
                var detail = await client.GetFromJsonAsync<CameraAgentGalleryCapture>(
                    $"/api/v1/operations/gallery/{candidate.CaptureId:D}").ConfigureAwait(false);
                if (detail?.Detail is { EvidenceAvailability: "Available", RawRetentionHold: false })
                {
                    captures[candidate.CaptureSequence] = detail;
                }
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for {count} complete {agent.Name} captures after sequence {minimumSequence}.");
            }
            if (captures.Count < count)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
        return captures.Values.OrderBy(static capture => capture.CaptureSequence).Take(count).ToArray();
    }

    private static async Task WaitForHealthyAsync(AgentContext agent, ContainerResourceSampler sampler)
    {
        using var client = new HttpClient { BaseAddress = agent.BaseUri, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Contains("Production celestial catalog snapshot is installed", StringComparison.Ordinal) &&
                        body.Contains("119625", StringComparison.Ordinal) &&
                        !body.Contains(ExpectedCatalogSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        Assert.Fail($"{agent.Name} did not reach healthy production-catalog state.");
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned session owns the HttpClient, which owns its handler.")]
    private static async Task<AgentSession> LoginAsync(AgentContext agent)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookies,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { BaseAddress = agent.BaseUri, Timeout = TimeSpan.FromMinutes(2) };
        using var login = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode();
        var html = await login.Content.ReadAsStringAsync().ConfigureAwait(false);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.IsTrue(token.Success);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["Input.Email"] = agent.OwnerEmail,
            ["Input.Password"] = (await File.ReadAllTextAsync(agent.PasswordFile).ConfigureAwait(false)).Trim(),
            ["Input.RememberMe"] = "false",
            ["_handler"] = "login"
        });
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var cookie = cookies.GetCookies(agent.BaseUri).Cast<Cookie>().Single(item => item.Name == agent.CookieName);
        return new AgentSession(client, cookie.Name, cookie.Value);
    }

    private static async Task AssertForeignCookieRejectedAsync(AgentSession foreign, AgentContext target)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true
        };
        using var client = new HttpClient(handler) { BaseAddress = target.BaseUri };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/operations/summary");
        request.Headers.TryAddWithoutValidation("Cookie", $"{foreign.CookieName}={foreign.CookieValue}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<TopologyEvidence> AssertTopologyAsync(
        AgentContext hualapai,
        AgentContext siding,
        string expectedImage)
    {
        var first = await InspectContainerAsync(hualapai.Container).ConfigureAwait(false);
        var second = await InspectContainerAsync(siding.Container).ConfigureAwait(false);
        var firstCollector = await InspectContainerAsync(hualapai.CollectorContainer).ConfigureAwait(false);
        var secondCollector = await InspectContainerAsync(siding.CollectorContainer).ConfigureAwait(false);
        var firstDeny = await InspectContainerAsync(hualapai.CentralDenyContainer).ConfigureAwait(false);
        var secondDeny = await InspectContainerAsync(siding.CentralDenyContainer).ConfigureAwait(false);
        Assert.AreEqual(expectedImage, first.Image);
        Assert.AreEqual(expectedImage, second.Image);
        Assert.IsTrue(first.ReadOnlyRootFileSystem);
        Assert.IsTrue(second.ReadOnlyRootFileSystem);
        Assert.IsTrue(firstCollector.ReadOnlyRootFileSystem);
        Assert.IsTrue(secondCollector.ReadOnlyRootFileSystem);
        Assert.IsTrue(firstDeny.ReadOnlyRootFileSystem);
        Assert.IsTrue(secondDeny.ReadOnlyRootFileSystem);
        Assert.IsTrue(first.TempIsTmpfs);
        Assert.IsTrue(second.TempIsTmpfs);
        Assert.IsTrue(firstCollector.TempIsTmpfs);
        Assert.IsTrue(secondCollector.TempIsTmpfs);
        Assert.AreNotEqual(first.ContainerId, second.ContainerId);
        Assert.AreNotEqual(first.Hostname, second.Hostname);
        Assert.AreNotEqual(first.Project, second.Project);
        Assert.AreNotEqual(first.Network, second.Network);
        Assert.AreNotEqual(first.SandboxKey, second.SandboxKey);
        Assert.AreNotEqual(first.ProcessId, second.ProcessId);
        Assert.IsFalse(first.Environment.Any(static value => value.StartsWith("LocalIdentity__AdminPassword=", StringComparison.Ordinal)));
        Assert.IsFalse(second.Environment.Any(static value => value.StartsWith("LocalIdentity__AdminPassword=", StringComparison.Ordinal)));
        var firstMounts = first.Mounts.Concat(firstCollector.Mounts).ToArray();
        var secondMounts = second.Mounts.Concat(secondCollector.Mounts).ToArray();
        var firstSources = firstMounts.Select(static mount => Path.GetFullPath(mount.Source)).ToHashSet(StringComparer.Ordinal);
        var secondSources = secondMounts.Select(static mount => Path.GetFullPath(mount.Source)).ToHashSet(StringComparer.Ordinal);
        Assert.IsEmpty(firstSources.Intersect(secondSources).ToArray());
        Assert.IsFalse(firstSources.Any(firstSource => secondSources.Any(secondSource =>
            IsSameOrNested(firstSource, secondSource) || IsSameOrNested(secondSource, firstSource))));
        Assert.IsTrue(first.Mounts.Where(static mount => mount.Destination.Contains("catalog", StringComparison.Ordinal)).All(static mount => !mount.ReadWrite));
        Assert.IsTrue(second.Mounts.Where(static mount => mount.Destination.Contains("catalog", StringComparison.Ordinal)).All(static mount => !mount.ReadWrite));
        await AssertPrivateNetworkAsync(first.Network, hualapai).ConfigureAwait(false);
        await AssertPrivateNetworkAsync(second.Network, siding).ConfigureAwait(false);
        var running = await DockerAsync("ps", "--format", "{{.Names}}").ConfigureAwait(false);
        Assert.IsFalse(Regex.IsMatch(
            running,
            "(logichost|mssql|sqlserver|redis|minio|mailpit)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        var centralTrafficAttempts = await CountCentralTrafficAttemptsAsync(hualapai, siding).ConfigureAwait(false);
        Assert.AreEqual(0, centralTrafficAttempts);
        await DockerAsync(
            "exec",
            hualapai.Container,
            "/bin/sh",
            "-c",
            "curl --silent --max-time 2 http://central-deny:8080/ >/dev/null 2>&1 || true").ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        var canaryProbeAttempts = await CountCentralTrafficAttemptsAsync(hualapai, siding).ConfigureAwait(false);
        Assert.AreEqual(1, canaryProbeAttempts);
        return new TopologyEvidence(
            first.Image == second.Image,
            !firstSources.Overlaps(secondSources),
            first.Project,
            second.Project,
            first.Network,
            second.Network,
            firstMounts,
            secondMounts,
            canaryProbeAttempts);
    }

    private static async Task AssertPrivateNetworkAsync(string network, AgentContext agent)
    {
        var json = await DockerAsync("network", "inspect", network).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0];
        Assert.AreEqual("bridge", root.GetProperty("Driver").GetString());
        var names = root.GetProperty("Containers").EnumerateObject()
            .Select(static property => property.Value.GetProperty("Name").GetString()).ToArray();
        Assert.HasCount(3, names);
        CollectionAssert.Contains(names, agent.Container);
        CollectionAssert.Contains(names, agent.CollectorContainer);
        CollectionAssert.Contains(names, agent.CentralDenyContainer);
        Assert.IsTrue(names.Any(static name => name?.EndsWith("-collector", StringComparison.Ordinal) == true));
    }

    private static async Task<ContainerInspection> InspectContainerAsync(string container)
    {
        var json = await DockerAsync("inspect", container).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0];
        var networks = root.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().ToArray();
        Assert.HasCount(1, networks);
        return new ContainerInspection(
            root.GetProperty("Id").GetString()!,
            root.GetProperty("Image").GetString()!,
            root.GetProperty("Config").GetProperty("Hostname").GetString()!,
            root.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.project").GetString()!,
            networks[0].Name,
            root.GetProperty("NetworkSettings").GetProperty("SandboxKey").GetString()!,
            root.GetProperty("State").GetProperty("Pid").GetInt32(),
            root.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean(),
            HasTmpfs(root.GetProperty("HostConfig"), "/tmp"),
            root.GetProperty("Config").GetProperty("Env").EnumerateArray().Select(static item => item.GetString()!).ToArray(),
            root.GetProperty("Mounts").EnumerateArray().Select(static item => new MountEvidence(
                item.GetProperty("Source").GetString()!,
                item.GetProperty("Destination").GetString()!,
                item.GetProperty("RW").GetBoolean())).ToArray());
    }

    private static bool HasTmpfs(JsonElement hostConfig, string path) =>
        hostConfig.GetProperty("Tmpfs") is { ValueKind: JsonValueKind.Object } tmpfs && tmpfs.TryGetProperty(path, out _);

    private static async Task<int> CountCentralTrafficAttemptsAsync(params AgentContext[] agents)
    {
        var attempts = 0;
        foreach (var agent in agents)
        {
            var logs = await DockerLogsAsync(agent.CentralDenyContainer).ConfigureAwait(false);
            attempts += logs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(static line => line.Trim().Equals("HVO197_DENY_HIT", StringComparison.Ordinal));
        }
        return attempts;
    }

    private static bool IsSameOrNested(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static object[] SanitizeMounts(IEnumerable<MountEvidence> mounts) => mounts.Select(static mount => new
    {
        sourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mount.Source))),
        mount.Destination,
        mount.ReadWrite
    }).Cast<object>().ToArray();

    private static Dictionary<Guid, string> CaptureChecksums(
        AgentContext agent,
        IEnumerable<CameraAgentGalleryCapture> captures)
    {
        var manifests = new ManifestReader(agent.RuntimeRoot);
        return captures.SelectMany(static capture => capture.Artifacts)
            .ToDictionary(static artifact => artifact.ArtifactId, artifact => manifests.Read(artifact.ArtifactId).Descriptor.Artifact.ChecksumSha256);
    }

    private static void AssertChecksums(AgentContext agent, IReadOnlyDictionary<Guid, string> expected)
    {
        var manifests = new ManifestReader(agent.RuntimeRoot);
        foreach (var (artifactId, checksum) in expected)
        {
            var manifest = manifests.Read(artifactId);
            Assert.AreEqual(checksum, HashFile(Path.Combine(agent.RuntimeRoot,
                manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar))), ignoreCase: true);
        }
    }

    private static async Task<string> CaptureMetricsAsync(AgentContext agent, string evidenceRoot)
    {
        using var client = new HttpClient { BaseAddress = agent.BaseUri };
        var metrics = await client.GetStringAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, $"issue-197-{agent.Name}-metrics.txt"), metrics).ConfigureAwait(false);
        return metrics;
    }

    private static void AssertMetricSurface(string metrics)
    {
        StringAssert.Contains(metrics, "camera_agent_capture", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "camera_agent_ingress", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "camera_agent_lanes", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "camera_agent_processing", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "dotnet_gc_heap_total_allocated_bytes", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "dotnet_gc_last_collection_heap_size_bytes", StringComparison.Ordinal);
    }

    private static RuntimeMetricEvidence ReadRuntimeMetrics(string metrics) => new(
        ReadPrometheusValue(metrics, "dotnet_gc_heap_total_allocated_bytes_total"),
        ReadPrometheusValue(metrics, "dotnet_gc_last_collection_heap_size_bytes", "gc_heap_generation=\"loh\""),
        ReadPrometheusValue(metrics, "dotnet_gc_last_collection_heap_size_bytes", "gc_heap_generation=\"poh\""),
        ReadPrometheusValue(metrics, "dotnet_gc_last_collection_heap_fragmentation_size_bytes", "gc_heap_generation=\"loh\""),
        ReadPrometheusSum(metrics, "dotnet_gc_collections_total"),
        ReadPrometheusValue(metrics, "dotnet_gc_pause_time_seconds_total"),
        ReadPrometheusSum(metrics, "dotnet_process_cpu_time_seconds_total"),
        ReadPrometheusValue(metrics, "dotnet_process_memory_working_set_bytes"));

    private static async Task<RuntimeMetricEvidence> CaptureRuntimeMetricsAsync(HttpClient client) =>
        ReadRuntimeMetrics(await client.GetStringAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false));

    private static double ReadPrometheusValue(string metrics, string prefix, string? requiredLabel = null)
    {
        var line = metrics.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal) &&
                (requiredLabel is null || candidate.Contains(requiredLabel, StringComparison.Ordinal)));
        Assert.IsNotNull(line, $"Prometheus metric {prefix} with label {requiredLabel ?? "<none>"} is missing.");
        var value = line[(line.LastIndexOf(' ') + 1)..].Trim();
        Assert.IsTrue(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed));
        return parsed;
    }

    private static double ReadPrometheusSum(string metrics, string prefix) => metrics
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal))
        .Sum(static candidate => double.Parse(
            candidate[(candidate.LastIndexOf(' ') + 1)..].Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture));

    private static async Task AssertTelemetryEvidenceAsync(AgentContext agent)
    {
        foreach (var file in new[] { "logs.json", "metrics.json", "traces.json" })
        {
            var path = Path.Combine(agent.TelemetryRoot, file);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while ((!File.Exists(path) || new FileInfo(path).Length == 0) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 0, $"{agent.Name} did not retain OTLP {file}.");
        }
        var logs = await File.ReadAllTextAsync(Path.Combine(agent.TelemetryRoot, "logs.json")).ConfigureAwait(false);
        var metrics = await File.ReadAllTextAsync(Path.Combine(agent.TelemetryRoot, "metrics.json")).ConfigureAwait(false);
        var traces = await File.ReadAllTextAsync(Path.Combine(agent.TelemetryRoot, "traces.json")).ConfigureAwait(false);
        var telemetry = string.Concat(metrics, traces);
        StringAssert.Contains(telemetry, $"cameraagent-{agent.Name}-197", StringComparison.Ordinal);
        foreach (var eventId in new[] { 3000, 7301, 7302, 2002, 2064, 2003, 2041, 2040, 2050, 2011, 2009, 2072 })
        {
            StringAssert.Contains(logs, $"\"intValue\":\"{eventId}\"", StringComparison.Ordinal);
        }
        Assert.IsFalse(logs.Contains("\"severityText\":\"Error\"", StringComparison.Ordinal));
        Assert.IsFalse(logs.Contains("\"severityText\":\"Critical\"", StringComparison.Ordinal));
        Assert.IsFalse(logs.Contains("central-forbidden.invalid", StringComparison.Ordinal));
        StringAssert.Contains(metrics, "camera_agent.capture", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "camera_agent.ingress", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "camera_agent.lanes", StringComparison.Ordinal);
        StringAssert.Contains(metrics, "camera_agent.processing", StringComparison.Ordinal);
        StringAssert.Contains(traces, "capture-cycle", StringComparison.Ordinal);
        StringAssert.Contains(traces, "raw-ingress.accept", StringComparison.Ordinal);
        StringAssert.Contains(traces, "capture-lanes.process", StringComparison.Ordinal);
        StringAssert.Contains(traces, "processing-graph.execute", StringComparison.Ordinal);
    }

    private static void AssertLineageReachesRaw(
        CameraAgentGalleryArtifact artifact,
        Guid rawArtifactId,
        ManifestReader manifests)
    {
        var pending = new Queue<Guid>(artifact.SourceArtifactIds);
        var visited = new HashSet<Guid>();
        while (pending.TryDequeue(out var sourceId))
        {
            if (!visited.Add(sourceId))
            {
                continue;
            }
            if (sourceId == rawArtifactId)
            {
                return;
            }
            foreach (var parent in manifests.Read(sourceId).Descriptor.Artifact.SourceArtifactIds)
            {
                pending.Enqueue(parent);
            }
        }
        Assert.Fail($"Artifact {artifact.ArtifactId:D} lineage does not reach raw artifact {rawArtifactId:D}.");
    }

    private static string ReadOwnerId(AgentContext agent)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(agent.RuntimeRoot, "identity", "cameraagent_identity.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM AspNetUsers WHERE IsSiteOwner = 1;";
        return (string?)command.ExecuteScalar() ?? throw new AssertFailedException($"{agent.Name} has no site owner.");
    }

    private static DurableSnapshot ReadDurableSnapshot(AgentContext agent) => new(
        ReadJournalScalar(agent, "SELECT COUNT(*) FROM raw_captures;"),
        ReadJournalScalar(agent, "SELECT COUNT(*) FROM processing_outputs;"),
        ReadJournalScalar(agent, "SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed';"),
        ReadJournalScalar(agent, "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';"),
        ReadJournalScalar(agent, "SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed';"),
        ReadJournalScalar(agent, """
            SELECT COALESCE(SUM(output_count - 1), 0)
            FROM (
                SELECT COUNT(*) AS output_count
                FROM processing_outputs
                GROUP BY capture_id, node_id, role, variant
                HAVING COUNT(*) > 1
            );
            """));

    private static long ReadMaximumSequence(AgentContext agent) =>
        ReadJournalScalar(agent, "SELECT COALESCE(MAX(capture_sequence), 0) FROM raw_captures;");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Queries are fixed private test constants.")]
    private static long ReadJournalScalar(AgentContext agent, string sql)
    {
        using var connection = OpenJournal(agent.RuntimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenJournal(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void AssertDrained(DurableSnapshot snapshot, string agent)
    {
        Assert.AreEqual(0L, snapshot.PendingRawCaptures, $"{agent} raw ingress did not drain.");
        Assert.AreEqual(0L, snapshot.PendingLaneWork, $"{agent} lane work did not drain.");
        Assert.AreEqual(0L, snapshot.PendingProcessingNodes, $"{agent} processing did not drain.");
        Assert.AreEqual(0L, snapshot.DuplicateLogicalOutputs, $"{agent} produced duplicate logical outputs.");
    }

    private static bool IsDrained(DurableSnapshot snapshot) =>
        snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
        snapshot.PendingProcessingNodes == 0 && snapshot.DuplicateLogicalOutputs == 0;

    private static string SnapshotTree(string root)
    {
        var description = string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(root, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static long DirectoryBytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(static file => new FileInfo(file).Length);

    private static async Task<string> StatInodeAsync(string path) =>
        (await ProcessAsync("stat", ["--format=%d:%i", path]).ConfigureAwait(false)).Trim();

    private static Task<string> DockerAsync(params string[] arguments) => ProcessAsync("docker", arguments);

    private static Task<string> DockerLogsAsync(string container) =>
        ProcessAsync("docker", ["logs", container], includeStandardError: true);

    private static async Task<string> ProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        bool includeStandardError = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        Assert.IsTrue(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var standardOutput = await output.ConfigureAwait(false);
        var standardError = await error.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, $"{fileName} {string.Join(' ', arguments)} failed: {standardError}");
        return includeStandardError ? string.Concat(standardOutput, standardError) : standardOutput;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new AssertFailedException($"Required environment variable {name} is missing.");

    private static string RequiredPath(string name) => Path.GetFullPath(Required(name));

    private static int ReadPositiveInteger(string name, int fallback, int minimum)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }
        if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            throw new InvalidOperationException($"{name} must be an integer greater than or equal to {minimum}.");
        }
        return value;
    }

    private static string ReadProcValue(string path, string name)
    {
        var line = File.ReadLines(path).First(value =>
            value.IndexOf(':', StringComparison.Ordinal) is var separator && separator >= 0 &&
            value[..separator].Trim().Equals(name, StringComparison.Ordinal));
        return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    private static long ReadProcKilobytes(string path, string name)
    {
        var value = ReadProcValue(path, name);
        return long.Parse(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
    }

    private sealed class ManifestReader(string root)
    {
        private readonly Dictionary<Guid, ArtifactManifestV2> _syntheticReferences = [];
        private bool _loadedSyntheticReferences;

        internal ArtifactManifestV2 Read(Guid artifactId)
        {
            if (_syntheticReferences.TryGetValue(artifactId, out var reference))
            {
                return reference;
            }
            using (var connection = OpenJournal(root))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT sidecar_relative_path FROM raw_captures WHERE raw_artifact_id = $id
                    UNION ALL
                    SELECT sidecar_relative_path FROM processing_outputs WHERE artifact_id = $id
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$id", artifactId.ToString("N"));
                if (command.ExecuteScalar() is string relativePath)
                {
                    return Parse(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                }
            }
            LoadSyntheticReferences();
            return _syntheticReferences.TryGetValue(artifactId, out reference)
                ? reference
                : throw new AssertFailedException($"Artifact {artifactId:D} has no local manifest.");
        }

        private void LoadSyntheticReferences()
        {
            if (_loadedSyntheticReferences)
            {
                return;
            }
            _loadedSyntheticReferences = true;
            var synthetic = Path.Combine(root, "calibration", "synthetic");
            if (!Directory.Exists(synthetic))
            {
                return;
            }
            foreach (var path in Directory.EnumerateFiles(synthetic, "*.json", SearchOption.AllDirectories))
            {
                var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
                if (parsed.Document?.Manifest is { } manifest)
                {
                    _syntheticReferences[manifest.Descriptor.Artifact.ArtifactId] = manifest;
                }
            }
        }

        private static ArtifactManifestV2 Parse(string path)
        {
            var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
            return parsed.Document?.Manifest ?? throw new AssertFailedException($"Invalid manifest: {path}");
        }
    }

    private sealed class ContainerResourceSampler(AgentContext hualapai, AgentContext siding) : IDisposable
    {
        private readonly List<ResourceSample> _samples = [];
        private readonly CancellationTokenSource _stopping = new();
        private Task? _samplingTask;
        private string _phase = "startup-and-warmup";
        private string? _pausedAgent;
        private ProcessCounterSample? _hualapaiProcess;
        private ProcessCounterSample? _sidingProcess;
        private ProcessSample? _hualapaiContainer;
        private ProcessSample? _sidingContainer;
        private int _unavailableSampleAttempts;

        internal void Start() => _samplingTask = SamplePeriodicallyAsync();

        internal void SetPhase(string phase) => Volatile.Write(ref _phase, phase);

        internal void SetPausedAgent(string? agent) => Volatile.Write(ref _pausedAgent, agent);

        internal Task CaptureAsync() => SampleCoreAsync();

        internal async Task StopAsync()
        {
            if (_samplingTask is null)
            {
                return;
            }
            await _stopping.CancelAsync().ConfigureAwait(false);
            await _samplingTask.ConfigureAwait(false);
            _samplingTask = null;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _stopping.Dispose();
        }

        private async Task SamplePeriodicallyAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                do
                {
                    try
                    {
                        await SampleCoreAsync().ConfigureAwait(false);
                    }
                    catch (AssertFailedException) when (!_stopping.IsCancellationRequested)
                    {
                        // A SIGKILL/restart can briefly make one stats target unavailable.
                        Interlocked.Increment(ref _unavailableSampleAttempts);
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 && !_stopping.IsCancellationRequested)
                    {
                        // Sampling must not contend with the durable writer.
                        Interlocked.Increment(ref _unavailableSampleAttempts);
                    }
                }
                while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        private async Task SampleCoreAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var phase = Volatile.Read(ref _phase);
            var pausedAgent = Volatile.Read(ref _pausedAgent);
            var statsArguments = new List<string> { "stats", "--no-stream", "--format", "{{json .}}" };
            if (pausedAgent != hualapai.Name)
            {
                statsArguments.Add(hualapai.Container);
            }
            if (pausedAgent != siding.Name)
            {
                statsArguments.Add(siding.Container);
            }
            statsArguments.Add(hualapai.CollectorContainer);
            statsArguments.Add(siding.CollectorContainer);
            var output = await DockerAsync([.. statsArguments]).ConfigureAwait(false);
            var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseDockerStats)
                .ToDictionary(static sample => sample.Container, StringComparer.Ordinal);
            var processSamples = await Task.WhenAll(
                pausedAgent == hualapai.Name && _hualapaiProcess is not null
                    ? Task.FromResult(_hualapaiProcess)
                    : ReadProcessCountersAsync(hualapai.Container),
                pausedAgent == siding.Name && _sidingProcess is not null
                    ? Task.FromResult(_sidingProcess)
                    : ReadProcessCountersAsync(siding.Container)).ConfigureAwait(false);
            _hualapaiProcess = processSamples[0];
            _sidingProcess = processSamples[1];
            var backlogs = await Task.WhenAll(
                ReadBacklogAsync(hualapai),
                ReadBacklogAsync(siding)).ConfigureAwait(false);
            var first = pausedAgent == hualapai.Name
                ? _hualapaiContainer
                : values.GetValueOrDefault(hualapai.Container);
            var second = pausedAgent == siding.Name
                ? _sidingContainer
                : values.GetValueOrDefault(siding.Container);
            if (first is not null && second is not null &&
                values.TryGetValue(hualapai.CollectorContainer, out var firstCollector) &&
                values.TryGetValue(siding.CollectorContainer, out var secondCollector))
            {
                _hualapaiContainer = first;
                _sidingContainer = second;
                _samples.Add(new ResourceSample(
                    now,
                    phase,
                    first,
                    second,
                    firstCollector,
                    secondCollector,
                    processSamples[0],
                    processSamples[1],
                    backlogs[0],
                    backlogs[1]));
            }
        }

        internal ResourceSummary Summarize()
        {
            Assert.IsNotEmpty(_samples);
            var first = _samples.Select(static sample => sample.Hualapai).ToArray();
            var second = _samples.Select(static sample => sample.Siding).ToArray();
            var firstCollector = _samples.Select(static sample => sample.HualapaiCollector).ToArray();
            var secondCollector = _samples.Select(static sample => sample.SidingCollector).ToArray();
            var firstProcess = _samples.Select(static sample => sample.HualapaiProcess).ToArray();
            var secondProcess = _samples.Select(static sample => sample.SidingProcess).ToArray();
            var intervals = SamplingIntervals();
            Assert.IsNotEmpty(intervals);
            var phases = _samples.GroupBy(static sample => sample.Phase, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, SummarizePhase, StringComparer.Ordinal);
            Assert.IsLessThanOrEqualTo(5000d, intervals.Max(), "Resource sampling exceeded the five-second maximum gap.");
            Assert.IsGreaterThanOrEqualTo(2, phases["measured-workload"].SampleCount);
            Assert.IsGreaterThanOrEqualTo(2, phases["crash-recovery"].SampleCount);
            Assert.IsGreaterThanOrEqualTo(10, phases["pause-isolation"].SampleCount);
            return new ResourceSummary(
                CumulativeLifetime(firstProcess, static sample => sample.CpuSeconds),
                CumulativeLifetime(secondProcess, static sample => sample.CpuSeconds),
                CpuSeconds(_samples, static sample => sample.HualapaiCollector),
                CpuSeconds(_samples, static sample => sample.SidingCollector),
                firstProcess.Max(static sample => sample.RssBytes),
                secondProcess.Max(static sample => sample.RssBytes),
                firstCollector.Max(static sample => sample.MemoryUsageBytes),
                secondCollector.Max(static sample => sample.MemoryUsageBytes),
                _samples.Max(static sample => sample.HualapaiProcess.RssBytes + sample.SidingProcess.RssBytes),
                _samples.Max(static sample => sample.HualapaiCollector.MemoryUsageBytes + sample.SidingCollector.MemoryUsageBytes),
                _samples.Max(static sample => sample.Hualapai.MemoryUsageBytes + sample.Siding.MemoryUsageBytes +
                    sample.HualapaiCollector.MemoryUsageBytes + sample.SidingCollector.MemoryUsageBytes),
                CumulativeLifetime(firstProcess, static sample => sample.StorageBytesRead),
                CumulativeLifetime(firstProcess, static sample => sample.StorageBytesWritten),
                CumulativeLifetime(secondProcess, static sample => sample.StorageBytesRead),
                CumulativeLifetime(secondProcess, static sample => sample.StorageBytesWritten),
                CumulativeDelta(firstCollector, static sample => sample.BlockIoBytesRead),
                CumulativeDelta(firstCollector, static sample => sample.BlockIoBytesWritten),
                CumulativeDelta(secondCollector, static sample => sample.BlockIoBytesRead),
                CumulativeDelta(secondCollector, static sample => sample.BlockIoBytesWritten),
                CumulativeDelta(first, static sample => sample.NetworkBytesRead),
                CumulativeDelta(first, static sample => sample.NetworkBytesWritten),
                CumulativeDelta(second, static sample => sample.NetworkBytesRead),
                CumulativeDelta(second, static sample => sample.NetworkBytesWritten),
                CumulativeDelta(firstCollector, static sample => sample.NetworkBytesRead),
                CumulativeDelta(firstCollector, static sample => sample.NetworkBytesWritten),
                CumulativeDelta(secondCollector, static sample => sample.NetworkBytesRead),
                CumulativeDelta(secondCollector, static sample => sample.NetworkBytesWritten),
                intervals.Order().ElementAt((intervals.Length - 1) / 2),
                intervals.Max(),
                _samples.Count,
                Volatile.Read(ref _unavailableSampleAttempts),
                phases,
                SummarizeBacklog(_samples.Select(static sample => sample.HualapaiBacklog)),
                SummarizeBacklog(_samples.Select(static sample => sample.SidingBacklog)));
        }

        private static ProcessSample ParseDockerStats(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var blockIo = root.GetProperty("BlockIO").GetString()!.Split('/', 2, StringSplitOptions.TrimEntries);
            var networkIo = root.GetProperty("NetIO").GetString()!.Split('/', 2, StringSplitOptions.TrimEntries);
            return new ProcessSample(
                root.GetProperty("Name").GetString()!,
                double.Parse(root.GetProperty("CPUPerc").GetString()!.TrimEnd('%'), CultureInfo.InvariantCulture),
                ParseBytes(root.GetProperty("MemUsage").GetString()!.Split('/', 2, StringSplitOptions.TrimEntries)[0]),
                ParseBytes(blockIo[0]),
                ParseBytes(blockIo[1]),
                ParseBytes(networkIo[0]),
                ParseBytes(networkIo[1]));
        }

        private static async Task<ProcessCounterSample> ReadProcessCountersAsync(string container)
        {
            var output = await DockerAsync(
                "exec",
                container,
                "/bin/sh",
                "-c",
                "getconf CLK_TCK; cat /proc/1/stat; printf '\\n--STATUS--\\n'; cat /proc/1/status; printf '\\n--IO--\\n'; cat /proc/1/io").ConfigureAwait(false);
            var lines = output.Split('\n');
            var ticksPerSecond = double.Parse(lines[0], CultureInfo.InvariantCulture);
            var stat = lines[1];
            var fields = stat[(stat.LastIndexOf(") ", StringComparison.Ordinal) + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var userTicks = long.Parse(fields[11], CultureInfo.InvariantCulture);
            var systemTicks = long.Parse(fields[12], CultureInfo.InvariantCulture);
            var processStartTicks = long.Parse(fields[19], CultureInfo.InvariantCulture);
            var rss = long.Parse(
                Regex.Match(output, "^VmRSS:\\s+([0-9]+)\\s+kB$", RegexOptions.Multiline | RegexOptions.CultureInvariant).Groups[1].Value,
                CultureInfo.InvariantCulture) * 1024;
            var readBytes = ReadProcIo(output, "read_bytes");
            var writeBytes = ReadProcIo(output, "write_bytes");
            return new ProcessCounterSample(
                processStartTicks,
                (userTicks + systemTicks) / ticksPerSecond,
                rss,
                readBytes,
                writeBytes);
        }

        private static long ReadProcIo(string output, string name) => long.Parse(
            Regex.Match(output, $"^{name}:\\s+([0-9]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant).Groups[1].Value,
            CultureInfo.InvariantCulture);

        private static async Task<BacklogSample> ReadBacklogAsync(AgentContext agent)
        {
            using var connection = OpenJournal(agent.RuntimeRoot);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(r.payload_length), 0), MIN(w.created_unix_ms),
                    (SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed'),
                    (SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed')
                FROM capture_lane_work AS w
                JOIN raw_captures AS r ON r.raw_capture_row_id = w.raw_capture_row_id
                WHERE w.state NOT IN ('completed', 'abandoned');
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            var oldestUnixMilliseconds = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? (long?)null : reader.GetInt64(2);
            var oldestAgeSeconds = oldestUnixMilliseconds is { } oldest
                ? Math.Max(0, (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(oldest)).TotalSeconds)
                : 0;
            var pendingLaneCount = reader.GetInt64(0);
            var pendingRawCount = reader.GetInt64(3);
            var pendingProcessingCount = reader.GetInt64(4);
            return new BacklogSample(
                pendingLaneCount + pendingRawCount + pendingProcessingCount,
                reader.GetInt64(1),
                oldestAgeSeconds,
                pendingLaneCount,
                pendingRawCount,
                pendingProcessingCount);
        }

        private static double CpuSeconds(
            IReadOnlyList<ResourceSample> samples,
            Func<ResourceSample, ProcessSample> selector)
        {
            double seconds = 0;
            for (var index = 1; index < samples.Count; index++)
            {
                var duration = (samples[index].ObservedUtc - samples[index - 1].ObservedUtc).TotalSeconds;
                seconds += selector(samples[index]).CpuPercent / 100d * duration;
            }
            return seconds;
        }

        private static long CumulativeDelta(IReadOnlyList<ProcessSample> samples, Func<ProcessSample, long> selector)
        {
            long total = 0;
            for (var index = 1; index < samples.Count; index++)
            {
                var previous = selector(samples[index - 1]);
                var current = selector(samples[index]);
                total += current >= previous ? current - previous : current;
            }
            return total;
        }

        private static double CumulativeDelta(IReadOnlyList<ProcessCounterSample> samples, Func<ProcessCounterSample, double> selector)
        {
            double total = 0;
            for (var index = 1; index < samples.Count; index++)
            {
                var previous = selector(samples[index - 1]);
                var current = selector(samples[index]);
                total += samples[index].ProcessStartTicks == samples[index - 1].ProcessStartTicks
                    ? Math.Max(0, current - previous)
                    : current;
            }
            return total;
        }

        private static long CumulativeDelta(IReadOnlyList<ProcessCounterSample> samples, Func<ProcessCounterSample, long> selector)
        {
            long total = 0;
            for (var index = 1; index < samples.Count; index++)
            {
                var previous = selector(samples[index - 1]);
                var current = selector(samples[index]);
                total += samples[index].ProcessStartTicks == samples[index - 1].ProcessStartTicks
                    ? Math.Max(0, current - previous)
                    : current;
            }
            return total;
        }

        private static double CumulativeLifetime(
            IReadOnlyList<ProcessCounterSample> samples,
            Func<ProcessCounterSample, double> selector) => selector(samples[0]) + CumulativeDelta(samples, selector);

        private static long CumulativeLifetime(
            IReadOnlyList<ProcessCounterSample> samples,
            Func<ProcessCounterSample, long> selector) => selector(samples[0]) + CumulativeDelta(samples, selector);

        private double[] SamplingIntervals() => _samples.Zip(
            _samples.Skip(1),
            static (previous, current) => (current.ObservedUtc - previous.ObservedUtc).TotalMilliseconds).ToArray();

        private static long ParseBytes(string value)
        {
            var match = Regex.Match(
                value,
                "^([0-9]+(?:\\.[0-9]+)?(?:e[+-]?[0-9]+)?)([A-Za-z]+)?$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new InvalidDataException($"Docker reported an unsupported byte value: {value}");
            }
            var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var multiplier = match.Groups[2].Value switch
            {
                "" or "B" => 1d,
                "kB" => 1_000d,
                "MB" => 1_000_000d,
                "GB" => 1_000_000_000d,
                "KiB" => 1_024d,
                "MiB" => 1_048_576d,
                "GiB" => 1_073_741_824d,
                _ => throw new InvalidDataException($"Docker reported an unsupported byte unit: {match.Groups[2].Value}")
            };
            return checked((long)Math.Round(number * multiplier, MidpointRounding.AwayFromZero));
        }

        private static ResourcePhaseSummary SummarizePhase(IGrouping<string, ResourceSample> group)
        {
            var samples = group.ToArray();
            var firstProcess = samples.Select(static sample => sample.HualapaiProcess).ToArray();
            var secondProcess = samples.Select(static sample => sample.SidingProcess).ToArray();
            return new ResourcePhaseSummary(
                samples.Length,
                (samples[^1].ObservedUtc - samples[0].ObservedUtc).TotalMilliseconds,
                CumulativeDelta(firstProcess, static sample => sample.CpuSeconds),
                CumulativeDelta(secondProcess, static sample => sample.CpuSeconds),
                firstProcess.Max(static sample => sample.RssBytes),
                secondProcess.Max(static sample => sample.RssBytes),
                CumulativeDelta(firstProcess, static sample => sample.StorageBytesRead),
                CumulativeDelta(firstProcess, static sample => sample.StorageBytesWritten),
                CumulativeDelta(secondProcess, static sample => sample.StorageBytesRead),
                CumulativeDelta(secondProcess, static sample => sample.StorageBytesWritten),
                samples.Max(static sample => sample.HualapaiBacklog.PendingCount),
                samples.Max(static sample => sample.SidingBacklog.PendingCount),
                samples.Max(static sample => sample.HualapaiBacklog.PendingBytes),
                samples.Max(static sample => sample.SidingBacklog.PendingBytes),
                samples.Max(static sample => sample.HualapaiBacklog.OldestAgeSeconds),
                samples.Max(static sample => sample.SidingBacklog.OldestAgeSeconds));
        }

        private static BacklogSummary SummarizeBacklog(IEnumerable<BacklogSample> values)
        {
            var samples = values.ToArray();
            return new BacklogSummary(
                samples.Max(static sample => sample.PendingCount),
                samples.Max(static sample => sample.PendingBytes),
                samples.Max(static sample => sample.OldestAgeSeconds),
                samples[^1]);
        }
    }

    private sealed record AgentContext(
        string Name,
        Uri BaseUri,
        string Root,
        string Container,
        string Project,
        string AgentId,
        string LocationId,
        string LocationSource,
        DateTimeOffset SceneUtc,
        string CookieName,
        string OwnerEmail)
    {
        internal string RuntimeRoot => Path.Combine(Root, "runtime");
        internal string CatalogRoot => Path.Combine(Root, "catalog");
        internal string TelemetryRoot => Path.Combine(Root, "telemetry");
        internal string PasswordFile => Path.Combine(Root, "secrets", "owner-password");
        internal string CollectorContainer => $"hvo197-{Name}-collector";
        internal string CentralDenyContainer => $"hvo197-{Name}-central-deny";

        internal static AgentContext Create(
            string name,
            string prefix,
            string agentId,
            string locationId,
            string locationSource,
            DateTimeOffset sceneUtc,
            string cookieName) => new(
                name,
                new Uri(Required($"{prefix}_BASE_URI"), UriKind.Absolute),
                RequiredPath($"{prefix}_ROOT"),
                Required($"{prefix}_CONTAINER"),
                Required($"{prefix}_PROJECT"),
                agentId,
                locationId,
                locationSource,
                sceneUtc,
                cookieName,
                name == "hualapai" ? "hualapai-owner@cameraagent.test" : "siding-owner@cameraagent.test");
    }

    private sealed record AgentSession(HttpClient Client, string CookieName, string CookieValue) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed record AgentEvidence(
        string JpegPath,
        string JpegSha256,
        long JpegLength,
        string CatalogSha256,
        string CatalogInode,
        IReadOnlyList<string> MutableInodes,
        string LocationIdentitySha256,
        int RetentionDays,
        IReadOnlyDictionary<string, string> RecipeIdentities,
        IReadOnlyList<ArtifactEvidence> Artifacts,
        IReadOnlyList<double> StartIntervalsMilliseconds,
        IReadOnlyList<double> CompletionLatenciesMilliseconds);

    private sealed record ArtifactEvidence(
        Guid CaptureId,
        long CaptureSequence,
        Guid ArtifactId,
        string Role,
        string? Variant,
        string Sha256,
        IReadOnlyList<Guid> SourceArtifactIds,
        string? RecipeIdentitySha256);

    private sealed record ContainerInspection(
        string ContainerId,
        string Image,
        string Hostname,
        string Project,
        string Network,
        string SandboxKey,
        int ProcessId,
        bool ReadOnlyRootFileSystem,
        bool TempIsTmpfs,
        IReadOnlyList<string> Environment,
        IReadOnlyList<MountEvidence> Mounts);

    private sealed record MountEvidence(string Source, string Destination, bool ReadWrite);

    private sealed record TopologyEvidence(
        bool ImageIdsEqual,
        bool MutableStateIsolated,
        string HualapaiProject,
        string SidingProject,
        string HualapaiNetwork,
        string SidingNetwork,
        IReadOnlyList<MountEvidence> HualapaiMounts,
        IReadOnlyList<MountEvidence> SidingMounts,
        int CanaryProbeAttempts);

    private sealed record DurableSnapshot(
        long RawCaptureCount,
        long ProcessingOutputCount,
        long PendingRawCaptures,
        long PendingLaneWork,
        long PendingProcessingNodes,
        long DuplicateLogicalOutputs);

    private sealed record RestartEvidence(
        long HualapaiSequenceBefore,
        long HualapaiSequenceAfter,
        long SidingSequenceBefore,
        long SidingSequenceAfter,
        long HualapaiSequenceAfterSidingRestart,
        long SidingSequenceAfterHualapaiRestart,
        long HualapaiUnaffectedBaselineAfterSidingKill,
        long SidingUnaffectedBaselineAfterHualapaiKill,
        Guid HualapaiRecoveredCaptureId,
        Guid SidingSpringRecoveredCaptureId,
        string HualapaiPreCrashLaneState,
        string SidingSpringPreCrashLaneState,
        string HualapaiPostCrashLaneState,
        string SidingSpringPostCrashLaneState,
        double HualapaiKillMilliseconds,
        double SidingSpringKillMilliseconds,
        double HualapaiStartToHealthyMilliseconds,
        double SidingSpringStartToHealthyMilliseconds,
        double HualapaiCrashToHealthyMilliseconds,
        double SidingSpringCrashToHealthyMilliseconds,
        double HualapaiCrashToRecoveryMilliseconds,
        double SidingSpringCrashToRecoveryMilliseconds);

    private sealed record PendingCapture(Guid CaptureId, long Sequence, string State);

    private sealed record PauseEvidence(
        string PausedAgent,
        string UnaffectedAgent,
        long PausedSequence,
        long UnaffectedSequenceBefore,
        long UnaffectedSequenceAfter,
        double DurationMilliseconds,
        string PausedTreeSha256,
        double UnpauseToHealthyMilliseconds,
        double UnpauseToNextGraphMilliseconds,
        Guid RecoveredCaptureId,
        long RecoveredCaptureSequence);

    private sealed record FaultEvidence(PauseEvidence HualapaiPaused, PauseEvidence SidingSpringPaused);
    private sealed record ProcessSample(
        string Container,
        double CpuPercent,
        long MemoryUsageBytes,
        long BlockIoBytesRead,
        long BlockIoBytesWritten,
        long NetworkBytesRead,
        long NetworkBytesWritten);
    private sealed record ProcessCounterSample(
        long ProcessStartTicks,
        double CpuSeconds,
        long RssBytes,
        long StorageBytesRead,
        long StorageBytesWritten);
    private sealed record ResourceSample(
        DateTimeOffset ObservedUtc,
        string Phase,
        ProcessSample Hualapai,
        ProcessSample Siding,
        ProcessSample HualapaiCollector,
        ProcessSample SidingCollector,
        ProcessCounterSample HualapaiProcess,
        ProcessCounterSample SidingProcess,
        BacklogSample HualapaiBacklog,
        BacklogSample SidingBacklog);
    private sealed record ResourceSummary(
        double HualapaiCpuSeconds,
        double SidingCpuSeconds,
        double HualapaiCollectorEstimatedCpuSeconds,
        double SidingCollectorEstimatedCpuSeconds,
        long HualapaiPeakRssBytes,
        long SidingPeakRssBytes,
        long HualapaiCollectorPeakMemoryBytes,
        long SidingCollectorPeakMemoryBytes,
        long CombinedPeakRssBytes,
        long CombinedCollectorPeakMemoryBytes,
        long CombinedTopologyPeakMemoryBytes,
        long HualapaiStorageBytesRead,
        long HualapaiStorageBytesWritten,
        long SidingStorageBytesRead,
        long SidingStorageBytesWritten,
        long HualapaiCollectorBlockIoBytesRead,
        long HualapaiCollectorBlockIoBytesWritten,
        long SidingCollectorBlockIoBytesRead,
        long SidingCollectorBlockIoBytesWritten,
        long HualapaiContainerNetworkBytesRead,
        long HualapaiContainerNetworkBytesWritten,
        long SidingContainerNetworkBytesRead,
        long SidingContainerNetworkBytesWritten,
        long HualapaiCollectorNetworkBytesRead,
        long HualapaiCollectorNetworkBytesWritten,
        long SidingCollectorNetworkBytesRead,
        long SidingCollectorNetworkBytesWritten,
        double MedianSamplingIntervalMilliseconds,
        double MaximumSamplingIntervalMilliseconds,
        int SampleCount,
        int UnavailableSampleAttempts,
        IReadOnlyDictionary<string, ResourcePhaseSummary> Phases,
        BacklogSummary HualapaiBacklog,
        BacklogSummary SidingBacklog);

    private sealed record ResourcePhaseSummary(
        int SampleCount,
        double DurationMilliseconds,
        double HualapaiCpuSeconds,
        double SidingCpuSeconds,
        long HualapaiPeakRssBytes,
        long SidingPeakRssBytes,
        long HualapaiStorageBytesRead,
        long HualapaiStorageBytesWritten,
        long SidingStorageBytesRead,
        long SidingStorageBytesWritten,
        long HualapaiMaximumPendingCount,
        long SidingMaximumPendingCount,
        long HualapaiMaximumPendingBytes,
        long SidingMaximumPendingBytes,
        double HualapaiMaximumOldestAgeSeconds,
        double SidingMaximumOldestAgeSeconds);

    private sealed record BacklogSample(
        long PendingCount,
        long PendingBytes,
        double OldestAgeSeconds,
        long PendingLaneCount,
        long PendingRawCount,
        long PendingProcessingCount);
    private sealed record BacklogSummary(
        long MaximumPendingCount,
        long MaximumPendingBytes,
        double MaximumOldestAgeSeconds,
        BacklogSample Final);

    private sealed record RuntimeMetricEvidence(
        double TotalAllocatedBytes,
        double LargeObjectHeapBytes,
        double PinnedObjectHeapBytes,
        double LargeObjectHeapFragmentationBytes,
        double GarbageCollectionCount,
        double GarbageCollectionPauseSeconds,
        double ProcessCpuSeconds,
        double ProcessWorkingSetBytes);

    private sealed record RuntimeWorkloadEvidence(
        double ElapsedMilliseconds,
        double AllocatedBytes,
        double LargeObjectHeapBeforeBytes,
        double LargeObjectHeapAfterBytes,
        double PinnedObjectHeapBeforeBytes,
        double PinnedObjectHeapAfterBytes,
        double LargeObjectHeapFragmentationBeforeBytes,
        double LargeObjectHeapFragmentationAfterBytes,
        double GarbageCollections,
        double GarbageCollectionPauseSeconds,
        double ProcessCpuSeconds,
        double ProcessWorkingSetBeforeBytes,
        double ProcessWorkingSetAfterBytes)
    {
        internal static RuntimeWorkloadEvidence Create(
            RuntimeMetricEvidence before,
            RuntimeMetricEvidence after,
            double elapsedMilliseconds) => new(
            elapsedMilliseconds,
            after.TotalAllocatedBytes - before.TotalAllocatedBytes,
            before.LargeObjectHeapBytes,
            after.LargeObjectHeapBytes,
            before.PinnedObjectHeapBytes,
            after.PinnedObjectHeapBytes,
            before.LargeObjectHeapFragmentationBytes,
            after.LargeObjectHeapFragmentationBytes,
            after.GarbageCollectionCount - before.GarbageCollectionCount,
            after.GarbageCollectionPauseSeconds - before.GarbageCollectionPauseSeconds,
            after.ProcessCpuSeconds - before.ProcessCpuSeconds,
            before.ProcessWorkingSetBytes,
            after.ProcessWorkingSetBytes);
    }
}
