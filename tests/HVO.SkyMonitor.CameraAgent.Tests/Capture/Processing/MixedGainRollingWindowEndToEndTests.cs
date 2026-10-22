using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// VirtualSky windows that cross a gain step, stacked by the mixed-gain recipe through the real ingress, standard
/// lane, replay worker and host adapter with the active family composed of the agent's own virtual reference
/// acquisitions (#1524).
/// </summary>
[TestClass]
public sealed class MixedGainRollingWindowEndToEndTests
{
    private const string NodeId = "mixed-gain";
    private const double LowGain = 82;
    private const double HighGain = 100;
    private static readonly DateTimeOffset FixtureUtc = new(2026, 2, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LightExposure = TimeSpan.FromMilliseconds(32);
    private static readonly int[] PrimaryOnly = [0];

    private static readonly VirtualCalibrationLightOptions VirtualCalibration = new()
    {
        SourceModel = new VirtualCalibrationSourceModelV1 { Seed = 1524 },
        BiasExposure = TimeSpan.FromMilliseconds(1),
        DarkExposure = TimeSpan.FromSeconds(2),
        FlatExposure = TimeSpan.FromMilliseconds(100),
        DefectExposure = TimeSpan.FromMilliseconds(3),
        Offset = 1,
        TemperatureC = -10
    };

    [TestMethod]
    [TestCategory("Integration")]
    public Task AWindowAcrossAGainStepIsFrozenExecutedThroughTheAdapterAndStoredWithItsPlan()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            CollectionAssert.AreEqual(
                new[] { CaptureLaneHandlerOutcome.Completed, CaptureLaneHandlerOutcome.Completed },
                run.Drains.Select(static drain => drain.Outcome).ToArray());
            var low = run.Receipts[0];
            var light = run.Receipts[1].Manifest.Descriptor;
            var node = await run.ReadNodeAsync(1, NodeId).ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status);
            Assert.HasCount(1, node.Outputs);

