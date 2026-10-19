using System.Net;
using System.Runtime.Versioning;
using HVO.SkyMonitor.CameraAgent.Cli;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary>
/// Every command's exit codes (docs/runbooks/hvo-sky.md): 0, 1, 2, 3 and 130, each with its installer meaning, on every
/// command it applies to. <c>logout</c> sends nothing and waits for nothing, so it has no 1 or 130; <c>status --watch</c>
/// ends on a signal by design, so a signal gives 0 or 1 rather than 130.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[UnsupportedOSPlatform("windows")]
public sealed class SkyCliExitCodeTests
{
    private const string SettingsFile = """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500}}""";

    /// <summary>Every command; each test below lists, as data rows, the ones its exit code applies to.</summary>
    internal static readonly string[] AllCommands =
    [
        "status", "status --watch", "info", "config show", "config get", "config set", "config diff", "config apply",
        "restart", "login", "logout", "rig show", "rig stage", "rig apply", "schedule show", "schedule stage", "schedule apply"
    ];

    [TestMethod]
    public void ExitCodes_ArePinnedToTheInstallersMeanings()
    {
        Assert.AreEqual(0, (int)SkyExitCode.Success);
        Assert.AreEqual(1, (int)SkyExitCode.Failed);
        Assert.AreEqual(2, (int)SkyExitCode.Usage);
        Assert.AreEqual(3, (int)SkyExitCode.Refused);
        Assert.AreEqual(130, (int)SkyExitCode.Interrupted);
        Assert.HasCount(5, Enum.GetValues<SkyExitCode>());
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("status --watch")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("logout")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task Success_ExitsZeroAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        CliAssert.KeyNeverLeaks(result, harness.Api, CliHarness.Key);
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("status --watch")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task CameraAgentUnreachable_ExitsOneAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);
        harness.Api.Unreachable = true;

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.All, "could not be reached", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("status --watch")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task ServerError_ExitsOneAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);
        harness.Api.RefuseAll = HttpStatusCode.InternalServerError;

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("status --watch")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("logout")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task UnknownOption_ExitsTwoAndSendsNothingAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);

        var result = await harness.RunAsync(stop.Token, [.. args, "--bogus"]).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        Assert.IsEmpty(harness.Api.Requests);
        StringAssert.Contains(result.Error, "--help", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("status --watch")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("logout")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task Refused_ExitsThreeAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);
        if (command == "logout")
        {
            File.SetUnixFileMode(harness.CredentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        else
        {
            harness.Api.RefuseAll = HttpStatusCode.Unauthorized;
        }

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method is "PUT" or "POST"), "A refusal changes nothing.");
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("status --watch")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("logout")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task NotConfigured_ExitsThreeAndSendsNothingAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness(signedIn: false);
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);
        if (command == "logout")
        {
            // logout has nothing to refuse when nothing is saved: it says so and succeeds.
            var nothing = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);
            Assert.AreEqual(0, nothing.ExitCode, nothing.ToString());
            StringAssert.Contains(nothing.Out, "No API key is saved.", StringComparison.Ordinal);
            return;
        }

        if (command == "login")
        {
            // login needs an address: without --url, the environment or the file, and without a terminal, no one can give one.
            var noAddress = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);
            Assert.AreEqual(2, noAddress.ExitCode, noAddress.ToString());
            Assert.IsEmpty(harness.Api.Requests);
            return;
        }

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.IsEmpty(harness.Api.Requests);
        StringAssert.Contains(result.Error, "hvo-sky login", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("info")]
    [DataRow("config show")]
    [DataRow("config get")]
    [DataRow("config set")]
    [DataRow("config diff")]
    [DataRow("config apply")]
    [DataRow("restart")]
    [DataRow("login")]
    [DataRow("rig show")]
    [DataRow("rig stage")]
    [DataRow("rig apply")]
    [DataRow("schedule show")]
    [DataRow("schedule stage")]
    [DataRow("schedule apply")]
    public async Task Interrupted_Exits130Async(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = Prepare(command, harness, stop);
        harness.Api.Hang = true;
        harness.Api.OnRequest = _ => stop.Cancel();

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(130, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Interrupted", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("status")]
    [DataRow("config set")]
    [DataRow("rig stage")]
    [DataRow("schedule apply")]
    [DataRow("restart")]
    public async Task Json_FailuresWriteOneErrorDocumentWithTheExitCodeAndKindAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var (status, code, kind) in new[]
        {
            ((HttpStatusCode?)HttpStatusCode.Unauthorized, 3, "SignedOut"),
            (HttpStatusCode.Forbidden, 3, "Forbidden"),
            (HttpStatusCode.Conflict, 3, "Refused"),
            (HttpStatusCode.ServiceUnavailable, 1, "Failed"),
            (null, 1, "Unreachable")
        })
        {
            using var harness = new CliHarness();
            using var stop = new CancellationTokenSource();
            var args = Prepare(command, harness, stop);
            harness.Api.RefuseAll = status;
            harness.Api.Unreachable = status is null;

            var result = await harness.RunAsync(stop.Token, [.. args, "--json"]).ConfigureAwait(false);

            Assert.AreEqual(code, result.ExitCode, result.ToString());
            var error = result.Json().GetProperty("error");
            Assert.AreEqual(code, error.GetProperty("exitCode").GetInt32());
            Assert.AreEqual(kind, error.GetProperty("kind").GetString());
            Assert.AreEqual(string.Empty, result.Error, "With --json, a failure is the document on standard output alone.");
        }
    }

    /// <summary>The arguments of a command that succeeds against the default fake, with the fake and files it needs.</summary>
    internal static string[] Prepare(string command, CliHarness harness, CancellationTokenSource stop)
    {
        switch (command)
        {
            case "status --watch":
                // The watch ends as Ctrl+C would end it, after its second read.
                harness.Api.OnRequest = request =>
                {
                    if (harness.Api.RequestsTo("GET", Fake.SummaryRoute).Count >= 2)
                    {
                        stop.Cancel();
                    }
                };
                return ["status", "--watch"];
            case "config get":
                return ["config", "get", "skyMap.maximumObjects"];
            case "config set":
                return ["config", "set", "skyMap.maximumObjects=500"];
            case "config diff":
                return ["config", "diff", harness.WriteFile("settings.json", SettingsFile)];
            case "config apply":
                return ["config", "apply", harness.WriteFile("settings.json", SettingsFile)];
            case "restart":
                return ["restart", "--force"];
            case "login":
                harness.Input.Enqueue(CliHarness.Key);
                return ["login"];
            case "rig stage":
                return ["rig", "stage", "rig-2", "--acknowledge-unvalidated"];
            case "rig apply":
                harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
                    harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0
                        ? Fake.Rigs(active: "rig-2", pending: null)
                        : Fake.Rigs(active: "rig-1", pending: "rig-2")));
                return ["rig", "apply", "--force"];
            case "schedule stage":
                return ["schedule", "stage", harness.WriteFile("profile.json", """{"schemaVersion": "2", "name": "winter"}""")];
            case "schedule apply":
                return ["schedule", "apply", "schedule-2"];
            default:
                return command.Split(' ');
        }
    }
}
