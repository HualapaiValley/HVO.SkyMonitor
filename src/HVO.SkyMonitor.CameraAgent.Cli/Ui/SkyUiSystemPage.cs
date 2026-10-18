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
        var left = (width - 3) / 2;
        Place(_clientPanel, 1, 0, left, 9);
        Place(_agentPanel, 2 + left, 0, width - 3 - left, 9);
        PlaceButtons(Math.Min(height - 1, 10));
    }

    public override void DataChanged()
    {
        var connection = Ui.Connection;
        var info = Ui.Data.Info;
        _client.Fields =
        [
            new("Version", Ui.Context.Host.ClientVersion),
            new("Address", connection?.Url.ToString() ?? "none", connection is null ? HvoTone.Warning : HvoTone.Text),
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

            var context = Ui.Context;
            Ui.Say(HvoNotice.Info, $"Checking the key with CameraAgent at {url}...");
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
                        var file = SkyCredentialStore.Load(context.CredentialsPath);
                        SkyCredentialStore.Save(context.CredentialsPath, (file ?? new SkyStoredCredentials()) with { Url = url, ApiKey = key });
                    }
                    catch (Exception failure) when (failure is SkyCredentialFileException or IOException or UnauthorizedAccessException)
                    {
                        Ui.Say(HvoNotice.Danger, $"The key was accepted but not saved: {failure.Message}", "[ERROR]");
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

        Ui.OpenPrompt(new SkyUiPromptSpec("Sign out",
        [
            new($"Remove API key {connection.KeyId} from {Ui.Context.CredentialsPath}? The address is kept."),
            new("The key itself works until the site owner revokes it in CameraAgent (Account, API keys).", HvoTone.Muted)
        ], "Sign out", _ =>
        {
            try
            {
                var file = SkyCredentialStore.Load(Ui.Context.CredentialsPath);
                if (file is not null)
                {
                    SkyCredentialStore.Save(Ui.Context.CredentialsPath, file with { ApiKey = null });
                }
            }
            catch (Exception failure) when (failure is SkyCredentialFileException or IOException or UnauthorizedAccessException)
            {
                return $"The key could not be removed: {failure.Message}";
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

        Ui.OpenPrompt(new SkyUiPromptSpec("Restart CameraAgent",
        [
            new($"Restart CameraAgent at {Ui.Connection?.Url}? Capture stops until it is back."),
            new("Settings and a staged rig that apply at its next start take effect. The interface waits for the new process.", HvoTone.Muted)
        ], "Restart", _ =>
        {
            Ui.Restart((outcome, error) => Ui.Say(error is null ? HvoNotice.Success : HvoNotice.Danger, error is null
                ? $"CameraAgent is back: started {SkyCliFormat.Time(outcome!.After!.Host.ProcessStartedUtc)}, running {outcome.After.Application.InformationalVersion}."
                : $"CameraAgent was not restarted: {SkyCliContext.Classify(error).Message}", error is null ? null : "[REFUSED]"));
            return null;
        }));
    }
}
