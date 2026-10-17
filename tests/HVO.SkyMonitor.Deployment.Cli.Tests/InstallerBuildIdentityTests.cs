using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The installer project's HVO1312 checks of the identity it stamps, evaluated with the patterns the project file holds:
/// the Regex.IsMatch property function MSBuild calls is the .NET method these tests call.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InstallerBuildIdentityTests
{
    [TestMethod]
    [DataRow("InformationalVersion", "1.2.3-rc.1")]
    [DataRow("SourceRevisionId", "0123456789abcdef0123456789abcdef01234567")]
    public void ProjectFile_IdentityCheck_AcceptsTheValueAndRefusesItWithATrailingLineFeed(string property, string value)
    {
        var pattern = IdentityCheckPattern(property);

        Assert.IsTrue(Regex.IsMatch(value, pattern), pattern);
        Assert.IsFalse(Regex.IsMatch(value + "\n", pattern), pattern);
        Assert.IsFalse(Regex.IsMatch(value + "\r\n", pattern), pattern);
    }

    private static string IdentityCheckPattern(string property)
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot(), "src", "HVO.SkyMonitor.Deployment.Cli", "HVO.SkyMonitor.Deployment.Cli.csproj"));
        var call = new Regex($@"IsMatch\('\$\({property}\)', '(?<pattern>[^']+)'\)", RegexOptions.CultureInvariant);
        return project.Descendants("Error")
            .Where(static error => (string?)error.Attribute("Code") == "HVO1312")
            .Select(error => call.Match((string?)error.Attribute("Condition") ?? string.Empty))
            .Single(static match => match.Success)
            .Groups["pattern"].Value;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds HVO.SkyMonitor.v9.slnx.");
    }
}
