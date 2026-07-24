using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed standalone fixture used throughout the smoke scope.")]
public sealed class FullResolutionProductionCatalogStandaloneSmokeTests
{
    private const string CatalogRootEnvironmentVariable = "HVO_CATALOG_PERF_ROOT";
    private const string EvidenceRootEnvironmentVariable = "HVO_ISSUE_171_EVIDENCE_ROOT";
    private const string FixedUtcEnvironmentVariable = "HVO_ISSUE_171_FIXED_UTC";
    private const string ProductionSnapshotVersion = "hyg-v4.2-p3-s2-r1";
    private const string ProductionDatabaseSha256 = "B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2";
    private const long ProductionDatabaseLength = 9_302_016;
    private const long ProductionRowCount = 119_625;
    private const int PreRestartCaptureCount = 12;
    private static readonly DateTimeOffset FixedStartUtc = CreateRunAnchorUtc();
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ExpectedGraph =
        ["Calibration", "RollingCombination", "CalibratedPreview", "Preview", "Annotation", "LocalStorage", "Telemetry"];
    private static readonly FrameArtifactRole[] ExpectedRoles =
    [
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(300_000)]
    public async Task FullResolutionProductionCatalogStandaloneSmokeAsync()
    {
        var catalogRoot = Environment.GetEnvironmentVariable(CatalogRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(catalogRoot))
        {
            Assert.Inconclusive($"Set {CatalogRootEnvironmentVariable} to an installed verified Production HYG 4.2 catalog root.");
        }

        var snapshot = ResolveApprovedSnapshot(catalogRoot);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var configuredEvidenceRoot = Environment.GetEnvironmentVariable(EvidenceRootEnvironmentVariable);
        var resultDirectory = string.IsNullOrWhiteSpace(configuredEvidenceRoot)
            ? TestContext.ResultsDirectory ?? Path.Combine(Path.GetTempPath(), $"issue-171-evidence-{Guid.NewGuid():N}")
            : Path.GetFullPath(configuredEvidenceRoot);
        Directory.CreateDirectory(resultDirectory);
        var jpegPath = Path.Combine(resultDirectory, "issue-171-full-resolution-annotated.jpg");
        var evidencePath = Path.Combine(resultDirectory, "issue-171-standalone-smoke.json");

        var process = Process.GetCurrentProcess();
        var started = Stopwatch.StartNew();
        var processIoBefore = ReadProcessIo();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var rssBefore = process.WorkingSet64;
        var lohBefore = ReadLohSize();
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateProductionSmokeAsync(
            catalogRoot,
            FixedStartUtc).ConfigureAwait(false);
        var manifests = new ManifestReader(fixture.Root);
        var sampler = new RuntimeSampler(fixture.Root, process, () => fixture.UtcNow);

        var config = await fixture.Services.GetRequiredService<ICameraAgentConfigurationAccessor>()
            .WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        AssertFullResolutionConfiguration(config);

        var observations = await WaitForCompleteCapturesAsync(
            fixture,
            PreRestartCaptureCount,
            0,
            sampler).ConfigureAwait(false);
        var evidenceCapture = observations[^1].Capture;
        var rawManifests = observations
            .Select(observation => manifests.Read(
                observation.Capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw).ArtifactId))
            .OrderBy(static manifest => manifest.Descriptor.Capture.CaptureSequence)
            .ToArray();
        var cadence = AssertCadence(rawManifests);
        var evidenceManifest = rawManifests[^1];
        AssertProductionCaptureProvenance(evidenceManifest, snapshot);
        AssertArtifactContracts(fixture.Root, evidenceCapture, manifests);

