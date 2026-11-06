using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary><c>time-data</c> against the fake API, served from the product's own status record.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliTimeDataTests
{
    private const string Route = "/api/v1/operations/time-data";
    private static readonly DateTimeOffset Attempt = new(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2027, 6, 28, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TimeData_ShowsTheSourceCoverageLastAttemptAndAdoptionAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Route, Status(expired: false, new("failed", Attempt, Attempt.AddSeconds(3), "time-data.checksum-mismatch",
            "the Earth-orientation file does not match its published SHA-512", Attempt.AddDays(-1), Attempt.AddDays(1))));

        var result = await harness.RunAsync("time-data").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        foreach (var expected in new[] { "Active source:", "managed", "planning covered until 2027-06-28T00:00:00Z", "Refresh:",
            "on, daily automation set up", "failed (2026-10-22T06:00:00Z, time-data.checksum-mismatch)",
            "does not match its published SHA-512", "Next permitted attempt:", "2026-10-23T06:00:00Z", "Last adoption:", "(verified)" })
            StringAssert.Contains(result.Out, expected, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPIRED", result.Out, StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { "GET " + Route }, harness.Api.Requests.Select(static r => $"{r.Method} {r.PathAndQuery}").ToArray());
    }

    [TestMethod]
    public async Task TimeData_SaysPlainlyWhenTheActivePairHasExpiredAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Route, Status(expired: true, new("never", null, null, null, null, null, null)) with
        {
            RefreshEnabled = false,
            RefreshScheduled = false,
            ManagedPair = null,
            ActiveSource = "bundled"
        });

        var result = await harness.RunAsync("time-data").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "EXPIRED: planning was covered until 2027-06-28T00:00:00Z", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "off, daily automation not set up", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Last attempt:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "never", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TimeData_SaysPlainlyWhenNoPairCoversTheCurrentTimeAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Route, Status(expired: false, new("adopted", Attempt, Attempt, "time-data.adopted", "", Attempt, Attempt.AddDays(1)),
            coversNow: false));

        var result = await harness.RunAsync("time-data").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out,
            "NOT COVERING NOW: no verified pair covers the current time; the active pair's coverage ends 2027-06-28T00:00:00Z", StringComparison.Ordinal);
        Assert.DoesNotContain("planning covered until", result.Out, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TimeData_Json_IsTheStatusDocumentAsItIsAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Route, Status(expired: false, new("adopted", Attempt, Attempt, "time-data.adopted", "", Attempt, Attempt.AddDays(1))));

        var result = await harness.RunAsync("time-data", "--json").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode);
        var json = result.Json();
        Assert.AreEqual(CameraAgentTimeDataStatus.CurrentVersion, json.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("adopted", json.GetProperty("refresh").GetProperty("outcome").GetString());
        Assert.IsFalse(json.GetProperty("expired").GetBoolean());
        Assert.IsTrue(json.GetProperty("coversNow").GetBoolean());
    }

    [TestMethod]
    public async Task TimeData_RefusedKeyExitsThreeAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Route, _ => FakeCameraAgent.Response(HttpStatusCode.Forbidden, new { }));

        var result = await harness.RunAsync("time-data").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
    }

    private static CameraAgentTimeDataStatus Status(bool expired, CameraAgentTimeDataRefreshStatus refresh, bool? coversNow = null) => new(
        CameraAgentTimeDataStatus.CurrentVersion, Attempt, true, true, true, "managed", "none", End, End.AddMonths(4), End, expired, coversNow ?? !expired,
        new string('A', 64), new string('B', 64), refresh,
        new("20261021T060003Z-0a1b2c3d", Attempt.AddDays(-1), Attempt.AddDays(-1), "verified", End, new string('A', 64), new string('B', 64)));
}
