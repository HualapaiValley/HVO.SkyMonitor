using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// F5 System: hvo-sky's own version and connection, which always show, and CameraAgent's version, runtime, catalog and
/// restart support. Sign in saves an address and an owner API key after CameraAgent accepts it, as <c>hvo-sky login</c>
/// does; Sign out forgets the key here; Restart restarts CameraAgent and waits for it to come back.
/// </summary>
internal sealed class SkyUiSystemPage : SkyUiPage
{
    private readonly SkyUiFields _client;
    private readonly SkyUiFields _agent;
    private readonly HvoPanel _clientPanel;
    private readonly HvoPanel _agentPanel;
    private readonly Button _signOut;
    private readonly Button _restart;

    public SkyUiSystemPage(SkyTerminalUi ui)
        : base(ui, "System")
    {
        _client = new SkyUiFields(Theme, 10);
        _agent = new SkyUiFields(Theme, 10);
        _clientPanel = AddPanel("hvo-sky", _client);
        _agentPanel = AddPanel("CameraAgent", _agent);
        AddButton("Sign in", SignIn, primary: true);
        _signOut = AddButton("Sign out", SignOut);
        _restart = AddButton("Restart", Restart);
        AddButton("Refresh", Refresh);
    }

    public override string Help => Ui.Data.Info.Value is { Restart.Supervised: false }
        ? "CameraAgent is not supervised here, so it cannot restart itself: restart it with its service manager."
        : "hvo-sky's own version shows even when CameraAgent cannot be read.";

    public override void Refresh() => Ui.Read(Ui.Data.Info, SkyRoutes.Info, SkyApiClient.Read<SkyInfo>);

    public override void Arrange(int width, int height, bool wide)
    {
        if (wide)
        {
            var left = (width - 3) / 2;
            Place(_clientPanel, 1, 0, left, 9);
            Place(_agentPanel, 2 + left, 0, width - 3 - left, 9);
            PlaceButtons(Math.Min(height - 1, 10));
        }
        else
        {
            // 80 x 24: one above the other, so the paths and versions have the width. The address is in the header.
            Place(_clientPanel, 1, 0, width - 2, 7);
            Place(_agentPanel, 1, 7, width - 2, 8);
            PlaceButtons(Math.Min(height - 1, 16));
        }
    }

    public override void DataChanged()
    {
        var connection = Ui.Connection;
        var info = Ui.Data.Info;
        _client.Fields =
        [
            new("Version", Ui.Context.Host.ClientVersion),
            new("Key", connection is null ? "none" : connection.KeyId, connection is null ? HvoTone.Warning : HvoTone.Text),
            new("Access", info.Value?.Caller is { } caller ? SkyStatusText.DescribeAccess(caller.Access) : "not read yet"),
            new("From", connection?.KeySource ?? Ui.ConnectionProblem ?? "nowhere", connection is null ? HvoTone.Warning : HvoTone.Text),
            new("File", Ui.Context.CredentialsPath, HvoTone.Muted)
        ];
        _agentPanel.Caption = Titled("CameraAgent", info);
        _agent.Fields = info.Value is { } agent
            ?
            [
                new("Version", agent.Application.InformationalVersion),
                new("Runtime", $"{agent.Host.Runtime} ({agent.Host.RuntimeIdentifier})"),
                new("Started", SkyCliFormat.Time(agent.Host.ProcessStartedUtc)),
                new("Uptime", SkyCliFormat.Duration(TimeSpan.FromSeconds(agent.Host.UptimeSeconds))),
                new("Catalog", agent.Catalog is { } catalog ? $"{SkyJson.Text(catalog, "catalogId")} {SkyJson.Text(catalog, "catalogVersion")}" : "unknown"),
                new("Restart", agent.Restart.Requested ? "requested" : agent.Restart.Supervised ? "supervised" : "not supervised",
                    agent.Restart.Supervised ? HvoTone.Text : HvoTone.Warning)
            ]
            : [connection is null ? new SkyUiField(string.Empty, "Not signed in: Sign in below.", HvoTone.Warning) : Pending(info, "CameraAgent's information")];
        _signOut.Enabled = connection is not null && connection.KeySource == Ui.Context.CredentialsPath;
        _restart.Enabled = CanChange && info.Value?.Restart is { Supervised: true, Requested: false };
    }

    public override string Describe()
        => string.Join("\n\n", Describe(_clientPanel.Caption, _client, 76), Describe(_agentPanel.Caption, _agent, 76));

