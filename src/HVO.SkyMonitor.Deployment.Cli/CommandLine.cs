using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class CommandLine
{
    private const string Executable = "hvo-skymonitor";

    private static readonly string[] InstallValueOptions =
    [
        "--config", "--instance-id", "--friendly-name", "--owner-email", "--bind-address", "--port",
        "--product-root", "--catalog-bundle", "--catalog-manifest", "--catalog-index", "--catalog-version", "--asset-base-url", "--channel", "--image-ref", "--image-archive",
        "--image-archive-sha256", "--image-manifest", "--image-index", "--image-version",
        "--password-file", "--latitude", "--longitude",
        "--elevation", "--time-zone", "--replay-profile", "--cpus", "--memory"
    ];

    private static readonly string[] InstallFlags =
        ["--dry-run", "--resume", "--json", "--generate-password", "--acknowledge-plaintext-http", "--no-download"];

    private static readonly string[] OwnerRecoveryValueOptions = ["--instance-id", "--product-root", "--password-file"];

    private static readonly string[] OwnerRecoveryFlags = ["--generate-password", "--resume", "--json"];

    private static readonly string[] StatePreflightValueOptions =
    [
        "--instance-id", "--product-root", "--image-ref",
        "--image-manifest", "--image-index", "--image-version", "--asset-base-url", "--channel"
    ];

    private static readonly string[] StatePreflightFlags = ["--json", "--no-download"];

    private static readonly string[] StateResetValueOptions = ["--instance-id", "--confirm-instance-id", "--product-root"];

    private static readonly string[] StateResetFlags = ["--json", "--dry-run"];

    private static readonly string[] LifecycleValueOptions =
    [
        "--instance-id", "--confirm-instance-id", "--confirm-removed-operation-id", "--operation-id", "--product-root",
        "--image-ref", "--image-archive", "--image-archive-sha256", "--image-manifest", "--image-index", "--image-version",
        "--catalog-bundle", "--catalog-manifest", "--catalog-index",
        "--catalog-version", "--asset-base-url", "--channel"
    ];

    private static readonly string[] LifecycleFlags =
        ["--dry-run", "--resume", "--restore-only", "--json", "--no-download", "--migration-backward-compatible"];

    private static readonly CommandDefinition HostSetProductRoot =
        new(["host", "set-product-root"], "<absolute-path>", [], ["--json"]);

    /// <summary>
    /// Every command the parser accepts, in the order help lists them. Each parser reads its options from the same
    /// lists, so help names exactly the options that parser admits.
    /// </summary>
    internal static readonly IReadOnlyList<CommandDefinition> Commands =
    [
        Lifecycle("status"),
        new(["cameraagent", "install"], null, InstallValueOptions, InstallFlags,
            "--config reads the request from a JSON file and cannot be combined with the other value options."),
        new(["cameraagent", "recover-owner"], null, OwnerRecoveryValueOptions, OwnerRecoveryFlags),
        new(["cameraagent", "preflight"], null, StatePreflightValueOptions, StatePreflightFlags),
        new(["cameraagent", "reset-state"], null, StateResetValueOptions, StateResetFlags),
        Lifecycle("cameraagent", "upgrade"),
        Lifecycle("cameraagent", "rollback"),
        Lifecycle("cameraagent", "reinstall"),
        Lifecycle("cameraagent", "uninstall"),
        Lifecycle("cameraagent", "purge"),
        Lifecycle("catalog", "install"),
        Lifecycle("catalog", "select"),
        Lifecycle("catalog", "rollback"),
        Lifecycle("catalog", "gc"),
        Lifecycle("catalog", "check"),
        HostSetProductRoot
    ];

    private static CommandDefinition Lifecycle(params string[] path)
        => new(path, null, LifecycleValueOptions, LifecycleFlags,
            "These options are shared with the other lifecycle commands; not every one applies to this command.");

    /// <summary>
    /// The one place a non-production product root can be admitted. The variable is read here, where the
    /// process boundary is crossed, and never again: every request carries the answer it was parsed with, so
    /// validation cannot be changed by anything that mutates process state after a request exists. Only the
    /// ordinal value <c>1</c> admits, and an installer configuration file cannot reach this decision at all.
    /// </summary>
    private static bool TestProductRootAdmittedByEnvironment()
        => string.Equals(
            Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT"),
            "1",
            StringComparison.Ordinal);

    public static DeploymentCommand ParseCommand(string[] args)
        => ParseCommand(args, TestProductRootAdmittedByEnvironment());

    /// <summary>Parses with admission stated explicitly, so a caller never has to mutate process state.</summary>
    internal static DeploymentCommand ParseCommand(string[] args, bool allowTestProductRoot)
        => ParseCommand(args, new CommandLineContext(allowTestProductRoot, HostPolicyLocation.Production));

    /// <summary>
    /// Parses with admission and the host policy location stated explicitly. The host policy is read exactly once,
    /// here, and the production product root it resolves travels on the request like test-root admission does.
    /// </summary>
    internal static DeploymentCommand ParseCommand(string[] args, CommandLineContext context)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);
        // Help is answered before the host policy is read, so it works even where that policy is refused.
        if (HelpFor(args) is { } help)
        {
            return new HelpCommand(help);
        }
        try
        {
            return ParseOperation(args, context);
        }
        catch (InstallUsageException exception) when (exception.Usage is null)
        {
            // A mistake found here is in the command line itself, so it carries the usage of the command it was made
            // in, or the general usage when the arguments name no command. One raised later, by an operation already
            // under way, carries none and is still reported as that operation's failure.
            exception.Usage = Find(args) is { } command ? CommandUsage(command) : GeneralUsage();
            throw;
        }
    }

    /// <summary>
    /// The usage text an invocation asks for, or <see langword="null"/> when it asks for none. <c>--help</c>,
    /// <c>-h</c> or <c>help</c> alone, or <c>--help</c> or <c>-h</c> alone after a command group, asks for the general
    /// usage; <c>--help</c> or <c>-h</c> where a command reads an option asks for that command's usage.
    /// </summary>
    internal static string? HelpFor(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if ((args is [var only] && (IsHelpOption(only) || only == "help")) ||
            (args is [var group, var option] && IsGroup(group) && IsHelpOption(option)))
        {
            return GeneralUsage();
        }
        return Find(args) is { } command && RequestsHelp(args, command) ? CommandUsage(command) : null;
    }

    /// <summary>The usage that lists every command.</summary>
    internal static string GeneralUsage()
    {
        var usage = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Usage: {Executable} <command> [options]")
            .AppendLine()
            .AppendLine("Commands:");
        foreach (var command in Commands)
        {
            usage.AppendLine(CultureInfo.InvariantCulture, $"  {Synopsis(command)}");
        }
        return usage
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Run '{Executable} <command> --help' to list the options of one command.")
            .ToString();
    }

    /// <summary>The usage of one command, listing the options its parser admits.</summary>
    internal static string CommandUsage(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var usage = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Usage: {Executable} {Synopsis(command)} [options]")
            .AppendLine()
            .AppendLine("Options:");
        foreach (var option in command.ValueOptions)
        {
            usage.AppendLine(CultureInfo.InvariantCulture, $"  {option} <value>");
        }
        foreach (var flag in command.Flags)
        {
            usage.AppendLine(CultureInfo.InvariantCulture, $"  {flag}");
        }
        usage.AppendLine("  -h, --help");
        if (command.Note is not null)
        {
            usage.AppendLine().AppendLine(command.Note);
        }
        return usage.ToString();
    }

    private static string Synopsis(CommandDefinition command)
        => command.Positional is null ? command.Name : $"{command.Name} {command.Positional}";

    private static bool IsHelpOption(string argument) => argument is "--help" or "-h";

    private static bool IsGroup(string argument) => Commands.Any(command => command.Path.Count > 1 && command.Path[0] == argument);

    private static CommandDefinition? Find(string[] args)
        => Commands.FirstOrDefault(command =>
            args.Length >= command.Path.Count && command.Path.SequenceEqual(args.Take(command.Path.Count), StringComparer.Ordinal));

    /// <summary>
    /// Walks the arguments after the command the way its parser does, so a help option is recognised only where an
    /// option is read and an option value that happens to be <c>-h</c> stays a value.
    /// </summary>
    private static bool RequestsHelp(string[] args, CommandDefinition command)
    {
        for (var index = command.Path.Count; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsHelpOption(argument))
            {
                return true;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal) && !command.Flags.Contains(argument, StringComparer.Ordinal) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
            }
        }
        return false;
    }

    /// <summary>The reason given when the arguments name no command.</summary>
    private static string UnknownCommand(string[] args) => args switch
    {
        [] => "Missing command.",
        [var first, var second, ..] when IsHelpOption(first) || first == "help" =>
            $"Unexpected argument '{second}' after '{first}'.",
        [var group] when IsGroup(group) => $"Missing command after '{group}'.",
        [var group, var name, ..] when IsGroup(group) && name.StartsWith('-') => $"Missing command after '{group}'.",
        [var group, var name, ..] when IsGroup(group) => $"Unknown command '{group} {name}'.",
        [var name, ..] => $"Unknown command '{name}'."
    };

    private static DeploymentCommand ParseOperation(string[] args, CommandLineContext context)
    {
        var production = HostProductRootPolicy.Resolve(context.HostPolicy).Path;
        var allowTestProductRoot = context.AllowTestProductRoot;
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "install")
        {
            return new InstallDeploymentCommand(Parse(args, allowTestProductRoot, production))
            {
                AllowTestProductRoot = allowTestProductRoot,
                ProductionProductRoot = production
            };
        }
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "recover-owner")
        {
            return ParseOwnerRecovery(args, allowTestProductRoot, production);
        }
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "preflight")
        {
            return ParseStatePreflight(args, allowTestProductRoot, production);
        }
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "reset-state")
        {
            return ParseStateReset(args, allowTestProductRoot, production);
        }
        if (args.Length >= 1 && args[0] == "host")
        {
            return ParseHost(args, context, production);
        }
        return ParseLifecycle(args, allowTestProductRoot, production);
    }

    public static InstallRequest Parse(string[] args)
        => Parse(args, TestProductRootAdmittedByEnvironment());

    /// <summary>Parses with admission stated explicitly, so a caller never has to mutate process state.</summary>
    internal static InstallRequest Parse(string[] args, bool allowTestProductRoot)
        => Parse(args, allowTestProductRoot, HostProductRootPolicy.Resolve(HostPolicyLocation.Production).Path);

    /// <summary>Parses with admission and the resolved production product root stated explicitly.</summary>
    internal static InstallRequest Parse(string[] args, bool allowTestProductRoot, string productionProductRoot)
    {
        if (args.Length < 2 || args[0] != "cameraagent" || args[1] != "install")
        {
            throw new InstallUsageException("Usage: hvo-skymonitor cameraagent install [options]");
        }

        string? configPath = null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }

            if (InstallFlags.Contains(option, StringComparer.Ordinal))
            {
                if (!flags.Add(option))
                {
                    throw new InstallUsageException($"Duplicate option '{option}'.");
                }
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException($"Option '{option}' requires a value.");
            }

            if (!values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Duplicate option '{option}'.");
            }
        }

        if (values.Remove("--config", out configPath) && values.Count > 0)
        {
            throw new InstallUsageException("--config cannot be combined with value options.");
        }

        InstallRequest request;
        if (configPath is not null)
        {
            byte[] configuration;
            using (var stream = SafeFileSystem.OpenRegularFileRead(configPath))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                configuration = buffer.ToArray();
            }
            request = JsonSerializer.Deserialize(configuration, InstallRequestJsonContext.Default.InstallRequest)
                ?? throw new InstallUsageException("The installer configuration is empty.");
            // A configuration that does not name a product root installs into the production root this host's policy
            // resolved, exactly as the command-line form does; one that names a root keeps it and is validated.
            if (!NamesProductRoot(configuration))
            {
                request = request with { ProductRoot = productionProductRoot };
            }
        }
        else
        {
            RejectUnknown(values.Keys);
            request = new InstallRequest
            {
                InstanceId = ParseGuid(Get(values, "--instance-id"), "--instance-id"),
                FriendlyName = RequireOrPrompt(values, "--friendly-name", "Friendly name: "),
                OwnerEmail = Get(values, "--owner-email") ?? OwnerCredentialDefaults.Email,
                BindAddress = Get(values, "--bind-address") ?? "127.0.0.1",
                Port = ParseInt(Get(values, "--port"), 5130, "--port"),
                ProductRoot = Get(values, "--product-root") ?? productionProductRoot,
                CatalogBundle = Get(values, "--catalog-bundle"),
                CatalogManifest = Get(values, "--catalog-manifest"),
                CatalogIndex = Get(values, "--catalog-index"),
                CatalogVersion = Get(values, "--catalog-version"),
                AssetBaseUrl = Get(values, "--asset-base-url"),
                Channel = ParseChannel(Get(values, "--channel")),
                ImageReference = Get(values, "--image-manifest") is null && Get(values, "--image-index") is null
                    ? RequireOrPrompt(values, "--image-ref", "Immutable CameraAgent image digest or ID: ")
                    : Get(values, "--image-ref") ?? string.Empty,
                ImageArchive = Get(values, "--image-archive"),
                ImageArchiveSha256 = Get(values, "--image-archive-sha256"),
                ImageManifest = Get(values, "--image-manifest"),
                ImageIndex = Get(values, "--image-index"),
                ImageVersion = Get(values, "--image-version"),
                PasswordFile = Get(values, "--password-file"),
                LatitudeDegrees = ParseDouble(Get(values, "--latitude"), 0, "--latitude"),
                LongitudeDegrees = ParseDouble(Get(values, "--longitude"), 0, "--longitude"),
                ElevationMeters = ParseDouble(Get(values, "--elevation"), 0, "--elevation"),
                TimeZoneId = Get(values, "--time-zone") ?? "UTC",
                ReplayProfile = ParseReplayProfile(Get(values, "--replay-profile")),
                Cpus = ParseCpus(Get(values, "--cpus")),
                Memory = Get(values, "--memory")
            };
        }

        request = request with
        {
            AllowTestProductRoot = allowTestProductRoot,
            ProductionProductRoot = productionProductRoot,
            DryRun = flags.Contains("--dry-run") || request.DryRun,
            Resume = flags.Contains("--resume") || request.Resume,
            Json = flags.Contains("--json") || request.Json,
            AcknowledgePlaintextHttp = flags.Contains("--acknowledge-plaintext-http") || request.AcknowledgePlaintextHttp,
            GeneratePassword = flags.Contains("--generate-password") || request.GeneratePassword,
            NoDownload = flags.Contains("--no-download") || request.NoDownload
        };
        request.Validate();
        return request;
    }

    private static bool NamesProductRoot(byte[] configuration)
    {
        try
        {
            using var document = JsonDocument.Parse(configuration);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("productRoot", out _);
        }
        catch (JsonException exception)
        {
            throw new InstallUsageException("The installer configuration is not valid JSON.", exception);
        }
    }

    private static void RejectUnknown(IEnumerable<string> options)
    {
        // --config never reaches this check: it is removed first and admits no other value option.
        var unknown = options.FirstOrDefault(option => !InstallValueOptions.Contains(option, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new InstallUsageException($"Unknown option '{unknown}'.");
        }
    }

    private static string RequireOrPrompt(
        IReadOnlyDictionary<string, string?> values,
        string option,
        string prompt)
    {
        var value = Get(values, option);
        if (value is not null)
        {
            return value;
        }
        if (Console.IsInputRedirected)
        {
            throw new InstallUsageException($"{option} is required.");
        }
        Console.Error.Write(prompt);
        return Console.ReadLine() is { Length: > 0 } entered
            ? entered
            : throw new InstallUsageException($"{option} is required.");
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string option)
        => values.TryGetValue(option, out var value) ? value : null;

    private static Guid? ParseGuid(string? value, string option)
        => value is null ? null : Guid.TryParseExact(value, "D", out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be a canonical UUID.");

    private static int ParseInt(string? value, int fallback, string option)
        => value is null ? fallback : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be an integer.");

    private static double ParseDouble(string? value, double fallback, string option)
        => value is null ? fallback : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be a number.");

    private static decimal? ParseCpus(string? value)
        => value is null ? null : decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException("--cpus must be a number.");

    private static DistributionChannel ParseChannel(string? value)
        => value switch
        {
            null or "local" => DistributionChannel.Local,
            "stable" => DistributionChannel.Stable,
            "nightly" => DistributionChannel.Nightly,
            "prerelease" => DistributionChannel.Prerelease,
            _ => throw new InstallUsageException("--channel must be stable, nightly, prerelease, or local.")
        };

    private static CameraAgentReplayProfile ParseReplayProfile(string? value)
        => value switch
        {
            null or "in-process" => CameraAgentReplayProfile.InProcess,
            "local-runner" => CameraAgentReplayProfile.LocalRunner,
            _ => throw new InstallUsageException("--replay-profile must be in-process or local-runner.")
        };

    private static HostProductRootRequest ParseHost(string[] args, CommandLineContext context, string productionProductRoot)
    {
        if (args.Length < 2 || args[1] != "set-product-root")
        {
            throw new InstallUsageException(UnknownCommand(args));
        }
        if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            throw new InstallUsageException($"{HostSetProductRoot.Name} requires {HostSetProductRoot.Positional}.");
        }
        var (values, flags) = ParseOptions(args, 3, HostSetProductRoot.Flags);
        RejectUnknown(values, HostSetProductRoot.ValueOptions);
        var request = new HostProductRootRequest(args[2], flags.Contains("--json"))
        {
            AllowTestProductRoot = context.AllowTestProductRoot,
            ProductionProductRoot = productionProductRoot,
            PolicyLocation = context.HostPolicy
        };
        request.Validate();
        return request;
    }

    private static LifecycleRequest ParseLifecycle(string[] args, bool allowTestProductRoot, string productionProductRoot)
    {
        var (operation, optionOffset) = args switch
        {
            ["status", ..] => ((LifecycleOperationKind?)null, 1),
            ["cameraagent", "upgrade", ..] => (LifecycleOperationKind.Upgrade, 2),
            ["cameraagent", "rollback", ..] => (LifecycleOperationKind.Rollback, 2),
            ["cameraagent", "reinstall", ..] => (LifecycleOperationKind.Reinstall, 2),
            ["cameraagent", "uninstall", ..] => (LifecycleOperationKind.Uninstall, 2),
            ["cameraagent", "purge", ..] => (LifecycleOperationKind.Purge, 2),
            ["catalog", "install", ..] => (LifecycleOperationKind.CatalogInstall, 2),
            ["catalog", "select", ..] => (LifecycleOperationKind.CatalogSelect, 2),
            ["catalog", "rollback", ..] => (LifecycleOperationKind.CatalogRollback, 2),
            ["catalog", "gc", ..] => (LifecycleOperationKind.CatalogGarbageCollect, 2),
            ["catalog", "check", ..] => (LifecycleOperationKind.CatalogCheck, 2),
            _ => throw new InstallUsageException(UnknownCommand(args))
        };
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionOffset; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }
            if (LifecycleFlags.Contains(option, StringComparer.Ordinal))
            {
                if (!flags.Add(option)) throw new InstallUsageException($"Duplicate option '{option}'.");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Option '{option}' requires one non-duplicate value.");
            }
        }
        RejectUnknown(values, LifecycleValueOptions);
        var request = new LifecycleRequest(
            operation,
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? productionProductRoot,
            flags.Contains("--dry-run"),
            flags.Contains("--resume"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot,
            ProductionProductRoot = productionProductRoot,
            RestoreOnly = flags.Contains("--restore-only"),
            RecoveryOperationId = ParseGuid(Get(values, "--operation-id"), "--operation-id"),
            ImageReference = Get(values, "--image-ref"),
            ImageArchive = Get(values, "--image-archive"),
            ImageArchiveSha256 = Get(values, "--image-archive-sha256"),
            ImageManifest = Get(values, "--image-manifest"),
            ImageIndex = Get(values, "--image-index"),
            ImageVersion = Get(values, "--image-version"),
            NoDownload = flags.Contains("--no-download"),
            MigrationBackwardCompatible = flags.Contains("--migration-backward-compatible"),
            ConfirmationInstanceId = ParseGuid(Get(values, "--confirm-instance-id"), "--confirm-instance-id"),
            ConfirmRemovedOperationId = ParseGuid(
                Get(values, "--confirm-removed-operation-id"), "--confirm-removed-operation-id"),
            CatalogBundle = Get(values, "--catalog-bundle"),
            CatalogManifest = Get(values, "--catalog-manifest"),
            CatalogIndex = Get(values, "--catalog-index"),
            CatalogVersion = Get(values, "--catalog-version"),
            AssetBaseUrl = Get(values, "--asset-base-url"),
            Channel = ParseChannel(Get(values, "--channel"))
        };
        if (operation == LifecycleOperationKind.CatalogSelect && request.CatalogVersion is null)
        {
            throw new InstallUsageException("catalog select requires --catalog-version.");
        }
        request.Validate();
        return request;
    }

    private static CameraAgentStatePreflightRequest ParseStatePreflight(
        string[] args, bool allowTestProductRoot, string productionProductRoot)
    {
        var (values, flags) = ParseOptions(args, 2, StatePreflightFlags);
        RejectUnknown(values, StatePreflightValueOptions);
        var request = new CameraAgentStatePreflightRequest(
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? productionProductRoot,
            Get(values, "--image-ref"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot,
            ProductionProductRoot = productionProductRoot,
            ImageManifest = Get(values, "--image-manifest"),
            ImageIndex = Get(values, "--image-index"),
            ImageVersion = Get(values, "--image-version"),
            AssetBaseUrl = Get(values, "--asset-base-url"),
            // Presence, not the resolved value: an explicit --channel local must be rejected without a signed
            // release just as --channel stable is.
            Channel = Get(values, "--channel") is { } channel ? ParseChannel(channel) : null,
            NoDownload = flags.Contains("--no-download")
        };
        request.Validate();
        return request;
    }

    private static CameraAgentStateResetRequest ParseStateReset(
        string[] args, bool allowTestProductRoot, string productionProductRoot)
    {
        var (values, flags) = ParseOptions(args, 2, StateResetFlags);
        RejectUnknown(values, StateResetValueOptions);
        var request = new CameraAgentStateResetRequest(
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            ParseGuid(Get(values, "--confirm-instance-id"), "--confirm-instance-id"),
            Get(values, "--product-root") ?? productionProductRoot,
            flags.Contains("--dry-run"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot,
            ProductionProductRoot = productionProductRoot
        };
        request.Validate();
        return request;
    }

    private static (Dictionary<string, string?> Values, HashSet<string> Flags) ParseOptions(
        string[] args,
        int optionOffset,
        IReadOnlyCollection<string> supportedFlags)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionOffset; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }
            if (supportedFlags.Contains(option))
            {
                if (!flags.Add(option)) throw new InstallUsageException($"Duplicate option '{option}'.");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Option '{option}' requires one non-duplicate value.");
            }
        }
        return (values, flags);
    }

    private static void RejectUnknown(Dictionary<string, string?> values, IReadOnlyCollection<string> known)
    {
        var unknown = values.Keys.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null) throw new InstallUsageException($"Unknown option '{unknown}'.");
    }

    private static OwnerRecoveryRequest ParseOwnerRecovery(
        string[] args, bool allowTestProductRoot, string productionProductRoot)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }

            if (OwnerRecoveryFlags.Contains(option, StringComparer.Ordinal))
            {
                if (!flags.Add(option))
                {
                    throw new InstallUsageException($"Duplicate option '{option}'.");
                }
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Option '{option}' requires one non-duplicate value.");
            }
        }

        RejectUnknown(values, OwnerRecoveryValueOptions);

        var request = new OwnerRecoveryRequest(
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? productionProductRoot,
            Get(values, "--password-file"),
            flags.Contains("--generate-password"),
            flags.Contains("--resume"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot,
            ProductionProductRoot = productionProductRoot
        };
        request.Validate();
        return request;
    }
}

/// <summary>
/// What the command-line boundary decides once per invocation: whether a non-production product root is admitted,
/// and where the host product-root policy is read. Production uses <see cref="HostPolicyLocation.Production"/>; an
/// isolated test states its own location here rather than mutating process state.
/// </summary>
internal sealed record CommandLineContext(bool AllowTestProductRoot, HostPolicyLocation HostPolicy);

/// <summary>
/// One command: the words that name it, the positional argument it requires if any, the options its parser admits,
/// and a note help prints after them.
/// </summary>
internal sealed record CommandDefinition(
    IReadOnlyList<string> Path,
    string? Positional,
    IReadOnlyList<string> ValueOptions,
    IReadOnlyList<string> Flags,
    string? Note = null)
{
    public string Name => string.Join(' ', Path);
}
