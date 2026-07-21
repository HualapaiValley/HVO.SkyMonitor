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
        "retrospective-null-device-starvation-guard"
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
    public async Task FaultMatrix_ReferencesTestsDiscoveredByMSTest()
    {
        using var document = ReadManifest("central-transient-fault-matrix.json");
        var tests = document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("test").GetString()!)
            .ToArray();
        var repositoryRoot = FindRepositoryRoot();
        var projects = tests.GroupBy(ProjectFor, StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var discovered = await ListTestsAsync(repositoryRoot, project.Key).ConfigureAwait(false);
            foreach (var test in project)
            {
                discovered.Should().Contain(test, $"{test} must be an MSTest-discovered test in {project.Key}");
            }
        }
    }

    private static JsonDocument ReadManifest(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Validation", name)));

    private static string ProjectFor(string test)
        => test.StartsWith("HVO.SkyMonitor.CameraAgent.IntegrationTests.", StringComparison.Ordinal)
            ? "tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj"
            : test.StartsWith("HVO.SkyMonitor.IntegrationTests.", StringComparison.Ordinal)
                ? "tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj"
                : test.StartsWith("HVO.SkyMonitor.Tests.", StringComparison.Ordinal)
                    ? "tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj"
                    : throw new InvalidOperationException($"The fault matrix test assembly is unknown: {test}");

    private static async Task<HashSet<string>> ListTestsAsync(string repositoryRoot, string project)
    {
        var diagnosticPath = Path.Combine(Path.GetTempPath(), $"hvo-issue-116-discovery-{Guid.NewGuid():N}.log");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "test", project, "--no-build", "--no-restore", "--configuration", "Release", "--list-tests",
            "--diag", diagnosticPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to list tests for {project}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, $"dotnet test --list-tests failed for {project}: {error}");
        const string marker = "Received message: ";
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var line in File.ReadLinesAsync(diagnosticPath).ConfigureAwait(false))
        {
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }
            using var message = JsonDocument.Parse(line[(markerIndex + marker.Length)..]);
            var root = message.RootElement;
            if (!root.TryGetProperty("MessageType", out var messageType))
            {
                continue;
            }
            if (messageType.ValueEquals("TestCasesFound"))
            {
                AddDiscovered(root.GetProperty("Payload"), discovered);
            }
            else if (messageType.ValueEquals("TestDiscovery.Completed")
                && root.GetProperty("Payload").TryGetProperty("LastDiscoveredTests", out var final)
                && final.ValueKind == JsonValueKind.Array)
            {
                AddDiscovered(final, discovered);
            }
        }
        File.Delete(diagnosticPath);
        Assert.IsNotEmpty(discovered, $"No tests were discovered for {project}. Output: {output}");
        return discovered;
    }

    private static void AddDiscovered(JsonElement payload, ISet<string> discovered)
    {
        foreach (var test in payload.EnumerateArray())
        {
            if (test.TryGetProperty("FullyQualifiedName", out var name)
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                discovered.Add(name.GetString()!);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
