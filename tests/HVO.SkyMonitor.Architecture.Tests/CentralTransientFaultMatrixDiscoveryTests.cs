using System.Diagnostics;
using System.Text.Json;

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
            var missing = project
                .Where(test => !discovered.Contains(test))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
            {
                Assert.Fail(
                    $"Fault-matrix tests were not discovered in {project.Key}:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, missing));
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
        var projectPath = Path.Combine(repositoryRoot, project);
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var testAssembly = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "bin",
            "Release",
            "net10.0",
            $"{projectName}.dll");
        if (!File.Exists(testAssembly))
        {
            throw new InvalidOperationException(
                $"Release test assembly '{testAssembly}' does not exist for {project}. " +
                "Build HVO.SkyMonitor.v9.slnx in Release before running the architecture discovery guard.");
        }

        var discoveryPath = Path.Combine(Path.GetTempPath(), $"hvo-issue-116-discovery-{Guid.NewGuid():N}.txt");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "vstest", testAssembly, "--ListFullyQualifiedTests", $"--ListTestsTargetPath:{discoveryPath}"
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
                    $"dotnet vstest --ListFullyQualifiedTests timed out for {project}." +
                    FormatProcessOutput(timedOutOutput, timedOutError),
                    exception);
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dotnet vstest --ListFullyQualifiedTests exited {process.ExitCode} for {project}." +
                    FormatProcessOutput(output, error));
            }

            if (!File.Exists(discoveryPath))
            {
                throw new InvalidOperationException(
                    $"dotnet vstest --ListFullyQualifiedTests exited successfully for {project}, " +
                    $"but did not create discovery evidence at '{discoveryPath}'." +
                    FormatProcessOutput(output, error));
            }

            var discovered = (await File.ReadAllLinesAsync(discoveryPath).ConfigureAwait(false))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (discovered.Count == 0)
            {
                throw new InvalidOperationException(
                    $"dotnet vstest --ListFullyQualifiedTests created empty discovery evidence for {project} " +
                    $"at '{discoveryPath}'." +
                    FormatProcessOutput(output, error));
            }

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
                File.Delete(discoveryPath);
            }
            catch (Exception cleanupFailure) when (
                primaryFailure is not null && cleanupFailure is IOException or UnauthorizedAccessException)
            {
                primaryFailure.Data["DiscoveryEvidenceCleanup"] = cleanupFailure;
            }
        }
    }

    private static string FormatProcessOutput(string standardOutput, string standardError)
    {
        return $"{Environment.NewLine}Standard output:{Environment.NewLine}{standardOutput}" +
            $"{Environment.NewLine}Standard error:{Environment.NewLine}{standardError}";
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
