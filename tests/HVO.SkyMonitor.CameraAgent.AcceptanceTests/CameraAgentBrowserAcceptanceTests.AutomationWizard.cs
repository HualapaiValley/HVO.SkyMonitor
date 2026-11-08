using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    private static readonly string WizardBrowserTarget = "browser-preset:" + new string('a', 64);

    [TestMethod]
    public async Task SourceWindowWizardPreviewsSavesAndEditsWithKeyboardFocusAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        var site = DeploymentLocationSnapshot.Create("wizard-browser", 1, "fixture", null,
            DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 1000, "America/Phoenix");
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILocalAutomationTaskRegistry>(new WizardBrowserRegistry()));
            services.Replace(ServiceDescriptor.Singleton<IObservingDayCalendarProvider>(
                new FixedObservingDayCalendarProvider(ObservingDayCalendar.ForDeployment(site))));
        }).ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new() { Width = 390, Height = 844 }
        }).ConfigureAwait(false);
        await context.RouteAsync(url => !url.StartsWith(host.BaseAddress.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase), route => route.AbortAsync()).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GotoAsync("/operations/automations").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator("#automation-create").ClickAsync().ConfigureAwait(false);
        await page.Locator("#automation-id").FillAsync("browser-hourly").ConfigureAwait(false);
        await page.Locator("#automation-name").FillAsync("Browser hourly").ConfigureAwait(false);
        await page.Locator("#automation-save").ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'automation-source-window'").ConfigureAwait(false);
        await page.Locator("#automation-source-window").SelectOptionAsync(nameof(LocalAutomationSourceWindowKind.CompletedCivilHour)).ConfigureAwait(false);
        await page.Locator("#automation-settle").FillAsync("120").ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1").ConfigureAwait(false));
        await page.Locator("#automation-save").ClickAsync().ConfigureAwait(false);
        await page.Locator("#automation-window-preview").WaitForAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'automation-cancel'").ConfigureAwait(false);
        var preview = await page.Locator("#automation-window-preview").InnerTextAsync().ConfigureAwait(false);
        Assert.Contains("America/Phoenix", preview, StringComparison.Ordinal);
        Assert.Contains("-07:00", preview, StringComparison.Ordinal);
        Assert.Contains("UTC", preview, StringComparison.Ordinal);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1").ConfigureAwait(false));
        Assert.IsEmpty((await host.Services.GetRequiredService<ILocalAutomationStore>().GetStateAsync(CancellationToken.None)
            .ConfigureAwait(false)).Definitions, "Preview must not save a definition.");
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await page.Locator("#automation-settle").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual("120", await page.Locator("#automation-settle").InputValueAsync().ConfigureAwait(false));
        await page.Locator("#automation-save").ClickAsync().ConfigureAwait(false);
        await page.Locator("#automation-confirm").ClickAsync().ConfigureAwait(false);
        await page.Locator("dialog.automation-dialog").WaitForAsync(new() { State = WaitForSelectorState.Detached }).ConfigureAwait(false);
        await page.Locator("#automation-edit-browser-hourly").ClickAsync().ConfigureAwait(false);
        await page.Locator("#automation-save").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(nameof(LocalAutomationSourceWindowKind.CompletedCivilHour),
            await page.Locator("#automation-source-window").InputValueAsync().ConfigureAwait(false));
        await page.Locator("#automation-source-window").SelectOptionAsync(nameof(LocalAutomationSourceWindowKind.SunriseDay)).ConfigureAwait(false);
        await page.Locator("#automation-save").ClickAsync().ConfigureAwait(false);
        await page.Locator("#automation-confirm").ClickAsync().ConfigureAwait(false);
        await page.Locator("dialog.automation-dialog").WaitForAsync(new() { State = WaitForSelectorState.Detached }).ConfigureAwait(false);
        var definition = (await host.Services.GetRequiredService<ILocalAutomationStore>().GetStateAsync(CancellationToken.None)
            .ConfigureAwait(false)).Definitions.Single();
        Assert.AreEqual(2, definition.Version);
        Assert.AreEqual(LocalAutomationSourceWindowKind.SunriseDay, definition.Definition.SourceWindow!.Kind);
        Assert.AreEqual(2, definition.History.Count);
        Assert.IsEmpty(errors);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    // This browser case qualifies the host/UI and durable store, not a product algorithm.
    private sealed class WizardBrowserRegistry : ILocalAutomationTaskRegistry
    {
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() =>
        [new(LocalAutomationTaskKind.StillImageGeneration, "Synthetic browser product", [LocalAutomationTriggerKind.SourceWindowClosed],
            [WizardBrowserTarget], true, null)
            { SupportedSourceWindows = [LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceWindowKind.SunriseDay] }];

        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
            => definition.TaskKind == LocalAutomationTaskKind.StillImageGeneration && definition.TaskTarget == WizardBrowserTarget &&
                definition.TriggerKind == LocalAutomationTriggerKind.SourceWindowClosed && definition.SourceWindow?.IsValid() == true
                ? null : new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey,
            CancellationToken cancellationToken) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Skipped, "Browser fixture."));
    }
}
