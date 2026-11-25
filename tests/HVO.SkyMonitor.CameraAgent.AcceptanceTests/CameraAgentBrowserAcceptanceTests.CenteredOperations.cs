using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    [TestMethod]
    public async Task CenteredOperationsExplainsTheFifthCaptureAndReopensItsExactRevisionsAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        var fixture = new CenteredOperationsFixture();
        await using var host = await CameraAgentKestrelFixture.CreateAsync(fixture.Register,
            prepareRoot: CenteredOperationsFixture.PrepareAsync, scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        // Surface configuration errors before browser navigation tries to read the configured graph.
        await host.Services.GetRequiredService<CameraAgentConfigurationInitializer>().StartAsync(token).ConfigureAwait(false);
        Assert.IsTrue(host.Services.GetRequiredService<ICameraAgentConfigurationAccessor>().IsConfigured);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new() { BaseURL = host.BaseAddress.AbsoluteUri }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        page.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        page.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GotoAsync("/operations/pipeline").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Link, new() { Name = "Running pipeline", Exact = true }).ClickAsync().ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);

        var index = host.Services.GetRequiredService<ICenteredViewReadIndex>();
        var acquisition = page.GetByRole(AriaRole.Region, new() { Name = "Acquisition and buffer" });
        var display = page.GetByRole(AriaRole.Region, new() { Name = "Live display publication" });
        var satellites = display.Locator($"[data-layer-kind='{CenteredOperationsFixture.SatelliteKind}']");
        string firstHref;
        string enrichedHref;
        CenteredViewCurrent first;
        CenteredViewCurrent enriched;
        try
        {
            await fixture.StartAsync(host.Root, token).ConfigureAwait(false);
            first = await WaitForOperationsViewAsync(index, fixture, 4, token).ConfigureAwait(false);
            var definition = first.View.Base.Definition;
            var latest = definition.Contributors[4].Exposure;
            var runtime = await host.Services.GetRequiredService<ICenteredExposureRuntimeReader>().ReadCurrentAsync(fixture.Camera, token).ConfigureAwait(false);
            Assert.AreEqual(latest.Input.Key, runtime.AcquisitionHead?.Exposure);
            Assert.AreEqual(5L, runtime.AcquisitionHead?.Ordinal);
            Assert.AreEqual(105L, latest.Capture.CaptureSequence);
            var graphs = host.Services.GetRequiredService<IProcessingGraphOperations>();
            var running = await graphs.ReadRunManifestAsync(latest.ExecutionId, token).ConfigureAwait(false);
            Assert.AreEqual("Running", running?.Nodes.Single(node => node.NodeId == CenteredOperationsFixture.SatelliteNode).Status);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync().ConfigureAwait(false);
            await Assertions.Expect(acquisition.GetByRole(AriaRole.Link, new() { Name = "Capture #105", Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
            await Assertions.Expect(acquisition.GetByText("Mono16 (mono)", new() { Exact = false })).ToBeVisibleAsync().ConfigureAwait(false);
            await Assertions.Expect(display.Locator(".publication-facts").GetByRole(AriaRole.Link, new() { Name = "Capture #103", Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
            await Assertions.Expect(satellites.Locator("summary")).ToContainTextAsync("4/5 assessed").ConfigureAwait(false);
            await Assertions.Expect(satellites.Locator("summary")).Not.ToContainTextAsync("unavailable").ConfigureAwait(false);
            await satellites.Locator("summary").ClickAsync().ConfigureAwait(false);
            var fifth = satellites.GetByRole(AriaRole.Row).Filter(new() { HasText = "Capture #105" });
            await Assertions.Expect(fifth.GetByRole(AriaRole.Cell, new() { Name = "Waiting", Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
            Assert.AreEqual(5, await page.GetByRole(AriaRole.Region, new() { Name = "Centered window" }).Locator("tbody tr").CountAsync().ConfigureAwait(false));
            await Assertions.Expect(display.GetByText("VerifiedCentroidSimilarity", new() { Exact = false }).First).ToBeVisibleAsync().ConfigureAwait(false);
            firstHref = RevisionLink(first.View);
            await Assertions.Expect(display.GetByRole(AriaRole.Link, new() { Name = "Open exact stack revision" })).ToHaveAttributeAsync("href", firstHref).ConfigureAwait(false);
            await AssertOperationsArchiveAsync(context, firstHref, first.View, 4).ConfigureAwait(false);
            TestContext.WriteLine($"Acquired 105, displaying 103, satellite 4/5 assessed (105 Waiting). Earlier revision {firstHref}.");

            fixture.ReleaseSatellite.TrySetResult();
            enriched = await WaitForOperationsViewAsync(index, fixture, 5, token).ConfigureAwait(false);
            enrichedHref = RevisionLink(enriched.View);
            Assert.AreEqual(first.View.Base.BaseId, enriched.View.Base.BaseId);
            Assert.AreNotEqual(firstHref, enrichedHref);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync().ConfigureAwait(false);
            await Assertions.Expect(satellites.Locator("summary")).ToContainTextAsync("5/5 assessed").ConfigureAwait(false);
            await Assertions.Expect(fifth.GetByRole(AriaRole.Cell, new() { Name = "Ready", Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
            await AssertOperationsArchiveAsync(context, enrichedHref, enriched.View, 5).ConfigureAwait(false);
            await AssertOperationsArchiveAsync(context, firstHref, first.View, 4).ConfigureAwait(false);
            var execution = $"/operations/pipeline/executions/{definition.Reference.ExecutionId:D}";
            await page.GotoAsync(execution).ConfigureAwait(false);
            await Assertions.Expect(page.Locator($".run-publication a[href='{enrichedHref}']")).ToBeVisibleAsync().ConfigureAwait(false);
            TestContext.WriteLine($"Satellite 5/5 assessed (105 Ready). Enriched revision {enrichedHref}. Both archived JPEGs matched the same retained checksum.");
        }
        finally
        {
            fixture.ReleaseSatellite.TrySetResult();
        }

        await page.CloseAsync().ConfigureAwait(false);
        await host.RestartAsync().ConfigureAwait(false);
        var reader = host.Services.GetRequiredService<ICenteredExposureRuntimeReader>();
        var runId = first.View.Base.Definition.Stream.RunId;
        var retained = await reader.ReadRunAsync(fixture.Camera, runId, token).ConfigureAwait(false);
        while (retained.Basis == CenteredRuntimeEvidenceBasis.Unavailable)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            retained = await reader.ReadRunAsync(fixture.Camera, runId, token).ConfigureAwait(false);
        }
        Assert.AreEqual(CenteredRuntimeEvidenceBasis.RetainedJournal, retained.Basis);
        Assert.IsNull(retained.BufferOccupancy);
        await using var cold = await diagnostics.NewContextAsync(new() { BaseURL = host.BaseAddress.AbsoluteUri }).ConfigureAwait(false);
        var reopened = await cold.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(reopened, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await reopened.GotoAsync("/operations/pipeline/running").ConfigureAwait(false);
        await Assertions.Expect(reopened.GetByRole(AriaRole.Link, new() { Name = "Open exact stack revision" })).ToHaveAttributeAsync("href", enrichedHref).ConfigureAwait(false);
        await AssertOperationsArchiveAsync(cold, firstHref, first.View, 4).ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LegacyConfiguredPipelineLinksToAnExplicitlyUnavailableCenteredRuntimeAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var host = await CameraAgentKestrelFixture.CreateAsync(scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new() { BaseURL = host.BaseAddress.AbsoluteUri }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GotoAsync("/operations/pipeline").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Link, new() { Name = "Running pipeline", Exact = true }).ClickAsync().ConfigureAwait(false);
        await Assertions.Expect(page.GetByText("The selected profile uses legacy presentation.", new() { Exact = false })).ToBeVisibleAsync().ConfigureAwait(false);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Processing executions", Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Region, new() { Name = "Acquisition and buffer" }).CountAsync().ConfigureAwait(false));
    }

    private static string RevisionLink(CenteredView view) => CenteredViewLinks.Revision(
        view.Base.Definition.Reference.Capture.CaptureId, view.Base.BaseId, view.Revision.RevisionId);

    private static async Task<CenteredViewCurrent> WaitForOperationsViewAsync(ICenteredViewReadIndex index,
        CenteredOperationsFixture fixture, int assessed, CancellationToken token)
    {
        while (true)
        {
            if (await index.ReadLastLiveAsync(fixture.Camera, token).ConfigureAwait(false) is { } current &&
                current.View.Base.Definition is { Kind: CenteredViewKind.Stack, Reference.Capture.CaptureSequence: CenteredOperationsFixture.ReferenceSequence, Contributors.Count: 5 } &&
                AssessedSatellites(current.View) == assessed)
                return current;
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    private static int AssessedSatellites(CenteredView view) => view.Revision.Layers.Count(layer =>
        layer.Key.Kind == CenteredOperationsFixture.SatelliteKind && layer.Readiness is CenteredViewReadiness.Ready or CenteredViewReadiness.AssessedEmpty);

    private static async Task AssertOperationsArchiveAsync(IBrowserContext context, string href, CenteredView issued, int assessed)
    {
        var response = await context.APIRequest.GetAsync(href).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, response.Status);
            var archive = JsonSerializer.Deserialize<CenteredView>(await response.BodyAsync().ConfigureAwait(false),
                JsonSerializerOptions.Web);
            Assert.IsNotNull(archive);
            Assert.AreEqual(issued.Base.BaseId, archive.Base.BaseId);
            Assert.AreEqual(issued.Revision.RevisionId, archive.Revision.RevisionId);
            Assert.AreEqual(assessed, AssessedSatellites(archive));
        }
        finally { await response.DisposeAsync().ConfigureAwait(false); }

        var preview = await context.APIRequest.GetAsync(href + "/preview?inline=true").ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, preview.Status);
            var payload = await preview.BodyAsync().ConfigureAwait(false);
            Assert.AreEqual(issued.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(payload));
        }
        finally { await preview.DisposeAsync().ConfigureAwait(false); }
    }
}
