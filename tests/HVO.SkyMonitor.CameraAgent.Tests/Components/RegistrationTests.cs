using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Account.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class RegistrationTests
{
    [TestMethod]
    public void Register_DoesNotExposeAccountCreationForm()
    {
        using var context = new BunitContext();

        var component = context.Render<Register>();

        StringAssert.Contains(component.Markup, "Local self-registration is disabled.", StringComparison.Ordinal);
        Assert.IsEmpty(component.FindAll("form"));
        Assert.IsEmpty(component.FindAll("input"));
        Assert.IsEmpty(component.FindAll("button"));
    }
}