    private void SignIn()
    {
        var initial = Ui.Connection?.Url.ToString() ?? SkyCli.DefaultUrl;
        Ui.OpenPrompt(new SkyUiPromptSpec("Sign in",
        [
            new("Save CameraAgent's address and an owner API key, after CameraAgent accepts the key."),
            new("The site owner creates keys in CameraAgent (Account, API keys). The key is never shown.", HvoTone.Muted)
        ], "Sign in", answers =>
        {
            if (!SkyAddress.TryParse(answers.Fields[0], out var url, out var problem))
            {
                return $"The address: {problem}";
            }

            var key = answers.Fields[1].Trim();
            if (!SkyApiKey.IsWellFormed(key))
            {
                return $"That is not a CameraAgent API key: one starts with {SkyApiKey.Prefix} and is {SkyApiKey.Length} characters long.";
            }

            // The key goes to CameraAgent now: over plain HTTP to another machine, that is said first, as 'hvo-sky login' says it.
            var context = Ui.Context;
            if (context.ClearTextWarning(url!) is { } warning)
            {
                Ui.Say(HvoNotice.Warning, warning, "[PLAIN HTTP]");
            }
            else
            {
                Ui.Say(HvoNotice.Info, $"Checking the key with CameraAgent at {url}...");
            }

            Ui.Run(
                async token =>
                {
                    using var client = context.CreateClient(url!, key);
                    return await client.GetAsync<SkyInfo>(SkyRoutes.Info, token).ConfigureAwait(false);
                },
                (info, error) =>
                {
                    if (error is not null)
                    {
                        Ui.Say(HvoNotice.Danger, $"Not signed in: {SkyCliContext.Classify(error).Message}", "[REFUSED]");
                        return;
                    }

                    try
                    {
                        var file = context.LoadCredentials();
                        context.SaveCredentials((file ?? new SkyStoredCredentials()) with { Url = url, ApiKey = key });
                    }
                    catch (SkyCredentialFileException failure)
                    {
                        Ui.Say(HvoNotice.Danger, $"CameraAgent accepted the key, but it was not saved: {failure.Message}", "[REFUSED]");
                        return;
                    }

                    Ui.Connect();
                    var environment = !string.IsNullOrWhiteSpace(context.Host.GetEnvironmentVariable(SkyCredentialStore.ApiKeyVariable))
                        ? $" {SkyCredentialStore.ApiKeyVariable} is set, and takes precedence while it is." : string.Empty;
                    Ui.Say(HvoNotice.Success,
                        $"Signed in with API key {SkyApiKey.IdOf(key)} ({SkyStatusText.DescribeAccess(info!.Caller.Access)}). Saved in {context.CredentialsPath}.{environment}");
                });
            return null;
        })
        {
            Fields = [new SkyUiPromptField("Address", initial), new SkyUiPromptField("API key", Secret: true)]
        });
    }

    private void SignOut()
    {
        if (!_signOut.Enabled || Ui.Connection is not { } connection)
        {
            return;
        }

        Ui.OpenPrompt(new SkyUiPromptSpec("Sign out / Review",
        [
            new($"Remove API key {connection.KeyId} from {Ui.Context.CredentialsPath}? The address is kept."),
            new("The key itself works until the site owner revokes it in CameraAgent (Account, API keys).", HvoTone.Muted)
        ], "Sign out", _ =>
        {
            // A file that exists but cannot be used is refused, as 'hvo-sky logout' refuses it: the key may still be in it.
            try
            {
                var file = Ui.Context.LoadCredentials();
                if (file is not null)
                {
                    Ui.Context.SaveCredentials(file with { ApiKey = null });
                }
            }
            catch (SkyCredentialFileException failure)
            {
                return $"The API key could not be removed: {failure.Message}";
            }

            Ui.Connect();
            Ui.Say(HvoNotice.Success, $"Removed API key {connection.KeyId}. It still works until the site owner revokes it.");
            return null;
        }));
    }

    private void Restart()
    {
        if (!_restart.Enabled)
        {
            return;
        }

        Ui.OpenChangePrompt(new SkyUiPromptSpec("Restart / Review",
        [
            new($"Restart CameraAgent at {Ui.Connection?.Url}? Capture stops until it is back."),
            new("Settings and a staged rig that apply at its next start take effect. The interface waits for the new process.", HvoTone.Muted)
        ], "Restart", _ =>
        {
            Ui.Restart("the restart", after: null, (outcome, error, operation) => Ui.SayOutcome(operation, error,
                error is null ? $"CameraAgent is back: started {SkyCliFormat.Time(outcome!.After!.Host.ProcessStartedUtc)}, running {outcome.After.Application.InformationalVersion}." : string.Empty,
                "CameraAgent was not restarted"));
            return null;
        }));
    }
}
