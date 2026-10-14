using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class RegistrationPageTests
{
    private static readonly DateTimeOffset Now = OperatorUiTestData.Now;
    private const string VerificationCode = "PAIR1234";
    private const string Envelope = "sealed-envelope-body";

    [TestMethod]
    public void NotRegistered_MasksTheCodeAndOffersRegistration()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.NotRegistered)));

        var cut = context.Render<RegistrationPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("Register with LogicHost", cut.Find("#registration-register").TextContent));
        Assert.IsFalse(cut.Find("#registration-register").HasAttribute("disabled"));
        Assert.AreEqual(RegistrationPage.MaskedCode, cut.Find("#registration-code").TextContent);
        Assert.IsFalse(cut.Markup.Contains(VerificationCode, StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "device-identity-1", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "This CameraAgent is not registered", StringComparison.Ordinal);
        Assert.IsNotNull(cut.Find("#registration-lifecycle-heading"));
    }

    [TestMethod]
    public void RevealCode_ShowsAndHidesTheVerificationCode()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.NotRegistered)));
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-reveal-code");

        cut.Find("#registration-reveal-code").Click();

        Assert.AreEqual(VerificationCode, cut.Find("#registration-code").TextContent);
        Assert.AreEqual("true", cut.Find("#registration-reveal-code").GetAttribute("aria-pressed"));

        cut.Find("#registration-reveal-code").Click();

        Assert.AreEqual(RegistrationPage.MaskedCode, cut.Find("#registration-code").TextContent);
        Assert.AreEqual("false", cut.Find("#registration-reveal-code").GetAttribute("aria-pressed"));
    }

    [TestMethod]
    public void CopyDeviceId_WritesItToTheClipboardAndSaysSo()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.NotRegistered)));
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-copy-device");

        cut.Find("#registration-copy-device").Click();

        cut.WaitForAssertion(() => Assert.AreEqual("Device ID copied.", cut.Find(".registration-copy-status").TextContent));
        var copy = context.JSInterop.Invocations.Single(invocation => invocation.Identifier == "navigator.clipboard.writeText");
        Assert.AreEqual("device-identity-1", copy.Arguments[0]);
    }

    [TestMethod]
    public void CopyCode_WhenTheBrowserBlocksTheClipboard_RevealsTheCodeToCopyByHand()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.NotRegistered)));
        context.JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new JSException("blocked"));
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-copy-code");

        cut.Find("#registration-copy-code").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(
            "The browser blocked copying. Select the verification code and copy it by hand.",
            cut.Find(".registration-copy-status").TextContent));
        Assert.AreEqual(VerificationCode, cut.Find("#registration-code").TextContent);
    }

    [TestMethod]
    public void Wizard_SharesTheDeviceThenImportsTheEnvelopeAndReloads()
    {
        var service = new FakeRegistrationService(View(RegistrationState.NotRegistered), View(RegistrationState.Waiting));
        using var context = CreateContext(service);
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-register");

        cut.Find("#registration-register").Click();

        Assert.AreEqual(1, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "showModal"));
        StringAssert.Contains(cut.Find("dialog.registration-dialog .eyebrow").TextContent, "Step 1 of 2", StringComparison.Ordinal);
        Assert.AreEqual(RegistrationPage.MaskedCode, cut.Find("#registration-dialog-code").TextContent);
        Assert.AreEqual("https://logic.example/devices/register", cut.Find("#registration-open-logichost").GetAttribute("href"));

        cut.Find("#registration-dialog-next").Click();

        StringAssert.Contains(cut.Find("dialog.registration-dialog .eyebrow").TextContent, "Step 2 of 2", StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("#registration-dialog-import").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".operations-dialog-note.warning"));

        cut.Find("#registration-envelope").Input(Envelope);
        cut.Find("#registration-dialog-import").Click();

        cut.WaitForAssertion(() => Assert.AreEqual("Registered as Roof camera.", cut.Find(".registration-message strong").TextContent));
        Assert.IsEmpty(cut.FindAll("dialog.registration-dialog"));
        CollectionAssert.AreEqual(new[] { Envelope }, service.Imported);
        Assert.AreEqual(2, service.Reads);
        StringAssert.Contains(cut.Markup, "Roof camera is waiting for LogicHost", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains(Envelope, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Import_WhenLogicHostRejectsTheEnvelope_KeepsTheDialogOpenForARetry()
    {
        var service = new FakeRegistrationService(View(RegistrationState.NotRegistered), View(RegistrationState.Waiting));
        service.ImportResults.Enqueue(OperatorUiResult<RegistrationImportReceipt>.Failure(
            OperatorUiResultKind.Invalid, "LogicHost rejected the envelope."));
        using var context = CreateContext(service);
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-register");
        cut.Find("#registration-register").Click();
        cut.Find("#registration-dialog-next").Click();

        cut.Find("#registration-envelope").Input("expired-envelope");
        cut.Find("#registration-dialog-import").Click();

        cut.WaitForAssertion(() => Assert.AreEqual("LogicHost rejected the envelope.", cut.Find("#registration-import-error").TextContent));
        Assert.AreEqual("registration-import-error", cut.Find("#registration-envelope").GetAttribute("aria-describedby"));
        Assert.AreEqual(string.Empty, cut.Find("#registration-envelope").GetAttribute("value"));
        Assert.IsTrue(cut.Find("#registration-dialog-import").HasAttribute("disabled"));
        Assert.IsFalse(cut.Markup.Contains("expired-envelope", StringComparison.Ordinal));

        cut.Find("#registration-envelope").Input(Envelope);
        cut.Find("#registration-dialog-import").Click();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog.registration-dialog")));
        CollectionAssert.AreEqual(new[] { "expired-envelope", Envelope }, service.Imported);
    }

    [TestMethod]
    public void Import_WhenSetupDoesNotFinish_WarnsThatTheRegistrationIsStored()
    {
        var service = new FakeRegistrationService(View(RegistrationState.NotRegistered), View(RegistrationState.Waiting));
        service.ImportResults.Enqueue(OperatorUiResult<RegistrationImportReceipt>.Success(
            new RegistrationImportReceipt("Roof camera", DeploymentLocationResolutionStatus.Pending, SetupIncomplete: true)));
        using var context = CreateContext(service);
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-register");
        cut.Find("#registration-register").Click();
        cut.Find("#registration-dialog-next").Click();

        cut.Find("#registration-envelope").Input(Envelope);
        cut.Find("#registration-dialog-import").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(
            "Registered as Roof camera, but setup did not finish.", cut.Find(".registration-message strong").TextContent));
        Assert.IsNotNull(cut.Find(".registration-message .status-icon.warning"));
        StringAssert.Contains(cut.Find(".registration-message").TextContent, "do not paste it again", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("dialog.registration-dialog"));
        Assert.AreEqual(2, service.Reads);
    }

    [TestMethod]
    public void Active_OffersRegisterAgainWithAReplacementWarning()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.Active)));
        var cut = context.Render<RegistrationPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("Register again", cut.Find("#registration-register").TextContent));
        StringAssert.Contains(cut.Markup, "Roof camera is active", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Healthy, nothing pending", StringComparison.Ordinal);

        cut.Find("#registration-register").Click();
        cut.Find("#registration-dialog-next").Click();

        StringAssert.Contains(
            cut.Find(".operations-dialog-note.warning").TextContent, "Replaces the current registration", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Rejected_SaysLogicHostRefusesTheCredentials()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.Rejected)));

        var cut = context.Render<RegistrationPage>();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find(".registration-profile").TextContent, "LogicHost refuses Roof camera's credentials", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Refused by LogicHost", StringComparison.Ordinal);
        Assert.IsTrue(cut.FindAll(".ops-progress-step.blocking").Any(step => step.TextContent.Contains("refuses", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Standalone_DisablesRegistrationAndHidesTheLifecycle()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.Standalone)));

        var cut = context.Render<RegistrationPage>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("#registration-register").HasAttribute("disabled")));
        StringAssert.StartsWith(
            cut.Find("#registration-register-unavailable").TextContent, "Central integration is off.", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#registration-lifecycle-heading"));
        StringAssert.Contains(cut.Markup, "A standalone CameraAgent never creates a central device identity.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReadOnlyAccount_CannotStartRegistration()
    {
        using var context = CreateContext(new FakeRegistrationService(View(RegistrationState.NotRegistered) with { CanImport = false }));

        var cut = context.Render<RegistrationPage>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("#registration-register").HasAttribute("disabled")));
        Assert.AreEqual(
            "Operations change rights are required to register this CameraAgent.",
            cut.Find("#registration-register-unavailable").TextContent);
    }

    [TestMethod]
    public void Render_WhenAuthorizationIsRevoked_NavigatesToAccessDenied()
    {
        var service = new FakeRegistrationService();
        service.ReadResults.Enqueue(OperatorUiResult<RegistrationView>.Failure(OperatorUiResultKind.Unauthorized, "Denied"));
        using var context = CreateContext(service);

        context.Render<RegistrationPage>();

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Refresh_WhenTheReadFails_KeepsTheLastStateAndSaysSo()
    {
        var service = new FakeRegistrationService(View(RegistrationState.Active));
        service.ReadResults.Enqueue(OperatorUiResult<RegistrationView>.Failure(
            OperatorUiResultKind.Unavailable, "The registration state could not be read."));
        using var context = CreateContext(service);
        var cut = context.Render<RegistrationPage>();
        cut.WaitForElement("#registration-refresh");

        cut.Find("#registration-refresh").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Showing the last read state.", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Roof camera is active", StringComparison.Ordinal);
    }

    private static BunitContext CreateContext(FakeRegistrationService service)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentRegistrationUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        return context;
    }

    private static RegistrationView View(RegistrationState state)
    {
        var imported = state is RegistrationState.Waiting or RegistrationState.Active or RegistrationState.Rejected;
        var standalone = state == RegistrationState.Standalone;
        return new RegistrationView(
            state,
            standalone ? null : new RegistrationIdentity("device-identity-1", VerificationCode, Now.AddDays(-2)),
            imported
                ? new RegistrationRecord("Roof camera", Now.AddHours(-1), Now.AddDays(30), 10, DeploymentLocationResolutionStatus.Acknowledged)
                : null,
            new RegistrationHeartbeat(
                state == RegistrationState.Rejected ? FleetAvailability.Degraded : FleetAvailability.Available,
                state == RegistrationState.Rejected,
                state == RegistrationState.Active ? Now.AddSeconds(-5) : null,
                state == RegistrationState.Active),
            new RegistrationDelivery(true, ArtifactOutboxAvailability.Healthy, 0, 0, 0, imported),
            standalone ? null : "logic.example",
            standalone ? null : new Uri("https://logic.example/devices/register"),
            !standalone,
            true);
    }

    private sealed class FakeRegistrationService : ICameraAgentRegistrationUiService
    {
        private RegistrationView? _last;

        public FakeRegistrationService(params RegistrationView[] views)
        {
            foreach (var view in views)
            {
                ReadResults.Enqueue(OperatorUiResult<RegistrationView>.Success(view));
            }
        }

        public Queue<OperatorUiResult<RegistrationView>> ReadResults { get; } = new();

        public Queue<OperatorUiResult<RegistrationImportReceipt>> ImportResults { get; } = new();

        public List<string> Imported { get; } = [];

        public int Reads { get; private set; }

        public ValueTask<OperatorUiResult<RegistrationView>> GetRegistrationAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (ReadResults.TryDequeue(out var result))
            {
                _last = result.Value ?? _last;
                return ValueTask.FromResult(result);
            }
            return ValueTask.FromResult(OperatorUiResult<RegistrationView>.Success(_last!));
        }

        public ValueTask<OperatorUiResult<RegistrationImportReceipt>> ImportEnvelopeAsync(string envelope, CancellationToken cancellationToken)
        {
            Imported.Add(envelope);
            return ValueTask.FromResult(ImportResults.TryDequeue(out var result)
                ? result
                : OperatorUiResult<RegistrationImportReceipt>.Success(
                    new RegistrationImportReceipt("Roof camera", DeploymentLocationResolutionStatus.Acknowledged)));
        }
    }
}
