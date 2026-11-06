using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>#1383: the Current Sky layer-panel swatches show the colours the overlays are drawn in.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class CurrentSkyLayerSwatchTests
{
    [TestMethod]
    public void OverlaySwatchesUseTheRestoredOverlayColours()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "src", "HVO.SkyMonitor.CameraAgent", "Components", "Pages", "CurrentSkyPage.razor.css"));
        var expected = new (string Kind, string Declaration)[]
        {
            ("scene-constellations", "background: #60a0ff"),
            ("constellations", "background: #60a0ff"),
            ("scene-annotation", "border: 1px dashed #909090"),
            ("bodies", "border: 1px dashed #909090"),
            ("scene-cardinals", "background: #ffffff"),
            ("scene-image-circle", "border: 1px solid #606060"),
            ("boundary", "border: 1px solid #606060"),
            ("cloud-mask", "background: #ff4020"),
            ("cloud", "background: #ff4020"),
            ("cloud-labels", "background: #ffffff"),
            ("environment", "background: #ffffff")
        };
        foreach (var (kind, declaration) in expected)
        {
            var rules = Rule().Matches(css).Where(match => match.Groups["selectors"].Value.Split(',')
                .Any(selector => selector.Trim() == ".layer-swatch." + kind)).ToArray();
            Assert.HasCount(1, rules, kind);
            StringAssert.Contains(rules[0].Groups["body"].Value, declaration, StringComparison.Ordinal, kind);
        }
    }

    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex Rule();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds HVO.SkyMonitor.v9.slnx.");
    }
}
