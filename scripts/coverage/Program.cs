using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

var developmentV1 = args.Length > 0 && args[0] == "--development-v1";
if (developmentV1)
{
    args = args[1..];
}

var validateReportsOnly = args.Length > 0 && args[0] == "--validate-reports";
if (validateReportsOnly)
{
    args = args[1..];
}

if (args.Length < 2)
{
    throw new ArgumentException("A baseline file and at least one Cobertura report are required.");
}

using var baselineDocument = JsonDocument.Parse(File.ReadAllText(args[0]));
using var policyDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "policy.json")));
var retiredSourcePaths = policyDocument.RootElement.GetProperty("retiredSourcePaths").EnumerateArray()
    .Select(static path => path.GetString() ?? throw new InvalidDataException("Null retired coverage path."))
    .ToHashSet(StringComparer.Ordinal);
var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[0]) ?? ".", "..", ".."));
var aggregate = baselineDocument.RootElement.GetProperty("aggregate");
var baselineLine = aggregate.GetProperty("line").GetDouble();
var baselineBranch = aggregate.GetProperty("branch").GetDouble();
var tolerance = aggregate.GetProperty("tolerancePercentagePoints").GetDouble() / 100;
var thresholds = baselineDocument.RootElement.GetProperty("files").EnumerateObject().ToDictionary(
    static property => property.Name,
    static property => new Threshold(
        property.Value.GetProperty("line").GetDouble(),
        property.Value.GetProperty("branch").GetDouble(),
        property.Value.TryGetProperty("developmentV1LocalOnlyReason", out var reason) ? reason.GetString() : null),
    StringComparer.Ordinal);

foreach (var (path, threshold) in thresholds.ToArray())
{
    string? reason = null;
    if (!SourcePathExists(repositoryRoot, path))
    {
        if (!retiredSourcePaths.Contains(path))
        {
            Console.Error.WriteLine($"Coverage gate failed: required source path '{path}' is missing and is not a pinned retired path. Restore the source or review its move and floor mapping; do not silently retire it.");
            return 1;
        }

        reason = "source file is absent and explicitly pinned in coverage/policy.json as retired";
    }
    else if (developmentV1)
    {
        reason = threshold.DevelopmentV1LocalOnlyReason;
    }

    if (reason is not null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidDataException($"Empty developmentV1LocalOnlyReason for '{path}'.");
        }

        Console.WriteLine(FormattableString.Invariant(
            $"SKIP {path}: canonical floors line {threshold.Line:P2}, branch {threshold.Branch:P2}; {reason}"));
        thresholds.Remove(path);
    }
}

if (thresholds.Count == 0)
{
    throw new InvalidDataException("No measurable per-file coverage floors.");
}

var files = new Dictionary<string, Coverage>(StringComparer.Ordinal);

foreach (var report in args.Skip(1))
{
    XDocument document;
    try
    {
        document = ReadCoverageReport(report, thresholds, validateReportsOnly);
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or System.Xml.XmlException or FormatException or OverflowException)
    {
        Console.Error.WriteLine($"Coverage input validation failed for '{report}': {exception.Message} Restore valid XPlat coverage collection and rerun the gate.");
        return 1;
    }

    // Raw Coverlet conditions can have unequal branch arities. Their declared
    // counts are validated above; only the canonical ReportGenerator merge is
    // evaluated by the aggregation logic below.
    if (validateReportsOnly)
    {
        continue;
    }

    var classElements = document.Descendants("class").ToArray();

    foreach (var classElement in classElements)
    {
        var className = RequiredAttribute(classElement, "name");
        var sourcePath = NormalizeSourcePath(RequiredAttribute(classElement, "filename"));
        if (!files.TryGetValue(sourcePath, out var coverage))
        {
            coverage = new Coverage();
            files.Add(sourcePath, coverage);
        }

        foreach (var line in classElement.Element("lines")?.Elements("line") ?? [])
        {
            var number = int.Parse(RequiredAttribute(line, "number"), CultureInfo.InvariantCulture);
            coverage.Lines.Add(number);
            if (int.Parse(line.Attribute("hits")?.Value ?? "0", CultureInfo.InvariantCulture) > 0)
            {
                coverage.HitLines.Add(number);
            }

            var conditionCoverage = line.Attribute("condition-coverage")?.Value;
            var conditions = line.Element("conditions")?.Elements("condition").ToArray() ?? [];
            if (conditionCoverage is not null && conditions.Length == 0)
            {
                if (!TryParseConditionCoverage(conditionCoverage, out var covered, out var total))
                {
                    throw new InvalidDataException(
                        $"Coverage report '{report}' has invalid branch data for '{sourcePath}:{number}'.");
                }

                coverage.Branches[new BranchKey(className, number, "aggregate")] = new BranchCoverage(covered, total);
                continue;
            }

            var lineBranchTotal = 0;
            if (conditionCoverage is not null &&
                (!TryParseConditionCoverage(conditionCoverage, out _, out lineBranchTotal) ||
                 lineBranchTotal % conditions.Length != 0))
            {
                throw new InvalidDataException(
                    $"Coverage report '{report}' has invalid branch totals for '{sourcePath}:{number}'.");
            }

            var branchesPerCondition = conditions.Length == 0 ? 0 : lineBranchTotal / conditions.Length;
            foreach (var condition in conditions)
            {
                var key = new BranchKey(className, number, RequiredAttribute(condition, "number"));
                var covered = (int)Math.Round(
                    ParsePercentage(RequiredAttribute(condition, "coverage")) * branchesPerCondition / 100,
                    MidpointRounding.AwayFromZero);
                var current = coverage.Branches.GetValueOrDefault(key);
                coverage.Branches[key] = new BranchCoverage(
                    Math.Max(current.Covered, covered), Math.Max(current.Total, branchesPerCondition));
            }
        }
    }
}

