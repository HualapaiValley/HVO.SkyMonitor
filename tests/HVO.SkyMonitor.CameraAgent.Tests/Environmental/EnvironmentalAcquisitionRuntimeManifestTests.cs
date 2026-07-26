using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalAcquisitionRuntimeManifestTests
{
    private static readonly string[] ExpectedMetrics =
    [
        "skymonitor.environment.local.triggers",
        "skymonitor.environment.local.acquisitions",
        "skymonitor.environment.local.journal.commits",
        "skymonitor.environment.local.associations",
        "skymonitor.environment.local.acquisition.duration",
        "skymonitor.environment.local.journal.commit.duration",
        "skymonitor.environment.local.payload.size",
        "skymonitor.environment.local.history.query.duration",
        "skymonitor.environment.local.inflight",
        "skymonitor.environment.local.queue.depth",
        "skymonitor.environment.local.journal.records",
        "skymonitor.environment.local.journal.bytes",
        "skymonitor.environment.local.journal.oldest.age",
        "skymonitor.environment.local.sources"
    ];
    private static readonly string[] ExpectedSpans =
    [
        "environment.acquire",
        "environment.local.commit",
        "environment.associate",
        "environment.history.query",
        "environment.delivery.project"
    ];

    [TestMethod]
    public void ManifestDeclaresBoundedLogsMetricsSpansHealthAndPrivacy()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Validation",
            "cameraagent-environmental-acquisition-runtime-signals.json")));
        var root = document.RootElement;

        Assert.AreEqual("hvo-runtime-signal-manifest-v1", root.GetProperty("schema").GetString());
        Assert.AreEqual(209, root.GetProperty("issue").GetInt32());
        Assert.AreEqual(EnvironmentalAcquisitionTelemetry.InstrumentationName, root.GetProperty("meter").GetString());
        Assert.AreEqual(EnvironmentalAcquisitionTelemetry.InstrumentationName, root.GetProperty("activitySource").GetString());
        var logs = root.GetProperty("logs").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(2520, 10).ToArray(),
            logs.Select(log => log.GetProperty("eventId").GetInt32()).Order().ToArray());
        Assert.AreEqual(logs.Length, logs.Select(log => log.GetProperty("eventId").GetInt32()).Distinct().Count());
        Assert.IsTrue(logs.All(log => log.GetProperty("fields").GetArrayLength() > 0));

        var metrics = root.GetProperty("metrics").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(
            ExpectedMetrics,
            metrics.Select(metric => metric.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(metrics.Length, metrics.Select(metric => metric.GetProperty("name").GetString()).Distinct().Count());
        Assert.IsTrue(metrics.All(metric =>
            !string.IsNullOrWhiteSpace(metric.GetProperty("unit").GetString()) &&
            metric.GetProperty("labels").ValueKind == JsonValueKind.Object));
        Assert.IsTrue(metrics.SelectMany(metric => metric.GetProperty("labels").EnumerateObject())
            .All(label => label.Value.ValueKind == JsonValueKind.Array &&
                label.Value.EnumerateArray().Select(value => value.GetString()).Distinct().Count() ==
                label.Value.GetArrayLength()));

        CollectionAssert.AreEquivalent(
            ExpectedSpans,
            root.GetProperty("spans").EnumerateArray()
                .Select(span => span.GetProperty("name").GetString()).ToArray());
        var health = root.GetProperty("health");
        Assert.AreEqual("environmental-acquisition", health.GetProperty("check").GetString());
        Assert.AreEqual(12, health.GetProperty("fields").GetArrayLength());
        Assert.IsTrue(health.GetProperty("degraded").GetArrayLength() > 0);
        Assert.IsTrue(health.GetProperty("unhealthy").GetArrayLength() > 0);
        Assert.IsTrue(root.GetProperty("privacy").GetProperty("forbidden").GetArrayLength() > 0);
        StringAssert.Contains(
            root.GetProperty("collection").GetProperty("retainedArtifactPath").GetString(),
            "TestResults/issue-209",
            StringComparison.Ordinal);
    }
}
