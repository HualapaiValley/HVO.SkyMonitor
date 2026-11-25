using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    private const int QualWidth = 160;
    private const int QualHeight = 128;
    private const int QualBlackLevel = 64;
    private const int QualWhiteLevel = 4095;
    private const double QualOverlayTolerancePixels = 0.85;
    private static readonly PixelPoint QualRedTarget = new(32, 22);
    private static readonly PixelPoint QualGreenTarget = new(80, 22);
    private static readonly PixelPoint QualBlueTarget = new(128, 22);
    private static readonly PixelPoint QualNeutralTarget = new(78, 108);
    private static readonly (double X, double Y, double Peak)[] QualStars =
    [
        (24.3, 48.4, 2400), (42.7, 46.2, 2300), (62.4, 49.8, 2200), (88.6, 44.5, 2100),
        (114.2, 48.3, 2000), (136.5, 52.6, 1900), (26.8, 70.3, 1800), (50.5, 68.7, 1700),
        (74.2, 65.4, 1600), (98.4, 69.2, 1500), (124.7, 67.5, 1400), (140.2, 76.4, 1300),
        (28.4, 92.6, 1200), (52.3, 88.4, 1150), (76.8, 86.2, 1100), (118.6, 88.9, 1050),
        (40.2, 108.5, 1000), (132.4, 106.2, 950)
    ];

    [TestMethod]
    [DataRow("Mono16-Full", CameraPixelFormat.Mono16, 0, 0)]
    [DataRow("BayerRGGB16-Full", CameraPixelFormat.BayerRggb16, 0, 0)]
    [DataRow("BayerRGGB16-OddOrigin-BGGR", CameraPixelFormat.BayerRggb16, 1, 1)]
    public async Task CenteredPipelineQualificationCurrentSkyArchiveAndOperationsAsync(
        string modeName, CameraPixelFormat pixelFormat, int cfaOriginX, int cfaOriginY)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        ArgumentException.ThrowIfNullOrWhiteSpace(modeName);
        var bayer = pixelFormat == CameraPixelFormat.BayerRggb16;
        var baseUtc = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new QualificationHostClock { UtcNow = baseUtc };
        var cameraId = $"qual-browser-{modeName}";

        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            configureServices: services =>
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
                services.AddSingleton(new CameraModuleRegistration("QualRetained", typeof(QualificationBrowserRetainedCameraModule)));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            },
            prepareRoot: async root =>
            {
                var configPath = Path.Combine(root, "cameraagent.browser.json");
                var doc = JsonNode.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false))!.AsObject();
                doc["module"] = new JsonObject { ["type"] = "QualRetained" };
                doc["centeredExposure"] = new JsonObject
                {
                    ["cameraId"] = cameraId,
                    ["executionVersion"] = CenteredExposureProfile.CurrentExecutionVersion
                };
                var sensor = doc["rig"]!["sensor"]!.AsObject();
                sensor["name"] = $"BrowserQual-{modeName}";
                sensor["widthPixels"] = QualWidth + cfaOriginX * 2;
                sensor["heightPixels"] = QualHeight + cfaOriginY * 2;
                sensor["pixelSizeMicrons"] = 2.4;
                sensor["colorMode"] = bayer ? "Color" : "Mono";
                sensor["pixelFormat"] = bayer ? "BayerRggb16" : "Mono16";
                sensor["responseMode"] = bayer ? "BayerRaw" : "Monochrome";
                sensor["strideBytes"] = QualWidth * 2;
                sensor["byteOrder"] = "LittleEndian";
                var nonDefaultOrientation = cfaOriginX != 0 || cfaOriginY != 0;
                doc["rig"]!["orientation"] = JsonNode.Parse($$"""
                    {
                      "boresightAltitudeDegrees": {{(nonDefaultOrientation ? "85.0" : "90.0")}},
                      "boresightAzimuthDegrees": {{(nonDefaultOrientation ? "180.0" : "0.0")}},
                      "rollAdjustmentDegrees": {{(nonDefaultOrientation ? "15.0" : "0.0")}}
                    }
                    """);
                doc["rig"]!["optics"] = JsonNode.Parse($$"""
                    {
                      "projectionModel": "Perspective",
                      "focalLengthMillimeters": 8.0,
                      "fieldOfViewDegrees": 90.0,
                      "rollDegrees": 0.0,
                      "lensKind": "Rectilinear",
                      "principalPointX": {{(QualWidth + cfaOriginX * 2) / 2.0}},
                      "principalPointY": {{(QualHeight + cfaOriginY * 2) / 2.0}},
                      "imageCircleRadiusPixels": null,
                      "horizontalFlip": {{(nonDefaultOrientation ? "true" : "false")}},
                      "calibrationVersion": "browser-qual-optics-v1"
                    }
                    """);
                doc["rig"]!["readout"] = JsonNode.Parse($$"""
                    {
                      "roi": { "x": {{cfaOriginX}}, "y": {{cfaOriginY}}, "width": {{QualWidth}}, "height": {{QualHeight}} },
                      "binX": 1, "binY": 1, "binningAlgorithm": "IdentityV1",
                      "pixelFormat": "{{(bayer ? "BayerRggb16" : "Mono16")}}",
                      "sampleDepthBits": 12, "containerDepthBits": 16, "packing": "ByteAligned",
                      "storedCodeTransform": "RightAlignedV1", "levelCodeSpace": "NativeSample",
                      "blackLevel": {{QualBlackLevel}}, "whiteLevel": {{QualWhiteLevel}},
                      "strideBytes": {{QualWidth * 2}}, "byteOrder": "LittleEndian",
                      "cfaPattern": "{{(bayer ? "Rggb" : "None")}}",
                      "cfaOriginX": {{(bayer ? cfaOriginX.ToString(CultureInfo.InvariantCulture) : "null")}},
                      "cfaOriginY": {{(bayer ? cfaOriginY.ToString(CultureInfo.InvariantCulture) : "null")}}
                    }
                    """);
                doc["rig"]!["pipeline"]!["captureInterval"] = "00:00:02";
                doc["rig"]!["pipeline"]!["dayExposure"] = "00:00:00.2500000";
                doc["rig"]!["pipeline"]!["nightExposure"] = "00:00:00.2500000";
                doc["schedule"]!["setpointProfiles"]![0]!["captureInterval"] = "00:00:02";
                doc["schedule"]!["setpointProfiles"]![0]!["exposure"] = "00:00:00.2500000";
                var storage = doc["pipeline"]!["steps"]!.AsArray()
                    .Single(step => (string?)step?["type"] == "Storage")!.DeepClone();
                storage["dependsOn"] = new JsonArray("$raw");
                doc["pipeline"]!["steps"] = JsonNode.Parse("""
                    [
                      {
                        "id": "Calibration",
                        "type": "Calibration",
                        "order": 10,
                        "enabled": true,
                        "required": false,
                        "dependsOn": ["$raw"],
                        "publication": { "persistence": "durable-local" },
                        "options": { "strategy": "None", "outputVariant": "qualified-linear" }
                      }
                    ]
                    """);
                doc["pipeline"]!["steps"]!.AsArray().Add(storage);
                await File.WriteAllTextAsync(configPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
                    .ConfigureAwait(false);
            },
            scratchRoot: Path.GetTempPath()).ConfigureAwait(false);

        await host.Services.GetRequiredService<CameraAgentConfigurationInitializer>().StartAsync(token).ConfigureAwait(false);
        var activeConfig = await host.Services.GetRequiredService<ICameraAgentConfigurationAccessor>()
            .WaitForConfigurationAsync(token).ConfigureAwait(false);
        var runtime = host.Services.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
        await runtime.InitializeAsync(activeConfig, token).ConfigureAwait(false);
        using var grantWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        grantWait.CancelAfter(TimeSpan.FromSeconds(5));
        var grant = (await runtime.WaitForGrantAsync(grantWait.Token).ConfigureAwait(false)).Evidence;
        var schedule = new CenteredScheduleBinding(
            grant.ScheduleRevisionSha256, grant.LocalProfileSha256, grant.SetpointProfileId, grant.IntervalId,
            grant.OverrideId, CaptureCadenceMode.MinimumStartInterval);
        var ingress = host.Services.GetRequiredService<RawCaptureIngress>();
        var coordinator = host.Services.GetRequiredService<CenteredWindowCoordinator>();
        var registration = host.Services.GetRequiredService<CenteredWindowRegistrationWorker>();
        var singlePublisher = host.Services.GetRequiredService<CenteredViewPublicationWorker>();
        var windowPublisher = host.Services.GetRequiredService<CenteredViewWindowPublicationWorker>();
        var views = host.Services.GetRequiredService<SqliteCenteredViews>();
        var persistence = host.Services.GetRequiredService<CaptureProcessingPersistence>();
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        var layout = new FrameLayoutDescriptor(
            QualWidth, QualHeight, QualWidth * 2, pixelFormat, FrameByteOrder.LittleEndian, 12, 16,
            FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            QualBlackLevel, QualWhiteLevel, QualWidth * QualHeight * 2)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                QualWidth + cfaOriginX * 2, QualHeight + cfaOriginY * 2,
                cfaOriginX, cfaOriginY, QualWidth, QualHeight, 1, 1, FrameBinningAlgorithm.IdentityV1,
                bayer ? cfaOriginX : null, bayer ? cfaOriginY : null)
        };

        // Admit 5 exposures at t=0, 2, 4, 6, 8s (seq 1..5); omit t=10s so centered window #3 gets all 5
        // contributors while overlapping window #4 becomes a genuine 4-contributor PARTIAL stack (missing slot +2)!
        (double Dx, double Dy)[] shifts =
        [
            (-2.50, +1.50), // #1 (slot -2 for #3)
            (-1.25, +0.75), // #2 (slot -1 for #3, -2 for #4)
            (0.00, 0.00),   // #3 (reference 1)
            (+1.25, -0.75), // #4 (reference 2)
            (+2.50, -1.50)  // #5 (contributor +2 for #3, +1 for #4; carries moving trail)
        ];
        var admitted = new List<(ExposureKey Key, ReconstructionDescriptor Descriptor)>();
        CenteredViewStream? stream = null;
        for (var i = 0; i < shifts.Length; i++)
        {
            var scheduled = baseUtc.AddSeconds(i * 2);
            var rawBytes = RenderQualFrame(pixelFormat, cfaOriginX, cfaOriginY, shifts[i].Dx, shifts[i].Dy);
            var receipt = await AdmitQualFrameAsync(
                ingress, clock, activeConfig, schedule, scheduled, layout, rawBytes, $"qual-{i + 1}", token)
                .ConfigureAwait(false);
            await RunStandardLaneAsync(host.Services, activeConfig, token).ConfigureAwait(false);
            stream ??= CenteredViewLookup.ResolveStream(
                await ingress.ReadCurrentAsync(new(activeConfig.AgentId!, cameraId), token).ConfigureAwait(false));
            Assert.IsNotNull(stream);
            admitted.Add((new(stream.Camera, stream.RunId, receipt.Manifest.Descriptor.Capture.CaptureId),
                receipt.Manifest.Descriptor));
            await singlePublisher.DrainCameraAsync(stream.Camera, stream, CancellationToken.None, token)
                .ConfigureAwait(false);
            await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
            await registration.RegisterPendingAsync(token).ConfigureAwait(false);
            await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);
        }

        // Advance clock past window #4 deadline so window #4 freezes with missing slot +2 -> 4-frame partial stack!
        clock.UtcNow = baseUtc.AddSeconds(30);
        await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
        await registration.RegisterPendingAsync(token).ConfigureAwait(false);
        await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);

        Assert.IsNotNull(stream);
        var single3 = (await views.ReadArchiveAsync(stream, admitted[2].Key.CaptureId, token).ConfigureAwait(false))
            .Single(item => item.Base.Definition.Kind == CenteredViewKind.Single);
        var stack3 = (await views.ReadArchiveAsync(stream, admitted[2].Key.CaptureId, token).ConfigureAwait(false))
            .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
        var stack4 = (await views.ReadArchiveAsync(stream, admitted[3].Key.CaptureId, token).ConfigureAwait(false))
            .Single(item => item.Base.Definition.Kind == CenteredViewKind.Stack);
        Assert.AreEqual(5, stack3.Base.Definition.Contributors.Count);
        Assert.AreEqual(4, stack4.Base.Definition.Contributors.Count);

        // Publish a reference-marker layer on stack #3 and stack #4 + a moving trail on exposure #5 so
        // BOTH overlapping stacks #3 and #4 carry base-scoped reference markers and moving trail SVG coordinates!
        var refStar3 = new PixelPoint(QualStars[0].X, QualStars[0].Y);
        var refStar4 = new PixelPoint(QualStars[0].X - 1.25, QualStars[0].Y + 0.75);
        await PublishQualReferenceMarkerAsync(
            persistence, layerProducer, admitted[2].Key, admitted[2].Descriptor, stack3.Base.BaseId, layout, refStar3, token)
            .ConfigureAwait(false);
        await PublishQualReferenceMarkerAsync(
            persistence, layerProducer, admitted[3].Key, admitted[3].Descriptor, stack4.Base.BaseId, layout, refStar4, token)
            .ConfigureAwait(false);
        var trailStart5 = new PixelPoint(40.0, 56.0);
        var trailEnd5 = new PixelPoint(72.0, 74.0);
        await PublishQualTrailAsync(
            persistence, layerProducer, admitted[4].Key, admitted[4].Descriptor, layout,
            trailStart5, trailEnd5, token).ConfigureAwait(false);

        if (cfaOriginX != 0 || cfaOriginY != 0)
        {
            Assert.AreEqual(15.0, activeConfig.Rig!.Orientation!.RollAdjustmentDegrees, 1e-6);
            Assert.IsTrue(activeConfig.Rig.Optics!.HorizontalFlip);
            Assert.AreEqual(15.0, stack3.Base.Definition.Reference.Input.Geometry.Orientation.RollAdjustmentDegrees, 1e-6);
        }

        stack3 = (await views.ReadArchiveAsync(stream, admitted[2].Key.CaptureId, token).ConfigureAwait(false))
            .Single(item => item.Base.BaseId == stack3.Base.BaseId);
        stack4 = (await views.ReadArchiveAsync(stream, admitted[3].Key.CaptureId, token).ConfigureAwait(false))
            .Single(item => item.Base.BaseId == stack4.Base.BaseId);

        // Select Single #3 live first, then advance to Stack #3, then Partial Stack #4.
        Assert.IsNotNull(await views.SelectLiveAsync(stream, single3.Base.BaseId, token).ConfigureAwait(false));

        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);
        page.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        page.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);

        // 1. Current Sky: Single #3 (dimensions + JPEG decode + Chromium canvas pixel colors + reference markers)
        await page.GotoAsync("/").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator($".sky-layer-workspace[data-centered-base='{single3.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        await VerifyRenderedImageAndColorsAsync(page, context, bayer, single3.Base.Definition.Preview.PayloadSha256,
            $"{modeName}-current-sky-single3", offsetX: 0, offsetY: 0, token).ConfigureAwait(false);

        // 2. Current Sky: 5-frame Full Stack #3 (colors + SVG trail coordinates matching 5->3 displacement (+2.5, -1.5))
        clock.UtcNow = clock.UtcNow.AddSeconds(20);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, stack3.Base.BaseId, token).ConfigureAwait(false));
        await page.Locator($".sky-layer-workspace[data-centered-base='{stack3.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual("5 frames", await page.Locator("[data-centered-contributors]").InnerTextAsync().ConfigureAwait(false));
        await VerifyRenderedImageAndColorsAsync(page, context, bayer, stack3.Base.Definition.Preview.PayloadSha256,
            $"{modeName}-current-sky-stack3", offsetX: 0, offsetY: 0, token).ConfigureAwait(false);
        var liveRefMarker3 = await ReadBrowserReferenceMarkerAsync(page).ConfigureAwait(false);
        AssertQualPoint(liveRefMarker3, refStar3, $"{modeName}-current-sky-stack3-ref-marker");
        var liveTrail3 = await ReadBrowserTrailLineAsync(page).ConfigureAwait(false);
        AssertQualTrail(liveTrail3, new(trailStart5.X + 2.5, trailStart5.Y - 1.5), new(trailEnd5.X + 2.5, trailEnd5.Y - 1.5),
            $"{modeName}-current-sky-stack3-trail");

        // 3. Current Sky: 4-frame Partial Stack #4 (missing slot +2; colors + distinct SVG trail matching 5->4 (+1.25, -0.75))
        clock.UtcNow = clock.UtcNow.AddSeconds(20);
        Assert.IsNotNull(await views.SelectLiveAsync(stream, stack4.Base.BaseId, token).ConfigureAwait(false));
        await page.Locator($".sky-layer-workspace[data-centered-base='{stack4.Base.BaseId}']").WaitForAsync().ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual("4 frames", await page.Locator("[data-centered-contributors]").InnerTextAsync().ConfigureAwait(false));
        await VerifyRenderedImageAndColorsAsync(page, context, bayer, stack4.Base.Definition.Preview.PayloadSha256,
            $"{modeName}-current-sky-partial-stack4", offsetX: -1.25, offsetY: +0.75, token).ConfigureAwait(false);
        var liveRefMarker4 = await ReadBrowserReferenceMarkerAsync(page).ConfigureAwait(false);
        AssertQualPoint(liveRefMarker4, refStar4, $"{modeName}-current-sky-partial-stack4-ref-marker");
        var liveTrail4 = await ReadBrowserTrailLineAsync(page).ConfigureAwait(false);
        AssertQualTrail(liveTrail4, new(trailStart5.X + 1.25, trailStart5.Y - 0.75), new(trailEnd5.X + 1.25, trailEnd5.Y - 0.75),
            $"{modeName}-current-sky-partial-stack4-trail");
        Assert.IsGreaterThan(0.9, Math.Abs(liveTrail3.X1 - liveTrail4.X1),
            "Overlapping stacks #3 and #4 must display distinct base-scoped trail coordinates in Chromium.");
        Assert.IsGreaterThan(0.9, Math.Abs(liveRefMarker3.X - liveRefMarker4.X),
            "Overlapping stacks #3 and #4 must display distinct base-scoped reference marker coordinates in Chromium.");

        // 4. Shared Archive Viewer: reopen Single #3, Stack #3, and Partial Stack #4!
        await page.GotoAsync($"/gallery/{admitted[2].Key.CaptureId:D}?base={single3.Base.BaseId}&revision={single3.LatestRevisionId}")
            .ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(2, await page.Locator(".centered-archive-bases a").CountAsync().ConfigureAwait(false));
        await VerifyRenderedImageAndColorsAsync(page, context, bayer, single3.Base.Definition.Preview.PayloadSha256,
            $"{modeName}-archive-single3", offsetX: 0, offsetY: 0, token).ConfigureAwait(false);

        await page.GotoAsync($"/gallery/{admitted[2].Key.CaptureId:D}?base={stack3.Base.BaseId}&revision={stack3.LatestRevisionId}")
            .ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        await VerifyRenderedImageAndColorsAsync(page, context, bayer, stack3.Base.Definition.Preview.PayloadSha256,
            $"{modeName}-archive-stack3", offsetX: 0, offsetY: 0, token).ConfigureAwait(false);
        var archiveRefMarker3 = await ReadBrowserReferenceMarkerAsync(page).ConfigureAwait(false);
        AssertQualPoint(archiveRefMarker3, refStar3, $"{modeName}-archive-stack3-ref-marker");
        var archiveTrail3 = await ReadBrowserTrailLineAsync(page).ConfigureAwait(false);
        AssertQualTrail(archiveTrail3, new(trailStart5.X + 2.5, trailStart5.Y - 1.5), new(trailEnd5.X + 2.5, trailEnd5.Y - 1.5),
            $"{modeName}-archive-stack3-trail");

        await page.GotoAsync($"/gallery/{admitted[3].Key.CaptureId:D}?base={stack4.Base.BaseId}&revision={stack4.LatestRevisionId}")
            .ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas--verified").WaitForAsync().ConfigureAwait(false);
        var archiveRefMarker4 = await ReadBrowserReferenceMarkerAsync(page).ConfigureAwait(false);
        AssertQualPoint(archiveRefMarker4, refStar4, $"{modeName}-archive-partial-stack4-ref-marker");
        var archiveTrail4 = await ReadBrowserTrailLineAsync(page).ConfigureAwait(false);
        AssertQualTrail(archiveTrail4, new(trailStart5.X + 1.25, trailStart5.Y - 0.75), new(trailEnd5.X + 1.25, trailEnd5.Y - 0.75),
            $"{modeName}-archive-partial-stack4-trail");

        // Verify direct gallery artifact preview HTTP endpoint (/api/v1/operations/artifacts/{rawId}/preview)
        // preserves delivered CFA phase (including odd-origin (1,1) BGGR) over HTTP!
        var rawArtifactPreviewUrl = $"/api/v1/operations/artifacts/{admitted[2].Descriptor.Artifact.ArtifactId:D}/preview";
        var rawPreviewResp = await context.APIRequest.GetAsync(rawArtifactPreviewUrl).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, rawPreviewResp.Status, $"Expected 200 OK from {rawArtifactPreviewUrl}.");
            var rawJpeg = await rawPreviewResp.BodyAsync().ConfigureAwait(false);
            var decodedRawPreview = JpegImageCodec.DecodeJpeg(rawJpeg, token);
            Assert.AreEqual(QualWidth, decodedRawPreview.Width);
            Assert.AreEqual(QualHeight, decodedRawPreview.Height);
            AssertDecodedQualColors(decodedRawPreview, bayer, $"{modeName}-http-artifact-preview", 0, 0);
        }
        finally
        {
            await rawPreviewResp.DisposeAsync().ConfigureAwait(false);
        }

        // 5. Operations (/operations/pipeline/running): must identify actual input layout, CFA origin, partial stack & revision link!
        await page.GotoAsync("/operations/pipeline/running").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var displayRegion = page.GetByRole(AriaRole.Region, new() { Name = "Live display publication" });
        await Assertions.Expect(displayRegion).ToBeVisibleAsync().ConfigureAwait(false);
        var displayText = await displayRegion.InnerTextAsync().ConfigureAwait(false);
        StringAssert.Contains(displayText, bayer ? "BayerRggb16 (color)" : "Mono16 (mono)", StringComparison.Ordinal);
        StringAssert.Contains(displayText, bayer ? $"origin ({cfaOriginX}, {cfaOriginY})" : "No CFA", StringComparison.Ordinal);
        StringAssert.Contains(displayText, "Capture #4", StringComparison.Ordinal);
        StringAssert.Contains(displayText, "4/5 window slots contributed", StringComparison.Ordinal);
        StringAssert.Contains(displayText, nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity), StringComparison.Ordinal);
        var expectedRevHref = CenteredViewLinks.Revision(admitted[3].Key.CaptureId, stack4.Base.BaseId, stack4.LatestRevisionId);
        await Assertions.Expect(displayRegion.GetByRole(AriaRole.Link, new() { Name = "Open exact stack revision" }))
            .ToHaveAttributeAsync("href", expectedRevHref).ConfigureAwait(false);

        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task VerifyRenderedImageAndColorsAsync(
        IPage page, IBrowserContext context, bool bayer, string expectedSha256,
        string label, double offsetX, double offsetY, CancellationToken token)
    {
        var imgInfo = await page.EvaluateAsync<JsonElement>(
            """
            ([red, green, blue, neutral]) => {
                const img = document.querySelector('.sky-layer-canvas > img');
                if (!img || !img.complete || img.naturalWidth <= 0 || img.naturalHeight <= 0) {
                    throw new Error('Rendered centered preview img is missing or incomplete.');
                }
                const canvas = document.createElement('canvas');
                canvas.width = img.naturalWidth;
                canvas.height = img.naturalHeight;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0);
                const sample = (pt) => {
                    const cx = Math.round(pt.x), cy = Math.round(pt.y);
                    const data = ctx.getImageData(cx - 2, cy - 2, 5, 5).data;
                    let r = 0, g = 0, b = 0, n = 0;
                    for (let i = 0; i < data.length; i += 4) {
                        r += data[i]; g += data[i + 1]; b += data[i + 2]; n++;
                    }
                    return { r: r / n, g: g / n, b: b / n };
                };
                return {
                    src: img.getAttribute('src'),
                    width: img.naturalWidth,
                    height: img.naturalHeight,
                    red: sample(red),
                    green: sample(green),
                    blue: sample(blue),
                    neutral: sample(neutral)
                };
            }
            """,
            new object[]
            {
                new { x = QualRedTarget.X + offsetX, y = QualRedTarget.Y + offsetY },
                new { x = QualGreenTarget.X + offsetX, y = QualGreenTarget.Y + offsetY },
                new { x = QualBlueTarget.X + offsetX, y = QualBlueTarget.Y + offsetY },
                new { x = QualNeutralTarget.X + offsetX, y = QualNeutralTarget.Y + offsetY }
            }).ConfigureAwait(false);

        Assert.AreEqual(QualWidth, imgInfo.GetProperty("width").GetInt32(), $"{label}: naturalWidth");
        Assert.AreEqual(QualHeight, imgInfo.GetProperty("height").GetInt32(), $"{label}: naturalHeight");
        var imgSrc = imgInfo.GetProperty("src").GetString() ?? throw new AssertFailedException($"{label}: missing img src.");

        // Download actual published JPEG bytes served to the browser and verify exact SHA-256 + decoded pixels.
        var response = await context.APIRequest.GetAsync(imgSrc).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, response.Status, $"{label}: expected 200 OK for {imgSrc}.");
            var bytes = await response.BodyAsync().ConfigureAwait(false);
            Assert.AreEqual(expectedSha256, PayloadChecksum.ComputeSha256(bytes), $"{label}: retained preview SHA-256 mismatch.");
            var decoded = JpegImageCodec.DecodeJpeg(bytes, token);
            Assert.AreEqual(QualWidth, decoded.Width);
            Assert.AreEqual(QualHeight, decoded.Height);
            AssertDecodedQualColors(decoded, bayer, label, offsetX, offsetY);
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }

        // Also assert Chromium's actual rendered <canvas> pixels match the expected color/mono contract!
        if (bayer)
        {
            var red = imgInfo.GetProperty("red");
            var green = imgInfo.GetProperty("green");
            var blue = imgInfo.GetProperty("blue");
            var neutral = imgInfo.GetProperty("neutral");
            Assert.IsGreaterThan(red.GetProperty("b").GetDouble() * 1.8, red.GetProperty("r").GetDouble(), $"{label}: browser red target");
            Assert.IsGreaterThan(blue.GetProperty("r").GetDouble() * 1.8, blue.GetProperty("b").GetDouble(), $"{label}: browser blue target");
            Assert.IsGreaterThan(green.GetProperty("r").GetDouble() + 25, green.GetProperty("g").GetDouble(), $"{label}: browser green target");
            Assert.IsGreaterThan(green.GetProperty("b").GetDouble() + 25, green.GetProperty("g").GetDouble(), $"{label}: browser green target");
            var nr = neutral.GetProperty("r").GetDouble();
            var ng = neutral.GetProperty("g").GetDouble();
            var nb = neutral.GetProperty("b").GetDouble();
            Assert.IsLessThanOrEqualTo(15.0, Math.Max(Math.Abs(nr - ng), Math.Abs(ng - nb)), $"{label}: browser neutral target");
        }
        else
        {
            var neutral = imgInfo.GetProperty("neutral");
            var nr = neutral.GetProperty("r").GetDouble();
            var ng = neutral.GetProperty("g").GetDouble();
            var nb = neutral.GetProperty("b").GetDouble();
            Assert.IsLessThanOrEqualTo(2.0, Math.Max(Math.Abs(nr - ng), Math.Abs(ng - nb)), $"{label}: browser mono grayscale");
        }
    }

    private static void AssertDecodedQualColors(
        DecodedImage image, bool bayer, string label, double offsetX, double offsetY)
    {
        if (!bayer)
        {
            if (image.PixelFormat == CameraPixelFormat.Mono8)
            {
                return;
            }
            var span = image.PixelData.Span;
            for (var y = 12; y < image.Height - 12; y += 12)
            {
                for (var x = 12; x < image.Width - 12; x += 12)
                {
                    var o = y * image.StrideBytes + x * 3;
                    Assert.IsLessThanOrEqualTo(2, Math.Max(Math.Abs(span[o] - span[o + 1]), Math.Abs(span[o + 1] - span[o + 2])),
                        $"{label}: false color on mono JPEG at ({x},{y})");
                }
            }
            return;
        }

        Assert.AreEqual(CameraPixelFormat.Rgb24, image.PixelFormat, $"{label}: color preview must decode as Rgb24.");
        var r = SampleRgb(image, QualRedTarget.X + offsetX, QualRedTarget.Y + offsetY);
        var g = SampleRgb(image, QualGreenTarget.X + offsetX, QualGreenTarget.Y + offsetY);
        var b = SampleRgb(image, QualBlueTarget.X + offsetX, QualBlueTarget.Y + offsetY);
        var n = SampleRgb(image, QualNeutralTarget.X + offsetX, QualNeutralTarget.Y + offsetY);
        Assert.IsGreaterThan(r.B * 2.0, r.R, $"{label}: red target {r}");
        Assert.IsGreaterThan(b.R * 2.0, b.B, $"{label}: blue target {b}");
        Assert.IsGreaterThan(g.R + 30, g.G, $"{label}: green target {g}");
        Assert.IsGreaterThan(g.B + 30, g.G, $"{label}: green target {g}");
        Assert.IsLessThanOrEqualTo(14.0, Math.Max(Math.Abs(n.R - n.G), Math.Abs(n.G - n.B)), $"{label}: neutral target {n}");

        static (double R, double G, double B) SampleRgb(DecodedImage img, double cx, double cy)
        {
            var ix = (int)Math.Round(cx);
            var iy = (int)Math.Round(cy);
            var span = img.PixelData.Span;
            double sr = 0, sg = 0, sb = 0;
            var count = 0;
            for (var y = iy - 3; y <= iy + 3; y++)
            {
                for (var x = ix - 3; x <= ix + 3; x++)
                {
                    var o = y * img.StrideBytes + x * 3;
                    sr += span[o];
                    sg += span[o + 1];
                    sb += span[o + 2];
                    count++;
                }
            }
            return (sr / count, sg / count, sb / count);
        }
    }

    private static async Task<PixelPoint> ReadBrowserReferenceMarkerAsync(IPage page)
    {
        const string selector = ".sky-layer-overlay svg g[data-layer-kind='star-annotations'] circle";
        await page.Locator(selector).First.WaitForAsync().ConfigureAwait(false);
        var coords = await page.EvaluateAsync<JsonElement>(
            """
            (sel) => {
                const circle = document.querySelector(sel);
                if (!circle) throw new Error('Reference marker SVG circle not found.');
                return {
                    x: parseFloat(circle.getAttribute('cx')),
                    y: parseFloat(circle.getAttribute('cy'))
                };
            }
            """, selector).ConfigureAwait(false);
        return new PixelPoint(coords.GetProperty("x").GetDouble(), coords.GetProperty("y").GetDouble());
    }

    private static void AssertQualPoint(PixelPoint actual, PixelPoint expected, string label)
    {
        Assert.AreEqual(expected.X, actual.X, QualOverlayTolerancePixels, $"{label}: X");
        Assert.AreEqual(expected.Y, actual.Y, QualOverlayTolerancePixels, $"{label}: Y");
    }

    private static async Task<(double X1, double Y1, double X2, double Y2)> ReadBrowserTrailLineAsync(IPage page)
    {
        var selector = $".sky-layer-overlay svg g[data-layer-kind='{TransientTrailPresentation.LayerKind}'] line";
        await page.Locator(selector).First.WaitForAsync().ConfigureAwait(false);
        var coords = await page.EvaluateAsync<JsonElement>(
            """
            (sel) => {
                const line = document.querySelector(sel);
                if (!line) throw new Error('Trail SVG line not found.');
                return {
                    x1: parseFloat(line.getAttribute('x1')),
                    y1: parseFloat(line.getAttribute('y1')),
                    x2: parseFloat(line.getAttribute('x2')),
                    y2: parseFloat(line.getAttribute('y2'))
                };
            }
            """, selector).ConfigureAwait(false);
        return (
            coords.GetProperty("x1").GetDouble(),
            coords.GetProperty("y1").GetDouble(),
            coords.GetProperty("x2").GetDouble(),
            coords.GetProperty("y2").GetDouble());
    }

    private static void AssertQualTrail(
        (double X1, double Y1, double X2, double Y2) actual, PixelPoint start, PixelPoint end, string label)
    {
        Assert.AreEqual(start.X, actual.X1, QualOverlayTolerancePixels, $"{label}: X1");
        Assert.AreEqual(start.Y, actual.Y1, QualOverlayTolerancePixels, $"{label}: Y1");
        Assert.AreEqual(end.X, actual.X2, QualOverlayTolerancePixels, $"{label}: X2");
        Assert.AreEqual(end.Y, actual.Y2, QualOverlayTolerancePixels, $"{label}: Y2");
    }

    private static byte[] RenderQualFrame(
        CameraPixelFormat format, int cfaOriginX, int cfaOriginY, double shiftDx, double shiftDy)
    {
        var bayer = format == CameraPixelFormat.BayerRggb16;
        var bytes = new byte[QualWidth * QualHeight * 2];
        for (var y = 0; y < QualHeight; y++)
        {
            for (var x = 0; x < QualWidth; x++)
            {
                var rx = x + shiftDx;
                var ry = y + shiftDy;
                var channel = bayer ? (((y + cfaOriginY) & 1, (x + cfaOriginX) & 1) switch
                {
                    (0, 0) => 0,
                    (1, 1) => 2,
                    _ => 1
                }) : -1;
                double value = 110;
                if (Math.Abs(rx - QualRedTarget.X) <= 6 && Math.Abs(ry - QualRedTarget.Y) <= 6)
                    value = !bayer ? 1800 : channel == 0 ? 3200 : 110;
                else if (Math.Abs(rx - QualGreenTarget.X) <= 6 && Math.Abs(ry - QualGreenTarget.Y) <= 6)
                    value = !bayer ? 1800 : channel == 1 ? 3200 : 110;
                else if (Math.Abs(rx - QualBlueTarget.X) <= 6 && Math.Abs(ry - QualBlueTarget.Y) <= 6)
                    value = !bayer ? 1800 : channel == 2 ? 3200 : 110;
                else if (Math.Abs(rx - QualNeutralTarget.X) <= 6 && Math.Abs(ry - QualNeutralTarget.Y) <= 6)
                    value = 1800;

                var px = x + 0.5 + shiftDx;
                var py = y + 0.5 + shiftDy;
                foreach (var (sx, sy, peak) in QualStars)
                {
                    var dx = px - sx;
                    var dy = py - sy;
                    var r2 = dx * dx + dy * dy;
                    if (r2 < 25)
                        value += peak * Math.Exp(-0.5 * r2 / (1.15 * 1.15));
                }

                var code = (ushort)Math.Clamp(Math.Round(value), 0, QualWhiteLevel);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * QualWidth + x) * 2), code);
            }
        }
        return bytes;
    }

    private static async Task<RawCaptureReceipt> AdmitQualFrameAsync(
        RawCaptureIngress ingress,
        QualificationHostClock clock,
        CameraModuleConfig config,
        CenteredScheduleBinding schedule,
        DateTimeOffset scheduledUtc,
        FrameLayoutDescriptor layout,
        byte[] rawBytes,
        string sourceId,
        CancellationToken token)
    {
        var exposure = TimeSpan.FromMilliseconds(250);
        var cadence = TimeSpan.FromSeconds(2);
        clock.UtcNow = scheduledUtc;
        var reservation = await ingress.ReserveCenteredAsync(
            config, new(null, null, null, null, "browser-qual"), null, token, schedule: schedule)
            .ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(exposure, 10, null, null);
        var request = new CaptureRequest(scheduledUtc, cadence, CaptureMode.Still, setpoint)
        {
            BoundGraphRevisionId = reservation.Revision.State.RevisionId,
            BoundGraph = reservation.Revision.Pipeline
        };
        await ingress.DispatchCenteredAsync(
            reservation.Pending.Key, setpoint, null, "browser-qual", token, scheduledUtc, cadence)
            .ConfigureAwait(false);
        var endUtc = scheduledUtc.Add(exposure);
        var readoutUtc = endUtc.AddMilliseconds(10);
        var frame = new CameraFrame(
            scheduledUtc, layout.Width, layout.Height, layout.PixelFormat, rawBytes,
            new FrameMetadata(exposure, 10, -10, SourceId: sourceId), layout.StrideBytes)
        {
            Layout = layout
        };
        var result = new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
        {
            AcquisitionTiming = new(scheduledUtc, endUtc, readoutUtc)
        };
        clock.UtcNow = readoutUtc.AddMilliseconds(1);
        return await ingress.AcceptCenteredAsync(
            config, new(request, result, scheduledUtc, cadence, readoutUtc - scheduledUtc), reservation, token)
            .ConfigureAwait(false);
    }

    private static async Task RunStandardLaneAsync(IServiceProvider services, CameraModuleConfig config, CancellationToken token)
    {
        var laneStore = services.GetRequiredService<ICaptureLaneStore>();
        var lane = services.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
        var lease = await laneStore.ClaimAsync(lane, "browser-qual", config, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await services.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, token)
            .ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, token).ConfigureAwait(false);
    }

    private static async Task PublishQualReferenceMarkerAsync(
        CaptureProcessingPersistence persistence,
        CenteredViewLayerProducer layerProducer,
        ExposureKey sourceKey,
        ReconstructionDescriptor rawDescriptor,
        string baseId,
        FrameLayoutDescriptor layout,
        PixelPoint marker,
        CancellationToken token)
    {
        var rawArtifact = new ProcessingArtifact(
            rawDescriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, rawDescriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawDescriptor.Artifact.Recipe).IdentitySha256,
            rawDescriptor.Artifact.MediaType, layout, Array.Empty<byte>(),
            rawDescriptor.Artifact.CreatedUtc, rawDescriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(rawDescriptor))
        {
            CaptureId = sourceKey.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(rawDescriptor)
        };
        var payload = PresentationLayerPayloadJson.Create(
            rawArtifact.DescriptorIdentitySha256!, layout.Width, layout.Height,
            markers: [new(marker, 4, new(120, 220, 255))]);
        var product = PresentationProcessingProducts.CreateLayerProduct(
            payload, "browser-qual-ref-marker-source", [rawArtifact], "browser-qual-ref-marker-v1");
        await persistence.PersistCenteredViewProductAsync(rawDescriptor, product, token).ConfigureAwait(false);
        var slot = new ExposureProductSlot(Guid.NewGuid(), "star-annotations", payload.SchemaVersion, product.Variant);
        var productRef = new ExposureProductRef(
            sourceKey, slot, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
        var lookup = new ExposureProductLookup(sourceKey, slot, ExposureProductAvailability.Available, 1, productRef, null);
        await layerProducer.PublishAsync(
            baseId, CenteredViewLayerScope.Reference, lookup, new("star-annotations", 20, true),
            rawDescriptor.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
    }

    private static async Task PublishQualTrailAsync(
        CaptureProcessingPersistence persistence,
        CenteredViewLayerProducer layerProducer,
        ExposureKey sourceKey,
        ReconstructionDescriptor rawDescriptor,
        FrameLayoutDescriptor layout,
        PixelPoint start,
        PixelPoint end,
        CancellationToken token)
    {
        var rawArtifact = new ProcessingArtifact(
            rawDescriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, rawDescriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawDescriptor.Artifact.Recipe).IdentitySha256,
            rawDescriptor.Artifact.MediaType, layout, Array.Empty<byte>(),
            rawDescriptor.Artifact.CreatedUtc, rawDescriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(rawDescriptor))
        {
            CaptureId = sourceKey.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(rawDescriptor)
        };
        var payload = PresentationLayerPayloadJson.Create(
            rawArtifact.DescriptorIdentitySha256!, layout.Width, layout.Height,
            segments: [new(start, end, 2, new(255, 210, 90))]);
        var product = PresentationProcessingProducts.CreateLayerProduct(
            payload, "browser-qual-trail-source", [rawArtifact], "browser-qual-trail-v1");
        await persistence.PersistCenteredViewProductAsync(rawDescriptor, product, token).ConfigureAwait(false);
        var slot = new ExposureProductSlot(Guid.NewGuid(), "measured-trails", payload.SchemaVersion, product.Variant);
        var productRef = new ExposureProductRef(
            sourceKey, slot, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
        var lookup = new ExposureProductLookup(sourceKey, slot, ExposureProductAvailability.Available, 1, productRef, null);
        await layerProducer.PublishToContributorsAsync(
            lookup, new(TransientTrailPresentation.LayerKind, 500, true),
            rawDescriptor.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
    }

    private sealed class QualificationHostClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => UtcNow.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Activated by CameraModuleFactory via dependency injection.")]
    private sealed class QualificationBrowserRetainedCameraModule : ICameraModule, ICameraBoundExposureCapture, ICameraSetpointController
    {
        public string Id => "qual-browser-retained";
        public string DisplayName => "Qualification Browser Retained Camera";
        public string ModuleType => "QualRetained";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public bool EnforcesBoundStartLimit => true;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Qualification host admits retained exposures directly.");
        public Task<CaptureResult> CaptureBoundAsync(ExposureAttemptKey attempt, CaptureRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Qualification host admits retained exposures directly.");
        public ValueTask<DateTimeOffset> ApplySetpointAsync(CaptureSetpoint setpoint, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DateTimeOffset.UtcNow);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
