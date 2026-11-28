using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var categories = new[] { "Unit", "Integration", "Manual", "Soak", "External", "Hardware", "Performance" };
var emitMatrix = args is ["--emit-matrix"];
var emitCameraAgentDiscovery = args is ["--emit-cameraagent-discovery"];
if (args.Length != 0 && !emitMatrix && !emitCameraAgentDiscovery)
{
    await Console.Error.WriteLineAsync("Usage: hvo-test-category-audit [--emit-matrix | --emit-cameraagent-discovery]").ConfigureAwait(false);
    return 2;
}

try
{
    var root = FindRepositoryRoot();
    var projects = LoadProjects(root);
    var unitProjects = LoadUnitProjects(root, projects);
    if (emitCameraAgentDiscovery)
    {
        const string cameraAgent = "HVO.SkyMonitor.CameraAgent.Tests";
        if (!unitProjects.Contains(cameraAgent))
        {
            throw new InvalidDataException($"{cameraAgent} is missing from the Unit project inventory.");
        }

        var tests = await DiscoverAsync(root, cameraAgent, projects[cameraAgent]).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(tests.OrderBy(static test => test.Id))).ConfigureAwait(false);
        return 0;
    }

    var failures = await CheckSourcesAsync(root, categories).ConfigureAwait(false);
    var report = new SortedDictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

    foreach (var (project, relativePath) in projects)
    {
        var counts = categories.ToDictionary(static category => category, static _ => 0, StringComparer.Ordinal);
        foreach (var test in await DiscoverAsync(root, project, relativePath).ConfigureAwait(false))
        {
            var unknownCategories = test.Categories.Except(categories, StringComparer.Ordinal).ToArray();
            if (unknownCategories.Length > 0)
            {
                failures.Add($"{project}: unknown test category on {test.Name}: {string.Join(", ", unknownCategories)}");
            }
            else if (test.Categories.Length == 0)
            {
                failures.Add($"{project}: uncategorized test {test.Name}");
            }
            else if (test.Categories.Length > 1)
            {
                failures.Add($"{project}: multiply categorized test {test.Name}: {string.Join(", ", test.Categories)}");
            }
            else
            {
                counts[test.Categories[0]]++;
            }
        }

        if ((counts["Unit"] > 0) != unitProjects.Contains(project))
        {
            failures.Add($"{project}: Unit discovery disagrees with membership in tests/unit-test-projects.json");
        }

        report.Add(project, counts);
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

    if (emitMatrix)
    {
        foreach (var (project, counts) in report)
        {
            foreach (var category in categories)
            {
                await Console.Out.WriteLineAsync($"{category}\t{projects[project]}\t{counts[category]}").ConfigureAwait(false);
            }
        }
    }
    else
    {
        var table = BuildTable(report, categories);
        await Console.Out.WriteAsync(table).ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summaryPath)
        {
            await File.AppendAllTextAsync(summaryPath, table).ConfigureAwait(false);
        }
    }

    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
    or InvalidOperationException or KeyNotFoundException or JsonException or System.Xml.XmlException)
{
    await Console.Error.WriteLineAsync($"Test category audit failed: {exception.Message}").ConfigureAwait(false);
    return 1;
}

static string BuildTable(SortedDictionary<string, Dictionary<string, int>> report, string[] categories)
{
    var lines = new List<string>
    {
        "### Discovered test cases",
        "",
        $"| Assembly | {string.Join(" | ", categories)} | Total |",
        $"| --- | {string.Join(" | ", categories.Select(static _ => "---:"))} | ---: |"
    };
    foreach (var (project, counts) in report)
    {
        lines.Add($"| {project} | {string.Join(" | ", categories.Select(category => counts[category]))} | {counts.Values.Sum()} |");
    }

    var totals = categories.Select(category => report.Values.Sum(counts => counts[category])).ToArray();
    lines.Add($"| **Total** | {string.Join(" | ", totals)} | {totals.Sum()} |");
    return string.Join('\n', lines) + "\n\n";
}