if (validateReportsOnly)
{
    Console.WriteLine($"Validated all {args.Length - 1} input Cobertura reports before merging.");
    return 0;
}

var failures = new List<string>();
foreach (var requiredPath in thresholds.Keys.Order(StringComparer.Ordinal))
{
    if (!files.ContainsKey(requiredPath))
    {
        var minimum = thresholds[requiredPath];
        failures.Add(FormattableString.Invariant(
            $"{requiredPath}: measured coverage MISSING; floors line {minimum.Line:P2}, branch {minimum.Branch:P2}; required coverage path is missing from the supplied reports"));
    }
}

var totalLines = 0;
var coveredLines = 0;
var totalBranches = 0;
var coveredBranches = 0;
foreach (var (sourcePath, coverage) in files)
{
    totalLines += coverage.Lines.Count;
    coveredLines += coverage.HitLines.Count;
    totalBranches += coverage.Branches.Values.Sum(static branch => branch.Total);
    coveredBranches += coverage.Branches.Values.Sum(static branch => branch.Covered);

    if (!thresholds.TryGetValue(sourcePath, out var minimum))
    {
        continue;
    }

    if (developmentV1 && coverage.Lines.Count == 0)
    {
        failures.Add(FormattableString.Invariant(
            $"{sourcePath}: measured coverage MISSING (no executable line data); floors line {minimum.Line:P2}, branch {minimum.Branch:P2}"));
        continue;
    }

    var lineRate = Rate(coverage.HitLines.Count, coverage.Lines.Count, 1);
    var fileBranches = coverage.Branches.Values.Sum(static branch => branch.Total);
    var fileCoveredBranches = coverage.Branches.Values.Sum(static branch => branch.Covered);
    var branchRate = Rate(fileCoveredBranches, fileBranches, 1);
    Console.WriteLine(FormattableString.Invariant(
        $"{sourcePath}: line {lineRate:P2} ({coverage.HitLines.Count}/{coverage.Lines.Count}), branch {branchRate:P2} ({fileCoveredBranches}/{fileBranches})"));
    if (lineRate < minimum.Line || branchRate < minimum.Branch)
    {
        failures.Add(FormattableString.Invariant(
            $"{sourcePath}: line {lineRate:P2} (min {minimum.Line:P2}), branch {branchRate:P2} (min {minimum.Branch:P2})"));
    }
}

var aggregateLineRate = Rate(coveredLines, totalLines, 0);
var aggregateBranchRate = Rate(coveredBranches, totalBranches, 0);
var minimumLine = baselineLine - tolerance;
var minimumBranch = baselineBranch - tolerance;
Console.WriteLine(FormattableString.Invariant(
    $"Aggregate coverage: line {aggregateLineRate:P4} ({coveredLines}/{totalLines}), branch {aggregateBranchRate:P4} ({coveredBranches}/{totalBranches})"));
if (!developmentV1 && (aggregateLineRate < minimumLine || aggregateBranchRate < minimumBranch))
{
    failures.Add(FormattableString.Invariant(
        $"aggregate: line {aggregateLineRate:P4} (min {minimumLine:P4}), branch {aggregateBranchRate:P4} (min {minimumBranch:P4})"));
}

if (developmentV1)
{
    Console.WriteLine($"Development v1 enforces {thresholds.Count} canonical per-file floors. Aggregate and component coverage remain local/full-qualification gates; the Unit-only aggregate above is informational.");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Coverage gate failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    Console.Error.WriteLine("Restore missing coverage collection or add tests for the uncovered paths, then rerun the same gate. Keep the checked-in floors unchanged; documented local-only paths require their catalog/Integration prerequisites.");
    return 1;
}

