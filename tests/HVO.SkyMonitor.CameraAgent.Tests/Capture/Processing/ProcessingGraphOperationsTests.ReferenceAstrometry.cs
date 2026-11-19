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

            using var provider = CreateProvider(root);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var token = CancellationToken.None;
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var registry = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var source = CreateReferenceCatalog();
            if (realCamera)
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                    await operations.CreateReferenceAstrometryRevisionAsync(registry.ActiveRevisionId, "missing-depth",
                        null, source, "invalid", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await operations.CreateReferenceAstrometryRevisionAsync(registry.ActiveRevisionId, "opaque-readout",
                        4.5, source, "opaque", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);

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
                "configured", realCamera ? 4.5 : null, source, "create-reference", "test-owner", null, token).ConfigureAwait(false);
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
    public async Task ReferenceAstrometry_PreservesCoherentCustomOptionsAndRejectsBadCatalogs()
    {
        var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
        var source = CreateReferenceCatalog();
        var token = CancellationToken.None;
        var (generated, originalPin) = await ReferenceAstrometryPreset.ApplyAsync(config.Pipeline, 4.5, source, token).ConfigureAwait(false);
        var custom = generated with
        {
            Steps = generated.Steps.Select(step => step.Type == "MeasuredAstrometry"
                ? step with { Options = CaptureContractJson.SerializeToElement(new
                {
                    useAstrometryPrediction = true, catalogSelection = originalPin, captureStride = 120,
                    recipe = new { solver = new { maximumCatalogMagnitude = 4.5, hypothesisLimit = 120000 } }
                }) }
                : step).ToArray()
        };
        var (inherited, pin) = await ReferenceAstrometryPreset.ApplyAsync(custom, null, source, token).ConfigureAwait(false);
        Assert.AreSame(custom, inherited);
        Assert.AreEqual(originalPin, pin);
        var (updated, newPin) = await ReferenceAstrometryPreset.ApplyAsync(custom, 3.25, source, token).ConfigureAwait(false);
        Assert.AreNotEqual(originalPin.SelectionIdentitySha256, newPin.SelectionIdentitySha256);
        var updatedSolver = updated.Steps.Single(step => step.Type == "MeasuredAstrometry").Options!.Value;
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
