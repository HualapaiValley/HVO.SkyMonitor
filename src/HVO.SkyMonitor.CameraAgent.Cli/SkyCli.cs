using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>The <c>hvo-sky</c> process.</summary>
internal static class SkyCliProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var termination = SkyCliTermination.Register();
        return await SkyCli.RunAsync(args, SkyCliHost.System(), termination.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>hvo-sky</c>: the command-line client of a running CameraAgent (install contract section 8). Every command talks to
/// CameraAgent's operations API over HTTP with an owner API key; nothing reaches into its process, its containers or its
/// databases. The installer, <c>hvo-sky-install</c>, owns the lifecycle; this client owns the runtime.
/// </summary>
internal static partial class SkyCli
{
    internal const string CommandName = "hvo-sky";

    /// <summary>Options that would put a credential on the command line. None exists; each is refused by name.</summary>
    private static readonly string[] CredentialOptions =
        ["--api-key", "--apikey", "--key", "--password", "--passwd", "--token", "--secret", "--session"];

    /// <summary>
    /// Runs one command line and returns its exit code (<see cref="SkyExitCode"/>). <paramref name="cancellationToken"/>
    /// is the interrupt (<see cref="SkyCliTermination.Token"/> in the real process).
    /// </summary>
    public static async Task<int> RunAsync(string[] args, SkyCliHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        // Before the parser sees them: its errors repeat the tokens they reject, and a credential must not be repeated.
        if (RefuseCredentialArguments(args, host))
        {
            return (int)SkyExitCode.Usage;
        }

        var root = CreateRootCommand(host);
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await host.Error.WriteLineAsync(error.Message);
            }

            await host.Error.WriteLineAsync($"Run '{CommandName} {DescribeCommand(parseResult)}--help' for usage.");
            return (int)SkyExitCode.Usage;
        }

        return await parseResult.InvokeAsync(
            new InvocationConfiguration
            {
                Output = host.Out,
                Error = host.Error,
                EnableDefaultExceptionHandler = false,

                // The signals are SkyCliTermination's, which also handles SIGHUP.
                ProcessTerminationTimeout = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses an API key anywhere on the command line, and any option that would carry a credential, without repeating
    /// either: an argument is visible to other users of the machine and is kept in shell history.
    /// </summary>
    internal static bool RefuseCredentialArguments(IReadOnlyList<string> args, SkyCliHost host)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        if (args.Any(SkyApiKey.AppearsIn))
        {
            host.Error.WriteLine(
                $"An API key was given on the command line. {CommandName} never takes one there, and did not use it. It may now be in your shell history: revoke it in CameraAgent (Account, API keys) and create another. Then run '{CommandName} login', which reads the key from the terminal or standard input, or set {SkyCredentialStore.ApiKeyVariable}.");
            return true;
        }

        if (args.FirstOrDefault(IsCredentialOption) is { } option)
        {
            var name = option.Split('=')[0];
            host.Error.WriteLine(
                $"{name} is not an option: {CommandName} never takes a credential on the command line. Run '{CommandName} login', which reads the API key from the terminal or standard input, or set {SkyCredentialStore.ApiKeyVariable}.");
            return true;
        }

        return false;

        static bool IsCredentialOption(string argument)
            => CredentialOptions.Any(name => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The global options, recognised after any command.</summary>
    internal sealed class GlobalOptions
    {
        public Option<bool> Json { get; } = new("--json")
        {
            Description = "Write results as one JSON document on standard output (errors too).",
            Recursive = true
        };

        public Option<Uri?> Url { get; } = new("--url")
        {
            Description = $"CameraAgent's address, such as http://127.0.0.1:5130/. Overrides {SkyCredentialStore.UrlVariable} and the credentials file.",
            Recursive = true,
            CustomParser = ParseUrl
        };

        public Option<string?> CredentialsFile { get; } = new("--credentials-file")
        {
            Description = "The credentials file (default: $XDG_CONFIG_HOME/hvo-sky/credentials.json, or ~/.config/hvo-sky/credentials.json).",
            Recursive = true
        };

        public SkyCliContext CreateContext(SkyCliHost host, ParseResult parseResult)
            => new(host, parseResult.GetValue(Json), parseResult.GetValue(Url), parseResult.GetValue(CredentialsFile));
    }

    internal static RootCommand CreateRootCommand(SkyCliHost host)
    {
        var options = new GlobalOptions();
        var root = new RootCommand(
            "The command-line client of a running CameraAgent. It talks to CameraAgent's operations API with an owner API key; 'hvo-sky login' saves one.")
        {
            options.Json,
            options.Url,
            options.CredentialsFile
        };

        foreach (var command in new CommandBuilder(host, options).CreateCommands())
        {
            root.Add(command);
        }

        return root;
    }

    /// <summary>Adds the command actions: each gets a context, and every failure becomes an exit code.</summary>
    internal sealed partial class CommandBuilder(SkyCliHost host, GlobalOptions options)
    {
        public IEnumerable<Command> CreateCommands()
        {
            yield return CreateStatusCommand();
            yield return CreateInfoCommand();
            yield return CreateConfigCommand();
            yield return CreateRestartCommand();
            yield return CreateLoginCommand();
            yield return CreateLogoutCommand();
            yield return CreateRigCommand();
            yield return CreateScheduleCommand();
        }

        private void SetAction(Command command, Func<SkyCliContext, ParseResult, CancellationToken, Task<int>> action)
            => command.SetAction((parseResult, cancellationToken) => RunActionAsync(parseResult, action, cancellationToken));

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Every command must end with a documented exit code, so an unforeseen failure is reported as a failed command (1) rather than as an unhandled-exception crash.")]
        private async Task<int> RunActionAsync(
            ParseResult parseResult,
            Func<SkyCliContext, ParseResult, CancellationToken, Task<int>> action,
            CancellationToken cancellationToken)
        {
            SkyCliContext context;
            try
            {
                context = options.CreateContext(host, parseResult);
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                await host.Error.WriteLineAsync($"The credentials file path is not valid: {error.Message}");
                return (int)SkyExitCode.Usage;
            }

            try
            {
                return await action(context, parseResult, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Reported as any refusal is, so --json still writes its error document.
                return context.Fail(new SkyCliOutcomeException(
                    "Interrupted. What was already sent may still take effect.", SkyExitCode.Interrupted, "Interrupted"));
            }
            catch (Exception error)
            {
                return context.Fail(error);
            }
        }

        /// <summary>A list argument takes every token no option claims, so an unknown option is refused rather than read as a value.</summary>
        private static void RejectOptions(Argument<string[]> argument) => argument.Validators.Add(result =>
        {
            if (result.Tokens.FirstOrDefault(token => token.Value.StartsWith('-')) is { } option)
            {
                result.AddError($"Unrecognized command or argument '{option.Value}'.");
            }
        });

        /// <summary>
        /// A single argument would otherwise take an unknown option as its value: it is refused as the command line refuses
        /// any unknown option. <paramref name="allowDash"/> keeps <c>-</c>, which names standard input.
        /// </summary>
        private static void RejectOption<T>(Argument<T> argument, bool allowDash = false) => argument.Validators.Add(result =>
        {
            if (result.Tokens.FirstOrDefault(token => token.Value.StartsWith('-') && !(allowDash && token.Value == "-")) is { } option)
            {
                result.AddError($"Unrecognized command or argument '{option.Value}'.");
            }
        });

        private static Option<bool> ForceOption(string description) => new("--force") { Description = description };

        private static Option<string?> ReasonOption() => new("--reason")
        {
            Description = $"Why, recorded with the change (at most {SkySettingsContract.MaximumReasonLength} characters)."
        };

        private static string? ReadReason(ParseResult parseResult, Option<string?> option)
        {
            var reason = parseResult.GetValue(option);
            if (reason is not null && reason.Length > SkySettingsContract.MaximumReasonLength)
            {
                throw new SkyCliUsageException($"--reason takes at most {SkySettingsContract.MaximumReasonLength} characters.");
            }

            return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        }

        /// <summary>Writes one line of text, or <paramref name="json"/> with <c>--json</c>, and returns success.</summary>
        private static int Report(SkyCliContext context, object json, string text)
        {
            if (context.Json)
            {
                context.WriteJson(json);
            }
            else
            {
                context.Out.WriteLine(text);
            }

            return (int)SkyExitCode.Success;
        }
    }

    private static Uri? ParseUrl(ArgumentResult result)
    {
        var text = result.Tokens.Count == 0 ? string.Empty : result.Tokens[0].Value;
        if (SkyAddress.TryParse(text, out var address, out var error))
        {
            return address;
        }

        result.AddError($"--url: {error}");
        return null;
    }

    private static string DescribeCommand(ParseResult parseResult)
    {
        var names = new List<string>();
        for (var result = parseResult.CommandResult; result.Parent is CommandResult parent; result = parent)
        {
            names.Insert(0, result.Command.Name);
        }

        return names.Count == 0 ? string.Empty : string.Join(' ', names) + " ";
    }
}
