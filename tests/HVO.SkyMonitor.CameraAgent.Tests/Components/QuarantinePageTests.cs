using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class QuarantinePageTests
{
    [TestMethod]
    public void CursorPages_ExposeRecordsBeyondFiftyWithoutPaths()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.QuarantineHandler = (kind, alias, cursor, pageSize, _) =>
        {
            Assert.AreEqual("Artifact", kind);
            Assert.AreEqual(25, pageSize);
            var start = cursor switch { null => 0, "25" => 25, "50" => 50, _ => -1 };
            var items = Enumerable.Range(start, Math.Min(25, 60 - start))
                .Select(CreateItem)
                .ToArray();
            var next = start switch { 0 => "25", 25 => "50", _ => null };
            return ValueTask.FromResult(OperatorUiResult<OperatorOutboxPage>.Success(new(
                "Artifact", ["raw-ingress", "storage-1"], alias ?? "raw-ingress", items, next)));
        };

        var cut = context.Render<QuarantinePage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Record 24", StringComparison.Ordinal));
        cut.Find(".cursor-nav .btn-primary").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Record 49", StringComparison.Ordinal));
        cut.Find(".cursor-nav .btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Record 59", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("/tmp/", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("main"));
        });
    }

    [TestMethod]
    public void ReplayFailure_KeepsNativeDialogOpenAndCancelClosesIt()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.QuarantineHandler = (_, _, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<OperatorOutboxPage>.Success(new(
                "Artifact", ["raw-ingress"], "raw-ingress", [CreateItem(1)], null)));
        service.OutboxHandler = (_, _, _, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Unavailable, "The outbox command could not be completed."));

        var cut = context.Render<QuarantinePage>();
        cut.WaitForElement("button[id$='-replay']").Click();
        cut.Find(".confirmation-actions .btn-primary").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("DIALOG", cut.Find("dialog").TagName);
            Assert.AreEqual("alert", cut.Find(".dialog-error").GetAttribute("role"));
        });
        cut.Find("dialog").TriggerEvent("oncancel", EventArgs.Empty);
        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation => invocation.Identifier == "close"));
        });
    }

    private static OperatorOutboxItem CreateItem(int index) => new(
        "Artifact",
        "Quarantined",
        "raw-ingress",
        "Preview",
        index,
        1024,
        OperatorUiTestData.Now.AddMinutes(-index),
        "invalid-source",
        $"replay-{index}",
        $"abandon-{index}",
        OperatorUiTestData.Now.AddHours(-1),
        OperatorUiTestData.Now,
        "image/jpeg",
        $"Record {index}");

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        return service;
    }
}
