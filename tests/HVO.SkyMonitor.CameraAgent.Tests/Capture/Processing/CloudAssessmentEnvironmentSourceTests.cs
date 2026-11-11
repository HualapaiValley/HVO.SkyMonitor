// Production-composition regression ported from the C-1 audit reproducer. Refs #1777
// SQLite/files only; no hosted services, Docker, devices, or outbound weather HTTP.
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Integration")]
public sealed partial class CloudAssessmentEnvironmentSourceTests
{
    public TestContext TestContext { get; set; } = null!;

    // ---------------------------------------------------------------------------------------------------------
    // Level 1: the production processing step produces reason-coded metadata without throwing.
    // ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    public async Task Step_ProcessAsync_ProducesEnvironmentMissingMetadata(string profile)
    {
        using var harness = Harness.Create(profile);
        var provider = harness.Provider;
        var configuration = CreateConfiguration(cloudRequired: false, harness.Root);
        var hostOptions = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        var policyKinds = EnvironmentalAssociationCaptureLaneHandler.ResolvePolicyKinds(
            hostOptions.EnvironmentalAcquisition);
        Assert.IsNotEmpty(policyKinds);
        Assert.DoesNotContain(EnvironmentalObservationKind.RainState, policyKinds);

        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(0);
        var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);

        // The environment-association lane has completed for this capture, as it does in production.
        var association = await DriveLaneAsync(
            provider.GetRequiredService<ICaptureLaneStore>(),
            provider.GetRequiredService<CaptureLanePolicy>().Definitions,
            provider.GetServices<ICaptureLaneHandler>().ToArray(),
            "environment-association",
            configuration).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, association.Result.Outcome, association.Result.Reason);

        // The step instance the production pipeline factory activates, with the DI-provided cloud environment.
        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration);
        try
        {
            var node = graph.Nodes.Single(static candidate => candidate.Id == "CloudAssessment");
            Assert.IsInstanceOfType<CloudAssessmentCaptureProcessingStep>(node.Step);
            var context = CreateStepContext(configuration, submission, receipt);
            context.BeginNode(node.Id, node.Dependencies, node.DeclaredDependencies);

            await node.Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

            var outcome = context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            var product = outcome.Products.Single();
            Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
            Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
            var assessment = CloudAssessmentJson.Parse(product.Payload).Assessment;
            Assert.IsNotNull(assessment);
            Assert.Contains(CloudAssessmentReasonCodes.EnvironmentMissing, assessment.ReasonCodes);
            Assert.AreEqual(EnvironmentalObservationMatchStatus.Missing, assessment.Environment!.PrecipitationStatus);
            Assert.AreEqual(0, harness.Network.Calls);
            Record($"STEP profile={profile} status={outcome.Status} kind={product.Kind} " +
                $"reasons=[{string.Join(',', assessment.ReasonCodes)}] precipitationStatus={assessment.Environment.PrecipitationStatus}");
        }
        finally
        {
            graph.DisposeSteps();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Level 2: the graph node, through the production composition and the real live path:
    // ingress accept -> environment-association lane -> standard lane (StandardCaptureLaneHandler ->
    // FrameProcessingWorker.ProcessGraphItemAsync) -> durable execution record.
    // ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    public async Task LiveGraph_OptionalCloudNode_CompletesWithoutStepWarnings(string profile)
    {
        var observation = await RunLiveCaptureAsync(profile, cloudRequired: false, captures: 2).ConfigureAwait(false);
        Record(observation);

        Assert.AreEqual(0, observation.NetworkCalls, "No network I/O is permitted or expected.");
        Assert.HasCount(2, observation.Captures);
        foreach (var capture in observation.Captures)
        {
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, capture.AssociationLane.Outcome);
            Assert.AreEqual(ProcessingGraphExecutionClass.Live, capture.ExecutionClass);

            // The capture itself is processed: the lane completes and the independent required node runs.
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, capture.StandardLane.Outcome, capture.StandardLane.Reason);
            Assert.AreEqual("Completed", capture.Node("Calibration").Status);
            Assert.AreEqual("Completed", capture.Node("Telemetry").Status);

            var cloud = capture.Node("CloudAssessment");
            Assert.AreEqual("Completed", cloud.Status);
            Assert.IsNull(cloud.Reason);
            Assert.HasCount(1, cloud.Outputs);
            Assert.IsNotNull(capture.CloudReasonCodes);
            Assert.Contains(CloudAssessmentReasonCodes.EnvironmentMissing, capture.CloudReasonCodes);
            Assert.AreEqual("Missing", capture.CloudPrecipitationStatus);

            var presentation = capture.Node("CloudPresentation");
            Assert.AreEqual("Skipped", presentation.Status);
            Assert.AreEqual(CloudAssessmentReasonCodes.MissingClearReference, presentation.Reason);
        }
        Assert.IsFalse(observation.Logs.Any(static entry => entry.EventId == 2013));
    }

    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    public async Task LiveGraph_RequiredCloudNode_CompletesOnFirstAttemptWithoutRetry(string profile)
    {
        var observation = await RunLiveCaptureAsync(profile, cloudRequired: true, captures: 1).ConfigureAwait(false);
        Record(observation);

        var capture = observation.Captures.Single();
        Assert.AreEqual(0, observation.NetworkCalls);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, capture.StandardLane.Outcome);
        Assert.AreEqual(1, capture.StandardLaneAttempt);
        Assert.AreEqual("lane work completed", capture.LaneDisposition);
        Assert.AreEqual(0L, capture.StandardBacklog.PendingCount);
        Assert.AreEqual(0L, capture.StandardBacklog.LeasedCount);
        Assert.AreEqual(0L, capture.StandardBacklog.RetryCount);
        Assert.AreEqual(0L, capture.StandardBacklog.QuarantineCount);
        var cloud = capture.Node("CloudAssessment");
        Assert.AreEqual("Completed", cloud.Status);
        Assert.IsNull(cloud.Reason);
        Assert.AreEqual(1, cloud.AttemptCount);
        Assert.HasCount(1, cloud.Outputs);
        Assert.IsNotNull(capture.CloudReasonCodes);
        Assert.Contains(CloudAssessmentReasonCodes.EnvironmentMissing, capture.CloudReasonCodes);
        Assert.AreEqual("Missing", capture.CloudPrecipitationStatus);
        Assert.IsFalse(observation.Logs.Any(static entry => entry.EventId == 2013));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Controls: the same harness, where the association policy can answer the RainState question.
    // ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("rain-source-no-reading")]
    [DataRow("nws-plus-rain-source")]
    [DataRow("acquisition-disabled")]
    public async Task Control_LiveGraph_CloudNodeCompletesWithEnvironmentMissingReason(string profile)
    {
        var observation = await RunLiveCaptureAsync(profile, cloudRequired: false, captures: 1).ConfigureAwait(false);
        Record(observation);

        var capture = observation.Captures.Single();
        Assert.AreEqual(0, observation.NetworkCalls);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, capture.StandardLane.Outcome, capture.StandardLane.Reason);
        var cloud = capture.Node("CloudAssessment");
        Assert.AreEqual("Completed", cloud.Status);
        Assert.IsNull(cloud.Reason);
        Assert.HasCount(1, cloud.Outputs);
        Assert.IsNotNull(capture.CloudReasonCodes);
        Assert.Contains(CloudAssessmentReasonCodes.EnvironmentMissing, capture.CloudReasonCodes);
        Assert.AreEqual("Missing", capture.CloudPrecipitationStatus);
        Assert.IsFalse(observation.Logs.Any(static entry => entry.EventId == 2013));
    }

    // ---------------------------------------------------------------------------------------------------------

    private static async Task<Observation> RunLiveCaptureAsync(string profile, bool cloudRequired, int captures)
    {
        using var harness = Harness.Create(profile);
        var provider = harness.Provider;
        var root = harness.Root;
        var logs = harness.Logs;
        var network = harness.Network;

        // Reading .Value runs the host's data-annotation validation: this configuration is accepted.
        var hostOptions = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        var configuredKinds = EnvironmentalAssociationCaptureLaneHandler.ResolvePolicyKinds(
            hostOptions.EnvironmentalAcquisition);
        var configuration = CreateConfiguration(cloudRequired, root);

        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        // The production pipeline factory accepts the graph: nothing rejects a CloudAssessment node when
        // the association policy has no RainState.
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
            .ConfigureAwait(false);

        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var lanes = provider.GetRequiredService<CaptureLanePolicy>().Definitions;
        var handlers = provider.GetServices<ICaptureLaneHandler>().ToArray();
        var observed = new List<CaptureObservation>();
        for (var index = 0; index < captures; index++)
        {
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(index), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;

            var association = hostOptions.EnvironmentalAcquisition.Enabled
                ? (await DriveLaneAsync(laneStore, lanes, handlers, "environment-association", configuration)
                    .ConfigureAwait(false)).Result
                : CaptureLaneHandlerResult.Success;
            var (standard, standardContext, laneDisposition, attempt) = await DriveLaneAsync(
                laneStore, lanes, handlers, "standard", configuration).ConfigureAwait(false);
            operations.NotifyLiveWorkChanged();

            Assert.IsNotNull(standardContext.Execution, "A live capture runs under a durable execution.");
            var detail = await operations.ReadExecutionDetailAsync(
                standardContext.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);

            IReadOnlyList<string>? reasonCodes = null;
            string? precipitationStatus = null;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var cloudNode = await store.ReadNodeAsync(captureId, "CloudAssessment", CancellationToken.None)
                .ConfigureAwait(false);
            if (cloudNode is { Outputs.Count: 1 })
            {
                var payloadPath = Path.Combine(
                    root, cloudNode.Outputs[0].PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
                var parsed = CloudAssessmentJson.Parse(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false));
                Assert.IsNotNull(parsed.Assessment);
                reasonCodes = parsed.Assessment.ReasonCodes;
                precipitationStatus = parsed.Assessment.Environment?.PrecipitationStatus.ToString();
            }

            observed.Add(new CaptureObservation(
                captureId,
                association,
                standard,
                laneDisposition,
                attempt,
                (await laneStore.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                    .Single(static backlog => backlog.Lane == "standard"),
                standardContext.Execution.ExecutionClass,
                detail.Execution.Status.ToString(),
                detail.Nodes.Select(static node => new NodeObservation(
                    node.NodeId, node.Required, node.Status, node.Reason, node.AttemptCount,
                    node.Outputs.Select(static output => output.ToString() ?? string.Empty).ToArray())).ToArray(),
                reasonCodes,
                precipitationStatus));
        }

        return new Observation(
            profile,
            cloudRequired,
            configuredKinds,
            registry.ActiveRevisionId,
            observed,
            logs.Entries.ToArray(),
            network.Calls);
    }

    private static async Task<(CaptureLaneHandlerResult Result, CaptureLaneHandlerContext Context, string Disposition, int Attempt)>
        DriveLaneAsync(
            ICaptureLaneStore laneStore,
            IReadOnlyList<CaptureLaneDefinition> lanes,
            ICaptureLaneHandler[] handlers,
            string laneName,
            CameraModuleConfig configuration)
    {
        var definition = lanes.Single(lane => lane.Name == laneName);
        Assert.IsTrue(definition.Enabled, $"Lane {laneName} is enabled.");
        var lease = await laneStore.ClaimAsync(definition, "ci1777-regression", configuration, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(lease, $"Lane {laneName} has claimable work.");
        var handler = handlers.Single(candidate => candidate.Lane == laneName);
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        string disposition;
        if (result.Outcome == CaptureLaneHandlerOutcome.Completed)
        {
            await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            disposition = "lane work completed";
        }
        else
        {
            // What the lane worker does with a non-success result on this attempt.
            var settled = await laneStore.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
            disposition = $"lane store settled attempt {lease.Attempt} as {settled}";
        }
        return (result, lease.Context, disposition, lease.Attempt);
    }

    private static Dictionary<string, string?> CreateSettings(string profile)
    {
        const string prefix = "CameraAgent:EnvironmentalAcquisition:";
        var settings = new Dictionary<string, string?>
        {
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
        };
        void AddNws(int index)
        {
            var source = $"{prefix}Sources:{index}:";
            settings[source + "Id"] = "nws-temperature";
            settings[source + "Type"] = "OnlineWeather";
            settings[source + "Kind"] = "AirTemperature";
            settings[source + "Triggers:0"] = "Periodic";
            settings[source + "PeriodSeconds"] = "300";
            settings[source + "ValidForSeconds"] = "7200";
            settings[source + "StaleAfterSeconds"] = "5400";
            settings[source + "Options:Provider"] = "Nws";
            settings[source + "Options:StationId"] = "KIGM";
            settings[prefix + "WeatherSourcePriority:0"] = "nws-temperature";
        }
        void AddVirtualRain(int index)
        {
            // A RainState source that is configured but never produces a reading during the test.
            var source = $"{prefix}Sources:{index}:";
            settings[source + "Id"] = "rain";
            settings[source + "Type"] = "VirtualEnvironment";
            settings[source + "Kind"] = "RainState";
            settings[source + "Triggers:0"] = "Periodic";
            settings[source + "Options:Seed"] = "7";
            settings[source + "Options:EpochUtc"] = "2026-01-01T00:00:00+00:00";
            settings[source + "Options:BooleanValue"] = "false";
        }
        switch (profile)
        {
            case "nws-only":
                settings[prefix + "Enabled"] = "true";
                AddNws(0);
                break;
            case "sqm-only":
                settings[prefix + "Enabled"] = "true";
                settings[prefix + "Sources:0:Id"] = "sqm";
                settings[prefix + "Sources:0:Type"] = "SkyQualityMeter";
                settings[prefix + "Sources:0:Kind"] = "SkyQuality";
                settings[prefix + "Sources:0:Triggers:0"] = "Periodic";
                settings[prefix + "Sources:0:Options:DeviceId"] = "sqm-fixture";
                break;
            case "rain-source-no-reading":
                settings[prefix + "Enabled"] = "true";
                AddVirtualRain(0);
                break;
            case "nws-plus-rain-source":
                settings[prefix + "Enabled"] = "true";
                AddNws(0);
                AddVirtualRain(1);
                break;
            case "acquisition-disabled":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile));
        }
        return settings;
    }

    // The cloud nodes exactly as cameraagent.sample.json declares them (optional, durable-local, on $raw),
    // plus calibration, storage and a required node, none of which depends on anything cloud-related.
    private static CameraModuleConfig CreateConfiguration(bool cloudRequired, string root)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        "Calibration", "Calibration", Order: -1000,
                        Options: JsonSerializer.SerializeToElement(new { strategy = "None", outputVariant = "none" }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "CloudAssessment", "CloudAssessment", Order: 60,
                        // The sample's reference path; the grid is reduced to fit the 2x2 fixture frame.
                        Options: JsonSerializer.SerializeToElement(new
                        {
                            clearReferenceManifestPath = "references/cloud/clear-reference.manifest.json",
                            gridColumns = 1,
                            gridRows = 1,
                            minimumReferenceSignal = 1,
                            minimumSamplesPerTile = 2
                        }),
                        DependsOn: ["$raw"],
                        Required: cloudRequired,
                        Publication: new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal)),
                    new CaptureProcessingStepConfig(
                        "CloudPresentationLayer", "CloudPresentation", Order: 71,
                        Options: JsonSerializer.SerializeToElement(new
                        {
                            widthPixels = 2,
                            heightPixels = 2,
                            requireQuantifiedAssessment = true
                        }),
                        DependsOn: ["CloudAssessment"],
                        Required: false,
                        Publication: new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal)),
                    // Durable-local outputs require a Storage node to own retention, as in the sample.
                    new CaptureProcessingStepConfig(
                        "Storage", "LocalStorage", Order: 100,
                        Options: JsonSerializer.SerializeToElement(new { storageRoot = root, queueForUpload = false }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "Telemetry", "Telemetry", Order: 1000, DependsOn: ["Calibration", "LocalStorage"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "ci1777-regression-agent");

    private static CaptureLoopSubmission CreateSubmission(int index)
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-1).AddSeconds(index * 2);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var frame = new CameraFrame(
            startedUtc,
            2,
            2,
            CameraPixelFormat.Mono16,
            new byte[] { (byte)(index + 1), 0, 2, 0, 3, 0, 4, 0 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 10, "test"));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    // Mirrors FrameProcessingWorker.CreateContextAsync: the raw frame is reconstructed from the committed payload
    // and carries the artifact identity the committed manifest records.
    private static CaptureProcessingContext CreateStepContext(
        CameraModuleConfig configuration, CaptureLoopSubmission submission, RawCaptureReceipt receipt)
    {
        var descriptor = receipt.Manifest.Descriptor;
        var payload = File.ReadAllBytes(receipt.StoredFrame.AbsolutePath);
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid, reconstruction.ReasonCode);
        var artifact = new FrameArtifact(
            descriptor.Artifact.ArtifactId,
            FrameArtifactRole.Raw,
            frame!,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256);
        return new CaptureProcessingContext(
            configuration,
            submission with { Result = submission.Result with { Frame = frame, Artifacts = new FrameArtifactSet(artifact) } },
            receipt);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ci1777-cloud", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private void Record(Observation observation)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"GRAPH  profile={observation.Profile} cloudRequired={observation.CloudRequired} " +
            $"configuredKinds=[{string.Join(',', observation.ConfiguredKinds)}] " +
            $"revision={observation.RevisionId} networkCalls={observation.NetworkCalls}");
        foreach (var capture in observation.Captures)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  capture association-lane={capture.AssociationLane.Outcome}/{capture.AssociationLane.Reason} " +
                $"standard-lane={capture.StandardLane.Outcome}/{capture.StandardLane.Reason} ({capture.LaneDisposition}) " +
                $"execution={capture.ExecutionClass}/{capture.ExecutionStatus}");
            foreach (var node in capture.Nodes)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"    node {node.NodeId,-18} required={node.Required,-5} status={node.Status,-16} " +
                    $"reason={node.Reason ?? "-"} attempts={node.AttemptCount} outputs={node.Outputs.Count}");
            }
            text.AppendLine(CultureInfo.InvariantCulture,
                $"    cloud product reasonCodes={(capture.CloudReasonCodes is null ? "(no product)" : string.Join(',', capture.CloudReasonCodes))} " +
                $"precipitationStatus={capture.CloudPrecipitationStatus ?? "-"}");
        }
        foreach (var entry in observation.Logs.Where(static entry => entry.Level >= LogLevel.Warning))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  log {entry.Level} event={entry.EventId} \"{entry.Message}\" " +
                $"exception={entry.ExceptionType ?? "-"}: {entry.ExceptionMessage ?? "-"}");
        }
        Record(text.ToString().TrimEnd());
    }

    private void Record(string line) => TestContext.WriteLine(line);

    /// <summary>The production composition over a temp root, with logs recorded and the network refused.</summary>
    private sealed class Harness : IDisposable
    {
        private Harness(string root, ServiceProvider provider, RecordingLoggerProvider logs, FailClosedHandler network)
        {
            Root = root;
            Provider = provider;
            Logs = logs;
            Network = network;
        }

        internal string Root { get; }

        internal ServiceProvider Provider { get; }

        internal RecordingLoggerProvider Logs { get; }

        internal FailClosedHandler Network { get; }

        internal static Harness Create(string profile)
        {
            var root = CreateRoot();
            var logs = new RecordingLoggerProvider();
            var network = new FailClosedHandler();
            var settings = CreateSettings(profile);
            settings["CameraAgent:RawIngressRoot"] = root;
            settings["CameraAgent:RawIngressReserveBytes"] = "0";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            // The host registers its configuration; the environmental source factory reads source options from it.
            services.AddSingleton<IConfiguration>(configuration);
            services.AddCameraAgentInfrastructure(configuration);
            services.AddSingleton<ILoggerProvider>(logs);
            services.Configure<LoggerFilterOptions>(static options => options.MinLevel = LogLevel.Debug);
            // The host (Program.cs) supplies the Data Protection implementation; it is not exercised here.
            services.AddSingleton<IDeploymentLocationProtector>(new PassThroughLocationProtector());
            // Last registration wins: the production client with a transport that cannot reach a network.
            services.AddSingleton(serviceProvider => new WeatherProviderClient(
                new HttpClient(network),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<IEnvironmentalAcquisitionStateStore>(),
                serviceProvider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot));
            var provider = services.BuildServiceProvider();
            return new Harness(root, provider, logs, network);
        }

        public void Dispose()
        {
            Provider.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record Observation(
        string Profile,
        bool CloudRequired,
        IReadOnlyList<EnvironmentalObservationKind> ConfiguredKinds,
        string RevisionId,
        IReadOnlyList<CaptureObservation> Captures,
        IReadOnlyList<LogEntry> Logs,
        int NetworkCalls);

    private sealed record CaptureObservation(
        Guid CaptureId,
        CaptureLaneHandlerResult AssociationLane,
        CaptureLaneHandlerResult StandardLane,
        string LaneDisposition,
        int StandardLaneAttempt,
        CaptureLaneBacklog StandardBacklog,
        ProcessingGraphExecutionClass ExecutionClass,
        string ExecutionStatus,
        IReadOnlyList<NodeObservation> Nodes,
        IReadOnlyList<string>? CloudReasonCodes,
        string? CloudPrecipitationStatus)
    {
        internal NodeObservation Node(string id) => Nodes.Single(node => node.NodeId == id);
    }

    private sealed record NodeObservation(
        string NodeId, bool Required, string Status, string? Reason, int AttemptCount, IReadOnlyList<string> Outputs);

    private sealed record LogEntry(
        LogLevel Level, int EventId, string Category, string Message, string? ExceptionType, string? ExceptionMessage);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _entries = new();

        internal IReadOnlyCollection<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category, System.Collections.Concurrent.ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue(new LogEntry(
                        logLevel, eventId.Id, category, formatter(state, exception),
                        exception?.GetType().Name, exception?.Message));
                }
            }
        }
    }

    private sealed class PassThroughLocationProtector : IDeploymentLocationProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
    }

    /// <summary>Counts and refuses every request, so the test can prove it made no network call.</summary>
    private sealed class FailClosedHandler : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new HttpRequestException("F4 reproducer: network access is disabled.");
        }
    }
}
