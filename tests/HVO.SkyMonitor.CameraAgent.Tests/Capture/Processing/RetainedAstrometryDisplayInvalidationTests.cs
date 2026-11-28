using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("context-open")]
    [DataRow("assessment-evicted")]
    [DataRow("context-evicted")]
    [DataRow("selected-evicted")]
    public async Task CurrentSkyWithdrawsInvalidatedRetainedGeometry(string scenario)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = SiloInfrastructureFixture.OpenProcessingStore(first.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, telemetry);
            var older = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var second = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(second, store, persistence, telemetry, gate).ConfigureAwait(false);
            var holdEarlierImage = scenario == "selected-evicted";
            var selectedOutput = holdEarlierImage ? older : newest;
            var selectedCapture = selectedOutput.Capture.CaptureId;
            var displayArtifact = await ProduceRetainedDisplayBaseAsync(holdEarlierImage ? first : second,
                persistence, telemetry).ConfigureAwait(false);
            var contextOutput = await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[3], token).ConfigureAwait(false);
            Assert.IsNotNull(contextOutput);

            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(first.Fixture.Options, store);
            using var selectedReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var layerService = new CameraAgentLayeredPresentationService(store, artifacts, persistence: persistence, astrometry: selectedReader);
            var contextOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumeOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pauseContext = 0;
            var checkpoint = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            checkpoint.Setup(api => api.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (id == contextOutput.ArtifactId && Interlocked.Exchange(ref pauseContext, 0) != 0)
                    {
                        contextOpen.TrySetResult();
                        await resumeOpen.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    }
                    return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                });
            using var latestReader = CreateUnpooledAstrometryReader(store, checkpoint.Object);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(latestReader, layerService);
            var expectedOlder = CameraAgentLatestAstrometry.FromVerified(
                await selectedReader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false));
            var expectedNewest = CameraAgentLatestAstrometry.FromVerified(
                await selectedReader.GetAsync(newest.Capture.CaptureId, token).ConfigureAwait(false));
            Assert.IsNotNull(expectedOlder);
            Assert.IsNotNull(expectedNewest);
            var selected = await selectedReader.GetAsync(selectedCapture, token).ConfigureAwait(false);
            var firstLayers = await layerService.GetCurrentAsync(selectedCapture, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, firstLayers.Status);
            Assert.IsNotNull(firstLayers.Presentation);
            Assert.AreEqual(displayArtifact, firstLayers.Presentation.Astrometry?.CompatibleBaseArtifactId);
            var association = firstLayers.Presentation.Layers.Single(x => x.Kind == AstrometricDiagnosticPresentation.AssociationsLayer);
            var residual = firstLayers.Presentation.Layers.Single(x => x.Kind == AstrometricDiagnosticPresentation.ResidualsLayer);

            using var context = new BunitContext();
            var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
            module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            var ui = CurrentSkyPageTests.Configure(context);
            var image = CurrentSkyPageTests.AstrometryImage(selected);
            var structured = CurrentSkyPageTests.WithStructuredBase();
            image = image with
            {
                SelectedStage = CameraAgentPresentationStage.Annotated,
                StructuredLayersAvailable = true,
                Stages = structured.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                    ? slot with { DisplayArtifactId = displayArtifact }
                    : slot).ToArray()
            };
            ui.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(image));
            ui.PresentationHandler = service.GetLayeredPresentationAsync;
            ui.CurrentPresentationHandler = service.GetCurrentLayeredPresentationAsync;
            ui.AstrometryHandler = service.GetRetainedAstrometryAsync;
            OperatorUiResult<CameraAgentLatestAstrometryRead>? lastRead = null;
            ui.LatestAstrometrySelectionHandler = async (cancellation, previous, assessment) =>
            {
                var response = await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, assessment).ConfigureAwait(false);
                Volatile.Write(ref lastRead, response);
                return response;
            };
            var page = context.Render<CurrentSkyPage>();
            var timeout = TimeSpan.FromSeconds(8);
            page.WaitForAssertion(() =>
            {
                AssertHeadline(expectedNewest, holdEarlierImage);
                AssertTools(available: true);
            }, timeout);
            await page.InvokeAsync(() => page.Find(".pixel-sky-calculator input").ChangeAsync("280")).ConfigureAwait(false);
            await page.InvokeAsync(() => page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240")).ConfigureAwait(false);
            await page.InvokeAsync(() => page.Find("form.pixel-sky-calculator").SubmitAsync()).ConfigureAwait(false);
            StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent, "mean of date", StringComparison.Ordinal);
            await page.InvokeAsync(() => page.Find($"input[data-layer-target='{residual.DomGroupId}']").ChangeAsync(true)).ConfigureAwait(false);

            var latestPath = Path.Combine(root, newest.PayloadRelativePath);
            using (File.Open(latestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, "Showing the previously verified result", StringComparison.Ordinal);
                    AssertHeadline(expectedNewest, holdEarlierImage);
                    AssertTools(available: true);
                    Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));
                }, timeout);
            }

            if (scenario == "context-open")
            {
                Volatile.Write(ref pauseContext, 1);
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                try
                {
                    await contextOpen.Task.WaitAsync(timeout).ConfigureAwait(false);
                    var member = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, contextOutput, token).ConfigureAwait(false);
                    member.FlipByte();
                    await member.RecordCopyStateAsync(SiloAvailabilityState.Quarantined, token)
                        .ConfigureAwait(false);
                }
                finally { resumeOpen.TrySetResult(); }
                page.WaitForAssertion(() =>
                {
                    AssertHeadline(expectedOlder, different: true);
                    AssertTools(available: false);
                }, timeout);
                var fallback = Volatile.Read(ref lastRead)!.Value!;
                Assert.IsTrue(fallback.Completed);
                Assert.IsTrue(fallback.PreviousSummaryQuarantined);
                Assert.IsTrue(fallback.SelectedAssessmentInvalidated);
                Assert.AreEqual(expectedOlder, fallback.Summary);
            }
            else
            {
                var target = scenario == "context-evicted" ? contextOutput : selectedOutput;
                var pending = holdEarlierImage ? newest : older;
                var pendingMember = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, pending, token).ConfigureAwait(false);
                pendingMember.SetUnavailable(true);
                var expired = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, target, token).ConfigureAwait(false);
                await expired.ExpireAsync(token).ConfigureAwait(false);
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    var summary = page.Find(".latest-measured-solution");
                    StringAssert.Contains(summary.TextContent, "temporarily unavailable", StringComparison.Ordinal);
                    if (holdEarlierImage) AssertHeadline(expectedNewest, different: true);
                    else Assert.IsEmpty(summary.QuerySelectorAll("time"), "The committed eviction left a stale headline timestamp.");
                    AssertTools(available: false);
                }, timeout);
                var incomplete = Volatile.Read(ref lastRead)!.Value!;
                Assert.IsFalse(incomplete.Completed);
                Assert.AreEqual(!holdEarlierImage, incomplete.PreviousSummaryEvicted);
                Assert.IsTrue(incomplete.SelectedAssessmentInvalidated);
                pendingMember.SetUnavailable(false);
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    AssertHeadline(holdEarlierImage ? expectedNewest : expectedOlder, different: true);
                    AssertTools(available: false);
                }, timeout);
            }

            var reloadedLayers = await layerService.GetCurrentAsync(selectedCapture, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, reloadedLayers.Status);
            Assert.AreEqual(displayArtifact, reloadedLayers.Presentation?.BaseArtifactId);
            Assert.IsEmpty(reloadedLayers.Presentation!.Layers);
            page.WaitForAssertion(() =>
            {
                Assert.HasCount(1, page.FindAll(".sky-layer-canvas img"));
                AssertTools(available: false);
            }, timeout);
            var selectedAfter = await selectedReader.GetAsync(selectedCapture, token).ConfigureAwait(false);
            Assert.IsNull(selectedAfter.Mapping);

            void AssertHeadline(CameraAgentLatestAstrometry summary, bool different)
            {
                var headline = page.Find(".latest-measured-solution").TextContent;
                StringAssert.Contains(headline, summary.ArtifactId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(headline, summary.SolutionIdentitySha256, StringComparison.Ordinal);
                StringAssert.Contains(headline, summary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
                StringAssert.Contains(headline, different ? "For a different capture" : "For the selected capture", StringComparison.Ordinal);
            }

            void AssertTools(bool available)
            {
                Assert.AreEqual(!available, page.Find(".pixel-sky-calculator button").HasAttribute("disabled"),
                    "The selected calculator must reflect the exact retained assessment's validity.");
                foreach (var id in new[] { association.DomGroupId, residual.DomGroupId })
                {
                    Assert.AreEqual(available, page.FindAll($"g[id='{id}']").Count != 0, $"Unexpected SVG group state for {id}.");
                    Assert.AreEqual(available, page.FindAll($"input[data-layer-target='{id}']").Count != 0);
                }
                if (!available) Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<Guid> ProduceRetainedDisplayBaseAsync(AstrometryDurabilityFixture fixture,
        CaptureProcessingPersistence persistence, CaptureProcessingTelemetry telemetry)
    {
        var descriptor = fixture.Fixture.Manifest.Descriptor;
        var root = fixture.Fixture.Options.Value.RawIngressRoot;
        var raw = new ProcessingArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256, descriptor.Artifact.MediaType,
            descriptor.Layout, await File.ReadAllBytesAsync(Path.Combine(root, fixture.Fixture.Manifest.RelativeArtifactPath)).ConfigureAwait(false),
            descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence, descriptor.Artifact.SourceArtifactIds, descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc)
        { CaptureId = descriptor.Capture.CaptureId, DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor) };
        var executor = new ProcessingRecipeExecutor();
        var rolling = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.RollingMean,
            JsonSerializer.SerializeToElement(new { maximumFrameCount = 2 }), ProcessingInputSelector.Raw(), [raw], "rolling-mean")).ConfigureAwait(false)).Products.Single();
        var preview = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Combined(), [Artifact(rolling)], "combined-preview")).ConfigureAwait(false)).Products.Single();
        var previewArtifact = Artifact(preview);
        var scene = ProjectedSceneJson.Parse(fixture.Scene.Payload).Scene!.SceneIdentitySha256;
        var manifest = PresentationProcessingProducts.CreateManifestProduct(
            PresentationProcessingProducts.CreateReference(previewArtifact, scene), scene, [], "overlay-manifest", previewArtifact);
        var graph = new CaptureProcessingGraph([
            new("display-rolling", new FixedArtifactProducingStep(rolling), [], false, BuiltInProcessingRecipes.RollingMean,
                rolling.Role, rolling.Variant, new string('D', 64)),
            new("display-preview", new FixedArtifactProducingStep(preview), ["display-rolling"], false, BuiltInProcessingRecipes.EncodedPreview,
                preview.Role, preview.Variant, new string('E', 64)),
            new("display-manifest", new FixedMetadataProducingStep(manifest), ["display-preview"], false, PresentationProcessingProducts.ManifestRecipeName,
                manifest.Role, manifest.Variant, new string('F', 64))
        ]);
        var processed = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Fixture.Item, graph, persistence, telemetry,
            1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
        return previewArtifact.ArtifactId;

        ProcessingArtifact Artifact(ProcessingProduct product) => new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.Role, product.Variant, product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload, raw.CreatedUtc,
            product.TotalIntegration, product.Compatibility, raw.CaptureSequence, product.SourceArtifactIds, raw.ObservationStartedUtc, raw.ObservationEndedUtc)
        {
            CaptureId = raw.CaptureId,
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256 ?? product.OutputIdentitySha256
        };
    }
}
