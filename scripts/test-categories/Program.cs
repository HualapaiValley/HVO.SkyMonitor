using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

var root = FindRepositoryRoot();
var categories = new[] { "Unit", "Integration", "Manual", "Soak", "External", "Hardware" };
var expected = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
{
    ["tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj"] = Counts(unit: 132),
    ["tests/HVO.SkyMonitor.Imaging.Tests/HVO.SkyMonitor.Imaging.Tests.csproj"] = Counts(unit: 182, manual: 2),
    ["tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj"] = Counts(unit: 89, manual: 6),
    ["tests/HVO.SkyMonitor.Catalog.Sqlite.Tests/HVO.SkyMonitor.Catalog.Sqlite.Tests.csproj"] = Counts(unit: 78),
    ["tests/HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests/HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests.csproj"] = Counts(manual: 3),
    ["tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj"] = Counts(unit: 1164, integration: 167, manual: 25, soak: 1, hardware: 1),
    ["tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj"] = Counts(unit: 2, integration: 5, manual: 10),
    ["tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj"] = Counts(unit: 276, integration: 6, manual: 1),
    ["tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj"] = Counts(integration: 362, manual: 29),
    ["tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj"] = Counts(integration: 23)
};
var totals = categories.ToDictionary(static category => category, static _ => 0, StringComparer.Ordinal);
var failures = new List<string>();
var discoveredProjects = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
    .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
    .ToHashSet(StringComparer.Ordinal);
foreach (var project in discoveredProjects.Except(expected.Keys, StringComparer.Ordinal))
{
    failures.Add($"test project is missing from the category matrix: {project}");
}
foreach (var project in expected.Keys.Except(discoveredProjects, StringComparer.Ordinal))
{
    failures.Add($"category matrix project does not exist: {project}");
}

foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
             .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                 !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
{
    foreach (Match match in CategoryPatterns.TestCategoryExpression().Matches(
                 await File.ReadAllTextAsync(file).ConfigureAwait(false)))
    {
        if (match.Groups[2].Success)
        {
            failures.Add($"test category must use a literal value in {Path.GetRelativePath(root, file)}: {match.Value}");
            continue;
        }

        var category = match.Groups[1].Value;
        if (!categories.Contains(category, StringComparer.Ordinal))
        {
            failures.Add($"unknown test category '{category}' in {Path.GetRelativePath(root, file)}");
        }
    }
}

foreach (var (relativeProject, expectedCounts) in expected)
{
    var project = Path.Combine(root, relativeProject);
    var all = await DiscoverAsync(root, project, filter: null).ConfigureAwait(false);
    var selected = new Dictionary<string, IReadOnlyDictionary<Guid, string>>(StringComparer.Ordinal);
    foreach (var category in categories)
    {
        selected[category] = await DiscoverAsync(root, project, $"TestCategory={category}").ConfigureAwait(false);
        totals[category] += selected[category].Count;
        if (selected[category].Count != expectedCounts[category])
        {
            failures.Add($"{relativeProject}: {category} discovery count is {selected[category].Count}; expected {expectedCounts[category]}");
        }
    }

    var union = selected.Values.SelectMany(static tests => tests.Keys).ToHashSet();
    foreach (var missing in all.Keys.Except(union))
    {
        failures.Add($"uncategorized test in {relativeProject}: {all[missing]} ({missing})");
    }

    foreach (var unexpected in union.Except(all.Keys))
    {
        var fullyQualifiedName = selected.Values
            .Select(tests => tests.GetValueOrDefault(unexpected))
            .First(static name => name is not null);
        failures.Add($"category discovery returned an unknown test in {relativeProject}: {fullyQualifiedName} ({unexpected})");
    }

    for (var left = 0; left < categories.Length; left++)
    {
        for (var right = left + 1; right < categories.Length; right++)
        {
            foreach (var overlap in selected[categories[left]].Keys.Intersect(selected[categories[right]].Keys))
            {
                failures.Add($"multiply categorized test in {relativeProject}: {selected[categories[left]][overlap]} ({overlap}; {categories[left]}, {categories[right]})");
            }
        }
    }
}

if (failures.Count > 0)
{
    await Console.Error.WriteLineAsync("Test category audit failed:").ConfigureAwait(false);
    foreach (var failure in failures.Order(StringComparer.Ordinal))
    {
        await Console.Error.WriteLineAsync($"- {failure}").ConfigureAwait(false);
    }

    return 1;
}

Console.WriteLine($"Test category audit passed: {string.Join(", ", categories.Select(category => $"{category}={totals[category]}"))}.");
return 0;

static IReadOnlyDictionary<string, int> Counts(
    int unit = 0,
    int integration = 0,
    int manual = 0,
    int soak = 0,
    int external = 0,
    int hardware = 0)
    => new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Unit"] = unit,
        ["Integration"] = integration,
        ["Manual"] = manual,
        ["Soak"] = soak,
        ["External"] = external,
        ["Hardware"] = hardware
    };

