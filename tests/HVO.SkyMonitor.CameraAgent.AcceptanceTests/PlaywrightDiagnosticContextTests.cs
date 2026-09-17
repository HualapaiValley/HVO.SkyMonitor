using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Playwright;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed browser diagnostic session.")]
public sealed class PlaywrightDiagnosticContextTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FailureRetainsSanitizedArtifactsAndSuccessRetainsNothing()
    {
        var directory = Path.Combine(TestContext.ResultsDirectory ?? Path.GetTempPath(), "playwright-failures");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        var rawTraceCount = Directory.GetFiles(Path.GetTempPath(), "hvo-playwright-*.zip").Length;
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);

        try
        {
            await using var failure = await PlaywrightDiagnosticContext.CreateAsync(browser, null, TestContext, 1).ConfigureAwait(false);
            var failurePage = await failure.Context.NewPageAsync().ConfigureAwait(false);
            await failure.Context.RouteAsync("https://diagnostics.invalid/**", route => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/html",
                Body = "<main><div>DOM_FREE_TEXT_7f91</div><div>VISIBLE_SCREENSHOT_7f91</div><input type='password' value='diagnostic-secret'><textarea>TEXTAREA_7f91</textarea><select><option selected>OPTION_VALUE_7f91</option></select></main>"
            })).ConfigureAwait(false);
            await failurePage.GotoAsync("https://diagnostics.invalid/").ConfigureAwait(false);
            await failurePage.EvaluateAsync("localStorage.setItem('opaque', 'LOCAL_STORAGE_7f91'); sessionStorage.setItem('opaque', 'SESSION_STORAGE_7f91'); document.cookie='opaque=COOKIE_VALUE_7f91'; console.error('CONSOLE_FREE_TEXT_7f91'); setTimeout(() => { throw new Error('PAGE_ERROR_FREE_TEXT_7f91'); }); fetch('http://127.0.0.1:1/private?opaque=QUERY_VALUE_7f91', { method: 'POST', headers: { 'X-Opaque': 'HEADER_VALUE_7f91' }, body: 'POST_BODY_7f91' }).catch(() => {});")
                .ConfigureAwait(false);
            await failurePage.WaitForTimeoutAsync(100).ConfigureAwait(false);
            Assert.Fail("deterministic browser assertion failure");
        }
        catch (AssertFailedException exception)
        {
            StringAssert.Contains(exception.Message, "deterministic browser assertion failure", StringComparison.Ordinal);
        }

        var retained = Directory.GetFiles(directory);
        Assert.IsTrue(retained.Any(path => path.EndsWith(".trace.zip", StringComparison.Ordinal)));
        Assert.IsTrue(retained.Any(path => path.EndsWith(".screenshot.png", StringComparison.Ordinal)));
        Assert.IsTrue(retained.Any(path => path.EndsWith(".dom.html", StringComparison.Ordinal)));
        Assert.IsTrue(retained.Any(path => path.EndsWith(".console.log", StringComparison.Ordinal)));
        Assert.IsTrue(retained.Any(path => path.EndsWith(".page-errors.log", StringComparison.Ordinal)));
        Assert.IsTrue(retained.Any(path => path.EndsWith(".network.log", StringComparison.Ordinal)));
        foreach (var textPath in retained.Where(path => !path.EndsWith(".zip", StringComparison.Ordinal)
                                                        && !path.EndsWith(".png", StringComparison.Ordinal)))
        {
            var text = await File.ReadAllTextAsync(textPath).ConfigureAwait(false);
            AssertNoSentinels(text);
        }
        await using (var trace = await ZipFile.OpenReadAsync(
            retained.Single(path => path.EndsWith(".trace.zip", StringComparison.Ordinal))).ConfigureAwait(false))
        {
            Assert.HasCount(1, trace.Entries);
            Assert.AreEqual("trace.json", trace.Entries[0].FullName);
            foreach (var entry in trace.Entries)
            {
                await using var stream = await entry.OpenAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                AssertNoSentinels(text);
                using var manifest = JsonDocument.Parse(text);
                Assert.AreEqual("hvo-playwright-failure-trace-v1", manifest.RootElement.GetProperty("schema").GetString());
                Assert.AreEqual(1, manifest.RootElement.GetProperty("pages").GetArrayLength());
            }
        }
        Assert.AreEqual(rawTraceCount, Directory.GetFiles(Path.GetTempPath(), "hvo-playwright-*.zip").Length,
            "the raw authenticated trace must be deleted in the same capture scope");

        var retainedCount = retained.Length;
        await using (var success = await PlaywrightDiagnosticContext.CreateAsync(browser, null, TestContext, 2).ConfigureAwait(false))
        {
            var page = await success.Context.NewPageAsync().ConfigureAwait(false);
            await page.SetContentAsync("<main>passing control</main>").ConfigureAwait(false);
            await success.CompleteAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(retainedCount, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public async Task ConcurrentContextsUseDistinctArtifactNames()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        try
        {
            var contexts = await Task.WhenAll(
                diagnostics.NewContextAsync(),
                diagnostics.NewContextAsync()).ConfigureAwait(false);
            await using var first = contexts[0];
            await using var second = contexts[1];
            _ = await first.NewPageAsync().ConfigureAwait(false);
            _ = await second.NewPageAsync().ConfigureAwait(false);
        }
        finally
        {
            await diagnostics.DisposeAsync().ConfigureAwait(false);
        }
        var traces = Directory.GetFiles(
            Path.Combine(TestContext.ResultsDirectory ?? Path.GetTempPath(), "playwright-failures"),
            $"*{nameof(ConcurrentContextsUseDistinctArtifactNames)}*.trace.zip");
        Assert.AreEqual(2, traces.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count());
    }

    private static void AssertNoSentinels(string text)
    {
        foreach (var sentinel in new[]
        {
            "diagnostic-secret", "DOM_FREE_TEXT_7f91", "VISIBLE_SCREENSHOT_7f91", "TEXTAREA_7f91",
            "OPTION_VALUE_7f91", "LOCAL_STORAGE_7f91", "SESSION_STORAGE_7f91", "COOKIE_VALUE_7f91",
            "CONSOLE_FREE_TEXT_7f91", "PAGE_ERROR_FREE_TEXT_7f91", "QUERY_VALUE_7f91", "HEADER_VALUE_7f91",
            "POST_BODY_7f91"
        })
        {
            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        }
    }
}
