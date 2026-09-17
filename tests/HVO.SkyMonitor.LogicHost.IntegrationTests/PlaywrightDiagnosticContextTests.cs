using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using Microsoft.Playwright;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed browser diagnostic session.")]
public sealed class PlaywrightDiagnosticContextTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FailureRetainsArtifactsAndSuccessRetainsNothing()
    {
        var directory = Path.Combine(TestContext.ResultsDirectory ?? Path.GetTempPath(), "playwright-failures");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }).ConfigureAwait(false);
        try
        {
            await using var failure = await PlaywrightDiagnosticContext.CreateAsync(browser, null, TestContext, 1).ConfigureAwait(false);
            var page = await failure.Context.NewPageAsync().ConfigureAwait(false);
            await page.SetContentAsync("<main><input type='password' value='diagnostic-secret'></main>").ConfigureAwait(false);
            await page.EvaluateAsync("console.error('Bearer diagnostic-token'); setTimeout(() => { throw new Error('secret=page-secret'); }); fetch('http://127.0.0.1:1/private?token=query-secret').catch(() => {});")
                .ConfigureAwait(false);
            await page.WaitForTimeoutAsync(100).ConfigureAwait(false);
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
        foreach (var textPath in retained.Where(path => !path.EndsWith(".zip", StringComparison.Ordinal)
                                                        && !path.EndsWith(".png", StringComparison.Ordinal)))
        {
            var text = await File.ReadAllTextAsync(textPath).ConfigureAwait(false);
            Assert.DoesNotContain("diagnostic-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("diagnostic-token", text, StringComparison.Ordinal);
            Assert.DoesNotContain("page-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", text, StringComparison.Ordinal);
        }
        await using (var trace = await ZipFile.OpenReadAsync(
            retained.Single(path => path.EndsWith(".trace.zip", StringComparison.Ordinal))).ConfigureAwait(false))
        {
            Assert.IsFalse(trace.Entries.Any(entry => entry.FullName.StartsWith("resources/", StringComparison.Ordinal)));
            foreach (var entry in trace.Entries)
            {
                await using var stream = await entry.OpenAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                Assert.DoesNotContain("diagnostic-secret", text, StringComparison.Ordinal);
                Assert.DoesNotContain("diagnostic-token", text, StringComparison.Ordinal);
                Assert.DoesNotContain("page-secret", text, StringComparison.Ordinal);
                Assert.DoesNotContain("query-secret", text, StringComparison.Ordinal);
            }
        }
        var retainedCount = retained.Length;
        await using (var success = await PlaywrightDiagnosticContext.CreateAsync(browser, null, TestContext, 2).ConfigureAwait(false))
        {
            _ = await success.Context.NewPageAsync().ConfigureAwait(false);
            await success.CompleteAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(retainedCount, Directory.GetFiles(directory).Length);
    }
}
