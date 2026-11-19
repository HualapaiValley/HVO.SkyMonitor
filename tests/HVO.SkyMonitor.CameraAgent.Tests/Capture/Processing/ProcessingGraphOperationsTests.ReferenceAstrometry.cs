using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly JsonSerializerOptions ReferenceAstrometryJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceAstrometry_ActivatesInstallerDefaultOrExplicitRealCameraDepth(bool realCamera)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-astrometry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = await LoadAstrometryProfileAsync(realCamera ? "cameraagent.zwo-asi676mc.sample.json" :
                "Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            if (realCamera)
            {
                Assert.AreEqual("ZwoAsi", config.ModuleType);
                config = config with
                {
                    Pipeline = new([new("Storage", "LocalStorage", Options: JsonSerializer.SerializeToElement(
                        new FileStorageCaptureProcessingStepOptions { StorageRoot = root, QueueForUpload = false }),
                        DependsOn: ["$raw"])], CapturePipelineSchemaVersions.ExplicitV2)
                };
            }
            else
            {
                Assert.AreEqual(13, config.Pipeline.Steps.Count);
                Assert.AreEqual(300d, config.ModuleOptions!.Value.GetProperty("magnitudeZeroElectronsPerSecond").GetDouble());
                Assert.AreEqual(TimeSpan.FromSeconds(20), config.Rig.Pipeline.NightExposure);
            }

            var source = CreateReferenceCatalog();
            using var provider = CreateProvider(root, configureServices: services => services.AddSingleton(source));
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var token = CancellationToken.None;
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var registry = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            if (realCamera)
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                    await operations.CreateReferenceAstrometryRevisionAsync(registry.ActiveRevisionId, "missing-depth",
                        null, "invalid", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await operations.CreateReferenceAstrometryRevisionAsync(registry.ActiveRevisionId, "opaque-readout",
                        4.5, "opaque", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);

                // Model a physical module that declares calibrated linear sensor codes. The current ZWO profile
                // deliberately exposes opaque container values and cannot provide this contract.
                config = config with
                {
                    Module = new("CalibratedPhysicalCamera"),
                    Rig = config.Rig with
                    {
                        Readout = config.Rig.Readout! with
                        {
                            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
                            WhiteLevel = 4095
                        },
                        Optics = config.Rig.Optics with { CalibrationVersion = "calibrated-physical-fixture-v1" }
                    }
                };
                registry = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            }

            var (draft, pin) = await operations.CreateReferenceAstrometryRevisionAsync(registry.ActiveRevisionId,
                "configured", realCamera ? 4.5 : null, "create-reference", "test-owner", null, token).ConfigureAwait(false);
            var depth = realCamera ? 4.5 : 3.25;
            Assert.AreEqual(depth, pin.MaximumMagnitude);
            var selected = await pin.ResolveAsync(source, token).ConfigureAwait(false);
            Assert.HasCount(realCamera ? 2 : 1, selected.Stars);

            _ = await operations.ValidateRevisionAsync(draft.RevisionId, "validate-reference", "test-owner", null, token).ConfigureAwait(false);
            _ = await operations.ActivateRevisionAsync(draft.RevisionId, registry.StateVersion,
                "activate-reference", "test-owner", null, token).ConfigureAwait(false);
            var live = await operations.PrepareLiveExecutionAsync(config, Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, live.Seed.Revision.State.RevisionId);
            Assert.AreEqual(config.Module, live.Configuration.Module);
            Assert.AreEqual(config.Rig, live.Configuration.Rig);
            Assert.AreEqual(config.Schedule, live.Configuration.Schedule);
            var steps = live.Configuration.Pipeline.Steps;
            var original = await operations.ReadRevisionSnapshotAsync(registry.ActiveRevisionId, token).ConfigureAwait(false);
            foreach (var node in original.Nodes)
                Assert.AreEqual(node.PlanSha256, live.Seed.Revision.Nodes.Single(value => value.NodeId == node.NodeId).PlanSha256, node.NodeId);
            var prediction = steps.Single(step => step.Type == "AstrometryPrediction");
            Assert.AreEqual(depth, prediction.Options!.Value.GetProperty("maximumMagnitude").GetDouble());
            Assert.AreEqual("$raw", prediction.DependsOn!.Single());
            var solve = steps.Single(step => step.Type == "MeasuredAstrometry");
            var options = solve.Options!.Value.Deserialize<MeasuredAstrometryCaptureOptions>(ReferenceAstrometryJson);
            Assert.IsNotNull(options);
            Assert.AreEqual(depth, options.Recipe.Solver!.MaximumCatalogMagnitude);
            Assert.AreEqual(pin, options.CatalogSelection);
            Assert.IsFalse(solve.Required);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task ReferenceAstrometry_PreservesCoherentCustomOptionsAndRejectsBadCatalogs(bool changeDepth, bool stringClock)
    {
        var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
        var source = CreateReferenceCatalog();
        var token = CancellationToken.None;
        var (generated, originalPin) = await ReferenceAstrometryPreset.ApplyAsync(config.Pipeline, 4.5, source, token).ConfigureAwait(false);
        object synchronization = stringClock ? "Synchronized" : (int)AstrometricClockSynchronization.Synchronized;
        var custom = generated with
        {
            Steps = generated.Steps.Select(step => step.Type == "MeasuredAstrometry"
                ? step with
                {
                    Options = CaptureContractJson.SerializeToElement(new
                    {
                        useAstrometryPrediction = true,
                        catalogSelection = new
                        {
                            originalPin.CatalogIdentitySha256,
                            originalPin.SelectionIdentitySha256,
                            originalPin.MaximumMagnitude,
                            originalPin.MaximumEntries,
                            futurePinField = new { label = "keep", version = 9 }
                        },
                        captureStride = 120,
                        clock = new { source = "fixture", synchronization, maximumErrorSeconds = 0.1, estimatedErrorSeconds = 0.04 },
                        recipe = new { solver = new { maximumCatalogMagnitude = 4.5, hypothesisLimit = 120000 } }
                    })
                }
                : step).ToArray()
        };
        using var provider = CreateProvider(Path.GetTempPath());
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        factory.CreateGraph(config with { Pipeline = custom }).DisposeSteps();
        var originalOptions = custom.Steps.Single(step => step.Type == "MeasuredAstrometry").Options!.Value;
        var (updated, newPin) = await ReferenceAstrometryPreset.ApplyAsync(
            custom, changeDepth ? 3.25 : null, source, token).ConfigureAwait(false);
        if (changeDepth)
        {
            Assert.AreNotEqual(originalPin.SelectionIdentitySha256, newPin.SelectionIdentitySha256);
            Assert.AreEqual(3.25, newPin.MaximumMagnitude);
        }
        else
        {
            Assert.AreSame(custom, updated);
            Assert.AreEqual(originalPin, newPin);
        }
        factory.CreateGraph(config with { Pipeline = updated }).DisposeSteps();
        var updatedSolver = updated.Steps.Single(step => step.Type == "MeasuredAstrometry").Options!.Value;
        Assert.AreEqual(originalOptions.GetProperty("clock").GetRawText(), updatedSolver.GetProperty("clock").GetRawText());
        var updatedPin = updatedSolver.GetProperty("catalogSelection");
        Assert.AreEqual(newPin.SelectionIdentitySha256, updatedPin.GetProperty("selectionIdentitySha256").GetString());
        Assert.AreEqual(originalOptions.GetProperty("catalogSelection").GetProperty("futurePinField").GetRawText(),
            updatedPin.GetProperty("futurePinField").GetRawText());
        Assert.AreEqual(newPin.MaximumMagnitude,
            updatedSolver.GetProperty("recipe").GetProperty("solver").GetProperty("maximumCatalogMagnitude").GetDouble());
        Assert.AreEqual(120, updatedSolver.GetProperty("captureStride").GetInt32());
        Assert.AreEqual(120000, updatedSolver.GetProperty("recipe").GetProperty("solver").GetProperty("hypothesisLimit").GetInt32());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ReferenceAstrometryPreset.ApplyAsync(custom, 3.25, CreateReferenceCatalog("replaced"), token).ConfigureAwait(false))
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ReferenceAstrometryPreset.ApplyAsync(config.Pipeline, 7, CreateReferenceCatalog(complete: false), token).ConfigureAwait(false))
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await ReferenceAstrometryPreset.ApplyAsync(config.Pipeline, double.NaN, source, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReferenceAstrometry_ResumedActivationRejectsAReplacedCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-astrometry-restart-{Guid.NewGuid():N}");
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var token = CancellationToken.None;
            ProcessingGraphRegistryState basis;
            string draftId;
            using (var firstHost = CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog())))
            {
                await firstHost.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = firstHost.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                basis = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
                var (draft, _) = await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId,
                    "pending", 3.25, "create", "test-owner", null, token).ConfigureAwait(false);
                draftId = draft.RevisionId;
                _ = await operations.ValidateRevisionAsync(draftId, "validate", "test-owner", null, token).ConfigureAwait(false);
            }

            using var restarted = CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog("replaced")));
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var resumed = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var before = await resumed.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            Assert.AreEqual(basis.StateVersion, before.StateVersion);
            await Assert.ThrowsExactlyAsync<ReferenceAstrometryCatalogConflictException>(async () =>
                await resumed.ActivateRevisionAsync(draftId, before.StateVersion, "activate", "test-owner", null, token)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            var after = await resumed.GetRegistryAsync(token).ConfigureAwait(false);
            Assert.AreEqual(basis.ActiveRevisionId, after.ActiveRevisionId);
            Assert.AreEqual(basis.StateVersion, after.StateVersion);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, after.Revisions.Single(item => item.RevisionId == draftId).Lifecycle);

            // Ordinary named graphs retain their existing selection policy. Their optional pinned node still
            // rejects an incompatible catalog at execution time.
            var pipeline = await resumed.ReadRevisionPipelineAsync(draftId, token).ConfigureAwait(false);
            var manual = await resumed.CreateRevisionAsync("Manual astrometry", "candidate", pipeline!,
                "manual-create", "test-owner", null, token).ConfigureAwait(false);
            _ = await resumed.ValidateRevisionAsync(manual.RevisionId, "manual-validate", "test-owner", null, token).ConfigureAwait(false);
            var activated = await resumed.ActivateRevisionAsync(manual.RevisionId, after.StateVersion,
                "manual-activate", "test-owner", null, token).ConfigureAwait(false);
            Assert.AreEqual(manual.RevisionId, activated.ActiveRevisionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<CameraModuleConfig> LoadAstrometryProfileAsync(string path)
        => new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, path),
            AgentId = "reference-astrometry-test",
            CentralIntegration = new() { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance).LoadAsync(CancellationToken.None);

    private static IAstrometricCatalogSource CreateReferenceCatalog(string version = "1", bool complete = true)
    {
        var mock = new Mock<IAstrometricCatalogSource>(MockBehavior.Strict);
        var metadata = new CatalogMetadata("reference-fixture", version, new Uri("https://example.test/catalog"), new string('A', 64), "test", "1");
        var package = new AstrometricCatalogProvenance("reference-fixture", version, "fixture", "1");
        CelestialCatalogObject[] stars = [new("bright", "Bright", 1, 30, 3), new("faint", "Faint", 2, 45, 4)];
        mock.Setup(source => source.ReadAsync(It.IsAny<double>(), 2500, It.IsAny<CancellationToken>()))
            .Returns((double depth, int _, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new AstrometricCatalogData(metadata, stars.Where(star => star.Magnitude <= depth),
                    complete, depth, provenance: package));
            });
        return mock.Object;
    }
}

