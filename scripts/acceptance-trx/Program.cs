using System.Globalization;
using System.Xml;
using System.Xml.Linq;

const string ExpectedTest = "HVO.SkyMonitor.IntegrationTests.LogicHostDependencyOutageAcceptanceTests.Issue107_DependenciesDegradeWithoutFabricatedDataAndRecoverWithinBound";
const string ExpectedMethod = "Issue107_DependenciesDegradeWithoutFabricatedDataAndRecoverWithinBound";
const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
{
    return Fail("usage: acceptance-trx <absolute-results-directory>");
}

var resultsDirectory = args[0];
if (!Directory.Exists(resultsDirectory) || IsReparsePoint(resultsDirectory))
{
    return Fail("results directory is missing or unsafe");
}

var trxFiles = Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.TopDirectoryOnly).ToArray();
if (trxFiles.Length != 1 || IsReparsePoint(trxFiles[0]))
{
    return Fail("exactly one regular TRX file is required");
}

XDocument document;
try
{
    var settings = new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        MaxCharactersInDocument = 8 * 1024 * 1024,
        XmlResolver = null
    };
    using var stream = File.OpenRead(trxFiles[0]);
    using var reader = XmlReader.Create(stream, settings);
    document = XDocument.Load(reader, LoadOptions.None);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
{
    return Fail("TRX is unreadable or malformed");
}

var root = document.Root;
if (root is null || root.Name != XName.Get("TestRun", TrxNamespace))
{
    return Fail("TRX root is invalid");
}

var ns = XNamespace.Get(TrxNamespace);
var results = root.Descendants(ns + "UnitTestResult").ToArray();
var definitions = root.Descendants(ns + "UnitTest").ToArray();
var summary = root.Elements(ns + "ResultSummary").SingleOrDefault();
var counters = summary?.Elements(ns + "Counters").SingleOrDefault();
if (results.Length != 1 || definitions.Length != 1 || summary?.Attribute("outcome")?.Value != "Completed" || counters is null)
{
    return Fail("TRX must describe exactly one completed test");
}

var result = results[0];
var definition = definitions[0];
var testMethod = definition.Descendants(ns + "TestMethod").SingleOrDefault();
var declaredName = testMethod is null
    ? null
    : $"{testMethod.Attribute("className")?.Value}.{testMethod.Attribute("name")?.Value}";
if (result.Attribute("testName")?.Value != ExpectedMethod || definition.Attribute("name")?.Value != ExpectedMethod ||
    declaredName != ExpectedTest ||
    result.Attribute("outcome")?.Value != "Passed" ||
    result.Attribute("testId")?.Value != definition.Attribute("id")?.Value)
{
    return Fail("TRX test identity or outcome is invalid");
}

foreach (var attribute in counters.Attributes())
{
    if (!int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
    {
        return Fail("TRX counters are invalid");
    }
    var expected = attribute.Name.LocalName is "total" or "executed" or "passed" ? 1 : 0;
    if (value != expected)
    {
        return Fail("TRX does not contain exactly one passing test");
    }
}

foreach (var required in new[] { "total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted" })
{
    if (counters.Attribute(required) is null)
    {
        return Fail("TRX counters are incomplete");
    }
}

return 0;

static bool IsReparsePoint(string path)
    => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
