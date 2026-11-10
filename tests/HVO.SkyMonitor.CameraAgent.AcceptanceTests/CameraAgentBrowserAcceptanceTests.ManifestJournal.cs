using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class CameraAgentBrowserAcceptanceTests
{
    [TestMethod]
    [DataRow("flat")]
    [DataRow("deep")]
    [DataRow("unicode-high")]
    [DataRow("unicode-low")]
    public Task OptionalManifestCountCorruptionKeepsBrowserJournalReadableAndRecoversAsync(string scenario)
        => AssertOptionalManifestEvidenceRecoveryAsync(scenario);

    [TestMethod]
    public Task AuxiliaryManifestDescriptorCorruptionKeepsBrowserJournalReadableAndRecoversAsync()
        => AssertOptionalManifestEvidenceRecoveryAsync("auxiliary-descriptor");

    [TestMethod]
    public async Task CancelledBeforeWorkerReplayKeepsBrowserJournalAndManifestDownloadAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var host = await CameraAgentKestrelFixture.CreateAsync(configureServices: SuppressFixtureReplayWorker).ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        { BaseURL = host.BaseAddress.ToString() }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await WaitForGalleryCapturesAsync(page, minimumCards: 24).ConfigureAwait(false);
        var runUrl = await page.Locator(".capture-card .run-link").Nth(3).GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(runUrl);
        await page.GotoAsync(runUrl).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var liveId = Guid.Parse(new Uri(page.Url).Segments[^1]);
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
        var submitted = await operations.SubmitReplayAsync(new(live.Execution.CaptureId,
            live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId), "manifest-browser-cancel", "test", CancellationToken.None).ConfigureAwait(false);
        var id = submitted.Execution.ExecutionId;
        await operations.CancelReplayAsync(id, "manifest-browser-cancel-command", "test", null, CancellationToken.None).ConfigureAwait(false);
        var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
        Assert.AreEqual(ProcessingGraphExecutionStatus.Cancelled, detail.Execution.Status);
        Assert.IsTrue(detail.Nodes.All(static node => node.Attempts.Count == 0 && node.Outputs.Count == 0));
        var replayUrl = new Uri(page.Url).AbsolutePath.Replace(liveId.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase);
        await page.GotoAsync(replayUrl).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".run-diagram__node").First).ConfigureAwait(false);
        var link = page.GetByRole(AriaRole.Link, new() { Name = "Download manifest", Exact = true });
        await VisibleAsync(link).ConfigureAwait(false);
        var download = await page.RunAndWaitForDownloadAsync(() => link.ClickAsync()).ConfigureAwait(false);
        var path = Path.Combine(TestContext.TestResultsDirectory!, "cancelled-before-worker-manifest.json");
        await download.SaveAsAsync(path).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        Assert.AreEqual(id, json.RootElement.GetProperty("executionId").GetGuid());
        Assert.AreEqual("Cancelled", json.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("nodes").EnumerateArray().All(static node => node.GetProperty("outputs").GetArrayLength() == 0));
        Assert.AreEqual(0, await page.Locator("#manifest-unavailable").CountAsync().ConfigureAwait(false));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    // Keep only this fixture's Replay worker unstarted; live capture and every other
    // hosted service use their original registrations and normal host lifetime.
    private static void SuppressFixtureReplayWorker(IServiceCollection services)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var registration = services[index];
            if (registration.ServiceType != typeof(IHostedService) || registration.ImplementationFactory is not { } create)
                continue;
            services[index] = ServiceDescriptor.Singleton<IHostedService>(provider =>
            {
                var service = (IHostedService)create(provider);
                return service.GetType().Name == "ProcessingReplayWorker" ? new UnstartedFixtureReplayWorker() : service;
            });
        }
    }

    private sealed class UnstartedFixtureReplayWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoAuxiliaryEvidence : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task AssertOptionalManifestEvidenceRecoveryAsync(string scenario)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await WaitForGalleryCapturesAsync(page, minimumCards: 24).ConfigureAwait(false);
        var runUrl = await page.Locator(".capture-card .run-link").Nth(3).GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(runUrl);
        await page.GotoAsync(runUrl).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var id = Guid.Parse(new Uri(page.Url).Segments[^1]);
        var api = $"/api/v1/operations/processing-graphs/executions/{id:D}";
        AuxiliaryRunManifestEvidenceFixture? auxiliaryEvidence = null;
        if (scenario == "auxiliary-descriptor")
        {
            var baseline = await context.APIRequest.GetAsync(api + "/manifest").ConfigureAwait(false);
            Assert.AreEqual(200, baseline.Status);
            using var manifest = JsonDocument.Parse(await baseline.BodyAsync().ConfigureAwait(false));
            var output = manifest.RootElement.GetProperty("nodes").EnumerateArray()
                .SelectMany(static node => node.GetProperty("outputs").EnumerateArray()).First();
            auxiliaryEvidence = await AuxiliaryRunManifestEvidenceFixture.CreateAsync(host.Root,
                output.GetProperty("artifactId").GetGuid()).ConfigureAwait(false);
        }
        var auxiliary = auxiliaryEvidence;
        await using var auxiliaryLifetime = (auxiliary is null ? new NoAuxiliaryEvidence() : (IAsyncDisposable)auxiliary).ConfigureAwait(false);
        var downloadLink = page.GetByRole(AriaRole.Link, new() { Name = "Download manifest", Exact = true });
        await VisibleAsync(downloadLink).ConfigureAwait(false);
        var download = await page.RunAndWaitForDownloadAsync(() => downloadLink.ClickAsync()).ConfigureAwait(false);
        var beforePath = Path.Combine(TestContext.TestResultsDirectory!, $"manifest-{scenario}-before-corruption.json");
        await download.SaveAsAsync(beforePath).ConfigureAwait(false);
        TestContext.AddResultFile(beforePath);
        using var before = JsonDocument.Parse(await File.ReadAllBytesAsync(beforePath).ConfigureAwait(false));
        Assert.AreEqual(id, before.RootElement.GetProperty("executionId").GetGuid());

        using var connection = new SqliteConnection($"Data Source={Path.Combine(host.Root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_executions SET frozen_plan_json =
                CAST(json_set(CAST(frozen_plan_json AS TEXT), '$.committedOutputCounts', json($counts)) AS BLOB)
            WHERE execution_id = $execution;
            """;
        command.Parameters.AddWithValue("$execution", id.ToString("N"));
        command.Parameters.AddWithValue("$counts", scenario switch
        {
            "deep" => new string('[', 70) + "0" + new string(']', 70),
            "unicode-high" => "{\"\\uD800\":1}",
            "unicode-low" => "{\"\\uDC00\":1}",
            _ => "{\"preview\":-1}"
        });
        if (auxiliary is not null) await auxiliary.CorruptAsync().ConfigureAwait(false);
        else Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        var ordinary = await context.APIRequest.GetAsync(api).ConfigureAwait(false);
        Assert.AreEqual(200, ordinary.Status);
        var refused = await context.APIRequest.GetAsync(api + "/manifest").ConfigureAwait(false);
        Assert.AreEqual(409, refused.Status);
        StringAssert.Contains(await refused.TextAsync().ConfigureAwait(false), CameraAgentRunManifest.CorruptReason, StringComparison.Ordinal);
        await page.ReloadAsync().ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".run-diagram__node").First).ConfigureAwait(false);
        Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "Download manifest", Exact = true }).IsDisabledAsync().ConfigureAwait(false));
        Assert.AreEqual(CameraAgentRunManifest.CorruptReason, await page.Locator("#manifest-unavailable").InnerTextAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await page.GetByText("The execution could not be read.", new() { Exact = true }).CountAsync().ConfigureAwait(false));

        command.CommandText = """
            UPDATE processing_executions SET frozen_plan_json =
                CAST(json_remove(CAST(frozen_plan_json AS TEXT), '$.committedOutputCounts') AS BLOB)
            WHERE execution_id = $execution;
            """;
        if (auxiliary is not null) await auxiliary.RestoreReferenceAsync().ConfigureAwait(false);
        else Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        await page.ReloadAsync().ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await VisibleAsync(downloadLink).ConfigureAwait(false);
        var recovered = await page.RunAndWaitForDownloadAsync(() => downloadLink.ClickAsync()).ConfigureAwait(false);
        var afterPath = Path.Combine(TestContext.TestResultsDirectory!, $"manifest-{scenario}-after-recovery.json");
        await recovered.SaveAsAsync(afterPath).ConfigureAwait(false);
        TestContext.AddResultFile(afterPath);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(beforePath).ConfigureAwait(false), await File.ReadAllBytesAsync(afterPath).ConfigureAwait(false));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }
}