Console.WriteLine(string.Concat("Coverage gate ", "passed."));
return 0;

static string RequiredAttribute(XElement element, string name)
    => element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"Coverage XML element '{element.Name}' is missing attribute '{name}'.");

static XDocument ReadCoverageReport(string report, IReadOnlyDictionary<string, Threshold> thresholds, bool allowEmpty)
{
    var document = XDocument.Load(report, LoadOptions.None);
    if (document.Root?.Name != "coverage" || document.Root.Element("packages") is null)
    {
        throw new InvalidDataException("Expected a Cobertura coverage root with packages.");
    }

    var classes = document.Descendants("class").ToArray();
    if (!allowEmpty && classes.Length == 0)
    {
        throw new InvalidDataException("Coverage report contains no class entries.");
    }

    foreach (var entry in classes)
    {
        _ = RequiredAttribute(entry, "name");
        var sourcePath = NormalizeSourcePath(RequiredAttribute(entry, "filename"));
        foreach (var line in entry.Element("lines")?.Elements("line") ?? [])
        {
            var number = int.Parse(RequiredAttribute(line, "number"), CultureInfo.InvariantCulture);
            var hits = int.Parse(RequiredAttribute(line, "hits"), CultureInfo.InvariantCulture);
            if (number <= 0 || hits < 0)
            {
                throw new InvalidDataException($"Invalid line data for '{sourcePath}:{number}'.");
            }

            if (!thresholds.TryGetValue(sourcePath, out var minimum))
            {
                continue;
            }

            var marker = line.Attribute("branch")?.Value;
            var hasMarker = bool.TryParse(marker, out var hasBranches);
            var conditionCoverage = line.Attribute("condition-coverage")?.Value;
            var conditions = line.Element("conditions")?.Elements("condition").ToArray() ?? [];
            if ((!hasMarker && conditionCoverage is null) ||
                ((hasBranches || conditions.Length > 0) && conditionCoverage is null) ||
                (conditionCoverage is not null &&
                 (!TryParseConditionCoverage(conditionCoverage, out var covered, out var total) ||
                  covered < 0 || covered > total || total < 0 ||
                  ((hasBranches || conditions.Length > 0) && total == 0) ||
                  (hasMarker && !hasBranches && total > 0))))
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"{sourcePath}:{number}: measured branch data MISSING or invalid; branch floor {minimum.Branch:P2}. A branch-marked line needs condition-coverage counts; branchless lines must explicitly declare branch=false."));
            }
        }
    }

    return document;
}

static string NormalizeSourcePath(string path)
{
    var normalized = path.Replace('\\', '/');
    var sourceMarker = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
    if (sourceMarker >= 0)
    {
        return normalized[(sourceMarker + 5)..];
    }
    var toolsMarker = normalized.LastIndexOf("/tools/", StringComparison.Ordinal);
    return toolsMarker >= 0 ? normalized[(toolsMarker + 1)..] : normalized.TrimStart('/');
}

static bool SourcePathExists(string repositoryRoot, string path)
{
    var relativePath = path.StartsWith("tools/", StringComparison.Ordinal)
        ? path
        : Path.Combine("src", path);
    return File.Exists(Path.Combine(repositoryRoot, relativePath));
}

static double ParsePercentage(string value)
{
    var percent = value.EndsWith('%') ? value[..^1] : value;
    return double.Parse(percent, CultureInfo.InvariantCulture);
}

static bool TryParseConditionCoverage(string value, out int covered, out int total)
{
    covered = 0;
    total = 0;
    var open = value.IndexOf('(', StringComparison.Ordinal);
    var slash = value.IndexOf('/', open + 1);
    var close = value.IndexOf(')', slash + 1);
    return open >= 0 && slash > open && close > slash &&
        int.TryParse(value.AsSpan(open + 1, slash - open - 1), CultureInfo.InvariantCulture, out covered) &&
        int.TryParse(value.AsSpan(slash + 1, close - slash - 1), CultureInfo.InvariantCulture, out total);
}

static double Rate(int covered, int total, double empty) => total == 0 ? empty : (double)covered / total;

sealed class Coverage
{
    public HashSet<int> Lines { get; } = [];
    public HashSet<int> HitLines { get; } = [];
    public Dictionary<BranchKey, BranchCoverage> Branches { get; } = [];
}

readonly record struct BranchKey(string ClassName, int Line, string Condition);
readonly record struct BranchCoverage(int Covered, int Total);
readonly record struct Threshold(double Line, double Branch, string? DevelopmentV1LocalOnlyReason);
