using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

public sealed partial class CameraAgentArtifactServiceTests
{
    [TestMethod]
    public async Task RecordedComparisonPolicyUsesOwnPixelsAndLeavesDefaultAndRawEvidenceUnchangedAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (payload, probe) = CreateCombinedFixtureFrame();
        var raw = await fixture.AddRawAsync(payload, 100, 100).ConfigureAwait(false);
        var input = ProcessingInput(raw);
        var normalized = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.LinearNormalization, JsonSerializer.SerializeToElement(new LinearNormalizationOptions()),
            ProcessingInputSelector.Raw(input.Variant), [input], "none", InputArtifactId: input.ArtifactId), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, normalized.Status);
        var product = normalized.Products.Single();
        var calibrated = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Calibrated,
            mediaType: product.MediaType, width: 100, height: 100, pixelFormat: CameraPixelFormat.Mono16,
            payload: product.Payload.ToArray(), recipeOverride: product.Recipe.Descriptor, variant: "none").ConfigureAwait(false);
        var combined = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined,
            width: 100, height: 100, pixelFormat: CameraPixelFormat.Mono16, payload: payload,
            mediaType: "application/x-hvo-linear-frame").ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, combined, new(0.5, 0.9997, 8, OutputEncoding: "Packed")).ConfigureAwait(false);

        var baseline = await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var expectedBaseline = new CameraAgentPreviewEncoder().Encode(raw.Manifest.Descriptor.Layout, payload, 2048,
            16 * 1024 * 1024, CancellationToken.None);
        CollectionAssert.AreEqual(expectedBaseline.Content, baseline.Content.ToArray());
        var rawPreview = await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        var calPreview = await fixture.Service.GetPreviewAsync(calibrated.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        var retained = await fixture.Service.GetPreviewAsync(reference.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, rawPreview.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, calPreview.Status);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, retained.Status);
        Assert.AreEqual(rawPreview.ChecksumSha256, calPreview.ChecksumSha256);
        Assert.AreEqual(rawPreview.ChecksumSha256, retained.ChecksumSha256, "Identical linear pixels reproduce D with one transfer.");
        Assert.AreEqual(rawPreview.DisplayPolicyIdentity, calPreview.DisplayPolicyIdentity);
        Assert.AreNotEqual(baseline.ChecksumSha256, rawPreview.ChecksumSha256);
        StringAssert.Contains(rawPreview.DisplayPolicy!, "black=0.5 white=0.9997 asinh=8", StringComparison.Ordinal);
        StringAssert.Contains(rawPreview.DisplayPolicy!, "not a locked transfer curve", StringComparison.Ordinal);
        Assert.IsLessThanOrEqualTo(8, JpegImageCodec.DecodeJpeg(baseline.Content).PixelData.Span[probe]);
        Assert.IsGreaterThanOrEqualTo(100, JpegImageCodec.DecodeJpeg(rawPreview.Content).PixelData.Span[probe]);
        Assert.AreEqual(baseline.ChecksumSha256,
            (await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None).ConfigureAwait(false)).ChecksumSha256);

        // A different Calibrated frame must not quietly display the Combined frame's pixels.
        var changed = payload.ToArray();
        Array.Clear(changed, 9_000 * 2, 990 * 2);
        var differentCal = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Calibrated,
            width: 100, height: 100, pixelFormat: CameraPixelFormat.Mono16, payload: changed, variant: "different").ConfigureAwait(false);
        var different = await fixture.Service.GetPreviewAsync(differentCal.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, different.Status);
        Assert.AreNotEqual(rawPreview.ChecksumSha256, different.ChecksumSha256);
        Assert.IsLessThanOrEqualTo(8, JpegImageCodec.DecodeJpeg(different.Content).PixelData.Span[probe]);

        foreach (var artifact in new[] { raw, calibrated, combined, reference })
        {
            var read = await fixture.Service.OpenContentAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, read.Status);
            var content = read.Content!;
            await using (content.ConfigureAwait(false))
            {
                CollectionAssert.AreEqual(artifact.Payload, await ReadAllAsync(content).ConfigureAwait(false));
                Assert.AreEqual(artifact.ChecksumSha256, content.ChecksumSha256);
            }
        }
        CollectionAssert.AreEqual(raw.Payload, calibrated.Payload);

        var current = new CameraAgentCurrentImagePresentationService(fixture.Gallery,
            new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions())), fixture.Service,
            new ComparisonRuntime(), new NoStructuredLayers(), TimeProvider.System);
        var projection = await current.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(raw.Manifest.Descriptor.Capture.CaptureId, projection.DisplayCapture!.CaptureId);
        foreach (var slot in projection.Stages.Where(static slot => slot.Stage != CameraAgentPresentationStage.Annotated))
        {
            Assert.AreEqual(reference.ArtifactId, slot.DisplayReferenceId);
            StringAssert.Contains(slot.DisplayPolicy!, "black=0.5 white=0.9997 asinh=8", StringComparison.Ordinal);
        }
        File.Delete(reference.PayloadPath);
        var fallback = await current.GetAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var slot in fallback.Stages.Where(static slot => slot.Stage != CameraAgentPresentationStage.Annotated))
        {
            Assert.IsNull(slot.DisplayReferenceId);
            Assert.AreEqual(slot.ArtifactId, slot.DisplayArtifactId);
            Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, slot.Availability);
            StringAssert.Contains(slot.DisplayPolicy!, "global default", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8)]
    [DataRow(CameraPixelFormat.Rgb24)]
    public async Task EightBitComparisonUsesRecordedPolicyWithoutApplyingNonlinearTransferAsync(CameraPixelFormat format)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var pixels = Enumerable.Repeat((byte)80, 4 * ImageLayout.BytesPerPixel(format)).ToArray();
        var source = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined, pixelFormat: format, payload: pixels).ConfigureAwait(false);
        var calibrated = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Calibrated, pixelFormat: format, payload: pixels).ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, source, new(0.3, 0.8, 9, OutputEncoding: "Packed")).ConfigureAwait(false);
        var baseline = await fixture.Service.GetPreviewAsync(calibrated.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var comparison = await fixture.Service.GetPreviewAsync(calibrated.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, comparison.Status);
        Assert.AreEqual(baseline.ChecksumSha256, comparison.ChecksumSha256);
        Assert.AreEqual(CameraAgentPreviewOperation.EncodeOnly, baseline.Operation);
        Assert.AreEqual(CameraAgentPreviewOperation.EncodeOnly, comparison.Operation);
        CollectionAssert.AreEqual(baseline.Content.ToArray(), comparison.Content.ToArray());
    }

    [TestMethod]
    [DataRow("wrong-capture")]
    [DataRow("cross-capture-source")]
    [DataRow("wrong-role")]
    [DataRow("wrong-recipe")]
    [DataRow("wrong-version")]
    [DataRow("wrong-input-role")]
    [DataRow("wrong-input-recipe")]
    [DataRow("wrong-input-variant")]
    [DataRow("wrong-dimensions")]
    [DataRow("wrong-layout")]
    [DataRow("black-white")]
    [DataRow("white-out-of-bounds")]
    [DataRow("strength-out-of-bounds")]
    [DataRow("nonfinite")]
    [DataRow("missing-parameters")]
    [DataRow("missing-reference")]
    [DataRow("corrupt-reference")]
    [DataRow("missing-source")]
    [DataRow("journal-recipe")]
    [DataRow("journal-output")]
    [DataRow("reference-rig")]
    [DataRow("target-rig")]
    [DataRow("truncated-reference")]
    public async Task DisplayReferenceRejectsInvalidEvidenceWithoutPoisoningDefaultAsync(string scenario)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var combined = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined,
            pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload).ConfigureAwait(false);
        var referenceSource = scenario == "cross-capture-source" ? await fixture.AddRawAsync().ConfigureAwait(false) : combined;
        var reference = await AddDisplayReferenceAsync(fixture, raw, referenceSource, new(0.5, 0.9997, 8, OutputEncoding: "Packed"), scenario).ConfigureAwait(false);
        var target = scenario == "wrong-capture" ? await fixture.AddRawAsync().ConfigureAwait(false) : raw;
        if (scenario == "target-rig")
        {
            var wrongRig = raw.Manifest with
            {
                Descriptor = raw.Manifest.Descriptor with
                {
                    Profiles = raw.Manifest.Descriptor.Profiles with { Rig = raw.Manifest.Descriptor.Profiles.Rig with { Sha256 = new string('C', 64) } }
                }
            };
            target = await fixture.AddOutputAsync(wrongRig, FrameArtifactRole.Calibrated,
                pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload).ConfigureAwait(false);
        }
        if (scenario == "missing-reference") File.Delete(reference.PayloadPath);
        if (scenario == "missing-source") File.Delete(combined.PayloadPath);
        if (scenario == "corrupt-reference") await File.WriteAllBytesAsync(reference.PayloadPath, new byte[reference.Payload.Length]).ConfigureAwait(false);
        if (scenario == "truncated-reference") await File.WriteAllBytesAsync(reference.PayloadPath, reference.Payload[..^1]).ConfigureAwait(false);
        if (scenario == "journal-recipe")
            await fixture.ExecuteAsync($"UPDATE processing_outputs SET recipe_identity_sha256 = '{new string('0', 64)}' WHERE artifact_id = '{reference.ArtifactId:N}';").ConfigureAwait(false);
        if (scenario == "journal-output")
            await fixture.ExecuteAsync($"UPDATE processing_outputs SET output_identity_sha256 = '{new string('0', 64)}' WHERE artifact_id = '{reference.ArtifactId:N}';").ConfigureAwait(false);

        var baseline = await fixture.Service.GetPreviewAsync(target.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var rejected = await fixture.Service.GetPreviewAsync(target.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, rejected.Status, scenario);
        Assert.IsTrue(rejected.Content.IsEmpty);
        Assert.AreEqual(baseline.ChecksumSha256,
            (await fixture.Service.GetPreviewAsync(target.ArtifactId, CancellationToken.None).ConfigureAwait(false)).ChecksumSha256);
    }

    [TestMethod]
    public async Task DisplayReferenceRejectsStretchedTargetsAndWrongCombinedSourceAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var combined = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined,
            pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload).ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, combined, new(0.5, 0.9997, 8, OutputEncoding: "Packed")).ConfigureAwait(false);
        var unrelatedPreview = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
        var annotated = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        var otherCombined = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined,
            pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload, variant: "other").ConfigureAwait(false);
        foreach (var target in new[] { unrelatedPreview, annotated, otherCombined })
        {
            var result = await fixture.Service.GetPreviewAsync(target.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, result.Status);
        }
        Assert.AreEqual(CameraAgentArtifactReadStatus.InvalidRequest,
            (await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, Guid.Empty).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task EncodedReferenceSelfPreviewIsNeverStretchedTwiceAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        foreach (var encoding in new[] { "Packed", "Jpeg" })
        {
            var reference = await AddDisplayReferenceAsync(fixture, raw, raw, new(0.3, 0.8, 7, OutputEncoding: encoding),
                variant: encoding).ConfigureAwait(false);
            var plain = await fixture.Service.GetPreviewAsync(reference.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            var explicitReference = await fixture.Service.GetPreviewAsync(reference.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, explicitReference.Status);
            CollectionAssert.AreEqual(plain.Content.ToArray(), explicitReference.Content.ToArray());
            if (encoding == "Jpeg") CollectionAssert.AreEqual(reference.Payload, explicitReference.Content.ToArray());
        }
    }

    [TestMethod]
    public async Task PolicyIdentitySeparatesConcurrentRequestAndGenerationFlightsAndCachesAsync()
    {
        var encoder = new PolicyBlockingEncoder();
        using var fixture = await ArtifactFixture.CreateAsync(
            new ArtifactReadOptions { MaximumConcurrentPreviews = 4 }, encoder).ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var copy = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Calibrated,
            pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload).ConfigureAwait(false);
        var first = await AddDisplayReferenceAsync(fixture, raw, raw, new(0.3, 0.8, 3), variant: "first").ConfigureAwait(false);
        var second = await AddDisplayReferenceAsync(fixture, raw, raw, new(0.3, 0.8, 9), variant: "second").ConfigureAwait(false);
        var one = Task.Run(async () => await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, first.ArtifactId).ConfigureAwait(false));
        await encoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var same = fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, first.ArtifactId).AsTask();
        var two = Task.Run(async () => await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, second.ArtifactId).ConfigureAwait(false));
        var copyTwo = Task.Run(async () => await fixture.Service.GetPreviewAsync(copy.ArtifactId, CancellationToken.None, second.ArtifactId).ConfigureAwait(false));
        try
        {
            await encoder.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => fixture.Service.PreviewRequestWaiters == 4).ConfigureAwait(false);
        }
        finally { encoder.Release.TrySetResult(); }
        var results = await Task.WhenAll(one, same, two, copyTwo).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsTrue(results.All(static result => result.Status == CameraAgentArtifactReadStatus.Found));
        Assert.AreEqual(2, encoder.Count);
        Assert.AreEqual(results[0].ChecksumSha256, results[1].ChecksumSha256);
        Assert.AreEqual(results[2].ChecksumSha256, results[3].ChecksumSha256);
        Assert.AreNotEqual(results[0].ChecksumSha256, results[2].ChecksumSha256);
        Assert.AreNotEqual(results[0].DisplayPolicyIdentity, results[2].DisplayPolicyIdentity);
        await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, first.ArtifactId).ConfigureAwait(false);
        await fixture.Service.GetPreviewAsync(copy.ArtifactId, CancellationToken.None, second.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(2, encoder.Count, "Both policy caches must survive without colliding.");
        File.Delete(first.PayloadPath);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict,
            (await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, first.ArtifactId).ConfigureAwait(false)).Status,
            "Reference evidence is revalidated before using cached display bytes.");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Service.GetPreviewAsync(raw.ArtifactId, canceled.Token, second.ArtifactId).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferenceValidationIsSingleFlightWithOneSlotAndZeroCacheAsync(bool corruptReference)
    {
        var encoder = new PolicyBlockingEncoder();
        using var fixture = await ArtifactFixture.CreateAsync(new ArtifactReadOptions
        {
            MaximumConcurrentPreviews = 1,
            PreviewCacheBytes = 0
        }, encoder).ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var source = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined,
            pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload).ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, source, new(OutputEncoding: "Packed")).ConfigureAwait(false);
        if (corruptReference) await File.WriteAllBytesAsync(reference.PayloadPath, new byte[reference.Payload.Length]).ConfigureAwait(false);

        var lifecycle = StorageLifecycleLock.ForRoot(fixture.Root);
        await lifecycle.WaitAsync().ConfigureAwait(false);
        Task<CameraAgentArtifactPreviewResult>[] requests;
        try
        {
            requests = Enumerable.Range(0, 12).Select(_ => fixture.Service.GetPreviewAsync(
                raw.ArtifactId, CancellationToken.None, reference.ArtifactId).AsTask()).ToArray();
            await WaitUntilAsync(() => fixture.Service.PreviewRequestWaiters == 12).ConfigureAwait(false);
            Assert.AreEqual(0L, fixture.Service.PayloadValidationReads);
        }
        finally { lifecycle.Release(); encoder.Release.TrySetResult(); }
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsTrue(results.All(result => result.Status == (corruptReference
            ? CameraAgentArtifactReadStatus.Conflict : CameraAgentArtifactReadStatus.Found)));
        Assert.AreEqual(corruptReference ? 1L : 4L, fixture.Service.PayloadValidationReads,
            "Overlapping requests must share reference/source/target/core checksums, not only encoding.");
        Assert.AreEqual(corruptReference ? 1L : 3L, fixture.Service.EvidenceValidationReads);
        Assert.AreEqual(corruptReference ? 0 : 1, encoder.Count);
        Assert.AreEqual(0, fixture.Service.PreviewRequestWaiters);

        // A new request is not part of the completed flight and must revalidate evidence even on failure.
        await File.WriteAllBytesAsync(reference.PayloadPath, reference.Payload).ConfigureAwait(false);
        var before = fixture.Service.PayloadValidationReads;
        var retry = await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, retry.Status);
        Assert.AreEqual(before + 4, fixture.Service.PayloadValidationReads);
        Assert.AreEqual(corruptReference ? 1 : 2, encoder.Count, "Zero cache cannot hide an extra request flight.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReferenceFlightCancellationReleasesLeaderAndWaiterStateAsync(bool cancelLeader)
    {
        using var fixture = await ArtifactFixture.CreateAsync(new ArtifactReadOptions { MaximumConcurrentPreviews = 1 }).ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, raw, new(OutputEncoding: "Packed")).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var lifecycle = StorageLifecycleLock.ForRoot(fixture.Root);
        await lifecycle.WaitAsync().ConfigureAwait(false);
        Task<CameraAgentArtifactPreviewResult> leader;
        Task<CameraAgentArtifactPreviewResult> follower;
        try
        {
            leader = fixture.Service.GetPreviewAsync(raw.ArtifactId, cancelLeader ? cancellation.Token : CancellationToken.None, reference.ArtifactId).AsTask();
            follower = fixture.Service.GetPreviewAsync(raw.ArtifactId, cancelLeader ? CancellationToken.None : cancellation.Token, reference.ArtifactId).AsTask();
            await WaitUntilAsync(() => fixture.Service.PreviewRequestWaiters == 2).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await (cancelLeader ? leader : follower).ConfigureAwait(false)).ConfigureAwait(false);
            await WaitUntilAsync(() => fixture.Service.PreviewRequestWaiters == 1).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
        var result = await (cancelLeader ? follower : leader).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
        Assert.AreEqual(0, fixture.Service.PreviewRequestWaiters);
        Assert.AreEqual(4L, fixture.Service.PayloadValidationReads);
    }

    [TestMethod]
    [DataRow(FrameArtifactRole.Combined)]
    [DataRow(FrameArtifactRole.Calibrated)]
    [DataRow(FrameArtifactRole.Raw)]
    public async Task NullSelectorVariantIsWildcardButSpecifiedWrongVariantIsRejectedAsync(FrameArtifactRole role)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var source = role == FrameArtifactRole.Raw ? raw : await fixture.AddOutputAsync(raw.Manifest, role, pixelFormat: CameraPixelFormat.Mono16, payload: raw.Payload).ConfigureAwait(false);
        var selector = role switch
        {
            FrameArtifactRole.Raw => ProcessingInputSelector.Raw(),
            FrameArtifactRole.Combined => ProcessingInputSelector.Combined(),
            _ => ProcessingInputSelector.Calibrated()
        };
        var reference = await AddDisplayReferenceAsync(fixture, raw, source, new(OutputEncoding: "Packed"), selector: selector).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, reference.Manifest.Descriptor.Artifact.Recipe.Options.GetProperty("input").GetProperty("variant").ValueKind);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found,
            (await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false)).Status);
        var explicitReference = await AddDisplayReferenceAsync(fixture, raw, source, new(OutputEncoding: "Packed"),
            variant: "explicit", selector: selector with { Variant = source.Manifest.Descriptor.Artifact.Variant }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found,
            (await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, explicitReference.ArtifactId).ConfigureAwait(false)).Status);
        var wrong = await AddDisplayReferenceAsync(fixture, raw, source, new(OutputEncoding: "Packed"),
            fault: "wrong-input-variant", variant: "wrong", selector: selector).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict,
            (await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, wrong.ArtifactId).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    [DataRow("jpeg-header", 0L)]
    [DataRow("jpeg-truncated", 1L)]
    [DataRow(null, 1L)]
    public async Task JpegHeaderIsCheckedBeforeScanlineValidationAsync(string? fault, long expectedDecodes)
    {
        using var fixture = await ArtifactFixture.CreateAsync(new ArtifactReadOptions { MaximumPreviewDimension = 2 }).ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, raw, new(), fault).ConfigureAwait(false);
        var content = await fixture.Service.OpenContentAsync(reference.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, content.Status, "The payload checksum matches the sidecar/journal; this is not a checksum refusal.");
        await content.Content!.DisposeAsync().ConfigureAwait(false);
        var header = JpegImageCodec.InspectJpeg(reference.Payload);
        Assert.AreEqual(fault == "jpeg-header" ? 4 : 2, header.Width);
        var preview = await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(fault is null ? CameraAgentArtifactReadStatus.Found : CameraAgentArtifactReadStatus.Conflict, preview.Status);
        Assert.AreEqual(expectedDecodes, fixture.Service.JpegValidationReads,
            "An oversized header must be rejected without entering the scanline decoder.");
    }

    [TestMethod]
    public async Task ComparisonResizesAfterExactlyOneFullFrameTransferAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync(new ArtifactReadOptions { MaximumPreviewDimension = 50 }).ConfigureAwait(false);
        var (pixels, _) = CreateCombinedFixtureFrame();
        var raw = await fixture.AddRawAsync(pixels, 100, 100).ConfigureAwait(false);
        var reference = await AddDisplayReferenceAsync(fixture, raw, raw, new(0.5, 0.9997, 8, OutputEncoding: "Packed")).ConfigureAwait(false);
        var preview = await fixture.Service.GetPreviewAsync(raw.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        var retained = await fixture.Service.GetPreviewAsync(reference.ArtifactId, CancellationToken.None, reference.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, preview.Status);
        Assert.AreEqual(50, preview.Width);
        Assert.AreEqual(50, preview.Height);
        CollectionAssert.AreEqual(retained.Content.ToArray(), preview.Content.ToArray());
        var resizedFirst = PackedImageDownsampler.Downsample(raw.Manifest.Descriptor.Layout, pixels, 50);
        var wrongOrder = JpegImageCodec.EncodeMono8ToJpeg(50, 50,
            Mono16DisplayStretch.Apply(50, 50, resizedFirst.Payload, options: new(0.5, 0.9997, 8)));
        Assert.AreNotEqual(PayloadChecksum.ComputeSha256(wrongOrder), preview.ChecksumSha256,
            "The fixture must distinguish downsampling before vs after histogram/transfer.");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(false);
        Assert.Throws<OperationCanceledException>(() => new CameraAgentPreviewEncoder().Encode(
            raw.Manifest.Descriptor.Layout, pixels, 50, 4096, canceled.Token, new(0.5, 0.9997, 8)));
    }

    [TestMethod]
    public async Task NoRetainedManifestHasDistinctStatusFromUnreadableRetainedLayersAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var result = await fixture.ReadLayersAsync(raw.Manifest.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.NotRetained, result.Status);
        Assert.AreNotEqual(CameraAgentLayeredPresentationStatus.Unavailable, result.Status);
        Assert.IsNull(result.Presentation);
    }

    private static ProcessingArtifact ProcessingInput(StoredArtifact artifact) => new(
        artifact.ArtifactId, artifact.Manifest.Descriptor.Artifact.Role, artifact.Manifest.Descriptor.Artifact.Variant,
        ProcessingIdentity.CreateRecipeIdentity(artifact.Manifest.Descriptor.Artifact.Recipe).IdentitySha256,
        artifact.Manifest.Descriptor.Artifact.MediaType, artifact.Manifest.Descriptor.Layout, artifact.Payload,
        artifact.Manifest.Descriptor.Artifact.CreatedUtc, TimeSpan.FromSeconds(1),
        new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"));

    private static async Task<StoredArtifact> AddDisplayReferenceAsync(
        ArtifactFixture fixture, StoredArtifact raw, StoredArtifact source, EncodedPreviewOptions options,
        string? fault = null, string variant = "combined-preview", ProcessingInputSelector? selector = null)
    {
        var input = ProcessingInput(source);
        // Use the actual producer to retain its normalized parameters and input-envelope shape.
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview, JsonSerializer.SerializeToElement(options),
            selector ?? (input.Role switch
            {
                FrameArtifactRole.Raw => ProcessingInputSelector.Raw(input.Variant),
                FrameArtifactRole.Combined => ProcessingInputSelector.Combined(input.Variant),
                _ => ProcessingInputSelector.Calibrated(input.Variant)
            }), [input], variant,
            InputArtifactId: input.ArtifactId), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        var product = outcome.Products.Single();
        var recipe = product.Recipe.Descriptor;
        var node = JsonNode.Parse(recipe.Options.GetRawText())!;
        switch (fault)
        {
            case "wrong-input-role": node["input"]!["role"] = "Raw"; break;
            case "wrong-input-recipe": node["input"]!["recipeIdentitySha256"] = new string('0', 64); break;
            case "wrong-input-variant": node["input"]!["variant"] = "unrelated"; break;
            case "black-white": node["parameters"]!["blackPercentile"] = 1; break;
            case "white-out-of-bounds": node["parameters"]!["whitePercentile"] = 2; break;
            case "strength-out-of-bounds": node["parameters"]!["asinhStrength"] = 1001; break;
            case "nonfinite": node["parameters"]!["asinhStrength"] = "NaN"; break;
            case "missing-parameters": node.AsObject().Remove("parameters"); break;
        }
        recipe = RecipeIdentityDescriptor.Create(fault == "wrong-recipe" ? "annotation" : recipe.Name,
            recipe.SemanticVersion, fault == "wrong-version" ? "future-version" : recipe.ImplementationVersion,
            JsonSerializer.SerializeToElement(node));
        var width = source.Manifest.Descriptor.Layout.Width;
        var height = source.Manifest.Descriptor.Layout.Height;
        var payload = product.Payload.ToArray();
        if (fault == "jpeg-header") payload = JpegImageCodec.EncodeMono8ToJpeg(4, 4, new byte[16]);
        if (fault == "jpeg-truncated") payload = payload[..^2];
        if (fault == "reference-rig") raw = raw with
        {
            Manifest = raw.Manifest with
            {
                Descriptor = raw.Manifest.Descriptor with
                {
                    Profiles = raw.Manifest.Descriptor.Profiles with
                    {
                        Rig = raw.Manifest.Descriptor.Profiles.Rig with { Sha256 = new string('B', 64) }
                    }
                }
            }
        };
        return await fixture.AddOutputAsync(raw.Manifest,
            fault == "wrong-role" ? FrameArtifactRole.AnnotatedPreview : FrameArtifactRole.Preview,
            mediaType: product.MediaType, encodedWidth: fault == "wrong-dimensions" ? width * 2 : width,
            encodedHeight: height, width: fault == "wrong-dimensions" ? width * 2 : width,
            height: fault == "wrong-dimensions" ? height / 2 : height,
            pixelFormat: fault == "wrong-layout" ? CameraPixelFormat.Mono16 : product.Layout?.PixelFormat ?? CameraPixelFormat.Mono8,
            payload: payload, sourceArtifactId: source.ArtifactId, recipeOverride: recipe, variant: variant).ConfigureAwait(false);
    }

    private sealed class PolicyBlockingEncoder : ICameraAgentPreviewEncoder
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CameraAgentEncodedPreview Encode(FrameLayoutDescriptor layout, ReadOnlyMemory<byte> payload,
            int maximumDimension, int maximumEncodedBytes, CancellationToken cancellationToken, Mono16DisplayStretchOptions? displayOptions = null)
        {
            Entered.TrySetResult();
            if (Interlocked.Increment(ref _count) == 2) TwoEntered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            return new([(byte)(displayOptions?.AsinhStrength ?? 4)], layout.Width, layout.Height);
        }
    }

    private sealed class ComparisonRuntime : ICameraAgentPresentationRuntime
    {
        public CameraAgentPresentationRuntimeSnapshot GetSnapshot(DateTimeOffset observedUtc)
            => new(new(CameraAgentPresentationSystemState.Paused, "Test capture paused.", observedUtc), null);
    }

    private sealed class NoStructuredLayers : ICameraAgentStructuredLayerAvailability
    {
        public ValueTask<bool> IsAvailableAsync(Guid captureId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
