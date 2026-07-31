using System.Security.Cryptography;
using System.Text.Json;

const string ExpectedSourceHead = "909b8e50ad9ccfbaf6bcd0ecb9ff5773b556195c";
const string ExpectedProductionRevision = "f72da9adcace56f207f69a6aa370cecdc7dc13ad";
const string ExpectedSummarySha256 = "6C398B69A6D2DB9C0C804DFFD9965C0C39A6D36BE4174B95080159F7D74A8C96";
const string ExpectedHarnessSha256 = "91F8706553B6B178E23FF5712F17828F99C68903001FC65B954BC41B8D26BE19";
const string ExpectedEnvironmentSha256 = "A18F054C05BEC238D7B50BC88965E9D2A38C9F7FE8855A1BDEC3585FD423873A";
const string ExpectedWorkloadSha256 = "05ADB4D0C05976C4E6908252B6CA88CB8E95FA7A6B65AAF83EF407B06952ED99";

if (args.Length != 2)
{
    await Console.Error.WriteLineAsync(
        "Usage: issue-246:validate-baseline <baseline-five-trial-summary.json> <baseline-five-trial-manifest.json>")
        .ConfigureAwait(false);
    return 2;
}

var summaryPath = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
if (!File.Exists(summaryPath) || !File.Exists(manifestPath))
{
    await Console.Error.WriteLineAsync("The supplied baseline summary or manifest does not exist.")
        .ConfigureAwait(false);
    return 2;
}

var summaryBytes = await File.ReadAllBytesAsync(summaryPath).ConfigureAwait(false);
RequireEqual(ExpectedSummarySha256, Convert.ToHexString(SHA256.HashData(summaryBytes)), "summary SHA-256");
using var summary = JsonDocument.Parse(summaryBytes);
var summaryRoot = summary.RootElement;
RequireEqual("hvo-issue-246-five-trial-summary-v1", ReadString(summaryRoot, "Schema"), "summary schema");
RequireEqual("baseline", ReadString(summaryRoot, "Phase"), "summary phase");
RequireEqual(ExpectedSourceHead, ReadString(summaryRoot, "SourceHead"), "source head");
RequireEqual(ExpectedProductionRevision, ReadString(summaryRoot, "ProductionRevision"), "production revision");
RequireEqual(ExpectedHarnessSha256, ReadString(summaryRoot, "HarnessSha256"), "harness SHA-256");
RequireEqual(
    ExpectedEnvironmentSha256,
    ReadString(summaryRoot, "EnvironmentFingerprintSha256"),
    "environment fingerprint SHA-256");
RequireEqual(ExpectedWorkloadSha256, ReadString(summaryRoot, "WorkloadSha256"), "workload SHA-256");
if (summaryRoot.GetProperty("TrialCount").GetInt32() != 5)
{
    throw new InvalidOperationException("The reviewed baseline summary must contain exactly five trials.");
}

using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
var manifestRoot = manifest.RootElement;
RequireEqual("hvo-issue-246-five-trial-manifest-v1", ReadString(manifestRoot, "Schema"), "manifest schema");
RequireEqual(
    ExpectedSourceHead,
    ReadString(manifestRoot.GetProperty("Source"), "Head"),
    "manifest source head");
var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
var expectedNames = new HashSet<string>(StringComparer.Ordinal)
{
    "baseline-five-trial-summary.json"
};
foreach (var trial in Enumerable.Range(1, 5))
{
    expectedNames.Add($"trial-{trial}/central-artifact-retention-evidence.json");
    expectedNames.Add($"trial-{trial}/manifest.json");
}
var files = manifestRoot.GetProperty("Files").EnumerateArray().ToArray();
var observedNames = new HashSet<string>(StringComparer.Ordinal);
foreach (var file in files)
{
    var name = ReadString(file, "Name");
    if (!observedNames.Add(name))
    {
        throw new InvalidOperationException($"The baseline manifest contains a duplicate member: {name}");
    }
}
var missingNames = expectedNames.Except(observedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
var unexpectedNames = observedNames.Except(expectedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
if (missingNames.Length != 0 || unexpectedNames.Length != 0)
{
    throw new InvalidOperationException(
        $"The baseline manifest must contain the exact reviewed 11-file set. Missing: "
        + $"{string.Join(", ", missingNames)}. Unexpected: {string.Join(", ", unexpectedNames)}.");
}
var summaryMatched = false;
foreach (var file in files)
{
    var name = ReadString(file, "Name");
    var memberPath = Path.GetFullPath(Path.Combine(manifestDirectory, name));
    if (!memberPath.StartsWith(manifestDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !string.Equals(memberPath, manifestDirectory, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The baseline manifest contains a path outside its evidence directory.");
    }
    if (!File.Exists(memberPath))
    {
        throw new InvalidOperationException($"Baseline manifest member is missing: {name}");
    }
    var bytes = await File.ReadAllBytesAsync(memberPath).ConfigureAwait(false);
    if (file.GetProperty("ByteLength").GetInt64() != bytes.LongLength)
    {
        throw new InvalidOperationException($"Baseline manifest length mismatch: {name}");
    }
    RequireEqual(
        ReadString(file, "Sha256"),
        Convert.ToHexString(SHA256.HashData(bytes)),
        $"manifest member SHA-256 ({name})");
    if (string.Equals(memberPath, summaryPath, StringComparison.Ordinal))
    {
        summaryMatched = true;
        RequireEqual(ExpectedSummarySha256, ReadString(file, "Sha256"), "manifest summary SHA-256");
    }
}
if (!summaryMatched)
{
    throw new InvalidOperationException("The reviewed baseline summary is not a member of the supplied manifest.");
}

await Console.Out.WriteLineAsync(
    $"Issue #246 reviewed baseline validated: source={ExpectedSourceHead}, summary_sha256={ExpectedSummarySha256}")
    .ConfigureAwait(false);
return 0;

static string ReadString(JsonElement element, string propertyName)
    => element.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException($"Baseline property '{propertyName}' is null.");

static void RequireEqual(string expected, string actual, string description)
{
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Issue #246 baseline {description} mismatch. Expected {expected}; observed {actual}.");
    }
}
