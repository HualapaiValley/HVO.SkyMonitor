using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class Issue435RuntimeSignalsTests
{
    [TestMethod]
    public void ManifestDeclaresBoundedLowCardinalityPrivacySafeSignals()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "validation", "issue-435-runtime-signals.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Assert.AreEqual("issue-435-runtime-signals-v1", root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(2, root.GetProperty("metrics").GetArrayLength());
        Assert.AreEqual(2, root.GetProperty("logEventIds").GetArrayLength());
        var forbidden = root.GetProperty("privacy").GetProperty("forbiddenFields")
            .EnumerateArray().Select(static item => item.GetString()).ToArray();
        CollectionAssert.Contains(forbidden, "path");
        CollectionAssert.Contains(forbidden, "payload");
        CollectionAssert.Contains(forbidden, "checksumSha256");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
