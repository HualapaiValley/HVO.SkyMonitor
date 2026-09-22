using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;

namespace HVO.SkyMonitor.CameraAgent.Tests.Operations;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOperationsSummaryTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void LatestReturnsNullForEmptyOrUnavailableSources()
    {
        Assert.IsNull(CameraAgentOperationsSummaryProvider.Latest([]));
        Assert.IsNull(CameraAgentOperationsSummaryProvider.Latest([null, null]));
    }

    [TestMethod]
    public async Task GetAsyncSnapshotsOperationalStatesWithSourcesWithoutSensitiveContent()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = TimeProvider.System;
        var root = Path.Combine(Path.GetTempPath(), "private-operations-root");
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = true }
        });
        var rawIngress = new RawIngressState(timeProvider);
        rawIngress.Set(RawIngressAvailability.Degraded, "secret-raw-reason", 7, 700, 2, 200, now.AddMinutes(-1));
        var lanes = new CaptureLaneState(timeProvider, options);
        lanes.Update([
            new CaptureLaneBacklog("standard", true, 3, 300, now.AddMinutes(-2), 1, 2, 0,
                PendingCaptures: [new("agent-operations", 1), new("agent-operations", 2)]),
            new CaptureLaneBacklog("transient", false, 4, 400, now.AddMinutes(-1), 0, 0, 2, 1,
                PendingCaptures: [new("agent-operations", 3), new("agent-operations", 4)])
        ]);
        var processing = new CaptureProcessingState();
        processing.SetDurable(5, 2, 1, now.AddMinutes(-3));
        var artifactOutbox = new ArtifactOutboxState();
        artifactOutbox.ReportUnavailable(root, "secret-outbox-exception");
        var storage = new StoragePressureState();
        storage.Set(new StoragePressureSnapshot(
            root,
            new StorageCapacity(10_000, 2_000),
            true,
            2,
            now,
            "secret-probe-path"));
        var runtime = new FleetRuntimeState(timeProvider);
        runtime.ModuleAvailable();
        runtime.CaptureFailed("secret-module-exception");
        var heartbeat = new FleetHeartbeatState();
        heartbeat.Update(
            new FleetStatusOutboxSnapshot(6, 600, 1, 2, 3, 4, 5, now.AddMinutes(-4), now),
            FleetAvailability.Degraded,
            "https://private-heartbeat/secret");
        var environmental = new EnvironmentalObservationDeliveryState();
        environmental.Update(
            new EnvironmentalObservationOutboxSnapshot(20, 2_000, 8, 800, 1, 2, 3, 4, 5, now.AddMinutes(-5), now),
            EnvironmentalObservationDeliveryAvailability.Degraded,
            "secret-environmental-reason");
        var transient = new TransientWorkerState(timeProvider);
        transient.Set(TransientWorkerAvailability.Degraded, "secret-transient-reason", 9, 10);
        var telemetryProvider = new CaptureTelemetrySink();
        telemetryProvider.Report(new CaptureTelemetrySample(
            now,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
            12,
            null,
            CaptureMode.Still,
            false,
            true,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(1200),
            []));
        var configuration = new CameraAgentConfigurationAccessor();
        using var moduleOptions = JsonDocument.Parse("""{"apiKey":"module-secret","endpoint":"https://private-module"}""");
        configuration.SetConfiguration(CreateConfiguration(moduleOptions.RootElement.Clone()));
        using var captureTelemetry = new CaptureControlTelemetry();
        using var coordinator = new CaptureAdmissionCoordinator(
            new NullIngress(), options, timeProvider, captureTelemetry);
        var laneSnapshotRefresher = new RecordingLaneSnapshotRefresher();
        var provider = new CameraAgentOperationsSummaryProvider(
            timeProvider,
            coordinator,
            rawIngress,
            laneSnapshotRefresher,
            lanes,
            processing,
            artifactOutbox,
            storage,
            runtime,
            heartbeat,
            environmental,
            new ExecutionEvidenceExportState(),
            transient,
            telemetryProvider,
            configuration,
            new CameraAgentStorageResolver(configuration, options),
            options);

        var summary = await provider.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, laneSnapshotRefresher.Calls);
        Assert.AreEqual("Degraded", summary.RawIngress.Value.Availability);
        Assert.AreEqual(7L, summary.RawIngress.Value.PendingCount);
        Assert.HasCount(2, summary.CaptureLanes.Value.Lanes);
        Assert.AreEqual(2L, summary.CaptureLanes.Value.RetryCount);
        Assert.AreEqual(2L, summary.CaptureLanes.Value.Lanes[0].RetryCount);
        CollectionAssert.AreEqual(
            new long[] { 3, 4 },
            summary.CaptureLanes.Value.Lanes[1].PendingCaptures.Select(static capture => capture.CaptureSequence).ToArray());
        Assert.AreEqual(1L, summary.CaptureProcessing.Value.TerminalCount);
        Assert.AreEqual("Unavailable", summary.ArtifactOutbox.Value.Availability);
        Assert.AreEqual("raw-ingress", summary.Storage.Value[0].Alias);
        Assert.IsFalse(summary.Storage.Value[0].ProbeSucceeded);
        Assert.AreEqual(6L, summary.Heartbeat.Value.PendingCount);
        Assert.AreEqual(8L, summary.EnvironmentalDelivery.Value.PendingCount);
        Assert.AreEqual(9L, summary.TransientWorker.Value.PendingFrames);
        Assert.AreEqual(32, summary.TransientWorker.Value.MaximumCandidates);
        Assert.AreEqual(1, summary.CaptureTelemetry.Value.SampleCount);
        Assert.IsNotNull(summary.CaptureProcessing.ObservedUtc);
        Assert.IsNotNull(summary.ArtifactOutbox.ObservedUtc);
        Assert.AreEqual("agent-operations", summary.Configuration.Value.AgentId);
        Assert.AreEqual("VirtualSky", summary.Configuration.Value.ModuleType);
        Assert.AreEqual("validated", summary.Configuration.Value.ValidationStatus);
        Assert.AreEqual("Enabled", summary.Configuration.Value.CentralIntegration);
        Assert.AreEqual("Off", summary.Configuration.Value.TransientDetection);
        Assert.IsFalse(string.IsNullOrWhiteSpace(summary.RawIngress.Source));
        Assert.IsFalse(string.IsNullOrWhiteSpace(summary.RawIngress.Freshness));

        var json = JsonSerializer.Serialize(summary, SerializerOptions);
        Assert.IsFalse(json.Contains(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("secret-", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("leaseToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("configurationSha256", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("pixelData", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A standalone install has central integration and transient detection switched off. Those
    /// sections have no observation and never will, so they must read "disabled" rather than
    /// "unknown"/"stale", the configuration snapshot is a startup fact ("static"), and an
    /// observed section still derives fresh/stale from its observation time. #984.
    /// </summary>
    [TestMethod]
    public async Task GetAsyncMarksSwitchedOffSubsystemsDisabledRatherThanUnknown()
    {
        var timeProvider = TimeProvider.System;
        var root = Path.Combine(Path.GetTempPath(), "private-operations-root-disabled");
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            TransientDetection = new TransientDetectionOptions { Mode = TransientOperatingMode.Off }
        });
        var rawIngress = new RawIngressState(timeProvider);
        rawIngress.Set(RawIngressAvailability.Accepting, "accepting", 0, 0, 0, 0, null);
        var configuration = new CameraAgentConfigurationAccessor();
        using var moduleOptions = JsonDocument.Parse("""{}""");
        configuration.SetConfiguration(CreateConfiguration(moduleOptions.RootElement.Clone()));
        using var captureTelemetry = new CaptureControlTelemetry();
        using var coordinator = new CaptureAdmissionCoordinator(
            new NullIngress(), options, timeProvider, captureTelemetry);
        var provider = new CameraAgentOperationsSummaryProvider(
            timeProvider,
            coordinator,
            rawIngress,
            new RecordingLaneSnapshotRefresher(),
            new CaptureLaneState(timeProvider, options),
            new CaptureProcessingState(),
            new ArtifactOutboxState(),
            new StoragePressureState(),
            new FleetRuntimeState(timeProvider),
            new FleetHeartbeatState(),
            new EnvironmentalObservationDeliveryState(),
            new ExecutionEvidenceExportState(),
            new TransientWorkerState(timeProvider),
            new CaptureTelemetrySink(),
            configuration,
            new CameraAgentStorageResolver(configuration, options),
            options);

        var summary = await provider.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperationsFreshness.Disabled, summary.ArtifactOutbox.Freshness);
        Assert.AreEqual(OperationsFreshness.Disabled, summary.Heartbeat.Freshness);
        Assert.AreEqual(OperationsFreshness.Disabled, summary.EnvironmentalDelivery.Freshness);
        Assert.AreEqual(OperationsFreshness.Disabled, summary.ExecutionEvidenceExport.Freshness);
        Assert.AreEqual(OperationsFreshness.Disabled, summary.TransientWorker.Freshness);
        Assert.AreEqual(OperationsFreshness.Static, summary.Configuration.Freshness);
        Assert.AreEqual("Disabled", summary.ArtifactOutbox.Value.Availability);
        Assert.IsNull(summary.ArtifactOutbox.ObservedUtc);
        // An observed section is unaffected by the switch.
        Assert.AreEqual(OperationsFreshness.Fresh, summary.RawIngress.Freshness);
    }

    private static CameraModuleConfig CreateConfiguration(JsonElement moduleOptions)
        => new(
            new ObservatoryLocation(20, -155, 1000, "Pacific/Honolulu"),
            new CameraModuleDescriptor("VirtualSky", moduleOptions),
            new CameraRigConfig(
                new SensorProfile("secret-sensor", 10, 10, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 2, 180, 2),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            CapturePipelineConfig.Empty,
            AgentId: "agent-operations");

    private sealed class NullIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class RecordingLaneSnapshotRefresher : IOperationsQueueSnapshotRefresher
    {
        public int Calls { get; private set; }

        public ValueTask RefreshOperationsQueueSnapshotsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.CompletedTask;
        }
    }
}
