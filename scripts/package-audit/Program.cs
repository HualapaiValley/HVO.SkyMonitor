using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: package-audit <vulnerable.json> <deprecated.json> <allowlist.json>");
    return 2;
}

var vulnerabilities = ReadFindings(args[0], "vulnerabilities");
var deprecated = ReadFindings(args[1], "deprecationReasons");
var allowlist = JsonSerializer.Deserialize<AllowlistEntry[]>(File.ReadAllText(args[2]), AuditJson.Options)
    ?? throw new InvalidDataException($"Package allowlist '{args[2]}' is invalid.");
var failures = new List<string>();

foreach (var finding in vulnerabilities)
{
    failures.Add(
        $"vulnerable {finding.DependencyType} package {finding.Id} {finding.Version} in {finding.Project}: {string.Join(", ", finding.Details)}");
}

foreach (var entry in allowlist)
{
    if (!DateOnly.TryParseExact(entry.ExpiresOn, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var expiry))
    {
        failures.Add($"allowlist entry {entry.Id} {entry.Version} has invalid expiry '{entry.ExpiresOn}'");
    }
    else if (expiry < DateOnly.FromDateTime(DateTime.UtcNow))
    {
        failures.Add($"allowlist entry {entry.Id} {entry.Version} expired on {entry.ExpiresOn}");
    }

    if (string.IsNullOrWhiteSpace(entry.Reason))
    {
        failures.Add($"allowlist entry {entry.Id} {entry.Version} has no review reason");
    }
}

foreach (var finding in deprecated)
{
    var entry = allowlist.SingleOrDefault(item => item.Matches(finding));
    if (entry is null)
    {
        failures.Add(
            $"deprecated {finding.DependencyType} package {finding.Id} {finding.Version} in {finding.Project}: {string.Join(", ", finding.Details)}");
    }
}

foreach (var entry in allowlist)
{
    foreach (var project in entry.Projects)
    {
        if (!deprecated.Any(finding => entry.Matches(finding) &&
                string.Equals(finding.Project, project, StringComparison.Ordinal)))
        {
            failures.Add($"stale allowlist entry {entry.Id} {entry.Version} for {project} is no longer reported");
        }
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Package audit failed:");
    foreach (var failure in failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine(
    $"Package audit passed: no vulnerabilities; {deprecated.Length} exact deprecated package occurrences reviewed.");
return 0;

static Finding[] ReadFindings(string path, string detailProperty)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var findings = new List<Finding>();
    foreach (var project in document.RootElement.GetProperty("projects").EnumerateArray())
    {
        var projectPath = NormalizeProjectPath(project.GetProperty("path").GetString() ?? "unknown project");
        if (!project.TryGetProperty("frameworks", out var frameworks))
        {
            continue;
        }

        foreach (var framework in frameworks.EnumerateArray())
        {
            var frameworkName = framework.GetProperty("framework").GetString() ?? "unknown framework";
            AddPackages(framework, frameworkName, "topLevelPackages", "TopLevel", projectPath, detailProperty, findings);
            AddPackages(framework, frameworkName, "transitivePackages", "Transitive", projectPath, detailProperty, findings);
        }
    }

    return findings.ToArray();
}

static void AddPackages(
    JsonElement framework,
    string frameworkName,
    string property,
    string dependencyType,
    string project,
    string detailProperty,
    List<Finding> findings)
{
    if (!framework.TryGetProperty(property, out var packages))
    {
        return;
    }

    foreach (var package in packages.EnumerateArray())
    {
        if (!package.TryGetProperty(detailProperty, out var details))
        {
            continue;
        }

        var values = details.ValueKind == JsonValueKind.Array
            ? details.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : item.ToString()).Order(StringComparer.Ordinal).ToArray()
            : [details.ToString()];
        findings.Add(new Finding(
            package.GetProperty("id").GetString()!,
            package.GetProperty("resolvedVersion").GetString()!,
            project,
            frameworkName,
            dependencyType,
            ReadAlternative(package),
            values));
    }
}

static string? ReadAlternative(JsonElement package)
{
    if (!package.TryGetProperty("alternativePackage", out var alternative))
    {
        return null;
    }

    return $"{alternative.GetProperty("id").GetString()}|{alternative.GetProperty("versionRange").GetString()}";
}

static string NormalizeProjectPath(string path)
{
    var normalized = path.Replace('\\', '/');
    foreach (var marker in new[] { "/src/", "/tests/" })
    {
        var index = normalized.LastIndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            return normalized[(index + 1)..];
        }
    }

    return normalized;
}

sealed record Finding(
    string Id,
    string Version,
    string Project,
    string Framework,
    string DependencyType,
    string? AlternativePackage,
    IReadOnlyList<string> Details);

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json.")]
sealed record AllowlistEntry(
    string Id,
    string Version,
    string Framework,
    string DependencyType,
    string[] Projects,
    string[] DeprecationReasons,
    string? AlternativePackage,
    string ExpiresOn,
    string Reason)
{
    public bool Matches(Finding finding)
        => string.Equals(Id, finding.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Version, finding.Version, StringComparison.Ordinal) &&
            string.Equals(Framework, finding.Framework, StringComparison.Ordinal) &&
            string.Equals(DependencyType, finding.DependencyType, StringComparison.Ordinal) &&
            string.Equals(AlternativePackage, finding.AlternativePackage, StringComparison.Ordinal) &&
            Projects.Contains(finding.Project, StringComparer.Ordinal) &&
            DeprecationReasons.Order(StringComparer.Ordinal).SequenceEqual(finding.Details, StringComparer.Ordinal);
}

static class AuditJson
{
    internal static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true };
}