            // The window's execution froze its plan across the gain step, and the stored product is what it planned.
            var frozen = await run.ReadPlanAsync(1, NodeId).ConfigureAwait(false);
            Assert.AreEqual(run.FamilyIdentitySha256, frozen.Plan.CalibrationFamilyIdentitySha256);
            var selected = Selected(frozen.Plan);
            CollectionAssert.AreEqual(new[] { -1, 0 }, selected.Select(static input => input.WindowPosition).ToArray());
            CollectionAssert.AreEqual(new[] { LowGain, HighGain }, selected.Select(static input => input.Gain).ToArray());
            CollectionAssert.AreEqual(
                new[] { low.Manifest.Descriptor.Artifact.ArtifactId, light.Artifact.ArtifactId },
                selected.Select(static input => input.ArtifactId).ToArray());
            var masters = selected.SelectMany(static input => input.Masters!).Select(static master => master.ArtifactId)
                .ToArray();
            CollectionAssert.AreEquivalent(
                run.Bundles.Values.SelectMany(LibraryMasters).Select(static master => master.ArtifactId).ToArray(),
                masters);
            CollectionAssert.AreEquivalent(
                selected.Select(static input => input.ArtifactId).Concat(masters).ToArray(),
                node.Outputs[0].Descriptor!.Artifact.SourceArtifactIds.ToArray());
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_CompletedExecutionKeepsReferencedMasters()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, (await run.ReadNodeAsync(1, NodeId).ConfigureAwait(false)).Status);
            var masters = run.Bundles.Values.SelectMany(LibraryMasters).Select(static master => master.ArtifactId).Order().ToArray();
            Assert.HasCount(8, masters);
            var holds = run.Provider.GetRequiredService<IProcessingRetentionHolds>();
            async Task<Guid[]> HeldMastersAsync()
                => (await holds.GetRetentionHoldsAsync(run.Root, CancellationToken.None).ConfigureAwait(false))
                    .Select(static hold => hold.ArtifactId).Intersect(masters).Order().ToArray();

            // The family is superseded and no bundle holds its own artifacts, so only the completed executions' frozen
            // plans can hold the masters their stored products were calibrated with.
            Assert.IsTrue((await run.Provider.GetRequiredService<CalibrationFamilyActivationService>().DeactivateAsync(
                new CalibrationFamilyDeactivationRequest(
                    "mixed-gain-deactivate", run.Family.AgentId, run.Family.RigId, "operator", null),
                CancellationToken.None).ConfigureAwait(false)).IsAccepted);
            await ClearBundleRetentionHoldsAsync(run.Root).ConfigureAwait(false);
            CollectionAssert.AreEqual(masters, await HeldMastersAsync().ConfigureAwait(false),
                "a completed execution's stored product still references its plan's masters");

            // Once the products that reference them are removed, the masters are released.
            _ = await ((IProcessingOutputExpiration)holds).ExpireOutputsAsync(
                run.Root, DateTimeOffset.UtcNow.AddDays(1), new HashSet<string>(StringComparer.Ordinal),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(await run.Store.ReadNodeAsync(run.CaptureId(1), NodeId, CancellationToken.None).ConfigureAwait(false),
                "expiry removes the products");
            Assert.IsEmpty(await HeldMastersAsync().ConfigureAwait(false));
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_CenteredWindowMustRetainEveryRequiredPosition()
        => RunAsync([LowGain, HighGain, 90], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            var operations = run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var revision = await operations.CreateRevisionAsync(
                "centered-mixed-gain", "v1",
                Pipeline(run.Root, [MixedGainStep(windowSize: 3, nodeId: "centered", windowKind: "Centered")]),
                "centered-create-key", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ValidateRevisionAsync(
                revision.RevisionId, "centered-validate-key", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    run.CaptureId(1), revision.RevisionId, run.Receipts[1].Manifest.Descriptor.Artifact.ArtifactId,
                    TriggerReference: "centered-replay"),
                "centered-replay-key", "owner-test", CancellationToken.None).ConfigureAwait(false);
            var detail = await RunReplayAsync(run, replay.Execution.ExecutionId).ConfigureAwait(false);

            // The family has no member for the +1 frame's gain, so the plan excludes it. A centered window requires every
            // position, and an excluded frame does not fill one.
            var frozen = await run.Store.ReadNormalizationPlanAsync(replay.Execution.ExecutionId, "centered", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            CollectionAssert.AreEqual(new[] { -1, 0 }, Selected(frozen.Plan).Select(static input => input.WindowPosition).ToArray());
            var node = detail.Nodes.Single(static state => state.NodeId == "centered");
            Assert.AreEqual("Skipped", node.Status, node.Reason);
            Assert.AreEqual(NormalizationPlanFreezer.WindowIncomplete, node.Reason);
            Assert.IsEmpty(node.Outputs);
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_RealMixedGainProductsCanFeedRollingMean()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep(), MeanStep("mean", NodeId, 2)], async run =>
        {
            var products = new List<Guid>();
            for (var index = 0; index < 2; index++)
            {
                var mixed = await run.ReadNodeAsync(index, NodeId).ConfigureAwait(false);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, mixed.Status, mixed.Reason);
                products.Add(mixed.Outputs.Single().ArtifactId);
            }

            // Two windows freeze two plans, so their products' recipe identities differ, but both are on the scale the
            // family, policy and response fix, which their shared normalized compatibility names.
            var mean = await run.ReadNodeAsync(1, "mean").ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, mean.Status, mean.Reason);
            CollectionAssert.AreEquivalent(products, mean.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds.ToArray());
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_MixedGainSelectionHonoursMaximumAge()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep(maximumAgeMilliseconds: 1000)], async run =>
        {
            // The frames are five seconds apart, so the older one is outside the configured age.
            var plan = (await run.ReadPlanAsync(1, NodeId).ConfigureAwait(false)).Plan;
            CollectionAssert.AreEqual(PrimaryOnly, Selected(plan).Select(static input => input.WindowPosition).ToArray());
            Assert.AreEqual(
                NormalizationPlanFreezer.MaximumAgeExceeded,
                plan.Inputs.Single(static input => input.WindowPosition == -1).ExclusionReason);
            var node = await run.ReadNodeAsync(1, NodeId).ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status, node.Reason);
            Assert.Contains(run.Receipts[1].Manifest.Descriptor.Artifact.ArtifactId, node.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds);
            Assert.DoesNotContain(run.Receipts[0].Manifest.Descriptor.Artifact.ArtifactId, node.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds);
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_MixedGainSelectionHonoursMaximumIntegration()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep(maximumIntegrationMilliseconds: 32)], async run =>
        {
            // Each light integrates 32 ms, so the configured 32 ms admits the primary alone.
            var plan = (await run.ReadPlanAsync(1, NodeId).ConfigureAwait(false)).Plan;
            CollectionAssert.AreEqual(PrimaryOnly, Selected(plan).Select(static input => input.WindowPosition).ToArray());
            Assert.AreEqual(
                NormalizationPlanFreezer.MaximumIntegrationExceeded,
                plan.Inputs.Single(static input => input.WindowPosition == -1).ExclusionReason);
            var node = await run.ReadNodeAsync(1, NodeId).ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status, node.Reason);
            Assert.DoesNotContain(run.Receipts[0].Manifest.Descriptor.Artifact.ArtifactId, node.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds);
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_RatioBoundaryRestartsAtPrimary()
        => RunAsync([LowGain, 300], [LowGain, 300], [MixedGainStep()], async run =>
        {
            // 82 and 300 are about 12x apart, past the 4x bound: the window restarts at the primary, which is eligible.
            var node = await run.ReadNodeAsync(1, NodeId).ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status, node.Reason);
            var plan = (await run.ReadPlanAsync(1, NodeId).ConfigureAwait(false)).Plan;
            CollectionAssert.AreEqual(PrimaryOnly, Selected(plan).Select(static input => input.WindowPosition).ToArray());
            Assert.AreEqual(
                GainNormalizationReasonCodes.CoefficientRatioExceeded,
                plan.Inputs.Single(static input => input.WindowPosition == -1).ExclusionReason);
            Assert.DoesNotContain(run.Receipts[0].Manifest.Descriptor.Artifact.ArtifactId, node.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds);
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task Review1530_SelectorKeepsValidNormalizedRollingProducts()
        => RunAsync(
            [LowGain, HighGain],
            [LowGain, HighGain],
            [MixedGainStep(), MeanStep("mean-1", NodeId, 1), MeanStep("mean-2", "mean-1", 2)],
            async run =>
            {
                var products = new List<Guid>();
                for (var index = 0; index < 2; index++)
                {
                    var first = await run.ReadNodeAsync(index, "mean-1").ConfigureAwait(false);
                    Assert.AreEqual(DurableProcessingNodeStatus.Completed, first.Status, first.Reason);
                    products.Add(first.Outputs.Single().ArtifactId);
                }

                // A rolling mean of mixed-gain products carries their normalized compatibility on, and a later window
                // over those means keeps both.
                var second = await run.ReadNodeAsync(1, "mean-2").ConfigureAwait(false);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, second.Status, second.Reason);
                CollectionAssert.AreEquivalent(products, second.Outputs.Single().Descriptor!.Artifact.SourceArtifactIds.ToArray());
            });

    /// <summary>
    /// Acquires the agent's own references at each family gain, accepts one VirtualSky light per capture gain five
    /// seconds apart, activates the family composed of those references and drains every capture through the standard
    /// lane under the configured window steps, then runs <paramref name="assert"/>.
    /// </summary>
    private static async Task RunAsync(
        double[] captureGains,
        double[] familyGains,
        CaptureProcessingStepConfig[] windowSteps,
        Func<Run, Task> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        VirtualSkyCameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration(root, windowSteps);
            ((CameraAgentConfigurationAccessor)provider.GetRequiredService<ICameraAgentConfigurationAccessor>())
                .SetConfiguration(configuration);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await provider.GetRequiredService<CaptureAdmissionCoordinator>().InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var library = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            _ = await library.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            // The agent acquires its own references at each gain of the family.
            var acquisition = provider.GetRequiredService<VirtualCalibrationAcquisitionCoordinator>();
            foreach (var gain in familyGains)
            {
                var job = await acquisition.AcquireAsync(AcquisitionRequest(gain), CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(CalibrationAcquisitionStates.Published, job.State, job.FailureReason);
            }
            var bundles = (await library.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false))
                .ToDictionary(static snapshot => snapshot.Bundle.Applicability.MinimumGain, static snapshot => snapshot.Bundle);

            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            module = new VirtualSkyCameraModule(
                TimeProvider.System,
                provider.GetRequiredService<ICelestialCatalog>(),
                provider.GetRequiredService<IProjectedSceneStore>(),
                provider.GetRequiredService<IConstellationTopology>(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipts = new List<RawCaptureReceipt>(captureGains.Length);
            for (var index = 0; index < captureGains.Length; index++)
            {
                var receipt = await ingress.AcceptAsync(
                    configuration,
                    await CreateSubmissionAsync(module, index, captureGains[index]).ConfigureAwait(false),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                receipts.Add(receipt);
            }

            // The operator activates one family whose members are those acquisitions.
            var light = receipts[^1].Manifest.Descriptor;
            var family = Family(light, bundles);
            var stored = await provider.GetRequiredService<SqliteCalibrationFamilyStore>()
                .StoreAsync(family, CancellationToken.None).ConfigureAwait(false);
            var activated = await provider.GetRequiredService<CalibrationFamilyActivationService>().ActivateAsync(
                new CalibrationFamilyActivationRequest(
                    "mixed-gain-activate",
                    new CalibrationFamilyActivationTarget(
                        light.Capture.AgentId, light.Capture.RigId, light.Profiles.Rig.Sha256, light.Profiles.Sensor.Sha256,
                        light.Profiles.Calibration),
                    stored.IdentitySha256,
                    "operator",
                    null),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(activated.IsAccepted);

            var drains = new List<CaptureLaneHandlerResult>(receipts.Count);
            foreach (var _ in receipts)
            {
                drains.Add(await DrainOneAsync(provider, configuration).ConfigureAwait(false));
            }
            await assert(new Run(provider, root, receipts, bundles, family, stored.IdentitySha256, drains))
                .ConfigureAwait(false);
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed record Run(
        ServiceProvider Provider,
        string Root,
        IReadOnlyList<RawCaptureReceipt> Receipts,
        IReadOnlyDictionary<double, CalibrationLibraryBundleV1> Bundles,
        CalibrationFamilyV1 Family,
        string FamilyIdentitySha256,
        IReadOnlyList<CaptureLaneHandlerResult> Drains)
    {
        public SqliteCaptureProcessingStore Store => Provider.GetRequiredService<SqliteCaptureProcessingStore>();

        public Guid CaptureId(int index) => Receipts[index].Manifest.Descriptor.Capture.CaptureId;

        public async Task<DurableProcessingNode> ReadNodeAsync(int index, string nodeId)
            => await Store.ReadNodeAsync(CaptureId(index), nodeId, CancellationToken.None).ConfigureAwait(false)
               ?? throw new AssertFailedException($"Capture {index} has no {nodeId} node.");

        public async Task<FrozenNormalizationPlan> ReadPlanAsync(int index, string nodeId)
        {
            var execution = (await Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
                .Single(state => state.CaptureId == CaptureId(index));
            return await Store.ReadNormalizationPlanAsync(execution.ExecutionId, nodeId, CancellationToken.None)
                       .ConfigureAwait(false)
                   ?? throw new AssertFailedException($"Capture {index} froze no {nodeId} plan.");
        }
    }

    private static NormalizationPlanInputV1[] Selected(NormalizationPlanV1 plan)
        => [.. plan.Inputs
            .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
            .OrderBy(static input => input.WindowPosition)];

    private static CaptureProcessingStepConfig MixedGainStep(
        int windowSize = 2,
        string nodeId = NodeId,
        string windowKind = "Trailing",
        double? maximumAgeMilliseconds = null,
        double? maximumIntegrationMilliseconds = null)
    {
        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["windowSize"] = windowSize,
            ["windowKind"] = windowKind,
            ["recipe"] = BuiltInProcessingRecipes.MixedGainNormalizedMean,
            ["outputVariant"] = "mixed-gain"
        };
        if (maximumAgeMilliseconds is { } age)
        {
            options["maximumAgeMilliseconds"] = age;
        }
        if (maximumIntegrationMilliseconds is { } integration)
        {
            options["maximumIntegrationMilliseconds"] = integration;
        }
        return new CaptureProcessingStepConfig(
            "RollingCombination", nodeId, Options: JsonSerializer.SerializeToElement(options), DependsOn: ["$raw"]);
    }

    private static CaptureProcessingStepConfig MeanStep(string nodeId, string dependsOn, int windowSize)
        => new(
            "RollingCombination",
            nodeId,
            Options: JsonSerializer.SerializeToElement(new { windowSize, outputVariant = nodeId }),
            DependsOn: [dependsOn]);

    private static async Task<ProcessingGraphExecutionDetail> RunReplayAsync(Run run, Guid executionId)
    {
        var operations = run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var worker = run.Provider.GetRequiredService<ProcessingReplayWorker>();
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var detail = await operations.ReadExecutionDetailAsync(executionId, timeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                if (detail.Execution.Status is not (ProcessingGraphExecutionStatus.Pending or ProcessingGraphExecutionStatus.Running))
                {
                    return detail;
                }
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ClearBundleRetentionHoldsAsync(string root)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE calibration_library_bundles SET retention_hold = 0;";
        Assert.IsGreaterThan(0, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static async Task<CaptureLaneHandlerResult> DrainOneAsync(ServiceProvider provider, CameraModuleConfig configuration)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");
        var lease = await laneStore.ClaimAsync(
            standard, "mixed-gain-window-test", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(
            static candidate => candidate.Lane == "standard");
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().NotifyLiveWorkChanged();
        return result;
    }

    private static VirtualCalibrationAcquisitionRequestV1 AcquisitionRequest(double gain)
        => new(
            VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
            FormattableString.Invariant($"mixed-gain-references-{gain}"),
            gain,
            VirtualCalibration.Offset,
            VirtualCalibration.TemperatureC,
            VirtualCalibration.BiasExposure,
            VirtualCalibration.DarkExposure,
            VirtualCalibration.FlatExposure,
            VirtualCalibration.DefectExposure,
            LightExposure,
            FixtureUtc.AddDays(-1),
            FixtureUtc.AddYears(1),
            VirtualCalibration.SourceModel,
            "operator",
            "mixed-gain window");

    private static CalibrationFamilyV1 Family(
        ReconstructionDescriptor light,
        IReadOnlyDictionary<double, CalibrationLibraryBundleV1> bundles)
        => new(
            CalibrationFamilyV1.CurrentSchemaVersion,
            "virtual-mixed-gain-family",
            light.Capture.AgentId,
            light.Capture.RigId,
            light.Profiles.Rig.Sha256,
            light.Profiles.Sensor.Sha256,
            light.Profiles.Calibration,
            light.Profiles.Mask.Sha256,
            [.. bundles.OrderBy(static pair => pair.Key).Select(pair => Member(pair.Key, pair.Value, light.Profiles.Mask.Sha256))]);

    private static CalibrationFamilyMemberV1 Member(double gain, CalibrationLibraryBundleV1 bundle, string maskSha256)
    {
        var applicability = bundle.Applicability;
        var masters = LibraryMasters(bundle).ToArray();
        return new CalibrationFamilyMemberV1(
            FormattableString.Invariant($"gain-{gain}"),
            gain,
            applicability.MinimumOffset,
            applicability.InputLayout,
            applicability.MinimumTemperatureC!.Value,
            applicability.MaximumTemperatureC!.Value,
            applicability.MinimumLightExposure!.Value,
            applicability.MaximumLightExposure!.Value,
            applicability.EffectiveFromUtc,
            null,
            maskSha256,
            [.. CalibrationReferenceKinds.All
                .Select(kind => masters.Single(master => master.Kind == kind))
                .Select(static master => new CalibrationFamilyMasterV1(master.Kind, master.ArtifactId, master.PayloadSha256))]);
    }

    private static IEnumerable<CalibrationLibraryArtifactV1> LibraryMasters(CalibrationLibraryBundleV1 bundle)
        => bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master);

    private static async Task<CaptureLoopSubmission> CreateSubmissionAsync(VirtualSkyCameraModule module, int index, double gain)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 5L);
        var setpoint = new CaptureSetpoint(LightExposure, gain, null, VirtualCalibration.TemperatureC);
        var request = new CaptureRequest(startedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint);
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        return new CaptureLoopSubmission(
            request,
            result with
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.Add(LightExposure), startedUtc.Add(LightExposure).AddMilliseconds(100))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog(
            [new CelestialCatalogObject("star", "Star", 2.5, 20, 1)]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateConfiguration(string root, CaptureProcessingStepConfig[] windowSteps)
    {
        var sensor = new SensorProfile(
            "MixedGainWindow", 64, 48, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome, SensorRecipeVersion: "mixed-gain-window-sensor-v1")
        {
            SimulationResponse = new ConfiguredSensorResponseProfile(
                "mixed-gain-window-response-v1",
                16,
                0,
                400,
                4.0,
                200,
                60_000,
                [new SensorReadNoisePoint(0, 2.0), new SensorReadNoisePoint(400, 1.0)],
                0,
                "ZWO 0.1 dB",
                "mixed-gain window test response")
        };
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, 64, 48), 1, 1, FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            0, ushort.MaxValue, 128);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(
                new VirtualSkyCameraModuleOptions
                {
                    MaximumResults = 10,
                    FixedSceneUtc = FixtureUtc,
                    VirtualCalibration = VirtualCalibration
                })),
            new CameraRigConfig(
                sensor,
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), LightExposure, LightExposure, LowGain, LowGain),
                ProfileVersion: "mixed-gain-window-rig-v1",
                Readout: readout),
            Pipeline(root, windowSteps),
            "mixed-gain-window-agent");
    }

    // A scene-bearing capture's graph keeps its one projected scene and storage beside the window steps.
    private static CapturePipelineConfig Pipeline(string root, CaptureProcessingStepConfig[] windowSteps)
        => new(
            [
                .. windowSteps,
                new CaptureProcessingStepConfig(
                    "ProjectedScene", "scene", DependsOn: ["$raw"],
                    Publication: new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal)),
                new CaptureProcessingStepConfig(
                    "Storage", "storage", Order: 100, DependsOn: ["scene"],
                    Options: JsonSerializer.SerializeToElement(new { storageRoot = root, queueForUpload = false }))
            ],
            CapturePipelineSchemaVersions.ExplicitV2,
            CapturePipelineDependencyPolicy.RejectEnabledDependent);
}
