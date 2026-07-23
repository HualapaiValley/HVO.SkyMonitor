using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SystemStatusPageTests
{
    [TestMethod]
    public void Status_RendersAllowlistAndNoMutationOrSensitiveConfiguration()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<SystemStatusPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Unversioned startup snapshot", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Virtual sensor", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Processing pipeline", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Environment delivery", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Transient", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("form"));
            Assert.IsFalse(cut.Markup.Contains("Apply", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("/tmp/", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("connection", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("latitude", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("longitude", StringComparison.OrdinalIgnoreCase));
        });
    }

    [TestMethod]
    public void Failure_RendersAlert()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService
        {
            SystemHandler = _ => ValueTask.FromResult(
                OperatorUiResult<CameraAgentSystemStatus>.Failure(OperatorUiResultKind.Unavailable, "The startup configuration snapshot is unavailable."))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<SystemStatusPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("alert", cut.Find(".system-state--error").GetAttribute("role")));
    }

    [TestMethod]
    public void StandaloneStatus_RendersCentralAndEffectiveDeliveryAsDisabled()
    {
        using var context = new BunitContext();
        var source = OperatorUiTestData.SystemStatus();
        var service = new TestOperatorUiService
        {
            SystemHandler = _ => ValueTask.FromResult(
                OperatorUiResult<CameraAgentSystemStatus>.Success(source with
                {
                    CentralIntegration = "Disabled",
                    Upload = source.Upload with { Enabled = false },
                    Environmental = source.Environmental with { Enabled = false }
                }))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<SystemStatusPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Central integration", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Disabled", StringComparison.Ordinal);
            var upload = cut.FindAll("article").Single(node => node.TextContent.Contains("Artifact delivery policy", StringComparison.Ordinal));
            var environmental = cut.FindAll("article").Single(node => node.TextContent.Contains("Observation policy", StringComparison.Ordinal));
            StringAssert.Contains(upload.TextContent, "EnabledNo", StringComparison.Ordinal);
            StringAssert.Contains(environmental.TextContent, "EnabledNo", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void AssemblyQualifiedProcessingType_IsNeverRendered()
    {
        using var context = new BunitContext();
        const string sentinel = "Sensitive.Processing.Step, Sensitive.Assembly, Version=1.0.0.0";
        var status = OperatorUiTestData.SystemStatus() with
        {
            ModuleType = sentinel,
            Pipeline = [new CameraAgentPipelineNodeStatus("safe-node", sentinel, true, [])]
        };
        var service = new TestOperatorUiService
        {
            SystemHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentSystemStatus>.Success(status))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<SystemStatusPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(cut.Markup.Contains(sentinel, StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("Sensitive.Assembly", StringComparison.Ordinal));
            StringAssert.Contains(cut.Markup, "Unavailable", StringComparison.Ordinal);
        });
    }
}
