using System.CommandLine;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    /// <summary>The address <c>login</c> offers at its prompt: the installer's default listener on this machine.</summary>
    internal const string DefaultUrl = "http://127.0.0.1:5130/";

    internal sealed partial class CommandBuilder
    {
        private Command CreateLoginCommand()
        {
            var command = new Command(
                "login",
                "Save CameraAgent's address and an owner API key in the credentials file, after checking that CameraAgent accepts the key. The key is read from the terminal without echo, or as one line of standard input; never from the command line. The site owner creates keys in CameraAgent (Account, API keys).");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                var file = context.LoadCredentials();
                var url = context.UrlOverride
                    ?? SkyCredentialStore.FromEnvironment(context.Host.GetEnvironmentVariable).Url
                    ?? file?.Url
                    ?? AskUrl(context);
                var key = context.ReadSecret($"API key for {url}: ");
                if (!SkyApiKey.IsWellFormed(key))
                {
                    throw new SkyCliUsageException(
                        $"That is not a CameraAgent API key: one starts with {SkyApiKey.Prefix} and is {SkyApiKey.Length} characters long. The site owner creates keys in CameraAgent (Account, API keys).");
                }

                // Checked before it is saved: a key CameraAgent refuses is never written (exit 3, nothing changed).
                context.WarnIfClearText(url);
                SkyInfo info;
                using (var client = context.CreateClient(url, key))
                {
                    info = await client.GetAsync<SkyInfo>(SkyRoutes.Info, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    context.SaveCredentials((file ?? new SkyStoredCredentials()) with { Url = url, ApiKey = key });
                }
                catch (SkyCredentialFileException problem)
                {
                    throw new SkyCredentialFileException($"CameraAgent accepted the key, but it was not saved: {problem.Message}", problem);
                }

                var keyId = SkyApiKey.IdOf(key);
                if (context.Json)
                {
                    context.WriteJson(new
                    {
                        signedIn = true,
                        url,
                        keyId,
                        access = info.Caller.Access,
                        credentialsFile = context.CredentialsPath
                    });
                }
                else
                {
                    await context.Out.WriteLineAsync(
                        $"Signed in to {info.Application.Name} at {url} with API key {keyId} ({SkyStatusText.DescribeAccess(info.Caller.Access)}). Saved in {context.CredentialsPath}.");
                }

                if (!string.IsNullOrWhiteSpace(context.Host.GetEnvironmentVariable(SkyCredentialStore.ApiKeyVariable)))
                {
                    await context.Error.WriteLineAsync($"{SkyCredentialStore.ApiKeyVariable} is set, and takes precedence over the saved key while it is.");
                }

                return (int)SkyExitCode.Success;
            });
            return command;
        }

        private static Uri AskUrl(SkyCliContext context)
        {
            if (!context.Host.IsInteractive)
            {
                throw new SkyCliUsageException($"No CameraAgent address: give it with --url, or set {SkyCredentialStore.UrlVariable}.");
            }

            var text = context.Ask($"CameraAgent address [{DefaultUrl}]: ", false)?.Trim();
            if (!SkyAddress.TryParse(string.IsNullOrEmpty(text) ? DefaultUrl : text, out var address, out var error))
            {
                throw new SkyCliUsageException($"The address: {error}");
            }

            return address!;
        }

        private Command CreateLogoutCommand()
        {
            var command = new Command(
                "logout",
                "Remove the saved API key from the credentials file; the address is kept. The key itself works until the site owner revokes it in CameraAgent. A credentials file that exists but cannot be used is refused (exit 3), as the key may still be in it.");
            SetAction(command, async (context, _, _) =>
            {
                // A file that exists but cannot be used is not "no key": the key may still be in it (exit 3).
                SkyStoredCredentials? file;
                string? key;
                try
                {
                    file = context.LoadCredentials();
                    key = file?.ApiKey;
                    if (key is not null)
                    {
                        context.SaveCredentials(file! with { ApiKey = null });
                    }
                }
                catch (SkyCredentialFileException problem)
                {
                    throw new SkyCredentialFileException($"The API key could not be removed: {problem.Message}", problem);
                }

                if (key is null)
                {
                    return Report(context, new { signedOut = false, keyId = (string?)null, credentialsFile = context.CredentialsPath },
                        "No API key is saved.");
                }

                var keyId = SkyApiKey.IdOf(key);
                var result = Report(context, new { signedOut = true, keyId, credentialsFile = context.CredentialsPath },
                    $"Removed API key {keyId} from {context.CredentialsPath}. The key still works until the site owner revokes it in CameraAgent (Account, API keys).");
                if (!string.IsNullOrWhiteSpace(context.Host.GetEnvironmentVariable(SkyCredentialStore.ApiKeyVariable)))
                {
                    await context.Error.WriteLineAsync($"{SkyCredentialStore.ApiKeyVariable} is still set, so commands still use that key.");
                }

                return result;
            });
            return command;
        }
    }
}