[TestClass]
public sealed class VerifiedReferenceAstrometryCatalogTests
{
    [TestMethod]
    [TestCategory("Manual")]
    public async Task ReferenceAstrometry_GeneratesTheVerifiedHyg42SelectionAtEachRequestedDepth()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_BETA_HYG42_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle))
            Assert.Inconclusive("Set HVO_BETA_HYG42_BUNDLE to the verified production HYG 4.2 bundle directory.");
        var catalog = CatalogSnapshotResolver.ResolveDirectory(bundle!, new(bundle!, "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        }).Catalog;
        var pipeline = new CapturePipelineConfig([], CapturePipelineSchemaVersions.ExplicitV2);
        var token = CancellationToken.None;
        var (_, standard) = await ReferenceAstrometryPreset.ApplyAsync(pipeline, null, catalog, token).ConfigureAwait(false);
        Assert.AreEqual(3.25, standard.MaximumMagnitude);
        Assert.AreEqual(2500, standard.MaximumEntries);
        Assert.AreEqual("0323fa224d3953443b145fefe4d946f2d0a9ae22828a9b72fad9acbe42dc5596", standard.SelectionIdentitySha256);
        var (_, custom) = await ReferenceAstrometryPreset.ApplyAsync(pipeline, 5, catalog, token).ConfigureAwait(false);
        Assert.AreEqual(standard.CatalogIdentitySha256, custom.CatalogIdentitySha256);
        Assert.AreEqual("ce81fa2e7cc859725cb105bc1b2167cf6fd286de2419c5aa1d282c19fa274cb8", custom.SelectionIdentitySha256);
        var customSnapshot = await custom.ResolveAsync(catalog, token).ConfigureAwait(false);
        Assert.HasCount(1637, customSnapshot.Stars);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ReferenceAstrometryPreset.ApplyAsync(pipeline, 5.5, catalog, token).ConfigureAwait(false)).ConfigureAwait(false);
    }
}
