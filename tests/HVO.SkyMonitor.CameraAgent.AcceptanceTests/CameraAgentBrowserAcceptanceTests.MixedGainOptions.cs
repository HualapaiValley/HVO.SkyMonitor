using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    [TestMethod]
    public async Task OwnerMixedGainOptionsRenderEditRejectSaveAndReloadAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var host = await CameraAgentKestrelFixture.CreateAsync(prepareRoot: async root =>
        {
            var path = Path.Combine(root, "cameraagent.browser.json");
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
            configuration["pipeline"]!["steps"]!.AsArray().Insert(2, JsonNode.Parse("""
                {
                  "id": "MixedGain", "type": "RollingCombination", "order": 20,
                  "required": false, "enabled": true, "dependsOn": ["$raw"],
                  "options": {
                    "recipe": "mixed-gain-normalized-mean", "windowSize": 7,
                    "outputVariant": "mixed-retained", "normalizationPlan": { "future": 17 }
                  }
                }
                """));
            await File.WriteAllTextAsync(path, configuration.ToJsonString(new() { WriteIndented = true })).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);

        // Exercise the real VirtualSky capture pipeline without requiring a calibration family or activating a draft.
        await page.WaitForFunctionAsync("""
            async () => {
                const response = await fetch('/api/v1/operations/gallery?pageSize=1');
                return response.ok && (await response.json()).items.length > 0;
            }
            """, options: new() { PollingInterval = 100 }).ConfigureAwait(false);
        var response = await context.APIRequest.GetAsync("/api/v1/operations/gallery?pageSize=1").ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, response.Status);
            var gallery = JsonSerializer.Deserialize<CameraAgentGalleryPage>(await response.TextAsync().ConfigureAwait(false), JsonSerializerOptions.Web);
            Assert.IsNotNull(gallery);
            Assert.AreEqual(GalleryEvidenceOrigin.Simulated, gallery.Items.Single().EvidenceOrigin);
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }

        var operations = host.Services.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var before = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var sourceId = before.ActiveRevisionId;
        var original = await operations.ReadRevisionPipelineAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
        var originalOptions = original!.Steps.Single(step => step.Id == "MixedGain").Options!.Value;
        await page.GotoAsync("/operations/pipeline/graphs/new?from=" + sourceId).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var panel = page.Locator("section[aria-label='Mixed-gain rolling combination']");
        await VisibleAsync(panel.GetByRole(AriaRole.Heading, new() { Name = "Mixed-gain rolling combination", Level = 3 })).ConfigureAwait(false);
        var size = panel.Locator("input[id$='-windowSize']");
        await Assertions.Expect(size).ToHaveValueAsync("7").ConfigureAwait(false);
        foreach (var width in new[] { 1440, 390, 320 })
        {
            await page.SetViewportSizeAsync(width, 900).ConfigureAwait(false);
            await AssertPageStructureAsync(page, "/operations/pipeline/graphs/new").ConfigureAwait(false);
            Assert.IsFalse(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1")
                .ConfigureAwait(false), $"Mixed-gain options overflow at {width}px.");
            await AssertComputedContrastAsync(page, "/operations/pipeline/graphs/new", new ViewportSize { Width = width, Height = 900 })
                .ConfigureAwait(false);
        }

        var preview = page.GetByRole(AriaRole.Button, new() { Name = "Preview plan", Exact = true });
        var save = page.GetByRole(AriaRole.Button, new() { Name = "Save immutable draft", Exact = true });
        await size.FillAsync("8").ConfigureAwait(false);
        await size.PressAsync("Tab").ConfigureAwait(false);
        await preview.ClickAsync().ConfigureAwait(false);
        await Assertions.Expect(save).ToBeEnabledAsync().ConfigureAwait(false);
        await size.FillAsync("0").ConfigureAwait(false);
        await size.PressAsync("Tab").ConfigureAwait(false);
        await preview.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".option-errors[role='alert']").First).ConfigureAwait(false);
        Assert.IsTrue(await save.IsDisabledAsync().ConfigureAwait(false));
        Assert.AreEqual(before.Revisions.Count, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).Revisions.Count);

        await size.FillAsync("8").ConfigureAwait(false);
        await size.PressAsync("Tab").ConfigureAwait(false);
        var age = panel.Locator("input[id$='-maximumAgeMilliseconds']");
        await age.FillAsync("12.5").ConfigureAwait(false);
        await age.PressAsync("Tab").ConfigureAwait(false);
        await preview.ClickAsync().ConfigureAwait(false);
        await Assertions.Expect(save).ToBeEnabledAsync().ConfigureAwait(false);
        await save.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByText("saved as an immutable revision")).ConfigureAwait(false);
        var after = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(sourceId, after.ActiveRevisionId);
        var saved = after.Revisions.Single(revision => before.Revisions.All(old => old.RevisionId != revision.RevisionId));
        var pipeline = await operations.ReadRevisionPipelineAsync(saved.RevisionId, CancellationToken.None).ConfigureAwait(false);
        var options = pipeline!.Steps.Single(step => step.Id == "MixedGain").Options!.Value;
        Assert.AreEqual(8, options.GetProperty("windowSize").GetInt32());
        Assert.AreEqual(12.5, options.GetProperty("maximumAgeMilliseconds").GetDouble());
        foreach (var key in new[] { "recipe", "outputVariant", "normalizationPlan" })
            Assert.AreEqual(originalOptions.GetProperty(key).GetRawText(), options.GetProperty(key).GetRawText());
        var untouched = await operations.ReadRevisionPipelineAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(originalOptions.GetRawText(), untouched!.Steps.Single(step => step.Id == "MixedGain").Options!.Value.GetRawText());

        await page.GotoAsync("/operations/pipeline/graphs/new?from=" + saved.RevisionId).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await Assertions.Expect(size).ToHaveValueAsync("8").ConfigureAwait(false);
        await Assertions.Expect(age).ToHaveValueAsync("12.5").ConfigureAwait(false);
        await preview.ClickAsync().ConfigureAwait(false);
        Assert.IsTrue(await save.IsDisabledAsync().ConfigureAwait(false), "Reloading an unchanged revision cannot create another draft.");
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }
}
