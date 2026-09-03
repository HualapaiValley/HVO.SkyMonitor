using System.Text.Json;
using System.Diagnostics;
using FluentAssertions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CentralTransientAcceptanceManifestTests
{
    private static readonly string[] RequiredFaultRows =
    [
        "cameraagent-central-outage-isolation",
        "cameraagent-hybrid-durable-handoff",
        "hybrid-concurrent-duplicate-convergence",
        "hybrid-identity-conflict-quarantine",
        "hybrid-source-failure-evidence",
        "out-of-order-window",
        "deadline-before-late-arrival",
        "incompatible-required-position",
        "missing-required-input",
        "missing-mask-evidence",
        "corrupt-source-object",
        "duplicate-concurrent-scheduling",
        "expired-lease-adoption",
        "max-attempt-adoption",
        "crash-after-sql-before-generic-completion",
        "sql-transaction-rollback",
        "minio-missing",
        "minio-unavailable",
        "minio-corrupt-concurrent",
        "no-candidate",
        "limitation-needs-review",
        "post-commit-invalidation",
        "version-preserving-reprocessing",
        "retrospective-version-selection",
        "retrospective-required-device-identity"
    ];

    [TestMethod]
    public void FaultMatrix_MapsEveryRequiredBoundaryToDeterministicExecutableEvidence()
    {
        using var document = ReadManifest("central-transient-fault-matrix.json");
        var root = document.RootElement;
        root.GetProperty("schema").GetString().Should().Be("hvo-central-transient-fault-matrix-v1");
        root.GetProperty("issue").GetInt32().Should().Be(116);
        root.GetProperty("defaultMode").GetString().Should().Be("Off");
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();

        rows.Select(row => row.GetProperty("id").GetString()).Should()
            .BeEquivalentTo(RequiredFaultRows, options => options.WithoutStrictOrdering());
        rows.Select(row => row.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();
        rows.Should().OnlyContain(row =>
            !string.IsNullOrWhiteSpace(row.GetProperty("fault").GetString()) &&
            !string.IsNullOrWhiteSpace(row.GetProperty("control").GetString()) &&
            !string.IsNullOrWhiteSpace(row.GetProperty("expectedDurableState").GetString()) &&
            row.GetProperty("test").GetString()!.StartsWith("HVO.SkyMonitor.", StringComparison.Ordinal) &&
            row.GetProperty("test").GetString()!.Count(character => character == '.') >= 3);
        rows.Select(row => row.GetProperty("workload").GetString()).Should()
            .OnlyContain(value => new[] { "W0", "W3M", "W4" }.Contains(value, StringComparer.Ordinal));
        rows.Count(row => !row.GetProperty("dockerRequired").GetBoolean()).Should().BeGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public void RuntimeSignals_DeclareBoundedLogsMetricsSpansHealthAndRetention()
    {
        using var document = ReadManifest("central-transient-runtime-signals.json");
        var root = document.RootElement;
        root.GetProperty("schema").GetString().Should().Be("hvo-runtime-signal-manifest-v1");
        root.GetProperty("issue").GetInt32().Should().Be(116);

        var logs = root.GetProperty("logs").EnumerateArray().ToArray();
        logs.Select(log => log.GetProperty("eventId").GetInt32()).Should()
            .BeEquivalentTo([2130, 2131, 2132, 2133, 2136, 2139, 2140, 2160, 2161]);
        logs.Select(log => log.GetProperty("eventId").GetInt32()).Should().OnlyHaveUniqueItems();

        var metrics = root.GetProperty("metrics").EnumerateArray().ToArray();
        metrics.Select(metric => metric.GetProperty("name").GetString()).Should()
            .Contain([
                "skymonitor.central.transient.outcomes",
                "skymonitor.central.transient.classifications",
                "skymonitor.central.transient.candidates",
                "skymonitor.central.derivative.queue",
                "skymonitor.central.derivative.window.pins.bytes"
            ]);
        metrics.Select(metric => metric.GetProperty("name").GetString()).Should().OnlyHaveUniqueItems();
        metrics.Should().OnlyContain(metric =>
            !string.IsNullOrWhiteSpace(metric.GetProperty("unit").GetString()) &&
            metric.GetProperty("labels").ValueKind == JsonValueKind.Object);
        foreach (var label in metrics.SelectMany(metric =>
                     metric.GetProperty("labels").EnumerateObject()))
        {
            label.Value.ValueKind.Should().Be(JsonValueKind.Array);
            var values = label.Value.EnumerateArray().Select(value => value.GetString()).ToArray();
            values.Should().OnlyHaveUniqueItems().And.NotContainNulls();
        }
        var expectedRecipes = new[]
        {
            "encoded-preview", "annotation", "image-quality", "rolling-mean",
            "central-transient-validation", "other"
        };
        foreach (var metric in metrics.Where(metric =>
                     metric.GetProperty("labels").TryGetProperty("recipe", out _)))
        {
            metric.GetProperty("labels").GetProperty("recipe").EnumerateArray()
                .Select(value => value.GetString()).Should().Equal(expectedRecipes);
        }
        metrics.Single(metric => metric.GetProperty("name").ValueEquals(
                "skymonitor.central.derivative.duration"))
            .GetProperty("labels").GetProperty("outcome").EnumerateArray()
            .Select(value => value.GetString()).Should().Contain("transient-validation.persisted");
        metrics.Single(metric => metric.GetProperty("name").ValueEquals(
                "skymonitor.central.derivative.attempts"))
            .GetProperty("labels").GetProperty("cause").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal([
                "none", "recipe", "input", "lease", "source-missing", "storage",
                "source-integrity", "output-integrity", "database", "execution"
            ]);
        var submissionOperations = metrics.Single(metric => metric.GetProperty("name").ValueEquals(
            "skymonitor.central.derivative.operations")).GetProperty("labels");
        submissionOperations.GetProperty("operation").EnumerateArray()
            .Select(value => value.GetString()).Should().Contain("transient-submit");
        submissionOperations.GetProperty("outcome").EnumerateArray()
            .Select(value => value.GetString()).Should().Contain(["accepted", "duplicate", "retired", "rejected"]);
        var expectedWindowStatuses = new[]
        {
            "waiting", "pending", "skipped", "quarantined", "terminalfailure"
        };
        foreach (var metric in metrics.Where(metric => metric.GetProperty("name").GetString() is
                     "skymonitor.central.derivative.window.resolutions" or
                     "skymonitor.central.derivative.window.selected_inputs" or
                     "skymonitor.central.derivative.window.expected_inputs" or
                     "skymonitor.central.derivative.window.missing_inputs" or
                     "skymonitor.central.derivative.window.completeness" or
                     "skymonitor.central.derivative.window.processing_lag" or
                     "skymonitor.central.derivative.window.selected_bytes"))
        {
            metric.GetProperty("labels").GetProperty("outcome").EnumerateArray()
                .Select(value => value.GetString()).Should().Equal(expectedWindowStatuses);
        }

        root.GetProperty("spans").GetArrayLength().Should().BeGreaterThanOrEqualTo(7);
        var health = root.GetProperty("health");
        health.GetProperty("check").GetString().Should().Be("central-derivative-worker");
        health.GetProperty("degraded").GetArrayLength().Should().BeGreaterThan(0);
        health.GetProperty("unhealthy").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("privacy").GetProperty("forbidden").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("collection").GetProperty("retainedArtifactPath").GetString().Should()
            .Contain("TestResults/issue-116");
    }

    [TestMethod]
    public void ReviewRuntimeSignals_ReserveBoundedPrivateLifecycleEvidence()
    {
        using var document = ReadManifest("transient-review-runtime-signals.json");
        var root = document.RootElement;
        root.GetProperty("schema").GetString().Should().Be("hvo-runtime-signal-manifest-v1");
        root.GetProperty("issue").GetInt32().Should().Be(118);
        root.GetProperty("logs").EnumerateArray()
            .Select(log => log.GetProperty("eventId").GetInt32()).Should().Equal([2162, 2163, 2164, 2165, 2166, 2167]);
        var metrics = root.GetProperty("metrics").EnumerateArray().ToArray();
        metrics.Select(metric => metric.GetProperty("name").GetString()).Should().OnlyHaveUniqueItems();
        metrics.Should().OnlyContain(metric => metric.GetProperty("labels").EnumerateObject()
            .All(label => new[] { "operation", "outcome", "kind" }
                .Contains(label.Name, StringComparer.Ordinal)));
        var forbidden = root.GetProperty("privacy").GetProperty("forbidden").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        forbidden.Should().Contain([
            "actor identity", "recipient", "payload bytes", "storage path", "object credentials",
            "idempotency key", "checksum", "lease token"
        ]);
        root.GetProperty("health").GetProperty("check").GetString().Should().Be("central-transient-lifecycle");
        root.GetProperty("collection").GetProperty("retainedArtifactPath").GetString().Should()
            .Contain("TestResults/issue-118");
    }

    private static JsonDocument ReadManifest(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Validation", name)));
}