        byte[] annotatedJpeg;
        string annotatedJpegSha256;
        long downloadedBytes;
        using (var ownerClient = await fixture.CreateOwnerClientAsync().ConfigureAwait(false))
        {
            downloadedBytes = await AssertHttpAndUiEvidenceAsync(
                ownerClient,
                fixture.Root,
                evidenceCapture,
                manifests).ConfigureAwait(false);
            var annotated = evidenceCapture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview);
            using var response = await ownerClient.GetAsync(new Uri(
                $"/api/v1/operations/artifacts/{annotated.ArtifactId:D}/preview",
                UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(JpegImageCodec.MediaType, response.Content.Headers.ContentType?.MediaType);
            annotatedJpeg = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            annotatedJpegSha256 = Convert.ToHexString(SHA256.HashData(annotatedJpeg));
            var decoded = JpegImageCodec.DecodeJpeg(annotatedJpeg);
            Assert.AreEqual(1936, decoded.Width);
            Assert.AreEqual(1216, decoded.Height);
            Assert.AreEqual(CameraPixelFormat.Mono8, decoded.PixelFormat);
            Assert.IsGreaterThan(1, decoded.PixelData.Span.ToArray().Distinct().Count());
            AssertOverlayPixels(fixture.Root, evidenceCapture, manifests);
            var annotatedManifest = manifests.Read(annotated.ArtifactId);
            var preview = evidenceCapture.Artifacts.Single(artifact =>
                artifact.ArtifactId == annotatedManifest.Descriptor.Artifact.SourceArtifactIds.Single());
            using var previewResponse = await ownerClient.GetAsync(new Uri(
                $"/api/v1/operations/artifacts/{preview.ArtifactId:D}/preview",
                UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode);
            AssertDecodedJpegOverlay(
                JpegImageCodec.DecodeJpeg(await previewResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false)),
                decoded);
        }
        await File.WriteAllBytesAsync(jpegPath, annotatedJpeg).ConfigureAwait(false);

        var completedArtifactChecksums = observations
            .SelectMany(static observation => observation.Capture.Artifacts)
            .ToDictionary(static artifact => artifact.ArtifactId, static artifact => artifact.ChecksumSha256);
        var rawCaptureCountWhenInterruptionArmed = ReadDurableSnapshot(fixture.Root).RawCaptureCount;
        fixture.ArmLaneInterruption();
        await WaitForConditionAsync(
            () =>
            {
                var snapshot = ReadDurableSnapshot(fixture.Root);
                return snapshot.RawCaptureCount > rawCaptureCountWhenInterruptionArmed &&
                    fixture.LaneInterruptionCount > 0 &&
                    snapshot.PendingLaneWork + snapshot.PendingProcessingNodes > 0;
            },
            "a post-measurement capture to enter durable processing",
            sampler).ConfigureAwait(false);
        var coordinator = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var paused = await coordinator.PauseAsync(
            $"issue-171-pause-{Guid.NewGuid():N}",
            coordinator.Snapshot.Version,
            "issue-171-production-smoke",
            "full-resolution restart evidence",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);
        var preRestartMaximumSequence = ReadMaximumCaptureSequence(fixture.Root);
        var measuredMaximumSequence = rawManifests[^1].Descriptor.Capture.CaptureSequence;
        var interruptedCaptureCount = checked((int)(preRestartMaximumSequence - measuredMaximumSequence));
        Assert.IsGreaterThan(0, interruptedCaptureCount);
        var interruptedRawChecksums = ReadRawArtifactIds(
                fixture.Root,
                measuredMaximumSequence + 1,
                preRestartMaximumSequence)
            .ToDictionary(
                static artifactId => artifactId,
                artifactId => manifests.Read(artifactId).Descriptor.Artifact.ChecksumSha256);
        Assert.HasCount(interruptedCaptureCount, interruptedRawChecksums);
        var beforeRestart = ReadDurableSnapshot(fixture.Root);
        Assert.IsGreaterThan(0L, beforeRestart.PendingLaneWork + beforeRestart.PendingProcessingNodes,
            "The restart must begin while durable local work is still pending.");

        await fixture.StopHostAsync().ConfigureAwait(false);
        var stoppedBeforeRestart = ReadDurableSnapshot(fixture.Root);
        Assert.AreEqual(beforeRestart.RawCaptureCount, stoppedBeforeRestart.RawCaptureCount);
        Assert.AreEqual(beforeRestart.ProcessingOutputCount, stoppedBeforeRestart.ProcessingOutputCount);
        Assert.IsGreaterThan(0L, stoppedBeforeRestart.PendingLaneWork + stoppedBeforeRestart.PendingProcessingNodes,
            "The stopped host must leave interrupted durable work for startup recovery.");
        fixture.DisarmLaneInterruption();
        await fixture.StartStoppedHostAsync().ConfigureAwait(false);
        await WaitForConditionAsync(
            () => fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>().Snapshot is
            { IsInitialized: true, State: CaptureAdmissionState.Paused },
            "paused standalone state to recover",
            sampler).ConfigureAwait(false);
        var restartDrainDuration = await WaitForDurableConvergenceAsync(fixture.Root, sampler).ConfigureAwait(false);
        var afterRestart = ReadDurableSnapshot(fixture.Root);
        Assert.AreEqual(beforeRestart.RawCaptureCount, afterRestart.RawCaptureCount);
        Assert.IsGreaterThanOrEqualTo(beforeRestart.ProcessingOutputCount, afterRestart.ProcessingOutputCount);
        Assert.AreEqual(0L, afterRestart.DuplicateLogicalOutputs);
        AssertArtifactChecksums(fixture.Root, completedArtifactChecksums, manifests);
        AssertArtifactChecksums(fixture.Root, interruptedRawChecksums, manifests);
        var recoveredInterruptedCaptures = await WaitForCompleteCapturesAsync(
            fixture,
            interruptedCaptureCount,
            measuredMaximumSequence,
            sampler).ConfigureAwait(false);
        Assert.AreEqual(preRestartMaximumSequence, recoveredInterruptedCaptures[^1].Capture.CaptureSequence);
        foreach (var recoveredCapture in recoveredInterruptedCaptures)
        {
            AssertArtifactContracts(fixture.Root, recoveredCapture.Capture, manifests);
        }
        AssertArtifactChecksums(
            fixture.Root,
            recoveredInterruptedCaptures
                .SelectMany(static observation => observation.Capture.Artifacts)
                .ToDictionary(static artifact => artifact.ArtifactId, static artifact => artifact.ChecksumSha256),
            manifests);

        using (var recoveredClient = await fixture.CreateOwnerClientAsync().ConfigureAwait(false))
        {
            var annotated = evidenceCapture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview);
            using var recovered = await recoveredClient.GetAsync(new Uri(
                $"/api/v1/operations/artifacts/{annotated.ArtifactId:D}/preview",
                UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
            Assert.AreEqual(
                annotatedJpegSha256,
                Convert.ToHexString(SHA256.HashData(await recovered.Content.ReadAsByteArrayAsync().ConfigureAwait(false))));
        }

        var recoveredCoordinator = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var resumed = await recoveredCoordinator.ResumeAsync(
            $"issue-171-resume-{Guid.NewGuid():N}",
            recoveredCoordinator.Snapshot.Version,
            "issue-171-production-smoke",
            "full-resolution restart evidence",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, resumed.State);
        var resumedCapture = (await WaitForCompleteCapturesAsync(
            fixture,
            1,
            preRestartMaximumSequence,
            sampler).ConfigureAwait(false))[0].Capture;
        Assert.IsGreaterThan(preRestartMaximumSequence, resumedCapture.CaptureSequence);
        var finalCoordinator = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var finalPause = await finalCoordinator.PauseAsync(
            $"issue-171-final-pause-{Guid.NewGuid():N}",
            finalCoordinator.Snapshot.Version,
            "issue-171-production-smoke",
            "final performance drain",
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, finalPause.State);
        var finalDrainDuration = await WaitForDurableConvergenceAsync(fixture.Root, sampler).ConfigureAwait(false);
        sampler.Sample();
        await fixture.StopHostAsync().ConfigureAwait(false);
        Assert.IsEmpty(fixture.OutboundAttempts);

        process.Refresh();
        var processIoAfter = ReadProcessIo();
        var cpuAfter = process.TotalProcessorTime;
        var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);
        var rssAfter = process.WorkingSet64;
        var lohAfter = ReadLohSize();
        var finalDurable = ReadDurableSnapshot(fixture.Root);
        Assert.AreEqual(0L, finalDurable.PendingRawCaptures);
        Assert.AreEqual(0L, finalDurable.PendingLaneWork);
        Assert.AreEqual(0L, finalDurable.PendingProcessingNodes);
        Assert.AreEqual(0L, finalDurable.DuplicateLogicalOutputs);

        var selectedCatalogRowIds = evidenceManifest.Scene!.Objects!
            .Select(static item => item.Id)
            .Where(static id => !id.StartsWith("solar-system:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.IsNotEmpty(selectedCatalogRowIds);
        var completionLatencies = observations.Select(observation =>
            (observation.ObservedCompleteUtc - manifests.Read(
                observation.Capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw).ArtifactId)
                .Descriptor.Timing.ExposureStartedUtc).TotalMilliseconds).ToArray();
        var captureStartIntervals = cadence.ActualStartIntervalMilliseconds;
        var evidence = new
        {
            schemaVersion = "issue-171-standalone-smoke-v1",
            trial = Environment.GetEnvironmentVariable("HVO_ISSUE_171_TRIAL") ?? "local",
            revision = new
            {
                commit = Environment.GetEnvironmentVariable("HVO_ISSUE_171_REVISION") ?? "working-tree",
                branch = Environment.GetEnvironmentVariable("HVO_ISSUE_171_BRANCH") ?? "unknown",
                dirtyStateSha256 = Environment.GetEnvironmentVariable("HVO_ISSUE_171_DIRTY_SHA256") ?? "unrecorded"
            },
            environment = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                sdk = Environment.GetEnvironmentVariable("HVO_ISSUE_171_SDK_VERSION") ?? "unrecorded",
                configuration = "Release",
                processorCount = Environment.ProcessorCount,
                processorModel = ReadProcessorModel(),
                totalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                serverGarbageCollection = System.Runtime.GCSettings.IsServerGC,
                storageType = Environment.GetEnvironmentVariable("HVO_ISSUE_171_STORAGE_TYPE") ?? "unrecorded",
                externalServiceVersions = "N/A: LogicHost and shared central services are intentionally absent",
                hostMode = "in-process Kestrel, standalone central integration disabled"
            },
            workload = new
            {
                width = 1936,
                height = 1216,
                pixelFormat = CameraPixelFormat.Mono16.ToString(),
                strideBytes = 3872,
                reducedSampleWidth = 484,
                reducedSampleHeight = 304,
                pixelCountIncrease = 16,
                exposureSeconds = 5,
                cadenceMode = CaptureCadenceMode.MinimumStartInterval.ToString(),
                runClockAnchorUtc = FixedStartUtc,
                fixedSceneUtc = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
                virtualSkySeed = 2025,
                syntheticCalibrationSeed = 195,
                rollingWindowSize = 5,
                initialBacklog = 0,
                concurrency = 1,
                preRestartCaptures = observations.Length,
                rawCapturesAtRestart = beforeRestart.RawCaptureCount,
                resumedCaptures = finalDurable.RawCaptureCount - afterRestart.RawCaptureCount,
                samplingIntervalMilliseconds = 100
            },
            catalog = new
            {
                snapshot.SnapshotVersion,
                packageKind = snapshot.PackageKind.ToString(),
                snapshot.ManifestVersion,
                snapshot.CatalogVersion,
                snapshot.SchemaVersion,
                snapshot.PreprocessingVersion,
                databaseSha256 = snapshot.DatabaseSha256.ToUpperInvariant(),
                snapshot.DatabaseLength,
                snapshot.RowCount,
                selectedCatalogRowIds
            },
            location = new
            {
                provenance = evidenceManifest.Descriptor.Location,
                identitySha256 = evidenceManifest.Descriptor.Location!.IdentitySha256
            },
            scene = new
            {
                evidenceManifest.Scene.SceneId,
                evidenceManifest.Scene.RigProfileVersion,
                evidenceManifest.Scene.ProjectionModel,
                evidenceManifest.Scene.ProjectionAlgorithmVersion,
                evidenceManifest.Scene.ProjectionCalibrationVersion,
                evidenceManifest.Scene.AstronomyAlgorithmVersion,
                evidenceManifest.Scene.SceneUtc
            },
            captures = observations.Select(static observation => new
            {
                observation.Capture.CaptureId,
                observation.Capture.CaptureSequence,
                observation.Capture.ExposureStartedUtc,
                observation.ObservedCompleteUtc,
                artifacts = observation.Capture.Artifacts.Select(static artifact => new
                {
                    artifact.ArtifactId,
                    role = artifact.Role.ToString(),
                    artifact.Variant,
                    artifact.ChecksumSha256,
                    artifact.ByteLength,
                    recipe = artifact.Recipe?.IdentitySha256,
                    artifact.SourceArtifactIds
                })
            }),
            retainedAnnotatedJpeg = new
            {
                fileName = Path.GetFileName(jpegPath),
                sha256 = annotatedJpegSha256,
                byteLength = annotatedJpeg.LongLength,
                width = 1936,
                height = 1216,
                pixelFormat = CameraPixelFormat.Mono8.ToString()
            },
            performance = new
            {
                elapsedMilliseconds = started.Elapsed.TotalMilliseconds,
                cpuMilliseconds = (cpuAfter - cpuBefore).TotalMilliseconds,
                allocationsBytes = allocationsAfter - allocationsBefore,
                rssBeforeBytes = rssBefore,
                rssAfterBytes = rssAfter,
                peakObservedRssBytes = sampler.PeakRssBytes,
                lohSizeAfterLastGcBeforeBytes = lohBefore,
                lohSizeAfterLastGcAfterBytes = lohAfter,
                maximumObservedLohSizeAfterGcBytes = sampler.MaximumObservedLohSizeAfterGcBytes,
                processIo = ProcessIoSnapshot.Difference(processIoBefore, processIoAfter),
                retainedFilesystemBytes = DirectoryBytes(fixture.Root),
                downloadedBytes,
                completedCaptureCount = finalDurable.RawCaptureCount,
                workflowThroughputCapturesPerSecond = finalDurable.RawCaptureCount / started.Elapsed.TotalSeconds,
                sustainedCadenceCapturesPerSecond = (rawManifests.Length - 1) /
                    (rawManifests[^1].Descriptor.CycleEvidence!.ModuleCallStartedUtc -
                        rawManifests[0].Descriptor.CycleEvidence!.ModuleCallStartedUtc).TotalSeconds,
                captureStartIntervalMilliseconds = captureStartIntervals,
                requestedStartIntervalMilliseconds = cadence.RequestedStartIntervalMilliseconds,
                monotonicStartJitterMilliseconds = cadence.MonotonicStartJitterMilliseconds,
                captureStartReasons = cadence.StartReasons,
                completionLatencyMilliseconds = completionLatencies,
                peakRawCaptureBacklog = sampler.PeakRawCaptureBacklog,
                peakLaneBacklog = sampler.PeakLaneBacklog,
                peakLaneBacklogBytes = sampler.PeakLaneBacklogBytes,
                maximumObservedLaneBacklogAgeMilliseconds = sampler.MaximumObservedLaneBacklogAgeMilliseconds,
                peakProcessingBacklog = sampler.PeakProcessingBacklog,
                restartDrainMilliseconds = restartDrainDuration.TotalMilliseconds,
                finalDrainMilliseconds = finalDrainDuration.TotalMilliseconds,
                drained = finalDurable.PendingRawCaptures == 0 && finalDurable.PendingLaneWork == 0 &&
                    finalDurable.PendingProcessingNodes == 0
            },
            restart = new
            {
                beforeRestart,
                stoppedBeforeRestart,
                afterRestart,
                recoveredInterruptedCaptureIds = recoveredInterruptedCaptures
                    .Select(static observation => observation.Capture.CaptureId),
                resumedCaptureSequence = resumedCapture.CaptureSequence,
                pendingDurableWorkBeforeRestart = beforeRestart.PendingLaneWork + beforeRestart.PendingProcessingNodes,
                duplicateLogicalOutputs = finalDurable.DuplicateLogicalOutputs
            },
            result = new
            {
                equivalentBaseline = "N/A: the canonical reduced sample uses a fixture-oriented physical response, 25-second cadence, no synthetic-reference correction, and central/archive steps; scaling it would not measure the same path",
                nearestComparators = "canonical reduced standalone acceptance plus W1/W2 recipe and representative local-graph gates",
                arrivalBudgetSeconds = 5,
                sustainedArrivalBudget = captureStartIntervals.All(static interval => interval is >= 4900 and <= 5500),
                regressionDisposition = "absolute candidate baseline; compare future runs against this five-trial record"
            },
            centralTrafficAttempts = fixture.OutboundAttempts.Count
        };
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(evidence, EvidenceJson))
            .ConfigureAwait(false);
        TestContext.AddResultFile(jpegPath);
        TestContext.AddResultFile(evidencePath);
        TestContext.WriteLine($"Annotated JPEG SHA-256: {annotatedJpegSha256}");
        TestContext.WriteLine($"Evidence manifest: {evidencePath}");
    }

    private static ApprovedCatalogSnapshot ResolveApprovedSnapshot(string catalogRoot)
    {
        var snapshot = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(catalogRoot)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Production
        });
        Assert.AreEqual(ProductionSnapshotVersion, snapshot.SnapshotVersion);
        Assert.AreEqual(CatalogSnapshotPackageKind.Production, snapshot.PackageKind);
        Assert.AreEqual(1, snapshot.ManifestVersion);
        Assert.AreEqual("4.2", snapshot.CatalogVersion);
        Assert.AreEqual("2", snapshot.SchemaVersion);
        Assert.AreEqual("3", snapshot.PreprocessingVersion);
        Assert.AreEqual(ProductionDatabaseSha256, snapshot.DatabaseSha256, ignoreCase: true);
        Assert.AreEqual(ProductionDatabaseLength, snapshot.DatabaseLength);
        Assert.AreEqual(ProductionRowCount, snapshot.RowCount);
        return new ApprovedCatalogSnapshot(
            snapshot.SnapshotVersion,
            snapshot.PackageKind,
            snapshot.ManifestVersion,
            snapshot.CatalogVersion,
            snapshot.SchemaVersion,
            snapshot.PreprocessingVersion,
            snapshot.DatabaseSha256,
            snapshot.DatabaseLength,
            snapshot.RowCount);
    }

    private static void AssertFullResolutionConfiguration(CameraModuleConfig config)
    {
        Assert.AreEqual(1936, config.Rig.Sensor.WidthPixels);
        Assert.AreEqual(1216, config.Rig.Sensor.HeightPixels);
        Assert.AreEqual(CameraPixelFormat.Mono16, config.Rig.Sensor.PixelFormat);
        Assert.AreEqual(3872, config.Rig.Sensor.StrideBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(5), config.Rig.Pipeline.CaptureInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(5), config.Rig.Pipeline.DayExposure);
        Assert.AreEqual(TimeSpan.FromSeconds(5), config.Rig.Pipeline.NightExposure);
        Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, config.Rig.Pipeline.CadenceMode);
        Assert.AreEqual(TimeSpan.FromSeconds(5), config.Rig.Pipeline.Envelope!.DayDefaults.Exposure);
        Assert.AreEqual(TimeSpan.FromSeconds(5), config.Rig.Pipeline.Envelope.NightDefaults.Exposure);
        CollectionAssert.AreEqual(
            ExpectedGraph,
            config.ResolveProcessingSteps().OrderBy(static step => step.Order).Select(static step => step.Id).ToArray());
        Assert.IsFalse(config.ResolveProcessingSteps().Any(static step => step.Type.Contains("Upload", StringComparison.Ordinal)));
    }

    private static CadenceEvidence AssertCadence(ArtifactManifestV2[] rawManifests)
    {
        Assert.IsGreaterThanOrEqualTo(3, rawManifests.Length);
        var actualIntervals = new List<double>(rawManifests.Length - 1);
        var requestedIntervals = new List<double>(rawManifests.Length - 1);
        var jitters = new List<double>(rawManifests.Length);
        var reasons = new List<string>(rawManifests.Length);
        for (var index = 0; index < rawManifests.Length; index++)
        {
            var descriptor = rawManifests[index].Descriptor;
            var cycle = descriptor.CycleEvidence;
            Assert.AreEqual(TimeSpan.FromSeconds(5), descriptor.Controls.EffectiveExposure);
            Assert.IsNotNull(cycle);
            Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, cycle.CadenceMode);
            jitters.Add(cycle.MonotonicStartJitter.TotalMilliseconds);
            reasons.Add(cycle.StartReason.ToString());
            if (index == 0)
            {
                Assert.AreEqual(CaptureStartReason.Initial, cycle.StartReason);
                Assert.IsGreaterThanOrEqualTo(FixedStartUtc, cycle.ModuleCallStartedUtc);
                continue;
            }

            Assert.IsTrue(cycle.StartReason is CaptureStartReason.DeadlineReached or CaptureStartReason.DeadlineOverrun);
            Assert.IsNotNull(cycle.ObservedInterExposureGap);
            var interval = cycle.ObservedInterExposureGap.Value;
            var observedUtcInterval = cycle.ModuleCallStartedUtc -
                rawManifests[index - 1].Descriptor.CycleEvidence!.ModuleCallStartedUtc;
            Assert.AreEqual(interval.TotalMilliseconds, observedUtcInterval.TotalMilliseconds, 10d,
                "Persisted monotonic and UTC module-start intervals diverged.");
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.9), interval);
            Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(5.5), interval,
                "The full-resolution graph did not sustain the five-second arrival budget.");
            actualIntervals.Add(interval.TotalMilliseconds);
            requestedIntervals.Add((descriptor.Timing.RequestedStartUtc -
                rawManifests[index - 1].Descriptor.Timing.RequestedStartUtc).TotalMilliseconds);
        }
        return new CadenceEvidence(actualIntervals, requestedIntervals, jitters, reasons);
    }

    private static void AssertProductionCaptureProvenance(
        ArtifactManifestV2 manifest,
        ApprovedCatalogSnapshot snapshot)
    {
        var descriptor = manifest.Descriptor;
        var scene = manifest.Scene;
        Assert.AreEqual(1936, descriptor.Layout.Width);
        Assert.AreEqual(1216, descriptor.Layout.Height);
        Assert.AreEqual(3872, descriptor.Layout.StrideBytes);
        Assert.AreEqual(4_708_352L, descriptor.Layout.ByteLength);
        Assert.AreEqual(CameraPixelFormat.Mono16, descriptor.Layout.PixelFormat);
        Assert.AreEqual("hualapai-cameraagent-standalone-full", descriptor.Location?.LocationId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(descriptor.Location?.IdentitySha256));
        Assert.IsNotNull(scene);
        Assert.AreEqual("HYG 4.2", scene.CatalogName);
        Assert.AreEqual(snapshot.CatalogVersion, scene.CatalogVersion);
        Assert.AreEqual(snapshot.DatabaseSha256, scene.CatalogChecksumSha256, ignoreCase: true);
        Assert.AreEqual(snapshot.SchemaVersion, scene.CatalogSchemaVersion);
        Assert.AreEqual(snapshot.PreprocessingVersion, scene.CatalogPreprocessingVersion);
        Assert.AreEqual("EquidistantFisheye", scene.ProjectionModel);
        Assert.AreEqual("virtual-fisheye-180-equidistant-v1", scene.ProjectionCalibrationVersion);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero), scene.SceneUtc);
        Assert.IsNotNull(scene.Objects);
        Assert.IsNotEmpty(scene.Objects);
        var rowIds = scene.Objects.Select(static item => item.Id).ToArray();
        Assert.IsTrue(rowIds.All(static id => !string.IsNullOrWhiteSpace(id)));
        Assert.AreEqual(rowIds.Length, rowIds.Distinct(StringComparer.Ordinal).Count());
    }

    private static void AssertArtifactContracts(
        string root,
        CameraAgentGalleryCapture capture,
        ManifestReader manifests)
    {
        CollectionAssert.IsSubsetOf(ExpectedRoles, capture.Artifacts.Select(static artifact => artifact.Role).Distinct().ToArray());
        Assert.AreEqual(2, capture.Artifacts.Count(static artifact => artifact.Role == FrameArtifactRole.Preview));
        var acquisitionRawArtifactId = capture.Artifacts
            .Single(static artifact => artifact.Role == FrameArtifactRole.Raw)
            .ArtifactId;
        foreach (var artifact in capture.Artifacts)
        {
            var manifest = manifests.Read(artifact.ArtifactId);
            Assert.IsTrue(manifest.Descriptor.Validate().IsValid);
            Assert.AreEqual(artifact.ChecksumSha256, manifest.Descriptor.Artifact.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(capture.CaptureId, manifest.Descriptor.Capture.CaptureId);
            if (artifact.Role != FrameArtifactRole.Raw)
            {
                Assert.IsNotNull(artifact.Recipe);
                AssertLineageReachesRaw(artifact, acquisitionRawArtifactId, manifests);
            }
        }

        var calibrated = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Calibrated);
        var calibratedManifest = manifests.Read(calibrated.ArtifactId);
        Assert.AreEqual(BuiltInProcessingRecipes.ReferenceCalibration, calibratedManifest.Descriptor.Artifact.Recipe.Name);
        Assert.AreEqual("synthetic-corrected", calibratedManifest.Descriptor.Artifact.Variant);
        Assert.HasCount(5, calibratedManifest.Descriptor.Artifact.SourceArtifactIds);
        Assert.HasCount(4, Directory.EnumerateFiles(
            Path.Combine(root, "calibration", "synthetic"), "*.bin", SearchOption.AllDirectories).ToArray());
        var combined = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Combined);
        var combinedManifest = manifests.Read(combined.ArtifactId);
        Assert.HasCount(5, combinedManifest.Descriptor.Artifact.SourceArtifactIds);
        Assert.AreEqual(TimeSpan.FromSeconds(25).Ticks, ReadOutputTotalIntegrationTicks(root, combined.ArtifactId));
    }

    private static async Task<long> AssertHttpAndUiEvidenceAsync(
        HttpClient client,
        string root,
        CameraAgentGalleryCapture expected,
        ManifestReader manifests)
    {
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        var healthBody = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(healthBody, "Production celestial catalog snapshot is installed", StringComparison.Ordinal);
        StringAssert.Contains(healthBody, ProductionDatabaseSha256, StringComparison.Ordinal);
        StringAssert.Contains(healthBody, ProductionRowCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var summary = await client.GetFromJsonAsync<CameraAgentOperationsSummary>(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.IsNotNull(summary);
        Assert.AreEqual("Disabled", summary.Configuration.Value.CentralIntegration);
        Assert.AreEqual("Disabled", summary.ArtifactOutbox.Value.Availability);
        Assert.AreEqual(0L, summary.ArtifactOutbox.Value.PendingCount);
        var storage = summary.Storage.Value.Single(static item => item.Alias == "raw-ingress");
        Assert.IsTrue(storage.ProbeSucceeded);
        Assert.IsFalse(storage.IsUnderPressure);
        Assert.AreEqual(7, storage.EffectiveRetentionDays);

        var apiCapture = await client.GetFromJsonAsync<CameraAgentGalleryCapture>(new Uri(
            $"/api/v1/operations/gallery/{expected.CaptureId:D}",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.IsNotNull(apiCapture);
        Assert.AreEqual(expected.CaptureId, apiCapture.CaptureId);

        using var gallery = await client.GetAsync(new Uri("/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, gallery.StatusCode);
        using var detail = await client.GetAsync(new Uri($"/gallery/{expected.CaptureId:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
        var detailHtml = await detail.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(detailHtml, "detail-hero", StringComparison.Ordinal);
        StringAssert.Contains(detailHtml, expected.CaptureId.ToString("D"), StringComparison.OrdinalIgnoreCase);

        long downloadedBytes = 0;
        foreach (var artifact in expected.Artifacts)
        {
            using var response = await client.GetAsync(new Uri(
                $"/api/v1/operations/artifacts/{artifact.ArtifactId:D}/content",
                UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(
                HttpStatusCode.OK,
                response.StatusCode,
                DescribeArtifactEvidence(root, artifact.ArtifactId, manifests));
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            downloadedBytes += payload.LongLength;
            Assert.AreEqual(artifact.ByteLength, payload.LongLength);
            Assert.AreEqual(artifact.ChecksumSha256, Convert.ToHexString(SHA256.HashData(payload)), ignoreCase: true);
            Assert.AreEqual(
                artifact.ChecksumSha256,
                response.Headers.GetValues("X-Artifact-SHA256").Single(),
                ignoreCase: true);
            if (artifact.Role is FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview)
            {
                using var previewResponse = await client.GetAsync(new Uri(
                    $"/api/v1/operations/artifacts/{artifact.ArtifactId:D}/preview",
                    UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode);
                var jpeg = await previewResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                downloadedBytes += jpeg.LongLength;
                var decoded = JpegImageCodec.DecodeJpeg(jpeg);
                Assert.AreEqual(1936, decoded.Width);
                Assert.AreEqual(1216, decoded.Height);
                Assert.AreEqual(CameraPixelFormat.Mono8, decoded.PixelFormat);
                Assert.IsGreaterThan(1, decoded.PixelData.Span.ToArray().Distinct().Count());
            }
        }
        return downloadedBytes;
    }

    private static void AssertOverlayPixels(
        string root,
        CameraAgentGalleryCapture capture,
        ManifestReader manifests)
    {
        var annotated = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview);
        var annotatedManifest = manifests.Read(annotated.ArtifactId);
        var sourceId = annotatedManifest.Descriptor.Artifact.SourceArtifactIds.Single();
        var preview = capture.Artifacts.Single(artifact => artifact.ArtifactId == sourceId);
        var annotatedPixels = ReadPayload(root, annotatedManifest);
        var previewPixels = ReadPayload(root, manifests.Read(preview.ArtifactId));
        Assert.AreEqual(1936 * 1216, annotatedPixels.Length);
        Assert.AreEqual(annotatedPixels.Length, previewPixels.Length);
        CollectionAssert.AreNotEqual(previewPixels, annotatedPixels);

        var circlePoints = new[] { (1564, 608), (372, 608), (968, 1204), (968, 12) };
        Assert.IsTrue(circlePoints.All(point => annotatedPixels[point.Item2 * 1936 + point.Item1] >= 96));
        var changedOverlayPixels = 0;
        var changedCardinalPixels = 0;
        for (var y = 0; y < 1216; y++)
        {
            for (var x = 0; x < 1936; x++)
            {
                var radius = Math.Sqrt(Math.Pow(x - 968d, 2) + Math.Pow(y - 608d, 2));
                if (radius is < 575 or > 616)
                {
                    continue;
                }
                var index = y * 1936 + x;
                if (annotatedPixels[index] != previewPixels[index] && annotatedPixels[index] >= 96)
                {
                    changedOverlayPixels++;
                }
                if (annotatedPixels[index] == byte.MaxValue && previewPixels[index] != byte.MaxValue)
                {
                    changedCardinalPixels++;
                }
            }
        }
        Assert.IsGreaterThan(1000, changedOverlayPixels);
        Assert.IsGreaterThan(0, changedCardinalPixels);
    }

    private static void AssertDecodedJpegOverlay(DecodedImage preview, DecodedImage annotated)
    {
        Assert.AreEqual(CameraPixelFormat.Mono8, preview.PixelFormat);
        Assert.AreEqual(preview.Width, annotated.Width);
        Assert.AreEqual(preview.Height, annotated.Height);
        var previewPixels = preview.PixelData.Span;
        var annotatedPixels = annotated.PixelData.Span;
        var changedOverlayPixels = 0;
        for (var y = 0; y < annotated.Height; y++)
        {
            for (var x = 0; x < annotated.Width; x++)
            {
                var radius = Math.Sqrt(Math.Pow(x - 968d, 2) + Math.Pow(y - 608d, 2));
                if (radius is >= 575 and <= 616)
                {
                    var index = y * annotated.StrideBytes + x;
                    if (annotatedPixels[index] - previewPixels[index] >= 24)
                    {
                        changedOverlayPixels++;
                    }
                }
            }
        }
        Assert.IsGreaterThan(500, changedOverlayPixels,
            "The retained JPEG did not preserve the expected image-circle/cardinal overlay after compression.");
    }

    private static async Task<CaptureObservation[]> WaitForCompleteCapturesAsync(
        StandaloneCameraAgentKestrelFixture fixture,
        int count,
        long minimumSequence,
        RuntimeSampler sampler)
    {
        var observations = new Dictionary<long, CaptureObservation>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (observations.Count < count)
        {
            sampler.Sample();
            var gallery = fixture.Services.GetRequiredService<ICameraAgentGallery>();
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(
                    PageSize: 100,
                    MinimumSequence: minimumSequence + 1,
                    EvidenceOrigin: GalleryEvidenceOrigin.Simulated),
                CancellationToken.None).ConfigureAwait(false);
            foreach (var candidate in page.Items.OrderBy(static capture => capture.CaptureSequence))
            {
                if (observations.ContainsKey(candidate.CaptureSequence) ||
                    !ExpectedRoles.All(role => candidate.Artifacts.Any(artifact => artifact.Role == role)) ||
                    candidate.ProcessingNodes.Any(static node => node.Status != "Completed"))
                {
                    continue;
                }
                var detail = await gallery.GetCaptureAsync(candidate.CaptureId, CancellationToken.None).ConfigureAwait(false);
                if (detail?.Detail is not { EvidenceAvailability: "Available", RawRetentionHold: false })
                {
                    continue;
                }
                observations[candidate.CaptureSequence] = new CaptureObservation(detail, fixture.UtcNow);
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for full-resolution standalone captures.");
            }
            if (observations.Count < count)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
        return observations.Values.OrderBy(static observation => observation.Capture.CaptureSequence).Take(count).ToArray();
    }

    private static async Task<TimeSpan> WaitForDurableConvergenceAsync(string root, RuntimeSampler sampler)
    {
        var stopwatch = Stopwatch.StartNew();
        await WaitForConditionAsync(
            () =>
            {
                var snapshot = ReadDurableSnapshot(root);
                return snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                    snapshot.PendingProcessingNodes == 0;
            },
            "standalone durable work to drain",
            sampler).ConfigureAwait(false);
        return stopwatch.Elapsed;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        string description,
        RuntimeSampler sampler)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (!condition())
        {
            sampler.Sample();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for {description}.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private static DurableSnapshot ReadDurableSnapshot(string root) => new(
        ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures;"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM processing_outputs;"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed';"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed';"),
        ReadJournalCount(root, """
            SELECT COALESCE(SUM(output_count - 1), 0)
            FROM (
                SELECT COUNT(*) AS output_count
                FROM processing_outputs
                GROUP BY capture_id, node_id, role, variant
                HAVING COUNT(*) > 1
            );
            """));

    private static long ReadMaximumCaptureSequence(string root) =>
        ReadJournalCount(root, "SELECT COALESCE(MAX(capture_sequence), 0) FROM raw_captures;");

    private static Guid[] ReadRawArtifactIds(string root, long minimumSequence, long maximumSequence)
    {
        using var connection = OpenJournal(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_artifact_id
            FROM raw_captures
            WHERE capture_sequence BETWEEN $minimum AND $maximum
            ORDER BY capture_sequence;
            """;
        command.Parameters.AddWithValue("$minimum", minimumSequence);
        command.Parameters.AddWithValue("$maximum", maximumSequence);
        using var reader = command.ExecuteReader();
        var artifactIds = new List<Guid>();
        while (reader.Read())
        {
            artifactIds.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }
        return artifactIds.ToArray();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Queries are fixed private test constants and contain no external input.")]
    private static long ReadJournalCount(string root, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadOutputTotalIntegrationTicks(string root, Guid artifactId)
    {
        using var connection = OpenJournal(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_integration_ticks FROM processing_outputs WHERE artifact_id = $artifact_id;";
        command.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static LaneBacklogSnapshot ReadLaneBacklog(string root, DateTimeOffset now)
    {
        using var connection = OpenJournal(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(raw.payload_length), 0), MIN(work.created_unix_ms)
            FROM capture_lane_work AS work
            JOIN raw_captures AS raw ON raw.raw_capture_row_id = work.raw_capture_row_id
            WHERE work.state IN ('pending', 'leased', 'retry_wait');
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        var oldest = reader.IsDBNull(2)
            ? (double?)null
            : Math.Max(0, (now - DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))).TotalMilliseconds);
        return new LaneBacklogSnapshot(reader.GetInt64(0), reader.GetInt64(1), oldest);
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

    private static byte[] ReadPayload(string root, ArtifactManifestV2 manifest) => File.ReadAllBytes(
        Path.Combine(root, manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar)));

    private static void AssertArtifactChecksums(
        string root,
        IReadOnlyDictionary<Guid, string> expected,
        ManifestReader manifests)
    {
        foreach (var (artifactId, checksum) in expected)
        {
            var manifest = manifests.Read(artifactId);
            Assert.AreEqual(checksum, manifest.Descriptor.Artifact.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(
                checksum,
                Convert.ToHexString(SHA256.HashData(ReadPayload(root, manifest))),
                ignoreCase: true);
        }
    }

    private static void AssertLineageReachesRaw(
        CameraAgentGalleryArtifact artifact,
        Guid acquisitionRawArtifactId,
        ManifestReader manifests)
    {
        var pending = new Queue<Guid>(artifact.SourceArtifactIds);
        var visited = new HashSet<Guid>();
        var reachesRaw = false;
        while (pending.TryDequeue(out var artifactId))
        {
            if (!visited.Add(artifactId))
            {
                continue;
            }
            var source = manifests.Read(artifactId);
            if (artifactId == acquisitionRawArtifactId)
            {
                reachesRaw = true;
            }
            foreach (var parent in source.Descriptor.Artifact.SourceArtifactIds)
            {
                pending.Enqueue(parent);
            }
        }
        Assert.IsTrue(reachesRaw,
            $"Artifact {artifact.ArtifactId:D} lineage did not reach acquisition raw {acquisitionRawArtifactId:D}.");
    }

    private static string DescribeArtifactEvidence(string root, Guid artifactId, ManifestReader manifests)
    {
        try
        {
            var manifest = manifests.Read(artifactId);
            var reconstruction = FrameReconstructor.TryReconstruct(
                manifest.Descriptor,
                ReadPayload(root, manifest),
                out _);
            return $"Artifact reconstruction: {reconstruction.ReasonCode ?? "valid"}.";
        }
        catch (AssertFailedException exception)
        {
            return exception.Message;
        }
    }

    private static long ReadLohSize()
    {
        var generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static DateTimeOffset CreateRunAnchorUtc()
    {
        var configured = Environment.GetEnvironmentVariable(FixedUtcEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parsed = DateTimeOffset.Parse(
                configured,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal);
            if (parsed.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException($"{FixedUtcEnvironmentVariable} must be UTC.");
            }
            return parsed;
        }
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
    }

    private static ProcessIoSnapshot? ReadProcessIo()
    {
        const string path = "/proc/self/io";
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
        {
            return null;
        }
        var values = File.ReadLines(path)
            .Select(static line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(
                static parts => parts[0],
                static parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        return new ProcessIoSnapshot(
            values["rchar"],
            values["wchar"],
            values["syscr"],
            values["syscw"],
            values["read_bytes"],
            values["write_bytes"]);
    }

    private static string ReadProcessorModel()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model is not null)
            {
                return model.Split(':', 2, StringSplitOptions.TrimEntries)[1];
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static long DirectoryBytes(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Sum(static path => new FileInfo(path).Length);

    private sealed class ManifestReader(string root)
    {
        private readonly Dictionary<Guid, ArtifactManifestV2> _syntheticReferenceCache = [];
        private bool _syntheticReferencesLoaded;

        internal ArtifactManifestV2 Read(Guid artifactId)
        {
            if (_syntheticReferenceCache.TryGetValue(artifactId, out var syntheticReference))
            {
                return syntheticReference;
            }

            using (var connection = OpenJournal(root))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT sidecar_relative_path
                    FROM raw_captures
                    WHERE raw_artifact_id = $artifact_id
                    UNION ALL
                    SELECT sidecar_relative_path
                    FROM processing_outputs
                    WHERE artifact_id = $artifact_id
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
                if (command.ExecuteScalar() is string relativePath)
                {
                    var manifest = ParseManifest(Path.Combine(
                        root,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)),
                        requireManifest: true)!;
                    Assert.AreEqual(artifactId, manifest.Descriptor.Artifact.ArtifactId);
                    return manifest;
                }
            }

            LoadSyntheticReferences();
            return _syntheticReferenceCache.TryGetValue(artifactId, out syntheticReference)
                ? syntheticReference
                : throw new AssertFailedException($"Artifact {artifactId:D} has no local manifest.");
        }

        private void LoadSyntheticReferences()
        {
            if (_syntheticReferencesLoaded)
            {
                return;
            }
            _syntheticReferencesLoaded = true;
            var syntheticRoot = Path.Combine(root, "calibration", "synthetic");
            if (!Directory.Exists(syntheticRoot))
            {
                return;
            }
            foreach (var sidecarPath in Directory.EnumerateFiles(
                         syntheticRoot,
                         "*.json",
                         SearchOption.AllDirectories))
            {
                if (ParseManifest(sidecarPath, requireManifest: false) is { } manifest)
                {
                    _syntheticReferenceCache[manifest.Descriptor.Artifact.ArtifactId] = manifest;
                }
            }
        }

        private static ArtifactManifestV2? ParseManifest(string path, bool requireManifest)
        {
            var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
            if (parsed.Document?.Manifest is { } manifest)
            {
                return manifest;
            }
            if (requireManifest)
            {
                throw new AssertFailedException($"Expected artifact sidecar is not a valid manifest: {path}");
            }
            return null;
        }
    }

    private sealed class RuntimeSampler(string root, Process process, Func<DateTimeOffset> utcNow)
    {
        internal long PeakRssBytes { get; private set; }
        internal long MaximumObservedLohSizeAfterGcBytes { get; private set; }
        internal long PeakRawCaptureBacklog { get; private set; }
        internal long PeakLaneBacklog { get; private set; }
        internal long PeakLaneBacklogBytes { get; private set; }
        internal double MaximumObservedLaneBacklogAgeMilliseconds { get; private set; }
        internal long PeakProcessingBacklog { get; private set; }

        internal void Sample()
        {
            process.Refresh();
            PeakRssBytes = Math.Max(PeakRssBytes, process.WorkingSet64);
            MaximumObservedLohSizeAfterGcBytes = Math.Max(MaximumObservedLohSizeAfterGcBytes, ReadLohSize());
            var database = Path.Combine(root, "journal", "raw-ingress.db");
            if (!File.Exists(database))
            {
                return;
            }
            try
            {
                PeakRawCaptureBacklog = Math.Max(PeakRawCaptureBacklog,
                    ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed';"));
                var lane = ReadLaneBacklog(root, utcNow());
                PeakLaneBacklog = Math.Max(PeakLaneBacklog, lane.Count);
                PeakLaneBacklogBytes = Math.Max(PeakLaneBacklogBytes, lane.Bytes);
                MaximumObservedLaneBacklogAgeMilliseconds = Math.Max(
                    MaximumObservedLaneBacklogAgeMilliseconds,
                    lane.OldestAgeMilliseconds ?? 0);
                PeakProcessingBacklog = Math.Max(PeakProcessingBacklog,
                    ReadJournalCount(root, "SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed';"));
            }
            catch (SqliteException)
            {
                // Startup can expose the database before all schemas are initialized.
            }
        }
    }

    private sealed record CaptureObservation(
        CameraAgentGalleryCapture Capture,
        DateTimeOffset ObservedCompleteUtc);

    private sealed record ApprovedCatalogSnapshot(
        string SnapshotVersion,
        CatalogSnapshotPackageKind PackageKind,
        int ManifestVersion,
        string CatalogVersion,
        string SchemaVersion,
        string PreprocessingVersion,
        string DatabaseSha256,
        long DatabaseLength,
        long RowCount);

    private sealed record LaneBacklogSnapshot(long Count, long Bytes, double? OldestAgeMilliseconds);

    private sealed record CadenceEvidence(
        IReadOnlyList<double> ActualStartIntervalMilliseconds,
        IReadOnlyList<double> RequestedStartIntervalMilliseconds,
        IReadOnlyList<double> MonotonicStartJitterMilliseconds,
        IReadOnlyList<string> StartReasons);

    private sealed record ProcessIoSnapshot(
        long CharactersRead,
        long CharactersWritten,
        long ReadSystemCalls,
        long WriteSystemCalls,
        long StorageBytesRead,
        long StorageBytesWritten)
    {
        internal static ProcessIoSnapshot? Difference(ProcessIoSnapshot? before, ProcessIoSnapshot? after) =>
            before is null || after is null
                ? null
                : new ProcessIoSnapshot(
                    after.CharactersRead - before.CharactersRead,
                    after.CharactersWritten - before.CharactersWritten,
                    after.ReadSystemCalls - before.ReadSystemCalls,
                    after.WriteSystemCalls - before.WriteSystemCalls,
                    after.StorageBytesRead - before.StorageBytesRead,
                    after.StorageBytesWritten - before.StorageBytesWritten);
    }

    private sealed record DurableSnapshot(
        long RawCaptureCount,
        long ProcessingOutputCount,
        long PendingRawCaptures,
        long PendingLaneWork,
        long PendingProcessingNodes,
        long DuplicateLogicalOutputs);
}
