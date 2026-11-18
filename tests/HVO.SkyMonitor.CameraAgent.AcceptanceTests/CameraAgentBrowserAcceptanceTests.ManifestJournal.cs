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
    private const string CorruptLedgerStatus = "corrupt-status";
    private const string MissingLedgerHeader = "missing-header";

    // Processing schema 9 reads manifest counts from the typed ledger, never from optional counters in the frozen
    // plan JSON. The table's constraints leave each ledger two corrupt shapes its reader can meet: a header that
    // records the corruption, and a header that is gone. Legacy JSON notations (deep, negative, lone surrogate)
    // cannot be stored in typed rows; converting them is covered by the Unit and migration tests.
    [TestMethod]
    [DataRow("Inputs", CorruptLedgerStatus)]
    [DataRow("Outputs", CorruptLedgerStatus)]
    [DataRow("Inputs", MissingLedgerHeader)]
    [DataRow("Outputs", MissingLedgerHeader)]
    public Task OptionalManifestCountCorruptionKeepsBrowserJournalReadableAndRecoversAsync(string ledgerKind, string fault)
        => AssertOptionalManifestEvidenceRecoveryAsync($"{ledgerKind}-ledger-{fault}", ledgerKind, fault);

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
        await WaitForCompletedExecutionAsync(host, liveId).ConfigureAwait(false);
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

    private sealed record ManifestLedgerRow(string LedgerKind, string NodeId, long? EvidenceCount, string EvidenceStatus);

    private static async Task<ManifestLedgerRow[]> ReadManifestLedgerAsync(SqliteConnection connection, Guid executionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ledger_kind, node_id, evidence_count, evidence_status FROM processing_execution_manifest_counts
            WHERE execution_id = $execution ORDER BY ledger_kind, node_id;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = new List<ManifestLedgerRow>();
        while (await reader.ReadAsync().ConfigureAwait(false))
            rows.Add(new(reader.GetString(0), reader.GetString(1),
                await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetInt64(2), reader.GetString(3)));
        return [.. rows];
    }

    // Damages or repairs only the selected ledger's header, then proves the complete ledger of both kinds is exactly
    // the expected rows, so neither step can pass by touching nothing or by disturbing the opposite ledger.
    private static async Task WriteManifestLedgerHeaderAsync(SqliteConnection connection, Guid executionId,
        ManifestLedgerRow[] original, string ledgerKind, string fault, bool restore)
    {
        var header = original.Single(row => row.LedgerKind == ledgerKind && row.NodeId.Length == 0);
        Assert.AreEqual("Recorded", header.EvidenceStatus);
        Assert.IsTrue(original.Any(row => row.LedgerKind == ledgerKind && row.NodeId.Length > 0),
            "The completed execution must retain typed count rows in the ledger under test.");
        using var command = connection.CreateCommand();
        if (fault == CorruptLedgerStatus)
            command.CommandText = "UPDATE processing_execution_manifest_counts SET evidence_status=$status WHERE execution_id=$execution AND ledger_kind=$kind AND node_id='';";
        else if (fault != MissingLedgerHeader)
            throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unknown manifest ledger fault.");
        else if (restore)
            command.CommandText = "INSERT INTO processing_execution_manifest_counts(execution_id, ledger_kind, node_id, evidence_count, evidence_status) VALUES ($execution, $kind, '', NULL, $status);";
        else
            command.CommandText = "DELETE FROM processing_execution_manifest_counts WHERE execution_id=$execution AND ledger_kind=$kind AND node_id='' AND evidence_status=$status;";
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$kind", ledgerKind);
        command.Parameters.AddWithValue("$status", restore || fault == MissingLedgerHeader ? header.EvidenceStatus : "Corrupt");
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        var expected = restore ? original
            : fault == MissingLedgerHeader ? [.. original.Where(row => row != header)]
            : [.. original.Select(row => row == header ? row with { EvidenceStatus = "Corrupt" } : row)];
        CollectionAssert.AreEqual(expected, await ReadManifestLedgerAsync(connection, executionId).ConfigureAwait(false));
    }

    // The fourth gallery card is an older capture, not necessarily a finished run. A live execution that is still
    // recording changes its journal, count ledger and manifest underneath exact before-and-after comparisons.
    private static async Task WaitForCompletedExecutionAsync(CameraAgentKestrelFixture host, Guid executionId)
    {
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(DefaultTimeoutMilliseconds);
        ProcessingGraphExecutionStatus? status;
        while ((status = (await operations.ReadExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false))?.Execution.Status)
            != ProcessingGraphExecutionStatus.Completed)
        {
            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, $"The selected live execution did not complete; its status is {status?.ToString() ?? "unknown"}.");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private async Task AssertOptionalManifestEvidenceRecoveryAsync(string scenario, string? ledgerKind = null, string? ledgerFault = null)
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
        await WaitForCompletedExecutionAsync(host, id).ConfigureAwait(false);
        await page.ReloadAsync().ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
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
        ManifestLedgerRow[] ledger = [];
        string? journal = null;
        if (auxiliary is not null) await auxiliary.CorruptAsync().ConfigureAwait(false);
        else
        {
            journal = await (await context.APIRequest.GetAsync(api).ConfigureAwait(false)).TextAsync().ConfigureAwait(false);
            ledger = await ReadManifestLedgerAsync(connection, id).ConfigureAwait(false);
            await WriteManifestLedgerHeaderAsync(connection, id, ledger, ledgerKind!, ledgerFault!, restore: false).ConfigureAwait(false);
        }
        var ordinary = await context.APIRequest.GetAsync(api).ConfigureAwait(false);
        Assert.AreEqual(200, ordinary.Status);
        // Ordinary detail never depends on the optional ledger rows, so it is the same journal, not merely a readable one.
        if (journal is not null) Assert.AreEqual(journal, await ordinary.TextAsync().ConfigureAwait(false));
        var refused = await context.APIRequest.GetAsync(api + "/manifest").ConfigureAwait(false);
        Assert.AreEqual(409, refused.Status);
        StringAssert.Contains(await refused.TextAsync().ConfigureAwait(false), CameraAgentRunManifest.CorruptReason, StringComparison.Ordinal);
        await page.ReloadAsync().ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".run-diagram__node").First).ConfigureAwait(false);
        Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "Download manifest", Exact = true }).IsDisabledAsync().ConfigureAwait(false));
        Assert.AreEqual(CameraAgentRunManifest.CorruptReason, await page.Locator("#manifest-unavailable").InnerTextAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await page.GetByText("The execution could not be read.", new() { Exact = true }).CountAsync().ConfigureAwait(false));

        if (auxiliary is not null) await auxiliary.RestoreReferenceAsync().ConfigureAwait(false);
        else await WriteManifestLedgerHeaderAsync(connection, id, ledger, ledgerKind!, ledgerFault!, restore: true).ConfigureAwait(false);
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
