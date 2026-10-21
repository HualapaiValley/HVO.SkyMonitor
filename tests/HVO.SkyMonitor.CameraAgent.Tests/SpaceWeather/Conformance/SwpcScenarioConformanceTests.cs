using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Conformance;

/// <summary>Replays every approved fixture expectation through the real runner and SQLite store.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwpcScenarioConformanceTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "swpc-conformance-" + Guid.NewGuid().ToString("N"));
    private static string Pack => Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1");
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public void ScenarioRowsEqualTheManifest()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Pack, "manifest.json")))!;
        var expected = manifest["derived"]!["scenarios"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray();
        var actual = typeof(SwpcScenarioConformanceTests).GetMethod(nameof(RealRunnerAndStoreMatchScenario))!
            .GetCustomAttributes(typeof(DataRowAttribute), false).Cast<DataRowAttribute>().Select(a => (string)a.Data[0]!).ToArray();
        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    [DataRow("scenarios/s01-duplicate-refetch.json")]
    [DataRow("scenarios/s02-same-identity-revised-content.json")]
    [DataRow("scenarios/s03-correction-reissue.json")]
    [DataRow("scenarios/s04-continuation-self-reference.json")]
    [DataRow("scenarios/s05-extension-chain-expiry.json")]
    [DataRow("scenarios/s06-extension-before-root.json")]
    [DataRow("scenarios/s07-watch-supersession-and-cancel.json")]
    [DataRow("scenarios/s08-watch-replacement-and-escalation.json")]
    [DataRow("scenarios/s09-synthetic-cancel-warning.json")]
    [DataRow("scenarios/s10-restart-ack-rolling-window.json")]
    [DataRow("scenarios/s11-stale-cdn-out-of-order.json")]
    [DataRow("scenarios/s12-malformed-bodies.json")]
    [DataRow("scenarios/s13-outage-backoff-halts.json")]
    [DataRow("scenarios/s14-year-rollover-and-local-day.json")]
    [DataRow("scenarios/s15-missing-scale-and-location.json")]
    [DataRow("scenarios/s16-kp-estimated-future-bin.json")]
    [DataRow("scenarios/s17-three-day-issue-versus-last-modified.json")]
    [DataRow("scenarios/s18-ovation-site-band.json")]
    [DataRow("scenarios/s19-early-resume-spacing.json")]
    [DataRow("scenarios/s20-record-level-drift.json")]
    [DataRow("scenarios/s21-pending-short-window-and-cap.json")]
    [DataRow("scenarios/s22-gone-client-error-network.json")]
    [DataRow("scenarios/s23-retry-after-forms.json")]
    [DataRow("scenarios/s24-json-nonfinite.json")]
    [DataRow("scenarios/s25-year-bounds.json")]
    [DataRow("scenarios/s26-original-record-verbatim.json")]
    [DataRow("scenarios/s27-original-record-outer-whitespace.json")]
    [DataRow("scenarios/s28-orphan-join-chain.json")]
    public async Task RealRunnerAndStoreMatchScenario(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Pack, file)).ConfigureAwait(false))!;
        Assert.AreEqual(0d, scenario["configuration"]!["jitterFraction"]!.GetValue<double>());
        var steps = scenario["steps"]!.AsArray();
        var firstAt = steps.First(s => s?["atUtc"] is not null)!["atUtc"]!.GetValue<string>();
        var clock = new ManualTimeProvider(SwpcTestData.Time(firstAt));
        var client = new PolicyStubClient();
        var store = Open();
        var runner = new SwpcAttemptRunner(store, client, clock, SwpcJitter.None);
        var coverage = new SwpcScenarioCoverage(file);
        try
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var step in steps.Select(s => s!))
            {
                var where = file + ":step:" + step["step"];
                var expected = step["expect"]?.AsObject() ?? new JsonObject();
                var kind = step["kind"]!.GetValue<string>();
                if (step["product"]?.GetValue<string>() == "ovation")
                {
                    // These six 200 responses change only the excluded product and its drift episode.
                    // Reject a future skip that could halt the provider or assert shared/core state.
                    Assert.AreEqual("fetch", kind, where);
                    Assert.AreEqual(200, step["response"]!["status"]!.GetValue<int>(), where);
                    Assert.IsTrue(expected["outcome"]!.GetValue<string>() is "updated" or "rejected", where);
                    Assert.IsFalse(expected.ContainsKey("notices") || expected.ContainsKey("originalRecords") || expected.ContainsKey("products"), where);
                    var skippedView = await store.ReadAsync(clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                    AssertOrphanMarkers(skippedView, where);
                    coverage.SkipStep(expected, where + " OVATION U3");
                    continue;
                }
                if (step["atUtc"] is { } time) clock.Advance(SwpcTestData.Time(time.GetValue<string>()) - clock.GetUtcNow());
                var at = clock.GetUtcNow();
                // ISwpcStore timed operations purge first. This read also supplies the current command revision.
                var view = await store.ReadAsync(at, CancellationToken.None).ConfigureAwait(false);
                SwpcRunResult? run = null;
                switch (kind)
                {
                    case "fetch":
                        var product = SwpcScenarioPolicyTests.Product(step["product"]!.GetValue<string>());
                        var trigger = step["trigger"]?.GetValue<string>() == "manual" ? SwpcAttemptTrigger.Manual : SwpcAttemptTrigger.Scheduled;
                        client.LastValidators = null;
                        client.Response = step["response"] is { } response ? SwpcScenarioPolicyTests.Response(product, response, at) : null;
                        run = await runner.RunAsync(product, trigger, "conformance-" + step["step"]).ConfigureAwait(false);
                        Assert.AreNotEqual(SwpcRefusal.NotDue, run.Refusal, where + ": scheduled before permitted attempt");
                        if (run.Refusal is null) Assert.AreEqual(SwpcWriteStatus.Applied, run.WriteStatus, where);
                        view = run.View;
                        break;
                    case "acknowledge":
                        var write = await store.AcknowledgeAsync(new(Guid.NewGuid(), view.Revision, at,
                            step["group"]!.GetValue<string>(), new(step["operator"]!.GetValue<string>(), at)), CancellationToken.None).ConfigureAwait(false);
                        Assert.AreEqual(SwpcWriteStatus.Applied, write.Status, where);
                        view = write.View;
                        break;
                    case "resume":
                        var resume = await runner.ResumeAsync(step["scope"]!.GetValue<string>() == "provider" ? null
                            : SwpcScenarioPolicyTests.Product(step["product"]!.GetValue<string>()), Guid.NewGuid()).ConfigureAwait(false);
                        Assert.IsFalse(resume.NotHalted, where);
                        Assert.AreEqual(SwpcWriteStatus.Applied, resume.Write!.Status, where);
                        view = resume.View;
                        break;
                    case "restart":
                        runner.Dispose();
                        store.Dispose();
                        store = Open();
                        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                        runner = new(store, client, clock, SwpcJitter.None);
                        view = await store.ReadAsync(at, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case "evaluate":
                        break;
                    default:
                        Assert.Fail(where + ": unknown step kind " + kind);
                        break;
                }
                AssertOrphanMarkers(view, where);
                var observed = SwpcScenarioView.Observe(view, scenario, at);
                if (run is not null)
                {
                    observed["refused"] = run.Refusal is null ? null : run.ReasonCode;
                    observed["request"] = client.LastValidators is { } sent
                        ? JsonSerializer.SerializeToNode(new { ifNoneMatch = sent.EntityTag, ifModifiedSince = sent.LastModified }) : null;
                    observed["outcome"] = run.Outcome is { } outcome ? SwpcPolicy.OutcomeToken(outcome) : null;
                    observed["attention"] = JsonSerializer.SerializeToNode(run.Attention.Select(e => new { group = e.Group, reason = e.Reason, level = e.Level }));
                    observed["diagnostics"] = JsonSerializer.SerializeToNode(run.Diagnostics);
                    observed["product"] = SwpcScenarioView.Product(view, run.Product, at);
                }
                coverage.CompareStep(expected, observed, where);
            }
            Assert.AreEqual(file.Contains("s18-", StringComparison.Ordinal) ? 2 : file.Contains("s24-", StringComparison.Ordinal) ? 4 : 0,
                coverage.SkippedSteps, file);
            coverage.Report(TestContext, steps.Count);
        }
        finally
        {
            runner.Dispose();
            store.Dispose();
        }
    }

    /// <summary>Requires markers to name precisely the currently orphaned linked components.</summary>
    private static void AssertOrphanMarkers(SwpcStoreView view, string where) =>
        CollectionAssert.AreEquivalent(view.Notices.Groups.Where(g => g.Orphan).Select(g => g.Identity).ToArray(),
            view.Notices.OrphanGroups.ToArray(), where + ": R1/R4 orphan markers");

    /// <summary>Reopens the same unique database root, including across a fixture restart.</summary>
    private SqliteSwpcStore Open() => new(new(root, Enum.GetValues<SwpcProduct>().ToImmutableDictionary(p => p,
        p => new SwpcFreshnessThresholds(SwpcSource.BaseInterval(p), SwpcSource.OutdatedAfter(p)))), NullLogger<SqliteSwpcStore>.Instance);
}