static SortedDictionary<string, string> LoadProjects(string root)
{
    var projects = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in EnumerateTestFiles(root, "*.csproj"))
    {
        var project = Path.GetFileNameWithoutExtension(file);
        var path = Relative(root, file);
        if (!CategoryPatterns.ProjectName().IsMatch(project) ||
            path != $"tests/{project}/{project}.csproj" ||
            !projects.TryAdd(project, path))
        {
            throw new InvalidDataException($"Invalid or ambiguous test project path: {path}");
        }
    }

    var solutionPaths = XDocument.Load(Path.Combine(root, "HVO.SkyMonitor.v9.slnx"))
        .Descendants("Project").Select(static node => (string?)node.Attribute("Path"))
        .OfType<string>().Where(static path => path.StartsWith("tests/", StringComparison.Ordinal)).ToArray();
    if (projects.Count == 0 || !projects.Values.Order(StringComparer.Ordinal).SequenceEqual(solutionPaths.Order(StringComparer.Ordinal)))
    {
        var missing = projects.Values.Except(solutionPaths, StringComparer.Ordinal);
        var extra = solutionPaths.Except(projects.Values, StringComparer.Ordinal);
        throw new InvalidDataException(
            $"Test projects and solution inventory differ (missing: {string.Join(", ", missing)}; extra: {string.Join(", ", extra)}).");
    }

    return projects;
}

static HashSet<string> LoadUnitProjects(string root, SortedDictionary<string, string> projects)
{
    const string inventory = "tests/unit-test-projects.json";
    var entries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, inventory)));
    var unitProjects = new HashSet<string>(StringComparer.Ordinal);
    if (entries is not { Length: > 0 })
    {
        throw new InvalidDataException($"{inventory} must list the Unit-bearing test projects.");
    }

    foreach (var entry in entries)
    {
        if (entry is null || !projects.ContainsKey(entry) || !unitProjects.Add(entry))
        {
            throw new InvalidDataException($"Invalid, duplicate, or unknown project '{entry}' in {inventory}.");
        }
    }

    return unitProjects;
}

static async Task<List<string>> CheckSourcesAsync(string root, string[] categories)
{
    var failures = new List<string>();
    foreach (var file in EnumerateTestFiles(root, "*.cs"))
    {
        var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        var path = Relative(root, file);
        foreach (Match match in CategoryPatterns.TestCategoryExpression().Matches(text))
        {
            if (match.Groups[2].Success)
            {
                failures.Add($"test category must use a literal value in {path}: {match.Value}");
            }
            else if (!categories.Contains(match.Groups[1].Value, StringComparer.Ordinal))
            {
                failures.Add($"unknown test category '{match.Groups[1].Value}' in {path}");
            }
        }

        // Discovery expands DataRow cases. Sources that can defer expansion
        // until execution cannot provide dependable discovery totals.
        foreach (Match match in CategoryPatterns.UnexpandableTestDataExpression().Matches(text))
        {
            failures.Add($"test data source that static discovery cannot expand in {path}: {match.Value.Trim()}; use DataRow");
        }
    }

    return failures;
}

static IEnumerable<string> EnumerateTestFiles(string root, string pattern) =>
    Directory.EnumerateFiles(Path.Combine(root, "tests"), pattern, SearchOption.AllDirectories)
        .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

static async Task<IReadOnlyCollection<DiscoveredTest>> DiscoverAsync(string root, string project, string relativePath)
{
    var assembly = Path.Combine(root, Path.GetDirectoryName(relativePath)!, "bin", "Release", "net10.0", $"{project}.dll");
    if (!File.Exists(assembly))
    {
        throw new InvalidDataException($"Release assembly missing for {project}. Build the solution in Release before discovery.");
    }

    var diagnosticPath = Path.Combine(Path.GetTempPath(), $"hvo-test-discovery-{Guid.NewGuid():N}.diag");
    var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[]
    {
        "test", relativePath, "--no-build", "--no-restore", "--configuration", "Release", "--list-tests", "--diag", diagnosticPath
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    try
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start dotnet test discovery.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Test discovery failed for '{project}'.\n{await outputTask.ConfigureAwait(false)}\n{await errorTask.ConfigureAwait(false)}");
        }

        return ParseDiscoveredTests(diagnosticPath, project, assembly);
    }
    finally
    {
        var directory = Path.GetDirectoryName(diagnosticPath)!;
        var stem = Path.GetFileNameWithoutExtension(diagnosticPath);
        foreach (var file in Directory.EnumerateFiles(directory, $"{stem}.*.diag").Append(diagnosticPath))
        {
            File.Delete(file);
        }
    }
}

