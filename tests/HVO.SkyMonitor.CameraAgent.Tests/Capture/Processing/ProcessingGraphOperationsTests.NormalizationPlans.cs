using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private const string PlanNode = "preview";

    [TestMethod]
    public async Task NormalizationPlanFreezeIsLeaseCheckedIdempotentAndKeptAcrossRetries()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-normalization-plan-freeze");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var provider = CreateProvider(root, PlanSettings, clock);
            var fixture = await CreatePlanFixtureAsync(provider, root, clock).ConfigureAwait(false);
            var store = fixture.Store;
            var calibration = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            var original = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);
            Assert.IsNull(await store.ReadNormalizationPlanAsync(fixture.ExecutionId, PlanNode, CancellationToken.None)
                .ConfigureAwait(false));
            var plan = PlanFor(fixture.Inputs, fixture.Bundle, 0);

            var frozen = await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(NormalizationPlanContractJson.ComputeIdentitySha256(plan), frozen.IdentitySha256);
            CollectionAssert.AreEqual(NormalizationPlanContractJson.Serialize(plan), frozen.CanonicalJson);
            var stored = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);
            var expected = $"{Encoding.UTF8.GetString(original)[..^1]},\"normalizationPlans\":[{{\"nodeId\":\"{PlanNode}\"," +
                $"\"planIdentitySha256\":\"{frozen.IdentitySha256}\",\"plan\":{Encoding.UTF8.GetString(frozen.CanonicalJson)}}}]}}";
            Assert.AreEqual(expected, Encoding.UTF8.GetString(stored));
            var read = await store.ReadNormalizationPlanAsync(fixture.ExecutionId, PlanNode, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(frozen.IdentitySha256, read!.IdentitySha256);
            CollectionAssert.AreEqual(frozen.CanonicalJson, read.CanonicalJson);
            Assert.IsNull(await store.ReadNormalizationPlanAsync(fixture.ExecutionId, "other-node", CancellationToken.None)
                .ConfigureAwait(false));

            // Re-freezing the same window returns the same plan and writes nothing.
            Assert.AreEqual(frozen.IdentitySha256, (await store.FreezeNormalizationPlanAsync(calibration,
                fixture.Context, PlanNode, PlanFor(fixture.Inputs, fixture.Bundle, 0), CancellationToken.None)
                .ConfigureAwait(false)).IdentitySha256);
            CollectionAssert.AreEqual(stored, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await store.FreezeNormalizationPlanAsync(calibration,
                    fixture.Context, PlanNode, PlanFor(fixture.Inputs, fixture.Bundle, 40), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(stored, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));

            // A retry claims the work under a new lease; the stale lease cannot freeze, and the new one finds the same plan.
            clock.Advance(TimeSpan.FromSeconds(4));
            var retry = await store.ClaimReplayAsync("retry-host", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(retry);
            Assert.AreEqual(fixture.ExecutionId, retry.Execution.ExecutionId);
            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(frozen.IdentitySha256, (await store.FreezeNormalizationPlanAsync(calibration,
                ContextFor(retry), PlanNode, plan, CancellationToken.None).ConfigureAwait(false)).IdentitySha256);
            CollectionAssert.AreEqual(stored, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));
            Assert.IsNotNull(await fixture.Operations.ReadExecutionDetailAsync(fixture.ExecutionId, CancellationToken.None)
                .ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task NormalizationPlanFreezeRefusesAChangedSnapshotOrCancelledWorkAndWritesNothing()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-normalization-plan-refuse");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var provider = CreateProvider(root, PlanSettings, clock);
            var fixture = await CreatePlanFixtureAsync(provider, root, clock).ConfigureAwait(false);
            var store = fixture.Store;
            var calibration = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            var original = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);
            var plan = PlanFor(fixture.Inputs, fixture.Bundle, 0);
            var input = plan.Inputs[0];
            var masters = input.Masters!;

            NormalizationPlanV1[] otherWindows =
            [
                plan with { Inputs = [input with { PayloadSha256 = new string('E', 64) }] },
                plan with { Inputs = [input with { WindowPosition = input.WindowPosition + 1 }] },
                plan with { Inputs = [input, input with { Ordinal = 1, WindowPosition = -5, CaptureId = Guid.NewGuid(), ArtifactId = Guid.NewGuid() }] }
            ];
            foreach (var other in otherWindows)
            {
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, other, CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }

            (string Reason, NormalizationPlanMasterV1 Master)[] changedMasters =
            [
                (CalibrationFamilyReasonCodes.MasterMissing, masters[1] with { ArtifactId = Guid.NewGuid() }),
                (CalibrationFamilyReasonCodes.MasterMismatch, masters[1] with { PayloadSha256 = new string('A', 64) })
            ];
            foreach (var (reason, master) in changedMasters)
            {
                var changed = plan with
                {
                    Inputs = [input with { Masters = [.. masters.Select(existing => existing.Kind == master.Kind ? master : existing)] }]
                };
                var failure = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                    await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, changed, CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(reason, failure.ReasonCode);
            }
            await SetBundleStateAsync(root, fixture.Bundle.BundleId, "quarantined").ConfigureAwait(false);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterUnpublished, (await Assert.ThrowsExactlyAsync<NormalizationPlanException>(
                async () => await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false)).ReasonCode);
            await SetBundleStateAsync(root, fixture.Bundle.BundleId, "published").ConfigureAwait(false);

            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, cancelled.Token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            CollectionAssert.AreEqual(original, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));

            // A replay cancelled after its lease ended cannot freeze a plan, as it cannot write any durable state.
            _ = await store.CompleteReplayAsync(
                fixture.Lease, CaptureLaneHandlerResult.Wait("test-release"), CancellationToken.None).ConfigureAwait(false);
            var cancelledReplay = await fixture.Operations.CancelReplayAsync(
                fixture.ExecutionId, "plan-cancel", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Cancelled, cancelledReplay.Status);
            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(original, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));
            Assert.IsNull(await store.ReadNormalizationPlanAsync(fixture.ExecutionId, PlanNode, CancellationToken.None)
                .ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AFrozenNormalizationPlanIgnoresLiveChangesAndFailsExplicitlyWhenItsDependenciesAreCorrupt()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-normalization-plan-claim");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var provider = CreateProvider(root, PlanSettings, clock);
            var fixture = await CreatePlanFixtureAsync(provider, root, clock).ConfigureAwait(false);
            var store = fixture.Store;
            var calibration = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            var freezer = new NormalizationPlanFreezer(store, calibration);
            var original = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);

            // With no frozen plan, the freezer prepares one from the live family, which fails explicitly and writes nothing.
            Assert.AreEqual(CalibrationFamilyReasonCodes.InvalidFamily, (await Assert.ThrowsExactlyAsync<NormalizationPlanException>(
                async () => await freezer.FreezeAsync(
                    fixture.Context, PlanNode, "{}"u8.ToArray(), GainNormalizationPolicyV1.Approved, Asi174PlanResponse(),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false)).ReasonCode);
            Assert.AreEqual(CalibrationFamilyReasonCodes.IdentityMismatch, (await Assert.ThrowsExactlyAsync<NormalizationPlanException>(
                async () => await freezer.FreezeAsync(
                    fixture.Context, PlanNode, CalibrationFamilyContractJson.Serialize(UnrelatedFamily(fixture.Bundle)),
                    GainNormalizationPolicyV1.Approved, Asi174PlanResponse(), CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false)).ReasonCode);
            CollectionAssert.AreEqual(original, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));

            var frozen = await store.FreezeNormalizationPlanAsync(calibration,
                fixture.Context, PlanNode, PlanFor(fixture.Inputs, fixture.Bundle, 0), CancellationToken.None)
                .ConfigureAwait(false);

            // A changed live family, policy or response after the freeze does not change the plan a claim uses.
            var claimed = await freezer.FreezeAsync(
                fixture.Context,
                PlanNode,
                "not a family"u8.ToArray(),
                GainNormalizationPolicyV1.Approved with { MaximumCoefficientRatioMilli = 1000 },
                Asi174PlanResponse(sensorVersion: "virtual-asi174mm-electron-domain-v3"),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(frozen.IdentitySha256, claimed.IdentitySha256);
            CollectionAssert.AreEqual(frozen.CanonicalJson, claimed.CanonicalJson);

            // A master that is no longer intact fails the claim explicitly; the frozen plan is unchanged.
            var marker = Path.Combine(root, fixture.Bundle.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var markerBytes = await File.ReadAllBytesAsync(marker).ConfigureAwait(false);
            await File.WriteAllBytesAsync(marker, markerBytes[..^1]).ConfigureAwait(false);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, (await Assert.ThrowsExactlyAsync<NormalizationPlanException>(
                async () => await freezer.FreezeAsync(
                    fixture.Context, PlanNode, "{}"u8.ToArray(), GainNormalizationPolicyV1.Approved, Asi174PlanResponse(),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false)).ReasonCode);
            await File.WriteAllBytesAsync(marker, markerBytes).ConfigureAwait(false);
            Assert.AreEqual(frozen.IdentitySha256, (await freezer.FreezeAsync(
                fixture.Context, PlanNode, "{}"u8.ToArray(), GainNormalizationPolicyV1.Approved, Asi174PlanResponse(),
                CancellationToken.None).ConfigureAwait(false)).IdentitySha256);

            // Corrupt frozen evidence fails explicitly; nothing is re-derived from live state.
            var stored = Encoding.UTF8.GetString(await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));
            string[] corruptions =
            [
                stored.Replace(frozen.IdentitySha256, new string('0', 64), StringComparison.Ordinal),
                stored.Replace("\"memberId\":\"gain-0\"", "\"memberId\":\"gain-1\"", StringComparison.Ordinal),
                stored.Replace("\"plan\":{", "\"plan\":[{", StringComparison.Ordinal),
                stored[..^2]
            ];
            foreach (var corruption in corruptions)
            {
                Assert.AreNotEqual(stored, corruption);
                await WriteFrozenExecutionJsonAsync(root, fixture.ExecutionId, Encoding.UTF8.GetBytes(corruption))
                    .ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await store.ReadNormalizationPlanAsync(fixture.ExecutionId, PlanNode, CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await freezer.FreezeAsync(
                        fixture.Context, PlanNode, "{}"u8.ToArray(), GainNormalizationPolicyV1.Approved,
                        Asi174PlanResponse(), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, "payload")]
    [DataRow(false, "manifest")]
    [DataRow(false, "profile-marker")]
    [DataRow(true, "payload")]
    [DataRow(true, "manifest")]
    [DataRow(true, "profile-marker")]
    public async Task NormalizationPlanFreezeRejectsEvidenceCorruptedAfterPreparation(bool alreadyFrozen, string evidence)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-normalization-plan-evidence");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var provider = CreateProvider(root, PlanSettings, clock);
            var fixture = await CreatePlanFixtureAsync(provider, root, clock).ConfigureAwait(false);
            var calibration = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            var plan = PlanFor(fixture.Inputs, fixture.Bundle, 0);
            // Establish the same verified master snapshot as preparation, then damage files without changing library rows.
            await NormalizationPlanFreezer.ConfirmAsync(calibration, plan, CancellationToken.None).ConfigureAwait(false);
            if (alreadyFrozen)
            {
                _ = await fixture.Store.FreezeNormalizationPlanAsync(calibration,
                    fixture.Context, PlanNode, plan, CancellationToken.None).ConfigureAwait(false);
            }
            var manifest = fixture.Bundle.Artifacts.Single(artifact => artifact.Kind == CalibrationReferenceKinds.Flat)
                .ManifestRelativePath;
            var relative = evidence switch
            {
                "payload" => string.Concat(manifest.AsSpan(0, manifest.Length - ".json".Length), ".bin"),
                "manifest" => manifest,
                _ => fixture.Bundle.ProfileRelativePath
            };
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var original = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            var corrupt = original.ToArray();
            corrupt[0] ^= 0x01;
            await File.WriteAllBytesAsync(path, corrupt).ConfigureAwait(false);
            // A return must verify bytes even when an earlier evidence-cache fingerprint still matches.
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            Assert.AreEqual((long)original.Length, new FileInfo(path).Length);
            Assert.AreEqual(lastWriteUtc, File.GetLastWriteTimeUtc(path));

            var failure = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                await fixture.Store.FreezeNormalizationPlanAsync(calibration,
                    fixture.Context, PlanNode, plan, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, failure.ReasonCode);
            // Post-freeze failure keeps the first plan immutable; restoring evidence makes the same plan usable again.
            var frozen = await fixture.Store.ReadNormalizationPlanAsync(fixture.ExecutionId, PlanNode, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            CollectionAssert.AreEqual(NormalizationPlanContractJson.Serialize(plan), frozen.CanonicalJson);
            var stored = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await fixture.Store.FreezeNormalizationPlanAsync(calibration,
                    fixture.Context, PlanNode, PlanFor(fixture.Inputs, fixture.Bundle, 40), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, original).ConfigureAwait(false);
            var retry = await fixture.Store.FreezeNormalizationPlanAsync(calibration,
                fixture.Context, PlanNode, plan, CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(frozen.CanonicalJson, retry.CanonicalJson);
            CollectionAssert.AreEqual(stored, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static readonly Dictionary<string, string?> PlanSettings = new()
    {
        ["CameraAgent:ProcessingGraphs:ReplayLeaseSeconds"] = "3",
        ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
    };

    /// <summary>
    /// A claimed replay of one accepted capture, whose preview node froze that raw capture as its window, and a
    /// published synthetic calibration bundle in the same library for the plan's masters.
    /// </summary>
    private static async Task<PlanFixture> CreatePlanFixtureAsync(ServiceProvider provider, string root, MutableTimeProvider clock)
    {
        var configuration = CreateConfiguration() with
        {
            Pipeline = new CapturePipelineConfig(
                [new CaptureProcessingStepConfig("Preview", PlanNode, DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var receipt = await ingress.AcceptAsync(
            configuration, CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
        var replay = await operations.SubmitReplayAsync(
            new ProcessingReplaySubmission(
                receipt.Manifest.Descriptor.Capture.CaptureId,
                registry.ActiveRevisionId,
                receipt.Manifest.Descriptor.Artifact.ArtifactId),
            "normalization-plan-replay",
            "owner-test",
            CancellationToken.None).ConfigureAwait(false);
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var lease = await store.ClaimReplayAsync("plan-host", CancellationToken.None).ConfigureAwait(false)
            ?? throw new AssertFailedException("The replay was not claimed.");
        var inputs = await store.ReadFrozenExecutionRawInputsAsync(replay.Execution.ExecutionId, PlanNode, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1, inputs.Count);

        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        var model = new SyntheticCalibrationModelV1 { Gain = 0, TemperatureC = -10 };
        var template = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var light = template.Descriptor with
        {
            Controls = template.Descriptor.Controls with
            {
                RequestedGain = model.Gain,
                EffectiveGain = model.Gain,
                TemperatureSetpointC = model.TemperatureC,
                EffectiveTemperatureC = model.TemperatureC
            },
            Profiles = template.Descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "synthetic-calibration-model",
                    model.SchemaVersion,
                    SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
            }
        };
        var bundle = (await new SyntheticCalibrationReferenceStore(options, new CameraAgentClearReferenceLoader(options))
            .GetOrCreateAsync(light, model, CancellationToken.None).ConfigureAwait(false)).LibraryBundle;
        _ = await provider.GetRequiredService<SqliteCalibrationLibraryStore>()
            .AdoptPublishedBundleAsync(bundle, CancellationToken.None).ConfigureAwait(false);
        return new PlanFixture(store, operations, replay.Execution.ExecutionId, lease, ContextFor(lease), inputs, bundle);
    }

    private static ProcessingExecutionContext ContextFor(ProcessingReplayLease lease)
        => new(
            lease.Execution.ExecutionId,
            ProcessingGraphExecutionClass.Replay,
            lease.Execution.GraphRevisionId,
            lease.Execution.LocalPlanIdentitySha256,
            false,
            lease.WorkId,
            lease.LeaseToken,
            lease.LeaseOwner,
            lease.Execution.DeadlineUtc,
            lease.ClaimCount);

    /// <summary>A valid plan for the frozen window that selects the bundle's masters at one gain of the ASI174 model.</summary>
    private static NormalizationPlanV1 PlanFor(
        IReadOnlyList<ProcessingFrozenRawInput> inputs,
        CalibrationLibraryBundleV1 bundle,
        double gain)
    {
        var response = Asi174PlanResponse();
        return new NormalizationPlanV1(
            NormalizationPlanV1.CurrentSchemaVersion,
            new string('A', 64),
            GainNormalizationPolicyV1.Approved,
            response.Descriptor,
            response.IdentitySha256,
            response.ResolveCoefficient(0).Coefficient!,
            new FrameLayoutDescriptor(
                2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, 0, 4095, 8)
            {
                Readout = new FrameReadoutDescriptor(2, 2, 0, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null),
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample
            },
            NormalizationKernelV1.Linear16(response.Descriptor),
            [.. inputs.Select((input, ordinal) => new NormalizationPlanInputV1(
                ordinal,
                input.WindowPosition,
                input.Descriptor.Capture.CaptureId,
                input.Descriptor.Artifact.ArtifactId,
                input.DescriptorSha256,
                input.PayloadSha256,
                gain,
                input.Descriptor.Controls.EffectiveExposure,
                input.Descriptor.Controls.EffectiveOffset,
                input.Descriptor.Controls.EffectiveTemperatureC,
                NormalizationPlanInputDispositions.Selected,
                null,
                FormattableString.Invariant($"gain-{gain}"),
                [.. CalibrationReferenceKinds.All.Select(kind => bundle.Artifacts.Single(artifact => artifact.Kind == kind))
                    .Select(static artifact => new NormalizationPlanMasterV1(
                        artifact.Kind, artifact.ArtifactId, artifact.PayloadSha256, artifact.Exposure))],
                response.ResolveCoefficient(gain).Coefficient))]);
    }

    /// <summary>A valid family whose only member names the bundle's masters but matches none of the fixture's captures.</summary>
    private static CalibrationFamilyV1 UnrelatedFamily(CalibrationLibraryBundleV1 bundle)
    {
        var layout = new FrameLayoutDescriptor(
            2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None, 0, 65535, 8)
        {
            Readout = new FrameReadoutDescriptor(2, 2, 0, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null),
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
        return new CalibrationFamilyV1(
            CalibrationFamilyV1.CurrentSchemaVersion,
            "unrelated-family",
            "another-agent",
            "another-rig",
            new string('B', 64),
            new string('C', 64),
            new ProfileIdentityDescriptor("synthetic-calibration-model", "synthetic-calibration-model-v1", new string('D', 64)),
            new string('E', 64),
            [new CalibrationFamilyMemberV1(
                "gain-0",
                0,
                null,
                layout,
                -10,
                -10,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                DateTimeOffset.UnixEpoch,
                null,
                new string('E', 64),
                [.. bundle.Artifacts.Select(static artifact =>
                    new CalibrationFamilyMasterV1(artifact.Kind, artifact.ArtifactId, artifact.PayloadSha256))])]);
    }

    private static GainResponse Asi174PlanResponse(string sensorVersion = "virtual-asi174mm-electron-domain-v2")
        => GainResponseResolver.Resolve(
            GainResponseSourceKind.VirtualAsi174MmModel,
            new SensorProfile(
                "VirtualAsi174Mm", 2, 2, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                SensorResponseMode.Monochrome, 64, SampleByteOrder.LittleEndian, sensorVersion),
            null).Response!;

    private static async Task<byte[]> ReadFrozenExecutionJsonAsync(string root, Guid executionId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT frozen_plan_json FROM processing_executions WHERE execution_id = $execution;";
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        return (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task WriteFrozenExecutionJsonAsync(string root, Guid executionId, byte[] json)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_executions SET frozen_plan_json = $json WHERE execution_id = $execution;";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static async Task SetBundleStateAsync(string root, string bundleId, string state)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE calibration_library_bundles SET publication_state = $state WHERE bundle_id = $bundle;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$bundle", bundleId);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private sealed record PlanFixture(
        SqliteCaptureProcessingStore Store,
        ProcessingGraphOperationsCoordinator Operations,
        Guid ExecutionId,
        ProcessingReplayLease Lease,
        ProcessingExecutionContext Context,
        IReadOnlyList<ProcessingFrozenRawInput> Inputs,
        CalibrationLibraryBundleV1 Bundle);
}
