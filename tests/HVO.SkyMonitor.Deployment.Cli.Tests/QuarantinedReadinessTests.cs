using System.Net;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Backup and upgrade readiness when the only unhealthy checks are a processing-lane quarantine. The predicate is the
/// decision <c>--allow-quarantined-readiness</c> adds; archive checksums are not part of it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class QuarantinedReadinessTests
{
    private static readonly ApprovedCatalogContract Catalog = ProductionCatalog.Get(HygV42.CatalogId);

    [TestMethod]
    public void TryAccept_LaneQuarantineAndTerminalOutcome_IsReady()
    {
        using var document = JsonDocument.Parse(Health(laneQuarantine: 1, processing: "terminal;processing-quarantine"));

        Assert.IsTrue(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out var reason), reason);
        Assert.AreEqual(string.Empty, reason);
    }

    [TestMethod]
    public void TryAccept_LaneQuarantineWithoutProcessingFailure_IsReady()
    {
        using var document = JsonDocument.Parse(Health(laneQuarantine: 1, processing: null));

        Assert.IsTrue(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out _));
    }

    [TestMethod]
    [DataRow(0, "terminal", null)]
    [DataRow(1, "terminal;retry", null)]
    [DataRow(1, "processing-quarantine", null)]
    [DataRow(1, "terminal", "disk-pressure")]
    public void TryAccept_AnyOtherUnhealthyCheck_IsRefused(int quarantine, string? processing, string? other)
    {
        using var document = JsonDocument.Parse(Health(quarantine, processing, other));

        Assert.IsFalse(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out var reason));
        Assert.IsGreaterThan(0, reason.Length);
    }

    [TestMethod]
    public void TryAccept_CatalogMismatch_IsRefused()
    {
        using var document = JsonDocument.Parse(Health(1, "terminal").Replace("\"4.2\"", "\"4.4\"", StringComparison.Ordinal));

        Assert.IsFalse(QuarantinedReadiness.TryAccept(document.RootElement, Catalog, out var reason));
        StringAssert.Contains(reason, "catalog", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task WaitForHealth_QuarantineOnly503_SucceedsWhenAllowed()
    {
        using var handler = Script(Health(1, "terminal"));
        var client = new OwnerBootstrapClient(new Uri("http://camera.invalid"), handler);

        await client.WaitForHealthAsync(Catalog, CancellationToken.None, TimeSpan.FromSeconds(5), allowQuarantinedReadiness: true);

        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task WaitForHealth_QuarantineOnly503_StillFailsWithoutTheFlag()
    {
        using var handler = Script(Health(1, "terminal"));
        var client = new OwnerBootstrapClient(new Uri("http://camera.invalid"), handler);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            client.WaitForHealthAsync(Catalog, CancellationToken.None, TimeSpan.FromSeconds(3)));

        StringAssert.Contains(failure.Message, "did not become ready", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task WaitForHealth_AnotherUnhealthyCheck_FailsWithTheFlag()
    {
        using var handler = Script(Health(1, "terminal", "disk-pressure"));
        var client = new OwnerBootstrapClient(new Uri("http://camera.invalid"), handler);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            client.WaitForHealthAsync(Catalog, CancellationToken.None, TimeSpan.FromSeconds(3), allowQuarantinedReadiness: true));

        StringAssert.Contains(failure.Message, "disk-pressure", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Flags_AreAttestationsAndDoNotChangeTheJournaledRequest()
    {
        var id = Guid.Parse("0e4af399-9984-48eb-b119-27ac3e327756");
        var plain = new LifecycleRequest(LifecycleOperationKind.Backup, id, "/var/lib/hvo-sky", false, false, false);
        var flagged = plain with { AllowQuarantinedReadiness = true, Abort = true };

        Assert.AreEqual(plain.ComputeRequestSha256(), flagged.ComputeRequestSha256());
    }

    [TestMethod]
    public void Parse_BackupAdmitsTheFlags_AndOtherCommandsDoNotTakeAbort()
    {
        var id = Guid.NewGuid().ToString("D");
        var parsed = (LifecycleRequest)CommandLine.ParseCommand(
            ["cameraagent", "backup", "--instance-id", id, "--product-root", "/srv/hvo/product", "--abort", "--allow-quarantined-readiness"],
            allowTestProductRoot: true);

        Assert.IsTrue(parsed.Abort);
        Assert.IsTrue(parsed.AllowQuarantinedReadiness);
        var refused = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
            ["cameraagent", "upgrade", "--instance-id", id, "--product-root", "/srv/hvo/product",
                "--image-ref", $"sha256:{new string('a', 64)}", "--abort"],
            allowTestProductRoot: true));
        StringAssert.Contains(refused.Message, "--abort", StringComparison.Ordinal);
        var unknown = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
            ["catalog", "install", "--catalog-bundle", "/srv/hvo/bundle", "--product-root", "/srv/hvo/product", "--allow-quarantined-readiness"],
            allowTestProductRoot: true));
        StringAssert.Contains(unknown.Message, "'--allow-quarantined-readiness'", StringComparison.Ordinal);
    }

    private static ScriptedHandler Script(string health)
        => new((_, count, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                count % 2 == 1 ? """{"status":"Healthy"}""" : health,
                Encoding.UTF8,
                "application/json"),
            StatusCode = count % 2 == 1 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable
        }));

    private static string Health(int laneQuarantine, string? processing, string? other = null)
    {
        var laneStatus = laneQuarantine > 0 ? "Unhealthy" : "Healthy";
        var laneDescription = laneQuarantine > 0
            ? QuarantinedReadiness.LaneDescription
            : "Capture lanes are accepting and draining work.";
        var checks = new StringBuilder()
            .Append("{\"name\":\"catalog\",\"status\":\"Healthy\",\"description\":\"Catalog matches.\",\"data\":{\"CatalogId\":\"hyg-v42-production\",\"CatalogVersion\":\"4.2\",\"SchemaVersion\":\"2\",\"PreprocessingVersion\":\"3\",\"RowCount\":119625,\"PackageVersion\":\"hyg-v4.2-p3-s2-r1\"}},")
            .Append("{\"name\":\"capture-lanes\",\"status\":\"").Append(laneStatus)
            .Append("\",\"description\":\"").Append(laneDescription)
            .Append("\",\"data\":{\"Availability\":\"").Append(laneStatus)
            .Append("\",\"LaneCount\":1,\"PendingCount\":0,\"PendingBytes\":0,\"LeasedCount\":0,\"QuarantineCount\":")
            .Append(laneQuarantine.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("}},{\"name\":\"capture-admission\",\"status\":\"Healthy\",\"description\":\"Capture admission is intentionally paused.\",\"data\":{\"State\":\"Paused\",\"Version\":14,\"IsInitialized\":true}}");
        if (processing is null)
        {
            checks.Append(",{\"name\":\"capture-processing\",\"status\":\"Healthy\",\"description\":\"Capture processing is completing durable graphs.\",\"data\":{\"Availability\":\"Healthy\",\"PendingCount\":0,\"RetryCount\":0,\"TerminalCount\":0,\"OldestPendingAgeSeconds\":0,\"Reason\":\"completed\"}}");
        }
        else
        {
            checks.Append(",{\"name\":\"capture-processing\",\"status\":\"Unhealthy\",\"description\":\"")
                .Append(QuarantinedReadiness.ProcessingDescription)
                .Append("\",\"data\":{\"Availability\":\"Unhealthy\",\"PendingCount\":0,\"RetryCount\":0,\"TerminalCount\":1,\"OldestPendingAgeSeconds\":0,\"Reason\":\"")
                .Append(processing)
                .Append("\"}}");
        }
        if (other is not null)
        {
            checks.Append(",{\"name\":\"").Append(other)
                .Append("\",\"status\":\"Unhealthy\",\"description\":\"Unrelated failure.\",\"data\":null}");
        }
        var status = laneQuarantine > 0 || processing is not null || other is not null ? "Unhealthy" : "Healthy";
        return "{\"status\":\"" + status + "\",\"checks\":[" + checks + "]}";
    }
}
