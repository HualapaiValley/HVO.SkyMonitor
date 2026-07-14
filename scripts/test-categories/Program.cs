using System.Diagnostics;
using System.Text.RegularExpressions;

var root = FindRepositoryRoot();
var categories = new[] { "Unit", "Integration", "Manual", "Soak", "External", "Hardware" };
var expected = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
{
    ["tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj"] = Counts(unit: 115),
    ["tests/HVO.SkyMonitor.Imaging.Tests/HVO.SkyMonitor.Imaging.Tests.csproj"] = Counts(unit: 81, manual: 1),
    ["tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj"] = Counts(unit: 10, manual: 2),
    ["tests/HVO.SkyMonitor.Catalog.Sqlite.Tests/HVO.SkyMonitor.Catalog.Sqlite.Tests.csproj"] = Counts(unit: 56),
    ["tests/HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests/HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests.csproj"] = Counts(manual: 3),
    ["tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj"] = Counts(unit: 216, integration: 35, manual: 2, soak: 1),
    ["tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj"] = Counts(unit: 52, integration: 6, manual: 1),
    ["tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj"] = Counts(integration: 43),
    ["tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj"] = Counts(integration: 4)
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
    var selected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var category in categories)
    {
        selected[category] = await DiscoverAsync(root, project, $"TestCategory={category}").ConfigureAwait(false);
        totals[category] += selected[category].Count;
        if (selected[category].Count != expectedCounts[category])
        {
            failures.Add($"{relativeProject}: {category} discovery count is {selected[category].Count}; expected {expectedCounts[category]}");
        }
    }

    var union = selected.Values.SelectMany(static tests => tests).ToHashSet(StringComparer.Ordinal);
    foreach (var missing in all.Except(union, StringComparer.Ordinal))
    {
        failures.Add($"uncategorized test in {relativeProject}: {missing}");
    }

    foreach (var unexpected in union.Except(all, StringComparer.Ordinal))
    {
        failures.Add($"category discovery returned an unknown test in {relativeProject}: {unexpected}");
    }

    for (var left = 0; left < categories.Length; left++)
    {
        for (var right = left + 1; right < categories.Length; right++)
        {
            foreach (var overlap in selected[categories[left]].Intersect(selected[categories[right]], StringComparer.Ordinal))
            {
                failures.Add($"multiply categorized test in {relativeProject}: {overlap} ({categories[left]}, {categories[right]})");
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

static async Task<HashSet<string>> DiscoverAsync(string root, string project, string? filter)
{
    var arguments = new List<string>
    {
        "test", project, "--no-build", "--no-restore", "--configuration", "Release", "--list-tests"
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

    var tests = new HashSet<string>(StringComparer.Ordinal);
    var collecting = false;
    foreach (var line in output.Split(Environment.NewLine))
    {
        if (line.Contains("The following Tests are available:", StringComparison.Ordinal))
        {
            collecting = true;
            continue;
        }

        if (collecting && line.StartsWith("    ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(line))
        {
            tests.Add(line.Trim());
        }
    }

    return tests;
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
