using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly JsonSerializerOptions ReferenceAstrometryJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("crossed")]
    [DataRow("independent")]
    [DataRow("standalone")]
    public async Task ReferenceAstrometry_GraphPreviewAndCreationRequireTheSameScene(string mode)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-astrometry-scenes");
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var source = CreateReferenceCatalog();
            using var provider = CreateProvider(root, configureServices: services => services.AddSingleton(source));
            var token = CancellationToken.None;
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var (generated, _) = await ReferenceAstrometryPreset.ApplyAsync(config.Pipeline, 3.25, source, token).ConfigureAwait(false);
            var canonicalId = config.Pipeline.Steps.Single(step => step.Type == "ProjectedScene").Id!;
            var steps = generated.Steps.ToList();
            var measured = steps.Single(step => step.Type == "MeasuredStellarAssociations");
            var canonicalMeasurement = measured with
            {
                DependsOn = [canonicalId],
                Options = JsonSerializer.SerializeToElement(new { useAstrometryPrediction = false, outputVariant = "canonical-measurement" })
            };
            if (mode == "crossed")
                steps[steps.IndexOf(measured)] = canonicalMeasurement;
            if (mode == "standalone")
                steps.Add(canonicalMeasurement with { Id = "StandaloneMeasurement" });
            var pipeline = generated with { Steps = steps };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();

            if (mode == "crossed")
            {
                var previewFailure = Assert.ThrowsExactly<InvalidOperationException>(
                    () => factory.PreviewPlan(config with { Pipeline = pipeline }));
                StringAssert.Contains(previewFailure.Message, "must depend on the same scene producer", StringComparison.Ordinal);
                var creationFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await operations.CreateRevisionAsync("fixture", mode, pipeline, "create", "test-owner", null, token).ConfigureAwait(false))
                    .ConfigureAwait(false);
                Assert.AreEqual(previewFailure.Message, creationFailure.Message);

                // Historical graphs must still restore their required nodes and retained evidence.
                factory.CreateRetainedGraph(config with { Pipeline = pipeline }).DisposeSteps();
                return;
            }

            var preview = factory.PreviewPlan(config with { Pipeline = pipeline });
            Assert.IsTrue(preview.EffectiveNodes.Any(node => node.Id == "Astrometry"));
            Assert.AreEqual(mode == "standalone", preview.EffectiveNodes.Any(node => node.Id == "StandaloneMeasurement"));
            var draft = await operations.CreateRevisionAsync("fixture", mode, pipeline, "create", "test-owner", null, token)
                .ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ReferenceAstrometry_ReplaysCommittedCreationAfterCatalogChanges(bool missingCatalog, bool legacyReceipt)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-astrometry-receipts");
        const string key = "create";
        const string actor = "test-owner";
        const string reason = "initial setup";
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var token = CancellationToken.None;
            string basisId;
            string differentBasisId;
            (ProcessingGraphRevisionState Revision, MeasuredAstrometryCatalogSelection Selection) committed;
            using (var firstHost = CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog())))
            {
                await firstHost.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = firstHost.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                basisId = (await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false)).ActiveRevisionId;
                var different = config.Pipeline with
                {
                    Steps = config.Pipeline.Steps.Select(step => step.Type == "ProjectedScene"
                        ? step with { Options = JsonSerializer.SerializeToElement(new { maximumMagnitude = 7 }) }
                        : step).ToArray()
                };
                differentBasisId = (await operations.CreateRevisionAsync("basis", "different", different, "another-command",
                    actor, null, token).ConfigureAwait(false)).RevisionId;
                committed = await operations.CreateReferenceAstrometryRevisionAsync(basisId, "preset", 3.25, key, actor, reason, token)
                    .ConfigureAwait(false);
            }

            if (legacyReceipt)
            {
                // Simulate the exact pre-correction receipt format using the same frozen revision.
                const string legacyKind = "create-reference-astrometry";
                var oldHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{legacyKind}\n{committed.Revision.RevisionId}\n{actor}\n{reason}")));
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE processing_graph_commands SET command_kind = $kind, command_sha256 = $hash WHERE idempotency_key = $key;
                    """;
                command.Parameters.AddWithValue("$kind", legacyKind);
                command.Parameters.AddWithValue("$hash", oldHash);
                command.Parameters.AddWithValue("$key", key);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
            }

            using var restarted = missingCatalog
                ? CreateProvider(root)
                : CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog("replaced")));
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var resumed = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var replayed = await resumed.CreateReferenceAstrometryRevisionAsync(basisId, "preset", 3.25, key, actor, reason, token)
                .ConfigureAwait(false);
            Assert.AreEqual(committed, replayed);

            (string Basis, string Revision, double? Magnitude, string Actor, string Reason)[] changedRequests =
            [
                (differentBasisId, "preset", 3.25, actor, reason),
                (basisId, "other-revision", 3.25, actor, reason),
                (basisId, "preset", 4.5, actor, reason),
                (basisId, "preset", 3.25, "other-owner", reason),
                (basisId, "preset", 3.25, actor, "other reason")
            ];
            foreach (var changed in changedRequests)
            {
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await resumed.CreateReferenceAstrometryRevisionAsync(changed.Basis, changed.Revision, changed.Magnitude,
                        key, changed.Actor, changed.Reason, token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            if (!legacyReceipt)
            {
                // A missing magnitude yields the same graph by default, but is a distinct caller request.
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await resumed.CreateReferenceAstrometryRevisionAsync(basisId, "preset", null, key, actor, reason, token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }

            var registry = await resumed.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            await resumed.ValidateRevisionAsync(committed.Revision.RevisionId, "validate", actor, null, token).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ReferenceAstrometryCatalogConflictException>(async () =>
                await resumed.ActivateRevisionAsync(committed.Revision.RevisionId, registry.StateVersion,
                    "activate", actor, null, token).ConfigureAwait(false)).ConfigureAwait(false);
            if (missingCatalog)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await resumed.CreateReferenceAstrometryRevisionAsync(basisId, "fresh", 3.25, "fresh", actor, reason, token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            else
            {
                var fresh = await resumed.CreateReferenceAstrometryRevisionAsync(basisId, "fresh", 3.25, "fresh", actor, reason, token)
                    .ConfigureAwait(false);
                Assert.AreNotEqual(committed.Selection.CatalogIdentitySha256, fresh.Selection.CatalogIdentitySha256);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReferenceAstrometry_ConcurrentRetriesReturnTheCommittedSelection()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-astrometry-receipt-race");
        var firstReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var original = CreateReferenceCatalog();
            var replacement = CreateReferenceCatalog("replaced");
            var catalog = new Mock<IAstrometricCatalogSource>(MockBehavior.Strict);
            var reads = 0;
            catalog.Setup(source => source.ReadAsync(3.25, 2500, It.IsAny<CancellationToken>()))
                .Returns(async (double magnitude, int count, CancellationToken cancellationToken) =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        firstReadEntered.SetResult();
                        await allowFirstRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        return await original.ReadAsync(magnitude, count, cancellationToken).ConfigureAwait(false);
                    }
                    return await replacement.ReadAsync(magnitude, count, cancellationToken).ConfigureAwait(false);
                });
            using var provider = CreateProvider(root, configureServices: services => services.AddSingleton(catalog.Object));
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var basis = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var first = operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId, "race", 3.25,
                "same-key", "test-owner", null, token).AsTask();
            try
            {
                await firstReadEntered.Task.WaitAsync(token).ConfigureAwait(false);
                var committed = await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId, "race", 3.25,
                    "same-key", "test-owner", null, token).ConfigureAwait(false);
                Assert.AreEqual((await replacement.ReadAsync(3.25, 2500, token).ConfigureAwait(false)).IdentitySha256,
                    committed.Selection.CatalogIdentitySha256);
                allowFirstRead.SetResult();
                Assert.AreEqual(committed, await first.ConfigureAwait(false));
                var registry = await operations.GetRegistryAsync(token).ConfigureAwait(false);
                Assert.AreEqual(1, registry.Revisions.Count(revision => revision.Name == ReferenceAstrometryPreset.GraphName));
            }
            finally
            {
                allowFirstRead.TrySetResult();
                await first.ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceAstrometry_ChecksEffectiveReadoutAtCreationAndSelection(bool rollback)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-astrometry-readout");
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var fullFrame = new SensorReadoutProfile(new(0, 0, 5000, 4000), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1,
                FrameLevelCodeSpace.StoredContainer, 0, 65535);
            var full = config with
            {
                Module = new("CalibratedPhysicalCamera"),
                Rig = config.Rig with
                {
                    Sensor = config.Rig.Sensor with { WidthPixels = 5000, HeightPixels = 4000, PixelFormat = CameraPixelFormat.Mono16 },
                    Optics = config.Rig.Optics with { PrincipalPointX = 2500, PrincipalPointY = 2000 },
                    Readout = fullFrame
                },
                Pipeline = new([new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                    new FileStorageCaptureProcessingStepOptions { StorageRoot = root, QueueForUpload = false }))],
                    CapturePipelineSchemaVersions.ExplicitV2)
            };
            var compatible = full with
            {
                Rig = full.Rig with
                {
                    Readout = rollback
                        ? fullFrame with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 }
                        : fullFrame with { Roi = new(500, 500, 4000, 3000) }
                }
            };
            var detectorOversize = full with { Rig = full.Rig with { Readout = fullFrame with { Roi = new(0, 0, 4100, 4000) } } };
            using var provider = CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog()));
            var token = CancellationToken.None;
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var basis = await operations.EnsureConfiguredBasicAsync(full, token).ConfigureAwait(false);
            foreach (var incompatible in new[] { full, detectorOversize })
            {
                _ = await operations.EnsureConfiguredBasicAsync(incompatible, token).ConfigureAwait(false);
                var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId, "preset", 3.25,
                        "create", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);
                StringAssert.Contains(failure.Message, "readout dimensions", StringComparison.Ordinal);
            }

            var before = await operations.EnsureConfiguredBasicAsync(compatible, token).ConfigureAwait(false);
            Assert.AreEqual(basis.ActiveRevisionId, before.ActiveRevisionId);
            Assert.AreEqual(1, before.Revisions.Count);
            var (draft, _) = await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId, "preset", 3.25,
                "create", "test-owner", null, token).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test-owner", null, token).ConfigureAwait(false);
            if (rollback)
            {
                var active = await operations.ActivateRevisionAsync(draft.RevisionId, before.StateVersion,
                    "activate", "test-owner", null, token).ConfigureAwait(false);
                await operations.RollbackRevisionAsync(basis.ActiveRevisionId, active.StateVersion,
                    "restore", "test-owner", null, token).ConfigureAwait(false);
            }

            before = await operations.EnsureConfiguredBasicAsync(full, token).ConfigureAwait(false);
            var selectionFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                _ = rollback
                    ? await operations.RollbackRevisionAsync(draft.RevisionId, before.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false)
                    : await operations.ActivateRevisionAsync(draft.RevisionId, before.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false))
                .ConfigureAwait(false);
            StringAssert.Contains(selectionFailure.Message, "readout dimensions", StringComparison.Ordinal);
            var after = await operations.EnsureConfiguredBasicAsync(compatible, token).ConfigureAwait(false);
            Assert.AreEqual(before.StateVersion, after.StateVersion);
            Assert.AreEqual(basis.ActiveRevisionId, after.ActiveRevisionId);
            var selected = rollback
                ? await operations.RollbackRevisionAsync(draft.RevisionId, after.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false)
                : await operations.ActivateRevisionAsync(draft.RevisionId, after.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, selected.ActiveRevisionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceAstrometry_OrdinarySameNameSurvivesReopenAndSelection(bool rollback)
    {
        var root = Directory.CreateTempSubdirectory("hvo-astrometry-ordinary-").FullName;
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var token = CancellationToken.None;
            string ordinaryId;
            using (var originalHost = CreateProvider(root))
            {
                await originalHost.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = originalHost.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var basis = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
                var ordinary = await operations.CreateRevisionAsync("Reference astrometry preset", "existing-owner-revision",
                    config.Pipeline, "create", "test-owner", null, token).ConfigureAwait(false);
                ordinaryId = ordinary.RevisionId;
                _ = await operations.ValidateRevisionAsync(ordinaryId, "validate", "test-owner", null, token).ConfigureAwait(false);
                if (rollback)
                {
                    // Seed a previously selected ordinary revision using the pre-preset registry policy.
                    var store = originalHost.GetRequiredService<SqliteCaptureProcessingStore>();
                    var selected = await store.ActivateRevisionAsync(ordinaryId, basis.StateVersion, "activate-old",
                        "test-owner", null, token).ConfigureAwait(false);
                    _ = await store.RollbackRevisionAsync(basis.ActiveRevisionId, selected.StateVersion, "rollback-old",
                        "test-owner", null, token).ConfigureAwait(false);
                }
            }

            using var reopenedHost = CreateProvider(root);
            await reopenedHost.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopened = reopenedHost.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var before = await reopened.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var after = rollback
                ? await reopened.RollbackRevisionAsync(ordinaryId, before.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false)
                : await reopened.ActivateRevisionAsync(ordinaryId, before.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false);
            Assert.AreEqual(ordinaryId, after.ActiveRevisionId);
            Assert.AreEqual(before.StateVersion + 1, after.StateVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceAstrometry_ResumedSelectionRejectsAReplacedCatalog(bool rollback)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-astrometry-restart-{Guid.NewGuid():N}");
        try
        {
            var config = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var token = CancellationToken.None;
            ProcessingGraphRegistryState basis;
            string draftId;
            string manualId;
            using (var firstHost = CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog())))
            {
                await firstHost.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var operations = firstHost.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                basis = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
                var (draft, _) = await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId,
                    "pending", 3.25, "create", "test-owner", null, token).ConfigureAwait(false);
                draftId = draft.RevisionId;
                var (replayed, _) = await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId,
                    "pending", 3.25, "create", "test-owner", null, token).ConfigureAwait(false);
                Assert.AreEqual(draftId, replayed.RevisionId);
                _ = await operations.ValidateRevisionAsync(draftId, "validate", "test-owner", null, token).ConfigureAwait(false);

                var pipeline = await operations.ReadRevisionPipelineAsync(draftId, token).ConfigureAwait(false);
                var manual = await operations.CreateRevisionAsync(draft.Name, "manual", pipeline!,
                    "manual-create", "test-owner", null, token).ConfigureAwait(false);
                manualId = manual.RevisionId;
                _ = await operations.ValidateRevisionAsync(manualId, "manual-validate", "test-owner", null, token).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.CreateReferenceAstrometryRevisionAsync(basis.ActiveRevisionId,
                        "manual", 3.25, "colliding-create", "test-owner", null, token).ConfigureAwait(false)).ConfigureAwait(false);
                if (rollback)
                {
                    var active = await operations.ActivateRevisionAsync(draftId, basis.StateVersion, "activate-original",
                        "test-owner", null, token).ConfigureAwait(false);
                    basis = await operations.RollbackRevisionAsync(basis.ActiveRevisionId, active.StateVersion, "return-to-basis",
                        "test-owner", null, token).ConfigureAwait(false);
                }
            }

            using var restarted = CreateProvider(root, configureServices: services => services.AddSingleton(CreateReferenceCatalog("replaced")));
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var resumed = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var before = await resumed.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            Assert.AreEqual(basis.StateVersion, before.StateVersion);
            await Assert.ThrowsExactlyAsync<ReferenceAstrometryCatalogConflictException>(async () =>
                _ = rollback
                    ? await resumed.RollbackRevisionAsync(draftId, before.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false)
                    : await resumed.ActivateRevisionAsync(draftId, before.StateVersion, "select", "test-owner", null, token).ConfigureAwait(false))
                .ConfigureAwait(false);
            var after = await resumed.GetRegistryAsync(token).ConfigureAwait(false);
            Assert.AreEqual(basis.ActiveRevisionId, after.ActiveRevisionId);
            Assert.AreEqual(basis.StateVersion, after.StateVersion);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, after.Revisions.Single(item => item.RevisionId == draftId).Lifecycle);

            // Ordinary named graphs retain their existing selection policy. Their optional pinned node still
            // rejects an incompatible catalog at execution time.
            var activated = await resumed.ActivateRevisionAsync(manualId, after.StateVersion,
                "manual-activate", "test-owner", null, token).ConfigureAwait(false);
            Assert.AreEqual(manualId, activated.ActiveRevisionId);
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
