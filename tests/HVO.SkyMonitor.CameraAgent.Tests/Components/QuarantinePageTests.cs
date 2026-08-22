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

    [TestMethod]
    public void TransientRuntimeSource_RendersExactIdentityAndSafeAbandonmentWarning()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var captureId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        service.QuarantineHandler = (kind, alias, _, pageSize, _) =>
        {
            Assert.AreEqual("TransientRuntime", kind);
            Assert.IsNull(alias);
            Assert.AreEqual(25, pageSize);
            return ValueTask.FromResult(OperatorUiResult<OperatorOutboxPage>.Success(new(
                "TransientRuntime", [], null,
                [new OperatorOutboxItem(
                    "TransientRuntime", "quarantined", null, "Required transient", 3, 4096,
                    OperatorUiTestData.Now, "transient-runtime.input-levels-invalid", null, "abandon-token",
                    OperatorUiTestData.Now.AddHours(-1), AgentId: "agent-east", Lane: "transient",
                    CaptureId: captureId, ArtifactId: artifactId, CaptureSequence: 10, WorkId: 42, OuterWorkId: 41,
                    ManifestSha256: new string('A', 64), PayloadSha256: new string('C', 64),
                    ProcessingProfile: "pipeline configured-v1",
                    ProcessingProfileSha256: new string('B', 64),
                    DurableState: "hybrid; outer completed; work quarantined; frame quarantined")],
                null)));
        };
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/operations/quarantine?kind=TransientRuntime");

        var cut = context.Render<QuarantinePage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "agent-east", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "transient", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, captureId.ToString(), StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(cut.Markup, artifactId.ToString(), StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(cut.Markup, new string('A', 64), StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("button[id$='-replay']"));
            Assert.AreEqual("page", cut.Find(".source-nav a.active").GetAttribute("aria-current"));
        });
        cut.Find("button[id$='-abandon']").Click();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "audited loss decision", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Lane work", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "outer completed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, new string('B', 64), StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Legacy durable rows do not contain deployment-run identity", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".confirmation-actions .btn-danger"));
            Assert.AreEqual("quarantine-confirm-heading", cut.Find("dialog").GetAttribute("aria-labelledby"));
        });
        cut.Find("#deployment-run-id").Change("d331-0821084607");
        cut.Find("#deployment-inventory-sha").Change(new string('d', 64));
        cut.Find("#legacy-ownership-acknowledgment").Change(true);
        cut.Find(".bind-ownership").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("d331-0821084607", cut.Find("#deployment-run-id").GetAttribute("value"));
            Assert.AreEqual(new string('D', 64), cut.Find("#deployment-inventory-sha").GetAttribute("value"));
            StringAssert.Contains(cut.Markup, "sealed into the protected abandon token", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll(".confirmation-actions .btn-danger"));
        });
    }

    [TestMethod]
    public void TransientRuntimeFailure_PreservesSealedOwnershipForRetry()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.QuarantineHandler = (_, _, _, _, _) => ValueTask.FromResult(
            OperatorUiResult<OperatorOutboxPage>.Success(new(
                "TransientRuntime", [], null,
                [new OperatorOutboxItem(
                    "TransientRuntime", "quarantined", null, "Required transient", 3, 4096,
                    OperatorUiTestData.Now, "transient-runtime.input-levels-invalid", null, "reference-token",
                    OperatorUiTestData.Now.AddHours(-1), AgentId: "agent-east", Lane: "transient",
                    CaptureId: Guid.NewGuid(), ArtifactId: Guid.NewGuid(), CaptureSequence: 10,
                    WorkId: 42, OuterWorkId: 41, ManifestSha256: new string('A', 64),
                    PayloadSha256: new string('C', 64), ProcessingProfile: "pipeline configured-v1",
                    ProcessingProfileSha256: new string('B', 64),
                    DurableState: "hybrid; outer completed; work quarantined; frame quarantined")], null)));
        var submissions = new List<string>();
        service.OutboxHandler = (_, _, token, _, _, _) =>
        {
            submissions.Add(token);
            return ValueTask.FromResult(submissions.Count == 1
                ? OperatorUiResult<OperatorCommandReceipt>.Failure(
                    OperatorUiResultKind.Unavailable, "The outbox command could not be completed.")
                : OperatorUiResult<OperatorCommandReceipt>.Success(new(
                    "Abandon transient runtime item", "Applied", "Abandoned", null, OperatorUiTestData.Now)));
        };
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/operations/quarantine?kind=TransientRuntime");
        var cut = context.Render<QuarantinePage>();
        cut.WaitForElement("button[id$='-abandon']").Click();
        cut.Find("#deployment-run-id").Change("d331-0821084607");
        cut.Find("#deployment-inventory-sha").Change(new string('d', 64));
        cut.Find("#legacy-ownership-acknowledgment").Change(true);
        cut.Find(".bind-ownership").Click();
        cut.WaitForElement(".confirmation-actions .btn-danger").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, submissions);
            Assert.AreEqual("bound-action-token", submissions[0]);
            Assert.AreEqual("d331-0821084607", cut.Find("#deployment-run-id").GetAttribute("value"));
            Assert.AreEqual(new string('D', 64), cut.Find("#deployment-inventory-sha").GetAttribute("value"));
            Assert.IsTrue(cut.Find("#legacy-ownership-acknowledgment").HasAttribute("checked"));
            Assert.IsTrue(cut.Find("#legacy-ownership-acknowledgment").HasAttribute("disabled"));
            StringAssert.Contains(cut.Markup, "sealed into the protected abandon token", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "could not be completed", StringComparison.Ordinal);
        });
        cut.Find(".confirmation-actions .btn-danger").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, submissions);
            Assert.IsTrue(submissions.All(static token => token == "bound-action-token"));
            Assert.IsEmpty(cut.FindAll("dialog"));
            StringAssert.Contains(cut.Markup, "Current state: Abandoned", StringComparison.Ordinal);
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
