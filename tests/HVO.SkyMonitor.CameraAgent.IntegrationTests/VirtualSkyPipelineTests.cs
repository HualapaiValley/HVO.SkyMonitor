using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class VirtualSkyPipelineTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string[] ExpectedProcessingSteps =
        ["CloudObservation", "Calibration", "RollingCombination", "CalibratedPreview", "Preview", "Annotation", "LocalStorage"];
    private static readonly FrameArtifactRole[] ExpectedArtifactRoles =
        [FrameArtifactRole.Raw, FrameArtifactRole.Calibrated, FrameArtifactRole.Combined, FrameArtifactRole.Preview, FrameArtifactRole.AnnotatedPreview];
    private static readonly string[] ExpectedPreviewVariants = ["calibrated-display", "default"];
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;

    [TestMethod]
    public async Task CentralTransportOutageDoesNotBlockAcquisitionOrLoseLocalTransientProvenance()
    {
        using var scope = Fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var telemetry = services.GetRequiredService<ICaptureTelemetryProvider>();
        var latest = services.GetRequiredService<ILatestFrameAccessor>();
        await WaitUntilAsync(
            () => telemetry.Latest is { FrameStored: true }
                && latest.TryGetSnapshot(FrameArtifactRole.Raw, out _),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Raw, out var before));
        var beforeTimestamp = before.TimestampUtc;
        var beforeStored = services.GetRequiredService<IFrameStorageService>().List(
            Fixture.StorageRoot, DateOnly.FromDateTime(before.TimestampUtc.UtcDateTime), null, 10_000).Count;

        var configured = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        var outageOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = configured.RawIngressRoot,
            CaptureDistribution = configured.CaptureDistribution,
            UploadBatchSize = 1,
            UploadPollIntervalSeconds = 1,
            UploadRetryInitialDelaySeconds = 1,
            UploadRetryMaximumDelaySeconds = 1
        });
        using var outageFactory = new OutageHttpClientFactory();
        var outageClient = new ArtifactUploadClient(outageFactory, outageOptions, TimeProvider.System);
        var drain = ActivatorUtilities.CreateInstance<ArtifactOutboxDrainService>(
            services, outageClient, outageOptions);
        await drain.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WaitUntilAsync(
                HasTransportFailureRetry,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await WaitUntilAsync(
                () => latest.TryGetSnapshot(FrameArtifactRole.Raw, out var current)
                    && current.TimestampUtc > beforeTimestamp,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        finally
        {
            await drain.StopAsync(CancellationToken.None).ConfigureAwait(false);
            drain.Dispose();
            ResetTransportFailureRetries();
        }

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Raw, out var after));
        Assert.IsGreaterThan(beforeTimestamp, after.TimestampUtc);
        Assert.IsNotNull(after.Metadata?.Scene?.TransientScenario);
        var transient = after.Metadata.Scene.TransientScenario;
        var stored = services.GetRequiredService<IFrameStorageService>().List(
            Fixture.StorageRoot, DateOnly.FromDateTime(after.TimestampUtc.UtcDateTime), null, 10_000);
        Assert.IsGreaterThan(beforeStored, stored.Count);
        var currentFrameManifests = stored.Select(item => new
        {
            Stored = item,
            Parsed = CaptureContractJson.ParseManifest(
                File.ReadAllBytes(Path.ChangeExtension(item.AbsolutePath, ".json")))
        })
            .Where(item => item.Parsed.IsValid
                && item.Stored.TimestampUtc > beforeTimestamp)
            .ToArray();
        Assert.IsNotEmpty(currentFrameManifests);
        Assert.IsTrue(currentFrameManifests.All(item => File.Exists(item.Stored.AbsolutePath)));
        Assert.IsTrue(currentFrameManifests.Any(item =>
            item.Parsed.Document!.Manifest!.Descriptor.Artifact.Role == FrameArtifactRole.Raw));
        Assert.IsTrue(currentFrameManifests.Any(item =>
            item.Parsed.Document!.Manifest!.Descriptor.Artifact.Role == FrameArtifactRole.Preview));
        Assert.IsTrue(currentFrameManifests.All(item =>
            item.Parsed.Document!.Manifest!.Scene?.TransientScenario?.ParametersSha256
                == transient.ParametersSha256));
        Assert.IsTrue(HasPendingOrRetryOutboxRecord());
        Assert.AreEqual(RawIngressAvailability.Accepting,
            services.GetRequiredService<RawIngressState>().Snapshot.Availability);
        Assert.AreEqual(CaptureProcessingAvailability.Healthy,
            services.GetRequiredService<CaptureProcessingState>().Snapshot.Availability);
    }

    [TestMethod]
    public async Task ConfiguredPipelinePublishesPersistsReportsAndQueuesVirtualFrame()
    {
        using var scope = Fixture.CreateCameraAgentScope();
        using var hostTelemetryScope = Fixture.CreateHostScope();
        var services = scope.ServiceProvider;
        var telemetry = services.GetRequiredService<ICaptureTelemetryProvider>();
        var latest = services.GetRequiredService<ILatestFrameAccessor>();
        var initialStartedUtc = telemetry.Latest?.StartedUtc ?? DateTimeOffset.MinValue;
        using var logger = new RecordingLoggerProvider();
        services.GetRequiredService<ILoggerFactory>().AddProvider(logger);
        hostTelemetryScope.ServiceProvider.GetRequiredService<ILoggerFactory>().AddProvider(logger);
        var instrumentNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var metricMeasurements = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
        var metricTagValues = new ConcurrentBag<string>();
        using var meterListener = CreateMeterListener(instrumentNames, metricMeasurements, metricTagValues);
        var activityNames = new ConcurrentBag<string>();
        var activityTagValues = new ConcurrentBag<string>();
        using var activityListener = CreateActivityListener(activityNames, activityTagValues);

        await WaitUntilAsync(() => telemetry.Latest is { FrameStored: true } sample &&
            sample.StartedUtc > initialStartedUtc &&
            latest.TryGetSnapshot(FrameArtifactRole.Raw, out _) &&
            latest.TryGetSnapshot(FrameArtifactRole.Combined, out _) &&
            latest.TryGetSnapshot(out _), TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        var sample = telemetry.Latest!;
        Assert.IsTrue(sample.FrameStored);
        CollectionAssert.AreEqual(
            ExpectedProcessingSteps,
            sample.ProcessingSteps.Select(step => step.Name).ToArray());
        Assert.IsTrue(
            sample.ProcessingSteps.All(step => step.Succeeded),
            string.Join("; ", sample.ProcessingSteps
                .Where(step => !step.Succeeded)
                .Select(step => $"{step.Name}: {step.ErrorMessage}")));

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Raw, out var raw));
        Assert.AreEqual(CameraPixelFormat.Mono16, raw.PixelFormat);
        Assert.AreEqual("VirtualSky", raw.Metadata!.SourceId);
        Assert.IsNotNull(raw.Metadata.Scene);
        Assert.IsNotNull(raw.Metadata.Scene.CloudScenario);
        Assert.IsNotNull(raw.Metadata.Scene.TransientScenario);
        var cloudProvenance = raw.Metadata.Scene.CloudScenario;
        var transientProvenance = raw.Metadata.Scene.TransientScenario;
        var cloudDefinition = cloudProvenance.Parameters.Deserialize<VirtualCloudScenarioDefinition>(
            SerializerOptions)!;
        Assert.AreEqual(cloudDefinition.ComputeCanonicalScenarioId(), cloudProvenance.ScenarioId);
        var expectedCloudCover = new VirtualCloudField(cloudDefinition).ComputeSkyCoverage(
            cloudProvenance.IntegrationStartUtc,
            cloudProvenance.IntegrationEndUtc - cloudProvenance.IntegrationStartUtc);
        var transientDefinition = transientProvenance.Parameters.Deserialize<VirtualTransientScenarioDefinition>(
            SerializerOptions)!;
        Assert.AreEqual(transientDefinition.ComputeCanonicalScenarioId(), transientProvenance.ScenarioId);
        Assert.AreEqual(transientDefinition.ComputeParametersSha256(), transientProvenance.ParametersSha256);
        Assert.IsTrue(
            (raw.TimestampUtc - transientProvenance.IntegrationStartUtc).Duration() <= TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(
            transientProvenance.IntegrationStartUtc + raw.Metadata.Exposure,
            transientProvenance.IntegrationEndUtc);
        Assert.AreEqual(1, transientProvenance.SkyPrimitiveCount);
        Assert.AreEqual(1, transientProvenance.SensorPrimitiveCount);
        Assert.AreEqual(4095, ReadMono16(raw.PixelData.Span, raw.Width * 2, 1, 1));

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Combined, out var combined));
        Assert.AreEqual(CameraPixelFormat.Mono16, combined.PixelFormat);
        Assert.IsTrue(latest.TryGetSnapshot(out var preview));
        Assert.AreEqual(CameraPixelFormat.Mono8, preview.PixelFormat);
        Assert.AreEqual("AnnotatedPreview", preview.Metadata!.SourceId);
        Assert.AreEqual("integration-annotation-v2", preview.RecipeVersion);
        Assert.AreEqual("integration-annotation-v2", preview.Metadata.Extra!["annotationRecipeVersion"]);
        Assert.AreEqual(transientProvenance.ScenarioId, preview.Metadata.Scene!.TransientScenario!.ScenarioId);

        var stored = services.GetRequiredService<IFrameStorageService>().List(
            Fixture.StorageRoot, DateOnly.FromDateTime(raw.TimestampUtc.UtcDateTime), null, 1000);
        CollectionAssert.IsSubsetOf(
            ExpectedArtifactRoles, stored.Select(item => item.Role).Distinct().ToArray());
        Assert.IsTrue(stored.All(item => File.Exists(item.AbsolutePath)));
        var previewVariants = stored
            .Where(static item => item.Role == FrameArtifactRole.Preview)
            .Select(item => CaptureContractJson.ParseManifest(File.ReadAllBytes(Path.ChangeExtension(item.AbsolutePath, ".json"))))
            .Where(static parsed => parsed.IsValid)
            .Select(static parsed => parsed.Document!.Manifest!.Descriptor.Artifact.Variant)
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.IsSubsetOf(ExpectedPreviewVariants, previewVariants.ToArray());
        var manifests = stored
            .Select(item => (Stored: item, Parsed: CaptureContractJson.ParseManifest(
                File.ReadAllBytes(Path.ChangeExtension(item.AbsolutePath, ".json")))))
            .Where(static item => item.Parsed.IsValid && item.Parsed.Document!.Manifest is not null)
            .ToArray();
        var defaultPreview = manifests
            .Where(item =>
                item.Parsed.Document!.Manifest!.Descriptor.Artifact.Role == FrameArtifactRole.Preview &&
                item.Parsed.Document.Manifest.Descriptor.Artifact.Variant == "default")
            .OrderByDescending(static item => item.Stored.TimestampUtc)
            .First();
        Assert.AreEqual(transientProvenance.ScenarioId,
            defaultPreview.Parsed.Document!.Manifest!.Scene!.TransientScenario!.ScenarioId);
        var previewPayload = await File.ReadAllBytesAsync(defaultPreview.Stored.AbsolutePath).ConfigureAwait(false);
        var previewLayout = defaultPreview.Parsed.Document.Manifest.Descriptor.Layout;
        Assert.AreEqual(CameraPixelFormat.Mono8, previewLayout.PixelFormat);
        Assert.AreEqual(byte.MaxValue, previewPayload[previewLayout.StrideBytes + 1]);
        AssertLineageReachesRaw(defaultPreview.Parsed.Document.Manifest.Descriptor, manifests);

        var pending = services.GetRequiredService<IArtifactOutbox>().List(Fixture.StorageRoot, 1000);
        Assert.IsNotEmpty(pending);
        Assert.IsTrue(pending.All(item => item.Role == FrameArtifactRole.Raw));
        Assert.IsTrue(pending.All(item => item.AgentId == "cameraagent-integration-test"));
        Assert.IsTrue(pending.All(item => File.Exists(Path.Combine(Fixture.StorageRoot, item.RelativeArtifactPath))));
        Assert.IsTrue(pending.Any(item => item.Scene is not null));
        Assert.IsTrue(pending.Where(item => item.Role == FrameArtifactRole.Raw).All(item => item.FrameId != item.ArtifactId));
        using (var outbox = new SqliteConnection(
            $"Data Source={Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db")}"))
        {
            await outbox.OpenAsync().ConfigureAwait(false);
            using var outboxCommand = outbox.CreateCommand();
            outboxCommand.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE manifest_kind = 'v2' AND status = 'pending';";
            Assert.IsGreaterThanOrEqualTo(pending.Count, Convert.ToInt32(
                await outboxCommand.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
            outboxCommand.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE status = 'pending' AND manifest_kind != 'v2';";
            Assert.AreEqual(0, Convert.ToInt32(
                await outboxCommand.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
        }

        await WaitUntilAsync(HasNoUnfinishedLaneWork, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        using var journal = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        await journal.OpenAsync().ConfigureAwait(false);
        using var countCommand = journal.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE state = 'committed';";
        Assert.IsGreaterThan(0L, Convert.ToInt64(
            await countCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        countCommand.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';";
        Assert.AreEqual(0L, Convert.ToInt64(
            await countCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        countCommand.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;";
        Assert.AreEqual(0L, Convert.ToInt64(
            await countCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        var ingressState = services.GetRequiredService<RawIngressState>().Snapshot;
        Assert.AreEqual(RawIngressAvailability.Accepting, ingressState.Availability);
        Assert.AreEqual(0L, ingressState.PendingCount);
        Assert.AreEqual(0L, ingressState.PendingBytes);
        Assert.AreEqual(0L, ingressState.QuarantineCount);
        Assert.AreEqual(0L, ingressState.QuarantineBytes);

        using var processing = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        await processing.OpenAsync().ConfigureAwait(false);
        using var processingCommand = processing.CreateCommand();
        processingCommand.CommandText = "SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed';";
        Assert.AreEqual(0L, Convert.ToInt64(
            await processingCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        processingCommand.CommandText = "SELECT COUNT(DISTINCT output_identity_sha256) FROM processing_outputs;";
        Assert.IsGreaterThanOrEqualTo(5L, Convert.ToInt64(
            await processingCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        var processingState = services.GetRequiredService<CaptureProcessingState>().Snapshot;
        Assert.AreEqual(CaptureProcessingAvailability.Healthy, processingState.Availability);
        Assert.AreEqual(0L, processingState.PendingCount);
        Assert.AreEqual(0L, processingState.RetryCount);
        Assert.AreEqual(0L, processingState.TerminalCount);
        processingCommand.CommandText = "SELECT artifact_id FROM processing_outputs WHERE node_id = 'CalibratedPreview' ORDER BY capture_sequence DESC LIMIT 1;";
        var localOnlyPreviewId = Guid.ParseExact(
            Convert.ToString(await processingCommand.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)!,
            "N");
        Assert.IsFalse(pending.Any(item => item.ArtifactId == localOnlyPreviewId));

        var uploadCheckpoint = ReadPendingOutboxCheckpoint();
        var configuredUploadOptions = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        var drainOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = configuredUploadOptions.RawIngressRoot,
            CaptureDistribution = configuredUploadOptions.CaptureDistribution,
            UploadBatchSize = 1,
            UploadPollIntervalSeconds = configuredUploadOptions.UploadPollIntervalSeconds,
            UploadRetryInitialDelaySeconds = configuredUploadOptions.UploadRetryInitialDelaySeconds,
            UploadRetryMaximumDelaySeconds = configuredUploadOptions.UploadRetryMaximumDelaySeconds
        });
        var uploadUnfinishedAtDrainCheckpoint = -1;
        var drain = ActivatorUtilities.CreateInstance<ArtifactOutboxDrainService>(services, drainOptions);
        await drain.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await WaitUntilAsync(
                    () => HasMatchingAcknowledgement(uploadCheckpoint),
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                uploadUnfinishedAtDrainCheckpoint = CountUnfinishedOutboxRecords();
            }
            catch (AssertFailedException)
            {
                var backgroundFailure = drain.ExecuteTask?.Exception?.GetBaseException().ToString() ?? "none";
                Assert.Fail($"Two-host outbox drain did not acknowledge: {ReadOutboxState(uploadCheckpoint.IdempotencyKey)}; background failure: {backgroundFailure}");
            }
        }
        finally
        {
            await drain.StopAsync(CancellationToken.None).ConfigureAwait(false);
            drain.Dispose();
        }
        using (var hostScope = Fixture.CreateHostScope())
        {
            var centralDb = hostScope.ServiceProvider.GetRequiredService<HVO.SkyMonitor.LogicHost.Data.ApplicationDbContext>();
            var connection = centralDb.Database.GetDbConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM CentralArtifacts
                WHERE IdempotencyKey = @idempotencyKey
                  AND ArtifactId = @artifactId
                  AND ChecksumSha256 = @checksumSha256
                  AND ByteLength = @byteLength;
                """;
            AddParameter(command, "@idempotencyKey", uploadCheckpoint.IdempotencyKey);
            AddParameter(command, "@artifactId", uploadCheckpoint.ArtifactId);
            AddParameter(command, "@checksumSha256", uploadCheckpoint.ChecksumSha256);
            AddParameter(command, "@byteLength", uploadCheckpoint.ByteLength);
            Assert.AreEqual(1, Convert.ToInt32(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.Clear();
            command.CommandText = """
                SELECT COUNT(*)
                FROM EnvironmentalObservations AS observation
                INNER JOIN EnvironmentalObservationSources AS source
                    ON source.Id = observation.SourceRecordId
                WHERE observation.SourceKind = 'Simulated'
                  AND observation.Kind = 'CloudCover'
                  AND observation.AgentId IS NOT NULL
                  AND source.ParametersSha256 = @parametersSha256
                  AND observation.ObservedFromUtc = @observedFromUtc
                  AND observation.ObservedThroughUtc = @observedThroughUtc
                  AND ABS(observation.NumericValue - @numericValue) < 0.000000000001;
                """;
            AddParameter(command, "@parametersSha256", cloudProvenance.ParametersSha256);
            AddParameter(command, "@observedFromUtc", cloudProvenance.IntegrationStartUtc);
            AddParameter(command, "@observedThroughUtc", cloudProvenance.IntegrationEndUtc);
            AddParameter(command, "@numericValue", expectedCloudCover);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            var cloudObservationCount = 0;
            while (DateTimeOffset.UtcNow < deadline && cloudObservationCount == 0)
            {
                cloudObservationCount = Convert.ToInt32(
                    await command.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (cloudObservationCount == 0)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            Assert.AreEqual(1, cloudObservationCount);

            command.CommandText = """
                SELECT CAST(observation.ObservationId AS nvarchar(36))
                FROM EnvironmentalObservations AS observation
                INNER JOIN EnvironmentalObservationSources AS source
                    ON source.Id = observation.SourceRecordId
                WHERE observation.SourceKind = 'Simulated'
                  AND observation.Kind = 'CloudCover'
                  AND observation.AgentId IS NOT NULL
                  AND source.ParametersSha256 = @parametersSha256
                  AND observation.ObservedFromUtc = @observedFromUtc
                  AND observation.ObservedThroughUtc = @observedThroughUtc
                  AND ABS(observation.NumericValue - @numericValue) < 0.000000000001;
                """;
            var observationId = Guid.Parse(Convert.ToString(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture)!);
            await WaitUntilAsync(
                () => HasNoEnvironmentalOutboxRecord(observationId),
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }

        using var client = Fixture.CreateCameraAgentClient();
        using var response = await client.GetAsync(new Uri("/api/v1.0/frames/latest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        var healthJson = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
        meterListener.RecordObservableInstruments();
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        StringAssert.Contains(healthJson, "raw-ingress", StringComparison.Ordinal);
        StringAssert.Contains(healthJson, "capture-processing", StringComparison.Ordinal);
        StringAssert.Contains(healthJson, "environmental-delivery", StringComparison.Ordinal);
        Assert.IsTrue(instrumentNames.ContainsKey("skymonitor.environment.edge.enqueue"));
        Assert.IsTrue(instrumentNames.ContainsKey("skymonitor.environment.ingest"));
        Assert.IsTrue(metricMeasurements.GetValueOrDefault("skymonitor.environment.edge.enqueue") > 0);
        Assert.IsTrue(metricMeasurements.GetValueOrDefault("skymonitor.environment.edge.delivery") > 0);
        Assert.IsTrue(metricMeasurements.GetValueOrDefault("skymonitor.environment.ingest") > 0);
        string[] requiredInstruments =
        [
            "camera_agent.capture.count",
            "camera_agent.capture.frames.stored",
            "camera_agent.capture.processing_ms",
            "camera_agent.capture.loop_ms",
            "camera_agent.capture_control.cycles",
            "camera_agent.capture_control.cycle.duration",
            "camera_agent.capture_control.segment.duration",
            "camera_agent.ingress.committed",
            "camera_agent.ingress.committed.bytes",
            "camera_agent.ingress.commit.duration",
            "camera_agent.ingress.pending",
            "camera_agent.ingress.pending.bytes",
            "camera_agent.ingress.quarantine.records",
            "camera_agent.ingress.quarantine.bytes",
            "camera_agent.processing.graphs",
            "camera_agent.processing.nodes",
            "camera_agent.processing.outputs",
            "camera_agent.processing.output.bytes",
            "camera_agent.processing.graph.duration",
            "camera_agent.processing.pending",
            "camera_agent.processing.retry",
            "camera_agent.processing.oldest.age"
        ];
        CollectionAssert.IsSubsetOf(requiredInstruments, instrumentNames.Keys.ToArray());
        string[] requiredActivities =
        [
            "capture-cycle",
            "capture-control",
            "capture-ingress-handoff",
            "raw-ingress.accept",
            "payload.publish",
            "sidecar.publish",
            "sqlite.commit",
            "processing-graph.execute",
            "processing-step.execute",
            "processing-artifact.persist"
        ];
        CollectionAssert.IsSubsetOf(requiredActivities, activityNames.Distinct().ToArray());
        Assert.IsTrue(activityNames.Contains("environment.enqueue", StringComparer.Ordinal));
        Assert.IsTrue(activityNames.Contains("environment.deliver", StringComparer.Ordinal));
        Assert.IsTrue(activityNames.Contains("environment.ingest", StringComparer.Ordinal));
        Assert.IsTrue(metricTagValues.Contains("CloudCover", StringComparer.Ordinal));
        Assert.IsTrue(metricTagValues.Contains("Simulated", StringComparer.Ordinal));
        Assert.IsFalse(logger.Entries.Any(static entry => entry.Level >= LogLevel.Warning),
            string.Join(Environment.NewLine, logger.Entries.Select(static entry => $"{entry.Level}:{entry.Category}:{entry.Message}")));

        var telemetryText = string.Join('\n',
            metricTagValues.Concat(activityTagValues).Concat(logger.Entries.Select(static entry => entry.Message)));
        string[] forbiddenValues =
        [
            cloudProvenance.ScenarioId,
            cloudProvenance.ParametersSha256,
            cloudProvenance.Parameters.GetRawText(),
            transientProvenance.ScenarioId,
            transientProvenance.ParametersSha256,
            transientProvenance.Parameters.GetRawText(),
            Fixture.DevicePublicId.ToString("D"),
            Fixture.ObservatoryId.ToString("D"),
            "integration-registration-token",
            "cameraagent-integration-key",
            TestClients.SystemCameraAgent.ClientSecret,
            "spatialFrequency",
            "keyframes"
        ];
        foreach (var forbidden in forbiddenValues)
        {
            Assert.IsFalse(telemetryText.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"Environmental telemetry or logs exposed private value '{forbidden}'.");
        }

        await WriteRuntimeEvidenceAsync(
            ingressState,
            processingState,
            stored.Count,
            uploadUnfinishedAtDrainCheckpoint,
            instrumentNames.Keys,
            activityNames,
            metricMeasurements).ConfigureAwait(false);
    }

    private static async Task WriteRuntimeEvidenceAsync(
        RawIngressSnapshot ingress,
        CaptureProcessingSnapshot processing,
        int storedArtifactCount,
        int uploadUnfinishedAtDrainCheckpoint,
        IEnumerable<string> instruments,
        IEnumerable<string> activities,
        IReadOnlyDictionary<string, double> measurements)
    {
        var files = Directory.EnumerateFiles(Fixture.StorageRoot, "*", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path))
            .ToArray();
        var evidence = new
        {
            SchemaVersion = "issue-61-configured-pipeline-runtime-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            StoredArtifactCount = storedArtifactCount,
            UploadUnfinishedAtDrainCheckpoint = uploadUnfinishedAtDrainCheckpoint,
            FilesystemSnapshot = new
            {
                FileCount = files.Length,
                TotalBytes = files.Sum(static file => file.Length),
                SqliteFileCount = files.Count(static file => file.Extension is ".db" or ".db-shm" or ".db-wal"),
                SqliteBytes = files.Where(static file => file.Extension is ".db" or ".db-shm" or ".db-wal")
                    .Sum(static file => file.Length)
            },
            OperationCounts = new
            {
                RawIngressCommits = measurements.GetValueOrDefault("camera_agent.ingress.committed"),
                RawIngressCommittedBytes = measurements.GetValueOrDefault("camera_agent.ingress.committed.bytes"),
                RawIngressSqliteTransactions = measurements.GetValueOrDefault("camera_agent.ingress.sqlite.transactions"),
                ProcessingOutputs = measurements.GetValueOrDefault("camera_agent.processing.outputs"),
                ProcessingOutputBytes = measurements.GetValueOrDefault("camera_agent.processing.output.bytes")
            },
            RawIngress = ingress,
            CaptureProcessing = processing,
            WarningPolicy = "All errors and all VirtualSky/environmental warnings are forbidden; known unrelated TestServer/fixture warnings are dispositioned outside this feature gate.",
            Instruments = instruments.Order(StringComparer.Ordinal).ToArray(),
            Activities = activities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Measurements = measurements.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
        };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            root = root.Parent;
        }
        var outputDirectory = Path.Combine(
            root?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."),
            "TestResults", "issue-61", "runtime");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "configured-pipeline.json"),
            JsonSerializer.Serialize(evidence, EvidenceSerializerOptions))
            .ConfigureAwait(false);
    }

    private static ushort ReadMono16(ReadOnlySpan<byte> pixels, int strideBytes, int x, int y)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pixels[(y * strideBytes + x * 2)..]);

    private static void AssertLineageReachesRaw(
        ReconstructionDescriptor descriptor,
        IReadOnlyCollection<(StoredFrameReference Stored, ArtifactManifestParseResult Parsed)> manifests)
    {
        var byArtifactId = manifests.ToDictionary(
            static item => item.Parsed.Document!.Manifest!.Descriptor.Artifact.ArtifactId,
            static item => item.Parsed.Document!.Manifest!.Descriptor);
        var pending = new Queue<Guid>(descriptor.Artifact.SourceArtifactIds);
        var visited = new HashSet<Guid>();
        while (pending.TryDequeue(out var artifactId))
        {
            if (!visited.Add(artifactId) || !byArtifactId.TryGetValue(artifactId, out var source))
            {
                continue;
            }
            if (source.Artifact.Role == FrameArtifactRole.Raw)
            {
                return;
            }
            foreach (var parent in source.Artifact.SourceArtifactIds)
            {
                pending.Enqueue(parent);
            }
        }
        Assert.Fail("The configured preview lineage did not reach a durable raw artifact.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the configured VirtualSky pipeline.");
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static bool HasNoUnfinishedLaneWork()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
    }

    private static OutboxCheckpoint ReadPendingOutboxCheckpoint()
    {
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idempotency_key, artifact_id, payload_sha256, payload_length
            FROM artifact_outbox_records
            WHERE status = 'pending' AND manifest_kind = 'v2'
            ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read(), "A pending outbox record was not available for the upload checkpoint.");
        return new OutboxCheckpoint(
            reader.GetString(0),
            Guid.ParseExact(reader.GetString(1), "N"),
            reader.GetString(2),
            reader.GetInt64(3));
    }

    private static bool HasMatchingAcknowledgement(OutboxCheckpoint checkpoint)
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        if (!File.Exists(path))
        {
            return false;
        }
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, acknowledgement
            FROM artifact_outbox_records
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", checkpoint.IdempotencyKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "acknowledged" || reader.IsDBNull(1))
        {
            return false;
        }

        try
        {
            var acknowledgement = JsonSerializer.Deserialize<ArtifactUploadAcknowledgement>((byte[])reader[1]);
            acknowledgement?.Validate();
            return acknowledgement is not null &&
                string.Equals(acknowledgement.IdempotencyKey, checkpoint.IdempotencyKey, StringComparison.OrdinalIgnoreCase) &&
                acknowledgement.ArtifactId == checkpoint.ArtifactId &&
                string.Equals(acknowledgement.ChecksumSha256, checkpoint.ChecksumSha256, StringComparison.OrdinalIgnoreCase) &&
                acknowledgement.ByteLength == checkpoint.ByteLength &&
                acknowledgement.AcceptedManifestSchemaVersion == ArtifactManifestV2.CurrentSchemaVersion;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static int CountUnfinishedOutboxRecords()
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        if (!File.Exists(path))
        {
            return 0;
        }
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE status <> 'acknowledged';";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasTransportFailureRetry()
    {
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE status = 'retry' AND last_reason = 'transport-failure';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static void ResetTransportFailureRetries()
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE artifact_outbox_records
            SET status = 'pending', attempt_count = 0, next_attempt_unix_ms = updated_unix_ms,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                last_reason = NULL
            WHERE status = 'retry' AND last_reason = 'transport-failure';
            """;
        Assert.IsGreaterThan(0, command.ExecuteNonQuery(),
            "The injected transport-failure retry must be restored for the shared integration fixture.");
    }

    private static bool HasPendingOrRetryOutboxRecord()
    {
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE status IN ('pending', 'retry');";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static bool HasNoEnvironmentalOutboxRecord(Guid observationId)
    {
        var path = Path.Combine(Fixture.StorageRoot, ".environment", "environmental-observation-outbox.db");
        if (!File.Exists(path))
        {
            return false;
        }
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM environmental_observation_outbox WHERE observation_id = $observationId;";
        command.Parameters.AddWithValue("$observationId", observationId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static MeterListener CreateMeterListener(
        ConcurrentDictionary<string, byte> instrumentNames,
        ConcurrentDictionary<string, double> measurements,
        ConcurrentBag<string> tagValues)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name is
                    CaptureTelemetryMetricsRecorder.MeterName or
                    CaptureControlTelemetry.MeterName or
                    RawIngressTelemetry.MeterName or
                    CaptureProcessingTelemetry.MeterName or
                    "HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery" or
                    "HVO.SkyMonitor.LogicHost.EnvironmentalObservations")
                {
                    instrumentNames.TryAdd(instrument.Name, 0);
                    current.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            measurements.AddOrUpdate(instrument.Name, measurement, (_, total) => total + measurement);
            CaptureTagValues(tags, tagValues);
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            measurements.AddOrUpdate(instrument.Name, measurement, (_, total) => total + measurement);
            CaptureTagValues(tags, tagValues);
        });
        listener.Start();
        return listener;
    }

    private static ActivityListener CreateActivityListener(
        ConcurrentBag<string> activityNames,
        ConcurrentBag<string> tagValues)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is
                CaptureControlTelemetry.ActivitySourceName or
                RawIngressTelemetry.ActivitySourceName or
                CaptureProcessingTelemetry.ActivitySourceName or
                "HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery" or
                "HVO.SkyMonitor.LogicHost.EnvironmentalObservations",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                activityNames.Add(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    if (tag.Value is not null)
                    {
                        tagValues.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void CaptureTagValues(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        ConcurrentBag<string> values)
    {
        foreach (var tag in tags)
        {
            if (tag.Value is not null)
            {
                values.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
            }
        }
    }

    private static string ReadOutboxState(string idempotencyKey)
    {
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status || ':' || COALESCE(last_reason, '') || ':ack=' ||
                   CASE WHEN acknowledgement IS NULL THEN 'missing' ELSE 'present' END
            FROM artifact_outbox_records
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        var selected = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "no-record";
        command.Parameters.Clear();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'leased' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'acknowledged' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END)
            FROM artifact_outbox_records;
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return $"selected={selected}; counts=pending:{reader.GetInt64(0)},leased:{reader.GetInt64(1)},retry:{reader.GetInt64(2)},acknowledged:{reader.GetInt64(3)},quarantined:{reader.GetInt64(4)}";
    }

    private static SqliteConnection OpenOutboxReadConnection()
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record OutboxCheckpoint(
        string IdempotencyKey,
        Guid ArtifactId,
        string ChecksumSha256,
        long ByteLength);

    private sealed class OutageHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(new OutageHttpMessageHandler())
        {
            BaseAddress = new Uri("http://central-outage.invalid/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(1)
        };

        public HttpClient CreateClient(string name) => client;

        public void Dispose() => client.Dispose();
    }

    private sealed class OutageHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("Injected Central transport outage.");
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(
        string category,
        ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Error ||
               category.Contains("Environmental", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("VirtualSky", StringComparison.OrdinalIgnoreCase);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception)));
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(LogLevel Level, string Category, string Message);
}
