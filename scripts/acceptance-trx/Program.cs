using System.Globalization;
using System.Xml;
using System.Xml.Linq;

const string DefaultExpectedTest = "HVO.SkyMonitor.IntegrationTests.LogicHostDependencyOutageAcceptanceTests.Issue107_DependenciesDegradeWithoutFabricatedDataAndRecoverWithinBound";
const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
const string SanitizedTestId = "00000000-0000-0000-0000-000000000001";

if (args.Length is < 1 or > 3 || !Path.IsPathFullyQualified(args[0]) ||
    (args.Length == 3 && !Path.IsPathFullyQualified(args[2])))
{
    return Fail("usage: acceptance-trx <absolute-results-directory> [expected-fully-qualified-test] [absolute-sanitized-output]");
}

var resultsDirectory = args[0];
var expectedTest = args.Length >= 2 ? args[1] : DefaultExpectedTest;
if (!TrySplitFullyQualifiedTest(expectedTest, out var expectedClass, out var expectedMethod))
{
    return Fail("expected test name is invalid");
}

var outputPath = args.Length == 3 ? args[2] : null;
if (!TryFindSingleTrx(resultsDirectory, out var trxFile))
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
    using var stream = File.OpenRead(trxFile);
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
var summaries = root.Elements(ns + "ResultSummary").ToArray();
var counterElements = summaries.Length == 1 ? summaries[0].Elements(ns + "Counters").ToArray() : [];
if (results.Length != 1 || definitions.Length != 1 || summaries.Length != 1 || counterElements.Length != 1 ||
    summaries[0].Attribute("outcome")?.Value != "Completed")
{
    return Fail("TRX must describe exactly one completed test");
}

var result = results[0];
var definition = definitions[0];
var testMethods = definition.Descendants(ns + "TestMethod").ToArray();
var declaredName = testMethods.Length == 1
    ? $"{testMethods[0].Attribute("className")?.Value}.{testMethods[0].Attribute("name")?.Value}"
    : null;
if (result.Attribute("testName")?.Value != expectedMethod || definition.Attribute("name")?.Value != expectedMethod ||
    declaredName != expectedTest ||
    result.Attribute("outcome")?.Value != "Passed" ||
    result.Attribute("testId")?.Value != definition.Attribute("id")?.Value)
{
    return Fail("TRX test identity or outcome is invalid");
}

var counters = counterElements[0];
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

if (outputPath is not null && !TryWriteSanitizedTrx(outputPath, expectedClass, expectedMethod))
{
    return Fail("sanitized TRX could not be written safely");
}

return 0;

static bool IsReparsePoint(string path)
    => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

static bool TryFindSingleTrx(string resultsDirectory, out string trxFile)
{
    trxFile = string.Empty;
    try
    {
        if (!Directory.Exists(resultsDirectory) || IsReparsePoint(resultsDirectory))
        {
            return false;
        }

        var trxFiles = Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.TopDirectoryOnly).Take(2).ToArray();
        if (trxFiles.Length != 1 || IsReparsePoint(trxFiles[0]))
        {
            return false;
        }

        trxFile = trxFiles[0];
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}

static bool TrySplitFullyQualifiedTest(string value, out string className, out string methodName)
{
    className = string.Empty;
    methodName = string.Empty;
    if (value.Length is < 3 or > 1024 || value[0] == '.' || value[^1] == '.')
    {
        return false;
    }

    var segments = value.Split('.');
    if (segments.Length < 2 || segments.Any(segment => !IsIdentifier(segment)))
    {
        return false;
    }

    methodName = segments[^1];
    className = string.Join('.', segments, 0, segments.Length - 1);
    return true;
}

static bool IsIdentifier(string value)
{
    if (value.Length == 0 || !(value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
    {
        return false;
    }

    return value.Skip(1).All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

static bool TryWriteSanitizedTrx(string outputPath, string className, string methodName)
{
    if (OperatingSystem.IsWindows())
    {
        return false;
    }

    string? temporaryPath = null;
    try
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory) || IsReparsePoint(outputDirectory) ||
            (File.Exists(outputPath) && IsReparsePoint(outputPath)))
        {
            return false;
        }

        temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        var ns = XNamespace.Get(TrxNamespace);
        var sanitized = new XDocument(
            new XElement(ns + "TestRun",
                new XElement(ns + "Results",
                    new XElement(ns + "UnitTestResult",
                        new XAttribute("testId", SanitizedTestId),
                        new XAttribute("testName", methodName),
                        new XAttribute("outcome", "Passed"))),
                new XElement(ns + "TestDefinitions",
                    new XElement(ns + "UnitTest",
                        new XAttribute("id", SanitizedTestId),
                        new XAttribute("name", methodName),
                        new XElement(ns + "TestMethod",
                            new XAttribute("className", className),
                            new XAttribute("name", methodName)))),
                new XElement(ns + "ResultSummary",
                    new XAttribute("outcome", "Completed"),
                    new XElement(ns + "Counters",
                        new XAttribute("total", 1),
                        new XAttribute("executed", 1),
                        new XAttribute("passed", 1),
                        new XAttribute("failed", 0),
                        new XAttribute("error", 0),
                        new XAttribute("timeout", 0),
                        new XAttribute("aborted", 0),
                        new XAttribute("inconclusive", 0),
                        new XAttribute("notExecuted", 0)))));

        var settings = new XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            NewLineHandling = NewLineHandling.None
        };
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        using (var stream = new FileStream(temporaryPath, fileOptions))
        {
            using var writer = XmlWriter.Create(stream, settings);
            sanitized.Save(writer);
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
        temporaryPath = null;
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
    {
        return false;
    }
    finally
    {
        if (temporaryPath is not null)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
