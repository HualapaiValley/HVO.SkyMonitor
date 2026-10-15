using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    private static readonly JsonSerializerOptions MeasuredTrailEvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task MeasuredTrailsUseRealCaptureEvidenceAndFrozenSaveAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        Guid captureId = default;
        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            services =>
            {
                // Pause background acquisition, retention and finalization for explicit evidence checkpoints.
                foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IHostedService) &&
                    (value.ImplementationType == typeof(CameraCaptureService) ||
                     value.ImplementationType == typeof(RetentionBackgroundService) ||
                     value.ImplementationFactory?.Method.ReturnType == typeof(TransientWorkerService))).ToArray())
                    services.Remove(descriptor);
            },
            prepareRoot: async root => captureId = await MeasuredTrailBrowserFixture.PrepareAsync(root).ConfigureAwait(false),
            useTransientEvidence: true)
            .ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(), ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var output = Path.Combine(TestContext.TestRunDirectory!, "issue-1005");
        Directory.CreateDirectory(output);
        var apiRoot = $"/api/v1/operations/gallery/{captureId:D}";
        foreach (var suffix in new[] { "/presentation", "/presentation.svg" })
            await AssertStatusAsync(context.APIRequest.GetAsync(apiRoot + suffix), 401).ConfigureAwait(false);
        await AssertStatusAsync(context.APIRequest.PostAsync(apiRoot + "/materializations",
            new() { DataObject = new { enabledLayerIdentitySha256 = Array.Empty<string>() } }), 401).ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await AssertStatusAsync(context.APIRequest.PostAsync(apiRoot + "/materializations",
            new() { DataObject = new { enabledLayerIdentitySha256 = Array.Empty<string>() } }), 400).ConfigureAwait(false);
        await using (var viewer = await diagnostics.NewContextAsync(new() { BaseURL = host.BaseAddress.ToString() }).ConfigureAwait(false))
        {
            var viewerPage = await viewer.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(viewerPage, CameraAgentKestrelFixture.NonOwnerEmail, CameraAgentKestrelFixture.NonOwnerPassword).ConfigureAwait(false);
            foreach (var suffix in new[] { "/presentation", "/presentation.svg" })
                await AssertStatusAsync(viewer.APIRequest.GetAsync(apiRoot + suffix), 403).ConfigureAwait(false);
            await AssertStatusAsync(viewer.APIRequest.PostAsync(apiRoot + "/materializations",
                new() { DataObject = new { enabledLayerIdentitySha256 = Array.Empty<string>() } }), 403).ConfigureAwait(false);
        }
        await page.GotoAsync("/").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.GetByText("No recorded candidate for this capture", new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        await ScreenshotAsync("current-no-candidate").ConfigureAwait(false);

        await page.GotoAsync($"/gallery/{captureId:D}").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var toggle = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Measured transient trails" }).Locator("input");
        await toggle.WaitForAsync().ConfigureAwait(false);
        Assert.IsFalse(await toggle.IsCheckedAsync().ConfigureAwait(false));
        var projection = host.Services.GetRequiredService<ICameraAgentTransientOperatorProjection>();
        var provisional = await projection.GetCaptureTrailsAsync(captureId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("MeasuredEvidence", provisional.State, JsonSerializer.Serialize(provisional, MeasuredTrailEvidenceJsonOptions));
        Assert.AreEqual("ProvisionalCandidate", provisional.Trails.Single().State);
        var provisionalPresentation = (await host.Services.GetRequiredService<ICameraAgentLayeredPresentationService>()
            .GetAsync(captureId, CancellationToken.None).ConfigureAwait(false)).Presentation!;
        await AssertSvgAsync(provisionalPresentation, null).ConfigureAwait(false);
        await ScreenshotAsync("historical-before-select").ConfigureAwait(false);
        Assert.IsFalse(await toggle.IsDisabledAsync().ConfigureAwait(false),
            await page.Locator(".sky-layer-controls").InnerTextAsync().ConfigureAwait(false));
        await toggle.CheckAsync().ConfigureAwait(false);
        await page.GetByText("Positional uncertainty unavailable. No uncertainty band is drawn.", new() { Exact = true })
            .WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(1, await page.Locator(".source-list a[href^='/transients/']").CountAsync().ConfigureAwait(false));
        await ScreenshotAsync("historical-provisional-selected").ConfigureAwait(false);
        await toggle.UncheckAsync().ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh measured evidence", Exact = true }).ClickAsync().ConfigureAwait(false);
        Assert.IsFalse(await toggle.IsCheckedAsync().ConfigureAwait(false));

        // Let the actual worker finalize its immutable receipts, with the browser host stopped.
        await host.RunWithHostStoppedAsync(MeasuredTrailBrowserFixture.FinalizeAsync).ConfigureAwait(false);
        await page.GotoAsync(new Uri(host.BaseAddress, $"/gallery/{captureId:D}").ToString()).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        projection = host.Services.GetRequiredService<ICameraAgentTransientOperatorProjection>();
        var final = await projection.GetCaptureTrailsAsync(captureId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("MeasuredEvidence", final.State);
        Assert.IsFalse(final.Incomplete, final.Reason);
        var presentation = await host.Services.GetRequiredService<ICameraAgentLayeredPresentationService>()
            .GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(presentation.Presentation);
        Assert.AreNotEqual(provisionalPresentation.ManifestIdentitySha256, presentation.Presentation.ManifestIdentitySha256);
        await AssertSvgAsync(presentation.Presentation, provisionalPresentation.SvgChecksumSha256).ConfigureAwait(false);
        var trailLayer = presentation.Presentation.Layers.Single(value => value.Kind == TransientTrailPresentation.LayerKind);
        toggle = page.Locator($".sky-layer-controls input[data-layer-identity='{trailLayer.IdentitySha256}']");
        await toggle.CheckAsync().ConfigureAwait(false);
        await ScreenshotAsync("historical-final-selected").ConfigureAwait(false);
        await page.Locator(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        var savedLink = page.Locator(".sky-layer-result:visible a").Filter(new() { HasText = "Download original full-resolution stack" });
        await savedLink.WaitForAsync().ConfigureAwait(false);
        var savedUrl = new Uri(host.BaseAddress, (await savedLink.GetAttributeAsync("href").ConfigureAwait(false))!);
        var savedId = Guid.Parse(savedUrl.Segments[^2].Trim('/'));
        var savedOutput = await host.Services.GetRequiredService<SqliteCaptureProcessingStore>()
            .ReadOutputByArtifactIdAsync(savedId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(savedOutput);
        CollectionAssert.Contains(savedOutput.Artifact.SourceArtifactIds.ToArray(), presentation.Presentation.ManifestArtifactId);
        var savedBytes = await File.ReadAllBytesAsync(Path.Combine(host.Root, savedOutput.PayloadRelativePath)).ConfigureAwait(false);
        Assert.AreEqual(savedOutput.Artifact.ChecksumSha256, PayloadChecksum.ComputeSha256(savedBytes));
        await ScreenshotAsync("frozen-stack-saved").ConfigureAwait(false);
        var candidateHref = await page.Locator(".source-list a[href^='/transients/']").GetAttributeAsync("href").ConfigureAwait(false);
        await page.Locator(".source-list a[href^='/transients/']").ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == candidateHref).ConfigureAwait(false);
        await ScreenshotAsync("candidate-navigation").ConfigureAwait(false);

        var identities = Path.Combine(output, "identities.json");
        await File.WriteAllTextAsync(identities, JsonSerializer.Serialize(new
        {
            captureId, measuredUtc = DateTimeOffset.UtcNow,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            provisional, final,
            presentation.Presentation.ManifestIdentitySha256,
            presentation.Presentation.PresentationIdentitySha256,
            presentation.Presentation.SvgChecksumSha256,
            presentation.Presentation.BaseArtifactId,
            trailLayer.IdentitySha256,
            savedId,
            savedOutput.Artifact.SourceArtifactIds,
            savedChecksumSha256 = PayloadChecksum.ComputeSha256(savedBytes),
            limitation = "Deterministic VirtualSky fixture; no physical sensitivity or orbital-correlation claim."
        }, MeasuredTrailEvidenceJsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(identities);
        // An explicitly requested bounded hold lets native T3 preview inspect this same authenticated host.
        if (int.TryParse(Environment.GetEnvironmentVariable("HVO_ISSUE1005_BROWSER_HOLD_SECONDS"), out var seconds) &&
            seconds is > 0 and <= 600)
        {
            var addressPath = Environment.GetEnvironmentVariable("HVO_ISSUE1005_BROWSER_ADDRESS_PATH");
            if (!string.IsNullOrWhiteSpace(addressPath))
                await File.WriteAllTextAsync(addressPath, new Uri(host.BaseAddress, $"/gallery/{captureId:D}").ToString()).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        }
        await diagnostics.CompleteAsync().ConfigureAwait(false);

        async Task AssertSvgAsync(CameraAgentLayeredPresentation expected, string? staleChecksum)
        {
            var response = await context.APIRequest.GetAsync(new Uri(host.BaseAddress, apiRoot + "/presentation.svg").ToString(),
                new() { Headers = staleChecksum is null ? null : new Dictionary<string, string> { ["If-None-Match"] = $"\"{staleChecksum}\"" } })
                .ConfigureAwait(false);
            try
            {
                Assert.AreEqual(200, response.Status);
                Assert.AreEqual("private, no-cache", response.Headers["cache-control"]);
                Assert.AreEqual(expected.SvgChecksumSha256, PayloadChecksum.ComputeSha256(await response.BodyAsync().ConfigureAwait(false)));
            }
            finally { await response.DisposeAsync().ConfigureAwait(false); }
        }

        static async Task AssertStatusAsync(Task<IAPIResponse> request, int expected)
        {
            var response = await request.ConfigureAwait(false);
            try { Assert.AreEqual(expected, response.Status, response.Url); }
            finally { await response.DisposeAsync().ConfigureAwait(false); }
        }

        async Task ScreenshotAsync(string name)
        {
            var path = Path.Combine(output, name + ".png");
            await page.ScreenshotAsync(new() { Path = path, FullPage = true }).ConfigureAwait(false);
            TestContext.AddResultFile(path);
        }
    }
}
