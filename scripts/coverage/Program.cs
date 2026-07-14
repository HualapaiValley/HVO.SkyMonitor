using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length < 2)
{
    throw new ArgumentException("A baseline file and at least one Cobertura report are required.");
}

using var baselineDocument = JsonDocument.Parse(File.ReadAllText(args[0]));
var aggregate = baselineDocument.RootElement.GetProperty("aggregate");
var baselineLine = aggregate.GetProperty("line").GetDouble();
var baselineBranch = aggregate.GetProperty("branch").GetDouble();
var tolerance = aggregate.GetProperty("tolerancePercentagePoints").GetDouble() / 100;
var thresholds = baselineDocument.RootElement.GetProperty("files").EnumerateObject().ToDictionary(
    static property => property.Name,
    static property => new Threshold(
        property.Value.GetProperty("line").GetDouble(),
        property.Value.GetProperty("branch").GetDouble()),
    StringComparer.Ordinal);
var files = new Dictionary<string, Coverage>(StringComparer.Ordinal);

foreach (var report in args.Skip(1))
{
    var document = XDocument.Load(report, LoadOptions.None);
    var classElements = document.Descendants("class").ToArray();
    if (classElements.Length == 0)
    {
        throw new InvalidDataException($"Coverage report '{report}' contains no class entries.");
    }

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

var failures = new List<string>();
foreach (var requiredPath in thresholds.Keys.Order(StringComparer.Ordinal))
{
    if (!files.ContainsKey(requiredPath))
    {
        failures.Add($"required coverage path '{requiredPath}' is missing from the supplied reports");
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

    var lineRate = Rate(coverage.HitLines.Count, coverage.Lines.Count, 1);
    var fileBranches = coverage.Branches.Values.Sum(static branch => branch.Total);
    var fileCoveredBranches = coverage.Branches.Values.Sum(static branch => branch.Covered);
    var branchRate = Rate(fileCoveredBranches, fileBranches, 1);
    Console.WriteLine(FormattableString.Invariant(
        $"{sourcePath}: line {lineRate:P2} ({coverage.HitLines.Count}/{coverage.Lines.Count}), branch {branchRate:P2} ({fileCoveredBranches}/{fileBranches})"));
    if (lineRate < minimum.Line || branchRate < minimum.Branch)
    {
        failures.Add(FormattableString.Invariant(
            $"{sourcePath}: line {lineRate:P2} (min {minimum.Line:P0}), branch {branchRate:P2} (min {minimum.Branch:P0})"));
    }
}

var aggregateLineRate = Rate(coveredLines, totalLines, 0);
var aggregateBranchRate = Rate(coveredBranches, totalBranches, 0);
var minimumLine = baselineLine - tolerance;
var minimumBranch = baselineBranch - tolerance;
Console.WriteLine(FormattableString.Invariant(
    $"Aggregate coverage: line {aggregateLineRate:P4} ({coveredLines}/{totalLines}), branch {aggregateBranchRate:P4} ({coveredBranches}/{totalBranches})"));
if (aggregateLineRate < minimumLine || aggregateBranchRate < minimumBranch)
{
    failures.Add(FormattableString.Invariant(
        $"aggregate: line {aggregateLineRate:P4} (min {minimumLine:P4}), branch {aggregateBranchRate:P4} (min {minimumBranch:P4})"));
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Coverage gate failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine(string.Concat("Coverage gate ", "passed."));
return 0;

static string RequiredAttribute(XElement element, string name)
    => element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"Coverage XML element '{element.Name}' is missing attribute '{name}'.");

static string NormalizeSourcePath(string path)
{
    var normalized = path.Replace('\\', '/');
    var sourceMarker = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
    return sourceMarker >= 0 ? normalized[(sourceMarker + 5)..] : normalized.TrimStart('/');
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
readonly record struct Threshold(double Line, double Branch);
