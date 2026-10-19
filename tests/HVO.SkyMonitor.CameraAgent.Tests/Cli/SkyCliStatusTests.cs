using System.Net;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary><c>status</c>, <c>status --watch</c> and <c>info</c> against the fake API.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliStatusTests
{
    [TestMethod]
    public async Task Status_ShowsCaptureQueuesStorageAndLogicHostAsRowsAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("status").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "CameraAgent:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "(agent allsky01, VirtualSky)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Capture:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Running", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "4.0 captures/min", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Artifact outbox: 2 pending (4.0 KiB), 0 quarantined", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Storage raw:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "120.0 GiB free of 500.0 GiB", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "LogicHost:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "(disabled)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "As of:", StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { "GET " + Fake.SummaryRoute }, harness.Api.Requests.Select(static r => $"{r.Method} {r.PathAndQuery}").ToArray());
    }

    [TestMethod]
    public async Task Status_MarksAStaleSectionAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.SummaryRoute, Fake.Summary(capture: "Paused", freshness: "stale"));

        var result = await harness.RunAsync("status").ConfigureAwait(false);

        StringAssert.Contains(result.Out, "Paused (stale)", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Status_Json_IsCameraAgentsSummaryAsItIsAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("status", "--json").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode);
        var json = result.Json();
        Assert.AreEqual("Running", json.GetProperty("captureControl").GetProperty("value").GetProperty("state").GetString());
        Assert.AreEqual("allsky01", json.GetProperty("configuration").GetProperty("value").GetProperty("agentId").GetString());
    }

    [TestMethod]
    public async Task Watch_WritesOneLinePerChangeAndExitsZeroWhenEndedAfterAReadAsync()
    {
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var reads = 0;
        harness.Api.Handle("GET", Fake.SummaryRoute, _ =>
        {
            reads++;
            if (reads >= 5)
            {
                stop.Cancel();
            }

            // Reads 1 and 2 match, 3 and 4 match, and 5 differs again: three lines.
            return FakeCameraAgent.Response(HttpStatusCode.OK, Fake.Summary(rawPending: reads <= 2 ? 0 : reads <= 4 ? 3 : 1));
        });

        var result = await harness.RunAsync(stop.Token, "status", "--watch").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsTrue(lines.Length is 2 or 3, result.Out);
        StringAssert.Contains(lines[0], "capture Running; camera Available; raw 0", StringComparison.Ordinal);
        StringAssert.Contains(lines[1], "raw 3", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Watch_SaysWhenCameraAgentStopsAnsweringAndExitsOneWhenEndedThenAsync()
    {
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var reads = 0;
        harness.Api.Handle("GET", Fake.SummaryRoute, _ =>
        {
            reads++;
            if (reads >= 4)
            {
                stop.Cancel();
            }

            return reads == 1
                ? FakeCameraAgent.Response(HttpStatusCode.OK, Fake.Summary())
                : FakeCameraAgent.ProblemResponse(HttpStatusCode.BadGateway, "Bad gateway");
        });

        var result = await harness.RunAsync(stop.Token, "status", "--watch").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines, "A repeated failure is written once.");
        StringAssert.Contains(lines[1], "NOT READ: Bad gateway (HTTP 502)", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Watch_Json_WritesOneDocumentPerLineAsync()
    {
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var reads = 0;
        harness.Api.Handle("GET", Fake.SummaryRoute, _ =>
        {
            if (++reads >= 3)
            {
                stop.Cancel();
            }

            return reads == 2
                ? FakeCameraAgent.ProblemResponse(HttpStatusCode.ServiceUnavailable, "Starting")
                : FakeCameraAgent.Response(HttpStatusCode.OK, Fake.Summary());
        });

        var result = await harness.RunAsync(stop.Token, "status", "--watch", "--json").ConfigureAwait(false);

        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsGreaterThanOrEqualTo(2, lines.Length, result.Out);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("Running", first.RootElement.GetProperty("captureControl").GetProperty("value").GetProperty("state").GetString());
        Assert.AreEqual("Failed", second.RootElement.GetProperty("error").GetProperty("kind").GetString());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "SignedOut")]
    [DataRow(HttpStatusCode.Forbidden, "Forbidden")]
    public async Task Watch_Json_EndsWithOneCompactErrorLineAndKeepsExitThreeWhenTheKeyIsRefusedAsync(HttpStatusCode status, string kind)
    {
        using var harness = new CliHarness();
        var reads = 0;
        harness.Api.Handle("GET", Fake.SummaryRoute, _ => ++reads == 1
            ? FakeCameraAgent.Response(HttpStatusCode.OK, Fake.Summary())
            : FakeCameraAgent.ProblemResponse(status, "Refused"));

        var result = await harness.RunAsync("status", "--watch", "--json").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines, result.Out);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind, line);
            Assert.AreEqual(line.Trim(), line, "Each line is one compact document.");
        }

        using var last = JsonDocument.Parse(lines[^1]);
        var error = last.RootElement.GetProperty("error");
        Assert.AreEqual(kind, error.GetProperty("kind").GetString());
        Assert.AreEqual(3, error.GetProperty("exitCode").GetInt32());
        Assert.IsEmpty(result.Error);
    }

    [TestMethod]
    [DataRow("status --watch --interval 0", "--interval takes 1 to 3600 seconds.")]
    [DataRow("status --interval 5", "--interval is used with --watch.")]
    public async Task Watch_IntervalOutOfRangeOrWithoutWatch_IsAUsageErrorAsync(string commandLine, string expected)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        using var harness = new CliHarness();

        var result = await harness.RunAsync(commandLine.Split(' ')).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, expected, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task Info_ShowsBothVersionsTheRuntimeRestartSupportAndTheKeyInUseAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("info").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "hvo-sky:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "0.9.0-test+" + new string('c', 40), StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "CameraAgent: 0.9.0-test+" + new string('b', 40), StringComparison.Ordinal);
        StringAssert.Contains(result.Out, ".NET 10.0.13 (linux-arm64)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "hyg-v42-production 4.2 (118218 rows)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "supervised: 'hvo-sky restart' restarts it", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, $"API key {CliHarness.KeyId} (read-write), from {harness.CredentialsPath}", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Info_Json_HasTheClientsVersionTheKeysIdentifierAndCameraAgentsDocumentAsync()
    {
        using var harness = new CliHarness { Supervised = false };

        var result = await harness.RunAsync("info", "--json").ConfigureAwait(false);

        var json = result.Json();
        Assert.AreEqual("hvo-sky", json.GetProperty("client").GetProperty("name").GetString());
        Assert.AreEqual("0.9.0-test+" + new string('c', 40), json.GetProperty("client").GetProperty("version").GetString());
        Assert.AreEqual(CliHarness.KeyId, json.GetProperty("credential").GetProperty("keyId").GetString());
        Assert.IsFalse(json.GetProperty("info").GetProperty("restart").GetProperty("supervised").GetBoolean());
        Assert.AreEqual(CliHarness.Url, json.GetProperty("url").GetString());
    }

    [TestMethod]
    public async Task Version_IsTheBuildIdentityWithItsCommitAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("--version").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Out));
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task Help_ListsEveryCommandAndExitsZeroAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("--help").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        foreach (var command in new[] { "status", "info", "config", "restart", "login", "logout", "rig", "schedule" })
        {
            StringAssert.Contains(result.Out, command, StringComparison.Ordinal);
        }
    }
}
