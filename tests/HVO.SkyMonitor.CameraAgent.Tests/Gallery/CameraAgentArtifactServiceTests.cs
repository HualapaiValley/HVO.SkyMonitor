using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CameraAgentArtifactServiceTests
{
    [TestMethod]
    public async Task ValidRawAndDerivedContentReturnsExactCommittedBytesAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);

        var rawResult = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, rawResult.Status);
        var rawContent = rawResult.Content!;
        await using (rawContent.ConfigureAwait(false))
        {
            CollectionAssert.AreEqual(raw.Payload, await ReadAllAsync(rawContent).ConfigureAwait(false));
            Assert.AreEqual(raw.ChecksumSha256, rawContent.ChecksumSha256);
            Assert.AreEqual(FrameArtifactRole.Raw, rawContent.Role);
        }
        var previewResult = await fixture.Service.OpenContentAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, previewResult.Status);
        var previewContent = previewResult.Content!;
        await using (previewContent.ConfigureAwait(false))
        {
            CollectionAssert.AreEqual(preview.Payload, await ReadAllAsync(previewContent).ConfigureAwait(false));
            Assert.AreEqual(preview.ChecksumSha256, previewContent.ChecksumSha256);
            Assert.AreEqual(FrameArtifactRole.Preview, previewContent.Role);
        }
    }

    [TestMethod]
    public async Task MissingAndCorruptEvidenceFailsClosedAsync()
    {
        await AssertStatusAfterMutationAsync(
            static fixture =>
            {
                File.Delete(fixture.Raw!.SidecarPath);
                return Task.CompletedTask;
            },
            CameraAgentArtifactReadStatus.Gone).ConfigureAwait(false);
        await AssertStatusAfterMutationAsync(
            static fixture => fixture.ExecuteAsync("UPDATE raw_captures SET payload_length = payload_length + 1;"),
            CameraAgentArtifactReadStatus.Conflict).ConfigureAwait(false);
        await AssertStatusAfterMutationAsync(
            static fixture => fixture.ExecuteAsync($"UPDATE raw_captures SET payload_sha256 = '{new string('0', 64)}';"),
            CameraAgentArtifactReadStatus.Conflict).ConfigureAwait(false);
        await AssertStatusAfterMutationAsync(
            static fixture => File.WriteAllTextAsync(fixture.Raw!.SidecarPath, "{}"),
            CameraAgentArtifactReadStatus.Conflict).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TraversalAndSymbolicLinkEvidenceIsRejectedAsync()
    {
        using (var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false))
        {
            var raw = await fixture.AddRawAsync().ConfigureAwait(false);
            await fixture.ExecuteAsync("UPDATE raw_captures SET payload_relative_path = '../outside.bin';").ConfigureAwait(false);
            var result = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, result.Status);
        }

        using (var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false))
        {
            var raw = await fixture.AddRawAsync().ConfigureAwait(false);
            var outside = Path.Combine(Path.GetTempPath(), $"hvo-artifact-outside-{Guid.NewGuid():N}.bin");
            await File.WriteAllBytesAsync(outside, raw.Payload).ConfigureAwait(false);
            try
            {
                File.Delete(raw.PayloadPath);
                File.CreateSymbolicLink(raw.PayloadPath, outside);
                var result = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, result.Status);
            }
            finally
            {
                File.Delete(outside);
            }
        }
    }

    [TestMethod]
    public async Task SlowContentLeaseDoesNotHoldRootLifecycleLockAndPinnedHandleRemainsReadableAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var result = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);

        var lifecycleGate = StorageLifecycleLock.ForRoot(fixture.Root);
        await lifecycleGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try
        {
            File.Delete(raw.PayloadPath);
        }
        finally
        {
            lifecycleGate.Release();
        }

        var content = result.Content!;
        await using (content.ConfigureAwait(false))
        {
            CollectionAssert.AreEqual(raw.Payload, await ReadAllAsync(content).ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task ValidatedMetadataCacheAvoidsConditionalHeadAndRangeRescansAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);

        for (var request = 0; request < 4; request++)
        {
            var result = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            var content = result.Content!;
            await using (content.ConfigureAwait(false))
            {
                if (request == 3)
                {
                    content.Position = 2;
                    var range = new byte[3];
                    await content.ReadExactlyAsync(range).ConfigureAwait(false);
                    CollectionAssert.AreEqual(raw.Payload[2..5], range);
                }
            }
        }

        Assert.AreEqual(1L, fixture.Service.EvidenceValidationReads);
        Assert.AreEqual(4L, fixture.Service.PayloadValidationReads);
        var lifecycleGate = StorageLifecycleLock.ForRoot(fixture.Root);
        await lifecycleGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        lifecycleGate.Release();
    }

    [TestMethod]
    public async Task ValidationCacheRehashesWhenPayloadChangesWithoutMetadataIdentityChangeAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var first = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        await first.Content!.DisposeAsync().ConfigureAwait(false);
        var lastWriteUtc = File.GetLastWriteTimeUtc(raw.PayloadPath);
        await File.WriteAllBytesAsync(raw.PayloadPath, raw.Payload.Select(static value => (byte)(value ^ 0xFF)).ToArray())
            .ConfigureAwait(false);
        File.SetLastWriteTimeUtc(raw.PayloadPath, lastWriteUtc);

        var corrupt = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, corrupt.Status);
        Assert.AreEqual(1L, fixture.Service.EvidenceValidationReads);
        Assert.AreEqual(2L, fixture.Service.PayloadValidationReads);
    }

    [TestMethod]
    public async Task PreviewAllowsReconstructableRolesAndCachesByChecksumAsync()
    {
        var encoder = new CountingPreviewEncoder();
        using var fixture = await ArtifactFixture.CreateAsync(encoder: encoder).ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
        var calibrated = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Calibrated).ConfigureAwait(false);
        var combined = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined).ConfigureAwait(false);
        var metadata = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Metadata).ConfigureAwait(false);
        var unsupported = await fixture.AddOutputAsync(
            raw.Manifest,
            FrameArtifactRole.AnnotatedPreview,
            mediaType: "text/plain").ConfigureAwait(false);

        var first = await fixture.Service.GetPreviewAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Service.GetPreviewAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var rawPreview = await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var calibratedPreview = await fixture.Service.GetPreviewAsync(calibrated.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var combinedPreview = await fixture.Service.GetPreviewAsync(combined.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var metadataPreview = await fixture.Service.GetPreviewAsync(metadata.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var unsupportedPreview = await fixture.Service.GetPreviewAsync(unsupported.ArtifactId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, first.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, second.Status);
        CollectionAssert.AreEqual(first.Content.ToArray(), second.Content.ToArray());
        Assert.AreEqual(2, encoder.Count);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, rawPreview.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, calibratedPreview.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, combinedPreview.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.UnsupportedMediaType, metadataPreview.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.UnsupportedMediaType, unsupportedPreview.Status);
    }

    [TestMethod]
    public async Task PreviewUsesExistingSkiaEncoderAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);

        var result = await fixture.Service.GetPreviewAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
        Assert.IsGreaterThan(4, result.Content.Length);
        Assert.AreEqual((byte)0xFF, result.Content.Span[0]);
        Assert.AreEqual((byte)0xD8, result.Content.Span[1]);

        var bayerPayload = Enumerable.Range(0, 16)
            .SelectMany(static value => new[] { (byte)value, (byte)0 })
            .ToArray();
        var bayer = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.BayerRggb16, 4, 4, 8, bayerPayload);
        var downsampled = CameraAgentPreviewEncoder.Downsample(bayer.Descriptor.Layout, bayerPayload, 2);
        Assert.AreEqual(2, downsampled.Width);
        Assert.AreEqual(2, downsampled.Height);
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 3, 0, 12, 0, 15, 0 },
            downsampled.Payload.ToArray());
        var onePixel = CameraAgentPreviewEncoder.Downsample(bayer.Descriptor.Layout, bayerPayload, 1);
        Assert.AreEqual(1, onePixel.Width);
        Assert.AreEqual(1, onePixel.Height);
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, onePixel.Payload.ToArray());
    }

    [TestMethod]
    public async Task PreviewDimensionsBytesAndConcurrencyAreBoundedAsync()
    {
        var smallOptions = new ArtifactReadOptions { MaximumPreviewDimension = 1 };
        using (var fixture = await ArtifactFixture.CreateAsync(artifactRead: smallOptions).ConfigureAwait(false))
        {
            var raw = await fixture.AddRawAsync().ConfigureAwait(false);
            var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
            var result = await fixture.Service.GetPreviewAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            Assert.AreEqual(1, result.Width);
            Assert.AreEqual(1, result.Height);
        }

        using (var fixture = await ArtifactFixture.CreateAsync(
                   artifactRead: new ArtifactReadOptions { MaximumPreviewSourceBytes = 3 }).ConfigureAwait(false))
        {
            var raw = await fixture.AddRawAsync().ConfigureAwait(false);
            var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
            var result = await fixture.Service.GetPreviewAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.TooLarge, result.Status);
        }

        using (var fixture = await ArtifactFixture.CreateAsync(
                   artifactRead: new ArtifactReadOptions { MaximumPreviewEncodedBytes = 3 },
                   encoder: new CountingPreviewEncoder()).ConfigureAwait(false))
        {
            var raw = await fixture.AddRawAsync().ConfigureAwait(false);
            var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
            var result = await fixture.Service.GetPreviewAsync(preview.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.TooLarge, result.Status);
        }

        var blockingEncoder = new BlockingPreviewEncoder();
        using (var fixture = await ArtifactFixture.CreateAsync(
                   artifactRead: new ArtifactReadOptions { MaximumConcurrentPreviews = 1 },
                   encoder: blockingEncoder).ConfigureAwait(false))
        {
            var raw = await fixture.AddRawAsync().ConfigureAwait(false);
            var preview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
            var first = Task.Run(async () => await fixture.Service.GetPreviewAsync(
                preview.ArtifactId, CancellationToken.None).ConfigureAwait(false));
            await blockingEncoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var queued = Enumerable.Range(0, 49).Select(_ => fixture.Service.GetPreviewAsync(
                preview.ArtifactId, CancellationToken.None).AsTask()).ToArray();
            Assert.IsTrue(queued.All(static request => !request.IsCompleted));
            blockingEncoder.Release.TrySetResult();
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, (await first.ConfigureAwait(false)).Status);
            var completed = await Task.WhenAll(queued).ConfigureAwait(false);
            Assert.IsTrue(completed.All(static result => result.Status == CameraAgentArtifactReadStatus.Found));
            Assert.AreEqual(1, blockingEncoder.Count);
            Assert.HasCount(1, completed.Select(static result => result.ChecksumSha256).Distinct().ToArray());
        }

        var concurrentEncoder = new BlockingPreviewEncoder(requiredEntrants: 2);
        using (var fixture = await ArtifactFixture.CreateAsync(
                   artifactRead: new ArtifactReadOptions { MaximumConcurrentPreviews = 2 },
                   encoder: concurrentEncoder).ConfigureAwait(false))
        {
            var firstRaw = await fixture.AddRawAsync().ConfigureAwait(false);
            var firstPreview = await fixture.AddOutputAsync(firstRaw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
            var secondRaw = await fixture.AddRawAsync().ConfigureAwait(false);
            var secondPreview = await fixture.AddOutputAsync(
                secondRaw.Manifest, FrameArtifactRole.Preview, payloadSeed: 20).ConfigureAwait(false);
            var first = Task.Run(async () => await fixture.Service.GetPreviewAsync(
                firstPreview.ArtifactId, CancellationToken.None).ConfigureAwait(false));
            var second = Task.Run(async () => await fixture.Service.GetPreviewAsync(
                secondPreview.ArtifactId, CancellationToken.None).ConfigureAwait(false));

            await concurrentEncoder.RequiredEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var thirdRaw = await fixture.AddRawAsync().ConfigureAwait(false);
            var thirdPreview = await fixture.AddOutputAsync(
                thirdRaw.Manifest, FrameArtifactRole.Preview, payloadSeed: 30).ConfigureAwait(false);
            using var queuedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await fixture.Service.GetPreviewAsync(thirdPreview.ArtifactId, queuedCancellation.Token).ConfigureAwait(false))
                .ConfigureAwait(false);
            concurrentEncoder.Release.TrySetResult();
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, (await first.ConfigureAwait(false)).Status);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, (await second.ConfigureAwait(false)).Status);
        }
    }

    private static async Task AssertStatusAfterMutationAsync(
        Func<ArtifactFixture, Task> mutate,
        CameraAgentArtifactReadStatus expected)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        await mutate(fixture).ConfigureAwait(false);
        var result = await fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expected, result.Status);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory).ConfigureAwait(false);
        return memory.ToArray();
    }

    private sealed class ArtifactFixture : IDisposable
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        private readonly SqliteRawCaptureJournal _journal;
        private readonly SqliteCaptureProcessingStore _processingStore;

        private ArtifactFixture(
            string root,
            SqliteRawCaptureJournal journal,
            SqliteCaptureProcessingStore processingStore,
            CameraAgentArtifactService service)
        {
            Root = root;
            _journal = journal;
            _processingStore = processingStore;
            Service = service;
        }

        internal string Root { get; }

        internal CameraAgentArtifactService Service { get; }

        internal StoredArtifact? Raw { get; private set; }

        internal static async Task<ArtifactFixture> CreateAsync(
            ArtifactReadOptions? artifactRead = null,
            ICameraAgentPreviewEncoder? encoder = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-artifact-reader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                ArtifactRead = artifactRead ?? new ArtifactReadOptions()
            });
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var processingStore = new SqliteCaptureProcessingStore(options);
            await processingStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var service = new CameraAgentArtifactService(
                options,
                processingStore,
                encoder ?? new CameraAgentPreviewEncoder());
            return new ArtifactFixture(root, journal, processingStore, service);
        }

        internal async Task<StoredArtifact> AddRawAsync()
        {
            var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
            var captureId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var identity = await _journal.ReserveIdentityAsync(
                "artifact-reader", captureId, artifactId, CancellationToken.None).ConfigureAwait(false);
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var descriptor = template.Descriptor with
            {
                Capture = template.Descriptor.Capture with
                {
                    AgentId = identity.AgentId,
                    CaptureSequence = identity.CaptureSequence,
                    CaptureId = identity.CaptureId
                },
                Artifact = template.Descriptor.Artifact with
                {
                    ArtifactId = identity.ArtifactId,
                    MediaType = "application/x-skymonitor-mono16",
                    ChecksumSha256 = PayloadChecksum.ComputeSha256(payload)
                }
            };
            var relativePath = $"frames/{artifactId:N}.bin";
            var sidecarRelativePath = $"frames/{artifactId:N}.json";
            var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
            var sidecar = CaptureContractJson.Serialize(manifest);
            var payloadPath = Path.Combine(Root, relativePath);
            var sidecarPath = Path.Combine(Root, sidecarRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sidecarPath, sidecar).ConfigureAwait(false);
            await _journal.CommitAsync(new RawIngressJournalEntry(
                identity.AgentId,
                identity.CaptureSequence,
                identity.CaptureId,
                identity.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeManifestSha256(sidecar),
                descriptor.Artifact.ChecksumSha256,
                descriptor.Layout.ByteLength,
                relativePath,
                sidecarRelativePath,
                sidecar,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.DurableIngressUtc), CancellationToken.None).ConfigureAwait(false);
            Raw = new StoredArtifact(
                artifactId,
                payload,
                descriptor.Artifact.ChecksumSha256,
                payloadPath,
                sidecarPath,
                manifest);
            return Raw;
        }

        internal async Task<StoredArtifact> AddOutputAsync(
            ArtifactManifestV2 raw,
            FrameArtifactRole role,
            byte payloadSeed = 10,
            string mediaType = "application/x-hvo-packed-image")
        {
            var payload = new byte[] { payloadSeed, 20, 30, 40 };
            var recipe = RecipeIdentityDescriptor.Create(
                "artifact-reader-preview",
                "1.0.0",
                "artifact-reader-preview-v1",
                JsonSerializer.SerializeToElement(new { }));
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                role,
                "reader",
                recipeIdentity,
                [raw.Descriptor.Artifact.ArtifactId]);
            var artifactId = ProcessingIdentity.CreateArtifactId(outputIdentity);
            var layout = raw.Descriptor.Layout with
            {
                StrideBytes = 2,
                PixelFormat = CameraPixelFormat.Mono8,
                ByteOrder = FrameByteOrder.NotApplicable,
                SampleDepthBits = 8,
                ContainerDepthBits = 8,
                CfaPattern = ColorFilterArrayPattern.None,
                BlackLevel = 0,
                WhiteLevel = 255,
                ByteLength = payload.Length
            };
            var artifact = new ArtifactDescriptor(
                artifactId,
                role,
                "preview",
                "reader",
                raw.Descriptor.Timing.ReadoutCompletedUtc,
                [raw.Descriptor.Artifact.ArtifactId],
                recipe,
                mediaType,
                PayloadChecksum.ComputeSha256(payload));
            var descriptor = raw.Descriptor with { Layout = layout, Artifact = artifact };
            var relativePath = $"derived/{artifactId:N}.bin";
            var sidecarRelativePath = $"derived/{artifactId:N}.json";
            var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
            var sidecar = CaptureContractJson.Serialize(manifest);
            var payloadPath = Path.Combine(Root, relativePath);
            var sidecarPath = Path.Combine(Root, sidecarRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sidecarPath, sidecar).ConfigureAwait(false);

            using var connection = await OpenAsync().ConfigureAwait(false);
            using (var node = connection.CreateCommand())
            {
                node.CommandText = """
                    INSERT INTO processing_nodes(
                        capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                        output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                    VALUES ($capture, $node, 1, '[]', $recipe, $role, 'reader', $plan,
                            'Completed', NULL, 1, $completed);
                    """;
                node.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
                node.Parameters.AddWithValue("$node", role.ToString());
                node.Parameters.AddWithValue("$recipe", recipe.Name);
                node.Parameters.AddWithValue("$role", role.ToString());
                node.Parameters.AddWithValue("$plan", new string('A', 64));
                node.Parameters.AddWithValue("$completed", raw.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds());
                await node.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var output = connection.CreateCommand())
            {
                output.CommandText = """
                    INSERT INTO processing_outputs(
                        output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                        payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                        algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                        legacy_recipe_version, committed_unix_ms)
                    VALUES ($identity, $capture, $agent, $node, $artifact, $role, 'reader',
                            $payload, $sidecar, $descriptor, $recipe, $algorithms, $compatibility,
                            0, $sequence, NULL, $committed);
                    """;
                output.Parameters.AddWithValue("$identity", outputIdentity);
                output.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
                output.Parameters.AddWithValue("$agent", raw.Descriptor.Capture.AgentId);
                output.Parameters.AddWithValue("$node", role.ToString());
                output.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                output.Parameters.AddWithValue("$role", role.ToString());
                output.Parameters.AddWithValue("$payload", relativePath);
                output.Parameters.AddWithValue("$sidecar", sidecarRelativePath);
                output.Parameters.AddWithValue("$descriptor", sidecar);
                output.Parameters.AddWithValue("$recipe", recipeIdentity);
                output.Parameters.AddWithValue("$algorithms", JsonSerializer.SerializeToUtf8Bytes(
                    new[] { new ProcessingAlgorithmIdentity("preview", "v1") }, WebJson));
                output.Parameters.AddWithValue("$compatibility", JsonSerializer.SerializeToUtf8Bytes(
                    new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"),
                    WebJson));
                output.Parameters.AddWithValue("$sequence", raw.Descriptor.Capture.CaptureSequence);
                output.Parameters.AddWithValue("$committed", raw.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds());
                await output.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            return new StoredArtifact(
                artifactId,
                payload,
                artifact.ChecksumSha256,
                payloadPath,
                sidecarPath,
                manifest);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed test statements are supplied by this fixture.")]
        internal async Task ExecuteAsync(string sql)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }

        public void Dispose()
        {
            Service.Dispose();
            _processingStore.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record StoredArtifact(
        Guid ArtifactId,
        byte[] Payload,
        string ChecksumSha256,
        string PayloadPath,
        string SidecarPath,
        ArtifactManifestV2 Manifest);

    private class CountingPreviewEncoder : ICameraAgentPreviewEncoder
    {
        public int Count { get; private set; }

        public virtual CameraAgentEncodedPreview Encode(
            FrameLayoutDescriptor layout,
            ReadOnlyMemory<byte> payload,
            int maximumDimension)
        {
            Count++;
            var scale = Math.Min(1d, Math.Min(
                (double)maximumDimension / layout.Width,
                (double)maximumDimension / layout.Height));
            return new(
                [0xFF, 0xD8, .. payload.ToArray(), 0xFF, 0xD9],
                Math.Max(1, (int)Math.Floor(layout.Width * scale)),
                Math.Max(1, (int)Math.Floor(layout.Height * scale)));
        }
    }

    private sealed class BlockingPreviewEncoder(int requiredEntrants = 1) : CountingPreviewEncoder
    {
        private int _entered;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource RequiredEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override CameraAgentEncodedPreview Encode(
            FrameLayoutDescriptor layout,
            ReadOnlyMemory<byte> payload,
            int maximumDimension)
        {
            Entered.TrySetResult();
            if (Interlocked.Increment(ref _entered) >= requiredEntrants)
            {
                RequiredEntered.TrySetResult();
            }
            Release.Task.GetAwaiter().GetResult();
            return base.Encode(layout, payload, maximumDimension);
        }
    }
}
