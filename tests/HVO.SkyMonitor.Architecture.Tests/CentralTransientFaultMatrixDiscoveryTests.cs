using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CentralTransientAcceptanceManifestTests
{
    [TestMethod]
    public async Task FaultMatrix_ReferencesTestsDiscoveredByMSTest()
    {
        var matrixPath = Path.Combine(
            AppContext.BaseDirectory,
            "Validation",
            "central-transient-fault-matrix.json");
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(matrixPath).ConfigureAwait(false));
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

    private static string ProjectFor(string test)
    {
        if (test.StartsWith("HVO.SkyMonitor.CameraAgent.IntegrationTests.HybridTransientSubmissionTests.", StringComparison.Ordinal))
        {
            return "tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj";
        }
        if (test.StartsWith("HVO.SkyMonitor.CameraAgent.IntegrationTests.", StringComparison.Ordinal))
        {
            return "tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj";
        }
        if (test.StartsWith("HVO.SkyMonitor.IntegrationTests.", StringComparison.Ordinal))
        {
            return "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj";
        }
        if (test.StartsWith("HVO.SkyMonitor.Tests.", StringComparison.Ordinal))
        {
            return "tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj";
        }
        throw new InvalidOperationException($"The fault matrix test assembly is unknown: {test}");
    }

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
        Exception? primaryFailure = null;
        try
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between the timeout and termination request.
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                var timedOutOutput = await outputTask.ConfigureAwait(false);
                var timedOutError = await errorTask.ConfigureAwait(false);
                throw new TimeoutException(
                    $"dotnet test --list-tests timed out for {project}.{Environment.NewLine}{timedOutOutput}{Environment.NewLine}{timedOutError}",
                    exception);
            }

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
            Assert.IsNotEmpty(discovered, $"No tests were discovered for {project}. Output: {output}");
            return discovered;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                DeleteDiagnosticFiles(diagnosticPath);
            }
            catch (Exception cleanupFailure) when (
                primaryFailure is not null && cleanupFailure is IOException or UnauthorizedAccessException)
            {
                primaryFailure.Data[nameof(DeleteDiagnosticFiles)] = cleanupFailure;
            }
        }
    }

    private static void DeleteDiagnosticFiles(string diagnosticPath)
    {
        var directory = Path.GetDirectoryName(diagnosticPath)!;
        var stem = Path.GetFileNameWithoutExtension(diagnosticPath);
        var diagnosticFiles = Directory.EnumerateFiles(directory, $"{stem}.*.log", SearchOption.TopDirectoryOnly)
            .Append(diagnosticPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var deleteFailures = new List<Exception>();
        foreach (var file in diagnosticFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException exception)
            {
                deleteFailures.Add(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                deleteFailures.Add(exception);
            }
        }

        if (deleteFailures.Count > 0)
        {
            throw new IOException(
                $"Unable to delete test discovery diagnostics for '{diagnosticPath}'.",
                new AggregateException(deleteFailures));
        }
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
