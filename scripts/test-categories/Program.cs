using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

// This engine owns no counts. Every test project owns its own expected category inventory in a
// test-categories.json beside its project file (#853), so a count change is a change inside the
// owning project's directory and selects that project's component lane rather than the complete
// solution matrix that editing this file selects. The engine still discovers every project under
// tests/ and compares every actual case against every inventory, so ownership moved without any
// narrowing of what is audited: a missing, malformed, duplicate, unknown, or stale inventory fails.
var root = FindRepositoryRoot();
var categories = new[] { "Unit", "Integration", "Manual", "Soak", "External", "Hardware" };
var failures = new List<string>();
var expected = LoadInventories(root, categories, failures);

// The M5 macOS validation workflow pinned its own per-project expected Unit totals by hand, and
// they went stale the moment a repository-wide compile item added test methods to every project
// (#764). Two sources describing the same thing, and only one of them moved. Emitting this matrix
// lets that workflow read the numbers this audit already validates against a real discovery run,
// so the two cannot drift apart again. Tab-separated rather than JSON so the consumer needs only
// awk, which is present wherever the workflow runs. A structurally invalid inventory fails here
// too: a matrix emitted from a half-loaded inventory would be exactly the stale pin this replaces.
if (args.Contains("--emit-matrix", StringComparer.Ordinal))
{
    if (failures.Count > 0)
    {
        return ReportFailures(failures);
    }

    foreach (var (relativeProject, counts) in expected.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
    {
        foreach (var category in categories)
        {
            Console.WriteLine($"{category}\t{relativeProject}\t{counts[category]}");
        }
    }

    return 0;
}

var totals = categories.ToDictionary(static category => category, static _ => 0, StringComparer.Ordinal);

foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
             .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                 !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
{
    var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
    foreach (Match match in CategoryPatterns.TestCategoryExpression().Matches(text))
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

    // Every count below comes from static discovery, which expands a DataRow into one case per row but reports a
    // method whose rows come from a data source once however many rows that source yields. The matrix would then be
    // satisfied by a number smaller than what the Unit and Integration jobs execute, and it would stay satisfied while
    // the difference grew, because the audit and the runner would each be internally consistent and disagreeing only
    // with each other. That is the one failure this audit cannot see, so refuse the shape instead of counting it: a
    // pin that cannot enumerate what it is pinning is not a pin. The escape is either a DataRow per row, or teaching
    // this audit to expand the source and re-pinning the matrix against the expanded count.
    foreach (Match match in CategoryPatterns.UnexpandableTestDataExpression().Matches(text))
    {
        failures.Add(
            $"test data source that static discovery cannot expand in {Path.GetRelativePath(root, file)}: " +
            $"{match.Value.Trim()}; use DataRow so every row is discovered, or teach this audit (scripts/test-categories/Program.cs) to expand the source and re-pin the matrix against the expanded count");
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
    return ReportFailures(failures);
}

Console.WriteLine($"Test category audit passed: {string.Join(", ", categories.Select(category => $"{category}={totals[category]}"))}.");
return 0;

static int ReportFailures(List<string> failures)
{
    Console.Error.WriteLine("Test category audit failed:");
    foreach (var failure in failures.Order(StringComparer.Ordinal))
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

// Every project directory under tests/ that contains a project file must contain exactly one
// inventory, and every inventory must sit beside exactly one project file, so a project cannot
// be added without a count and an inventory cannot outlive its project. The file is a flat JSON
// object with one non-negative integer per known category, every category present, nothing else.
// Anything looser is rejected rather than defaulted: a category that defaults to zero is a pin
// that silently stops pinning the moment a case is recategorized.
static Dictionary<string, IReadOnlyDictionary<string, int>> LoadInventories(string root, string[] categories, List<string> failures)
{
    const string inventoryFileName = "test-categories.json";
    var inventories = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
    var testsRoot = Path.Combine(root, "tests");
    var projectFiles = Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(static path => !path.EndsWith("/HVO.SkyMonitor.LogicHost.TestInfrastructure.csproj", StringComparison.Ordinal))
        .ToArray();
    var projectsByDirectory = projectFiles
        .GroupBy(static path => Path.GetDirectoryName(path)!, StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
    var inventoryFiles = Directory.EnumerateFiles(testsRoot, inventoryFileName, SearchOption.AllDirectories)
        .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .ToArray();

    foreach (var inventoryFile in inventoryFiles)
    {
        var directory = Path.GetDirectoryName(inventoryFile)!;
        if (!projectsByDirectory.ContainsKey(directory))
        {
            failures.Add($"category inventory has no test project beside it: {Relative(root, inventoryFile)}");
        }
    }

    foreach (var (directory, projects) in projectsByDirectory)
    {
        if (projects.Length != 1)
        {
            failures.Add($"category inventory ownership is ambiguous; {projects.Length} project files share {Relative(root, directory)}");
            continue;
        }

        var relativeProject = Relative(root, projects[0]);
        var inventoryFile = Path.Combine(directory, inventoryFileName);
        if (!File.Exists(inventoryFile))
        {
            failures.Add($"test project is missing its category inventory {inventoryFileName}: {relativeProject}");
            continue;
        }

        var counts = ParseInventory(root, inventoryFile, categories, failures);
        if (counts is not null)
        {
            inventories[relativeProject] = counts;
        }
    }

    return inventories;
}

static IReadOnlyDictionary<string, int>? ParseInventory(string root, string inventoryFile, string[] categories, List<string> failures)
{
    var relativeInventory = Relative(root, inventoryFile);
    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(File.ReadAllText(inventoryFile), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
    }
    catch (JsonException exception)
    {
        failures.Add($"malformed category inventory {relativeInventory}: {exception.Message}");
        return null;
    }

    using (document)
    {
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            failures.Add($"category inventory must be a JSON object: {relativeInventory}");
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var valid = true;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!categories.Contains(property.Name, StringComparer.Ordinal))
            {
                failures.Add($"unknown category '{property.Name}' in {relativeInventory}");
                valid = false;
                continue;
            }

            if (counts.ContainsKey(property.Name))
            {
                failures.Add($"duplicate category '{property.Name}' in {relativeInventory}");
                valid = false;
                continue;
            }

            if (property.Value.ValueKind is not JsonValueKind.Number || !property.Value.TryGetInt32(out var count) || count < 0)
            {
                failures.Add($"category '{property.Name}' must be a non-negative integer in {relativeInventory}");
                valid = false;
                // Record the category as present so the value failure is reported once, not also as missing.
                counts[property.Name] = 0;
                continue;
            }

            counts[property.Name] = count;
        }

        foreach (var category in categories.Where(category => !counts.ContainsKey(category)))
        {
            failures.Add($"category '{category}' is missing from {relativeInventory}");
            valid = false;
        }

        return valid ? counts : null;
    }
}

static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

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

    // A denylist of names, deliberately, rather than anything that decides whether a source looks expandable. The
    // first alternative is a DynamicData attribute in an attribute list, which is why it requires an opening bracket
    // or a separating comma ahead of the name rather than matching the word anywhere. The second is a type declaring
    // MSTest's data-source interface or deriving from the attribute, which are the custom forms of the same thing; a
    // base list is the only place either name follows a colon or a comma.
    //
    // The holes are the ones a name list has and are worth stating rather than discovering: a using-alias for either
    // name, and a type reaching ITestDataSource through an intermediate base or interface rather than declaring it.
    // Both are visible in review; neither is silent the way the counting failure this replaces was. The pattern is
    // text, so it also matches these names in a comment or a string, which fails loudly in the safe direction.
    [GeneratedRegex("(?:\\[|,)\\s*(?:Microsoft\\.VisualStudio\\.TestTools\\.UnitTesting\\.)?DynamicData(?:Attribute)?\\s*\\(|[:,]\\s*(?:ITestDataSource|DynamicDataAttribute)\\b", RegexOptions.CultureInvariant)]
    internal static partial Regex UnexpandableTestDataExpression();
}
