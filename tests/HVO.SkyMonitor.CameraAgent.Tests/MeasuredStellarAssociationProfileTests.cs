using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Shipped label-rendering profiles wire a measured stellar association step, so their star labels depend on a
/// measurement of the captured frame. The measurement only decodes linear 16-bit readouts, which these tests pin per
/// profile; the Mono8 W6 profile stays unwired and must still capture and process without labeling any star.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class MeasuredStellarAssociationProfileTests
{
    private const string IntegrationFixture = "Fixtures/cameraagent.integration.json";
    private static readonly ObservatoryLocation Location = new(35.5599378, -113.9119818, 520, "America/Phoenix");
    private static readonly DateTimeOffset NightUtc = new(2026, 8, 15, 7, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("allsky01.virtual-asi676mc.full.json")]
    [DataRow("hvo-edge-01.virtual-asi676mm.full.json")]
    [DataRow("cameraagent.asi178mc-comparison.json")]
    [DataRow("cameraagent.sample.json")]
    [DataRow("cameraagent.standalone-production-smoke.json")]
    [DataRow("cameraagent.standalone-w6.json")]
    [DataRow("virtual-asi174.full.json")]
    [DataRow("virtual-asi178mc.full.json")]
    [DataRow("virtual-asi676mc.full.json")]
    [DataRow("virtual-asi676mm.full.json")]
    [DataRow(IntegrationFixture)]
    public async Task WiredProfile_CapturesALinearReadoutTheMeasurementCanDecode(string fileName)
    {
        var root = Directory.CreateTempSubdirectory("hvo-526-readout-").FullName;
        try
        {
            var configuration = await LoadAsync(fileName, root).ConfigureAwait(false);
            var steps = configuration.Pipeline!.Steps;
            var scene = steps.Single(static step => step.Type == "ProjectedScene");
            var measurement = steps.Single(static step => step.Type == "MeasuredStellarAssociations");
            CollectionAssert.AreEqual(new[] { scene.Id }, measurement.DependsOn!.ToArray());
            Assert.IsTrue(steps.Any(step => step.Type is "Annotation" or "ScenePresentationLayer" &&
                (step.DependsOn ?? []).Contains(measurement.Id)), "No label renderer consumes the measurement.");

            var module = new VirtualSkyCameraModule(
                TimeProvider.System,
                new InMemoryCelestialCatalog([]),
                new ProjectedSceneStore(),
                StandardConstellationTopology.CreateD3Celestial(),
                new AstronomyEnginePlanetEphemeris());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var result = await module.CaptureAsync(NightRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            var frame = result.Frame;
            Assert.IsNotNull(frame);
            var layout = frame.Layout;
            Assert.IsNotNull(layout);
            Assert.IsTrue(layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16,
                layout.PixelFormat.ToString());
            Assert.AreEqual(FrameSamplePacking.ByteAligned, layout.Packing);
            Assert.AreEqual(16, layout.ContainerDepthBits);
            Assert.AreEqual(FrameByteOrder.LittleEndian, layout.ByteOrder);
            Assert.IsNotNull(layout.WhiteLevel, "The readout declares no white level.");
            Assert.IsTrue(double.IsFinite(layout.WhiteLevel.Value));
            Assert.AreEqual(layout.ByteLength, frame.PixelData.Length);
            Assert.IsTrue(ResolvesSaturationLevel(layout),
                $"{layout.LevelCodeSpace}/{layout.StoredCodeTransform} at {layout.SampleDepthBits}-in-{layout.ContainerDepthBits}");
            if (layout.PixelFormat == CameraPixelFormat.BayerRggb16)
            {
                Assert.IsTrue(layout.Readout?.CfaOriginX is null or 0 or 1);
                Assert.IsTrue(layout.Readout?.CfaOriginY is null or 0 or 1);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Mono8W6Profile_ProcessesWithoutMeasurementAndLabelsNoStar()
    {
        var plain = await ProcessMono8Async(null).ConfigureAwait(false);
        var unlabeled = await ProcessMono8Async(("drawLabels", false)).ConfigureAwait(false);
        var expected = await ProcessMono8Async(("expectedPositionDiagnostics", true)).ConfigureAwait(false);

        // Turning labels off changes nothing, so the fail-closed policy suppressed every star label; the diagnostic
        // run proves a label-eligible star was in view and would otherwise have been drawn.
        CollectionAssert.AreEqual(unlabeled, plain);
        CollectionAssert.AreNotEqual(expected, plain);
    }

    private static async Task<byte[]> ProcessMono8Async((string Name, bool Value)? annotationOption)
    {
        var root = Directory.CreateTempSubdirectory("hvo-526-mono8-").FullName;
        try
        {
            var catalog = CreateCatalog();
            var configuration = await LoadAsync("cameraagent.standalone-w6-mono8.json", root).ConfigureAwait(false);
            if (annotationOption is { } option)
                configuration = WithStepOption(configuration, "Annotation", option.Name, option.Value);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICelestialCatalog>(catalog);
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CameraAgent:RawIngressRoot"] = root,
                    ["CameraAgent:RawIngressReserveBytes"] = "0"
                })
                .Build());
            services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
            var provider = services.BuildServiceProvider();
            await using var providerLifetime = provider.ConfigureAwait(false);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            // The host's factory supplies the durable scene staging store, as a running agent's capture does.
            var module = provider.GetRequiredService<ICameraModuleFactory>().Create(configuration.ModuleType);
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var request = NightRequest(configuration);
            var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraPixelFormat.Mono8, result.Frame!.PixelFormat);
            var submission = new CaptureLoopSubmission(request, result, NightUtc, request.TargetInterval, TimeSpan.Zero);
            var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(configuration);
            try
            {
                Assert.IsFalse(graph.Nodes.Any(static node => node.Alias == "MeasuredStellarAssociations"));
                var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                var processed = await FrameProcessingWorker.ProcessGraphItemAsync(
                    new FrameProcessingItem(configuration, submission, receipt), graph, persistence,
                    provider.GetRequiredService<CaptureProcessingTelemetry>(), 1, NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);

                var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                byte[]? annotated = null;
                foreach (var node in graph.Nodes)
                {
                    var durable = await persistence.ReadNodeAsync(captureId, node.Id, CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(durable, node.Id);
                    Assert.AreEqual(DurableProcessingNodeStatus.Completed, durable.Status, $"{node.Id}: {durable.Reason}");
                    Assert.IsFalse(durable.Outcome is ProcessingOutcomeStatus.RetryableFailure
                        or ProcessingOutcomeStatus.TerminalFailure, $"{node.Id}: {durable.Outcome} {durable.Reason}");
                    if (node.Alias == "Annotation")
                    {
                        annotated = await File.ReadAllBytesAsync(
                            Path.Combine(root, durable.Outputs.Single().PayloadRelativePath)).ConfigureAwait(false);
                    }
                }
                Assert.IsNotNull(annotated, "The profile rendered no annotation.");
                return annotated;
            }
            finally
            {
                graph.DisposeSteps();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Mirrors the measurement's linear decode: the declared white level must resolve to a stored code.</summary>
    private static bool ResolvesSaturationLevel(FrameLayoutDescriptor layout)
    {
        var codeSpace = layout.LevelCodeSpace ?? (layout.SampleDepthBits == layout.ContainerDepthBits
            ? FrameLevelCodeSpace.StoredContainer : (FrameLevelCodeSpace?)null);
        return codeSpace switch
        {
            FrameLevelCodeSpace.StoredContainer => true,
            FrameLevelCodeSpace.NativeSample => layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.RightAlignedV1 or FrameStoredCodeTransform.IdentityV1 or
                    FrameStoredCodeTransform.FullRangeScaledV1 or FrameStoredCodeTransform.LeftShiftedV1 => true,
                null => layout.SampleDepthBits == layout.ContainerDepthBits,
                _ => false
            },
            _ => false
        };
    }

    private static CaptureRequest NightRequest(CameraModuleConfig configuration)
        => new(NightUtc, configuration.Rig.Pipeline.CaptureInterval, CaptureMode.Still,
            new CaptureSetpoint(configuration.Rig.Pipeline.NightExposure, configuration.Rig.Pipeline.NightGain, null, null));

    /// <summary>
    /// Bright stars near the W6 zenith at <see cref="NightUtc"/>, inside the Mono8 profile's central region of interest
    /// and its label magnitude limit.
    /// </summary>
    private static InMemoryCelestialCatalog CreateCatalog()
    {
        CelestialCatalogObject Create(string id, string name, string hip, AltAzPoint horizontal)
        {
            var ofDate = CoordinateTransforms.HorizontalToEquatorial(
                horizontal, NightUtc, Location.LatitudeDegrees, Location.LongitudeDegrees);
            var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, NightUtc);
            return new CelestialCatalogObject(id, name, j2000.RightAscensionHours, j2000.DeclinationDegrees, 0.03, 0, hip);
        }

        return new InMemoryCelestialCatalog([
            Create("HIP 900001", "Zenith Star", "900001", new AltAzPoint(89, 0)),
            Create("HIP 900002", "East Star", "900002", new AltAzPoint(86, 90)),
            Create("HIP 900003", "West Star", "900003", new AltAzPoint(84, 270))
        ]);
    }

    private static CameraModuleConfig WithStepOption(CameraModuleConfig configuration, string type, string name, bool value)
    {
        var steps = configuration.Pipeline!.Steps.Select(step =>
        {
            if (step.Type != type)
                return step;
            var options = JsonNode.Parse(step.Options!.Value.GetRawText())!.AsObject();
            options[name] = value;
            return step with { Options = JsonSerializer.SerializeToElement(options) };
        }).ToArray();
        return configuration with { Pipeline = configuration.Pipeline with { Steps = steps } };
    }

    /// <summary>Loads a profile with its storage redirected into <paramref name="root"/>.</summary>
    private static async Task<CameraModuleConfig> LoadAsync(string fileName, string root)
    {
        var template = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, fileName)).ConfigureAwait(false);
        var path = Path.Combine(root, Path.GetFileName(fileName));
        await File.WriteAllTextAsync(path, template
            .Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(Path.Combine(root, "storage")), StringComparison.Ordinal)
            .Replace("__TRANSIENT_EPOCH_UTC__", JsonSerializer.Serialize(NightUtc.AddMinutes(-1)), StringComparison.Ordinal))
            .ConfigureAwait(false);
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = path,
            AgentId = "measured-association-profile-test",
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = Location
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        var configuration = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        return configuration.Pipeline is null
            ? configuration
            : WithStorageRoot(configuration, Path.Combine(root, "storage"));
    }

    private static CameraModuleConfig WithStorageRoot(CameraModuleConfig configuration, string storageRoot)
    {
        var steps = configuration.Pipeline!.Steps.Select(step =>
        {
            if (step.Type != "Storage" || step.Options is not { } raw)
                return step;
            var options = JsonNode.Parse(raw.GetRawText())!.AsObject();
            options["storageRoot"] = storageRoot;
            return step with { Options = JsonSerializer.SerializeToElement(options) };
        }).ToArray();
        return configuration with { Pipeline = configuration.Pipeline with { Steps = steps } };
    }
}
