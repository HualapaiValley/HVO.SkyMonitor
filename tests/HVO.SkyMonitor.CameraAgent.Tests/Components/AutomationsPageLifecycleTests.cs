using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class AutomationsPageTests
{
    [TestMethod]
    public void RegisteredRestartHasGuidedSetupAndUsesTheExistingImmutableDefinitionCommand()
    {
        using var context = CreateContext();
        var state = Automation(definitions: [], calendar: [], runs: []);
        var service = Register(context, state with { Registry = [.. state.Registry,
            new(LocalAutomationTaskKind.ScheduledCameraAgentRestart, "Installer lifecycle", [LocalAutomationTriggerKind.Periodic],
                ["installed-cameraagent"], true, null)] });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/automations?setup=restart");
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#restart-setup-guide");
        Assert.AreEqual("86400", cut.Find("#automation-interval").GetAttribute("value"));
        Assert.IsEmpty(service.SaveRequests);
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();
        var request = service.SaveRequests.Single();
        Assert.AreEqual("cameraagent-restart", request.DefinitionId);
        Assert.AreEqual(LocalAutomationTaskKind.ScheduledCameraAgentRestart, request.TaskKind);
        Assert.AreEqual("installed-cameraagent", request.TaskTarget);
        Assert.AreEqual(LocalAutomationTriggerKind.Periodic, request.TriggerKind);
    }
}
