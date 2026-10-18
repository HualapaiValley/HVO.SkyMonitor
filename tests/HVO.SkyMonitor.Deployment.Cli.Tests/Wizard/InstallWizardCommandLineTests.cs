using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Wizard;
using Terminal.Gui.Input;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;

/// <summary>
/// The wizard and the command line it was given (PR-1371-R1 F1, F2). The command line is checked exactly as the direct
/// path checks it before any page is passed over, so what the direct path refuses exits 2 without the wizard opening; an
/// explicit value reaches the install unchanged; and the answers the Review page checked are the answers applied,
/// whether a value was restored from the saved answers or given again.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class InstallWizardCommandLineTests
{
    private const string Release = "/srv/hvo/release";
    private const string Catalog = "/srv/hvo/catalog.bundle";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(new[] { "--password-file", "" })]
    [DataRow(new[] { "--password-file", "/srv/hvo/password", "--generate-password" })]
    [DataRow(new[] { "--owner-email", "" })]
    [DataRow(new[] { "--owner-email", "not-an-address" })]
    [DataRow(new[] { "--memory", "" })]
    [DataRow(new[] { "--memory", "3g" })]
    [DataRow(new[] { "--cpus", "1" })]
    [DataRow(new[] { "--bind-address", "" })]
    [DataRow(new[] { "--port", "0" })]
    [DataRow(new[] { "--latitude", "91" })]
    [DataRow(new[] { "--product-root", "" })]
    [DataRow(new[] { "--image-archive", "/srv/hvo/image.tar" })]
    [DataRow(new[] { "--friendly-name", "" })]
    public async Task AnExplicitOptionTheDirectPathRefuses_ExitsTwoWithoutTheWizard(string[] given)
    {
        ArgumentNullException.ThrowIfNull(given);
        using var host = new WizardHost();
        string[] withRequired = ["cameraagent", "install", .. given, "--release", Release, "--catalog-bundle", Catalog];
        string[] direct = given.Contains("--friendly-name") ? withRequired : [.. withRequired, "--friendly-name", "North"];
        var interactive = InstallWizardRunnerTests.Interactive(host, _ => throw new AssertFailedException("The wizard must not open."));
        var (directExitCode, _, directError) = await InstallWizardRunnerTests.RunAsync(host, direct, interactive);

        var (exitCode, _, error) = await InstallWizardRunnerTests.RunAsync(host, withRequired, interactive);

        Assert.AreEqual((int)InstallerExitCode.Usage, directExitCode, directError);
        Assert.AreEqual((int)InstallerExitCode.Usage, exitCode, error);
        Assert.AreEqual(directError, error, "the wizard's command line is refused with the direct path's words");
        Assert.IsEmpty(host.Requests);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void AnAddressOtherComputersReach_IsAQuestionTheWizardAsks_NotAMistake()
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--bind-address", "0.0.0.0", "--release", Release, "--catalog-bundle", Catalog]);

        Assert.IsTrue(driver.PageOf<NetworkPage>().Applies, "it asks whether plain HTTP is understood");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EveryExplicitValue_ReachesTheInstallUnchanged()
    {
        using var host = new WizardHost();
        string[] given =
        [
            "cameraagent", "install", "--instance-id", WizardHost.InstanceId.ToString("D"), "--owner-email", "admin@example.test",
            "--bind-address", "127.0.0.1", "--port", "5140", "--acknowledge-plaintext-http", "--latitude", "35.2",
            "--longitude", "-114.1", "--elevation", "1300", "--time-zone", "UTC", "--cpus", "3", "--memory", "6g",
            "--replay-profile", "local-runner", "--password-file", "/srv/hvo/password", "--release", Release,
            "--image-source", "registry", "--catalog-bundle", Catalog, "--no-download"
        ];

        var (exitCode, _, error) = await InstallWizardRunnerTests.RunAsync(host, given, driver =>
        {
            driver.Replace("North");
            driver.NextTo<ReviewPage>();
            driver.WaitIdle("the plan check");
            driver.Press(Key.Enter);
        });

        Assert.AreEqual(0, exitCode, error);
        var direct = Request(host, [.. given, "--friendly-name", "North"]);
        Assert.AreEqual(direct, host.Installs.Single());
        Assert.AreEqual(direct with { DryRun = true, Json = true }, host.Requests.Single(request => request.DryRun));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(null, "Registry", DisplayName = "restored registry source, nothing given")]
    [DataRow("archive", "Archive", DisplayName = "restored registry source, archive given")]
    [DataRow("registry", "Registry", DisplayName = "restored registry source, registry given")]
    public async Task TheReviewedAnswers_AreTheAppliedAnswers(string? givenSource, string expected)
    {
        using var host = new WizardHost();
        Directory.CreateDirectory(Path.GetDirectoryName(host.AnswersPath)!);
        await File.WriteAllTextAsync(host.AnswersPath, $$"""{"schemaVersion":1,"instanceId":"{{WizardHost.InstanceId:D}}","friendlyName":"Saved Camera","release":"/srv/hvo/older-release","imageSource":"Registry","catalogBundle":"/srv/hvo/older.bundle"}""");
        string[] args = ["cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog, .. givenSource is null ? Array.Empty<string>() : ["--image-source", givenSource]];

        var (exitCode, _, error) = await InstallWizardRunnerTests.RunAsync(host, args, driver =>
        {
            Assert.AreEqual("Saved Camera", driver.PageOf<CameraPage>().NameField!.Text);
            driver.NextTo<ReviewPage>();
            driver.WaitIdle("the plan check");
            driver.Press(Key.Enter);
        });

        Assert.AreEqual(0, exitCode, error);
        var reviewed = host.Requests.Single(request => request.DryRun);
        var applied = host.Installs.Single();
        Assert.AreEqual(reviewed with { DryRun = false, Json = false }, applied, "the Review page checked what was installed");
        Assert.AreEqual(Enum.Parse<ReleaseImageSource>(expected), applied.ImageSource);
        Assert.AreEqual(Release, applied.Release, "the release given replaces the saved one");
        Assert.AreEqual(Catalog, applied.CatalogBundle);
    }

    private static InstallRequest Request(WizardHost host, string[] arguments)
        => host.Services.Parse(arguments) is InstallDeploymentCommand install
            ? install.Request
            : throw new AssertFailedException("The arguments make an install.");
}
