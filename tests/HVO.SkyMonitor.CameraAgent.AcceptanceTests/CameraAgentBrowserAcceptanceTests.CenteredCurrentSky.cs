using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Common.Configuration;
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
        var raw101 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-12), token).ConfigureAwait(false);
        var raw102 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-10), token).ConfigureAwait(false);
        var raw103 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-8), token).ConfigureAwait(false);
        var raw104 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-6), token).ConfigureAwait(false);
        var raw105 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-4), token).ConfigureAwait(false);
        var raw106 = await AddCenteredRawAsync(host.Root, journal, layout, pixels, now.AddSeconds(-2), token).ConfigureAwait(false);

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
        var input106 = Input(raw106);

        // Publish Single 101 and select live (Warming)
        var single101 = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input101.Raw.Capture.CaptureSequence,
            new(input101, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single101);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, single101.Base.BaseId, token).ConfigureAwait(false));
        controlledLookup.SetLive(stream, input101.Raw, CenteredViewLiveState.Warming);

        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        string savedHref;
        string stack103LastLiveRevId;
        string stack103EnrichedRevId;
        CenteredView stack103;
        CenteredView stack104;

        await using (var context = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false))
        {
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

            // 3. Frame 104 arrives -> hold single 103 while acquisition head moves to 104 and state is Building stack!
            controlledLookup.SetLive(stream, input104.Raw, CenteredViewLiveState.Building);
            await page.Locator("button.refresh-link").ClickAsync().ConfigureAwait(false);
            await page.Locator($"[data-centered-head='#{raw104.Capture.CaptureSequence}']").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual($"#{raw103.Capture.CaptureSequence}",
                await page.Locator("[data-centered-reference]").InnerTextAsync().ConfigureAwait(false));
            Assert.AreEqual("Building stack",
                await page.Locator("[data-centered-state]").GetAttributeAsync("data-centered-state").ConfigureAwait(false));
            Assert.AreEqual("1 frame (single)",
                await page.Locator("[data-centered-contributors]").InnerTextAsync().ConfigureAwait(false));

            // 4. Frame 105 arrives -> switch in-place to 5-frame registered stack centered on 103, then publish overlay-only revision on SAME base
            var exp101 = single101.Base.Definition.Reference with { Linear = new(Guid.NewGuid(), new string('1', 64), new string('2', 64)) };
            var exp102 = single102.Base.Definition.Reference with { Linear = new(Guid.NewGuid(), new string('3', 64), new string('4', 64)) };
            var exp103 = single103.Base.Definition.Reference with { Linear = new(Guid.NewGuid(), new string('A', 64), new string('B', 64)) };
            var exp104 = new CenteredViewExposure(input104, Guid.NewGuid(), new(Guid.NewGuid(), new string('C', 64), new string('D', 64)));
            var exp105 = new CenteredViewExposure(input105, Guid.NewGuid(), new(Guid.NewGuid(), new string('E', 64), new string('F', 64)));
            var exp106 = new CenteredViewExposure(input106, Guid.NewGuid(), new(Guid.NewGuid(), new string('9', 64), new string('0', 64)));

            stack103 = await RegisterStackAsync(exp103, [exp101, exp102, exp103, exp104, exp105]).ConfigureAwait(false);
            stack104 = await RegisterStackAsync(exp104, [exp102, exp103, exp104, exp105, exp106]).ConfigureAwait(false);

            controlledLookup.SetLive(stream, input105.Raw, CenteredViewLiveState.Ready);
            Assert.IsNotNull(await views.SelectLiveAsync(stream, stack103.Base.BaseId, token).ConfigureAwait(false));
            await page.Locator($".sky-layer-workspace[data-centered-base='{stack103.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
            await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual($"#{raw103.Capture.CaptureSequence}",
                await page.Locator("[data-centered-reference]").InnerTextAsync().ConfigureAwait(false));
            Assert.AreEqual($"#{raw105.Capture.CaptureSequence}",
                await page.Locator("[data-centered-head]").InnerTextAsync().ConfigureAwait(false));
            Assert.AreEqual("5 frames",
                await page.Locator("[data-centered-contributors]").InnerTextAsync().ConfigureAwait(false));
            Assert.AreEqual("Ready",
                await page.Locator("[data-centered-state]").GetAttributeAsync("data-centered-state").ConfigureAwait(false));

            // Pin the exact DOM <img> node and its src before overlay-only revision arrives!
            var baseImgSrc = await page.EvaluateAsync<string?>(
                "() => { const img = document.querySelector('.sky-layer-canvas > img'); window.__hvoBaseImg = img; return img?.getAttribute('src') ?? null; }")
                .ConfigureAwait(false);
            Assert.IsNotNull(baseImgSrc);
            StringAssert.Contains(baseImgSrc, stack103.Base.BaseId, StringComparison.Ordinal);
            Assert.IsFalse(baseImgSrc.Contains(stack103.Revision.RevisionId, StringComparison.Ordinal));

            // Publish star-annotations optional layer on stack103 -> SVG updates WITHOUT remounting <img> or changing src!
            var stack103WithStars = await PublishLayerAsync(input103, stack103.Base.BaseId, CenteredViewLayerScope.Reference, "star-annotations", 20, true, 1).ConfigureAwait(false);
            Assert.IsNotNull(stack103WithStars);
            stack103LastLiveRevId = stack103WithStars.Revision.RevisionId;
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

            // 5. Frame 106 arrives -> advance live to 5-frame stack104; late trail on 105 enriches 103 in archive WITHOUT replacing live 104!
            controlledLookup.SetLive(stream, input106.Raw, CenteredViewLiveState.Ready);
            Assert.IsNotNull(await views.SelectLiveAsync(stream, stack104.Base.BaseId, token).ConfigureAwait(false));
            await page.Locator("button.refresh-link").ClickAsync().ConfigureAwait(false);
            await page.Locator($".sky-layer-workspace[data-centered-base='{stack104.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual($"#{raw104.Capture.CaptureSequence}",
                await page.Locator("[data-centered-reference]").InnerTextAsync().ConfigureAwait(false));
            Assert.AreEqual($"#{raw106.Capture.CaptureSequence}",
                await page.Locator("[data-centered-head]").InnerTextAsync().ConfigureAwait(false));
            Assert.AreEqual("5 frames",
                await page.Locator("[data-centered-contributors]").InnerTextAsync().ConfigureAwait(false));

            await PublishContributorTrailAsync(input105).ConfigureAwait(false);
            await page.Locator("button.refresh-link").ClickAsync().ConfigureAwait(false);
            await page.Locator($".sky-layer-workspace[data-centered-base='{stack104.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual($"#{raw104.Capture.CaptureSequence}",
                await page.Locator("[data-centered-reference]").InnerTextAsync().ConfigureAwait(false));

            // 6. Archive reopen of capture 103: conflicting browser localStorage must NOT override archive visibility!
            await page.EvaluateAsync("() => { localStorage.setItem('hvo.archivedCapture.layerVisibility.v1:/', JSON.stringify({ 'star-annotations': false })); }")
                .ConfigureAwait(false);
            await page.GotoAsync($"/gallery/{raw103.Capture.CaptureId:D}").ConfigureAwait(false);
            await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
            await page.Locator(".centered-archive-bases a").First.WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual(2, await page.Locator(".centered-archive-bases a").CountAsync().ConfigureAwait(false));
            var archiveStarToggle = page.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input:not(:disabled)");
            await archiveStarToggle.WaitForAsync().ConfigureAwait(false);
            Assert.IsTrue(await archiveStarToggle.IsCheckedAsync().ConfigureAwait(false));
            Assert.AreEqual(stack103LastLiveRevId,
                await page.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-revision").ConfigureAwait(false));

            // Later archive enrichment revision is distinctly labelled and clickable
            var enrichmentLink = page.Locator(".centered-archive-revisions a[data-revision-role='LaterArchiveEnrichment']");
            await enrichmentLink.WaitForAsync().ConfigureAwait(false);
            stack103EnrichedRevId = await enrichmentLink.GetAttributeAsync("data-revision-id").ConfigureAwait(false)
                ?? throw new AssertFailedException("Expected enriched revision id.");
            await enrichmentLink.ClickAsync().ConfigureAwait(false);
            await page.Locator(".centered-layer-controls [data-layer-kind='MeasuredTransientTrails'] input:not(:disabled)").WaitForAsync().ConfigureAwait(false);

            // Turn off star-annotations, save personal view selection
            await page.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input:not(:disabled)").UncheckAsync().ConfigureAwait(false);
            await page.Locator(".save-centered-selection").ClickAsync().ConfigureAwait(false);
            savedHref = await page.Locator(".centered-save-result a").GetAttributeAsync("href").ConfigureAwait(false)
                ?? throw new AssertFailedException("Expected saved personal selection href.");
            StringAssert.Contains(savedHref, "?selection=", StringComparison.Ordinal);
        }

        // 7. Mutate site/default settings on disk AND restart the Kestrel host!
        //    Verify the restarted host reflects the changed MinimumRefreshSeconds site default
        //    AND reopens the exact archive & saved selection across restart!
        await host.RunWithHostStoppedAsync(async root =>
        {
            var settingsJson = JsonSerializer.Serialize(new
            {
                CameraAgent = new
                {
                    CurrentSky = new { MinimumRefreshSeconds = 60 }
                }
            });
            await File.WriteAllTextAsync(Path.Combine(root, DeploymentLocalSettings.FileName), settingsJson, token)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        Assert.AreEqual(60, host.Services.GetRequiredService<CurrentSkyRefreshSetting>().Seconds);

        await using (var restartedContext = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false))
        {
            var restartedPage = await restartedContext.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(restartedPage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);

            // Confirm the restarted host reflects MinimumRefreshSeconds = 60 in its authenticated settings API
            var settingsResponse = await restartedContext.APIRequest.GetAsync(CameraAgentSettingsContract.Route).ConfigureAwait(false);
            Assert.IsTrue(settingsResponse.Ok, $"Expected settings API 200 OK, got {settingsResponse.Status}.");
            var settingsPayload = JsonSerializer.Deserialize<CameraAgentSettingsResponse>(
                await settingsResponse.TextAsync().ConfigureAwait(false),
                CenteredAcceptanceJson);
            Assert.IsNotNull(settingsPayload);
            var currentSkySetting = settingsPayload.Groups
                .SelectMany(static group => group.Settings)
                .Single(static setting => string.Equals(setting.Key, CameraAgentSettingsCatalogue.MinimumRefreshInterval.Key, StringComparison.Ordinal));
            Assert.IsTrue(currentSkySetting.IsSet);
            Assert.AreEqual(CameraAgentSettingSource.SettingsFile, currentSkySetting.Source);
            Assert.AreEqual(60, currentSkySetting.Value?.GetInt32());

            // Seed conflicting browser default preferences for BOTH archive and live keys
            await restartedPage.EvaluateAsync(
                "() => { localStorage.setItem('hvo.archivedCapture.layerVisibility.v1:/', JSON.stringify({ 'star-annotations': true, 'MeasuredTransientTrails': false })); localStorage.setItem('hvo.currentSky.layerVisibility.v1:/', JSON.stringify({ 'star-annotations': true, 'MeasuredTransientTrails': false })); }")
                .ConfigureAwait(false);

            // Reopen saved personal selection after restart -> exact pinned base, revision, and saved layers!
            await restartedPage.GotoAsync(savedHref).ConfigureAwait(false);
            await WaitForInteractiveShellAsync(restartedPage).ConfigureAwait(false);
            await restartedPage.Locator(".centered-saved-selection").WaitForAsync().ConfigureAwait(false);
            await restartedPage.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual(stack103.Base.BaseId,
                await restartedPage.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-base").ConfigureAwait(false));
            Assert.AreEqual(stack103EnrichedRevId,
                await restartedPage.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-revision").ConfigureAwait(false));
            Assert.IsFalse(await restartedPage.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input").IsCheckedAsync().ConfigureAwait(false));
            Assert.IsTrue(await restartedPage.Locator(".centered-layer-controls [data-layer-kind='MeasuredTransientTrails'] input").IsCheckedAsync().ConfigureAwait(false));

            // Plain archive reopen after restart -> exact last-live base/revision without drifting to browser prefs!
            await restartedPage.GotoAsync($"/gallery/{raw103.Capture.CaptureId:D}").ConfigureAwait(false);
            await WaitForInteractiveShellAsync(restartedPage).ConfigureAwait(false);
            await restartedPage.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual(stack103.Base.BaseId,
                await restartedPage.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-base").ConfigureAwait(false));
            Assert.AreEqual(stack103LastLiveRevId,
                await restartedPage.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-revision").ConfigureAwait(false));
            Assert.IsTrue(await restartedPage.Locator(".centered-layer-controls [data-layer-kind='star-annotations'] input").IsCheckedAsync().ConfigureAwait(false));

            // Explicit mismatched target fails closed without substituting another base
            await restartedPage.GotoAsync($"/gallery/{raw103.Capture.CaptureId:D}?base={stack104.Base.BaseId}&revision={stack104.Revision.RevisionId}")
                .ConfigureAwait(false);
            await WaitForInteractiveShellAsync(restartedPage).ConfigureAwait(false);
            await restartedPage.Locator(".centered-selection-error").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual(0, await restartedPage.Locator(".sky-layer-workspace").CountAsync().ConfigureAwait(false));

            // Malformed ?selection=not-a-guid displays explicit unavailable notice without crashing Blazor
            await restartedPage.GotoAsync($"/gallery/{raw103.Capture.CaptureId:D}?selection=not-a-guid")
                .ConfigureAwait(false);
            await WaitForInteractiveShellAsync(restartedPage).ConfigureAwait(false);
            await restartedPage.Locator(".centered-selection-error").WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual(0, await restartedPage.Locator(".sky-layer-workspace").CountAsync().ConfigureAwait(false));
        }

        await diagnostics.CompleteAsync().ConfigureAwait(false);
        _ = publisher;

        async ValueTask<CenteredView> RegisterStackAsync(CenteredViewExposure reference, IReadOnlyList<CenteredViewExposure> contributors)
        {
            var members = new CenteredViewContributor[contributors.Count];
            for (var index = 0; index < contributors.Count; index++)
            {
                var contributor = contributors[index];
                if (contributor.Capture.CaptureId == reference.Capture.CaptureId)
                {
                    members[index] = new(contributor, nameof(CenteredRegistrationMethod.ReferenceIdentity), JsonSerializer.SerializeToElement(new { }));
                }
                else
                {
                    var sourceEvidence = new CenteredStackSourceEvidence(index, contributor.Capture.CaptureId,
                        CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.VerifiedCentroidSimilarity,
                        new ImageSimilarity(1, 0, 1.0, -1.0), 100);
                    members[index] = new(contributor, nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity),
                        JsonSerializer.SerializeToElement(sourceEvidence, CenteredAcceptanceJson));
                }
            }
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

        async ValueTask<CenteredView?> PublishLayerAsync(ExposureInputV1 source, string baseId, CenteredViewLayerScope scope, string kind, int zOrder, bool enabledByDefault, long order)
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
            return await layerProducer.PublishAsync(baseId, scope, lookup, new(kind, zOrder, enabledByDefault),
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

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CenteredCurrentSkyOrdinaryVirtualSkyAdvancementAndArchiveReopenAsync(CameraPixelFormat pixelFormat)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        const int width = 224;
        const int height = 224;
        const double latitude = 35.347;
        const double longitude = -113.878;
        var fixedSceneUtc = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var wideStars = CreateOrdinaryCenteredWideStars(fixedSceneUtc, width, height, latitude, longitude);

        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ICelestialCatalog>(provider =>
                    new WideFieldBrowserCatalog(
                        wideStars,
                        provider.GetRequiredService<SqliteCelestialCatalog>())));
            },
            settingsOverrides: new Dictionary<string, string?>
            {
                ["CameraAgent:Observatory:LatitudeDegrees"] = "35.347",
                ["CameraAgent:Observatory:LongitudeDegrees"] = "-113.878",
                ["CameraAgent:Observatory:ElevationMeters"] = "0",
                ["CameraAgent:Observatory:TimeZoneId"] = "America/Phoenix"
            },
            prepareRoot: async root =>
            {
                var configPath = Path.Combine(root, "cameraagent.browser.json");
                var document = JsonNode.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false))?.AsObject()
                    ?? throw new InvalidDataException("The browser fixture configuration is invalid.");
                var isBayer = pixelFormat == CameraPixelFormat.BayerRggb16;
                document["centeredExposure"] = new JsonObject
                {
                    ["cameraId"] = "browser-centered-virtual",
                    ["executionVersion"] = CenteredExposureProfile.CurrentExecutionVersion
                };
                document["module"] = JsonNode.Parse(JsonSerializer.Serialize(new
                {
                    type = "VirtualSky",
                    options = new
                    {
                        seed = 1923,
                        fixedSceneUtc = fixedSceneUtc,
                        illuminationMode = "ControlledNight",
                        maximumMagnitude = 6.5,
                        maximumResults = 200,
                        magnitudeZeroElectronsPerSecond = 14000.0,
                        backgroundElectronsPerSecond = 2.0,
                        shotNoiseEnabled = false,
                        readNoiseStandardDeviation = 0.0,
                        rigProfileVersion = isBayer ? "browser-centered-bayer-v1" : "browser-centered-mono16-v1",
                        asi178Sensor = new { enabled = isBayer }
                    }
                }));
                var sensor = document["rig"]!["sensor"]!.AsObject();
                sensor["name"] = isBayer ? "BrowserCenteredBayerRggb16" : "BrowserCenteredMono16";
                sensor["widthPixels"] = width;
                sensor["heightPixels"] = height;
                sensor["pixelSizeMicrons"] = 5.86;
                sensor["colorMode"] = isBayer ? "Color" : "Mono";
                sensor["pixelFormat"] = isBayer ? "BayerRggb16" : "Mono16";
                sensor["responseMode"] = isBayer ? "BayerRaw" : "Monochrome";
                sensor["strideBytes"] = width * 2;
                sensor["byteOrder"] = "LittleEndian";
                sensor["sensorRecipeVersion"] = isBayer ? "browser-centered-bayer-v1" : "browser-centered-mono16-v1";
                var optics = document["rig"]!["optics"]!.AsObject();
                optics["principalPointX"] = width / 2.0;
                optics["principalPointY"] = height / 2.0;
                optics["imageCircleRadiusPixels"] = 0.98 * width / 2.0;
                if (isBayer)
                {
                    document["rig"]!.AsObject().Remove("readout");
                }
                else
                {
                    document["rig"]!["readout"] = JsonNode.Parse($$"""
                        {
                          "roi": { "x": 0, "y": 0, "width": {{width}}, "height": {{height}} },
                          "binX": 1,
                          "binY": 1,
                          "binningAlgorithm": "IdentityV1",
                          "pixelFormat": "Mono16",
                          "sampleDepthBits": 12,
                          "containerDepthBits": 16,
                          "packing": "ByteAligned",
                          "storedCodeTransform": "RightAlignedV1",
                          "levelCodeSpace": "NativeSample",
                          "blackLevel": 0,
                          "whiteLevel": 4095,
                          "strideBytes": {{width * 2}},
                          "byteOrder": "LittleEndian",
                          "cfaPattern": "None"
                        }
                        """);
                }
                document["rig"]!["pipeline"]!["captureInterval"] = "00:00:00.5000000";
                document["rig"]!["pipeline"]!["dayExposure"] = "00:00:00.2500000";
                document["rig"]!["pipeline"]!["nightExposure"] = "00:00:00.2500000";
                document["schedule"]!["setpointProfiles"]![0]!["captureInterval"] = "00:00:00.5000000";
                document["schedule"]!["setpointProfiles"]![0]!["exposure"] = "00:00:00.2500000";
                document["pipeline"]!["steps"] = JsonNode.Parse($$"""
                    [
                      {
                        "id": "Calibration",
                        "type": "Calibration",
                        "order": 10,
                        "enabled": true,
                        "required": false,
                        "dependsOn": ["$raw"],
                        "publication": { "persistence": "durable-local" },
                        "options": {
                          "strategy": "None",
                          "outputVariant": "qualified-linear"
                        }
                      },
                      {
                        "id": "ProjectedScene",
                        "type": "ProjectedScene",
                        "order": 15,
                        "enabled": true,
                        "required": false,
                        "dependsOn": ["$raw"],
                        "publication": { "persistence": "durable-local" }
                      },
                      {
                        "id": "LocalStorage",
                        "type": "Storage",
                        "order": 100,
                        "enabled": true,
                        "dependsOn": ["$raw"],
                        "options": {
                          "storageRoot": {{JsonSerializer.Serialize(root)}},
                          "retentionDays": 1,
                          "updateLatestFrame": true,
                          "queueForUpload": false
                        }
                      }
                    ]
                    """);
                await File.WriteAllTextAsync(configPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

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

        // Ordinary VirtualSky capture (with all real background workers running!) advances naturally
        // across at least two distinct live references (R1 -> R2, R2 != R1) without depending on a
        // racy contributors=5 wall-clock window, then reopens the exact advanced live view in the archive.
        var referenceLocator = page.Locator("[data-centered-reference^='#']");
        await referenceLocator.WaitForAsync(new() { Timeout = 30_000 }).ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync(new() { Timeout = 30_000 }).ConfigureAwait(false);
        // Sample the initial reference and displayed base atomically in one JS turn so live progression
        // between two Playwright round trips cannot skew firstReference against firstBaseId.
        var initial = await page.EvaluateAsync<JsonElement>(
            """
            () => {
                const refEl = document.querySelector('[data-centered-reference]');
                const ws = document.querySelector('.sky-layer-workspace');
                return {
                    reference: refEl?.getAttribute('data-centered-reference') ?? null,
                    baseId: ws?.getAttribute('data-centered-base') ?? null
                };
            }
            """).ConfigureAwait(false);
        var firstReference = initial.GetProperty("reference").GetString();
        var firstBaseId = initial.GetProperty("baseId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(firstReference));
        Assert.IsFalse(string.IsNullOrWhiteSpace(firstBaseId));
        var firstSequence = long.Parse(firstReference.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);

        await page.WaitForFunctionAsync(
            """
            (firstRef) => {
                const refEl = document.querySelector('[data-centered-reference]');
                const ws = document.querySelector('.sky-layer-workspace');
                const canvas = document.querySelector('.sky-layer-canvas--verified');
                const img = document.querySelector('.sky-layer-canvas > img');
                const detail = document.querySelector('.figure-actions a');
                if (!refEl || !ws || !canvas || !img || !img.complete || !detail) return false;
                const refAttr = refEl.getAttribute('data-centered-reference');
                const baseId = ws.getAttribute('data-centered-base');
                const revisionId = ws.getAttribute('data-centered-revision');
                const href = detail.getAttribute('href') ?? '';
                return !!refAttr && refAttr.startsWith('#') && refAttr !== firstRef &&
                    !!baseId && !!revisionId &&
                    href.includes(`base=${baseId}`) && href.includes(`revision=${revisionId}`);
            }
            """,
            firstReference,
            new PageWaitForFunctionOptions { Timeout = 45_000 }).ConfigureAwait(false);

        // Sample the advanced live frame atomically in one JS turn so live progression cannot skew
        // the displayed base/revision against the archive link href.
        var advanced = await page.EvaluateAsync<JsonElement>(
            """
            () => {
                const refEl = document.querySelector('[data-centered-reference]');
                const ws = document.querySelector('.sky-layer-workspace');
                const img = document.querySelector('.sky-layer-canvas > img');
                const detail = document.querySelector('.figure-actions a');
                return {
                    reference: refEl?.getAttribute('data-centered-reference') ?? null,
                    baseId: ws?.getAttribute('data-centered-base') ?? null,
                    revisionId: ws?.getAttribute('data-centered-revision') ?? null,
                    detailHref: detail?.getAttribute('href') ?? null,
                    complete: !!img && img.complete,
                    naturalWidth: img?.naturalWidth ?? 0,
                    naturalHeight: img?.naturalHeight ?? 0
                };
            }
            """).ConfigureAwait(false);

        var secondReference = advanced.GetProperty("reference").GetString();
        var displayedBaseId = advanced.GetProperty("baseId").GetString();
        var displayedRevisionId = advanced.GetProperty("revisionId").GetString();
        var detailHref = advanced.GetProperty("detailHref").GetString()
            ?? throw new AssertFailedException("Expected capture details href.");

        Assert.IsFalse(string.IsNullOrWhiteSpace(secondReference));
        Assert.AreNotEqual(firstReference, secondReference,
            $"Expected ordinary VirtualSky {pixelFormat} live view to advance past {firstReference}.");
        var secondSequence = long.Parse(secondReference.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(firstSequence, secondSequence,
            $"Expected ordinary VirtualSky {pixelFormat} sequence to advance ({firstReference} -> {secondReference}).");
        Assert.IsFalse(string.IsNullOrWhiteSpace(displayedBaseId));
        Assert.AreNotEqual(firstBaseId, displayedBaseId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(displayedRevisionId));
        Assert.IsTrue(
            advanced.GetProperty("complete").GetBoolean() &&
            advanced.GetProperty("naturalWidth").GetInt32() == width &&
            advanced.GetProperty("naturalHeight").GetInt32() == height,
            $"Expected ordinary VirtualSky {pixelFormat} centered base to render at exact {width}x{height} natural dimensions.");

        // Reopen the exact advanced live view in the archive through the "Open capture details" link!
        StringAssert.Contains(detailHref, "/gallery/", StringComparison.Ordinal);
        StringAssert.Contains(detailHref, $"base={displayedBaseId}", StringComparison.Ordinal);
        StringAssert.Contains(detailHref, $"revision={displayedRevisionId}", StringComparison.Ordinal);

        await page.GotoAsync(detailHref).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".centered-archive-bases a").First.WaitForAsync(new() { Timeout = 20_000 }).ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync(new() { Timeout = 20_000 }).ConfigureAwait(false);
        Assert.AreEqual(displayedBaseId,
            await page.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-base").ConfigureAwait(false));
        Assert.AreEqual(displayedRevisionId,
            await page.Locator(".sky-layer-workspace").GetAttributeAsync("data-centered-revision").ConfigureAwait(false));

        await diagnostics.CompleteAsync().ConfigureAwait(false);
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

    private static CelestialCatalogObject[] CreateOrdinaryCenteredWideStars(
        DateTimeOffset sceneUtc, int width, int height, double latitudeDegrees, double longitudeDegrees)
    {
        PixelPoint[] offsets =
        [
            new(30, -53), new(62, -18), new(-16, 21), new(-45, -38), new(-16, -19), new(28, 52),
            new(-60, -22), new(-61, 18), new(-49, 43), new(-22, 45), new(15, -40), new(4, 41), new(41, 11), new(20, 6),
            new(-70, -49), new(-97, -5), new(-21, -94), new(73, 46), new(54, 74), new(-89, -23), new(36, -86), new(-96, 31),
            new(13, -98), new(-81, 52), new(-38, 79), new(-82, 16), new(58, -64), new(68, -41), new(20, 87), new(1, 98)
        ];
        var rig = new CameraRigConfig(
            new SensorProfile("BrowserWide", width, height, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16, SensorResponseMode.Monochrome),
            new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, width / 2d, height / 2d, 0.98 * Math.Min(width, height) / 2d),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1));
        var projection = RigProjectionContextFactory.CreateNative(rig);
        var projector = ProjectorFactory.Create(projection);
        return offsets.Select((offset, index) =>
        {
            var direction = projector.Unproject(new(projection.PrincipalPointX + offset.X, projection.PrincipalPointY + offset.Y));
            Assert.IsNotNull(direction);
            var ofDate = CoordinateTransforms.HorizontalToEquatorial(direction.Value, sceneUtc, latitudeDegrees, longitudeDegrees);
            var star = EquatorialPrecession.PrecessToJ2000(ofDate, sceneUtc);
            var name = $"browser-wide-{index}";
            return new CelestialCatalogObject(name, name, star.RightAscensionHours, star.DeclinationDegrees,
                0.4 + index * 0.025, 0.3 + index % 11 * 0.05);
        }).ToArray();
    }

    private sealed class WideFieldBrowserCatalog(
        IEnumerable<CelestialCatalogObject> objects,
        SqliteCelestialCatalog backing) : ICelestialCatalog, IHipparcosCatalog, ICelestialCatalogMetadataSource, IDeepSkyCatalogSource
    {
        private readonly InMemoryCelestialCatalog _inner = new(objects);

        public CatalogMetadata Metadata => backing.Metadata;

        public string PreprocessingVersion => backing.PreprocessingVersion;

        public string? CatalogId => backing.CatalogId;

        public string? CatalogPackageVersion => backing.CatalogPackageVersion;

        public IDeepSkyCatalog? DeepSky => backing.DeepSky;

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) =>
            _inner.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default) =>
            _inner.QueryCandidatesAsync(query, cancellationToken);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(
            IReadOnlyCollection<string> hipparcosIds, CancellationToken cancellationToken = default) =>
            _inner.GetByHipparcosIdsAsync(hipparcosIds, cancellationToken);
    }
}
