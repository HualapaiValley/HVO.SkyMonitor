using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class SyntheticCalibrationReferenceStoreTests
{
    [TestMethod]
    public async Task GetOrCreateAsync_PersistsRestartStableReferencesConsumedByCanonicalRecipe()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = new SyntheticCalibrationModelV1
            {
                Gain = 99.5,
                TemperatureC = -9.5,
                Defects = [new SyntheticCalibrationDefect(1, 1)]
            };
            var ideal = new Linear16Frame(2, 2, 4, CameraPixelFormat.Mono16, Bytes([500, 500, 500, 500]));
            var corrupted = SyntheticCalibrationReferenceGenerator.ApplyToLight(
                ideal, TimeSpan.FromSeconds(1), model);
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, corrupted.ToArray());
            var descriptor = template.Descriptor with
            {
                Artifact = template.Descriptor.Artifact with
                {
                    ChecksumSha256 = PayloadChecksum.ComputeSha256(corrupted.Span)
                }
            };
            descriptor = WithSyntheticProfile(descriptor, model);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var loader = new CameraAgentClearReferenceLoader(options);
            var store = new SyntheticCalibrationReferenceStore(options, loader);

            var first = await store.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false);
            var restartedLoader = new CameraAgentClearReferenceLoader(options);
            var restartedStore = new SyntheticCalibrationReferenceStore(options, restartedLoader);
            var restarted = await restartedStore.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(first.ProfileIdentitySha256, restarted.ProfileIdentitySha256);
            CollectionAssert.AreEquivalent(
                first.References.Values.Select(static item => item.ArtifactId).ToArray(),
                restarted.References.Values.Select(static item => item.ArtifactId).ToArray());
            Assert.HasCount(4, first.ReferenceManifests);
            Assert.HasCount(4, first.ReferenceManifests.Values
                .Select(static manifest => manifest.Descriptor.Capture.CaptureSequence)
                .Distinct()
                .ToArray());
            foreach (var kind in CalibrationReferenceKinds.All)
            {
                Assert.AreEqual(
                    first.References[kind].ArtifactId,
                    first.ReferenceManifests[kind].Descriptor.Artifact.ArtifactId);
                Assert.AreEqual(FrameArtifactRole.Raw, first.ReferenceManifests[kind].Descriptor.Artifact.Role);
            }
            Assert.HasCount(4, await restartedLoader.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
            Assert.HasCount(4, Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).ToArray());
            Assert.HasCount(6, Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).ToArray());
            Assert.AreEqual(CalibrationLibraryBundleSources.SyntheticReferencesV1, first.LibraryBundle.Source);
            Assert.IsTrue(CalibrationLibraryContract.Validate(first.LibraryBundle).IsValid);
            var persistedBundle = Directory.EnumerateFiles(
                root, CalibrationLibraryEvidenceNames.BundleEnvelope, SearchOption.AllDirectories).Single();
            var persistedBundleJson = await File.ReadAllBytesAsync(persistedBundle).ConfigureAwait(false);
            Assert.IsNotNull(CalibrationLibraryContractJson.Parse(persistedBundleJson).Value);
            CollectionAssert.AreEqual(
                CalibrationLibraryContractJson.Serialize(first.LibraryBundle),
                persistedBundleJson);

            var light = new ProcessingArtifact(
                descriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                "source",
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType,
                descriptor.Layout,
                corrupted,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Controls.EffectiveExposure,
                CreateCompatibility(descriptor),
                descriptor.Capture.CaptureSequence,
                [],
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc,
                new ProcessingCaptureConditions(model.Gain, null, model.TemperatureC));
            var inputs = new List<ProcessingArtifact> { light };
            inputs.AddRange(CalibrationReferenceKinds.All.Select(kind => first.References[kind]));
            var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.ReferenceCalibration,
                JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
                ProcessingInputSelector.Raw("source"),
                inputs,
                "synthetic-corrected",
                AuxiliaryInputs: first.AuxiliaryInputs,
                InputArtifactId: light.ArtifactId)).ConfigureAwait(false);

            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            var values = Values(outcome.Products.Single().Payload.Span);
            Assert.IsTrue(values.All(static value => Math.Abs(value - 500) <= 2));
            CollectionAssert.AreEqual(
                new[] { light.ArtifactId }.Concat(CalibrationReferenceKinds.All.Select(kind => first.References[kind].ArtifactId)).ToArray(),
                outcome.Products.Single().SourceArtifactIds.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GetOrCreateAsync_BindsCompleteInputContextAndPublishesLinear16References()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = new SyntheticCalibrationModelV1();
            var baseline = CreateSyntheticDescriptor();
            var nativeDepth = baseline with
            {
                Layout = baseline.Layout with
                {
                    SampleDepthBits = 12,
                    WhiteLevel = 4095,
                    StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                    LevelCodeSpace = FrameLevelCodeSpace.NativeSample
                }
            };
            var differentReadout = baseline with
            {
                Layout = baseline.Layout with
                {
                    Readout = new FrameReadoutDescriptor(
                        4, 4, 0, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
                }
            };
            var differentAgent = baseline with
            {
                Capture = baseline.Capture with { AgentId = "another-agent" }
            };
            var differentProcessingProfile = baseline with
            {
                Profiles = baseline.Profiles with
                {
                    Processing = baseline.Profiles.Processing with { Version = "another-processing-version" }
                }
            };
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var store = new SyntheticCalibrationReferenceStore(
                options, new CameraAgentClearReferenceLoader(options));

            var bundles = new[]
            {
                await store.GetOrCreateAsync(baseline, model, CancellationToken.None).ConfigureAwait(false),
                await store.GetOrCreateAsync(nativeDepth, model, CancellationToken.None).ConfigureAwait(false),
                await store.GetOrCreateAsync(differentReadout, model, CancellationToken.None).ConfigureAwait(false),
                await store.GetOrCreateAsync(differentAgent, model, CancellationToken.None).ConfigureAwait(false),
                await store.GetOrCreateAsync(differentProcessingProfile, model, CancellationToken.None).ConfigureAwait(false)
            };

            Assert.HasCount(5, bundles.Select(static bundle => bundle.LibraryBundle.ProfileRelativePath)
                .Distinct(StringComparer.Ordinal).ToArray());
            var nativeBundle = bundles[1].LibraryBundle;
            Assert.IsTrue(CalibrationLibraryContract.Validate(nativeBundle).IsValid);
            Assert.AreEqual(nativeDepth.Layout, nativeBundle.Applicability.InputLayout);
            Assert.AreEqual(16, nativeBundle.Applicability.OutputLayout.SampleDepthBits);
            Assert.AreEqual(16, nativeBundle.Applicability.OutputLayout.ContainerDepthBits);
            Assert.AreEqual(FrameSamplePacking.ByteAligned, nativeBundle.Applicability.OutputLayout.Packing);
            Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, nativeBundle.Applicability.OutputLayout.StoredCodeTransform);
            Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, nativeBundle.Applicability.OutputLayout.LevelCodeSpace);
            Assert.IsTrue(nativeBundle.Artifacts.All(artifact =>
                bundles[1].ReferenceManifests[artifact.Kind].Descriptor.Layout == nativeBundle.Applicability.OutputLayout));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GetOrCreateAsync_RejectsCorruptPersistedReferenceWithoutReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = new SyntheticCalibrationModelV1 { Gain = 99.5, TemperatureC = -9.5 };
            var payload = SyntheticCalibrationReferenceGenerator.ApplyToLight(
                new Linear16Frame(2, 2, 4, CameraPixelFormat.Mono16, Bytes([500, 500, 500, 500])),
                TimeSpan.FromSeconds(1),
                model);
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload.ToArray());
            var descriptor = template.Descriptor with
            {
                Artifact = template.Descriptor.Artifact with
                {
                    ChecksumSha256 = PayloadChecksum.ComputeSha256(payload.Span)
                }
            };
            descriptor = WithSyntheticProfile(descriptor, model);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var firstStore = new SyntheticCalibrationReferenceStore(
                options, new CameraAgentClearReferenceLoader(options));
            _ = await firstStore.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false);
            var biasPath = Directory.EnumerateFiles(root, "bias.bin", SearchOption.AllDirectories).Single();
            var expectedBias = await File.ReadAllBytesAsync(biasPath).ConfigureAwait(false);
            await File.WriteAllBytesAsync(biasPath, [0, 1, 2, 3]).ConfigureAwait(false);
            var restartedStore = new SyntheticCalibrationReferenceStore(
                options, new CameraAgentClearReferenceLoader(options));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await firstStore.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CalibrationPublicationConflictException>(async () =>
                await restartedStore.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3 }, await File.ReadAllBytesAsync(biasPath).ConfigureAwait(false));
            await File.WriteAllBytesAsync(biasPath, expectedBias).ConfigureAwait(false);
            var recovered = await restartedStore.GetOrCreateAsync(
                descriptor, model, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(4, recovered.References);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GetOrCreateAsync_RejectsMissingMemberOfCommittedBundle()
    {
        foreach (var missingFile in new[]
                 {
                     "flat.bin",
                     CalibrationLibraryEvidenceNames.BundleEnvelope,
                     CalibrationLibraryEvidenceNames.ProfileMarker
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var model = new SyntheticCalibrationModelV1();
                var template = ReconstructableCaptureContractTests.CreateManifest(
                    CameraPixelFormat.Mono16, 2, 2, 4, Bytes([500, 500, 500, 500]));
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
                var firstStore = new SyntheticCalibrationReferenceStore(
                    options, new CameraAgentClearReferenceLoader(options));
                var descriptor = WithSyntheticProfile(template.Descriptor, model);
                _ = await firstStore.GetOrCreateAsync(descriptor, model, CancellationToken.None).ConfigureAwait(false);
                var missingPath = Directory.EnumerateFiles(root, missingFile, SearchOption.AllDirectories).Single();
                File.Delete(missingPath);

                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await firstStore.GetOrCreateAsync(
                        descriptor, model, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                var restartedStore = new SyntheticCalibrationReferenceStore(
                    options, new CameraAgentClearReferenceLoader(options));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await restartedStore.GetOrCreateAsync(
                        descriptor, model, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(missingPath));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    [TestMethod]
    public async Task GetOrCreateAsync_PreCancelledRequestDoesNotPublishEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, Bytes([500, 500, 500, 500]));
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var store = new SyntheticCalibrationReferenceStore(options, new CameraAgentClearReferenceLoader(options));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.GetOrCreateAsync(
                    template.Descriptor, new SyntheticCalibrationModelV1(), cancellation.Token).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.IsEmpty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(CalibrationPublicationFaultPoint.AfterPayloadPublished, 1, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterPayloadPublished, 2, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterPayloadPublished, 3, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterPayloadPublished, 4, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterManifestPublished, 1, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterManifestPublished, 2, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterManifestPublished, 3, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterManifestPublished, 4, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.BeforeBundleWrite, 1, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterBundlePublished, 1, 0, 1)]
    [DataRow(CalibrationPublicationFaultPoint.AfterProfilePublished, 1, 1, 0)]
    [DataRow(CalibrationPublicationFaultPoint.DirectorySynced, 1, 1, 0)]
    public async Task GetOrCreateAsync_EnvelopeFaultConvergesByAdoptionOrQuarantine(
        CalibrationPublicationFaultPoint faultPoint,
        int occurrence,
        int expectedAdopted,
        int expectedQuarantined)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var library = new SqliteCalibrationLibraryStore(
                       new InitializedIngress(), options, TimeProvider.System))
            {
                var publisher = new CalibrationArtifactPublisher(
                    options, new OneShotFaultInjector(faultPoint, occurrence));
                var store = new SyntheticCalibrationReferenceStore(
                    options, new CameraAgentClearReferenceLoader(options), publisher, library);

                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await store.GetOrCreateAsync(
                        CreateSyntheticDescriptor(),
                        new SyntheticCalibrationModelV1(),
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }

            using var restarted = new SqliteCalibrationLibraryStore(
                new InitializedIngress(), options, TimeProvider.System);
            var summary = await new CalibrationLibraryReconciler(restarted, options)
                .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(expectedAdopted, summary.Adopted);
            Assert.AreEqual(expectedQuarantined, summary.Quarantined);
            Assert.AreEqual(0, summary.Failed);
            Assert.HasCount(expectedAdopted, await restarted.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            if (faultPoint is CalibrationPublicationFaultPoint.BeforeBundleWrite or
                CalibrationPublicationFaultPoint.AfterBundlePublished)
            {
                var scenarioId = faultPoint == CalibrationPublicationFaultPoint.BeforeBundleWrite
                    ? "calibration-before-bundle-write"
                    : "calibration-bundle-publication";
                await Phase14ScenarioEvidence.RecordAsync(
                    scenarioId,
                    $"synthetic-{faultPoint}",
                    faultPoint.ToString(),
                    ["bundle-boundary-fault-observed", "uncommitted-bundle-not-adopted", "restart-quarantined-remnants"])
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GetOrCreateAsync_AfterSqliteFaultReplaysCanonicalBundleOnRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-synthetic-calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var library = new SqliteCalibrationLibraryStore(
                       new InitializedIngress(), options, TimeProvider.System))
            {
                var publisher = new CalibrationArtifactPublisher(
                    options, new OneShotFaultInjector(CalibrationPublicationFaultPoint.AfterSqlitePublication));
                var store = new SyntheticCalibrationReferenceStore(
                    options, new CameraAgentClearReferenceLoader(options), publisher, library);

                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await store.GetOrCreateAsync(
                        CreateSyntheticDescriptor(),
                        new SyntheticCalibrationModelV1(),
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.HasCount(1, await library.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            }

            using var restarted = new SqliteCalibrationLibraryStore(
                new InitializedIngress(), options, TimeProvider.System);
            var summary = await new CalibrationLibraryReconciler(restarted, options)
                .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, summary.Adopted);
            Assert.AreEqual(0, summary.Failed);
            Assert.HasCount(1, await restarted.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ReconstructionDescriptor CreateSyntheticDescriptor()
    {
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, Bytes([500, 500, 500, 500]));
        var model = new SyntheticCalibrationModelV1();
        return WithSyntheticProfile(template.Descriptor with
        {
            Controls = template.Descriptor.Controls with
            {
                RequestedGain = 100,
                EffectiveGain = 100,
                TemperatureSetpointC = -10,
                EffectiveTemperatureC = -10
            }
        }, model);
    }

    private static ReconstructionDescriptor WithSyntheticProfile(
        ReconstructionDescriptor descriptor,
        SyntheticCalibrationModelV1 model)
        => descriptor with
        {
            Profiles = descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "synthetic-calibration-model",
                    model.SchemaVersion,
                    SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
            }
        };

    private static ProcessingCompatibilityIdentity CreateCompatibility(ReconstructionDescriptor descriptor)
        => new(
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Calibration.Sha256,
            descriptor.Profiles.Mask.Sha256,
            descriptor.Profiles.Sensor.Sha256,
            "synthetic-test",
            descriptor.Profiles.Processing.Sha256);

    private static byte[] Bytes(IReadOnlyList<ushort> values)
    {
        var bytes = new byte[values.Count * 2];
        for (var index = 0; index < values.Count; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return bytes;
    }

    private static ushort[] Values(ReadOnlySpan<byte> bytes)
    {
        var values = new ushort[bytes.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(bytes[index * 2] | bytes[index * 2 + 1] << 8);
        }
        return values;
    }

    private sealed class OneShotFaultInjector(CalibrationPublicationFaultPoint target, int occurrence = 1)
        : ICalibrationPublicationFaultInjector
    {
        private int _observed;

        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point == target && Interlocked.Increment(ref _observed) == occurrence)
            {
                throw new IOException($"Injected calibration publication fault at {point}: {relativePath}");
            }
        }
    }

    private sealed class InitializedIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
