using System.Xml.Linq;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void Hosts_DoNotReferenceEachOther()
    {
        var root = FindRepositoryRoot();
        AssertNoProjectReference(
            Path.Combine(root, "src", "HVO.SkyMonitor.CameraAgent", "HVO.SkyMonitor.CameraAgent.csproj"),
            "HVO.SkyMonitor.LogicHost");
        AssertNoProjectReference(
            Path.Combine(root, "src", "HVO.SkyMonitor.LogicHost", "HVO.SkyMonitor.LogicHost.csproj"),
            "HVO.SkyMonitor.CameraAgent");
    }

    [TestMethod]
    public void AgentCore_DoesNotReferenceHostOrRenderingDependencies()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "src", "HVO.SkyMonitor.AgentCore", "HVO.SkyMonitor.AgentCore.csproj");
        var document = XDocument.Load(project);
        var references = document.Descendants("ProjectReference").Select(item => (string?)item.Attribute("Include"))
            .Concat(document.Descendants("PackageReference").Select(item => (string?)item.Attribute("Include")))
            .Where(static item => item is not null).Cast<string>();

        Assert.IsFalse(references.Any(reference => reference.Contains("CameraAgent", StringComparison.Ordinal) ||
            reference.Contains("LogicHost", StringComparison.Ordinal) || reference.Contains("SkiaSharp", StringComparison.Ordinal) ||
            reference.Contains("EntityFramework", StringComparison.Ordinal) || reference.Contains("Minio", StringComparison.Ordinal)));
    }

    private static void AssertNoProjectReference(string projectPath, string forbiddenName)
    {
        var references = XDocument.Load(projectPath).Descendants("ProjectReference")
            .Select(item => (string?)item.Attribute("Include"));
        Assert.IsFalse(references.Any(reference => reference?.Contains(forbiddenName, StringComparison.Ordinal) == true));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
