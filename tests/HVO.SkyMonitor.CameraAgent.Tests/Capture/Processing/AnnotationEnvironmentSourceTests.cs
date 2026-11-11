// Production-composition regression for presentation environment lookups outside the configured policy. Refs #1784
// SQLite/files only; no hosted services, Docker, devices, or outbound weather HTTP.
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Integration")]
public sealed class AnnotationEnvironmentSourceTests
{
    private static readonly string[] DefaultKindsMissing =
    [
        "AIRTEMPERATURE MISSING", "RELATIVEHUMIDITY MISSING", "ATMOSPHERICPRESSURE MISSING",
        "WINDSPEED MISSING", "RAINSTATE MISSING", "CLOUDCOVER MISSING"
    ];

    private static readonly string[] SelectedFactKinds = ["AirTemperature", "RainState"];

    private static readonly string[] OutsidePolicySelection = ["RainState"];

    public TestContext TestContext { get; set; } = null!;

    // Level 1: the production Annotation step, with metadata corners and the default environment token and kinds.

    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    public async Task Step_AnnotationWithMetadataCorners_ProducesWithMissingLinesForUnconfiguredKinds(string profile)
    {
        using var harness = Harness.Create(profile);
        var (outcome, thrown, lines) = await RunAnnotationStepAsync(harness, associate: true).ConfigureAwait(false);

        Record($"STEP profile={profile} thrown={thrown?.GetType().Name ?? "-"}: {thrown?.Message ?? "-"} " +
            $"outcome={outcome?.Status.ToString() ?? "-"}/{outcome?.ReasonCode ?? "-"} lines=[{string.Join(" | ", lines ?? [])}] " +
            $"network={harness.Network.Calls}");
        Assert.IsNull(thrown, thrown?.ToString());
        Assert.IsNotNull(outcome);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, outcome.Products.Single().Role);
        // No reading was acquired, so the configured kind's association is Missing too.
        CollectionAssert.AreEqual(DefaultKindsMissing, lines);
        Assert.AreEqual(0, harness.Network.Calls);
    }

    [TestMethod]
    [DataRow("nws-only", ProcessingOutcomeStatus.RetryableFailure)]
    [DataRow("sqm-only", ProcessingOutcomeStatus.Produced)]
    public async Task Step_BeforeTheAssociationLane_OnlyAConfiguredSelectedKindWaits(string profile, ProcessingOutcomeStatus expected)
    {
        using var harness = Harness.Create(profile);
        var (outcome, thrown, _) = await RunAnnotationStepAsync(harness, associate: false).ConfigureAwait(false);

        Record($"PENDING profile={profile} thrown={thrown?.GetType().Name ?? "-"} " +
            $"outcome={outcome?.Status.ToString() ?? "-"}/{outcome?.ReasonCode ?? "-"}");
        Assert.IsNull(thrown, thrown?.ToString());
        Assert.IsNotNull(outcome);
        Assert.AreEqual(expected, outcome.Status, outcome.ReasonCode);
        if (expected == ProcessingOutcomeStatus.RetryableFailure)
        {
            // AirTemperature is configured and selected: its pending association still defers the annotation.
            Assert.AreEqual(ProcessingReasonCodes.EnvironmentAssociationPending, outcome.ReasonCode);
        }
    }

    // Level 2: the graph node through the production live path, in the cameraagent.standalone-w6-mono8.json shape.

    [TestMethod]
    [DataRow("nws-only", false)]
    [DataRow("sqm-only", false)]
    [DataRow("nws-only", true)]
    [DataRow("sqm-only", true)]
    public async Task LiveGraph_W6AnnotationNode_CompletesOnFirstAttemptWithoutStepWarnings(string profile, bool required)
    {
        using var harness = Harness.Create(profile);
        var provider = harness.Provider;
        var configuration = CreateW6Configuration(required, harness.Root);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        _ = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var receipt = await ingress.AcceptAsync(
            configuration, await CreateSceneSubmissionAsync(provider).ConfigureAwait(false), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var association = await DriveLaneAsync(provider, "environment-association", configuration).ConfigureAwait(false);
        var standard = await DriveLaneAsync(provider, "standard", configuration).ConfigureAwait(false);
        Assert.IsNotNull(standard.Context.Execution);
        var detail = await operations.ReadExecutionDetailAsync(standard.Context.Execution.ExecutionId, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(detail);
        var backlog = (await provider.GetRequiredService<ICaptureLaneStore>().ReadBacklogsAsync(CancellationToken.None)
            .ConfigureAwait(false)).Single(static item => item.Lane == "standard");
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"GRAPH profile={profile} annotationRequired={required} association={association.Result.Outcome} " +
            $"standard={standard.Result.Outcome}/{standard.Result.Reason} ({standard.Disposition}) " +
            $"retry={backlog.RetryCount} quarantine={backlog.QuarantineCount} network={harness.Network.Calls}");
        foreach (var node in detail.Nodes)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  node {node.NodeId,-16} required={node.Required,-5} status={node.Status,-16} reason={node.Reason ?? "-"} attempts={node.AttemptCount} outputs={node.Outputs.Count}");
        }
        foreach (var entry in harness.Logs.Entries.Where(static entry => entry.Level >= LogLevel.Warning))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  log {entry.Level} event={entry.EventId} \"{entry.Message}\" exception={entry.ExceptionType ?? "-"}: {entry.ExceptionMessage ?? "-"}");
        }
        Record(text.ToString().TrimEnd());

        Assert.AreEqual(0, harness.Network.Calls);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, association.Result.Outcome, association.Result.Reason);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, standard.Result.Outcome, standard.Result.Reason);
        Assert.AreEqual(1, standard.Attempt);
        Assert.AreEqual("lane work completed", standard.Disposition);
        Assert.AreEqual(0L, backlog.RetryCount);
        Assert.AreEqual(0L, backlog.QuarantineCount);
        Assert.AreEqual("Completed", detail.Nodes.Single(static node => node.NodeId == "projected-scene").Status);
        var annotation = detail.Nodes.Single(static node => node.NodeId == "annotation");
        Assert.AreEqual(required, annotation.Required);
        Assert.AreEqual("Completed", annotation.Status, annotation.Reason);
        Assert.IsNull(annotation.Reason);
        Assert.AreEqual(1, annotation.AttemptCount);
        Assert.HasCount(1, annotation.Outputs);
        Assert.IsFalse(harness.Logs.Entries.Any(static entry => entry.EventId == 2013));
    }

    // The shared presentation facts builder (FrameFactsPresentation and EnvironmentPresentationLayer).

    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    public async Task FactsBuilder_ConfiguredSelectionOutsidePolicy_RetainsMissingFacts(string profile)
    {
        using var harness = Harness.Create(profile);
        var provider = harness.Provider;
        var configuration = CreateProjectionConfiguration(harness.Root);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(0);
        var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        _ = await DriveLaneAsync(provider, "environment-association", configuration).ConfigureAwait(false);
        var persisted = await provider.GetRequiredService<ILocalEnvironmentalAssociationStore>().ReadAssociationsAsync(
            harness.Root, receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        var context = CreateStepContext(configuration, submission, receipt);
        context.BeginNode("frame-facts", []);
        var scene = await CreateSceneAsync(receipt.Manifest.Descriptor).ConfigureAwait(false);
        // RainState is outside both policies; AirTemperature is configured for nws-only only.
        EnvironmentalObservationKind[] selection = [EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.AirTemperature];

        Exception? thrown = null;
        PresentationMetadataFactsProductV1? facts = null;
        try
        {
            facts = await provider.GetRequiredService<PresentationMetadataFactsBuilder>().BuildAsync(
                context, scene, null, [], selection, CancellationToken.None, retainFrameFacts: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            thrown = exception;
        }
        Record($"FACTS profile={profile} selection=[{string.Join(',', selection)}] thrown={thrown?.GetType().Name ?? "-"}: " +
            $"{thrown?.Message ?? "-"} facts=[{string.Join(", ", facts?.Environment.Select(static fact => $"{fact.Kind}:{fact.Status}") ?? [])}]");
        Assert.IsNull(thrown, thrown?.ToString());
        Assert.IsNotNull(facts);
        CollectionAssert.AreEqual(SelectedFactKinds, facts.Environment.Select(static fact => fact.Kind).ToArray());
        Assert.IsTrue(facts.Environment.All(static fact => fact.Status == "Missing" && fact.ObservationId is null));
        // A missing fact carries the policy identity of the sorted selection, as with acquisition disabled.
        var missingPolicyIdentity = EnvironmentalAssociationService.CreatePolicyIdentity(
            [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RainState]);
        var air = facts.Environment[0];
        if (profile == "nws-only")
        {
            // The configured kind is still the durable capture association the lane persisted.
            var association = persisted.Single();
            Assert.AreEqual(EnvironmentalObservationKind.AirTemperature, association.Kind);
            Assert.AreEqual(association.AssociationIdentitySha256, air.AssociationIdentitySha256);
            Assert.AreEqual(association.PolicyIdentitySha256, air.PolicyIdentitySha256);
        }
        else
        {
            Assert.AreEqual(EnvironmentalObservationKind.SkyQuality, persisted.Single().Kind);
            Assert.AreEqual(missingPolicyIdentity, air.PolicyIdentitySha256);
        }
        Assert.AreEqual(missingPolicyIdentity, facts.Environment[1].PolicyIdentitySha256);
    }

    // Decision record: graph validation is unchanged. Annotation and FrameFactsPresentation selections outside the
    // policy load and now render MISSING; the EnvironmentPresentationLayer rejection is kept as is.

    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    public void Factory_PolicyValidationContractIsUnchanged(string profile)
    {
        using var harness = Harness.Create(profile);
        var factory = harness.Provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        factory.CreateGraph(CreateW6Configuration(required: true, harness.Root)).DisposeSteps();
        factory.CreateGraph(CreateFrameFactsConfiguration(FrameFactsPresentation.StepAlias)).DisposeSteps();
        var legacy = Assert.ThrowsExactly<System.ComponentModel.DataAnnotations.ValidationException>(
            () => factory.CreateGraph(CreateFrameFactsConfiguration("EnvironmentPresentationLayer")));
        Record($"FACTORY profile={profile} annotation=accepted frameFacts=accepted environmentLayer=rejected: {legacy.Message}");
        StringAssert.Contains(legacy.Message, "RainState are outside the configured association policy", StringComparison.Ordinal);
    }

    private static async Task<(ProcessingOutcome? Outcome, Exception? Thrown, string[]? Lines)> RunAnnotationStepAsync(
        Harness harness, bool associate)
    {
        var provider = harness.Provider;
        var configuration = CreateProjectionConfiguration(harness.Root);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(0);
        var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        if (associate)
        {
            var association = await DriveLaneAsync(provider, "environment-association", configuration).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, association.Result.Outcome, association.Result.Reason);
        }

        // The step instances the production pipeline factory activates, with the DI-provided environment services.
        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration);
        try
        {
            var context = CreateStepContext(configuration, submission, receipt);
            var preview = graph.Nodes.Single(static node => node.Id == "preview");
            context.BeginNode(preview.Id, preview.Dependencies, preview.DeclaredDependencies);
            await preview.Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var node = graph.Nodes.Single(static candidate => candidate.Id == "annotation");
            var annotation = (AnnotationCaptureProcessingStep)node.Step;
            context.BeginNode(node.Id, node.Dependencies, node.DeclaredDependencies);
            var outcomesBefore = context.ProcessingOutcomes.Count;
            Exception? thrown = null;
            try
            {
                await annotation.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not AssertFailedException)
            {
                thrown = exception;
            }
            var outcome = context.ProcessingOutcomes.Skip(outcomesBefore).LastOrDefault();
            string[]? lines = null;
            if (thrown is null && outcome?.Status == ProcessingOutcomeStatus.Produced)
            {
                lines = (await annotation.CreateEnvironmentLinesAsync(context.ReconstructionDescriptor, CancellationToken.None)
                    .ConfigureAwait(false))?.ToArray();
            }
            return (outcome, thrown, lines);
        }
        finally
        {
            graph.DisposeSteps();
        }
    }

    // ---------------------------------------------------------------------------------------------------------

    private static async Task<(CaptureLaneHandlerResult Result, CaptureLaneHandlerContext Context, string Disposition, int Attempt)>
        DriveLaneAsync(IServiceProvider provider, string laneName, CameraModuleConfig configuration)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var definition = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(lane => lane.Name == laneName);
        var lease = await laneStore.ClaimAsync(definition, "ci1784-regression", configuration, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(lease, $"Lane {laneName} has claimable work.");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(candidate => candidate.Lane == laneName);
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        string disposition;
        if (result.Outcome == CaptureLaneHandlerOutcome.Completed)
        {
            await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            disposition = "lane work completed";
        }
        else
        {
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
        switch (profile)
        {
            case "nws-only":
                settings[prefix + "Enabled"] = "true";
                settings[prefix + "Sources:0:Id"] = "nws-temperature";
                settings[prefix + "Sources:0:Type"] = "OnlineWeather";
                settings[prefix + "Sources:0:Kind"] = "AirTemperature";
                settings[prefix + "Sources:0:Triggers:0"] = "Periodic";
                settings[prefix + "Sources:0:PeriodSeconds"] = "300";
                settings[prefix + "Sources:0:ValidForSeconds"] = "7200";
                settings[prefix + "Sources:0:StaleAfterSeconds"] = "5400";
                settings[prefix + "Sources:0:Options:Provider"] = "Nws";
                settings[prefix + "Sources:0:Options:StationId"] = "KIGM";
                settings[prefix + "WeatherSourcePriority:0"] = "nws-temperature";
                break;
            case "sqm-only":
                settings[prefix + "Enabled"] = "true";
                settings[prefix + "Sources:0:Id"] = "sqm";
                settings[prefix + "Sources:0:Type"] = "SkyQualityMeter";
                settings[prefix + "Sources:0:Kind"] = "SkyQuality";
                settings[prefix + "Sources:0:Triggers:0"] = "Periodic";
                settings[prefix + "Sources:0:Options:DeviceId"] = "sqm-fixture";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile));
        }
        return settings;
    }

    // The cameraagent.standalone-w6-mono8.json Annotation node: the projected-scene dependency, image circle, cardinals
    // and metadata corners with the default environment token and kinds, plus storage and a required telemetry node.
    private static CameraModuleConfig CreateW6Configuration(bool required, string root)
        => ProcessingGraphOperationsTests.CreateProjectedSceneConfiguration() with
        {
            AgentId = "ci1784-regression-agent",
            Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig("ProjectedScene", "projected-scene", Order: 5, DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "Preview", "preview", Order: 10,
                        Options: JsonSerializer.SerializeToElement(new { outputVariant = "mono8-display" }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "Annotation", "annotation", Order: 20,
                        Options: JsonSerializer.SerializeToElement(new
                        {
                            requireProjectedSceneDependency = true,
                            drawLabels = true,
                            maximumLabelMagnitude = 2.5,
                            drawConstellationLines = false,
                            drawImageCircle = true,
                            drawCardinalDirections = true,
                            drawMetadataCorners = true,
                            recipeVersion = "projected-scene-annotation-v3",
                            outputVariant = "mono8-annotated"
                        }),
                        DependsOn: ["preview", "projected-scene"],
                        Required: required),
                    new CaptureProcessingStepConfig(
                        "Storage", "storage", Order: 30,
                        Options: JsonSerializer.SerializeToElement(new { storageRoot = root, queueForUpload = false }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig("Telemetry", "telemetry", Order: 40, DependsOn: ["storage"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };

    // Annotation without a projected scene on a module that is not scene-bearing: image circle and cardinals from the
    // rig projection, plus metadata corners.
    private static CameraModuleConfig CreateProjectionConfiguration(string root)
        => ProcessingGraphOperationsTests.CreateProjectedSceneConfiguration() with
        {
            Module = new CameraModuleDescriptor("Test"),
            AgentId = "ci1784-regression-agent",
            Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        "Preview", "preview", Order: 10,
                        Options: JsonSerializer.SerializeToElement(new { outputVariant = "mono8-display" }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "Annotation", "annotation", Order: 20,
                        Options: JsonSerializer.SerializeToElement(new
                        {
                            drawLabels = false,
                            drawImageCircle = true,
                            drawCardinalDirections = true,
                            drawMetadataCorners = true,
                            outputVariant = "mono8-annotated"
                        }),
                        DependsOn: ["preview"]),
                    new CaptureProcessingStepConfig(
                        "Storage", "storage", Order: 30,
                        Options: JsonSerializer.SerializeToElement(new { storageRoot = root, queueForUpload = false }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig("Telemetry", "telemetry", Order: 40, DependsOn: ["storage"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };

    private static CameraModuleConfig CreateFrameFactsConfiguration(string alias)
        => ProcessingGraphOperationsTests.CreateProjectedSceneConfiguration() with
        {
            Pipeline = new CapturePipelineConfig(
                [
                    new("Calibration", "calibration", 10, DependsOn: ["$raw"]),
                    new("RollingCombination", "rolling", 20, DependsOn: ["calibration"]),
                    new("CombinedPreview", "preview", 30, DependsOn: ["rolling"]),
                    new("ProjectedScene", "scene", 40, DependsOn: ["$raw"]),
                    new(alias, "facts", 50,
                        JsonSerializer.SerializeToElement(new { widthPixels = 64, heightPixels = 64, environmentalKinds = OutsidePolicySelection }),
                        ["scene", "preview"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };

    private static CaptureLoopSubmission CreateSubmission(int index)
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-1).AddSeconds(index * 2);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var frame = new CameraFrame(
            startedUtc, 2, 2, CameraPixelFormat.Mono16,
            new byte[] { (byte)(index + 1), 0, 2, 0, 3, 0, 4, 0 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 10, "test"));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    // A capture whose frame carries staged projected-scene provenance, as a virtual-sky capture does.
    private static async Task<CaptureLoopSubmission> CreateSceneSubmissionAsync(IServiceProvider provider)
    {
        var stageKey = new string('7', 64);
        var sceneId = new string('8', 64);
        await provider.GetRequiredService<IProjectedSceneStagingStore>().StageAsync(
            stageKey, sceneId, await ProcessingGraphOperationsTests.CreateProjectedSceneAsync().ConfigureAwait(false),
            CancellationToken.None).ConfigureAwait(false);
        return ProcessingGraphOperationsTests.WithSceneProvenance(CreateSubmission(0), sceneId, stageKey);
    }

    // Mirrors FrameProcessingWorker.CreateContextAsync: the raw frame is reconstructed from the committed payload.
    private static CaptureProcessingContext CreateStepContext(
        CameraModuleConfig configuration, CaptureLoopSubmission submission, RawCaptureReceipt receipt)
    {
        var descriptor = receipt.Manifest.Descriptor;
        var payload = File.ReadAllBytes(receipt.StoredFrame.AbsolutePath);
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid, reconstruction.ReasonCode);
        var artifact = new FrameArtifact(
            descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame!,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256);
        return new CaptureProcessingContext(
            configuration,
            submission with { Result = submission.Result with { Frame = frame, Artifacts = new FrameArtifactSet(artifact) } },
            receipt);
    }

    private static async Task<ProjectedSceneV1> CreateSceneAsync(ReconstructionDescriptor descriptor)
        => ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative,
            await ProcessingGraphOperationsTests.CreateProjectedSceneAsync().ConfigureAwait(false),
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor)), "calibration-v1", "projection-v1");

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
            var root = Path.Combine(Path.GetTempPath(), "ci1784-annotation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var logs = new RecordingLoggerProvider();
            var network = new FailClosedHandler();
            var settings = CreateSettings(profile);
            settings["CameraAgent:RawIngressRoot"] = root;
            settings["CameraAgent:RawIngressReserveBytes"] = "0";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddCameraAgentInfrastructure(configuration);
            services.AddSingleton<ILoggerProvider>(logs);
            services.Configure<LoggerFilterOptions>(static options => options.MinLevel = LogLevel.Debug);
            services.AddSingleton<IDeploymentLocationProtector>(new PassThroughLocationProtector());
            // Last registration wins: the production client with a transport that cannot reach a network.
            services.AddSingleton(serviceProvider => new WeatherProviderClient(
                new HttpClient(network),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<IEnvironmentalAcquisitionStateStore>(),
                serviceProvider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot));
            return new Harness(root, services.BuildServiceProvider(), logs, network);
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
            throw new HttpRequestException("ci1784 regression: network access is disabled.");
        }
    }
}
