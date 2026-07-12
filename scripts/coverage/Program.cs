using System.Globalization;
using System.Xml.Linq;

const double MinimumAggregateLine = 0.40;
const double MinimumAggregateBranch = 0.30;

var highRisk = new HashSet<string>(StringComparer.Ordinal)
{
    "CameraGeometry.cs", "Projection.cs", "VisibleScene.cs", "ImageLayout.cs"
};
var rendererCatalog = new HashSet<string>(StringComparer.Ordinal)
{
    "SceneRenderers.cs", "SqliteCelestialCatalog.cs"
};
var requiredFiles = new HashSet<string>(highRisk, StringComparer.Ordinal);
requiredFiles.UnionWith(rendererCatalog);
var files = new Dictionary<string, Coverage>(StringComparer.Ordinal);

if (args.Length == 0)
{
    throw new ArgumentException("At least one Cobertura report is required.");
}

foreach (var report in args)
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
        var sourcePath = RequiredAttribute(classElement, "filename");
        var filename = Path.GetFileName(sourcePath.Replace('\\', '/'));
        if (!files.TryGetValue(filename, out var coverage))
        {
            coverage = new Coverage();
            files.Add(filename, coverage);
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
            if (conditionCoverage is not null && TryParseConditionCoverage(conditionCoverage, out var covered, out var total))
            {
                var key = (className, number);
                if (!coverage.Branches.TryGetValue(key, out var existing))
                {
                    coverage.Branches.Add(key, new BranchCoverage(covered, total));
                }
                else
                {
                    coverage.Branches[key] = new BranchCoverage(
                        Math.Max(existing.Covered, covered), Math.Max(existing.Total, total));
                }
            }
        }
    }
}

var failures = new List<string>();
var totalLines = 0;
var coveredLines = 0;
var totalBranches = 0;
var coveredBranches = 0;
foreach (var requiredFile in requiredFiles.Order(StringComparer.Ordinal))
{
    if (!files.ContainsKey(requiredFile))
    {
        failures.Add($"required coverage file '{requiredFile}' is missing from the supplied reports");
    }
}

foreach (var (filename, coverage) in files)
{
    totalLines += coverage.Lines.Count;
    coveredLines += coverage.HitLines.Count;
    var fileTotalBranches = coverage.Branches.Values.Sum(static item => item.Total);
    var fileCoveredBranches = coverage.Branches.Values.Sum(static item => item.Covered);
    totalBranches += fileTotalBranches;
    coveredBranches += fileCoveredBranches;

    (double Lines, double Branches)? threshold = highRisk.Contains(filename)
        ? (0.95, 0.90)
        : rendererCatalog.Contains(filename) ? (0.90, 0.85) : null;
    if (threshold is not { } minimum)
    {
        continue;
    }

    var lineRate = Rate(coverage.HitLines.Count, coverage.Lines.Count, 1);
    var branchRate = Rate(fileCoveredBranches, fileTotalBranches, 1);
    if (lineRate < minimum.Lines || branchRate < minimum.Branches)
    {
        failures.Add(FormattableString.Invariant(
            $"{filename}: line {lineRate:P2} (min {minimum.Lines:P0}), branch {branchRate:P2} (min {minimum.Branches:P0})"));
    }
}

var aggregateLineRate = Rate(coveredLines, totalLines, 0);
var aggregateBranchRate = Rate(coveredBranches, totalBranches, 0);
Console.WriteLine(FormattableString.Invariant(
    $"Aggregate coverage: line {aggregateLineRate:P2}, branch {aggregateBranchRate:P2}"));
if (aggregateLineRate < MinimumAggregateLine || aggregateBranchRate < MinimumAggregateBranch)
{
    failures.Add(FormattableString.Invariant(
        $"aggregate: line {aggregateLineRate:P2} (baseline {MinimumAggregateLine:P0}), branch {aggregateBranchRate:P2} (baseline {MinimumAggregateBranch:P0})"));
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

var status = "passed";
Console.WriteLine(string.Concat("Coverage gate ", status, "."));
return 0;

static string RequiredAttribute(XElement element, string name)
    => element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"Coverage XML element '{element.Name}' is missing attribute '{name}'.");

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
    public Dictionary<(string ClassName, int Line), BranchCoverage> Branches { get; } = [];
}

readonly record struct BranchCoverage(int Covered, int Total);