static IReadOnlyCollection<DiscoveredTest> ParseDiscoveredTests(string diagnosticPath, string project, string assembly)
{
    const string marker = "Received message: ";
    var tests = new Dictionary<Guid, DiscoveredTest>();
    int? total = null;

    foreach (var line in File.ReadLines(diagnosticPath))
    {
        var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            continue;
        }

        using var message = JsonDocument.Parse(line[(markerIndex + marker.Length)..]);
        var root = message.RootElement;
        switch (root.GetProperty("MessageType").GetString())
        {
            case "TestDiscovery.TestFound":
                AddTests(root.GetProperty("Payload"), tests, project, assembly);
                break;
            case "TestDiscovery.Completed":
                var payload = root.GetProperty("Payload");
                var sources = payload.GetProperty("FullyDiscoveredSources").EnumerateArray().Select(static source => source.GetString());
                if (total.HasValue || payload.GetProperty("IsAborted").GetBoolean() ||
                    !sources.SequenceEqual(new[] { assembly }, StringComparer.Ordinal) ||
                    payload.GetProperty("PartiallyDiscoveredSources").GetArrayLength() != 0 ||
                    payload.GetProperty("NotDiscoveredSources").GetArrayLength() != 0 ||
                    payload.GetProperty("SkippedDiscoverySources").GetArrayLength() != 0)
                {
                    throw new InvalidDataException($"Incomplete or ambiguous VSTest discovery for '{project}'.");
                }

                total = payload.GetProperty("TotalTests").GetInt32();
                if (payload.GetProperty("LastDiscoveredTests") is { ValueKind: not JsonValueKind.Null } lastTests)
                {
                    AddTests(lastTests, tests, project, assembly);
                }

                break;
        }
    }

    if (total is null || total <= 0 || total != tests.Count)
    {
        var reported = total?.ToString(CultureInfo.InvariantCulture) ?? "no total";
        throw new InvalidDataException($"VSTest discovery for '{project}' reported {reported} and yielded {tests.Count} unique cases.");
    }

    return tests.Values;
}

static void AddTests(JsonElement payload, Dictionary<Guid, DiscoveredTest> tests, string project, string assembly)
{
    foreach (var test in payload.EnumerateArray())
    {
        if (!test.TryGetProperty("Id", out var idElement) ||
            !Guid.TryParse(idElement.GetString(), out var id) || id == Guid.Empty ||
            !test.TryGetProperty("FullyQualifiedName", out var nameElement) ||
            string.IsNullOrWhiteSpace(nameElement.GetString()) ||
            !test.GetProperty("Source").ValueEquals(assembly))
        {
            throw new InvalidDataException($"VSTest discovery returned a case without a stable identity or with an unexpected assembly for '{project}'.");
        }

        var categories = test.GetProperty("Properties").EnumerateArray()
            .Where(static property => property.GetProperty("Key").GetProperty("Id").ValueEquals("MSTestDiscoverer.TestCategory"))
            .SelectMany(static property => property.GetProperty("Value").EnumerateArray())
            .Select(static category => category.GetString() ?? throw new InvalidDataException("VSTest discovery returned a null test category."))
            .Distinct(StringComparer.Ordinal).ToArray();
        var areas = test.GetProperty("Properties").EnumerateArray()
            .Where(static property => property.GetProperty("Key").GetProperty("Id").ValueEquals("TestObject.Traits"))
            .SelectMany(static property => property.GetProperty("Value").EnumerateArray())
            .Where(static trait => trait.GetProperty("Key").ValueEquals("Area"))
            .Select(static trait => trait.GetProperty("Value").GetString() ??
                throw new InvalidDataException("VSTest discovery returned a null Area trait."))
            .ToArray();
        var name = nameElement.GetString()!;
        if (!tests.TryAdd(id, new DiscoveredTest(id, name, categories, areas)))
        {
            throw new InvalidDataException($"VSTest discovery reused case ID '{id}' for '{name}' in '{project}'.");
        }
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

internal sealed record DiscoveredTest(Guid Id, string Name, string[] Categories, string[] Areas);

internal static partial class CategoryPatterns
{
    [GeneratedRegex(@"\A[A-Za-z0-9]+(\.[A-Za-z0-9]+)+\z", RegexOptions.CultureInvariant)]
    internal static partial Regex ProjectName();

    [GeneratedRegex("(?:Microsoft\\.VisualStudio\\.TestTools\\.UnitTesting\\.)?TestCategory(?:Attribute)?\\s*\\(\\s*(?:\"([^\"]+)\"|([^\\)]*))\\s*\\)", RegexOptions.CultureInvariant)]
    internal static partial Regex TestCategoryExpression();

    [GeneratedRegex("(?:\\[|,)\\s*(?:Microsoft\\.VisualStudio\\.TestTools\\.UnitTesting\\.)?DynamicData(?:Attribute)?\\s*\\(|[:,]\\s*(?:ITestDataSource|DynamicDataAttribute)\\b", RegexOptions.CultureInvariant)]
    internal static partial Regex UnexpandableTestDataExpression();
}
