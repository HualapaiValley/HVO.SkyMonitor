using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ExposureMeasurement_RunnerTraceAdmits106While103WaitsForLinearAndMeasurementWithoutACatalog()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var measured = await RunTraceAsync(now, overlap: false).ConfigureAwait(false);
        var baseline = await RunTraceAsync(now, overlap: false, measurementEnabled: false).ConfigureAwait(false);
        CollectionAssert.AreEqual(measured, baseline, "The matched observation must use the same raw and step-three payloads.");
        await RunTraceAsync(now, overlap: true).ConfigureAwait(false);
    }

    private async Task<(string Raw, string Linear)[]> RunTraceAsync(DateTimeOffset now, bool overlap, bool measurementEnabled = true)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-exposure-measurement-trace");
        try
        {
            const int frameCount = 6;
            const int size = 224;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var token = timeout.Token;
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var location = DeploymentLocationSnapshot.Create("measurement-trace", 1, "virtual", null,
                DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix");
            var camera = CreateConfig(CameraPixelFormat.Mono16, size, size);
            var rig = camera.Rig with
            {
                ControlPolicy = profile.Rig.ControlPolicy,
                Readout = new(new SensorCrop(0, 0, size, size), 1, 1, FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,
                    FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample, 64, 4095)
            };
            var file = camera with
            {
                AgentId = profile.AgentId,
                Schedule = profile.Schedule,
                Rig = rig,
                DeploymentLocation = location,
                CenteredExposure = new("measurement-trace", CenteredExposureProfile.CurrentExecutionVersion),
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1920,
                    fixedSequenceStartUtc = now,
                    illuminationMode = "ControlledNight"
                })),
                Pipeline = profile.Pipeline with
                {
                    Steps =
                    [
                        new("Calibration", "linear", DependsOn: ["$raw"], Required: false,
                            Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
                        new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false,
                            Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
                        // The overlap arm also exercises the optional consumer with no authorized catalog.
                        .. (overlap ? new CaptureProcessingStepConfig[] { new("MeasuredStellarAssociations", "association",
                            DependsOn: ["$raw"], Required: false) } : []),
                        .. profile.Pipeline.Steps
                    ]
                }
            };

            var fault = overlap ? new MeasurementTracePause() : null;
            // Simulator truth stays private to the module. The processing graph has no independently authorized catalog.
            var noCatalog = new InMemoryCelestialCatalog([]);
            using var provider = MeasurementProvider(root, location, noCatalog, fault,
                configureServices: measurementEnabled ? null : services => services.Remove(services.Single(service =>
                    service.ServiceType == typeof(ICenteredExposureGraphExtension) &&
                    service.ImplementationType == typeof(ExposureMeasurementGraphPlan))));
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, file).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var products = provider.GetRequiredService<IExposureProductReader>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var graphOps = provider.GetRequiredService<IProcessingGraphOperations>();
            var lanes = provider.GetRequiredService<ICaptureLaneStore>();
            await lanes.InitializeLanesAsync(token).ConfigureAwait(false);
            var definition = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
            var handler = provider.GetRequiredService<StandardCaptureLaneHandler>();
            var renderCatalog = new InMemoryCelestialCatalog([
                new CelestialCatalogObject("trace-star", "Trace Star", AstronomyTime.LocalMeanSiderealDegrees(now, -113.878) / 15d,
                    35.347, 0, 0.65)]);
            var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(now, autoAdvance: true), renderCatalog,
                new ProjectedSceneStore(), stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cameraLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var notifications = new MeasurementTraceNotifications(frameCount, stop, fault);
            var host = new CaptureHostContext(active, ingress, notifications,
                projectedSceneStager: provider.GetRequiredService<CaptureProjectedSceneStager>());
            var timing = new ExposureTestTimeProvider(now, autoAdvance: true);
            var cameraKey = new CameraKey(active.AgentId!, active.CenteredExposure!.CameraId);
            var commitMs = new List<double>(frameCount);
            using var metrics = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == RawIngressTelemetry.MeterName && instrument.Name == "camera_agent.ingress.commit.duration")
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            metrics.SetMeasurementEventCallback<double>((_, seconds, _, _) => commitMs.Add(seconds * 1000));
            metrics.Start();
            using var process = Process.GetCurrentProcess();
            var startIo = ReadTraceIo();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var runner = Task.Run(() => new CameraModuleRunner(module, host, timing, NullLogger.Instance).RunAsync(stop.Token), token);
            var processingMs = new List<double>(frameCount);
            var captures = new List<(RawCaptureReceipt Receipt, ExposureKey Exposure, ExposureProductSlot Slot)>(frameCount);
            long maxBacklog = 0, maxBacklogBytes = 0;
            long maxObservedRss = 0;
            try
            {
                if (!overlap)
                    await runner.WaitAsync(token).ConfigureAwait(false);
                for (var ordinal = 1; ordinal <= frameCount; ordinal++)
                {
                    await notifications.Committed[ordinal - 1].Task.WaitAsync(token).ConfigureAwait(false);
                    var queued = (await lanes.ReadBacklogsAsync(token).ConfigureAwait(false)).Single(item => item.Lane == "standard");
                    maxBacklog = Math.Max(maxBacklog, queued.ActiveWork?.Count ?? 0);
                    maxBacklogBytes = Math.Max(maxBacklogBytes, queued.ActiveWork?.Bytes ?? 0);
                    var lease = await lanes.ClaimAsync(definition, "measurement-trace", active, token).ConfigureAwait(false);
                    Assert.IsNotNull(lease);
                    var receipt = lease.Context.RawCapture;
                    Assert.AreEqual((long)ordinal, receipt.Manifest.CenteredExposure!.Source.Ordinal);
                    Assert.AreEqual(ordinal + 1L, receipt.Manifest.CenteredExposure.Target.Ordinal);
                    var exposure = CaptureExposureMeasurementReader.Key(receipt.Manifest);
                    var runId = lease.Context.Execution!.ExecutionId;
                    var slot = new ExposureProductSlot(runId, ExposureMeasurementProducts.StepAlias,
                        ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
                    var clock = Stopwatch.StartNew();
                    var work = Task.Run(async () => await handler.HandleAsync(lease.Context, token).ConfigureAwait(false), token);
                    if (ordinal == 3 && fault is not null)
                    {
                        await fault.LinearBlocked.Task.WaitAsync(token).ConfigureAwait(false);
                        Assert.AreEqual(LinearExposureDisposition.Pending, (await products.ReadLinearAsync(exposure, token).ConfigureAwait(false))?.Disposition);
                        Assert.IsNull(await products.ReadProductAsync(exposure, slot, token).ConfigureAwait(false));
                        fault.ResumeLinear.TrySetResult();
                        await fault.MeasurementBlocked.Task.WaitAsync(token).ConfigureAwait(false);
                        fault.AllowNextCapture.TrySetResult();
                        await runner.WaitAsync(token).ConfigureAwait(false);
                        var head = await ingress.ReadCurrentAsync(cameraKey, token).ConfigureAwait(false);
                        Assert.AreEqual(6L, head.AcquisitionHead?.Ordinal);
                        Assert.AreEqual(exposure.RunId, head.AcquisitionHead?.Exposure.RunId);
                        Assert.AreNotEqual(exposure.CaptureId, head.AcquisitionHead?.Exposure.CaptureId);
                        Assert.IsNull(await products.ReadProductAsync(exposure, slot, token).ConfigureAwait(false));
                        var blocked = (await lanes.ReadBacklogsAsync(token).ConfigureAwait(false)).Single(item => item.Lane == "standard");
                        Assert.AreEqual(4L, blocked.ActiveWork?.Count);
                        maxBacklog = Math.Max(maxBacklog, blocked.ActiveWork?.Count ?? 0);
                        maxBacklogBytes = Math.Max(maxBacklogBytes, blocked.ActiveWork?.Bytes ?? 0);
                        fault.ResumeMeasurement.TrySetResult();
                    }
                    var outcome = await work.ConfigureAwait(false);
                    Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome, outcome.Reason);
                    await lanes.CompleteAsync(lease, token).ConfigureAwait(false);
                    processingMs.Add(clock.Elapsed.TotalMilliseconds);
                    process.Refresh();
                    maxObservedRss = Math.Max(maxObservedRss, process.WorkingSet64);
                    captures.Add((receipt, exposure, slot));
                }
                await runner.WaitAsync(token).ConfigureAwait(false);
                var drained = (await lanes.ReadBacklogsAsync(token).ConfigureAwait(false)).Single(item => item.Lane == "standard");
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                var processIo = ReadTraceIo().ToDictionary(pair => pair.Key, pair => pair.Value - startIo[pair.Key], StringComparer.Ordinal);
                Assert.AreEqual(0L, drained.ActiveWork?.Count ?? 0);
                Assert.HasCount(frameCount, commitMs);

                long rawBytes = 0, linearBytes = 0, measurementBytes = 0, sidecarBytes = 0;
                var durableCaptures = new List<object>(frameCount);
                var inputChecksums = new List<(string Raw, string Linear)>(frameCount);
                foreach (var (receipt, exposure, slot) in captures)
                {
                    rawBytes += receipt.Manifest.Descriptor.Layout.ByteLength;
                    var linear = await products.ReadLinearAsync(exposure, token).ConfigureAwait(false);
                    Assert.IsNotNull(linear?.Product);
                    var rawChecksum = receipt.Manifest.Descriptor.Artifact.ChecksumSha256;
                    var linearChecksum = linear.Product.PayloadSha256;
                    inputChecksums.Add((rawChecksum, linearChecksum));
                    var result = await products.ReadProductAsync(exposure, slot, token).ConfigureAwait(false);
                    var detail = await graphOps.ReadExecutionDetailAsync(slot.ExecutionId, token).ConfigureAwait(false);
                    Assert.IsNotNull(detail);
                    Assert.AreEqual(receipt.Manifest.CaptureGraphRevisionId, detail.Execution.GraphRevisionId);
                    foreach (var independent in detail.Nodes.Where(node => node.NodeId is "linear" or "scene" or "preview" or "storage"))
                        Assert.AreEqual("Completed", independent.Status, independent.Reason);
                    if (overlap)
                        Assert.AreEqual("Skipped", detail.Nodes.Single(item => item.NodeId == "association").Status);
                    var read = await provider.GetRequiredService<IExposureMeasurementRunReader>().ReadAsync(detail, token).ConfigureAwait(false);
                    if (measurementEnabled)
                    {
                        Assert.IsNotNull(result?.Product);
                        Assert.IsNotNull(read?.Measurement, read?.UnavailableReason);
                        Assert.AreEqual(exposure, read.Measurement.Input.Exposure);
                        Assert.AreEqual(ExposureMeasurementStatus.Usable, read.Measurement.Sources.Status);
                        Assert.IsGreaterThan(0, read.Measurement.Hfd.ValidCount ?? 0);
                    }
                    else
                    {
                        Assert.IsNull(result);
                        Assert.IsNull(read);
                    }
                    foreach (var product in new[] { linear.Product, result?.Product }.OfType<ExposureProductRef>())
                    {
                        var output = await store.ReadOutputByArtifactIdAsync(product.ArtifactId, token).ConfigureAwait(false);
                        Assert.IsNotNull(output);
                        var payload = (await SiloInfrastructureFixture.ReadArtifactBytesAsync(provider, output.ArtifactId, token)
                            .ConfigureAwait(false)).LongLength;
                        sidecarBytes += output.EvidenceJson.LongLength;
                        if (product == linear.Product) linearBytes += payload;
                        else measurementBytes += payload;
                    }
                    durableCaptures.Add(new
                    {
                        slot = 100 + receipt.Manifest.CenteredExposure!.Source.Ordinal,
                        exposure.CaptureId,
                        exposure.RunId,
                        detail.Execution.GraphRevisionId,
                        executionId = slot.ExecutionId,
                        rawChecksum,
                        linearChecksum,
                        measurementArtifactId = result?.Product?.ArtifactId,
                        measurementIdentity = result?.Product?.OutputIdentitySha256,
                        hfdContributors = read?.Measurement?.Hfd.ValidCount
                    });
                }
                TestContext.WriteLine(JsonSerializer.Serialize(new
                {
                    arm = overlap ? "overlap-step4-paused" : measurementEnabled ? "acquisition-first" : "acquisition-first-measurement-off",
                    format = "Mono16",
                    size,
                    frameCount,
                    measurementEnabled,
                    commitMs,
                    commitSinceRunnerStartMs = notifications.CommitMilliseconds,
                    laneMs = processingMs,
                    cpuMs,
                    allocatedBytes,
                    processIo,
                    maxObservedRss,
                    maxBacklog,
                    maxBacklogBytes,
                    rawBytes,
                    linearBytes,
                    measurementBytes,
                    sidecarBytes,
                    pendingAfterDrain = drained.ActiveWork?.Count ?? 0,
                    durableCaptures
                }));
                return inputChecksums.ToArray();
            }
            finally
            {
                fault?.ResumeLinear.TrySetResult();
                fault?.ResumeMeasurement.TrySetResult();
                fault?.AllowNextCapture.TrySetResult();
                await stop.CancelAsync().ConfigureAwait(false);
                await runner.ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, long> ReadTraceIo() => OperatingSystem.IsLinux()
        ? File.ReadLines("/proc/self/io").Select(static line => line.Split(':', 2))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture), StringComparer.Ordinal)
        : new(StringComparer.Ordinal);

    private sealed class MeasurementTraceNotifications(int count, CancellationTokenSource stop, MeasurementTracePause? pause) : ICaptureDistributor
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _committed;
        internal TaskCompletionSource[] Committed { get; } = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        internal double[] CommitMilliseconds { get; } = new double[count];

        public void NotifyCommittedCapture()
        {
            var index = _committed++;
            CommitMilliseconds[index] = _clock.Elapsed.TotalMilliseconds;
            Committed[index].TrySetResult();
            if (index == 2 && pause is not null)
                pause.AllowNextCapture.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if (_committed == count)
                stop.Cancel();
        }

        public ValueTask ProcessEphemeralAsync(CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The centered trace must always use durable admission.");
    }

    private sealed class MeasurementTracePause : ICaptureProcessingFaultInjector
    {
        private int _linearAttempts;
        private int _measurementAttempts;
        internal TaskCompletionSource LinearBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeLinear { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MeasurementBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeMeasurement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowNextCapture { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.BeforeNodeExecution) return;
            if (nodeId == "linear" && ++_linearAttempts == 3)
            {
                LinearBlocked.TrySetResult();
                ResumeLinear.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            if (nodeId == ExposureMeasurementProducts.StepAlias && ++_measurementAttempts == 3)
            {
                MeasurementBlocked.TrySetResult();
                ResumeMeasurement.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
        }
    }
}