static async Task<IReadOnlyDictionary<Guid, string>> DiscoverAsync(string root, string project, string? filter)
{
    var diagnosticPath = Path.Combine(Path.GetTempPath(), $"hvo-test-discovery-{Guid.NewGuid():N}.diag");
    var arguments = new List<string>
    {
        "test", project, "--no-build", "--no-restore", "--configuration", "Release", "--list-tests", "--diag", diagnosticPath
    };
    if (filter is not null)
    {
        arguments.Add("--filter");
        arguments.Add(filter);
    }

    var startInfo = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    try
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start dotnet test discovery.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Test discovery failed for '{project}'.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }

        return ParseDiscoveredTests(diagnosticPath, project);
    }
    finally
    {
        DeleteDiagnosticFiles(diagnosticPath);
    }
}

static void DeleteDiagnosticFiles(string diagnosticPath)
{
    var directory = Path.GetDirectoryName(diagnosticPath)!;
    var stem = Path.GetFileNameWithoutExtension(diagnosticPath);
    var diagnosticFiles = Directory.EnumerateFiles(directory, $"{stem}.*.diag", SearchOption.TopDirectoryOnly)
        .Append(diagnosticPath)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    Exception? deleteFailure = null;
    foreach (var file in diagnosticFiles)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException exception)
        {
            deleteFailure ??= exception;
        }
        catch (UnauthorizedAccessException exception)
        {
            deleteFailure ??= exception;
        }
    }

    if (deleteFailure is not null)
    {
        throw new IOException($"Unable to delete test discovery diagnostics for '{diagnosticPath}'.", deleteFailure);
    }
}

static IReadOnlyDictionary<Guid, string> ParseDiscoveredTests(string diagnosticPath, string project)
{
    if (!File.Exists(diagnosticPath))
    {
        throw new InvalidOperationException($"Test diagnostics did not yield discovery completion for '{project}': the diagnostic file was not created.");
    }

    const string receivedMessageMarker = "Received message: ";
    var tests = new Dictionary<Guid, string>();
    var discoveryCompleted = false;
    foreach (var line in File.ReadLines(diagnosticPath))
    {
        var markerIndex = line.IndexOf(receivedMessageMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            continue;
        }

        using var message = JsonDocument.Parse(line[(markerIndex + receivedMessageMarker.Length)..]);
        var root = message.RootElement;
        if (!root.TryGetProperty("MessageType", out var messageType))
        {
            continue;
        }

        if (messageType.ValueEquals("TestDiscovery.TestFound"))
        {
            AddTests(root.GetProperty("Payload"), tests, project);
        }
        else if (messageType.ValueEquals("TestDiscovery.Completed"))
        {
            discoveryCompleted = true;
            var payload = root.GetProperty("Payload");
            if (payload.TryGetProperty("LastDiscoveredTests", out var lastDiscoveredTests) &&
                lastDiscoveredTests.ValueKind is not JsonValueKind.Null)
            {
                AddTests(lastDiscoveredTests, tests, project);
            }
        }
    }

    if (!discoveryCompleted)
    {
        throw new InvalidOperationException($"Test diagnostics did not yield a TestDiscovery.Completed message for '{project}'.");
    }

    return tests;
}

static void AddTests(JsonElement payload, IDictionary<Guid, string> tests, string project)
{
    if (payload.ValueKind is not JsonValueKind.Array)
    {
        throw new InvalidOperationException($"Test diagnostics contained a non-array discovery payload for '{project}'.");
    }

    foreach (var test in payload.EnumerateArray())
    {
        if (!test.TryGetProperty("Id", out var idElement) ||
            !Guid.TryParse(idElement.GetString(), out var id) ||
            !test.TryGetProperty("FullyQualifiedName", out var nameElement) ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            throw new InvalidOperationException($"Test diagnostics contained a discovered case without a stable Id and FullyQualifiedName for '{project}'.");
        }

        var fullyQualifiedName = nameElement.GetString()!;
        if (tests.TryGetValue(id, out var existingName) && !string.Equals(existingName, fullyQualifiedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Test diagnostics reused stable Id '{id}' for '{existingName}' and '{fullyQualifiedName}' in '{project}'.");
        }

        tests[id] = fullyQualifiedName;
    }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
}

static partial class CategoryPatterns
{
    [GeneratedRegex("(?:Microsoft\\.VisualStudio\\.TestTools\\.UnitTesting\\.)?TestCategory(?:Attribute)?\\s*\\(\\s*(?:\"([^\"]+)\"|([^\\)]*))\\s*\\)", RegexOptions.CultureInvariant)]
    internal static partial Regex TestCategoryExpression();
}
