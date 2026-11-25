using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    private static readonly JsonSerializerOptions CenteredAcceptanceJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CenteredCurrentSkyLiveAdvancementOverlayStabilityAndArchiveReopenAsync(CameraPixelFormat pixelFormat)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        var controlledLookup = new ControlledCenteredViewLookup();
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IHostedService) &&
                (value.ImplementationType == typeof(CameraCaptureService) ||
                 value.ImplementationType == typeof(RetentionBackgroundService) ||
                 value.ImplementationType?.FullName?.Contains("CaptureDistributionService", StringComparison.Ordinal) == true ||
                 value.ImplementationFactory?.Method.ReturnType == typeof(TransientWorkerService) ||
                 value.ImplementationFactory?.Method.ReturnType == typeof(CenteredViewPublicationWorker) ||
                 value.ImplementationFactory?.Method.ReturnType == typeof(CenteredViewWindowPublicationWorker) ||
                 value.ImplementationFactory?.Method.ReturnType == typeof(CenteredViewTrailPublication) ||
                 value.ImplementationFactory?.Method.ReturnType.Name == "RetainedIntegrityScrubService")).ToArray())
            {
                services.Remove(descriptor);
            }
            services.Replace(ServiceDescriptor.Singleton(provider =>
                new SqliteCenteredViews(
                    provider.GetRequiredService<SqliteCaptureProcessingStore>(),
                    provider.GetRequiredService<TimeProvider>(),
                    NullLogger<SqliteCenteredViews>.Instance,
                    ingress: null)));
            services.Replace(ServiceDescriptor.Singleton<ICenteredViewLookup>(provider =>
            {
                controlledLookup.Attach(provider.GetRequiredService<ICenteredViewReadIndex>());
                return controlledLookup;
            }));
        }).ConfigureAwait(false);

        var token = CancellationToken.None;
        var hostOptions = host.Services.GetRequiredService<IOptions<CameraAgentHostOptions>>();
        var journal = new SqliteRawCaptureJournal(Path.Combine(host.Root, "journal", "raw-ingress.db"), 30);
        await journal.InitializeAsync(token).ConfigureAwait(false);
        var processingStore = host.Services.GetRequiredService<SqliteCaptureProcessingStore>();
        var artifactService = host.Services.GetRequiredService<ICameraAgentArtifactService>();
        var views = host.Services.GetRequiredService<SqliteCenteredViews>();
        var publisher = host.Services.GetRequiredService<ICenteredViewPublisher>();
        var persistence = host.Services.GetRequiredService<CaptureProcessingPersistence>();
        var producer = new CenteredViewProducer(persistence, artifactService, views, hostOptions, processingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        const int width = 128;
        const int height = 96;
        var layout = pixelFormat == CameraPixelFormat.BayerRggb16
            ? new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.BayerRggb16,
                FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, width * height * 2)
            {
                Readout = new(width + 2, height + 2, 1, 1, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1),
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample
            }
            : new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
                FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);

        var pixels = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2), (ushort)(250 + (i % 600)));
        }

        var now = DateTimeOffset.UtcNow;
        var raw101 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-10), token).ConfigureAwait(false);
        var raw102 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-8), token).ConfigureAwait(false);
        var raw103 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-6), token).ConfigureAwait(false);
        var raw104 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-4), token).ConfigureAwait(false);
        var raw105 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-2), token).ConfigureAwait(false);

        var stream = new CenteredViewStream(new(raw101.Capture.AgentId, "camera-1"), Guid.NewGuid(), "gen-1923-browser");
        controlledLookup.Stream = stream;

        ExposureInputV1 Input(ReconstructionDescriptor raw) =>
            new(new(stream.Camera, stream.RunId, raw.Capture.CaptureId), raw,
                new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        var input101 = Input(raw101);
        var input102 = Input(raw102);
        var input103 = Input(raw103);
        var input104 = Input(raw104);
        var input105 = Input(raw105);

        // Publish Single 101 and select live (Warming)
        var single101 = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input101.Raw.Capture.CaptureSequence,
            new(input101, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single101);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, single101.Base.BaseId, token).ConfigureAwait(false));
        controlledLookup.SetLive(stream, input101.Raw, CenteredViewLiveState.Warming);

        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GotoAsync("/").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);

        // 1. Single 101 displays immediately with verified natural dimensions
        await page.Locator($"[data-centered-reference='#{raw101.Capture.CaptureSequence}']")
            .WaitForAsync().ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            $"() => {{ const img = document.querySelector('.sky-layer-canvas > img'); return !!img && img.complete && img.naturalWidth === {width} && img.naturalHeight === {height}; }}")
            .ConfigureAwait(false));

        // 2. Warm-up publisher wakes advance ready singles 102 and 103 immediately without 15s refresh throttle!
        var single102 = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input102.Raw.Capture.CaptureSequence,
            new(input102, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single102);
        controlledLookup.SetLive(stream, input102.Raw, CenteredViewLiveState.Warming);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, single102.Base.BaseId, token).ConfigureAwait(false));
        await page.Locator($"[data-centered-reference='#{raw102.Capture.CaptureSequence}']")
            .WaitForAsync().ConfigureAwait(false);

        var single103 = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input103.Raw.Capture.CaptureSequence,
            new(input103, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single103);
        controlledLookup.SetLive(stream, input103.Raw, CenteredViewLiveState.Warming);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, single103.Base.BaseId, token).ConfigureAwait(false));
        await page.Locator($"[data-centered-reference='#{raw103.Capture.CaptureSequence}']")
            .WaitForAsync().ConfigureAwait(false);

        // 3. Switch in-place to registered stack 103, then publish overlay-only revision on SAME base
        var exp103 = single103.Base.Definition.Reference with { Linear = new(Guid.NewGuid(), new string('A', 64), new string('B', 64)) };
        var exp104 = new CenteredViewExposure(input104, Guid.NewGuid(), new(Guid.NewGuid(), new string('C', 64), new string('D', 64)));
        var exp105 = new CenteredViewExposure(input105, Guid.NewGuid(), new(Guid.NewGuid(), new string('E', 64), new string('F', 64)));

        var stack103 = await RegisterStackAsync(exp103, exp105).ConfigureAwait(false);
        var stack104 = await RegisterStackAsync(exp104, exp105).ConfigureAwait(false);

        controlledLookup.SetLive(stream, input105.Raw, CenteredViewLiveState.Ready);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, stack103.Base.BaseId, token).ConfigureAwait(false));
        await page.Locator($".sky-layer-workspace[data-centered-base='{stack103.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);

        // Pin the exact DOM <img> node and its src before overlay-only revision arrives!
        var baseImgSrc = await page.EvaluateAsync<string?>(
            "() => { const img = document.querySelector('.sky-layer-canvas > img'); window.__hvoBaseImg = img; return img?.getAttribute('src') ?? null; }")
            .ConfigureAwait(false);
        Assert.IsNotNull(baseImgSrc);
        StringAssert.Contains(baseImgSrc, stack103.Base.BaseId, StringComparison.Ordinal);
        Assert.IsFalse(baseImgSrc.Contains(stack103.Revision.RevisionId, StringComparison.Ordinal));

        // Publish star-annotations optional layer on stack103 -> SVG updates WITHOUT remounting <img> or changing src!
        await PublishLayerAsync(input103, stack103.Base.BaseId, CenteredViewLayerScope.Reference, "star-annotations", 20, true, 1).ConfigureAwait(false);
        var starCheckbox = page.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input:not(:disabled)");
        await starCheckbox.WaitForAsync().ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            "() => window.__hvoBaseImg === document.querySelector('.sky-layer-canvas > img')").ConfigureAwait(false),
            "Overlay-only revision must not remount the base <img> element.");
        Assert.AreEqual(baseImgSrc,
            await page.Locator(".sky-layer-canvas > img").GetAttributeAsync("src").ConfigureAwait(false));

        // Interactive overlay toggle hides/shows the SVG group in-browser
        Assert.IsTrue(await starCheckbox.IsCheckedAsync().ConfigureAwait(false));
        await starCheckbox.UncheckAsync().ConfigureAwait(false);
        Assert.AreEqual("none", await page.EvaluateAsync<string>(
            "() => document.querySelector(\".sky-layer-overlay svg g[data-layer-kind='star-annotations']\")?.style.display ?? ''")
            .ConfigureAwait(false));

        // 4. Advance live to stack104; late trail on 105 enriches 103 in archive WITHOUT replacing live 104!
        Assert.IsNotNull(await views.SelectLiveAsync(stream, stack104.Base.BaseId, token).ConfigureAwait(false));
        await page.Locator("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await page.Locator($".sky-layer-workspace[data-centered-base='{stack104.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);

        await PublishContributorTrailAsync(input105).ConfigureAwait(false);
        await page.Locator("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await page.Locator($".sky-layer-workspace[data-centered-base='{stack104.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual($"#{raw104.Capture.CaptureSequence}",
            await page.Locator("[data-centered-reference]").InnerTextAsync().ConfigureAwait(false));

        // 5. Archive reopen of capture 103: conflicting browser localStorage must NOT override archive visibility!
        await page.EvaluateAsync("() => { localStorage.setItem('hvo.archivedCapture.layerVisibility.v1:/', JSON.stringify({ 'star-annotations': false })); }")
            .ConfigureAwait(false);
        await page.GotoAsync($"/gallery/{raw103.Capture.CaptureId:D}").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".centered-archive-bases a").First.WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(2, await page.Locator(".centered-archive-bases a").CountAsync().ConfigureAwait(false));
        // Default opens last-live stack 103 with star-annotations ON (archive default, ignoring conflicting localStorage)
        var archiveStarToggle = page.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input:not(:disabled)");
        await archiveStarToggle.WaitForAsync().ConfigureAwait(false);
        Assert.IsTrue(await archiveStarToggle.IsCheckedAsync().ConfigureAwait(false));

        // Later archive enrichment revision is distinctly labelled and clickable
        var enrichmentLink = page.Locator(".centered-archive-revisions a[data-revision-role='LaterArchiveEnrichment']");
        await enrichmentLink.WaitForAsync().ConfigureAwait(false);
        await enrichmentLink.ClickAsync().ConfigureAwait(false);
        await page.Locator(".centered-layer-controls [data-layer-kind='MeasuredTransientTrails'] input:not(:disabled)").WaitForAsync().ConfigureAwait(false);

        // Turn off star-annotations, save personal view selection, and reopen in a FRESH context with conflicting localStorage!
        await page.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input:not(:disabled)").UncheckAsync().ConfigureAwait(false);
        await page.Locator(".save-centered-selection").ClickAsync().ConfigureAwait(false);
        var savedHref = await page.Locator(".centered-save-result a").GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(savedHref);
        StringAssert.Contains(savedHref, "?selection=", StringComparison.Ordinal);

        await using (var freshContext = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false))
        {
            var freshPage = await freshContext.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(freshPage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
            await freshPage.EvaluateAsync("() => { localStorage.setItem('hvo.archivedCapture.layerVisibility.v1:/', JSON.stringify({ 'star-annotations': true, 'MeasuredTransientTrails': false })); }")
                .ConfigureAwait(false);
            await freshPage.GotoAsync(savedHref).ConfigureAwait(false);
            await WaitForInteractiveShellAsync(freshPage).ConfigureAwait(false);
            await freshPage.Locator(".centered-saved-selection").WaitForAsync().ConfigureAwait(false);
            await freshPage.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
            Assert.IsFalse(await freshPage.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input").IsCheckedAsync().ConfigureAwait(false));
            Assert.IsTrue(await freshPage.Locator(".centered-layer-controls [data-layer-kind='MeasuredTransientTrails'] input").IsCheckedAsync().ConfigureAwait(false));
        }

        // 6. Explicit mismatched target fails closed without substituting another base
        await page.GotoAsync($"/gallery/{raw103.Capture.CaptureId:D}?base={stack104.Base.BaseId}&revision={stack104.Revision.RevisionId}")
            .ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".centered-selection-error").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(0, await page.Locator(".sky-layer-workspace").CountAsync().ConfigureAwait(false));

        await diagnostics.CompleteAsync().ConfigureAwait(false);
        _ = publisher;

        async ValueTask<CenteredView> RegisterStackAsync(CenteredViewExposure reference, CenteredViewExposure contributor)
        {
            var sourceEvidence = new CenteredStackSourceEvidence(2, contributor.Capture.CaptureId,
                CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.VerifiedCentroidSimilarity,
                new ImageSimilarity(1, 0, 1.0, -1.0), 100);
            CenteredViewContributor[] members =
            [
                new(reference, nameof(CenteredRegistrationMethod.ReferenceIdentity), JsonSerializer.SerializeToElement(new { })),
                new(contributor, nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity), JsonSerializer.SerializeToElement(sourceEvidence, CenteredAcceptanceJson))
            ];
            var rawArtifact = new ProcessingArtifact(reference.Input.Raw.Artifact.ArtifactId, FrameArtifactRole.Calibrated,
                reference.Input.Raw.Artifact.Variant, new string('1', 64), reference.Input.Raw.Artifact.MediaType,
                layout, pixels, reference.ExposureStartedUtc, reference.Input.Raw.Controls.EffectiveExposure,
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(reference.Input.Raw))
            {
                CaptureId = reference.Capture.CaptureId
            };
            var encoded = CenteredViewPreviewEncoder.Encode([rawArtifact], layout, CenteredViewImageSource.RegisteredLinear,
                null, null, new(), token);
            await persistence.PersistCenteredViewProductAsync(reference.Input.Raw, encoded.Product, token).ConfigureAwait(false);
            var science = new CenteredViewArtifact(Guid.NewGuid(), new string('5', 64), new string('6', 64));
            var closedWindow = new CenteredViewArtifact(Guid.NewGuid(), new string('7', 64), new string('8', 64));
            return await views.RegisterBaseAsync(
                new(stream, reference, reference.Capture.CaptureSequence, CenteredViewKind.Stack, science, encoded.Preview, members, closedWindow),
                [], token).ConfigureAwait(false);
        }

        async ValueTask PublishLayerAsync(ExposureInputV1 source, string baseId, CenteredViewLayerScope scope, string kind, int zOrder, bool enabledByDefault, long order)
        {
            var rawPin = source.Raw.Artifact;
            var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
                ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
                rawPin.CreatedUtc, source.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(source.Raw))
            {
                CaptureId = source.Key.CaptureId,
                DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(source.Raw)
            };
            var payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
                segments: [new(new(12, 12), new(48, 48), 2, new(255, 220, 120))]);
            var product = PresentationProcessingProducts.CreateLayerProduct(payload, $"{kind}-source", [sourceArtifact], "layer-v1");
            await persistence.PersistCenteredViewProductAsync(source.Raw, product, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(Guid.NewGuid(), kind, payload.SchemaVersion, product.Variant);
            var productRef = new ExposureProductRef(source.Key, slot, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
                product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
            var lookup = new ExposureProductLookup(source.Key, slot, ExposureProductAvailability.Available, order, productRef, null);
            await layerProducer.PublishAsync(baseId, scope, lookup, new(kind, zOrder, enabledByDefault),
                source.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
        }

        async ValueTask PublishContributorTrailAsync(ExposureInputV1 source)
        {
            var rawPin = source.Raw.Artifact;
            var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
                ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
                rawPin.CreatedUtc, source.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(source.Raw))
            {
                CaptureId = source.Key.CaptureId,
                DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(source.Raw)
            };
            var payload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
                segments: [new(new(16, 20), new(64, 52), 2, new(255, 180, 90))]);
            var product = PresentationProcessingProducts.CreateLayerProduct(payload, "measured-trail-source", [sourceArtifact], "trail-v1");
            await persistence.PersistCenteredViewProductAsync(source.Raw, product, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(Guid.NewGuid(), "measured-trails", payload.SchemaVersion, product.Variant);
            var productRef = new ExposureProductRef(source.Key, slot, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
                product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
            var lookup = new ExposureProductLookup(source.Key, slot, ExposureProductAvailability.Available, 7, productRef, null);
            await layerProducer.PublishToContributorsAsync(lookup, new(TransientTrailPresentation.LayerKind, 500, true),
                source.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
        }
    }

    private static async Task<ReconstructionDescriptor> AddCenteredRawAsync(
        string root,
        SqliteRawCaptureJournal journal,
        FrameLayoutDescriptor layout,
        byte[] payload,
        DateTimeOffset exposureStartedUtc,
        CancellationToken token)
    {
        var captureId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var identity = await journal.ReserveIdentityAsync(
            CameraAgentKestrelFixture.AgentId, captureId, artifactId, token).ConfigureAwait(false);
        var template = ArtifactManifestFixture.CreateManifest(
            layout.PixelFormat, layout.Width, layout.Height, layout.StrideBytes, payload);
        var descriptor = template.Descriptor with
        {
            Layout = layout,
            Capture = template.Descriptor.Capture with
            {
                AgentId = identity.AgentId,
                CaptureSequence = identity.CaptureSequence,
                CaptureId = identity.CaptureId
            },
            Timing = template.Descriptor.Timing with
            {
                RequestedStartUtc = exposureStartedUtc,
                ExposureStartedUtc = exposureStartedUtc,
                ExposureEndedUtc = exposureStartedUtc.AddSeconds(1),
                ReadoutCompletedUtc = exposureStartedUtc.AddSeconds(1),
                DurableIngressUtc = exposureStartedUtc.AddSeconds(1)
            },
            Artifact = template.Descriptor.Artifact with
            {
                ArtifactId = identity.ArtifactId,
                MediaType = layout.PixelFormat == CameraPixelFormat.BayerRggb16
                    ? "application/x-skymonitor-bayer-rggb16"
                    : "application/x-skymonitor-mono16",
                ChecksumSha256 = PayloadChecksum.ComputeSha256(payload)
            }
        };
        var relativePath = $"frames/{artifactId:N}.bin";
        var sidecarRelativePath = $"frames/{artifactId:N}.json";
        var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
        var sidecar = CaptureContractJson.Serialize(manifest);
        var payloadPath = Path.Combine(root, relativePath);
        var sidecarPath = Path.Combine(root, sidecarRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await File.WriteAllBytesAsync(payloadPath, payload, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(sidecarPath, sidecar, token).ConfigureAwait(false);
        await journal.CommitAsync(new RawIngressJournalEntry(
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
            descriptor.Timing.DurableIngressUtc), token).ConfigureAwait(false);
        return descriptor;
    }

    private sealed class ControlledCenteredViewLookup : ICenteredViewLookup
    {
        private ICenteredViewReadIndex? _views;
        private volatile CenteredViewLiveContext? _liveContext;

        public CenteredViewStream? Stream { get; set; }

        public void Attach(ICenteredViewReadIndex views) => _views = views;

        public void SetLive(CenteredViewStream stream, ReconstructionDescriptor head, CenteredViewLiveState state)
        {
            var observed = DateTimeOffset.UtcNow;
            var headCapture = new CameraAgentPresentationCapture(
                head.Capture.CaptureId,
                head.Capture.CaptureSequence,
                head.Timing.ExposureStartedUtc,
                Math.Max(0, (long)(observed - head.Timing.ExposureStartedUtc).TotalSeconds),
                GalleryEvidenceOrigin.Simulated);
            _liveContext = new CenteredViewLiveContext(stream, headCapture, observed, state, null);
        }

        public ValueTask<CenteredViewLiveContext?> ReadLiveContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_liveContext);

        public ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadArchiveAsync(
            Guid referenceCaptureId, CancellationToken cancellationToken = default) =>
            _views is not null && Stream is not null
                ? _views.ReadArchiveAsync(Stream, referenceCaptureId, cancellationToken)
                : ValueTask.FromResult<IReadOnlyList<CenteredViewArchiveBase>>([]);
    }
}
