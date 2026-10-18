using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;

/// <summary>
/// The installer's wizard (install contract section 5, #1317), driven by keys on a machine of its own: each page is
/// passed over when the command line answered its questions, each refuses an answer the install would refuse, the
/// answers it saves are an answers file that installs exactly as the same file given to <c>--answers</c>, the plan it
/// shows is the one <c>--plan</c> makes, and leaving it changes nothing.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InstallWizardTests
{
    private const string Release = "/srv/hvo/release";
    private const string Catalog = "/srv/hvo/catalog.bundle";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("Camera", new[] { "--friendly-name", "North" }, false)]
    [DataRow("Camera", new[] { "--port", "5140" }, true)]
    [DataRow("Software", new[] { "--release", Release, "--catalog-bundle", Catalog }, false)]
    [DataRow("Software", new[] { "--image-ref", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--catalog-bundle", Catalog }, false)]
    [DataRow("Software", new[] { "--release", Release }, true)]
    [DataRow("Software", new[] { "--catalog-bundle", Catalog }, true)]
    [DataRow("Network", new[] { "--bind-address", "127.0.0.1", "--port", "5130" }, false)]
    [DataRow("Network", new[] { "--bind-address", "0.0.0.0", "--port", "5130" }, true)]
    [DataRow("Network", new[] { "--bind-address", "0.0.0.0", "--port", "5130", "--acknowledge-plaintext-http" }, false)]
    [DataRow("Network", new[] { "--port", "5130" }, true)]
    [DataRow("Location", new[] { "--latitude", "35", "--longitude", "-114", "--elevation", "1300", "--time-zone", "UTC" }, false)]
    [DataRow("Location", new[] { "--latitude", "35", "--longitude", "-114", "--elevation", "1300" }, true)]
    [DataRow("Limits", new[] { "--cpus", "2", "--memory", "4g", "--replay-profile", "in-process" }, false)]
    [DataRow("Limits", new[] { "--cpus", "2", "--memory", "4g" }, true)]
    [DataRow("Owner", new[] { "--owner-email", "a@example.test", "--generate-password" }, false)]
    [DataRow("Owner", new[] { "--owner-email", "a@example.test", "--password-file", "/srv/hvo/password" }, false)]
    [DataRow("Owner", new[] { "--owner-email", "a@example.test" }, true)]
    [DataRow("Owner", new[] { "--generate-password" }, true)]
    [DataRow("Review", new[] { "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog }, true)]
    public void EachPage_IsPassedOverWhenTheCommandLineAnsweredItsQuestions(string page, string[] given, bool shown)
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", .. given]);

        Assert.AreEqual(shown, driver.Wizard.Pages.Single(candidate => candidate.StepName == page).Applies);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void NoCommandLine_AsksEveryPage_AndCountsTheStepsThatApply()
    {
        using var host = new WizardHost();
        using var all = WizardDriver.Start(host, []);
        Assert.IsTrue(all.Wizard.Pages.All(page => page.Applies));
        Assert.IsInstanceOfType<CameraPage>(all.Page);
        Assert.AreEqual("STEP 1 OF 7", all.Wizard.Header.Badge);

        using var some = WizardDriver.Start(host, ["cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog, "--owner-email", "a@example.test", "--generate-password"]);
        Assert.AreEqual("STEP 1 OF 5", some.Wizard.Header.Badge);
        string[] steps = ["1 Camera", "2 Network", "3 Location", "4 Limits", "5 Review"];
        CollectionAssert.AreEqual(steps, some.Wizard.Steps.Tabs.ToArray());
        some.Replace("North");
        Assert.IsInstanceOfType<NetworkPage>(some.NextTo<NetworkPage>());

        using var nameGiven = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", "North"]);
        Assert.IsInstanceOfType<SoftwarePage>(nameGiven.Page, "the first page with a question");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void AnAnswerTheCommandLineGave_IsShownWithItsOptionAndNotAsked()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--release", Release, "--image-source", "registry", "--port", "5140", "--bind-address", "0.0.0.0", "--acknowledge-plaintext-http"]);
        driver.Replace("North");

        var software = driver.NextTo<SoftwarePage>();
        Assert.IsNull(software.ReleaseField);
        Assert.IsNull(software.SourceChoice);
        Assert.IsNotNull(software.CatalogField);
        CollectionAssert.Contains(software.Describe().ToArray(), $"Release folder: {Release} (given: --release)");
        StringAssert.Contains(driver.Screen, $"{Release}  (--release)", StringComparison.Ordinal);

        driver.Type(Catalog);
        var location = driver.NextTo<LocationPage>();
        Assert.IsFalse(driver.PageOf<NetworkPage>().Applies, "the network page was answered: address, port and plain HTTP");
        Assert.IsNotNull(location.LatitudeField);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void APageWithSomeAnswersGiven_ShowsThemBesideItsOpenQuestions()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host,
        [
            "cameraagent", "install", "--friendly-name", "North", "--image-source", "registry", "--bind-address", "0.0.0.0",
            "--latitude", "35", "--longitude", "-114", "--elevation", "1300", "--cpus", "3", "--memory", "6g",
            "--password-file", "/srv/hvo/password"
        ]);

        var software = (SoftwarePage)driver.Page;
        Assert.IsNotNull(software.ReleaseField);
        Assert.IsNull(software.SourceChoice);
        CollectionAssert.Contains(software.Describe().ToArray(), "Image from: registry (given: --image-source)");
        driver.Type(Release);
        driver.Press(Key.Tab);
        driver.Type(Catalog);

        var network = driver.NextTo<NetworkPage>();
        Assert.IsNull(network.AddressField);
        Assert.IsTrue(network.Acknowledgement!.Visible, "the given address is one other computers reach");
        Stays(driver, "Other computers reach this address over plain HTTP: tick the box to say you know.");
        driver.Press(Key.Tab);
        driver.Press(Key.Space);
        Assert.IsTrue(network.Acknowledgement.Checked);

        var location = driver.NextTo<LocationPage>();
        Assert.IsNull(location.LatitudeField);
        Assert.IsNotNull(location.TimeZoneField);
        CollectionAssert.Contains(location.Describe().ToArray(), "Latitude: 35 (given: --latitude)");

        var limits = driver.NextTo<LimitsPage>();
        Assert.IsNull(limits.CpusField);
        Assert.IsNull(limits.MemoryField);
        Assert.IsNotNull(limits.ReplayChoice);

        var owner = driver.NextTo<OwnerPage>();
        Assert.IsNull(owner.PasswordChoice);
        CollectionAssert.Contains(owner.Describe().ToArray(), "First password: /srv/hvo/password (given: --password-file)");
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        Assert.IsTrue(((ReviewPage)driver.Page).Check!.CanInstall, driver.Screen);

        var answers = driver.Session.ToAnswers();
        Assert.AreEqual(35d, answers.LatitudeDegrees);
        Assert.AreEqual(3m, answers.Cpus);
        Assert.AreEqual("/srv/hvo/password", answers.PasswordFile);
        Assert.IsTrue(answers.AcknowledgePlaintextHttp);
        Assert.IsNull(answers.ImageSource, "handed over beside the answers");
        Assert.AreEqual(Release, answers.Release);
        CollectionAssert.Contains(driver.Session.Arguments("/a.json"), "--image-source");

        using var generated = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog, "--generate-password"]);
        var withGenerated = generated.NextTo<OwnerPage>();
        CollectionAssert.Contains(withGenerated.Describe().ToArray(), "First password: generated (given: --generate-password)");
        Assert.IsTrue(generated.Session.ToAnswers().GeneratePassword);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void TheWizardsAnswers_InstallExactlyAsTheSameAnswersFile()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, []);

        driver.Replace("North Camera");
        driver.NextTo<SoftwarePage>();
        driver.Type(Release);
        driver.Press(Key.Tab);
        driver.Press(Key.CursorDown);
        driver.Press(Key.Tab);
        driver.Type(Catalog);
        var network = driver.NextTo<NetworkPage>();
        driver.Replace("0.0.0.0");
        Assert.IsTrue(network.Acknowledgement!.Visible, "asked for an address other computers reach");
        driver.Press(Key.Tab);
        driver.Replace("5140");
        driver.Press(Key.Tab);
        driver.Press(Key.Space);
        driver.NextTo<LocationPage>();
        driver.Replace("35.2");
        driver.Press(Key.Tab);
        driver.Replace("-114.1");
        driver.Press(Key.Tab);
        driver.Replace("1300");
        driver.Press(Key.Tab);
        driver.Replace("America/Phoenix");
        driver.NextTo<LimitsPage>();
        driver.Replace("3");
        driver.Press(Key.Tab);
        driver.Replace("6g");
        driver.Press(Key.Tab);
        driver.Press(Key.CursorDown);
        driver.NextTo<OwnerPage>();
        driver.Replace("admin@example.test");
        driver.Press(Key.Tab);
        driver.Press(Key.CursorDown);
        var review = driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        Assert.IsTrue(review.Check!.CanInstall, driver.Screen);
        driver.Press(Key.Enter);

        Assert.AreEqual(InstallWizardResult.Install, driver.Wizard.Result);
        Assert.IsTrue(driver.Closed);
        Assert.AreEqual(host.AnswersPath, driver.Session.AnswersPath);
        using var file = new AnswersFile(
            $$"""
            {"schemaVersion":1,"instanceId":"{{WizardHost.InstanceId:D}}","friendlyName":"North Camera","ownerEmail":"admin@example.test",
             "bindAddress":"0.0.0.0","port":5140,"catalogBundle":"{{Catalog}}","release":"{{Release}}","imageSource":"registry",
             "latitudeDegrees":35.2,"longitudeDegrees":-114.1,"elevationMeters":1300,"timeZoneId":"America/Phoenix",
             "acknowledgePlaintextHttp":true,"generatePassword":true,"replayProfile":"LocalRunner","cpus":3,"memory":"6g"}
            """);
        var fromWizard = Request(host, driver.Session.Arguments(driver.Session.AnswersPath));
        var fromFile = Request(host, ["cameraagent", "install", "--answers", file.Path]);
        Assert.AreEqual(fromFile, fromWizard);
        CollectionAssert.AreEqual(new[] { "cameraagent", "install", "--answers", host.AnswersPath }, driver.Session.Arguments(host.AnswersPath));

        // The saved file is the operator's only, in a folder that is theirs only, and holds no secret.
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(host.AnswersPath));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(host.AnswersPath)!));
        Assert.AreEqual(InstallAnswers.Read(file.Path) with { }, InstallAnswers.Read(host.AnswersPath));
        Assert.DoesNotContain("password\"", File.ReadAllText(host.AnswersPath).Replace("generatePassword", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        // The plan the wizard showed is the one --plan makes for that file.
        var plan = host.Requests.Single(request => request.DryRun);
        Assert.AreEqual(fromFile with { DryRun = true, Json = true }, plan);
        Assert.IsEmpty(host.Installs, "the wizard itself installs nothing");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void WhatInstallShHandsOver_StaysBesideTheAnswers()
    {
        using var host = new WizardHost();
        string[] given = ["cameraagent", "install", "--release", "/tmp/hvo-sky-install.x/release", "--image-source", "registry", "--catalog-bundle", "/home/a/.cache/hvo-sky/c.bundle", "--no-download", "--cpus", "4"];
        using var driver = WizardDriver.Start(host, given);
        driver.Replace("North");
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        driver.Press(Key.Enter);

        var arguments = driver.Session.Arguments(host.AnswersPath);
        CollectionAssert.AreEqual(
            new[] { "cameraagent", "install", "--answers", host.AnswersPath, "--release", "/tmp/hvo-sky-install.x/release", "--image-source", "registry", "--catalog-bundle", "/home/a/.cache/hvo-sky/c.bundle", "--no-download" },
            arguments);
        var saved = InstallAnswers.Read(host.AnswersPath);
        Assert.IsNull(saved.Release, "install.sh's own folder is not saved");
        Assert.IsNull(saved.CatalogBundle);
        Assert.IsNull(saved.ImageSource);
        Assert.AreEqual(4m, saved.Cpus, "a question the command line answered is saved");
        var request = Request(host, arguments);
        Assert.AreEqual("/tmp/hvo-sky-install.x/release", request.Release);
        Assert.AreEqual(ReleaseImageSource.Registry, request.ImageSource);
        Assert.IsTrue(request.NoDownload);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void SavedAnswers_AreWhereTheQuestionsStart_SoTheSameInstanceIsAddressedAgain()
    {
        using var host = new WizardHost();
        var instance = Guid.Parse("11111111-2222-4333-8444-555555555555");
        Directory.CreateDirectory(Path.GetDirectoryName(host.AnswersPath)!);
        File.WriteAllText(host.AnswersPath, $$"""{"schemaVersion":1,"instanceId":"{{instance:D}}","friendlyName":"Saved Camera","port":5150,"timeZoneId":"UTC","passwordFile":"/srv/p","latitudeDegrees":1.5,"cpus":2.5,"replayProfile":"LocalRunner","release":"{{Release}}","imageSource":"registry"}""");
        using var driver = WizardDriver.Start(host, []);

        Assert.AreEqual(instance, driver.Session.Answers.InstanceId);
        StringAssert.StartsWith(driver.Session.InstanceIdSource, "from the saved answers", StringComparison.Ordinal);
        Assert.AreEqual("Saved Camera", driver.PageOf<CameraPage>().NameField!.Text);
        var a = driver.Session.Answers;
        Assert.AreEqual("5150", a.Port);
        Assert.AreEqual("1.5", a.Latitude);
        Assert.AreEqual("2.5", a.Cpus);
        Assert.AreEqual(OwnerPasswordChoice.File, a.Password);
        Assert.AreEqual(CameraAgentReplayProfile.LocalRunner, a.ReplayProfile);
        Assert.AreEqual(ReleaseImageSource.Registry, a.ImageSource);
        Assert.AreEqual(Release, a.Release);

        using var given = WizardDriver.Start(host, ["cameraagent", "install", "--instance-id", WizardHost.InstanceId.ToString("D"), "--generate-password"]);
        Assert.AreEqual(WizardHost.InstanceId, given.Session.Answers.InstanceId, "the command line wins");
        Assert.AreEqual("from --instance-id", given.Session.InstanceIdSource);
        Assert.AreEqual(OwnerPasswordChoice.Generate, given.Session.Answers.Password);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void SavedAnswersThatCannotBeRead_AreSaidAndTheQuestionsStartFromTheDefaults()
    {
        using var host = new WizardHost();
        Directory.CreateDirectory(Path.GetDirectoryName(host.AnswersPath)!);
        File.WriteAllText(host.AnswersPath, "{\"schemaVersion\":9}");
        using var driver = WizardDriver.Start(host, []);

        Assert.IsNull(driver.Session.Saved);
        Assert.AreEqual(WizardHost.InstanceId, driver.Session.Answers.InstanceId);
        Assert.AreEqual(HvoNotice.Warning, driver.Wizard.Notice.Kind);
        StringAssert.StartsWith(driver.Wizard.Notice.Message, "[WARNING] The saved answers could not be read", StringComparison.Ordinal);
        Assert.AreEqual("new", driver.Session.InstanceIdSource);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void EachPage_RefusesAnAnswerTheInstallWouldRefuse_AndSaysWhy()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, []);

        Stays(driver, "The camera needs a name.");
        driver.Replace(new string('x', 81));
        Stays(driver, "The camera's name is at most 80 characters.");
        driver.Replace("North");
        driver.NextTo<SoftwarePage>();
        driver.Type("relative/release");
        Stays(driver, "The release folder must be an absolute path, starting with /.");
        driver.Replace(Release);
        Stays(driver, "Give the catalog bundle.");
        driver.Press(Key.Tab);
        driver.Press(Key.Tab);
        driver.Type(Catalog);
        driver.NextTo<NetworkPage>();
        driver.Replace("not-an-address");
        Stays(driver, "The address must be an IPv4 address, such as 127.0.0.1 or 0.0.0.0.");
        driver.Replace("0.0.0.0");
        Stays(driver, "Other computers reach this address over plain HTTP: tick the box to say you know.");
        driver.Replace("127.0.0.1");
        driver.Press(Key.Tab);
        driver.Replace("70000");
        Stays(driver, "The port must be a number from 1 to 65535.");
        driver.Replace("5130");
        driver.NextTo<LocationPage>();
        driver.Replace("91");
        Stays(driver, "The latitude must be a number of degrees from -90 (south) to 90 (north).");
        driver.Replace("0");
        driver.Press(Key.Tab);
        driver.Replace("east");
        Stays(driver, "The longitude must be a number of degrees from -180 (west) to 180 (east).");
        driver.Replace("0");
        driver.Press(Key.Tab);
        driver.Replace("high");
        Stays(driver, "The elevation must be a number of metres.");
        driver.Replace("0");
        driver.Press(Key.Tab);
        driver.Replace("Nowhere/Never");
        Stays(driver, "This machine does not know the time zone 'Nowhere/Never': give one such as America/Phoenix or UTC.");
        driver.Replace(" ");
        Stays(driver, "Give the time zone, such as America/Phoenix or UTC.");
        driver.Replace("UTC");
        driver.NextTo<LimitsPage>();
        driver.Replace("two");
        Stays(driver, "The CPUs must be a number, such as 2 or 2.5, or empty for the default.");
        driver.Replace("1");
        Stays(driver, null);
        driver.Replace(string.Empty);
        driver.Press(Key.Tab);
        driver.Replace("lots");
        Stays(driver, null);
        driver.Replace(string.Empty);
        driver.NextTo<OwnerPage>();
        driver.Replace("not an email");
        Stays(driver, "The owner's email must be an address such as you@example.com, or empty for the default.");
        driver.Replace(string.Empty);
        driver.Press(Key.Tab);
        driver.Press(Key.End);
        Assert.IsTrue(driver.PageOf<OwnerPage>().FileField!.Visible, "the file is asked for only when a file is chosen");
        Stays(driver, "Give the password file.");
        driver.Press(Key.Tab);
        driver.Type("/srv/hvo/password");
        driver.NextTo<ReviewPage>();
        Assert.AreEqual(OwnerPasswordChoice.File, driver.Session.Answers.Password);
        Assert.AreEqual("/srv/hvo/password", driver.Session.ToAnswers().PasswordFile);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void TheReview_WaitsForThePlanAndShowsIt()
    {
        using var host = new WizardHost();
        host.PlanReleased.Reset();
        using var driver = ToReview(host);

        var review = (ReviewPage)driver.Page;
        Assert.IsTrue(review.Checking);
        Assert.IsFalse(driver.Wizard.NextButton.Enabled, "Install waits for the check");
        StringAssert.StartsWith(driver.Wizard.Notice.Message, "[CHECKING]", StringComparison.Ordinal);
        driver.Press(Key.Enter);
        Assert.IsFalse(driver.Closed);

        host.PlanReleased.Set();
        driver.WaitIdle("the plan check");
        Assert.IsTrue(driver.Wizard.NextButton.Enabled);
        Assert.AreEqual("[ Install ]", driver.Wizard.NextButton.Text);
        Assert.AreEqual(HvoNotice.Success, driver.Wizard.Notice.Kind);
        var lines = review.Describe();
        StringAssert.StartsWith(lines[0], $"Install North, instance {WizardHost.InstanceId:D}, at http://127.0.0.1:5130/", StringComparison.Ordinal);
        CollectionAssert.Contains(lines.ToArray(), "  create     container  hvo-sky-cameraagent-6f9b3c1e2d4a4e8b9c7d1a2b3c4d5e6f (2 CPUs, 4 GiB, 256 PIDs)");
        CollectionAssert.Contains(lines.ToArray(), "5 to create, 1 to change, 0 unchanged.");
        Assert.AreEqual(1, host.Requests.Count);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void APlanWithNothingToChange_SaysSo()
    {
        using var host = new WizardHost();
        using var driver = ToReview(host);
        var review = (ReviewPage)driver.Page;
        var request = Request(host, ["cameraagent", "install", "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog]);
        var unchanged = WizardHost.Result(request, planned: true) with
        {
            Plan = DeploymentPlan.Of([new(DeploymentPlanAction.Unchanged, "folder", "/x")]),
            Warnings = [new("w", "Docker's data root is nearly full.")]
        };

        var lines = review.Lines(new WizardPlanCheck(unchanged, null, string.Empty, []));

        CollectionAssert.Contains(lines.Select(line => line.Text).ToArray(), "Nothing to change.");
        CollectionAssert.Contains(lines.Select(line => line.Text).ToArray(), "[WARNING] Docker's data root is nearly full.");
        Assert.AreEqual(HvoTone.Muted, lines.Single(line => line.Text.Contains("unchanged", StringComparison.Ordinal)).Tone);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("refused", "[REFUSED]")]
    [DataRow("usage", "[CHECK]")]
    [DataRow("failed", "[ERROR]")]
    [DataRow("prerequisites", "[REFUSED]")]
    [DataRow("unexpected", "[ERROR]")]
    public void APlanThatCannotBeCarriedOut_SaysWhyThatNothingChangedAndBackChangesTheAnswers(string failure, string word)
    {
        using var host = new WizardHost
        {
            PlanFailure = _ => failure switch
            {
                "refused" => InstallerException.Refused("Port 5130 is in use."),
                "usage" => new InstallUsageException("--time-zone is not installed on this host."),
                "failed" => new InstallerException("Docker did not answer."),
                "prerequisites" => new HostPrerequisiteException("The host does not meet CameraAgent's prerequisites."),
                _ => new InvalidOperationException("Something unforeseen.")
            }
        };
        using var driver = ToReview(host);
        driver.WaitIdle("the plan check");

        var review = (ReviewPage)driver.Page;
        Assert.IsFalse(review.Check!.CanInstall);
        Assert.IsFalse(driver.Wizard.NextButton.Enabled);
        Assert.AreEqual(HvoNotice.Danger, driver.Wizard.Notice.Kind);
        StringAssert.StartsWith(driver.Wizard.Notice.Message, word, StringComparison.Ordinal);
        StringAssert.EndsWith(driver.Wizard.Notice.Message, "Nothing was changed.", StringComparison.Ordinal);
        CollectionAssert.Contains(review.Describe().ToArray(), "Nothing was changed. Back changes the answers; Esc cancels.");
        driver.Press(Key.Enter);
        Assert.IsFalse(driver.Closed, "nothing to install");

        driver.Press(Key.B.WithAlt);
        Assert.IsInstanceOfType<OwnerPage>(driver.Page);
        Assert.IsEmpty(host.Installs);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ThePlanCheck_UsesAPrivateCopyOfTheAnswersAndRemovesIt()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog]);
        var scratch = Path.Combine(host.Root, "scratch");
        Directory.CreateDirectory(scratch);
        driver.Session.ScratchRoot = scratch;

        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");

        Assert.IsTrue(((ReviewPage)driver.Page).Check!.CanInstall, driver.Screen);
        Assert.IsEmpty(Directory.GetFileSystemEntries(scratch));
        Assert.IsFalse(File.Exists(host.AnswersPath), "nothing is saved before Install");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Install_SavesTheAnswersWhereTheOperatorSays_OrSaysWhyNot()
    {
        using var host = new WizardHost();
        using var driver = ToReview(host);
        driver.WaitIdle("the plan check");
        var review = (ReviewPage)driver.Page;

        review.SavePath.Text = Path.Combine(host.Root, "missing", "answers.json");
        driver.Press(Key.Enter);
        Assert.IsFalse(driver.Closed);
        StringAssert.StartsWith(driver.Wizard.Notice.Message, $"[ERROR] The answers could not be saved at {Path.Combine(host.Root, "missing", "answers.json")}", StringComparison.Ordinal);
        review.SavePath.Text = "relative.json";
        driver.Press(Key.Enter);
        StringAssert.Contains(driver.Wizard.Notice.Message, "must be an absolute path", StringComparison.Ordinal);

        var chosen = Path.Combine(host.Root, "answers.json");
        review.SavePath.Text = chosen;
        driver.Press(Key.Enter);
        Assert.IsTrue(driver.Closed);
        Assert.AreEqual(InstallWizardResult.Install, driver.Wizard.Result);
        Assert.AreEqual(chosen, driver.Session.AnswersPath);
        Assert.AreEqual(driver.Session.AnswersJson(), File.ReadAllText(chosen));
        Assert.IsFalse(File.Exists(host.AnswersPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("esc")]
    [DataRow("ctrl+c")]
    [DataRow("signal")]
    public void Leaving_ClosesTheWizardWithNothingChanged(string how)
    {
        using var host = new WizardHost();
        using var driver = ToReview(host);
        driver.WaitIdle("the plan check");

        switch (how)
        {
            case "esc":
                driver.Press(Key.Esc);
                break;
            case "ctrl+c":
                driver.Press(Key.C.WithCtrl);
                break;
            default:
                driver.Wizard.Cancel();
                break;
        }

        Assert.IsTrue(driver.Closed);
        Assert.AreEqual(InstallWizardResult.Cancelled, driver.Wizard.Result);
        Assert.IsTrue(driver.Wizard.Closing.IsCancellationRequested, "work it started stops");
        Assert.IsFalse(File.Exists(host.AnswersPath));
        Assert.IsEmpty(host.Installs);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Keys_NextBackAndTheButtons()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, []);
        driver.Replace("North");
        driver.Press(Key.N.WithAlt);
        Assert.IsInstanceOfType<SoftwarePage>(driver.Page);
        Assert.IsTrue(driver.Wizard.BackButton.Enabled);
        driver.Press(Key.B.WithAlt);
        Assert.IsInstanceOfType<CameraPage>(driver.Page);
        Assert.IsFalse(driver.Wizard.BackButton.Enabled, "nothing before the first page");
        driver.Press(Key.B.WithAlt);
        Assert.IsInstanceOfType<CameraPage>(driver.Page);

        driver.NextTo<SoftwarePage>();
        driver.Wizard.BackButton.SetFocus();
        driver.Press(Key.Enter);
        Assert.IsInstanceOfType<CameraPage>(driver.Page, "Enter presses the focused button");
        driver.Wizard.NextButton.SetFocus();
        driver.Press(Key.Enter);
        Assert.IsInstanceOfType<SoftwarePage>(driver.Page);
        Assert.AreEqual("North", driver.Session.Answers.FriendlyName);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void TheAnswersSoFar_ShowBesideThePageFrom110Columns_WithTheGivenOnesMarked()
    {
        using var host = new WizardHost();
        using var narrow = WizardDriver.Start(host, ["cameraagent", "install", "--port", "5140"], 80, 24);
        Assert.IsFalse(narrow.Wizard.SummaryShown);
        Assert.DoesNotContain("ANSWERS", narrow.Screen, StringComparison.Ordinal);

        using var wide = WizardDriver.Start(host, ["cameraagent", "install", "--port", "5140"], 120, 32);
        Assert.IsTrue(wide.Wizard.SummaryShown);
        StringAssert.Contains(wide.Screen, "ANSWERS", StringComparison.Ordinal);
        var summary = wide.Wizard.Summary().Select(line => line.Text).ToArray();
        CollectionAssert.Contains(summary, "Address     127.0.0.1:5140 *");
        CollectionAssert.Contains(summary, "Camera      -");
        CollectionAssert.Contains(summary, "* given on the command line");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ALongNotice_TakesTwoRowsFromThePage_EvenAfterAnotherLongOne()
    {
        using var host = new WizardHost { PlanFailure = _ => InstallerException.Refused("Port 5130 on 127.0.0.1 is already in use by another program.") };
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog]);

        driver.Replace("0.0.0.0");
        driver.Press(Key.Enter);
        Assert.AreEqual(2, driver.Wizard.NoticeRows);
        driver.Replace("127.0.0.1");
        driver.NextTo<LocationPage>();
        Assert.AreEqual(1, driver.Wizard.NoticeRows);
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");

        Assert.AreEqual(2, driver.Wizard.NoticeRows);
        var rows = driver.Rows;
        StringAssert.StartsWith(rows[2], " [REFUSED] Port 5130", StringComparison.Ordinal);
        StringAssert.StartsWith(rows[3], " was changed.", StringComparison.Ordinal);
        StringAssert.Contains(rows[4], "REVIEW THE PLAN", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void EveryPagesIntro_FitsItsTwoRows()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, []);
        foreach (var page in driver.Wizard.Pages.Where(page => page is not ReviewPage))
        {
            page.Showing();
            Assert.IsLessThanOrEqualTo(2, HvoTextLines.Wrap(page.Describe()[0], 72).Count(), page.StepName);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void NoColour_DrawsInTheTerminalsOwnColoursWithTheFocusInReverseVideo()
    {
        using var host = new WizardHost(new Dictionary<string, string?> { ["NO_COLOR"] = "1" });
        using var driver = WizardDriver.Start(host, []);
        driver.Replace("North");

        Assert.IsTrue(driver.DrawnAttributes().All(attribute => attribute.Foreground == Color.None && attribute.Background == Color.None));
        Assert.IsTrue(driver.ColoursOf("North").Style.HasFlag(TextStyle.Reverse), "the focused field");
        Assert.IsTrue(driver.ColoursOf("[1 Camera]").Style.HasFlag(TextStyle.Reverse), "the current step");
        Assert.IsFalse(HvoConsoleTextTests.HasColour(driver.Ansi));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void SixteenColoursAndAsciiFrames_ForATerminalThatCannotDrawMore()
    {
        using var host = new WizardHost(new Dictionary<string, string?> { ["TERM"] = "vt100", ["COLORTERM"] = null, ["LANG"] = "C" });
        using var driver = WizardDriver.Start(host, []);

        Assert.AreEqual(new TerminalCapabilities(TerminalColours.Ansi16, TerminalBorders.Ascii), driver.Wizard.Theme.Capabilities);
        Assert.IsTrue(driver.App.Driver!.Force16Colors);
        Assert.IsTrue(driver.Rows.All(row => row.All(c => c < 128)), driver.Screen);
        StringAssert.Contains(driver.Screen, "+- CAMERA -", StringComparison.Ordinal);
        Assert.DoesNotContain("38;2;", driver.Ansi, StringComparison.Ordinal);
        Assert.AreEqual(new Color(ColorName16.BrightCyan), driver.ColoursOf("CAMERA").Foreground);
    }

    private static void Stays(WizardDriver driver, string? problem)
    {
        var page = driver.Page;
        driver.Press(Key.Enter);
        Assert.AreSame(page, driver.Page, $"stays on {page.StepName}");
        Assert.AreEqual(HvoNotice.Danger, driver.Wizard.Notice.Kind);
        if (problem is not null)
        {
            Assert.AreEqual($"[ERROR] {problem}", driver.Wizard.Notice.Message);
        }
    }

    private static WizardDriver ToReview(WizardHost host)
    {
        var driver = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog]);
        driver.NextTo<OwnerPage>();
        driver.Press(Key.Enter);
        Assert.IsInstanceOfType<ReviewPage>(driver.Page);
        return driver;
    }

    private static InstallRequest Request(WizardHost host, string[] arguments)
        => host.Services.Parse(arguments) is InstallDeploymentCommand install
            ? install.Request
            : throw new AssertFailedException("The arguments make an install.");

    private sealed class AnswersFile : IDisposable
    {
        public AnswersFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-wizard-answers-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, JsonDocument.Parse(content).RootElement.GetRawText());
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
