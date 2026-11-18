using System.Text;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CloudControlsRequireRetainedCloudLayers(bool retained)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) =>
        {
            var presentation = Layered(id, new string('D', 64));
            if (retained)
                presentation = presentation with
                {
                    Layers =
                    [
                        new(new string('E', 64), "cloud-mask", "cloud-mask", 30, true, 1_000_000, "renderer-v1", "style-v1"),
                        new(new string('F', 64), "cloud-labels", "cloud-labels", 40, true, 1_000_000, "renderer-v1", "style-v1")
                    ],
                    Svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"cloud-mask\"></g><g id=\"cloud-labels\"></g></svg>")
                };
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(presentation));
        };
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".sky-layer-canvas img");
        cut.WaitForAssertion(() =>
        {
            var label = cut.FindAll(".scene-panel label").Single(item =>
                item.QuerySelector("strong")?.TextContent == "Measured cloud assessment");
            Assert.AreEqual(!retained, label.QuerySelector("input")!.HasAttribute("disabled"));
            if (retained)
            {
                Assert.HasCount(2, cut.FindAll(".scene-panel input[data-layer-target^='cloud-']"));
                Assert.IsFalse(label.TextContent.Contains("no layer retained", StringComparison.Ordinal));
            }
            else
                StringAssert.Contains(label.TextContent, "no layer retained", StringComparison.Ordinal);
        });
        if (Environment.GetEnvironmentVariable("HVO_CLOUD_ACCEPTANCE_OUTPUT") is { Length: > 0 } evidenceRoot)
        {
            Directory.CreateDirectory(evidenceRoot);
            File.WriteAllText(Path.Combine(evidenceRoot, $"current-sky-cloud-{retained}.html"), cut.Markup);
        }
    }
}
