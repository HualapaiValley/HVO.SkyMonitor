using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class RetainedPreviewImageTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProbeFailureKeepsNormalImageErrorHandlerWorking(bool timeout)
    {
        using var context = new BunitContext();
        var runtime = new Mock<IJSRuntime>();
        runtime.Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Throws(timeout ? new TaskCanceledException("Interop timeout") : new JSException("Module unavailable"));
        context.Services.AddSingleton(runtime.Object);
        var failures = 0;
        var image = context.Render<RetainedPreviewImage>(parameters => parameters
            .Add(component => component.Source, "/exact-source/preview")
            .Add(component => component.Alt, "Exact source")
            .Add(component => component.OnFailure, () => failures++));
        image.WaitForAssertion(() => Assert.HasCount(1, image.FindAll("img")));
        Assert.AreEqual(0, failures);
        image.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        image.WaitForAssertion(() => Assert.AreEqual(1, failures));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ImageFailureBeforeOrAfterAttachmentNotifiesTheParent(bool failedBeforeAttachment)
    {
        using var context = new BunitContext();
        RetainedPreviewImageTestSupport.Configure(context, failedBeforeAttachment);
        var failures = 0;
        var image = context.Render<RetainedPreviewImage>(parameters => parameters
            .Add(component => component.Source, "/api/v1/operations/artifacts/exact-source/preview")
            .Add(component => component.Alt, "Exact source")
            .Add(component => component.OnFailure, () => failures++));
        image.WaitForAssertion(() => Assert.AreEqual(failedBeforeAttachment ? 1 : 0, failures));
        if (!failedBeforeAttachment)
        {
            image.Find("img").TriggerEvent("onerror", EventArgs.Empty);
            image.WaitForAssertion(() => Assert.AreEqual(1, failures));
        }
    }
}

internal static class RetainedPreviewImageTestSupport
{
    internal static void Configure(BunitContext context, bool failed = false)
    {
        context.JSInterop.SetupModule("./Components/Presentation/RetainedPreviewImage.razor.js")
            .Setup<bool>("hasFailed", _ => true).SetResult(failed);
    }
}
