using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal",
    Justification = "MSTest requires public test classes.")]
public sealed class Issue208W1W2RetainedEvidence_20260726
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private const double Gain = 82;
    private const double Offset = 1;
    private const double TemperatureC = -10;
    private const ushort KnownTruthAdu = 12000;
    private static readonly DateTimeOffset FixtureUtc = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan BiasExposure = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan DarkExposure = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlatExposure = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefectExposure = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan LightExposure = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Asi676Cadence = TimeSpan.FromSeconds(10);
#if DEBUG
    private const bool ReleaseBuild = false;
#else
    private const bool ReleaseBuild = true;
#endif
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task W1W2AndAsi676_MasterHostedCorrectionPublicationAndRestart_RetainEvidence()
    {
#if DEBUG
        Assert.Fail("Canonical issue #208 retained evidence must execute from a Release build.");
#endif
        var revision = SanitizePathSegment(
            Environment.GetEnvironmentVariable("HVO_PERF_REVISION") ?? "working-tree");
        var results = new List<WorkloadEvidence>(2);
        foreach (var workload in new[]
                 {
                     new Workload("C208-W1", 1936, 1216, CameraPixelFormat.Mono16),
                     new Workload("C208-W2", 3096, 2080, CameraPixelFormat.BayerRggb16)
                 })
        {
            results.Add(await RunWorkloadAsync(workload).ConfigureAwait(false));
        }
        var asi676 = await RunAsi676Async().ConfigureAwait(false);

        var outputDirectory = ResolveRepositoryPath(Path.Combine("TestResults", "issue-208", revision));
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "c208-w1-w2-calibration-evidence.json");
        var evidence = new
        {
            schemaVersion = "issue-208-calibration-retained-evidence-v2",
            revision,
            recordedUtc = DateTimeOffset.UtcNow,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                processorCount = Environment.ProcessorCount,
                serverGarbageCollection = GCSettings.IsServerGC,
                configuration = ReleaseBuild ? "Release" : "non-release-rejected",
                concurrency = 1,
                initialBacklog = 0,
                warmups = WarmupCount,
                measuredOperations = MeasurementCount
            },
            productionPath = new
            {
                acquisition = nameof(VirtualCalibrationAcquisitionCoordinator),
                masterBuilder = nameof(CalibrationMasterBuilder),
                publisher = nameof(CalibrationArtifactPublisher),
                library = nameof(SqliteCalibrationLibraryStore),
                inputLoader = nameof(CalibrationLibraryProcessingInputLoader),
                correctionRecipe = BuiltInProcessingRecipes.ReferenceCalibration
            },
            asi676,
            workloads = results
        };
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #208 retained W1/W2 and ASI676 evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<Asi676Evidence> RunAsi676Async()
    {
        const int lightCount = 5;
        var root = Path.Combine(Path.GetTempPath(), $"hvo-issue-208-asi676-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Fixture? acquisitionFixture = null;
        SqliteCalibrationLibraryStore? restartedStore = null;
        VirtualSkyCameraModule? cleanModule = null;
        VirtualSkyCameraModule? corruptedModule = null;
        try
        {
            var model = new VirtualCalibrationSourceModelV1 { Seed = 676 };
            var modelIdentity = VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(model);
            var configurations = await LoadAsi676ConfigurationsAsync(model).ConfigureAwait(false);
            var inputLayout = SensorReadoutResolver.Resolve(
                configurations.Corrupted.Rig.Sensor,
                configurations.Corrupted.Rig.Readout!).Layout;
            AssertAsi676Layout(inputLayout);

            acquisitionFixture = await Fixture.CreateAsync(root, configurations.Corrupted).ConfigureAwait(false);
            var publicationStart = ResourceSnapshot.Capture();
            var publicationTimer = Stopwatch.StartNew();
            var job = await acquisitionFixture.Coordinator.AcquireAsync(
                CreateRequest("C208-ASI676", model), CancellationToken.None).ConfigureAwait(false);
            publicationTimer.Stop();
            var publicationEnd = ResourceSnapshot.Capture();
            Assert.AreEqual(CalibrationAcquisitionStates.Published, job.State);
            Assert.AreEqual(modelIdentity, job.Plan.SourceModelIdentitySha256);
            Assert.AreEqual(model, job.Plan.SourceModel);
            Assert.AreEqual(FixtureUtc, job.Plan.EffectiveFromUtc);
            Assert.AreEqual(Gain, job.Plan.Gain);
            Assert.AreEqual(Offset, job.Plan.Offset);
            Assert.AreEqual(TemperatureC, job.Plan.TemperatureC);
            Assert.AreEqual(LightExposure, job.Plan.ApplicableLightExposure);
            Assert.AreEqual(inputLayout, job.Plan.InputLayout);
            AssertAsi676Layout(job.Plan.InputLayout);

            var published = (await acquisitionFixture.Store.GetBundlesAsync(2, CancellationToken.None)
                .ConfigureAwait(false)).Single();
            AssertPublishedBundle(published, inputLayout);
            Assert.AreEqual(modelIdentity, published.Bundle.AcquisitionModelIdentitySha256);
            var activated = await acquisitionFixture.Store.ActivateAsync(
                published.Bundle.BundleId,
                "activate-C208-ASI676",
                0,
                "issue-208-evidence",
                "canonical hosted ASI676 trial",
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, activated.Version);
            Assert.AreEqual(published.BundleIdentitySha256, activated.ActiveBundle?.BundleIdentitySha256);

            var profileBytes = await File.ReadAllBytesAsync(
                ResolveRelativePath(root, published.Bundle.ProfileRelativePath)).ConfigureAwait(false);
            Assert.AreEqual(published.Bundle.ProfileIdentitySha256, PayloadChecksum.ComputeSha256(profileBytes));
            var hashesBeforeRestart = HashCalibrationEvidence(root);
            var io = MeasureCalibrationIo(root);

            var warmup = CreateVirtualSkyModule();
            try
            {
                await warmup.InitializeAsync(configurations.Clean, CancellationToken.None).ConfigureAwait(false);
                var warmupResult = await CaptureHostedAsync(
                    warmup, FixtureUtc.Subtract(Asi676Cadence)).ConfigureAwait(false);
                Assert.IsNotNull(warmupResult.Frame);
                AssertAsi676Layout(warmupResult.Frame.Layout!);
            }
            finally
            {
                await warmup.DisposeAsync().ConfigureAwait(false);
            }

            cleanModule = CreateVirtualSkyModule();
            corruptedModule = CreateVirtualSkyModule();
            await cleanModule.InitializeAsync(configurations.Clean, CancellationToken.None).ConfigureAwait(false);
            await corruptedModule.InitializeAsync(configurations.Corrupted, CancellationToken.None).ConfigureAwait(false);
            var inputLoader = CreateLibraryInputLoader(root);
            var hostedStart = ResourceSnapshot.Capture();
            var lights = new List<Asi676LightEvidence>(lightCount);
            RestartEvidence? restart = null;
            for (var index = 0; index < lightCount; index++)
            {
                if (index == 2)
                {
                    var restartStart = ResourceSnapshot.Capture();
                    var restartTimer = Stopwatch.StartNew();
                    await cleanModule.DisposeAsync().ConfigureAwait(false);
                    await corruptedModule.DisposeAsync().ConfigureAwait(false);
                    cleanModule = null;
                    corruptedModule = null;
                    acquisitionFixture!.Dispose();
                    acquisitionFixture = null;
                    restartedStore = await Fixture.CreateStoreAsync(root).ConfigureAwait(false);
                    var restoredState = await restartedStore.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
                    var restoredBundles = await restartedStore.GetBundlesAsync(2, CancellationToken.None).ConfigureAwait(false);
                    cleanModule = CreateVirtualSkyModule();
                    corruptedModule = CreateVirtualSkyModule();
                    await cleanModule.InitializeAsync(configurations.Clean, CancellationToken.None).ConfigureAwait(false);
                    await corruptedModule.InitializeAsync(configurations.Corrupted, CancellationToken.None)
                        .ConfigureAwait(false);
                    restartTimer.Stop();
                    var hashesAfterRestart = HashCalibrationEvidence(root);
                    Assert.AreEqual(1L, restoredState.Version);
                    Assert.AreEqual(published.Bundle.BundleId, restoredState.ActiveBundle?.Bundle.BundleId);
                    Assert.AreEqual(published.BundleIdentitySha256, restoredState.ActiveBundle?.BundleIdentitySha256);
                    Assert.HasCount(1, restoredBundles);
                    Assert.AreEqual(published.BundleIdentitySha256, restoredBundles[0].BundleIdentitySha256);
                    AssertHashDictionariesEqual(hashesBeforeRestart, hashesAfterRestart);
                    restart = new RestartEvidence(
                        restartTimer.Elapsed.TotalMilliseconds,
                        ResourceDelta.Between(restartStart, ResourceSnapshot.Capture()),
                        restoredState.Version,
                        restoredState.ActiveBundle!.Bundle.BundleId,
                        hashesAfterRestart.Count,
                        hashesAfterRestart);
                }

                var store = acquisitionFixture?.Store ?? restartedStore
                    ?? throw new AssertFailedException("The ASI676 calibration library is unavailable.");
                lights.Add(await ProcessAsi676LightAsync(
                    index,
                    configurations.Corrupted,
                    cleanModule!,
                    corruptedModule!,
                    store,
                    inputLoader,
                    published,
                    modelIdentity).ConfigureAwait(false));
            }
            var hostedEnd = ResourceSnapshot.Capture();
            Assert.IsNotNull(restart);

            var finalStore = acquisitionFixture?.Store ?? restartedStore!;
            var finalState = await finalStore.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, finalState.Version);
            Assert.AreEqual(published.BundleIdentitySha256, finalState.ActiveBundle?.BundleIdentitySha256);
            AssertHashDictionariesEqual(hashesBeforeRestart, HashCalibrationEvidence(root));
            var durations = lights.Select(static light => light.TotalMilliseconds).Order().ToArray();
            return new Asi676Evidence(
                "C208-ASI676",
                "virtual-asi676mc.full.json",
                FixtureUtc,
                LightExposure,
                Asi676Cadence,
                Gain,
                Offset,
                TemperatureC,
                model.Seed,
                modelIdentity,
                inputLayout.Width,
                inputLayout.Height,
                inputLayout.PixelFormat.ToString(),
                inputLayout.SampleDepthBits,
                inputLayout.ContainerDepthBits,
                inputLayout.StoredCodeTransform!.Value.ToString(),
                inputLayout.LevelCodeSpace!.Value.ToString(),
                inputLayout.CfaPattern.ToString(),
                inputLayout.Readout!.CfaOriginX,
                inputLayout.Readout.CfaOriginY,
                1,
                lightCount,
                2,
                durations[durations.Length / 2],
                durations[^1],
                ResourceDelta.Between(hostedStart, hostedEnd),
                new PublicationEvidence(
                    publicationTimer.Elapsed.TotalMilliseconds,
                    ResourceDelta.Between(publicationStart, publicationEnd),
                    job.Plan.JobId,
                    job.PlanIdentitySha256,
                    published.Bundle.BundleId,
                    published.BundleIdentitySha256,
                    published.Bundle.ProfileIdentitySha256,
                    published.Bundle.AcquisitionModelIdentitySha256,
                    published.Bundle.Artifacts.Count,
                    published.Bundle.Artifacts.Count(static artifact =>
                        artifact.Role == CalibrationLibraryArtifactRoles.Source),
                    published.Bundle.Artifacts.Count(static artifact =>
                        artifact.Role == CalibrationLibraryArtifactRoles.Master),
                    published.Bundle.Artifacts.Select(static artifact => new ArtifactChecksum(
                        artifact.Role,
                        artifact.Kind,
                        artifact.SourceIndex,
                        artifact.PayloadSha256,
                        artifact.ManifestRelativePath)).ToArray(),
                    io),
                restart!,
                lights,
                0,
                0);
        }
        finally
        {
            if (cleanModule is not null)
            {
                await cleanModule.DisposeAsync().ConfigureAwait(false);
            }
            if (corruptedModule is not null)
            {
                await corruptedModule.DisposeAsync().ConfigureAwait(false);
            }
            acquisitionFixture?.Dispose();
            restartedStore?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<Asi676LightEvidence> ProcessAsi676LightAsync(
        int index,
        CameraModuleConfig configuration,
        VirtualSkyCameraModule cleanModule,
        VirtualSkyCameraModule corruptedModule,
        SqliteCalibrationLibraryStore store,
        CalibrationLibraryProcessingInputLoader inputLoader,
        CalibrationLibraryBundleSnapshot published,
        string modelIdentity)
    {
        var timestamp = FixtureUtc.AddTicks(Asi676Cadence.Ticks * index);
        var resourcesStart = ResourceSnapshot.Capture();
        var timer = Stopwatch.StartNew();
        var cleanResult = await CaptureHostedAsync(cleanModule, timestamp).ConfigureAwait(false);
        var corruptedResult = await CaptureHostedAsync(corruptedModule, timestamp).ConfigureAwait(false);
        var clean = cleanResult.Frame ?? throw new AssertFailedException("The clean ASI676 twin produced no frame.");
        var corrupted = corruptedResult.Frame ?? throw new AssertFailedException("The corrupted ASI676 capture produced no frame.");
        AssertAsi676Layout(clean.Layout!);
        Assert.AreEqual(clean.Layout, corrupted.Layout);
        Assert.AreEqual(modelIdentity, corrupted.Metadata.Extra!["virtualCalibrationModelSha256"]);
        Assert.AreEqual(VirtualCalibrationSourceModelV1.CurrentSchemaVersion,
            corrupted.Metadata.Extra["virtualCalibrationSchema"]);
        Assert.AreEqual(VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion,
            corrupted.Metadata.Extra["virtualCalibrationAlgorithm"]);
        Assert.AreEqual(LightExposure, corrupted.Metadata.Exposure);
        Assert.AreEqual(Gain, corrupted.Metadata.Gain);
        Assert.AreEqual(Offset, corrupted.Metadata.Offset);
        Assert.AreEqual(TemperatureC, corrupted.Metadata.TemperatureC);
        Assert.IsTrue(MaximumSample(corrupted.PixelData.Span) <= 4095);

        var rawSha256 = PayloadChecksum.ComputeSha256(corrupted.PixelData.Span);
        var captureId = StableGuid("C208-ASI676", "capture", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var artifactId = StableGuid("C208-ASI676", "artifact", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var request = new CaptureRequest(
            timestamp,
            Asi676Cadence,
            CaptureMode.Still,
            new CaptureSetpoint(LightExposure, Gain, null, null));
        var submission = new CaptureLoopSubmission(
            request,
            corruptedResult,
            timestamp,
            Asi676Cadence,
            corruptedResult.ProcessingLatency);
        var descriptor = RawCaptureDescriptorFactory.Create(
            configuration,
            submission,
            new RawCaptureIdentity(configuration.AgentId!, index + 1, captureId, artifactId),
            rawSha256,
            timestamp.Add(LightExposure));
        Assert.IsTrue(descriptor.Validate().IsValid, descriptor.Validate().ReasonCode);
        Assert.AreEqual("virtual-calibration-source-model", descriptor.Profiles.Calibration.Name);
        Assert.AreEqual(VirtualCalibrationSourceModelV1.CurrentSchemaVersion, descriptor.Profiles.Calibration.Version);
        Assert.AreEqual(modelIdentity, descriptor.Profiles.Calibration.Sha256);
        Assert.AreEqual(Offset, descriptor.Controls.EffectiveOffset);
        Assert.AreEqual(TemperatureC, descriptor.Controls.EffectiveTemperatureC);
        Assert.AreEqual(LightExposure, descriptor.Controls.EffectiveExposure);
        AssertAsi676Layout(descriptor.Layout);
        var applicability = published.Bundle.Applicability;
        Assert.AreEqual(applicability.AgentId, descriptor.Capture.AgentId, "Agent identity mismatch.");
        Assert.AreEqual(applicability.RigId, descriptor.Capture.RigId, "Rig identity mismatch.");
        Assert.AreEqual(applicability.RigProfileSha256, descriptor.Profiles.Rig.Sha256,
            "Rig profile identity mismatch.");
        Assert.AreEqual(applicability.SensorProfileSha256, descriptor.Profiles.Sensor.Sha256,
            "Sensor profile identity mismatch.");
        Assert.AreEqual(published.Bundle.AcquisitionModelIdentitySha256, descriptor.Profiles.Calibration.Sha256,
            "Calibration corruption model identity mismatch.");

        var selection = await store.SelectAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(selection.IsSelected, selection.ReasonCode);
        Assert.AreEqual(1L, selection.StateVersion);
        Assert.AreEqual(published.BundleIdentitySha256, selection.Bundle?.BundleIdentitySha256);
        var inputs = await inputLoader.LoadAsync(selection.Bundle!, CancellationToken.None).ConfigureAwait(false);
        var confirmed = await store.SelectAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(confirmed.IsSelected, confirmed.ReasonCode);
        Assert.AreEqual(selection.StateVersion, confirmed.StateVersion);
        Assert.AreEqual(selection.Bundle!.BundleIdentitySha256, confirmed.Bundle!.BundleIdentitySha256);

        var rawArtifact = new FrameArtifact(
            artifactId,
            FrameArtifactRole.Raw,
            corrupted,
            recipeVersion: descriptor.Artifact.Recipe.OptionsSha256);
        var processingLight = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            configuration,
            rawArtifact,
            "source",
            corruptedResult.AcquisitionTiming,
            descriptor);
        var processingInputs = new List<ProcessingArtifact> { processingLight };
        processingInputs.AddRange(CalibrationReferenceKinds.All.Select(kind => inputs.References[kind]));
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ReferenceCalibration,
            JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
            ProcessingInputSelector.Raw("source"),
            processingInputs,
            "asi676-hosted-corrected",
            AuxiliaryInputs: inputs.AuxiliaryInputs,
            InputArtifactId: artifactId)).ConfigureAwait(false);
        var product = AssertProduced(outcome);
        var expectedLineage = new[]
        {
            artifactId,
            inputs.References[CalibrationReferenceKinds.Bias].ArtifactId,
            inputs.References[CalibrationReferenceKinds.Dark].ArtifactId,
            inputs.References[CalibrationReferenceKinds.Flat].ArtifactId,
            inputs.References[CalibrationReferenceKinds.Defect].ArtifactId
        };
        CollectionAssert.AreEqual(expectedLineage, product.SourceArtifactIds.ToArray());
        Assert.AreEqual(rawSha256, PayloadChecksum.ComputeSha256(corrupted.PixelData.Span),
            "Hosted correction mutated the native ASI676 raw frame.");

        var normalizedClean = CalibrationMasterBuilder.Normalize(
            new CalibrationSourceFrame(clean.Layout!, clean.PixelData));
        var residuals = CalculateAsi676Residuals(
            clean,
            corrupted,
            normalizedClean.PixelData.Span,
            product.Payload.Span,
            inputs.References[CalibrationReferenceKinds.Defect].Payload.Span);
        Assert.IsLessThanOrEqualTo(2d, residuals.CorrectedMeanAbsoluteErrorNativeAdu);
        Assert.IsLessThan(residuals.RawMeanAbsoluteErrorNativeAdu,
            residuals.CorrectedMeanAbsoluteErrorNativeAdu);
        Assert.IsLessThanOrEqualTo(2d, residuals.CorrectedRepairableDefectMeanAbsoluteErrorNativeAdu);
        foreach (var region in residuals.SpatialRegions)
        {
            Assert.IsLessThan(region.RawMeanAbsoluteErrorNativeAdu,
                region.CorrectedMeanAbsoluteErrorNativeAdu,
                $"The declared ASI676 spatial residual did not strictly improve for {region.Name}.");
        }
        timer.Stop();
        return new Asi676LightEvidence(
            index + 1,
            timestamp,
            timer.Elapsed.TotalMilliseconds,
            ResourceDelta.Between(resourcesStart, ResourceSnapshot.Capture()),
            PayloadChecksum.ComputeSha256(clean.PixelData.Span),
            rawSha256,
            product.ChecksumSha256,
            descriptor.Artifact.ArtifactId,
            descriptor.Profiles.Calibration.Sha256,
            selection.Bundle!.BundleIdentitySha256,
            selection.StateVersion,
            MaximumSample(corrupted.PixelData.Span),
            residuals,
            product.SourceArtifactIds);
    }

    private static async Task<Asi676Configurations> LoadAsi676ConfigurationsAsync(
        VirtualCalibrationSourceModelV1 model)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "virtual-asi676mc.full.json"),
            AgentId = "issue-208-asi676",
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        var loaded = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var baseOptions = JsonNode.Parse(loaded.ModuleOptions!.Value.GetRawText())?.AsObject()
            ?? throw new AssertFailedException("The ASI676 VirtualSky options were not readable.");
        baseOptions["fixedSceneUtc"] = JsonValue.Create(FixtureUtc);
        baseOptions.Remove("virtualCalibration");
        var cleanOptions = baseOptions.DeepClone().AsObject();
        var corruptedOptions = baseOptions.DeepClone().AsObject();
        var calibration = new VirtualCalibrationLightOptions
        {
            SourceModel = model,
            BiasExposure = BiasExposure,
            DarkExposure = DarkExposure,
            FlatExposure = FlatExposure,
            DefectExposure = DefectExposure,
            Offset = Offset,
            TemperatureC = TemperatureC
        };
        corruptedOptions["virtualCalibration"] = JsonSerializer.SerializeToNode(calibration, EvidenceJsonOptions);
        var pipeline = loaded.Rig.Pipeline with
        {
            CaptureInterval = Asi676Cadence,
            DayExposure = LightExposure,
            NightExposure = LightExposure,
            DayGain = Gain,
            NightGain = Gain
        };
        var clean = loaded with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky", JsonSerializer.SerializeToElement(cleanOptions, EvidenceJsonOptions)),
            Rig = loaded.Rig with { Pipeline = pipeline }
        };
        var corrupted = clean with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky", JsonSerializer.SerializeToElement(corruptedOptions, EvidenceJsonOptions))
        };
        FileCameraAgentConfigurationLoader.ValidateConfig(clean);
        FileCameraAgentConfigurationLoader.ValidateConfig(corrupted);
        var configured = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(
            corrupted.ModuleOptions!.Value.GetRawText(), EvidenceJsonOptions)
            ?? throw new AssertFailedException("The configured ASI676 VirtualSky options were not readable.");
        Assert.IsNotNull(configured.VirtualCalibration);
        Assert.AreEqual(model, configured.VirtualCalibration.SourceModel);
        Assert.AreEqual(
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(model),
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(configured.VirtualCalibration.SourceModel));
        Assert.AreEqual(FixtureUtc, configured.FixedSceneUtc);
        Assert.AreEqual(Asi676Cadence, corrupted.Rig.Pipeline.CaptureInterval);
        Assert.AreEqual(LightExposure, corrupted.Rig.Pipeline.NightExposure);
        Assert.AreEqual(Gain, corrupted.Rig.Pipeline.NightGain);
        return new Asi676Configurations(clean, corrupted);
    }

    private static VirtualSkyCameraModule CreateVirtualSkyModule()
        => new(
            new FixedTimeProvider(FixtureUtc),
            new InMemoryCelestialCatalog([]),
            new ProjectedSceneStore());

    private static Task<CaptureResult> CaptureHostedAsync(
        VirtualSkyCameraModule module,
        DateTimeOffset timestamp)
        => module.CaptureAsync(
            new CaptureRequest(
                timestamp,
                Asi676Cadence,
                CaptureMode.Still,
                new CaptureSetpoint(LightExposure, Gain, null, null)),
            CancellationToken.None);

    private static CalibrationLibraryProcessingInputLoader CreateLibraryInputLoader(string root)
    {
        var options = CreateOptions(root);
        return new CalibrationLibraryProcessingInputLoader(
            options, new CameraAgentClearReferenceLoader(options));
    }

    private static void AssertAsi676Layout(FrameLayoutDescriptor layout)
    {
        Assert.AreEqual(3552, layout.Width);
        Assert.AreEqual(3552, layout.Height);
        Assert.AreEqual(7104, layout.StrideBytes);
        Assert.AreEqual(checked(3552L * 3552 * 2), layout.ByteLength);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, layout.PixelFormat);
        Assert.AreEqual(12, layout.SampleDepthBits);
        Assert.AreEqual(16, layout.ContainerDepthBits);
        Assert.AreEqual(FrameByteOrder.LittleEndian, layout.ByteOrder);
        Assert.AreEqual(FrameSamplePacking.ByteAligned, layout.Packing);
        Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.NativeSample, layout.LevelCodeSpace);
        Assert.AreEqual(64d, layout.BlackLevel);
        Assert.AreEqual(4095d, layout.WhiteLevel);
        Assert.AreEqual(ColorFilterArrayPattern.Rggb, layout.CfaPattern);
        Assert.IsNotNull(layout.Readout);
        Assert.AreEqual(3552, layout.Readout.NativeWidth);
        Assert.AreEqual(3552, layout.Readout.NativeHeight);
        Assert.AreEqual(0, layout.Readout.RoiX);
        Assert.AreEqual(0, layout.Readout.RoiY);
        Assert.AreEqual(3552, layout.Readout.RoiWidth);
        Assert.AreEqual(3552, layout.Readout.RoiHeight);
        Assert.AreEqual(1, layout.Readout.BinX);
        Assert.AreEqual(1, layout.Readout.BinY);
        Assert.AreEqual(FrameBinningAlgorithm.IdentityV1, layout.Readout.BinningAlgorithm);
        Assert.AreEqual(0, layout.Readout.CfaOriginX);
        Assert.AreEqual(0, layout.Readout.CfaOriginY);
    }

    private static Asi676ResidualEvidence CalculateAsi676Residuals(
        CameraFrame clean,
        CameraFrame corrupted,
        ReadOnlySpan<byte> normalizedClean,
        ReadOnlySpan<byte> corrected,
        ReadOnlySpan<byte> defectMask)
    {
        string[] regionNames = ["center", "outer", "north-west", "north-east", "south-west", "south-east"];
        var rawRegionTotals = new double[regionNames.Length];
        var correctedRegionTotals = new double[regionNames.Length];
        var regionCounts = new long[regionNames.Length];
        double rawTotal = 0;
        double correctedTotal = 0;
        double rawDefectTotal = 0;
        double correctedDefectTotal = 0;
        var repairableDefectCount = 0;
        const double nativeRange = 4095 - 64;
        for (var y = 0; y < clean.Height; y++)
        {
            for (var x = 0; x < clean.Width; x++)
            {
                var nativeOffset = checked(y * clean.Layout!.StrideBytes + x * 2);
                var packedOffset = checked((y * clean.Width + x) * 2);
                var rawDifference = Math.Abs(
                    ReadSample(corrupted.PixelData.Span, nativeOffset) -
                    ReadSample(clean.PixelData.Span, nativeOffset));
                var correctedDifference = Math.Abs(
                    ReadSample(corrected, packedOffset) -
                    ReadSample(normalizedClean, packedOffset)) * nativeRange / ushort.MaxValue;
                rawTotal += rawDifference;
                correctedTotal += correctedDifference;

                var center = x >= clean.Width / 4 && x < clean.Width * 3 / 4 &&
                    y >= clean.Height / 4 && y < clean.Height * 3 / 4;
                AddRegion(center ? 0 : 1);
                AddRegion(2 + (y >= clean.Height / 2 ? 2 : 0) + (x >= clean.Width / 2 ? 1 : 0));
                if (ReadSample(defectMask, packedOffset) != 0 &&
                    HasSameLaneRepairNeighbor(defectMask, clean.Width, clean.Height, x, y))
                {
                    rawDefectTotal += rawDifference;
                    correctedDefectTotal += correctedDifference;
                    repairableDefectCount++;
                }

                void AddRegion(int region)
                {
                    rawRegionTotals[region] += rawDifference;
                    correctedRegionTotals[region] += correctedDifference;
                    regionCounts[region]++;
                }
            }
        }

        var sampleCount = checked((long)clean.Width * clean.Height);
        Assert.IsGreaterThan(0, repairableDefectCount);
        var regions = regionNames.Select((name, index) => new SpatialResidualEvidence(
            name,
            regionCounts[index],
            rawRegionTotals[index] / regionCounts[index],
            correctedRegionTotals[index] / regionCounts[index])).ToArray();
        return new Asi676ResidualEvidence(
            sampleCount,
            rawTotal / sampleCount,
            correctedTotal / sampleCount,
            repairableDefectCount,
            rawDefectTotal / repairableDefectCount,
            correctedDefectTotal / repairableDefectCount,
            regions);
    }

    private static bool HasSameLaneRepairNeighbor(
        ReadOnlySpan<byte> defectMask,
        int width,
        int height,
        int x,
        int y)
    {
        ReadOnlySpan<(int X, int Y)> directions = [(2, 0), (-2, 0), (0, 2), (0, -2)];
        foreach (var direction in directions)
        {
            var neighborX = x + direction.X;
            var neighborY = y + direction.Y;
            if (neighborX >= 0 && neighborX < width && neighborY >= 0 && neighborY < height &&
                ReadSample(defectMask, checked((neighborY * width + neighborX) * 2)) == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static ushort MaximumSample(ReadOnlySpan<byte> pixels)
    {
        ushort maximum = 0;
        for (var offset = 0; offset < pixels.Length; offset += 2)
        {
            maximum = Math.Max(maximum, ReadSample(pixels, offset));
        }
        return maximum;
    }

    private static void AssertHashDictionariesEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var pair in expected)
        {
            Assert.AreEqual(pair.Value, actual[pair.Key], pair.Key);
        }
    }

    private static async Task<WorkloadEvidence> RunWorkloadAsync(Workload workload)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-issue-208-{workload.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = CreateConfiguration(workload);
            var inputLayout = SensorReadoutResolver.Resolve(
                configuration.Rig.Sensor, configuration.Rig.Readout!).Layout;
            Assert.AreEqual(workload.Width, inputLayout.Width);
            Assert.AreEqual(workload.Height, inputLayout.Height);
            Assert.AreEqual(workload.PixelFormat, inputLayout.PixelFormat);
            Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, inputLayout.StoredCodeTransform);
            Assert.AreEqual(FrameLevelCodeSpace.NativeSample, inputLayout.LevelCodeSpace);

            var model = new VirtualCalibrationSourceModelV1 { Seed = 208 };
            var benchmarkSources = GenerateSources(inputLayout, model);
            var masterMeasurement = MeasureMasters(benchmarkSources);

            CalibrationAcquisitionJobSnapshot job;
            CalibrationLibraryBundleSnapshot published;
            CalibrationLibraryStateSnapshot activated;
            ResourceSnapshot publicationStart;
            ResourceSnapshot publicationEnd;
            double publicationMilliseconds;
            using (var fixture = await Fixture.CreateAsync(root, configuration).ConfigureAwait(false))
            {
                publicationStart = ResourceSnapshot.Capture();
                var publicationTimer = Stopwatch.StartNew();
                job = await fixture.Coordinator.AcquireAsync(
                    CreateRequest(workload.Id, model), CancellationToken.None).ConfigureAwait(false);
                publicationTimer.Stop();
                publicationMilliseconds = publicationTimer.Elapsed.TotalMilliseconds;
                publicationEnd = ResourceSnapshot.Capture();

                Assert.AreEqual(CalibrationAcquisitionStates.Published, job.State);
                published = (await fixture.Store.GetBundlesAsync(2, CancellationToken.None).ConfigureAwait(false)).Single();
                AssertPublishedBundle(published, inputLayout);
                activated = await fixture.Store.ActivateAsync(
                    published.Bundle.BundleId,
                    $"activate-{workload.Id}",
                    0,
                    "issue-208-evidence",
                    "cold publication trial",
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1L, activated.Version);
            }

            var hashesBeforeRestart = HashCalibrationEvidence(root);
            var calibrationIo = MeasureCalibrationIo(root);
            var restartResourcesStart = ResourceSnapshot.Capture();
            var restartTimer = Stopwatch.StartNew();
            using var restarted = await Fixture.CreateStoreAsync(root).ConfigureAwait(false);
            var restoredState = await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            var restoredBundles = await restarted.GetBundlesAsync(2, CancellationToken.None).ConfigureAwait(false);
            restartTimer.Stop();
            var restartResourcesEnd = ResourceSnapshot.Capture();
            var hashesAfterRestart = HashCalibrationEvidence(root);

            Assert.AreEqual(1L, restoredState.Version);
            Assert.AreEqual(published.Bundle.BundleId, restoredState.ActiveBundle?.Bundle.BundleId);
            Assert.HasCount(1, restoredBundles);
            Assert.AreEqual(published.BundleIdentitySha256, restoredBundles[0].BundleIdentitySha256);
            CollectionAssert.AreEquivalent(hashesBeforeRestart.Keys.ToArray(), hashesAfterRestart.Keys.ToArray());
            foreach (var pair in hashesBeforeRestart)
            {
                Assert.AreEqual(pair.Value, hashesAfterRestart[pair.Key], pair.Key);
            }

            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            var loader = new CalibrationLibraryProcessingInputLoader(
                options, new CameraAgentClearReferenceLoader(options));
            var libraryInputs = await loader.LoadAsync(restoredBundles.Single(), CancellationToken.None)
                .ConfigureAwait(false);
            var correctionFixture = CreateCorrectionFixture(
                workload, inputLayout, root, restoredBundles.Single(), libraryInputs);
            var correctionMeasurement = await MeasureCorrectionAsync(correctionFixture).ConfigureAwait(false);

            Assert.AreEqual(
                correctionFixture.RawChecksumSha256,
                PayloadChecksum.ComputeSha256(correctionFixture.RawPayload.Span),
                "The correction recipe mutated native raw evidence.");
            Assert.IsLessThan(double.MaxValue,
                correctionMeasurement.RawMeanAbsoluteErrorAdu);
            Assert.IsLessThan(correctionMeasurement.RawMeanAbsoluteErrorAdu,
                correctionMeasurement.CorrectedMeanAbsoluteErrorAdu);
            Assert.IsLessThanOrEqualTo(1d, correctionMeasurement.CorrectedMeanAbsoluteErrorAdu);
            Assert.IsLessThanOrEqualTo(1d, correctionMeasurement.CorrectedDefectMeanAbsoluteErrorAdu);

            var profilePath = ResolveRelativePath(root, published.Bundle.ProfileRelativePath);
            var profileBytes = await File.ReadAllBytesAsync(profilePath).ConfigureAwait(false);
            Assert.AreEqual(published.Bundle.ProfileIdentitySha256, PayloadChecksum.ComputeSha256(profileBytes));
            Assert.AreEqual(
                published.Bundle.AcquisitionModelIdentitySha256,
                VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(model));

            return new WorkloadEvidence(
                workload.Id,
                workload.Width,
                workload.Height,
                workload.PixelFormat.ToString(),
                inputLayout.ByteLength,
                inputLayout.SampleDepthBits,
                inputLayout.ContainerDepthBits,
                inputLayout.StoredCodeTransform!.Value.ToString(),
                inputLayout.LevelCodeSpace!.Value.ToString(),
                CalibrationMasterBuilder.RequiredSourceCount,
                WarmupCount,
                MeasurementCount,
                masterMeasurement,
                correctionMeasurement,
                new PublicationEvidence(
                    publicationMilliseconds,
                    ResourceDelta.Between(publicationStart, publicationEnd),
                    job.Plan.JobId,
                    job.PlanIdentitySha256,
                    published.Bundle.BundleId,
                    published.BundleIdentitySha256,
                    published.Bundle.ProfileIdentitySha256,
                    published.Bundle.AcquisitionModelIdentitySha256,
                    published.Bundle.Artifacts.Count,
                    published.Bundle.Artifacts.Count(static artifact =>
                        artifact.Role == CalibrationLibraryArtifactRoles.Source),
                    published.Bundle.Artifacts.Count(static artifact =>
                        artifact.Role == CalibrationLibraryArtifactRoles.Master),
                    published.Bundle.Artifacts
                        .OrderBy(static artifact => artifact.Role, StringComparer.Ordinal)
                        .ThenBy(static artifact => artifact.Kind, StringComparer.Ordinal)
                        .ThenBy(static artifact => artifact.SourceIndex)
                        .Select(static artifact => new ArtifactChecksum(
                            artifact.Role,
                            artifact.Kind,
                            artifact.SourceIndex,
                            artifact.PayloadSha256,
                            artifact.ManifestRelativePath))
                        .ToArray(),
                    calibrationIo),
                new RestartEvidence(
                    restartTimer.Elapsed.TotalMilliseconds,
                    ResourceDelta.Between(restartResourcesStart, restartResourcesEnd),
                    restoredState.Version,
                    restoredState.ActiveBundle?.Bundle.BundleId!,
                    hashesAfterRestart.Count,
                    hashesAfterRestart));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, CalibrationSourceFrame[]> GenerateSources(
        FrameLayoutDescriptor layout,
        VirtualCalibrationSourceModelV1 model)
    {
        var result = new Dictionary<string, CalibrationSourceFrame[]>(StringComparer.Ordinal);
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var frames = new CalibrationSourceFrame[CalibrationMasterBuilder.RequiredSourceCount];
            for (var sourceIndex = 0; sourceIndex < frames.Length; sourceIndex++)
            {
                frames[sourceIndex] = VirtualCalibrationSourceGenerator.Generate(
                    ToSourceKind(kind),
                    sourceIndex,
                    layout,
                    ExposureFor(kind),
                    Gain,
                    Offset,
                    TemperatureC,
                    model);
            }
            Assert.HasCount(3, frames);
            result.Add(kind, frames);
        }
        return result;
    }

    private static OperationMeasurement MeasureMasters(
        IReadOnlyDictionary<string, CalibrationSourceFrame[]> sources)
    {
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = BuildAllMasters(sources);
        }

        var resourcesStart = ResourceSnapshot.Capture();
        var durations = new double[MeasurementCount];
        Dictionary<string, string>? expectedChecksums = null;
        for (var index = 0; index < MeasurementCount; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var masters = BuildAllMasters(sources);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var checksums = masters.ToDictionary(
                static pair => pair.Key,
                static pair => PayloadChecksum.ComputeSha256(pair.Value.PixelData.Span),
                StringComparer.Ordinal);
            expectedChecksums ??= checksums;
            foreach (var pair in expectedChecksums)
            {
                Assert.AreEqual(pair.Value, checksums[pair.Key], pair.Key);
            }
        }
        var resourcesEnd = ResourceSnapshot.Capture();
        return CreateOperationMeasurement(
            durations,
            ResourceDelta.Between(resourcesStart, resourcesEnd),
            expectedChecksums!,
            sources.Values.Sum(static frames => frames.Sum(static frame => frame.PixelData.Length)),
            expectedChecksums!.Count * sources.First().Value[0].Layout.Width *
                (long)sources.First().Value[0].Layout.Height * 2);
    }

    private static Dictionary<string, CalibrationMasterResult> BuildAllMasters(
        IReadOnlyDictionary<string, CalibrationSourceFrame[]> sources)
    {
        var result = new Dictionary<string, CalibrationMasterResult>(StringComparer.Ordinal);
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            result.Add(kind, kind == CalibrationReferenceKinds.Defect
                ? CalibrationMasterBuilder.BuildDefectMask(sources[kind])
                : CalibrationMasterBuilder.BuildMedian(sources[kind]));
        }
        return result;
    }

    private static async Task<CorrectionMeasurement> MeasureCorrectionAsync(CorrectionFixture fixture)
    {
        var executor = new ProcessingRecipeExecutor();
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = AssertProduced(await executor.ExecuteAsync(fixture.Request).ConfigureAwait(false));
        }

        var resourcesStart = ResourceSnapshot.Capture();
        var durations = new double[MeasurementCount];
        string? checksum = null;
        ReadOnlyMemory<byte> corrected = default;
        for (var index = 0; index < MeasurementCount; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var product = AssertProduced(await executor.ExecuteAsync(fixture.Request).ConfigureAwait(false));
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            checksum ??= product.ChecksumSha256;
            Assert.AreEqual(checksum, product.ChecksumSha256);
            Assert.HasCount(5, product.SourceArtifactIds);
            Assert.AreEqual(fixture.LightArtifactId, product.SourceArtifactIds[0]);
            corrected = product.Payload;
        }
        var resourcesEnd = ResourceSnapshot.Capture();
        var rawMae = MeanAbsoluteError(fixture.RawPayload.Span, fixture.KnownTruth.Span, default, maskOnly: false);
        var correctedMae = MeanAbsoluteError(corrected.Span, fixture.KnownTruth.Span, default, maskOnly: false);
        var defectMae = MeanAbsoluteError(
            corrected.Span, fixture.KnownTruth.Span, fixture.DefectMask.Span, maskOnly: true);
        var operation = CreateOperationMeasurement(
            durations,
            ResourceDelta.Between(resourcesStart, resourcesEnd),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["corrected"] = checksum! },
            fixture.Request.Inputs.Sum(static input => input.Payload.Length),
            corrected.Length);
        return new CorrectionMeasurement(
            operation,
            fixture.RawChecksumSha256,
            checksum!,
            rawMae,
            correctedMae,
            defectMae,
            CountDefects(fixture.DefectMask.Span));
    }

    private static CorrectionFixture CreateCorrectionFixture(
        Workload workload,
        FrameLayoutDescriptor inputLayout,
        string root,
        CalibrationLibraryBundleSnapshot bundle,
        CalibrationLibraryProcessingInputs libraryInputs)
    {
        var profile = ReferenceCalibrationProfileJson.Parse(File.ReadAllBytes(
            ResolveRelativePath(root, bundle.Bundle.ProfileRelativePath)))
            ?? throw new AssertFailedException("The published calibration profile was not readable.");
        var bias = libraryInputs.References[CalibrationReferenceKinds.Bias].Payload;
        var dark = libraryInputs.References[CalibrationReferenceKinds.Dark].Payload;
        var flat = libraryInputs.References[CalibrationReferenceKinds.Flat].Payload;
        var defect = libraryInputs.References[CalibrationReferenceKinds.Defect].Payload;
        var truth = CreateConstantFrame(workload.Width, workload.Height, KnownTruthAdu);
        var raw = CreateKnownCorruptedLight(
            truth,
            bias.Span,
            dark.Span,
            flat.Span,
            defect.Span,
            profile.FlatNormalizationAdu,
            workload.Width,
            workload.Height);
        var rawChecksum = PayloadChecksum.ComputeSha256(raw);
        var lightId = StableGuid(workload.Id, "known-light");
        var light = new ProcessingArtifact(
            lightId,
            FrameArtifactRole.Raw,
            "source",
            rawChecksum,
            workload.PixelFormat == CameraPixelFormat.Mono16
                ? "application/x-skymonitor-mono16"
                : "application/x-skymonitor-bayer-rggb16",
            inputLayout,
            raw,
            FixtureUtc.AddMinutes(1),
            LightExposure,
            new ProcessingCompatibilityIdentity(
                $"{workload.Id}-rig",
                "north-up",
                bundle.Bundle.ProfileIdentitySha256,
                "full",
                $"{workload.Id}-sensor",
                "night",
                "issue-208"),
            Conditions: new ProcessingCaptureConditions(Gain, Offset, TemperatureC),
            ObservationStartedUtc: FixtureUtc.AddMinutes(1));
        var inputs = new List<ProcessingArtifact> { light };
        inputs.AddRange(CalibrationReferenceKinds.All.Select(kind => libraryInputs.References[kind]));
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ReferenceCalibration,
            JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
            ProcessingInputSelector.Raw("source"),
            inputs,
            "known-truth-corrected",
            AuxiliaryInputs: libraryInputs.AuxiliaryInputs,
            InputArtifactId: lightId);
        return new CorrectionFixture(request, lightId, raw, truth, defect, rawChecksum);
    }

    private static byte[] CreateKnownCorruptedLight(
        ReadOnlySpan<byte> truth,
        ReadOnlySpan<byte> bias,
        ReadOnlySpan<byte> dark,
        ReadOnlySpan<byte> flat,
        ReadOnlySpan<byte> defect,
        ushort flatNormalization,
        int width,
        int height)
    {
        var output = new byte[checked(width * height * 2)];
        for (var offset = 0; offset < output.Length; offset += 2)
        {
            var biasValue = ReadSample(bias, offset);
            var darkSignal = Math.Max(0, ReadSample(dark, offset) - biasValue);
            var scaledDark = DivideRounded(
                checked((ulong)darkSignal * (ulong)LightExposure.Ticks),
                (ulong)DarkExposure.Ticks);
            var flatDark = DivideRounded(
                checked((ulong)darkSignal * (ulong)FlatExposure.Ticks),
                (ulong)DarkExposure.Ticks);
            var flatSignal = Math.Max(1L, ReadSample(flat, offset) - biasValue - (long)flatDark);
            var lightSignal = DivideRounded(
                checked((ulong)ReadSample(truth, offset) * (ulong)flatSignal),
                flatNormalization);
            var value = ReadSample(defect, offset) == 0
                ? Math.Min(ushort.MaxValue, checked((ulong)biasValue + scaledDark + lightSignal))
                : ushort.MaxValue;
            WriteSample(output, offset, (ushort)value);
        }
        return output;
    }

    private static OperationMeasurement CreateOperationMeasurement(
        double[] durations,
        ResourceDelta resources,
        IReadOnlyDictionary<string, string> checksums,
        long inputBytes,
        long outputBytes)
    {
        Array.Sort(durations);
        var totalSeconds = durations.Sum() / 1000d;
        return new OperationMeasurement(
            WarmupCount,
            MeasurementCount,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            totalSeconds == 0 ? 0 : MeasurementCount / totalSeconds,
            inputBytes,
            outputBytes,
            resources,
            checksums);
    }

    private static ProcessingProduct AssertProduced(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.ContainsSingle(outcome.Products);
        return outcome.Products[0];
    }

    private static void AssertPublishedBundle(
        CalibrationLibraryBundleSnapshot snapshot,
        FrameLayoutDescriptor inputLayout)
    {
        Assert.AreEqual(CalibrationLibraryBundleSources.VirtualAcquisitionV1, snapshot.Bundle.Source);
        Assert.AreEqual(inputLayout, snapshot.Bundle.Applicability.InputLayout);
        Assert.AreEqual(CalibrationMasterBuilder.CreateNormalizedLayout(inputLayout),
            snapshot.Bundle.Applicability.OutputLayout);
        Assert.HasCount(16, snapshot.Bundle.Artifacts);
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var sources = snapshot.Bundle.Artifacts.Where(artifact =>
                artifact.Role == CalibrationLibraryArtifactRoles.Source && artifact.Kind == kind).ToArray();
            var master = snapshot.Bundle.Artifacts.Single(artifact =>
                artifact.Role == CalibrationLibraryArtifactRoles.Master && artifact.Kind == kind);
            Assert.HasCount(CalibrationMasterBuilder.RequiredSourceCount, sources);
            CollectionAssert.AreEqual(
                sources.OrderBy(static source => source.SourceIndex).Select(static source => source.ArtifactId).ToArray(),
                master.OrderedSourceArtifactIds.ToArray());
        }
    }

    private static CameraModuleConfig CreateConfiguration(Workload workload)
    {
        var cfa = workload.PixelFormat == CameraPixelFormat.BayerRggb16
            ? ColorFilterArrayPattern.Rggb
            : ColorFilterArrayPattern.None;
        var sensor = new SensorProfile(
            $"Issue208{workload.Id}",
            workload.Width,
            workload.Height,
            2.4,
            workload.PixelFormat == CameraPixelFormat.BayerRggb16 ? SensorColorMode.Color : SensorColorMode.Mono,
            workload.PixelFormat,
            workload.PixelFormat == CameraPixelFormat.BayerRggb16
                ? SensorResponseMode.BayerRaw
                : SensorResponseMode.Monochrome,
            checked(workload.Width * 2),
            SampleByteOrder.LittleEndian,
            $"{workload.Id}-sensor-v1");
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, workload.Width, workload.Height),
            1,
            1,
            FrameBinningAlgorithm.IdentityV1,
            workload.PixelFormat,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1,
            FrameLevelCodeSpace.NativeSample,
            0,
            ushort.MaxValue,
            checked(workload.Width * 2),
            SampleByteOrder.LittleEndian,
            cfa,
            workload.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null,
            workload.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                sensor,
                new OpticsProfile("EquidistantFisheye", 2.5, 170, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    LightExposure, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), Gain, Gain),
                ProfileVersion: $"{workload.Id}-rig-v1",
                Readout: readout),
            CapturePipelineConfig.Empty,
            AgentId: $"issue-208-{workload.Id.ToUpperInvariant()}");
    }

    private static VirtualCalibrationAcquisitionRequestV1 CreateRequest(
        string workloadId,
        VirtualCalibrationSourceModelV1 model)
        => new(
            VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
            $"issue-208-{workloadId}-cold-publication",
            Gain,
            Offset,
            TemperatureC,
            BiasExposure,
            DarkExposure,
            FlatExposure,
            DefectExposure,
            LightExposure,
            FixtureUtc,
            FixtureUtc.AddDays(1),
            model,
            "issue-208-evidence",
            "C208 W1/W2 retained evidence");

    private static VirtualCalibrationSourceKind ToSourceKind(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => VirtualCalibrationSourceKind.Bias,
            CalibrationReferenceKinds.Dark => VirtualCalibrationSourceKind.Dark,
            CalibrationReferenceKinds.Flat => VirtualCalibrationSourceKind.Flat,
            CalibrationReferenceKinds.Defect => VirtualCalibrationSourceKind.Defect,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static TimeSpan ExposureFor(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => BiasExposure,
            CalibrationReferenceKinds.Dark => DarkExposure,
            CalibrationReferenceKinds.Flat => FlatExposure,
            CalibrationReferenceKinds.Defect => DefectExposure,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static byte[] CreateConstantFrame(int width, int height, ushort value)
    {
        var result = new byte[checked(width * height * 2)];
        for (var offset = 0; offset < result.Length; offset += 2)
        {
            WriteSample(result, offset, value);
        }
        return result;
    }

    private static double MeanAbsoluteError(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> mask,
        bool maskOnly)
    {
        ulong total = 0;
        long count = 0;
        for (var offset = 0; offset < actual.Length; offset += 2)
        {
            if (maskOnly && ReadSample(mask, offset) == 0)
            {
                continue;
            }
            total += (ulong)Math.Abs(ReadSample(actual, offset) - ReadSample(expected, offset));
            count++;
        }
        Assert.IsGreaterThan(0L, count);
        return total / (double)count;
    }

    private static int CountDefects(ReadOnlySpan<byte> mask)
    {
        var count = 0;
        for (var offset = 0; offset < mask.Length; offset += 2)
        {
            if (ReadSample(mask, offset) != 0)
            {
                count++;
            }
        }
        Assert.IsGreaterThan(0, count);
        return count;
    }

    private static ushort ReadSample(ReadOnlySpan<byte> bytes, int offset)
        => (ushort)(bytes[offset] | bytes[offset + 1] << 8);

    private static void WriteSample(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static ulong DivideRounded(ulong numerator, ulong denominator)
        => (numerator + denominator / 2) / denominator;

    private static Guid StableGuid(params string[] values)
        => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\0', values))).AsSpan(0, 16));

    private static Dictionary<string, string> HashCalibrationEvidence(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "calibration"), "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private static IoEvidence MeasureCalibrationIo(string root)
    {
        var files = Directory.EnumerateFiles(Path.Combine(root, "calibration"), "*", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path)).ToArray();
        var database = new FileInfo(Path.Combine(root, "journal", "raw-ingress.db"));
        var walPath = string.Concat(database.FullName, "-wal");
        return new IoEvidence(
            files.Length,
            files.Sum(static file => file.Length),
            files.Count(static file => file.Extension == ".bin"),
            files.Where(static file => file.Extension == ".bin").Sum(static file => file.Length),
            files.Count(static file => file.Extension == ".json"),
            files.Where(static file => file.Extension == ".json").Sum(static file => file.Length),
            database.Exists ? database.Length : 0,
            File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            0,
            0);
    }

    private static string ResolveRelativePath(string root, string relativePath)
        => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ResolveRepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found for retained issue #208 evidence.");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "working-tree" : sanitized;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CaptureControlTelemetry _telemetry;
        private readonly CaptureAdmissionCoordinator _admission;

        private Fixture(
            SqliteCalibrationLibraryStore store,
            CaptureControlTelemetry telemetry,
            CaptureAdmissionCoordinator admission,
            VirtualCalibrationAcquisitionCoordinator coordinator)
        {
            Store = store;
            _telemetry = telemetry;
            _admission = admission;
            Coordinator = coordinator;
        }

        internal SqliteCalibrationLibraryStore Store { get; }
        internal VirtualCalibrationAcquisitionCoordinator Coordinator { get; }

        internal static async Task<Fixture> CreateAsync(string root, CameraModuleConfig configuration)
        {
            var timeProvider = new FixedTimeProvider(FixtureUtc);
            var options = CreateOptions(root);
            var ingress = new InitializingIngress(root);
            var telemetry = new CaptureControlTelemetry();
            var admission = new CaptureAdmissionCoordinator(ingress, options, timeProvider, telemetry);
            await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var store = new SqliteCalibrationLibraryStore(ingress, options, timeProvider);
            _ = await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(configuration);
            var coordinator = new VirtualCalibrationAcquisitionCoordinator(
                store,
                new CalibrationArtifactPublisher(options, NullCalibrationPublicationFaultInjector.Instance),
                admission,
                accessor,
                timeProvider);
            return new Fixture(store, telemetry, admission, coordinator);
        }

        internal static async Task<SqliteCalibrationLibraryStore> CreateStoreAsync(string root)
        {
            var store = new SqliteCalibrationLibraryStore(
                new InitializingIngress(root), CreateOptions(root), new FixedTimeProvider(FixtureUtc));
            _ = await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return store;
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Store.Dispose();
            _admission.Dispose();
            _telemetry.Dispose();
        }
    }

    private sealed class InitializingIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(
                    Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static IOptions<CameraAgentHostOptions> CreateOptions(string root)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });

    private sealed record Asi676Configurations(
        CameraModuleConfig Clean,
        CameraModuleConfig Corrupted);

    private sealed record Asi676Evidence(
        string Id,
        string SourceConfiguration,
        DateTimeOffset FixedSceneUtc,
        TimeSpan LightExposure,
        TimeSpan Cadence,
        double Gain,
        double Offset,
        double TemperatureC,
        int SourceModelSeed,
        string SourceModelIdentitySha256,
        int Width,
        int Height,
        string PixelFormat,
        int SampleDepthBits,
        int ContainerDepthBits,
        string StoredCodeTransform,
        string LevelCodeSpace,
        string CfaPattern,
        int? CfaOriginX,
        int? CfaOriginY,
        int Warmups,
        int MeasuredLights,
        int RestartAfterLight,
        double MedianMilliseconds,
        double P95Milliseconds,
        ResourceDelta Resources,
        PublicationEvidence Publication,
        RestartEvidence Restart,
        IReadOnlyList<Asi676LightEvidence> Lights,
        long NetworkOperations,
        long CentralServiceOperations);

    private sealed record Asi676LightEvidence(
        int LightNumber,
        DateTimeOffset RequestedStartUtc,
        double TotalMilliseconds,
        ResourceDelta Resources,
        string CleanNativeChecksumSha256,
        string RawChecksumSha256,
        string CorrectedChecksumSha256,
        Guid RawArtifactId,
        string DescriptorCalibrationModelIdentitySha256,
        string BundleIdentitySha256,
        long CalibrationStateVersion,
        ushort MaximumNativeSample,
        Asi676ResidualEvidence Residuals,
        IReadOnlyList<Guid> OrderedLineage);

    private sealed record Asi676ResidualEvidence(
        long SampleCount,
        double RawMeanAbsoluteErrorNativeAdu,
        double CorrectedMeanAbsoluteErrorNativeAdu,
        int RepairableDefectSampleCount,
        double RawRepairableDefectMeanAbsoluteErrorNativeAdu,
        double CorrectedRepairableDefectMeanAbsoluteErrorNativeAdu,
        IReadOnlyList<SpatialResidualEvidence> SpatialRegions);

    private sealed record SpatialResidualEvidence(
        string Name,
        long SampleCount,
        double RawMeanAbsoluteErrorNativeAdu,
        double CorrectedMeanAbsoluteErrorNativeAdu);

    private sealed record Workload(string Id, int Width, int Height, CameraPixelFormat PixelFormat);

    private sealed record CorrectionFixture(
        ProcessingExecutionRequest Request,
        Guid LightArtifactId,
        ReadOnlyMemory<byte> RawPayload,
        ReadOnlyMemory<byte> KnownTruth,
        ReadOnlyMemory<byte> DefectMask,
        string RawChecksumSha256);

    private sealed record WorkloadEvidence(
        string Id,
        int Width,
        int Height,
        string PixelFormat,
        long NativeBytes,
        int SampleDepthBits,
        int ContainerDepthBits,
        string StoredCodeTransform,
        string LevelCodeSpace,
        int SourcesPerKind,
        int Warmups,
        int MeasuredOperations,
        OperationMeasurement MasterBuild,
        CorrectionMeasurement Correction,
        PublicationEvidence Publication,
        RestartEvidence Restart);

    private sealed record OperationMeasurement(
        int Warmups,
        int Operations,
        double MedianMilliseconds,
        double P95Milliseconds,
        double OperationsPerSecond,
        long InputBytesPerOperation,
        long OutputBytesPerOperation,
        ResourceDelta Resources,
        IReadOnlyDictionary<string, string> OutputChecksumsSha256);

    private sealed record CorrectionMeasurement(
        OperationMeasurement Operation,
        string RawChecksumSha256,
        string CorrectedChecksumSha256,
        double RawMeanAbsoluteErrorAdu,
        double CorrectedMeanAbsoluteErrorAdu,
        double CorrectedDefectMeanAbsoluteErrorAdu,
        int DefectSampleCount);

    private sealed record PublicationEvidence(
        double ColdPublicationMilliseconds,
        ResourceDelta Resources,
        string JobId,
        string PlanIdentitySha256,
        string BundleId,
        string BundleIdentitySha256,
        string ProfileIdentitySha256,
        string SourceModelIdentitySha256,
        int ArtifactCount,
        int SourceArtifactCount,
        int MasterArtifactCount,
        IReadOnlyList<ArtifactChecksum> ArtifactChecksums,
        IoEvidence Io);

    private sealed record ArtifactChecksum(
        string Role,
        string Kind,
        int? SourceIndex,
        string PayloadSha256,
        string ManifestRelativePath);

    private sealed record RestartEvidence(
        double ColdRestartMilliseconds,
        ResourceDelta Resources,
        long StateVersion,
        string ActiveBundleId,
        int EvidenceFileCount,
        IReadOnlyDictionary<string, string> EvidenceChecksumsSha256);

    private sealed record IoEvidence(
        int CalibrationFileCount,
        long CalibrationBytes,
        int PayloadFileCount,
        long PayloadBytes,
        int JsonFileCount,
        long JsonBytes,
        long SqliteBytes,
        long WalBytes,
        long NetworkOperations,
        long CentralServiceOperations);

    private sealed record ResourceSnapshot(
        TimeSpan Cpu,
        long AllocatedBytes,
        long WorkingSetBytes,
        long PeakWorkingSetBytes,
        long HeapBytes,
        long FragmentedBytes,
        long LohBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections)
    {
        internal static ResourceSnapshot Capture()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var memory = GC.GetGCMemoryInfo();
            return new ResourceSnapshot(
                process.TotalProcessorTime,
                GC.GetTotalAllocatedBytes(precise: false),
                process.WorkingSet64,
                process.PeakWorkingSet64,
                memory.HeapSizeBytes,
                memory.FragmentedBytes,
                memory.GenerationInfo.Length > 3 ? memory.GenerationInfo[3].SizeAfterBytes : 0,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2));
        }
    }

    private sealed record ResourceDelta(
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetStartBytes,
        long WorkingSetEndBytes,
        long PeakWorkingSetBytes,
        long HeapStartBytes,
        long HeapEndBytes,
        long FragmentedStartBytes,
        long FragmentedEndBytes,
        long LohStartBytes,
        long LohEndBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections)
    {
        internal static ResourceDelta Between(ResourceSnapshot start, ResourceSnapshot end)
            => new(
                (end.Cpu - start.Cpu).TotalMilliseconds,
                end.AllocatedBytes - start.AllocatedBytes,
                start.WorkingSetBytes,
                end.WorkingSetBytes,
                Math.Max(start.PeakWorkingSetBytes, end.PeakWorkingSetBytes),
                start.HeapBytes,
                end.HeapBytes,
                start.FragmentedBytes,
                end.FragmentedBytes,
                start.LohBytes,
                end.LohBytes,
                end.Gen0Collections - start.Gen0Collections,
                end.Gen1Collections - start.Gen1Collections,
                end.Gen2Collections - start.Gen2Collections);
    }
}
