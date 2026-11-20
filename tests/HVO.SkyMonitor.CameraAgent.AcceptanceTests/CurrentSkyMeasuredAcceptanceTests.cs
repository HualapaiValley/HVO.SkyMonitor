using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class RetainedAstrometryBrowserAcceptanceTests
{
    private static readonly string[] RawPredictionDependencies = ["$raw"];
    private static readonly string[] PredictionDependencies = [AstrometryPredictionCaptureProcessingStep.Alias];
    private static readonly string[] AstrometryDependencies = [AstrometryPredictionCaptureProcessingStep.Alias, "Measured"];

    [TestMethod]
    [DataRow("accepted")]
    [DataRow("failure")]
    [DataRow("baseline")]
    public async Task CurrentSkyMeasuredVirtualCaptureRefreshPreservesTimestampAndCaptureContinuityAsync(string scenario)
    {
        var catalogFailure = scenario == "failure";
        var baseline = scenario == "baseline";
        var catalogRoot = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_1901_EVIDENCE_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(catalogRoot));
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceRoot));
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production"));
        Assert.AreEqual(119625L, snapshot.RowCount);
        Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);
        var stars = await snapshot.Catalog.ReadAsync(5, 2500).ConfigureAwait(false);
        Assert.IsTrue(stars.IsCompleteForRequestedMagnitude);
        var selection = new MeasuredAstrometryCatalogSelection(stars.IdentitySha256, stars.SelectionIdentitySha256, 5);
        evidenceRoot = Path.Combine(evidenceRoot, scenario);
        Directory.CreateDirectory(evidenceRoot);
        if (catalogFailure) selection = selection with { SelectionIdentitySha256 = new string('B', 64) };
        await using var host = await CameraAgentKestrelFixture.CreateAsync(prepareRoot: async root =>
        {
            var path = Path.Combine(root, "cameraagent.browser.json");
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
            var installer = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "installer-defaults-2.camera-module.json")).ConfigureAwait(false))!;
            var sensor = document["rig"]!["sensor"]!;
            sensor["widthPixels"] = 1024; sensor["heightPixels"] = 1024; sensor["strideBytes"] = 2048;
            document["rig"]!["readout"] = JsonNode.Parse("""
                { "roi": { "x": 0, "y": 0, "width": 1024, "height": 1024 },
                  "binX": 1, "binY": 1, "binningAlgorithm": "IdentityV1", "pixelFormat": "Mono16",
                  "sampleDepthBits": 16, "containerDepthBits": 16, "packing": "ByteAligned",
                  "storedCodeTransform": "RightAlignedV1", "levelCodeSpace": "NativeSample",
                  "blackLevel": 0, "whiteLevel": 65535, "strideBytes": 2048,
                  "byteOrder": "LittleEndian", "cfaPattern": "None" }
                """);
            var optics = document["rig"]!["optics"]!;
            optics["principalPointX"] = 512; optics["principalPointY"] = 512; optics["imageCircleRadiusPixels"] = 500;
            document["module"]!["options"] = JsonSerializer.SerializeToNode(new
            {
                seed = 1901,
                maximumMagnitude = 5,
                maximumResults = 2000,
                stellarCandidateSelection = "Exhaustive",
                magnitudeZeroElectronsPerSecond = 60000,
                backgroundElectronsPerSecond = 16,
                psfSigmaPixels = 1,
                psfRadiusPixels = 4,
                shotNoiseEnabled = true,
                readNoiseStandardDeviation = 1,
                constellationIds = installer["module"]!["options"]!["constellationIds"],
                includeConstellationEndpointStars = true,
                solarSystemBodies = installer["module"]!["options"]!["solarSystemBodies"]
            });
            var profile = document["schedule"]!["setpointProfiles"]![0]!;
            profile["exposure"] = "00:00:01"; profile["captureInterval"] = "00:00:05";
            document["rig"]!["pipeline"]!["captureInterval"] = "00:00:05";
            document["rig"]!["pipeline"]!["dayExposure"] = "00:00:01";
            document["rig"]!["pipeline"]!["nightExposure"] = "00:00:01";
            document["rig"]!["orientation"]!["boresightAltitudeDegrees"] = 82;
            document["rig"]!["orientation"]!["boresightAzimuthDegrees"] = 137;
            document["rig"]!["orientation"]!["rollAdjustmentDegrees"] = 13;
            // Preserve the installer topology and rich scene options. Only fixture resolution and roots differ.
            document["pipeline"] = installer["pipeline"]!.DeepClone();
            var steps = document["pipeline"]!["steps"]!.AsArray();
            JsonNode Find(string id) => steps.Single(step => step!["id"]!.GetValue<string>() == id)!;
            Find("LocalStorage")["options"]!["storageRoot"] = root;
            Find("ArchiveStorage")["options"]!["storageRoot"] = Path.Combine(root, "archive");
            foreach (var id in (string[])["CloudPresentation", "EnvironmentPresentation"])
            {
                Find(id)["options"]!["widthPixels"] = 1024;
                Find(id)["options"]!["heightPixels"] = 1024;
            }
            if (!baseline)
            {
                steps.Add(JsonSerializer.SerializeToNode(new
                {
                    id = AstrometryPredictionCaptureProcessingStep.Alias,
                    type = AstrometryPredictionCaptureProcessingStep.Alias,
                    required = false,
                    dependsOn = RawPredictionDependencies,
                    publication = new { persistence = "durable-local" }
                }));
                steps.Add(JsonSerializer.SerializeToNode(new
                {
                    id = "Measured",
                    type = "MeasuredStellarAssociations",
                    required = false,
                    dependsOn = PredictionDependencies,
                    publication = new { persistence = "durable-local" },
                    options = new { useAstrometryPrediction = true }
                }));
                steps.Add(JsonSerializer.SerializeToNode(new
                {
                    id = "Astrometry",
                    type = "MeasuredAstrometry",
                    required = false,
                    dependsOn = AstrometryDependencies,
                    publication = new { persistence = "durable-local" },
                    options = new
                    {
                        useAstrometryPrediction = true,
                        captureStride = 60,
                        catalogSelection = selection,
                        recipe = new MeasuredAstrometryOptions(Solver: new(MaximumCatalogMagnitude: 5))
                    }
                }));
            }
            await File.WriteAllTextAsync(path, document.ToJsonString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "qualified-config.json"), document.ToJsonString()).ConfigureAwait(false);
        }, scratchRoot: evidenceRoot, preserveRoot: true, productionCatalogRoot: catalogRoot,
            settingsOverrides: new Dictionary<string, string?>
            {
                ["CameraAgent:Observatory:LatitudeDegrees"] = "35.13",
                ["CameraAgent:Observatory:LongitudeDegrees"] = "-113.91",
                ["CameraAgent:Observatory:ElevationMeters"] = "1100",
                ["CameraAgent:Observatory:TimeZoneId"] = "America/Phoenix"
            }).ConfigureAwait(false);
        var reader = host.Services.GetRequiredService<ICameraAgentRetainedAstrometryService>();
        var gallery = host.Services.GetRequiredService<ICameraAgentGallery>();
        if (baseline)
        {
            var (captures, stack) = await WaitForFullStackAsync(gallery).ConfigureAwait(false);
            await WriteGraphTimingsAsync(host, captures, stack, evidenceRoot).ConfigureAwait(false);
            Assert.IsNull(await reader.GetLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            return;
        }
        if (catalogFailure)
        {
            var (captures, stack) = await WaitForFullStackAsync(gallery).ConfigureAwait(false);
            Assert.IsTrue(captures.Items.Any(c => c.ProcessingNodes.Any(n => n.NodeId == "Astrometry" && n.Status == "TerminalFailure")));
            Assert.IsTrue(stack.Artifacts.Any(a => a.Variant == "installer-annotated-preview" && a.Availability == "Available"));
            Assert.IsNull(await reader.GetLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            using var failurePlaywright = await Playwright.CreateAsync().ConfigureAwait(false);
            await using var failureBrowser = await failurePlaywright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
            await using var failureContext = await failureBrowser.NewContextAsync(new() { BaseURL = host.BaseAddress.ToString() }).ConfigureAwait(false);
            var failurePage = await failureContext.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(failurePage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
            await failurePage.GotoAsync("/").ConfigureAwait(false);
            Assert.Contains("No accepted measured result", await failurePage.Locator(".latest-measured-solution").InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
            Assert.IsTrue(await failurePage.Locator(".pixel-sky-calculator button").First.IsDisabledAsync().ConfigureAwait(false));
            await failurePage.ScreenshotAsync(new() { Path = Path.Combine(evidenceRoot, "no-accepted-raw-continues.png"), FullPage = true }).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "failure-evidence.json"), JsonSerializer.Serialize(new { captures, root = host.Root }, AstrometryBrowserJson)).ConfigureAwait(false);
            return;
        }
        var admission = host.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        using (var firstCaptureDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            while ((await gallery.GetPageAsync(new(PageSize: 2), firstCaptureDeadline.Token).ConfigureAwait(false)).Items.Count == 0)
                await Task.Delay(100, firstCaptureDeadline.Token).ConfigureAwait(false);
            await admission.PauseAsync("issue1901-proof-pause", null, "disposable-proof", "Hold accepted source for exact-frame controls", firstCaptureDeadline.Token).ConfigureAwait(false);
        }
        CameraAgentLatestAstrometry? accepted = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (accepted is null)
        {
            await Task.Delay(1000, deadline.Token).ConfigureAwait(false);
            accepted = await reader.GetLatestAcceptedAsync(deadline.Token).ConfigureAwait(false);
        }
        Assert.IsTrue(double.IsFinite(accepted.RightAscensionHours));
        Assert.IsNotNull(accepted.LocalScale.XArcsecondsPerPixel);
        var beforeCaptures = await gallery.GetPageAsync(new(PageSize: 20), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotEmpty(beforeCaptures.Items);
        var resourceSamples = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (var latestRead in new[] { false, true })
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var rss = process.WorkingSet64;
            var ioBefore = await File.ReadAllTextAsync("/proc/self/io").ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            if (latestRead) Assert.IsNotNull(await reader.GetLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
            else Assert.AreEqual(accepted.SolutionIdentitySha256, (await reader.GetAsync(accepted.CaptureId, CancellationToken.None).ConfigureAwait(false)).SolutionIdentitySha256);
            process.Refresh();
            Assert.IsTrue(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
            Assert.IsTrue(GC.GetTotalAllocatedBytes(true) - allocated < 256L * 1024 * 1024);
            resourceSamples.Add(new
            {
                operation = latestRead ? "latest-summary" : "selected-capture-baseline",
                wallMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                rssBefore = rss,
                rssAfter = process.WorkingSet64,
                ioBefore,
                ioAfter = await File.ReadAllTextAsync("/proc/self/io").ConfigureAwait(false)
            });
        }
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new() { BaseURL = host.BaseAddress.ToString(), ViewportSize = new() { Width = 1440, Height = 1100 } }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GotoAsync("/").ConfigureAwait(false);
        var summary = page.Locator(".latest-measured-solution");
        await summary.Locator("time").WaitForAsync().ConfigureAwait(false);
        var first = await summary.InnerTextAsync().ConfigureAwait(false);
        foreach (var field in new[] { "Measured center RA / Dec", "Measured roll", "arcsec/pixel", accepted.MidpointUtc.ToString("O"),
            FormattableString.Invariant($"{accepted.RightAscensionHours:0.######} h / {accepted.DeclinationDegrees:0.######}°"),
            FormattableString.Invariant($"{accepted.RollDegrees:0.######}°"),
            FormattableString.Invariant($"{accepted.LocalScale.XArcsecondsPerPixel:0.####} / {accepted.LocalScale.YArcsecondsPerPixel:0.####} arcsec/pixel") })
            Assert.Contains(field, first, StringComparison.OrdinalIgnoreCase);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidenceRoot, "latest-before-refresh.png"), FullPage = true }).ConfigureAwait(false);
        Assert.HasCount(1, await page.Locator(".astrometry-card .latest-measured-solution").AllAsync().ConfigureAwait(false));
        Assert.IsEmpty(await page.Locator(".sky-inspector > .latest-measured-solution").AllAsync().ConfigureAwait(false));
        var canonical = await VerifyCoexistingScenesAsync(host, accepted, evidenceRoot).ConfigureAwait(false);
        await VerifyCurrentMeasuredControlsAsync(page, host, accepted, evidenceRoot).ConfigureAwait(false);
        await VerifyRichSceneControlsAsync(page, canonical, evidenceRoot).ConfigureAwait(false);
        await VerifyCoexistingReplayAsync(host, accepted, evidenceRoot).ConfigureAwait(false);
        await admission.ResumeAsync("issue1901-proof-resume", null, "disposable-proof", "Verify unsolved refresh and raw continuity", CancellationToken.None).ConfigureAwait(false);
        var (afterCaptures, fullStack) = await WaitForFullStackAsync(gallery).ConfigureAwait(false);
        Assert.IsTrue(fullStack.ProcessingNodes.Any(n => n.NodeId == "LocalStorage" && n.Status == "Completed"));
        Assert.IsTrue(fullStack.ProcessingNodes.Any(n => n.NodeId == "ArchiveStorage" && n.Status == "Completed"));
        Assert.IsTrue(fullStack.ProcessingNodes.Any(n => n.NodeId == "Astrometry" && n.Status == "Skipped"));
        Assert.IsTrue(fullStack.Artifacts.Any(a => a.Variant == "installer-annotated-preview" && a.Availability == "Available"));
        await page.Locator("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await summary.GetByText("For a different capture.", new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        Assert.Contains(accepted.MidpointUtc.ToString("O"), await summary.InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
        Assert.IsTrue(await page.Locator(".pixel-sky-calculator button").First.IsDisabledAsync().ConfigureAwait(false));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidenceRoot, "latest-after-refresh.png"), FullPage = true }).ConfigureAwait(false);
        await VerifyUnsolvedMeasuredControlsAsync(page, accepted, evidenceRoot).ConfigureAwait(false);
        await VerifySceneLayerControlsAsync(page, ["Constellation lines"]).ConfigureAwait(false);
        await WriteGraphTimingsAsync(host, afterCaptures, fullStack, evidenceRoot).ConfigureAwait(false);
        Assert.IsTrue(afterCaptures.Items.Max(c => c.CaptureSequence) > beforeCaptures.Items.Max(c => c.CaptureSequence), "Durable raw capture must advance while no new solution is produced.");
        var retained = await reader.GetAsync(accepted.CaptureId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(accepted.SolutionIdentitySha256, retained.SolutionIdentitySha256);
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, retained.Solution!.Assessment.Status);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "latest-evidence.json"), JsonSerializer.Serialize(new
        {
            accepted,
            first,
            afterRefresh = await summary.InnerTextAsync().ConfigureAwait(false),
            root = host.Root,
            address = host.BaseAddress,
            catalogRows = snapshot.RowCount,
            snapshot.DatabaseSha256,
            beforeCaptures,
            afterCaptures,
            fullStack,
            resourceSamples,
            backlog = host.Services.GetRequiredService<CaptureLaneState>().Snapshot
        }, AstrometryBrowserJson)).ConfigureAwait(false);
    }

    private static async Task<(CameraAgentGalleryPage Captures, CameraAgentGalleryCapture Stack)> WaitForFullStackAsync(
        ICameraAgentGallery gallery)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        while (true)
        {
            var captures = await gallery.GetPageAsync(new(PageSize: 20), deadline.Token).ConfigureAwait(false);
            var stack = captures.Items.FirstOrDefault(c => c.CaptureSequence >= 5 &&
                c.ProcessingNodes.Any(n => n.NodeId == "Telemetry" && n.Status == "Completed") &&
                c.Artifacts.Any(a => a.Variant == "rolling-mean" && a.SourceArtifactIds.Count == 5));
            if (stack is not null) return (captures, stack);
            await Task.Delay(500, deadline.Token).ConfigureAwait(false);
        }
    }

    private static async Task WriteGraphTimingsAsync(CameraAgentKestrelFixture host, CameraAgentGalleryPage captures,
        CameraAgentGalleryCapture stack, string output)
    {
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        var executions = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 20, CancellationToken.None)
            .ConfigureAwait(false);
        var observations = new List<object>();
        foreach (var capture in captures.Items.Where(c => c.CaptureSequence <= stack.CaptureSequence).OrderBy(c => c.CaptureSequence))
        {
            var execution = executions.Single(e => e.CaptureId == capture.CaptureId);
            var detail = await operations.ReadExecutionDetailAsync(execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            observations.Add(new
            {
                capture.CaptureSequence,
                capture.ExposureStartedUtc,
                capture.DurableIngressUtc,
                execution.StartedUtc,
                execution.CompletedUtc,
                rawBytes = capture.Artifacts.Single(a => a.Role == FrameArtifactRole.Raw).ByteLength,
                outputs = capture.Artifacts.Select(a => new { a.Variant, a.ByteLength, sourceCount = a.SourceArtifactIds.Count }),
                nodes = detail.Nodes.Select(n => new { n.NodeId, n.Status, n.Reason, elapsed = n.CompletedUtc - n.StartedUtc })
            });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "graph-timings.json"), JsonSerializer.Serialize(new
        {
            observations,
            backlog = host.Services.GetRequiredService<CaptureLaneState>().Snapshot
        }, AstrometryBrowserJson)).ConfigureAwait(false);
    }
}
