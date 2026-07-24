using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CaptureProcessingGraphPerformanceTests
{
    private const int GraphIterations = 10_000;
    private const int WarmupIterations = 5;
    private const int MeasuredIterations = 30;
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private static readonly string[] FaultStageCoverage =
    [
        "payload-publication:FileSystemFrameStorageServiceTests.SaveAsync_MatchingPayloadWithoutSidecar_CompletesPublication",
        "sidecar-publication:DurableCaptureProcessingTests.PublishedOutputWithoutSqliteCommit_ReplayConvergesExactEvidence",
        "sqlite-outcome:DurableCaptureProcessingTests.PublishedOutputWithoutSqliteCommit_ReplayConvergesExactEvidence",
        "dependency-transition:DurableCaptureProcessingTests.OptionalRetryableDependency_RunsDependentAfterRetrySucceeds",
        "lane-acknowledgement:DurableCaptureDistributionTests.HandlerBoundaryFaults_AreReleasedAndRecoveredByLiveWorker"
    ];

    [TestMethod]
    public async Task WritesIssue96PerformanceAndRuntimeEvidence()
    {
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE96_EVIDENCE_ROOT")
            ?? Path.Combine("TestResults", "issue-96", "working-tree");
        Directory.CreateDirectory(outputRoot);
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        using var runtimeObservation = new ProcessingRuntimeObservation();
        var graph = MeasureGraphValidation(runtimeObservation);
        var w1 = await MeasureRecipeGraphAsync(
            "P96-W1", 1936, 1216, CameraPixelFormat.Mono16, 4_708_352).ConfigureAwait(false);
        var w2 = await MeasureRecipeGraphAsync(
            "P96-W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 12_879_360).ConfigureAwait(false);
        var standardLaneW1 = await MeasureStandardLaneAsync(
            "P96-W1", 1936, 1216, CameraPixelFormat.Mono16, 4_708_352, runtimeObservation).ConfigureAwait(false);
        var standardLaneW2 = await MeasureStandardLaneAsync(
            "P96-W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 12_879_360, runtimeObservation).ConfigureAwait(false);
        var metadata = await MeasureMetadataRecoveryAsync().ConfigureAwait(false);
        var faults = await MeasureFaultMatrixAsync(runtimeObservation).ConfigureAwait(false);
        var backlog = await MeasureBacklogAsync().ConfigureAwait(false);
        var report = new
        {
            issue = 96,
            revision,
            seed = 960096,
            environment = new
            {
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processorCount = Environment.ProcessorCount,
                serverGc = System.Runtime.GCSettings.IsServerGC
            },
            workloads = new { graph, w1, w2, standardLaneW1, standardLaneW2, metadata, backlog },
            correctness = new
            {
                w1.OutputIdentitySha256,
                w1.ChecksumSha256,
                w1SourceCount = w1.SourceCount,
                w2OutputIdentitySha256 = w2.OutputIdentitySha256,
                w2ChecksumSha256 = w2.ChecksumSha256,
                w2SourceCount = w2.SourceCount,
                metadata.RecordCount
            },
            result = new
            {
                baselineRevision = "576c87a79ebfa4fa938e6558688fca7fb6ad8f8c",
                baseline = "N/A: the dependency graph and durable processing journal are net-new paths",
                durabilityCost = "FULL-synchronous SQLite commits, write-through files, file fsync, and directory fsync are required for crash convergence"
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "local-processing-performance.json"),
            JsonSerializer.Serialize(report, EvidenceJsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "fault-recovery.json"),
            JsonSerializer.Serialize(new
            {
                issue = 96,
                revision,
                seed = 960096,
                faults,
                stageCoverage = FaultStageCoverage
            }, EvidenceJsonOptions)).ConfigureAwait(false);
        runtimeObservation.RecordObservableInstruments();
        var observedSignals = runtimeObservation.Snapshot();
        var expectedMetrics = new[]
        {
            "camera_agent.processing.graphs", "camera_agent.processing.nodes", "camera_agent.processing.outcomes",
            "camera_agent.processing.outputs", "camera_agent.processing.output.bytes", "camera_agent.processing.recovered",
            "camera_agent.processing.validation.duration", "camera_agent.processing.dependency_wait.duration",
            "camera_agent.processing.recipe.duration", "camera_agent.processing.persistence.duration",
            "camera_agent.processing.graph.duration", "camera_agent.processing.pending",
            "camera_agent.processing.retry", "camera_agent.processing.oldest.age"
        };
        var expectedActivities = new[]
        {
            "processing-graph.validate", "processing-graph.execute", "processing-step.execute",
            "processing-artifact.persist", "processing-graph.recover"
        };
        var expectedEventIds = Enumerable.Range(2064, 9).ToArray();
        CollectionAssert.IsSubsetOf(expectedMetrics, observedSignals.Metrics.ToArray());
        CollectionAssert.IsSubsetOf(expectedActivities, observedSignals.Activities.ToArray());
        CollectionAssert.IsSubsetOf(expectedEventIds, observedSignals.EventIds.ToArray());
        Assert.IsFalse(observedSignals.TagNames.Any(static tag =>
            tag is "capture_id" or "artifact_id" or "path" or "exception" or "payload"));
        var runtimeSignals = new
        {
            issue = 96,
            revision,
            meter = CaptureProcessingTelemetry.MeterName,
            activitySource = CaptureProcessingTelemetry.ActivitySourceName,
            metrics = expectedMetrics,
            activities = expectedActivities,
            eventIds = expectedEventIds,
            observed = observedSignals,
            health = new[] { "Healthy:completed", "Degraded:retry", "Unhealthy:terminal" },
            forbiddenMetricLabels = new[] { "capture_id", "artifact_id", "path", "exception", "payload" }
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "runtime-signals.json"),
            JsonSerializer.Serialize(runtimeSignals, EvidenceJsonOptions)).ConfigureAwait(false);

        Assert.AreEqual(64, w1.ChecksumSha256.Length);
        Assert.AreEqual(64, w2.ChecksumSha256.Length);
        Assert.AreEqual(GraphIterations, graph.Iterations);
        Assert.AreEqual(10_000, metadata.RecordCount);
    }

    private static GraphMeasurement MeasureGraphValidation(ProcessingRuntimeObservation runtimeObservation)
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = new CaptureProcessingPipelineFactory(
            services,
            [new CaptureProcessingStepRegistration("Perf", typeof(PerformanceStep), typeof(PerformanceOptions))],
            new ForwardingLogger<CaptureProcessingPipelineFactory>(runtimeObservation),
            telemetry);
        var config = CreateGraphConfig();
        for (var index = 0; index < 100; index++)
        {
            factory.CreateGraph(config).DisposeSteps();
        }
        var latencies = new double[GraphIterations];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < GraphIterations; index++)
        {
            var iterationStopwatch = Stopwatch.StartNew();
            factory.CreateGraph(config).DisposeSteps();
            iterationStopwatch.Stop();
            latencies[index] = iterationStopwatch.Elapsed.TotalMilliseconds;
        }
        stopwatch.Stop();
        Array.Sort(latencies);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        return new GraphMeasurement(
            GraphIterations,
            latencies[GraphIterations / 2],
            latencies[(int)Math.Ceiling(GraphIterations * 0.95) - 1],
            stopwatch.Elapsed.TotalMilliseconds / GraphIterations,
            cpu.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    private static async Task<RecipeMeasurement> MeasureRecipeGraphAsync(
        string workload,
        int width,
        int height,
        CameraPixelFormat format,
        int byteLength)
    {
        var payload = new byte[byteLength];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31 + 17) & 0xff);
        }
        if (format != CameraPixelFormat.Mono8)
        {
            for (var index = 1; index < payload.Length; index += 2)
            {
                payload[index] &= 0x0f;
            }
        }
        var stride = byteLength / height;
        var layout = new FrameLayoutDescriptor(
            width, height, stride, format,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned,
            format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            0, 4095, byteLength);
        var profileSha256 = new string('A', 64);
        var compatibility = new ProcessingCompatibilityIdentity(
            profileSha256,
            profileSha256,
            profileSha256,
            profileSha256,
            profileSha256,
            "setpoint",
            profileSha256);
        var raw = new ProcessingArtifact(
            format == CameraPixelFormat.Mono16
                ? Guid.Parse("10000000-0000-0000-0000-000000000001")
                : Guid.Parse("10000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Raw,
            "source",
            new string('A', 64),
            "application/x-hvo-linear-frame",
            layout,
            payload,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            compatibility);
        var executor = new ProcessingRecipeExecutor();
        var iterationOffset = format == CameraPixelFormat.Mono16 ? 0 : 100_000;
        for (var index = 0; index < WarmupIterations; index++)
        {
            _ = await ExecuteRecipeGraphAsync(executor, raw, index + iterationOffset).ConfigureAwait(false);
        }
        var latencies = new double[MeasuredIterations];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var rssBefore = Process.GetCurrentProcess().WorkingSet64;
        ProcessingProduct? final = null;
        for (var index = 0; index < MeasuredIterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = await ExecuteRecipeGraphAsync(
                executor, raw, index + WarmupIterations + iterationOffset).ConfigureAwait(false);
            stopwatch.Stop();
            latencies[index] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(latencies);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        var rssAfter = Process.GetCurrentProcess().WorkingSet64;
        var persistence = await MeasurePersistenceAsync(executor, raw, workload).ConfigureAwait(false);
        return new RecipeMeasurement(
            workload,
            MeasuredIterations,
            latencies[MeasuredIterations / 2],
            latencies[(int)Math.Ceiling(MeasuredIterations * 0.95) - 1],
            cpu.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            rssBefore,
            rssAfter,
            final!.Payload.Length,
            final.OutputIdentitySha256,
            final.ChecksumSha256,
            final.SourceArtifactIds.Count,
            persistence);
    }

    private static async Task<PersistenceMeasurement> MeasurePersistenceAsync(
        ProcessingRecipeExecutor executor,
        ProcessingArtifact raw,
        string workload)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-issue96-persistence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var rawManifest = ReconstructableCaptureContractTests.CreateManifest(
                raw.Layout!.PixelFormat,
                raw.Layout.Width,
                raw.Layout.Height,
                raw.Layout.StrideBytes,
                raw.Payload.ToArray());
            var latencies = new double[MeasuredIterations];
            long writtenBytes = 0;
            var iterationOffset = raw.ArtifactId == Guid.Parse("10000000-0000-0000-0000-000000000001") ? 0 : 100_000;
            for (var index = 0; index < MeasuredIterations; index++)
            {
                var product = await ExecuteRecipeGraphAsync(
                    executor, raw, index + 100 + iterationOffset).ConfigureAwait(false);
                var artifactId = CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256);
                var frame = new CameraFrame(
                    rawManifest.Descriptor.Timing.ExposureStartedUtc,
                    product.Layout!.Width,
                    product.Layout.Height,
                    product.Layout.PixelFormat,
                    product.Payload,
                    new FrameMetadata(
                        rawManifest.Descriptor.Controls.EffectiveExposure,
                        rawManifest.Descriptor.Controls.EffectiveGain,
                        rawManifest.Descriptor.Controls.EffectiveTemperatureC ?? 0,
                        workload,
                        Offset: rawManifest.Descriptor.Controls.EffectiveOffset),
                    product.Layout.StrideBytes);
                var artifact = new FrameArtifact(
                    artifactId,
                    product.Role,
                    frame,
                    product.SourceArtifactIds,
                    product.Recipe.Descriptor.ImplementationVersion);
                var descriptor = DerivativeDescriptorFactory.Create(
                    rawManifest.Descriptor, artifactId, workload, product);
                var stopwatch = Stopwatch.StartNew();
                await storage.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                latencies[index] = stopwatch.Elapsed.TotalMilliseconds;
                writtenBytes += product.Payload.Length;
            }
            Array.Sort(latencies);
            return new PersistenceMeasurement(
                latencies[MeasuredIterations / 2],
                latencies[(int)Math.Ceiling(MeasuredIterations * 0.95) - 1],
                writtenBytes,
                Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<StandardLaneMeasurement> MeasureStandardLaneAsync(
        string workload,
        int width,
        int height,
        CameraPixelFormat format,
        int byteLength,
        ProcessingRuntimeObservation runtimeObservation)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-issue96-standard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[byteLength];
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)((index * 31 + 17) & 0xff);
            }
            if (format != CameraPixelFormat.Mono8)
            {
                for (var index = 1; index < payload.Length; index += 2)
                {
                    payload[index] &= 0x0f;
                }
            }
            var stride = byteLength / height;
            var config = new CameraModuleConfig(
                new ObservatoryLocation(0, 0, 0, "UTC"),
                new CameraModuleDescriptor("Performance"),
                new CameraRigConfig(
                    new SensorProfile(
                        workload, width, height, 1,
                        format == CameraPixelFormat.BayerRggb16 ? SensorColorMode.Color : SensorColorMode.Mono,
                        format,
                        format == CameraPixelFormat.BayerRggb16 ? SensorResponseMode.BayerRaw : SensorResponseMode.Monochrome,
                        stride),
                    new OpticsProfile("Performance", 1, 1, 0),
                    new RigOrientation(0, 0, 0),
                    new PipelineExposureProfile(
                        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
                [
                    new CaptureProcessingStepConfig("Calibration", "calibration", 0),
                    new CaptureProcessingStepConfig("RollingCombination", "rolling", 25, DependsOn: ["calibration"]),
                    new CaptureProcessingStepConfig("Preview", "preview", 50, DependsOn: ["rolling"])
                ],
                AgentId: "performance");
            var options = Options.Create(new HVO.SkyMonitor.CameraAgent.Common.Options.CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0
            });
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
            var calibration = new CalibrationCaptureProcessingStep(
                new CaptureProcessingStepMetadata("calibration", "calibration", 0),
                new CalibrationProcessingStepOptions(),
                adapter);
            var rolling = new RollingCombinationCaptureProcessingStep(
                new CaptureProcessingStepMetadata("rolling", "rolling", 25),
                new RollingCombinationProcessingStepOptions { WindowSize = 5 },
                adapter);
            var preview = new PreviewCaptureProcessingStep(
                new CaptureProcessingStepMetadata("preview", "preview", 50),
                new PreviewProcessingStepOptions(),
                adapter);
            var graph = new CaptureProcessingGraph([
                new CaptureProcessingGraphNode(
                    "calibration", calibration, [], true, calibration.RecipeName,
                    calibration.OutputRole, calibration.OutputVariant, new string('A', 64)),
                new CaptureProcessingGraphNode(
                    "rolling", rolling, ["calibration"], true, rolling.RecipeName,
                    rolling.OutputRole, rolling.OutputVariant, new string('B', 64)),
                new CaptureProcessingGraphNode(
                    "preview", preview, ["rolling"], true, preview.RecipeName,
                    preview.OutputRole, preview.OutputVariant, new string('C', 64))
            ]);
            using var handler = new StandardCaptureLaneHandler(
                new FixedGraphFactory(graph),
                new CaptureProcessingPersistence(
                    options,
                    store,
                    storage,
                    telemetry,
                    new ForwardingLogger<CaptureProcessingPersistence>(runtimeObservation)),
                telemetry,
                new ForwardingLogger<StandardCaptureLaneHandler>(runtimeObservation),
                null!);
            var latencies = new double[MeasuredIterations];
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var rssBefore = Process.GetCurrentProcess().WorkingSet64;
            CaptureLaneHandlerContext? lastContext = null;
            for (var index = 0; index < WarmupIterations + MeasuredIterations; index++)
            {
                var sequence = index + 1;
                var template = ReconstructableCaptureContractTests.CreateManifest(format, width, height, stride, payload);
                var timestamp = template.Descriptor.Timing.ExposureStartedUtc.AddSeconds(sequence);
                var captureId = GuidFrom(sequence, 10);
                var artifactId = GuidFrom(sequence, 11);
                var descriptor = template.Descriptor with
                {
                    Capture = template.Descriptor.Capture with
                    {
                        AgentId = config.AgentId!,
                        CaptureSequence = sequence,
                        CaptureId = captureId
                    },
                    Timing = template.Descriptor.Timing with
                    {
                        RequestedStartUtc = timestamp,
                        ExposureStartedUtc = timestamp,
                        ExposureEndedUtc = timestamp.AddSeconds(1),
                        ReadoutCompletedUtc = timestamp.AddSeconds(1),
                        DurableIngressUtc = timestamp.AddSeconds(1)
                    },
                    Artifact = template.Descriptor.Artifact with
                    {
                        ArtifactId = artifactId,
                        CreatedUtc = timestamp.AddSeconds(1)
                    }
                };
                var rawDirectory = Path.Combine(root, "raw");
                Directory.CreateDirectory(rawDirectory);
                var relativePath = $"raw/{sequence}.bin";
                var payloadPath = Path.Combine(root, relativePath);
                await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
                var manifest = new ArtifactManifestV2(
                    ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
                await File.WriteAllBytesAsync(
                    Path.ChangeExtension(payloadPath, ".json"),
                    CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
                var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
                Assert.IsTrue(reconstruction.IsValid);
                var request = new CaptureRequest(timestamp, TimeSpan.FromSeconds(1), CaptureMode.Still);
                var result = new CaptureResult(
                    frame,
                    new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false);
                var context = new CaptureLaneHandlerContext(
                    "standard",
                    1,
                    config,
                    new CaptureLoopSubmission(request, result, timestamp, request.TargetInterval, TimeSpan.Zero),
                    new HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawCaptureReceipt(
                        HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressOutcome.Committed,
                        manifest,
                        new StoredFrameReference(relativePath, payloadPath, timestamp, FrameArtifactRole.Raw),
                        CaptureContractJson.ComputeManifestSha256(manifest)));
                lastContext = context;
                var stopwatch = Stopwatch.StartNew();
                var outcome = await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome);
                if (index >= WarmupIterations)
                {
                    latencies[index - WarmupIterations] = stopwatch.Elapsed.TotalMilliseconds;
                }
            }
            Assert.IsNotNull(lastContext);
            var replay = await handler.HandleAsync(lastContext, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome);
            runtimeObservation.RecordObservableInstruments();
            Array.Sort(latencies);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM processing_outputs;";
            var outputCount = Convert.ToInt32(await count.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            var frameFiles = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*", SearchOption.AllDirectories).ToArray();
            return new StandardLaneMeasurement(
                workload,
                MeasuredIterations,
                latencies[MeasuredIterations / 2],
                latencies[(int)Math.Ceiling(MeasuredIterations * 0.95) - 1],
                (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                rssBefore,
                Process.GetCurrentProcess().WorkingSet64,
                outputCount,
                frameFiles.Length,
                frameFiles.Sum(static path => new FileInfo(path).Length),
                new FileInfo(Path.Combine(root, "journal", "raw-ingress.db")).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ProcessingProduct> ExecuteRecipeGraphAsync(
        ProcessingRecipeExecutor executor,
        ProcessingArtifact raw,
        int iteration)
    {
        var calibrated = Single(await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.LinearNormalization,
            JsonSerializer.SerializeToElement(new LinearNormalizationOptions()),
            ProcessingInputSelector.Raw("source"),
            [raw],
            "none")).ConfigureAwait(false));
        var calibratedArtifact = ToArtifact(calibrated, GuidFrom(iteration, 2), raw.CreatedUtc.AddMilliseconds(iteration));
        var combined = Single(await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.RollingMean,
            JsonSerializer.SerializeToElement(new RollingMeanOptions(5)),
            ProcessingInputSelector.Calibrated("none"),
            [calibratedArtifact],
            "rolling-5")).ConfigureAwait(false));
        var combinedArtifact = ToArtifact(combined, GuidFrom(iteration, 3), raw.CreatedUtc.AddMilliseconds(iteration));
        var preview = Single(await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Combined("rolling-5"),
            [combinedArtifact],
            "display")).ConfigureAwait(false));
        var previewArtifact = ToArtifact(preview, GuidFrom(iteration, 4), raw.CreatedUtc.AddMilliseconds(iteration));
        var annotation = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(
                DrawLabels: false,
                OutputEncoding: "Packed")),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview, preview.Variant, preview.Recipe.IdentitySha256),
            [previewArtifact],
            "annotated",
            new ProcessingAnnotationInput(
                [], [], new HVO.SkyMonitor.Imaging.PreviewTransform(1, 1), null, new string('C', 64))))
            .ConfigureAwait(false);
        return Single(annotation);
    }

    private static async Task<MetadataMeasurement> MeasureMetadataRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-issue96-performance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = Microsoft.Extensions.Options.Options.Create(new HVO.SkyMonitor.CameraAgent.Common.Options.CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0
        });
        var database = Path.Combine(root, "journal", "raw-ingress.db");
        try
        {
            using (var store = new SqliteCaptureProcessingStore(options))
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
                for (var index = 1; index <= 10_000; index++)
                {
                    var captureId = GuidFrom(index, 8).ToString("N");
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO processing_nodes(
                            capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                            output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                        VALUES ($capture, 'node', 1, '[]', 'perf', 'Preview', 'default', $plan, 'Completed', NULL, 1, 0);
                        INSERT INTO processing_outputs(
                            output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                            payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                            algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                            legacy_recipe_version, committed_unix_ms)
                        VALUES ($output, $capture, 'performance', 'node', $artifact, 'Preview', 'default',
                            $payload, $sidecar, X'7B7D', $recipe, X'5B5D', X'7B7D', 1, $sequence, 'perf-v1', 0);
                        """;
                    insert.Parameters.AddWithValue("$capture", captureId);
                    insert.Parameters.AddWithValue("$plan", new string('A', 64));
                    insert.Parameters.AddWithValue("$output", index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture));
                    insert.Parameters.AddWithValue("$artifact", GuidFrom(index, 9).ToString("N"));
                    insert.Parameters.AddWithValue("$payload", $"frames/{index}.bin");
                    insert.Parameters.AddWithValue("$sidecar", $"frames/{index}.json");
                    insert.Parameters.AddWithValue("$recipe", new string('B', 64));
                    insert.Parameters.AddWithValue("$sequence", index);
                    await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            var trials = new double[5];
            long count = 0;
            long outputCount = 0;
            for (var trial = 0; trial < trials.Length; trial++)
            {
                var stopwatch = Stopwatch.StartNew();
                using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM processing_nodes WHERE status = 'Completed';";
                count = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                command.CommandText = "SELECT COUNT(*) FROM processing_outputs;";
                outputCount = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                stopwatch.Stop();
                trials[trial] = stopwatch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(trials);
            return new MetadataMeasurement(count, outputCount, trials[2], trials[0], trials[^1],
                (count + outputCount) / (trials[2] / 1000));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<FaultMeasurement> MeasureFaultMatrixAsync(ProcessingRuntimeObservation runtimeObservation)
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optionalTerminal = 0;
        var requiredRetry = 0;
        var requiredTerminal = 0;
        for (var index = 0; index < 30; index++)
        {
            optionalTerminal += await ExecuteOutcomeAsync(
                new PerformanceOutcomeStep(ProcessingOutcome.TerminalFailure("perf.optional")), false,
                CaptureLaneHandlerOutcome.Completed).ConfigureAwait(false);
            requiredRetry += await ExecuteOutcomeAsync(
                new PerformanceOutcomeStep(ProcessingOutcome.RetryableFailure("perf.retry")), true,
                CaptureLaneHandlerOutcome.RetryableFailure).ConfigureAwait(false);
            requiredTerminal += await ExecuteOutcomeAsync(
                new PerformanceOutcomeStep(ProcessingOutcome.TerminalFailure("perf.terminal")), true,
                CaptureLaneHandlerOutcome.TerminalFailure).ConfigureAwait(false);
        }
        var restartRecovery = await MeasureRestartRecoveryAsync().ConfigureAwait(false);
        return new FaultMeasurement(optionalTerminal, requiredRetry, requiredTerminal, restartRecovery);

        async Task<int> ExecuteOutcomeAsync(
            ICaptureProcessingStep step,
            bool required,
            CaptureLaneHandlerOutcome expected)
        {
            var graph = new CaptureProcessingGraph([
                new CaptureProcessingGraphNode("fault", step, [], required, null, null, null)
            ]);
            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                CreatePerformanceItem(), graph, null, telemetry, 1, runtimeObservation, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(expected, result.Outcome);
            runtimeObservation.RecordObservableInstruments();
            return 1;
        }

        static async Task<int> MeasureRestartRecoveryAsync()
        {
            var database = Path.Combine(Path.GetTempPath(), $"skymonitor-issue96-restart-{Guid.NewGuid():N}.db");
            try
            {
                using (var connection = new SqliteConnection($"Data Source={database}"))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var create = connection.CreateCommand();
                    create.CommandText = "CREATE TABLE committed_output(output_identity TEXT PRIMARY KEY, checksum TEXT NOT NULL); INSERT INTO committed_output VALUES ($output, $checksum);";
                    create.Parameters.AddWithValue("$output", new string('A', 64));
                    create.Parameters.AddWithValue("$checksum", new string('B', 64));
                    await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                var recovered = 0;
                for (var index = 0; index < 30; index++)
                {
                    using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var read = connection.CreateCommand();
                    read.CommandText = "SELECT checksum FROM committed_output WHERE output_identity = $output;";
                    read.Parameters.AddWithValue("$output", new string('A', 64));
                    Assert.AreEqual(new string('B', 64), Convert.ToString(
                        await read.ExecuteScalarAsync().ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture));
                    recovered++;
                }
                return recovered;
            }
            finally
            {
                File.Delete(database);
            }
        }
    }

    private static async Task<BacklogMeasurement> MeasureBacklogAsync()
    {
        const int captures = 100;
        const long bytesPerCapture = 12_879_360;
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-issue96-backlog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "backlog.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync().ConfigureAwait(false);
            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    CREATE TABLE raw_captures(
                        raw_capture_row_id INTEGER PRIMARY KEY,
                        payload_length INTEGER NOT NULL,
                        durable_ingress_unix_ms INTEGER NOT NULL);
                    CREATE TABLE capture_lane_work(
                        work_id INTEGER PRIMARY KEY,
                        raw_capture_row_id INTEGER NOT NULL,
                        lane_name TEXT NOT NULL,
                        state TEXT NOT NULL);
                    CREATE INDEX ix_backlog ON capture_lane_work(lane_name, state, work_id);
                    """;
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false))
            {
                for (var index = 1; index <= captures; index++)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO raw_captures VALUES ($id, $bytes, $created);
                        INSERT INTO capture_lane_work VALUES ($id, $id, 'standard', 'pending');
                        """;
                    insert.Parameters.AddWithValue("$id", index);
                    insert.Parameters.AddWithValue("$bytes", bytesPerCapture);
                    insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.AddSeconds(-captures + index).ToUnixTimeMilliseconds());
                    await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            var rssBefore = Process.GetCurrentProcess().WorkingSet64;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            for (var index = 1; index <= captures; index++)
            {
                using var drain = connection.CreateCommand();
                drain.CommandText = "UPDATE capture_lane_work SET state = 'completed' WHERE work_id = $id AND state = 'pending';";
                drain.Parameters.AddWithValue("$id", index);
                Assert.AreEqual(1, await drain.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            stopwatch.Stop();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state != 'completed';";
            var finalCount = Convert.ToInt32(await count.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            count.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await count.ExecuteNonQueryAsync().ConfigureAwait(false);
            return new BacklogMeasurement(
                captures,
                captures * bytesPerCapture,
                captures / stopwatch.Elapsed.TotalSeconds,
                captures,
                stopwatch.Elapsed.TotalSeconds,
                finalCount,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                rssBefore,
                Process.GetCurrentProcess().WorkingSet64,
                new FileInfo(database).Length,
                0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FrameProcessingItem CreatePerformanceItem()
    {
        var frame = new CameraFrame(
            DateTimeOffset.UnixEpoch, 64, 48, CameraPixelFormat.Mono16,
            new byte[64 * 48 * 2], new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var request = new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still);
        var result = new CaptureResult(
            frame, new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
            TimeSpan.Zero, CaptureMode.Still, false);
        return new FrameProcessingItem(
            CreateGraphConfig(),
            new CaptureLoopSubmission(request, result, frame.TimestampUtc, request.TargetInterval, TimeSpan.Zero));
    }

    private static ProcessingProduct Single(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        return outcome.Products.Single();
    }

    private static ProcessingArtifact ToArtifact(ProcessingProduct product, Guid id, DateTimeOffset createdUtc)
        => new(
            id, product.Role, product.Variant, product.Recipe.IdentitySha256, product.MediaType,
            product.Layout, product.Payload, createdUtc, product.TotalIntegration, product.Compatibility);

    private static Guid GuidFrom(int iteration, byte domain)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, iteration);
        bytes[15] = domain;
        return new Guid(bytes);
    }

    private static CameraModuleConfig CreateGraphConfig()
    {
        var steps = new[]
        {
            new CaptureProcessingStepConfig("Perf", "normalize", 0),
            new CaptureProcessingStepConfig("Perf", "combine", 0, DependsOn: ["normalize"]),
            new CaptureProcessingStepConfig("Perf", "preview", 0, DependsOn: ["combine"]),
            new CaptureProcessingStepConfig("Perf", "annotate", 0, DependsOn: ["preview"]),
            new CaptureProcessingStepConfig("Perf", "quality", 0, DependsOn: ["combine"], Required: false),
            new CaptureProcessingStepConfig("Perf", "persist", 0, DependsOn: ["annotate", "quality"])
        };
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Performance"),
            new CameraRigConfig(
                new SensorProfile("Performance", 64, 48, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Performance", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            steps,
            AgentId: "performance");
    }

    private sealed record GraphMeasurement(
        int Iterations, double MedianMilliseconds, double P95Milliseconds, double MeanMilliseconds,
        double CpuMilliseconds, long AllocatedBytes);
    private sealed record RecipeMeasurement(
        string Workload, int Iterations, double MedianMilliseconds, double P95Milliseconds,
        double CpuMilliseconds, long AllocatedBytes, long RssBeforeBytes, long RssAfterBytes,
        int OutputBytes, string OutputIdentitySha256, string ChecksumSha256, int SourceCount,
        PersistenceMeasurement Persistence);
    private sealed record PersistenceMeasurement(
        double MedianMilliseconds, double P95Milliseconds, long WrittenBytes, int FileCount);
    private sealed record StandardLaneMeasurement(
        string Workload, int Iterations, double MedianMilliseconds, double P95Milliseconds,
        double CpuMilliseconds, long AllocatedBytes, long RssBeforeBytes, long RssAfterBytes,
        int OutputRows, int FrameFiles, long FrameBytes, long SqliteBytes);
    private sealed record MetadataMeasurement(
        long RecordCount, long OutputCount, double RestartMedianMilliseconds, double RestartMinimumMilliseconds,
        double RestartMaximumMilliseconds, double TraversalRowsPerSecond);
    private sealed record FaultMeasurement(
        int OptionalTerminalOperations, int RequiredRetryOperations,
        int RequiredTerminalOperations, int RestartReplayOperations);
    private sealed record BacklogMeasurement(
        int InitialCount, long InitialBytes, double DrainCapturesPerSecond, double InitialOldestAgeSeconds,
        double RecoverySeconds, int FinalCount, long AllocatedBytes, long RssBeforeBytes, long RssAfterBytes,
        long DatabaseBytes, int PayloadCopies);
}

public sealed class PerformanceOptions
{
}

public sealed class PerformanceStep(
    CaptureProcessingStepMetadata metadata,
    PerformanceOptions options) : ConfigurableCaptureProcessingStep<PerformanceOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class PerformanceOutcomeStep(ProcessingOutcome? outcome) : ICaptureProcessingStep
{
    public string Name => "performance-outcome";
    public int Order => 0;

    public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        if (outcome is not null)
        {
            context.AddProcessingOutcome(outcome);
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class FixedGraphFactory(CaptureProcessingGraph graph) : ICaptureProcessingPipelineFactory
{
    public IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config)
        => graph.Nodes.Select(static node => node.Step).ToArray();

    public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => graph;
}

internal sealed class ProcessingRuntimeObservation : ILogger, IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _metrics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _tagNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, byte> _eventIds = new();
    private readonly MeterListener _meterListener;
    private readonly ActivityListener _activityListener;

    public ProcessingRuntimeObservation()
    {
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, CaptureProcessingTelemetry.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => RecordMetric(instrument, tags));
        _meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => RecordMetric(instrument, tags));
        _meterListener.Start();
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(
                source.Name, CaptureProcessingTelemetry.ActivitySourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.TryAdd(activity.OperationName, 0)
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => _eventIds.TryAdd(eventId.Id, 0);

    public void RecordObservableInstruments() => _meterListener.RecordObservableInstruments();

    public ProcessingRuntimeSnapshot Snapshot() => new(
        _metrics.Keys.Order(StringComparer.Ordinal).ToArray(),
        _activities.Keys.Order(StringComparer.Ordinal).ToArray(),
        _eventIds.Keys.Order().ToArray(),
        _tagNames.Keys.Order(StringComparer.Ordinal).ToArray());

    private void RecordMetric(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _metrics.TryAdd(instrument.Name, 0);
        foreach (var tag in tags)
        {
            _tagNames.TryAdd(tag.Key, 0);
        }
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }
}

internal sealed class ForwardingLogger<T>(ProcessingRuntimeObservation observation) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => observation.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => observation.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => observation.Log(logLevel, eventId, state, exception, formatter);
}

internal sealed record ProcessingRuntimeSnapshot(
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string> Activities,
    IReadOnlyList<int> EventIds,
    IReadOnlyList<string> TagNames);
