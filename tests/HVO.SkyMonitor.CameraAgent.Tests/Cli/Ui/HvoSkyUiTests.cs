using System.Net;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.CameraAgent.Cli.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli.Ui;

/// <summary>
/// <c>hvo-sky ui</c> (#1317, criterion 3): the command's exit codes, the pages and their keys, what is marked when it is
/// not current, and every change the interface makes, with the same checks, deadlines and outcomes as the commands.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[UnsupportedOSPlatform("windows")]
public sealed class HvoSkyUiTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(20);

    [TestMethod]
    public async Task Ui_WithJson_IsAUsageErrorAndOpensNothingAsync()
    {
        using var harness = new CliHarness { HasTerminal = true, CreateApplication = () => throw new AssertFailedException("Nothing opens.") };

        var result = await harness.RunAsync("ui", "--json").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        Assert.AreEqual("Usage", result.Json().GetProperty("error").GetProperty("kind").GetString());
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task Ui_WithoutATerminal_IsAUsageErrorAsync()
    {
        using var harness = new CliHarness { CreateApplication = () => throw new AssertFailedException("Nothing opens.") };

        var result = await harness.RunAsync("ui").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "needs a terminal", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Ui_ATerminalTerminalGuiCannotStartOn_ExitsOneAsync()
    {
        using var harness = new CliHarness { HasTerminal = true, CreateApplication = () => throw new InvalidOperationException("No console.") };

        var result = await harness.RunAsync("ui").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "could not start on this terminal: No console.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Ui_F10_ClosesItWithExitZeroAndWritesNothingAsync()
    {
        var stopped = false;
        using var harness = new CliHarness
        {
            HasTerminal = true,
            CreateApplication = () => SkyUiDriver.CreateApplication(80, 24),
            RunApplication = (app, window) =>
            {
                var token = app.Begin(window);
                app.InjectKey(Key.F10);
                stopped = window.StopRequested;
                app.End(token!);
            }
        };

        var result = await harness.RunAsync("ui").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.IsTrue(stopped, "F10 stopped the interface.");
        Assert.IsEmpty(result.Out);
        Assert.IsEmpty(result.Error);
    }

    [TestMethod]
    public async Task Ui_ASignal_ClosesItWithExit130Async()
    {
        using var interrupt = new CancellationTokenSource();
        var stopped = false;
        using var harness = new CliHarness
        {
            HasTerminal = true,
            CreateApplication = () => SkyUiDriver.CreateApplication(80, 24),
            RunApplication = (app, window) =>
            {
                var token = app.Begin(window);
                interrupt.Cancel();
                app.TimedEvents!.RunTimers();
                stopped = window.StopRequested;
                app.End(token!);
            }
        };

        var result = await harness.RunAsync(interrupt.Token, "ui").ConfigureAwait(false);

        Assert.AreEqual(130, result.ExitCode, result.ToString());
        Assert.IsTrue(stopped, "The signal stopped the interface.");
    }

    [TestMethod]
    public void TheFKeys_ChooseThePagesAndEscClosesAPromptButNeverTheInterface()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);

        for (var index = 0; index < SkyTerminalUi.PageNames.Length; index++)
        {
            driver.ShowPage(index);
            Assert.AreEqual(index, driver.Ui.PageIndex);
            Assert.AreEqual(index, driver.Ui.Tabs.Current);
        }

        driver.Ui.Pages[4].Press("Sign in");
        Assert.IsNotNull(driver.Ui.Prompt);
        driver.Press(Key.F2);
        driver.Press(Key.F10);
        Assert.AreEqual(4, driver.Ui.PageIndex, "A page key waits while a prompt is open.");
        Assert.IsNotNull(driver.Ui.Prompt, "So does F10.");
        Assert.IsFalse(((IRunnable)driver.Ui.Window).StopRequested);
        StringAssert.Contains(driver.Ui.Message, "Close the prompt first", StringComparison.Ordinal);
        driver.Press(Key.Esc);

        Assert.IsNull(driver.Ui.Prompt, "Esc closed the prompt.");
        Assert.IsFalse(((IRunnable)driver.Ui.Window).StopRequested, "Esc never closes the interface.");
    }

    [TestMethod]
    public void At80Columns_EveryPageKeepsItsNameAndTheHeaderSaysWhoTheKeyIs()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);

        var rows = driver.Rows;
        foreach (var name in SkyTerminalUi.NarrowPageNames)
        {
            StringAssert.Contains(rows[^2], $"F{Array.IndexOf(SkyTerminalUi.NarrowPageNames, name) + 1} {name}", StringComparison.Ordinal);
        }

        StringAssert.EndsWith(rows[0].TrimEnd(), $"[LIVE]  read-write key {CliHarness.KeyId}", StringComparison.Ordinal);
        StringAssert.StartsWith(rows[1].Trim(), $"CameraAgent: {CliHarness.Url} (allsky01, VirtualSky)", StringComparison.Ordinal);
        StringAssert.EndsWith(rows[1].TrimEnd(), "read 19:30:05Z", StringComparison.Ordinal);
        StringAssert.EndsWith(rows[^1].TrimEnd(), "80x24", StringComparison.Ordinal);
    }

    [TestMethod]
    public void WhileTheStatusIsNotLive_ChangesWaitAndSayWhy()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);
        harness.Api.Unreachable = true;

        driver.Tick();

        foreach (var (page, button) in new[] { (1, "Change"), (3, "Activate"), (4, "Restart") })
        {
            driver.ShowPage(page);
            Assert.IsFalse(driver.Ui.Page.Buttons.Single(candidate => candidate.Text == $"[ {button} ]").Enabled, $"{button} on page {page}");
        }

        driver.ShowPage(1);
        StringAssert.Contains(driver.Ui.Message, "Changes need live status", StringComparison.Ordinal);
        StringAssert.Contains(driver.Rows[0], "[OFFLINE]", StringComparison.Ordinal);
    }

    [TestMethod]
    public void NotSignedIn_OpensOnSystemAndSaysHowToSignIn()
    {
        using var harness = new CliHarness(signedIn: false);
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);

        Assert.AreEqual(4, driver.Ui.PageIndex);
        StringAssert.Contains(driver.Rows[0], "[NOT SIGNED IN]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Ui.Notice.Message, "F5 System, Sign in; or run 'hvo-sky login'", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public void SignIn_SavesTheAddressAndKeyAfterCameraAgentAcceptsItAndNeverShowsTheKey()
    {
        using var harness = new CliHarness(signedIn: false);
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.Ui.Pages[4].Press("Sign in");
        var prompt = driver.Ui.Prompt!;
        prompt.Fields[0].Text = CliHarness.Url;
        prompt.Fields[1].SetFocus();
        driver.Type(CliHarness.Key);

        Assert.DoesNotContain(CliHarness.Key, driver.Screen, StringComparison.Ordinal);
        prompt.Choose();
        driver.WaitIdle("the key check");

        StringAssert.Contains(driver.Ui.Notice.Message, $"Signed in with API key {CliHarness.KeyId} (read-write)", StringComparison.Ordinal);
        var saved = SkyCredentialStore.Load(harness.CredentialsPath)!;
        Assert.AreEqual(CliHarness.Key, saved.ApiKey);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(harness.CredentialsPath));
        Assert.IsNotNull(driver.Ui.Connection);
        Assert.DoesNotContain(CliHarness.Key, driver.Screen, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SignIn_AKeyCameraAgentRefuses_IsNotSaved()
    {
        using var harness = new CliHarness(signedIn: false);
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        harness.Api.RefuseAll = HttpStatusCode.Unauthorized;
        driver.Ui.Pages[4].Press("Sign in");
        var prompt = driver.Ui.Prompt!;
        prompt.Fields[0].Text = CliHarness.Url;
        prompt.Fields[1].Text = CliHarness.Key;

        prompt.Choose();
        driver.WaitIdle("the key check");

        StringAssert.StartsWith(driver.Ui.Notice.Message, "[REFUSED] Not signed in: CameraAgent did not accept the API key", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(harness.CredentialsPath));
    }

    [TestMethod]
    public void SignOut_RemovesTheKeyAndKeepsTheAddress()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);
        driver.Ui.Pages[4].Press("Sign out");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the reads after signing out");

        var saved = SkyCredentialStore.Load(harness.CredentialsPath)!;
        Assert.IsNull(saved.ApiKey);
        Assert.AreEqual(new Uri(CliHarness.Url), saved.Url);
        StringAssert.Contains(driver.Ui.Notice.Message, $"Removed API key {CliHarness.KeyId}", StringComparison.Ordinal);
    }

    [TestMethod]
    public void SignOut_CredentialsThatCannotBeWritten_SayTheKeyCouldNotBeRemoved()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);
        driver.Ui.Pages[4].Press("Sign out");
        File.SetUnixFileMode(Path.GetDirectoryName(harness.CredentialsPath)!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        driver.Ui.Prompt!.Choose();

        StringAssert.StartsWith(driver.Ui.Prompt!.Error, "The API key could not be removed: ", StringComparison.Ordinal);
        File.SetUnixFileMode(Path.GetDirectoryName(harness.CredentialsPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.AreEqual(CliHarness.Key, SkyCredentialStore.Load(harness.CredentialsPath)!.ApiKey);
    }

    [TestMethod]
    public void UnusableCredentials_AreNotSignedInWithTheReasonOnTheSystemPage()
    {
        using var harness = new CliHarness();
        File.SetUnixFileMode(harness.CredentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);

        Assert.AreEqual(4, driver.Ui.PageIndex);
        StringAssert.Contains(driver.Ui.Notice.Message, "can be read or changed by other users", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public void Restart_WaitsForTheNewProcessAndSaysWhenItCameBack()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);
        driver.Ui.Pages[4].Press("Restart");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the restart");

        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.RestartRoute));
        StringAssert.Contains(driver.Ui.Notice.Message, "CameraAgent is back: started 2026-10-18T18:05:00Z", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Restart_AcceptedThenNotBackWithinTheDeadline_SaysItWasAcceptedButNotVerified()
    {
        using var harness = new CliHarness { RestartReadsBeforeNewProcess = -1 };
        using var driver = SkyUiDriver.Start(harness, timeout: TimeSpan.FromSeconds(1), pollInterval: Quick);
        driver.ShowPage(4);
        driver.Ui.Pages[4].Press("Restart");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the restart");

        StringAssert.StartsWith(driver.Ui.Notice.Message,
            "[UNVERIFIED] CameraAgent accepted the restart, but it could not be verified: Timed out after 1 s (--timeout) waiting for CameraAgent to come back",
            StringComparison.Ordinal);
        Assert.DoesNotContain("was not restarted", driver.Ui.Notice.Message, StringComparison.Ordinal);
        AssertNoticeWhole(driver);
    }

    [TestMethod]
    public void Restart_RefusedByCameraAgent_SaysItWasNotRestarted()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("POST", Fake.RestartRoute, HttpStatusCode.Conflict, "CameraAgent is not supervised here.", "restart.unsupervised");
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);
        driver.Ui.Pages[4].Press("Restart");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the restart");

        StringAssert.StartsWith(driver.Ui.Notice.Message, "[REFUSED] CameraAgent was not restarted: CameraAgent is not supervised here.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RigApply_WaitsUntilTheStagedRevisionIsActive()
    {
        using var harness = new CliHarness();
        var readsAfterRestart = 0;
        harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            harness.Api.RequestsTo("POST", Fake.RestartRoute).Count == 0 || ++readsAfterRestart <= 2
                ? Fake.Rigs(active: "rig-1", pending: "rig-2")
                : Fake.Rigs(active: "rig-2", pending: null)));
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(2);
        driver.Ui.Pages[2].Press("Apply");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the rig");

        StringAssert.Contains(driver.Ui.Notice.Message, "Rig revision rig-2 is active.", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(3, readsAfterRestart);
    }

    [TestMethod]
    public void RigApply_ACameraThatFailsToInitialize_SaysSoWithCameraAgentsReason()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            harness.Api.RequestsTo("POST", Fake.RestartRoute).Count == 0
                ? Fake.Rigs(active: "rig-1", pending: "rig-2")
                : Fake.Rigs(active: "rig-1", pending: "rig-2", pendingFailure: "The camera did not initialize.")));
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(2);
        driver.Ui.Pages[2].Press("Apply");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the rig");

        StringAssert.Contains(driver.Ui.Notice.Message, "rig revision rig-2 did not become active: The camera did not initialize.", StringComparison.Ordinal);
        AssertNoticeWhole(driver);
    }

    [TestMethod]
    public void RigApply_ARevisionThatNeverBecomesActive_SaysItWasAcceptedAndShowsItWhole()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs(active: "rig-1", pending: "rig-2"));
        using var driver = SkyUiDriver.Start(harness, timeout: TimeSpan.FromSeconds(1), pollInterval: Quick);
        driver.ShowPage(2);
        driver.Ui.Pages[2].Press("Apply");

        driver.Ui.Prompt!.Choose();
        driver.WaitIdle("the rig");

        StringAssert.StartsWith(driver.Ui.Notice.Message,
            "[UNVERIFIED] CameraAgent accepted the restart that applies rig revision rig-2, but it could not be verified: Timed out after 1 s (--timeout) waiting for rig revision rig-2 to become active",
            StringComparison.Ordinal);
        AssertNoticeWhole(driver);
    }

    [TestMethod]
    public void RigStage_PreviewsAsksForTheAcknowledgementAndStages()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(2);
        driver.Press(Key.CursorDown);
        driver.Ui.Pages[2].Press("Stage");
        driver.WaitIdle("the preview");
        var prompt = driver.Ui.Prompt!;

        prompt.Choose();
        StringAssert.Contains(prompt.Error, "Tick", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.RigRoute + "/stage"));
        prompt.Acknowledgement!.Checked = true;
        prompt.Choose();
        driver.WaitIdle("the stage");

        var body = harness.Api.RequestsTo("POST", Fake.RigRoute + "/stage").Single().BodyJson();
        Assert.AreEqual("rig-2", body.GetProperty("revisionId").GetString());
        Assert.AreEqual(7, body.GetProperty("expectedVersion").GetInt64());
        StringAssert.Contains(driver.Ui.Notice.Message, "Staged rig revision rig-2.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Schedule_AnOlderRevision_GoesBackByRollback()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(3);
        driver.Press(Key.CursorDown);
        driver.Ui.Pages[3].Press("Activate");
        var prompt = driver.Ui.Prompt!;

        StringAssert.Contains(prompt.Describe(), "going back from the active revision", StringComparison.Ordinal);
        prompt.Choose();
        driver.WaitIdle("the rollback");

        Assert.AreEqual("schedule-2", harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/rollback").Single().BodyJson().GetProperty("revisionId").GetString());
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/activate"));
        StringAssert.Contains(driver.Ui.Notice.Message, "Schedule revision schedule-2 is active from the next capture boundary.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Schedule_ARevisionListedWithoutANumber_IsRefusedRatherThanGuessed()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.ScheduleRoute, new
        {
            stateVersion = 11,
            activeRevision = Fake.ScheduleRevision("schedule-3", 3),
            pendingRevision = (object?)null,
            history = new object[] { new { revisionId = "schedule-2" } }
        });
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(3);
        driver.Press(Key.CursorDown);

        driver.Ui.Pages[3].Press("Activate");

        Assert.IsNull(driver.Ui.Prompt);
        StringAssert.StartsWith(driver.Ui.Notice.Message, "[REFUSED] Schedule revision schedule-2 was not applied: whether", StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "POST"));
    }

    [TestMethod]
    public void Settings_AChangeIsCheckedAgainstTheCatalogueThenSentWithTheGroupsVersion()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(1);
        var field = driver.Ui.Data.Settings.Value!.FindField("skyMap.maximumObjects", exactOnly: true)!;
        var prompt = SkyUiSettingChange.Open(driver.Ui, field);

        prompt.Fields[0].Text = "999999";
        prompt.Choose();
        Assert.IsNotNull(driver.Ui.Prompt, "A value the catalogue refuses keeps the prompt open.");
        Assert.IsNotEmpty(prompt.Error);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
        prompt.Fields[0].Text = "450";
        prompt.Fields[1].Text = "fewer labels";
        prompt.Choose();
        driver.WaitIdle("the change");

        var body = harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/skyMap").Single().BodyJson();
        Assert.AreEqual("skyMap-v1", body.GetProperty("expectedVersion").GetString());
        Assert.AreEqual(450, body.GetProperty("values").GetProperty("skyMap.maximumObjects").GetInt32());
        Assert.AreEqual("fewer labels", body.GetProperty("reason").GetString());
        StringAssert.Contains(driver.Ui.Notice.Message, "skyMap.maximumObjects saved", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Settings_AChangeCameraAgentRefuses_SaysItWasNotSavedWithItsReasons()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/skyMap", HttpStatusCode.Conflict, "The settings group changed since it was read.", "settings.conflict");
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(1);
        var field = driver.Ui.Data.Settings.Value!.FindField("skyMap.maximumObjects", exactOnly: true)!;
        var prompt = SkyUiSettingChange.Open(driver.Ui, field);
        prompt.Fields[0].Text = "450";

        prompt.Choose();
        driver.WaitIdle("the change");

        StringAssert.StartsWith(driver.Ui.Notice.Message, "[REFUSED] skyMap.maximumObjects was not saved: The settings group changed since it was read.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AReadOnlyKey_CannotChangeAnythingAndSaysWhy()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.InfoRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK, Fake.Info(harness.StartedUtc, access: "Read")));
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);
        driver.ShowPage(4);

        foreach (var (page, button) in new[] { (1, "Change"), (2, "Apply"), (3, "Activate"), (4, "Restart") })
        {
            driver.ShowPage(page);
            Assert.IsFalse(driver.Ui.Page.Buttons.Single(candidate => candidate.Text == $"[ {button} ]").Enabled, $"{button} on page {page}");
        }

        StringAssert.Contains(driver.Screen, "read-only", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AReadWhoseBodyNeverArrives_EndsAtItsDeadlineAndTheStatusIsMarkedOffline()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, timeout: TimeSpan.FromSeconds(1), pollInterval: Quick);
        harness.Api.StallBodies = true;

        driver.Tick();

        StringAssert.Contains(driver.Rows[0], "[OFFLINE]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Ui.Notice.Message, "Timed out after 1 s (--timeout)", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "LAST KNOWN", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AStatusNotReadAgainInTime_IsMarkedStale()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);

        ((SkyUiClock)harness.Time).Now = SkyUiClock.Observed.AddSeconds(30);
        driver.Ui.UpdateChrome();

        StringAssert.Contains(driver.Rows[0], "[STALE]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Ui.Notice.Message, "LAST KNOWN, as of 2026-10-18T19:30:05Z", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheInterface_RunsWithoutTerminalGuisSyntaxHighlighting()
    {
        // As the installer's wizard: Terminal.Gui brings in TextMateSharp, its grammars and Onigwrap's native Oniguruma
        // for its Code and Markdown views. hvo-sky ships without them (Directory.Build.targets), and this test project
        // runs without them too, so every page here, and every other interface test, shows they are not needed.
        var here = AppContext.BaseDirectory;
        string[] left = ["TextMateSharp.dll", "TextMateSharp.Grammars.dll", "Onigwrap.dll"];
        Assert.IsEmpty(left.Where(file => File.Exists(Path.Combine(here, file))).ToArray());
        Assert.IsEmpty(Directory.EnumerateFiles(here, "*onigwrap*", SearchOption.AllDirectories).ToArray());

        using (var harness = new CliHarness())
        using (var driver = SkyUiDriver.Start(harness, pollInterval: Quick))
        {
            for (var index = 0; index < SkyTerminalUi.PageNames.Length; index++)
            {
                driver.ShowPage(index);
                StringAssert.Contains(driver.Rows[0], "[LIVE]", StringComparison.Ordinal);
            }

            driver.Ui.Pages[4].Press("Sign in");
            Assert.IsNotNull(driver.Ui.Prompt);
        }

        string[] highlighting = ["TextMateSharp", "TextMateSharp.Grammars", "Onigwrap"];
        Assert.IsEmpty(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => highlighting.Contains(assembly.GetName().Name)).ToArray());
    }

    /// <summary>The notice band shows the whole notice at 80 x 24: an outcome is never cut.</summary>
    private static void AssertNoticeWhole(SkyUiDriver driver)
    {
        var rows = driver.Rows;
        var height = driver.Ui.Notice.Frame.Height;
        var shown = string.Join(' ', rows.Skip(2).Take(height).Select(row => row.Trim()));
        Assert.AreEqual(Normalize(driver.Ui.Notice.Message), Normalize(shown), driver.Screen);

        static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    }

    [TestMethod]
    public void TheKey_IsOnNoPage()
    {
        using var harness = new CliHarness();
        using var driver = SkyUiDriver.Start(harness, pollInterval: Quick);

        for (var index = 0; index < SkyTerminalUi.PageNames.Length; index++)
        {
            driver.ShowPage(index);
            Assert.DoesNotContain(CliHarness.Key, driver.Screen, StringComparison.Ordinal);
            Assert.DoesNotContain(CliHarness.Key[(SkyApiKey.Prefix.Length + SkyApiKey.IdLength + 1)..], driver.Screen, StringComparison.Ordinal);
            StringAssert.Contains(driver.Ui.Page.Describe(), driver.Ui.Page.Name == "System" ? CliHarness.KeyId : string.Empty, StringComparison.Ordinal);
        }

        CliAssert.KeyNeverLeaks(new CliResult(0, driver.Screen, driver.Output), harness.Api, CliHarness.Key);
    }
}
