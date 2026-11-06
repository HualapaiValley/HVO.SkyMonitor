using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Cli;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary>
/// Criterion 2 of #1317: credentials stored <c>0600</c> in a <c>0700</c> folder, overridable by environment variables,
/// never accepted on the command line, and never printed, logged or written to <c>--json</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[UnsupportedOSPlatform("windows")]
public sealed class SkyCliCredentialTests
{
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [TestMethod]
    public async Task Login_SavesTheAddressAndKey0600InA0700FolderAfterCameraAgentAcceptsItAsync()
    {
        using var harness = new CliHarness(signedIn: false);
        harness.Input.Enqueue(CliHarness.Key);

        var result = await harness.RunAsync("login", "--url", CliHarness.Url).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.AreEqual(FileMode, File.GetUnixFileMode(harness.CredentialsPath));
        Assert.AreEqual(DirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(harness.CredentialsPath)!));
        var saved = SkyCredentialStore.Load(harness.CredentialsPath)!;
        Assert.AreEqual(new Uri(CliHarness.Url), saved.Url);
        Assert.AreEqual(CliHarness.Key, saved.ApiKey);
        StringAssert.Contains(result.Out, $"with API key {CliHarness.KeyId} (read-write)", StringComparison.Ordinal);
        Assert.AreEqual(("API key for " + CliHarness.Url + ": ", true), harness.Prompts.Single());
        CollectionAssert.AreEqual(new[] { "GET " + Fake.InfoRoute }, harness.Api.Requests.Select(static r => $"{r.Method} {r.PathAndQuery}").ToArray());
        CliAssert.KeyNeverLeaks(result, harness.Api, CliHarness.Key);
    }

    [TestMethod]
    public async Task Login_ReplacesAnExistingFileStill0600AndKeepsItsDirectory0700Async()
    {
        using var harness = new CliHarness();
        var other = CliHarness.MakeKey("fedcba9876543210", 'z');
        harness.Input.Enqueue(other);

        var result = await harness.RunAsync("login", "--json").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.AreEqual(FileMode, File.GetUnixFileMode(harness.CredentialsPath));
        Assert.AreEqual(other, SkyCredentialStore.Load(harness.CredentialsPath)!.ApiKey);
        var json = result.Json();
        Assert.AreEqual("fedcba9876543210", json.GetProperty("keyId").GetString());
        Assert.AreEqual("ReadWrite", json.GetProperty("access").GetString());
        Assert.IsTrue(json.GetProperty("signedIn").GetBoolean());
        CliAssert.KeyNeverLeaks(result, harness.Api, other);
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(harness.CredentialsPath)!, "*.tmp"));
    }

    [TestMethod]
    public async Task Login_AKeyCameraAgentRefuses_IsNotSavedAndExitsThreeAsync()
    {
        using var harness = new CliHarness(signedIn: false);
        harness.Api.RefuseAll = HttpStatusCode.Unauthorized;
        harness.Input.Enqueue(CliHarness.Key);

        var result = await harness.RunAsync("login", "--url", CliHarness.Url).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.IsFalse(File.Exists(harness.CredentialsPath));
        StringAssert.Contains(result.Error, "did not accept the API key", StringComparison.Ordinal);
        CliAssert.KeyNeverLeaks(result, harness.Api, CliHarness.Key);
    }

    [TestMethod]
    [DataRow("not-a-key")]
    [DataRow("hvo_sky_0123456789abcdef_short")]
    [DataRow("")]
    public async Task Login_AValueThatIsNotAKey_IsAUsageErrorThatNeverEchoesItAsync(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        using var harness = new CliHarness(signedIn: false);
        harness.Input.Enqueue(typed);

        var result = await harness.RunAsync("login", "--url", CliHarness.Url).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        Assert.IsEmpty(harness.Api.Requests);
        Assert.IsFalse(File.Exists(harness.CredentialsPath));
        if (typed.Length > 0)
        {
            Assert.DoesNotContain(typed, result.All, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Login_AtATerminal_OffersTheLocalAddressAndReadsTheKeyWithoutEchoAsync()
    {
        using var harness = new CliHarness(signedIn: false) { Interactive = true };
        harness.Input.Enqueue(string.Empty);
        harness.Input.Enqueue(CliHarness.Key);

        var result = await harness.RunAsync("login").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.AreEqual(($"CameraAgent address [{SkyCli.DefaultUrl}]: ", false), harness.Prompts[0]);
        Assert.IsTrue(harness.Prompts[1].Secret, "The key is read without echo.");
        Assert.AreEqual(new Uri(SkyCli.DefaultUrl), SkyCredentialStore.Load(harness.CredentialsPath)!.Url);
    }

    [TestMethod]
    public async Task Login_WhileTheEnvironmentSetsAKey_SaysThatItTakesPrecedenceAsync()
    {
        using var harness = new CliHarness(signedIn: false);
        harness.Environment[SkyCredentialStore.ApiKeyVariable] = CliHarness.MakeKey("1111111111111111", 'e');
        harness.Input.Enqueue(CliHarness.Key);

        var result = await harness.RunAsync("login", "--url", CliHarness.Url).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "HVO_SKY_API_KEY is set, and takes precedence", StringComparison.Ordinal);
        Assert.AreEqual("Bearer " + CliHarness.Key, harness.Api.Requests.Single().Authorization, "login checks the key it saves.");
    }

    [TestMethod]
    public async Task Logout_RemovesTheKeyKeepsTheAddressAndStays0600Async()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("logout").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var saved = SkyCredentialStore.Load(harness.CredentialsPath)!;
        Assert.IsNull(saved.ApiKey);
        Assert.AreEqual(new Uri(CliHarness.Url), saved.Url);
        Assert.AreEqual(FileMode, File.GetUnixFileMode(harness.CredentialsPath));
        StringAssert.Contains(result.Out, $"Removed API key {CliHarness.KeyId}", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "until the site owner revokes it", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
        CliAssert.KeyNeverLeaks(result, harness.Api, CliHarness.Key);

        var again = await harness.RunAsync("logout", "--json").ConfigureAwait(false);
        Assert.AreEqual(0, again.ExitCode);
        Assert.IsFalse(again.Json().GetProperty("signedOut").GetBoolean());
    }

    [TestMethod]
    public async Task ACredentialsFileOthersCanRead_IsRefusedWithExitThreeBeforeAnythingIsSentAsync()
    {
        using var harness = new CliHarness();
        File.SetUnixFileMode(harness.CredentialsPath, FileMode | UnixFileMode.OtherRead);

        var result = await harness.RunAsync("status").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "can be read or changed by other users", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "chmod 600", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task ACredentialsFolderOthersCanChange_IsRefusedWithExitThreeAsync()
    {
        using var harness = new CliHarness();
        File.SetUnixFileMode(Path.GetDirectoryName(harness.CredentialsPath)!, DirectoryMode | UnixFileMode.GroupWrite);

        var status = await harness.RunAsync("status").ConfigureAwait(false);
        harness.Input.Enqueue(CliHarness.Key);
        var login = await harness.RunAsync("login").ConfigureAwait(false);

        Assert.AreEqual(3, status.ExitCode, status.ToString());
        Assert.AreEqual(3, login.ExitCode, login.ToString());
        StringAssert.Contains(status.Error, "chmod 700", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
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
    public async Task AnInaccessibleCredentialsFolder_IsUnusableCredentialsWithExitThreeOnEveryCommandAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireModesEnforced();
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = SkyCliExitCodeTests.Prepare(command, harness, stop);
        var folder = Path.GetDirectoryName(harness.CredentialsPath)!;
        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            var result = await harness.RunAsync(stop.Token, [.. args, "--json"]).ConfigureAwait(false);

            AssertUnusable(result, harness, "a folder on its path cannot be searched");
        }
        finally
        {
            File.SetUnixFileMode(folder, DirectoryMode);
        }
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
    public async Task AnInaccessibleCredentialsFile_IsUnusableCredentialsWithExitThreeOnEveryCommandAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireModesEnforced();
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = SkyCliExitCodeTests.Prepare(command, harness, stop);
        File.SetUnixFileMode(harness.CredentialsPath, UnixFileMode.UserWrite);

        var result = await harness.RunAsync(stop.Token, [.. args, "--json"]).ConfigureAwait(false);

        AssertUnusable(result, harness, "cannot be read");
    }

    [TestMethod]
    public async Task ACredentialsFolderThatIsAFile_IsUnusableCredentialsRatherThanAbsentAsync()
    {
        using var harness = new CliHarness();
        var notAFolder = harness.WriteFile("not-a-folder", "x");

        var result = await harness.RunAsync("status", "--json", "--credentials-file", Path.Combine(notAFolder, "credentials.json")).ConfigureAwait(false);

        AssertUnusable(result, harness, "cannot be reached");
    }

    [TestMethod]
    [DataRow("folder")]
    [DataRow("file")]
    public async Task Logout_UnusableCredentials_ExitsThreeAndSaysTheKeyCouldNotBeRemovedAsync(string which)
    {
        ArgumentNullException.ThrowIfNull(which);
        RequireModesEnforced();
        using var harness = new CliHarness();
        var folder = Path.GetDirectoryName(harness.CredentialsPath)!;
        File.SetUnixFileMode(which == "folder" ? folder : harness.CredentialsPath, which == "folder" ? UnixFileMode.None : UnixFileMode.UserWrite);
        try
        {
            var result = await harness.RunAsync("logout").ConfigureAwait(false);

            Assert.AreEqual(3, result.ExitCode, result.ToString());
            StringAssert.StartsWith(result.Error, "The API key could not be removed: ", StringComparison.Ordinal);
            Assert.DoesNotContain("No API key is saved", result.All, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(folder, DirectoryMode);
            File.SetUnixFileMode(harness.CredentialsPath, FileMode);
        }

        Assert.AreEqual(CliHarness.Key, SkyCredentialStore.Load(harness.CredentialsPath)!.ApiKey, "Nothing was removed.");
    }

    [TestMethod]
    public async Task ACredentialsFileOfAnotherUser_IsUnusableCredentialsWithExitThreeAsync()
    {
        using var harness = new CliHarness { UserId = SkyFileSystem.CurrentUserId + 1 };

        var status = await harness.RunAsync("status", "--json").ConfigureAwait(false);
        var logout = await harness.RunAsync("logout").ConfigureAwait(false);

        AssertUnusable(status, harness, $"The credentials file {harness.CredentialsPath} cannot be used: it belongs to another user (user ID {SkyFileSystem.CurrentUserId})");
        Assert.AreEqual(3, logout.ExitCode, logout.ToString());
        StringAssert.StartsWith(logout.Error, "The API key could not be removed: ", StringComparison.Ordinal);
        Assert.AreEqual(CliHarness.Key, SkyCredentialStore.Load(harness.CredentialsPath)!.ApiKey, "Nothing was removed.");
    }

    [TestMethod]
    public async Task ACredentialsFolderOfAnotherUser_IsNotWrittenToAsync()
    {
        using var harness = new CliHarness { UserId = SkyFileSystem.CurrentUserId + 1 };
        File.Delete(harness.CredentialsPath);
        harness.Input.Enqueue(CliHarness.Key);

        var result = await harness.RunAsync("login", "--url", CliHarness.Url).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "CameraAgent accepted the key, but it was not saved", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, $"The credentials folder {Path.GetDirectoryName(harness.CredentialsPath)} cannot be used: it belongs to another user", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(harness.CredentialsPath));
    }

    [TestMethod]
    public void Store_TakesOnlyAMissingFileForAbsent()
    {
        using var harness = new CliHarness(signedIn: false);

        Assert.IsNull(SkyCredentialStore.Load(harness.CredentialsPath), "No folder: absent.");
        Directory.CreateDirectory(Path.GetDirectoryName(harness.CredentialsPath)!);
        Assert.IsNull(SkyCredentialStore.Load(harness.CredentialsPath), "A folder with no file: absent.");
        Directory.CreateDirectory(harness.CredentialsPath);
        Assert.ThrowsExactly<SkyCredentialFileException>(() => SkyCredentialStore.Load(harness.CredentialsPath), "A folder where the file should be is not absent.");
    }

    private static void RequireModesEnforced()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs a non-root user, for whom modes 0000 and 0200 are enforced.");
        }
    }

    private static void AssertUnusable(CliResult result, CliHarness harness, string problem)
    {
        Assert.AreEqual(3, result.ExitCode, result.ToString());
        var error = result.Json().GetProperty("error");
        Assert.AreEqual("UnusableCredentials", error.GetProperty("kind").GetString(), result.ToString());
        var message = error.GetProperty("message").GetString()!;
        StringAssert.Contains(message, "cannot be used", StringComparison.Ordinal);
        StringAssert.Contains(message, problem, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests, "Nothing was sent.");
    }

    [TestMethod]
    public async Task TheEnvironment_OverridesTheFilesAddressAndKeyAndUrlOverridesTheEnvironmentAddressAsync()
    {
        using var harness = new CliHarness();
        var environmentKey = CliHarness.MakeKey("2222222222222222", 'v');
        harness.Environment[SkyCredentialStore.UrlVariable] = "http://localhost:5131";
        harness.Environment[SkyCredentialStore.ApiKeyVariable] = environmentKey;

        var fromEnvironment = await harness.RunAsync("info", "--json").ConfigureAwait(false);
        var fromOption = await harness.RunAsync("info", "--json", "--url", "http://127.0.0.1:5199/").ConfigureAwait(false);

        Assert.AreEqual(0, fromEnvironment.ExitCode, fromEnvironment.ToString());
        Assert.AreEqual("http://localhost:5131/", fromEnvironment.Json().GetProperty("url").GetString());
        Assert.AreEqual("2222222222222222", fromEnvironment.Json().GetProperty("credential").GetProperty("keyId").GetString());
        StringAssert.Contains(fromEnvironment.Json().GetProperty("credential").GetProperty("source").GetString(), "HVO_SKY_API_KEY", StringComparison.Ordinal);
        Assert.AreEqual("http://127.0.0.1:5199/", fromOption.Json().GetProperty("url").GetString());
        Assert.IsTrue(harness.Api.Requests.All(request => request.Authorization == "Bearer " + environmentKey));
        CliAssert.KeyNeverLeaks(fromEnvironment, harness.Api, environmentKey);
    }

    [TestMethod]
    public async Task TheEnvironmentAlone_IsEnoughWithNoCredentialsFileAsync()
    {
        using var harness = new CliHarness(signedIn: false);
        harness.Environment[SkyCredentialStore.UrlVariable] = CliHarness.Url;
        harness.Environment[SkyCredentialStore.ApiKeyVariable] = CliHarness.Key;

        var result = await harness.RunAsync("status").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.IsFalse(File.Exists(harness.CredentialsPath));
    }

    [TestMethod]
    [DataRow(SkyCredentialStore.ApiKeyVariable, "hvo_sky_notakey")]
    [DataRow(SkyCredentialStore.UrlVariable, "ftp://camera")]
    public async Task AnEnvironmentValueThatCannotBeUsed_ExitsThreeWithoutEchoingAKeyAsync(string name, string value)
    {
        using var harness = new CliHarness();
        harness.Environment[name] = value;

        var result = await harness.RunAsync("status").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, name, StringComparison.Ordinal);
        if (name == SkyCredentialStore.ApiKeyVariable)
        {
            Assert.DoesNotContain(value, result.All, StringComparison.Ordinal);
        }

        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    [DataRow("status {key}")]
    [DataRow("login {key}")]
    [DataRow("config set site.observatoryName={key}")]
    [DataRow("--url http://127.0.0.1:5130/?k={key} status")]
    public async Task AKeyAnywhereOnTheCommandLine_IsRefusedWithoutEchoAndWithoutSendingAsync(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        using var harness = new CliHarness();
        var args = commandLine.Replace("{key}", CliHarness.Key, StringComparison.Ordinal).Split(' ');

        var result = await harness.RunAsync(args).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "never takes one there", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "revoke it", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
        CliAssert.KeyNeverLeaks(result, harness.Api, CliHarness.Key);
    }

    [TestMethod]
    [DataRow("login --api-key s3cret-value")]
    [DataRow("status --password=s3cret-value")]
    [DataRow("login --token s3cret-value")]
    [DataRow("info --secret s3cret-value")]
    [DataRow("logout --key=s3cret-value")]
    public async Task ACredentialOption_DoesNotExistAndItsValueIsNeverEchoedAsync(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        using var harness = new CliHarness();

        var result = await harness.RunAsync(commandLine.Split(' ')).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "never takes a credential on the command line", StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-value", result.All, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public void NoCommand_HasAnOptionThatCouldCarryACredential()
    {
        var host = new SkyCliHost { Out = TextWriter.Null, Error = TextWriter.Null, ReadLine = static (_, _) => null };
        var names = Walk(SkyCli.CreateRootCommand(host))
            .SelectMany(static command => command.Options)
            .SelectMany(static option => option.Aliases.Prepend(option.Name))
            .ToArray();

        foreach (var forbidden in new[] { "key", "api-key", "apikey", "password", "passwd", "token", "secret", "session" })
        {
            Assert.IsFalse(names.Any(name => name.TrimStart('-').Equals(forbidden, StringComparison.OrdinalIgnoreCase)), forbidden);
        }

        static IEnumerable<System.CommandLine.Command> Walk(System.CommandLine.Command command)
            => command.Subcommands.SelectMany(Walk).Prepend(command);
    }

    [TestMethod]
    public async Task AcrossEveryCommand_TheKeyIsSentOnlyAsABearerHeaderAndNeverPrintedAsync()
    {
        foreach (var command in SkyCliExitCodeTests.AllCommands)
        {
            foreach (var json in new[] { false, true })
            {
                using var harness = new CliHarness();
                using var stop = new CancellationTokenSource();
                var args = SkyCliExitCodeTests.Prepare(command, harness, stop);

                var result = await harness.RunAsync(stop.Token, json ? [.. args, "--json"] : args).ConfigureAwait(false);

                Assert.AreEqual(0, result.ExitCode, $"{command} {json}: {result}");
                CliAssert.KeyNeverLeaks(result, harness.Api, CliHarness.Key);
                Assert.IsTrue(harness.Api.Requests.All(static request => request.UserAgent?.StartsWith("hvo-sky/0.9.0-test", StringComparison.Ordinal) == true));
            }
        }
    }

    [TestMethod]
    public async Task ARedirect_IsNotFollowedSoTheKeyGoesNowhereElseAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.SummaryRoute, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://elsewhere.example/api/v1/operations/summary");
            return response;
        });

        var result = await harness.RunAsync("status").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "redirect", StringComparison.Ordinal);
        Assert.HasCount(1, harness.Api.Requests);
    }

    [TestMethod]
    public async Task PlainHttpToAnotherMachine_WarnsThatTheKeyCrossesTheNetworkUnencryptedAsync()
    {
        using var harness = new CliHarness();

        var remote = await harness.RunAsync("status", "--url", "http://192.0.2.10:5130/").ConfigureAwait(false);
        var local = await harness.RunAsync("status").ConfigureAwait(false);
        var tls = await harness.RunAsync("status", "--url", "https://allsky.example/").ConfigureAwait(false);

        StringAssert.Contains(remote.Error, "unencrypted", StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted", local.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted", tls.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An address bound to one of this machine's interfaces is this machine: a request to it never crosses the network,
    /// so <c>hvo-sky</c> run on the CameraAgent host against its own LAN address does not warn. Any other address, and a
    /// name, still does (#1384).
    /// </summary>
    [TestMethod]
    public async Task PlainHttpToAnAddressOfThisMachine_DoesNotWarnAsync()
    {
        using var harness = new CliHarness();
        harness.LocalAddresses = [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")];

        var lan = await harness.RunAsync("status", "--url", "http://192.0.2.10:5130/").ConfigureAwait(false);
        var lan6 = await harness.RunAsync("status", "--url", "http://[2001:db8::10]:5130/").ConfigureAwait(false);
        var other = await harness.RunAsync("status", "--url", "http://192.0.2.11:5130/").ConfigureAwait(false);
        var name = await harness.RunAsync("status", "--url", "http://allsky.example:5130/").ConfigureAwait(false);

        Assert.DoesNotContain("unencrypted", lan.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted", lan6.Error, StringComparison.Ordinal);
        StringAssert.Contains(other.Error, "unencrypted", StringComparison.Ordinal);
        StringAssert.Contains(name.Error, "unencrypted", StringComparison.Ordinal);
    }

    /// <summary>
    /// Through a proxy, a request to this machine's own address leaves it, Authorization header and all (PR-1390-R1 F1):
    /// such an address is this machine only when the proxy bypasses it, as <c>no_proxy</c> does. With no proxy it is this
    /// machine, and a name is warned about whatever the proxy.
    /// </summary>
    [TestMethod]
    public async Task PlainHttpToAnAddressOfThisMachine_ThroughAProxy_WarnsUnlessTheProxyBypassesItAsync()
    {
        using var harness = new CliHarness();
        harness.LocalAddresses = [IPAddress.Parse("192.0.2.10")];
        const string local = "http://192.0.2.10:5130/";

        harness.Proxy = new EnvironmentLikeProxy(new Uri("http://192.0.2.99:8080/"));
        var proxied = await harness.RunAsync("status", "--url", local).ConfigureAwait(false);
        harness.Proxy = new EnvironmentLikeProxy(new Uri("http://192.0.2.99:8080/"), "192.0.2.10");
        var bypassed = await harness.RunAsync("status", "--url", local).ConfigureAwait(false);
        var named = await harness.RunAsync("status", "--url", "http://allsky.example:5130/").ConfigureAwait(false);
        harness.Proxy = null;
        var direct = await harness.RunAsync("status", "--url", local).ConfigureAwait(false);

        StringAssert.Contains(proxied.Error, "unencrypted", StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted", bypassed.Error, StringComparison.Ordinal);
        StringAssert.Contains(named.Error, "unencrypted", StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted", direct.Error, StringComparison.Ordinal);
    }

    /// <summary>hvo-sky's own connections go through <see cref="HttpClient.DefaultProxy"/>, so the warning asks it.</summary>
    [TestMethod]
    public void Host_TheProxyTheWarningAsks_IsTheOneTheConnectionUses()
    {
        var host = SkyCliHost.System();

        Assert.AreSame(HttpClient.DefaultProxy, host.Proxy());
        Assert.IsNull(host.CreateHandler, "the connection's handler is the default one, which uses HttpClient.DefaultProxy");
    }

    /// <summary>
    /// A loopback address is this machine, and the connection reaches it directly whatever the proxy. An environment proxy
    /// does not bypass loopback, so with <c>http_proxy</c> and no matching <c>no_proxy</c> the key reached the proxy in
    /// clear, unwarned, and the request went to the proxy's own loopback rather than to this machine (#1404). With a
    /// bypass it went direct already.
    /// </summary>
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("localhost")]
    public async Task Loopback_ThroughAnEnvironmentProxy_GoesDirectAndTheKeyNeverReachesTheProxyAsync(string host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var agent = new RecordingHttpServer();
        await using var agentLifetime = agent.ConfigureAwait(false);
        var proxy = new RecordingHttpServer();
        await using var proxyLifetime = proxy.ConfigureAwait(false);
        var address = new Uri($"http://{host}:{agent.Port}/");

        await GetInfoAsync(address, new EnvironmentLikeProxy(proxy.Address), timeout.Token).ConfigureAwait(false);
        await GetInfoAsync(address, new EnvironmentLikeProxy(proxy.Address, host), timeout.Token).ConfigureAwait(false);

        Assert.IsEmpty(proxy.Requests, "the key never reaches the proxy");
        Assert.HasCount(2, agent.Requests);
        foreach (var request in agent.Requests)
        {
            StringAssert.StartsWith(request, "GET /api/v1/operations/info HTTP/1.1", StringComparison.Ordinal);
            StringAssert.Contains(request, $"Authorization: Bearer {CliHarness.Key}", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Any other address still goes through the proxy, as the environment configures it, and is warned about there
    /// (#1384); only loopback goes direct.
    /// </summary>
    [TestMethod]
    public async Task AnotherAddress_ThroughAnEnvironmentProxy_StillGoesThroughItAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var proxy = new RecordingHttpServer();
        await using var proxyLifetime = proxy.ConfigureAwait(false);

        await GetInfoAsync(new Uri("http://192.0.2.10:5130/"), new EnvironmentLikeProxy(proxy.Address), timeout.Token)
            .ConfigureAwait(false);

        var request = Assert.ContainsSingle(proxy.Requests);
        StringAssert.StartsWith(request, "GET http://192.0.2.10:5130/api/v1/operations/info HTTP/1.1", StringComparison.Ordinal);
    }

    /// <summary>
    /// The plain-HTTP warning never names loopback, which is right only because the connection's own handler, the one
    /// <see cref="Host_TheProxyTheWarningAsks_IsTheOneTheConnectionUses"/> pins, uses no proxy for it, in every form
    /// (#1404). Every other address uses the proxy the environment gives.
    /// </summary>
    [TestMethod]
    public async Task PlainHttpToLoopback_ThroughAProxy_IsNotWarnedAboutBecauseTheConnectionGoesDirectAsync()
    {
        using var harness = new CliHarness();
        harness.Proxy = new EnvironmentLikeProxy(new Uri("http://192.0.2.99:8080/"));
        string[] loopback = ["http://127.0.0.1:5130/", "http://localhost:5130/", "http://[::1]:5130/"];
        string[] other = ["http://192.0.2.10:5130/", "http://[2001:db8::10]:5130/", "http://allsky.example:5130/", "https://allsky.example/"];

        foreach (var url in loopback)
        {
            var result = await harness.RunAsync("status", "--url", url).ConfigureAwait(false);
            Assert.DoesNotContain("unencrypted", result.Error, StringComparison.Ordinal);
            using var handler = SkyApiClient.CreateHandler(new Uri(url));
            Assert.IsFalse(handler.UseProxy, url);
        }

        foreach (var url in other)
        {
            using var handler = SkyApiClient.CreateHandler(new Uri(url));
            Assert.IsTrue(handler.UseProxy, url);
        }
    }

    /// <summary>
    /// Reads <c>info</c> from <paramref name="address"/> with the key, on the connection's own handler, through
    /// <paramref name="proxy"/>.
    /// </summary>
    private static async Task GetInfoAsync(Uri address, IWebProxy proxy, CancellationToken cancellationToken)
    {
        using var client = new SkyApiClient(new SkyConnectionOptions
        {
            BaseAddress = address,
            ApiKey = CliHarness.Key,
            CreateHandler = () =>
            {
                var handler = SkyApiClient.CreateHandler(address);
                handler.Proxy = proxy;
                return handler;
            }
        });
        await client.GetAsync(SkyApiClient.OperationsRoute + "info", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A plain HTTP/1.1 server on loopback that records the head of every request and answers it with an empty JSON
    /// object. It stands for CameraAgent, or for a proxy, which is sent the same request with the absolute address.
    /// </summary>
    private sealed class RecordingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly Task _serving;

        public RecordingHttpServer()
        {
            _listener.Start();
            _serving = ServeAsync(_stop.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Uri Address => new($"http://127.0.0.1:{Port}/");

        public IReadOnlyList<string> Requests => [.. _requests];

        /// <summary>
        /// Stops the server. Stopping is signalled before the listener stops, so the accept loop ends on its own, however
        /// its shutdown reaches it, and only a fault of the server itself is thrown here.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            await _serving.ConfigureAwait(false);
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient connection;
                try
                {
                    connection = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                // Stopping reaches a pending accept as cancellation, and an accept begun after the listener stopped as
                // "not listening" (InvalidOperationException), a disposed listener or a socket error.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                using (connection)
                {
                    try
                    {
                        var stream = connection.GetStream();
                        _requests.Enqueue(await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false));
                        await stream.WriteAsync(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray(),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)
                && await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1)
            {
                head.Append((char)one[0]);
            }

            return head.ToString();
        }
    }

    /// <summary>
    /// A proxy as <c>http_proxy</c> with <c>no_proxy</c> configures one: every request goes through it except those to a
    /// host the bypass list names.
    /// </summary>
    private sealed class EnvironmentLikeProxy(Uri proxy, params string[] noProxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : proxy;

        public bool IsBypassed(Uri host)
        {
            ArgumentNullException.ThrowIfNull(host);
            return noProxy.Contains(host.Host, StringComparer.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    public void Store_DefaultPathFollowsXdgConfigHomeAndOtherwiseTheHomeDirectory()
    {
        Assert.AreEqual(
            Path.Combine("/srv/config", "hvo-sky", "credentials.json"),
            SkyCredentialStore.GetDefaultPath(name => name == "XDG_CONFIG_HOME" ? "/srv/config" : null, "/home/ada"));
        Assert.AreEqual(
            Path.Combine("/home/ada", ".config", "hvo-sky", "credentials.json"),
            SkyCredentialStore.GetDefaultPath(name => name == "XDG_CONFIG_HOME" ? "relative/path" : null, "/home/ada"));
        Assert.AreEqual(
            Path.Combine("/home/ada", ".config", "hvo-sky", "credentials.json"),
            SkyCredentialStore.GetDefaultPath(static _ => null, "/home/ada"));
    }

    [TestMethod]
    [DataRow("""{"schemaVersion": 2, "url": "http://127.0.0.1:5130/"}""", "schema 2")]
    [DataRow("""{"schemaVersion": 1, "url": "http://127.0.0.1:5130/", "password": "x"}""", "not a valid credentials file")]
    [DataRow("""{"schemaVersion": 1, "apiKey": "hvo_sky_wrong"}""", "is not a CameraAgent API key")]
    [DataRow("""{"schemaVersion": 1, "url": "file:///etc/passwd"}""", "cannot be used")]
    [DataRow("not json", "not a valid credentials file")]
    public void Store_RefusesAFileItCannotUse(string content, string expected)
    {
        using var harness = new CliHarness(signedIn: false);
        Directory.CreateDirectory(Path.GetDirectoryName(harness.CredentialsPath)!, DirectoryMode);
        File.WriteAllText(harness.CredentialsPath, content);
        File.SetUnixFileMode(harness.CredentialsPath, FileMode);

        var error = Assert.Throws<SkyCredentialFileException>(() => SkyCredentialStore.Load(harness.CredentialsPath));

        StringAssert.Contains(error.Message, expected, StringComparison.Ordinal);
        Assert.DoesNotContain("hvo_sky_wrong", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Store_PrintsTheKeysIdentifierNeverTheKey()
    {
        var stored = new SkyStoredCredentials { Url = new Uri(CliHarness.Url), ApiKey = CliHarness.Key };
        var connection = new SkyCliConnection(new Uri(CliHarness.Url), CliHarness.Key, "test");

        Assert.DoesNotContain(CliHarness.Key, stored.ToString(), StringComparison.Ordinal);
        StringAssert.Contains(stored.ToString(), CliHarness.KeyId, StringComparison.Ordinal);
        Assert.DoesNotContain(CliHarness.Key, connection.ToString(), StringComparison.Ordinal);
        Assert.AreEqual(CliHarness.KeyId, connection.KeyId);
    }

    [TestMethod]
    public void ApiKey_FormatMatchesWhatCameraAgentIssues()
    {
        var (id, token) = HVO.SkyMonitor.CameraAgent.Authentication.CameraAgentApiKeyToken.Create();

        Assert.IsTrue(SkyApiKey.IsWellFormed(token));
        Assert.AreEqual(id, SkyApiKey.IdOf(token));
        Assert.IsTrue(SkyApiKey.AppearsIn("--x=" + token));
        Assert.IsFalse(SkyApiKey.IsWellFormed(token[..^1]));
        Assert.IsFalse(SkyApiKey.IsWellFormed(token.ToUpperInvariant()));
        Assert.IsNull(SkyApiKey.IdOf("hvo_sky_x"));
    }
}
