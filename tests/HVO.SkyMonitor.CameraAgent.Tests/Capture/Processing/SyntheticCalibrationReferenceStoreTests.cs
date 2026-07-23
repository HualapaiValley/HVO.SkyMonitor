using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
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
            Assert.HasCount(5, Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).ToArray());

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
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
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
        foreach (var missingFile in new[] { "flat.bin", "calibration-profile.json" })
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
                _ = await firstStore.GetOrCreateAsync(template.Descriptor, model, CancellationToken.None).ConfigureAwait(false);
                var missingPath = Directory.EnumerateFiles(root, missingFile, SearchOption.AllDirectories).Single();
                File.Delete(missingPath);

                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await firstStore.GetOrCreateAsync(
                        template.Descriptor, model, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                var restartedStore = new SyntheticCalibrationReferenceStore(
                    options, new CameraAgentClearReferenceLoader(options));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await restartedStore.GetOrCreateAsync(
                        template.Descriptor, model, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
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
}
