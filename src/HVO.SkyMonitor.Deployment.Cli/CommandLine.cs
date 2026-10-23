using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class CommandLine
{
    private static string Executable => InstallerProgram.Current;

    /// <summary>The option that prints the installer's build identity; it is accepted only on its own.</summary>
    internal const string VersionOption = "--version";

    internal static readonly string[] InstallValueOptions =
    [
        "--config", "--instance-id", "--friendly-name", "--owner-email", "--bind-address", "--port",
        "--product-root", "--catalog-bundle", "--release", "--image-source", "--image-ref", "--image-archive",
        "--image-archive-sha256", "--password-file", "--latitude", "--longitude",
        "--elevation", "--time-zone", "--replay-profile", "--cpus", "--memory"
    ];

    internal static readonly string[] InstallFlags =
        ["--plan", "--dry-run", "--resume", "--json", "--generate-password", "--acknowledge-plaintext-http", "--no-download"];

    /// <summary>
    /// The answers file (install contract section 5). Only the options in <see cref="AnswersOverrideOptions"/> may
    /// accompany it, and <c>--no-download</c>.
    /// </summary>
    internal const string AnswersOption = "--answers";

    private static readonly string[] AnswersValueOptions = [AnswersOption];

    /// <summary>
    /// The value options that may accompany an answers file, each replacing the file's member: the container limits a
    /// re-run changes, and the release and catalog <c>install.sh</c> hands the installer (#1311).
    /// </summary>
    internal static readonly string[] AnswersOverrideOptions = ["--cpus", "--memory", "--release", "--image-source", "--catalog-bundle"];

    /// <summary>The flag every command with a read-only plan accepts; <c>--dry-run</c> is its older name.</summary>
    internal const string PlanOption = "--plan";

    private const string DeprecatedMigrationOption = "--migration-backward-compatible";

    private static readonly string[] OwnerRecoveryValueOptions = ["--instance-id", "--product-root", "--password-file"];

    private static readonly string[] OwnerRecoveryFlags = ["--generate-password", "--resume", "--json"];

    private static readonly string[] StatePreflightValueOptions = ["--instance-id", "--product-root", "--image-ref"];

    private static readonly string[] StatePreflightFlags = ["--json"];

    private static readonly string[] StateResetValueOptions = ["--instance-id", "--confirm-instance-id", "--product-root"];

    private static readonly string[] StateResetFlags = ["--json", "--plan", "--dry-run"];

    /// <summary>The options every lifecycle command that addresses one instance takes.</summary>
    private static readonly string[] InstanceValueOptions = ["--instance-id", "--product-root"];

    /// <summary>The flags of every lifecycle command that changes something.</summary>
    private static readonly string[] OperationFlags = ["--plan", "--dry-run", "--resume", "--json"];

    private static readonly CommandDefinition HostSetProductRoot =
        new(["host", "set-product-root"], "<absolute-path>",
            "Set the folder this host installs instances under, or repair its recorded mount.",
            [], ["--json", "--repair"],
            "With --repair, re-record the current mount of the same existing product root under the host-policy lock. Verify the backing filesystem first.");

    /// <summary>
    /// Every command the parser accepts, in the order help lists them. Each parser reads its options from its command's
    /// lists, so help names exactly the options that parser admits (#1384).
    /// </summary>
    internal static readonly IReadOnlyList<CommandDefinition> Commands =
    [
        new(["partial-prepare-cleanup"], null, "Inspect or recover an interrupted split-host prepare without creating containers.",
            ["--inventory", "--mode", "--run-id", "--state-root", "--evidence-root", "--confirm"], ["--dry-run", "--json"],
            "Requires the retained passed preflight and failed prepare. Specify --dry-run or --confirm RUN-ID; interrupted cleanup resumes with the same inputs."),
        new(["status"], null, "Show the installed instances, or the one --instance-id names.", InstanceValueOptions, ["--json"]),
        new(["cameraagent", "install"], null,
            "Install a CameraAgent instance, or change an installed one's container limits.",
            [.. AnswersValueOptions, .. InstallValueOptions], InstallFlags,
            "A new install makes an instance ID when --instance-id is not given. Only --cpus, --memory, --release, " +
            "--image-source, --catalog-bundle and --no-download may accompany --answers. --config cannot be combined with " +
            "the other value options. " + PlanScratchNote),
        new(["cameraagent", "recover-owner"], null,
            "Give an instance's owner a new temporary password.",
            OwnerRecoveryValueOptions, OwnerRecoveryFlags),
        new(["cameraagent", "preflight"], null,
            "Check, changing nothing, that an instance's state can run its image or --image-ref.",
            StatePreflightValueOptions, StatePreflightFlags),
        new(["cameraagent", "reset-state"], null,
            "Delete an uninstalled instance's local runtime state, keeping its configuration.",
            StateResetValueOptions, StateResetFlags),
        new(["cameraagent", "upgrade"], null,
            "Move an instance to a release's image (--release) or an immutable one (--image-ref).",
            [.. InstanceValueOptions, "--release", "--image-source", "--image-ref", "--image-archive", "--image-archive-sha256", "--operation-id"],
            ["--plan", "--dry-run", "--resume", "--restore-only", "--json", "--no-download", DeprecatedMigrationOption],
            PlanScratchNote),
        Lifecycle("Return an instance to the image it ran before its last upgrade.", "cameraagent", "rollback"),
        Lifecycle("Start an uninstalled instance again from the state it kept.", "cameraagent", "reinstall"),
        Lifecycle("Remove an instance's containers, keeping its state for cameraagent reinstall.", "cameraagent", "uninstall"),
        new(["cameraagent", "purge"], null, "Remove an uninstalled instance and everything it keeps.",
            ["--instance-id", "--confirm-instance-id", "--confirm-removed-operation-id", "--product-root"], OperationFlags,
            PlanScratchNote),
        new(["cameraagent", "backup"], null,
            "Copy an instance, captures included, into one archive in <product-root>/backups.",
            InstanceValueOptions, OperationFlags,
            "Run it as the instance's service account, not with sudo. The archive is readable by its owner alone. " + PlanScratchNote),
        new(["cameraagent", "restore"], "<archive>",
            "Put an instance back, uninstalled, from a backup in <product-root>/backups.",
            InstanceValueOptions, OperationFlags,
            "Run it as the instance's service account, not with sudo. <archive> is a backup's name in <product-root>/backups, or its path there. " +
            "The instance must be uninstalled or absent; cameraagent reinstall starts it once it is restored. " + PlanScratchNote),
        new(["catalog", "install"], null, "Install a catalog bundle beside the catalogs already installed.",
            ["--catalog-bundle", "--product-root"], OperationFlags, PlanScratchNote),
        new(["catalog", "select"], null, "Switch an instance to another installed catalog version.",
            ["--instance-id", "--catalog-version", "--product-root"], OperationFlags, PlanScratchNote),
        Lifecycle("Return an instance to the catalog version it used before.", "catalog", "rollback"),
        new(["catalog", "gc"], null,
            "Remove an installed catalog version that no instance uses.",
            ["--catalog-version", "--product-root"], OperationFlags,
            "--catalog-version is required unless --plan is given, which lists the versions it could remove. " + PlanScratchNote),
        HostSetProductRoot
    ];

    /// <summary>A lifecycle command that addresses one instance and takes nothing else.</summary>
    private static CommandDefinition Lifecycle(string description, params string[] path)
        => new(path, null, description, InstanceValueOptions, OperationFlags, PlanScratchNote);

    /// <summary>
    /// What each option takes and what it does, as every command's help that lists it says. A command lists only the
    /// options its parser admits (<see cref="Commands"/>).
    /// </summary>
    private static readonly Dictionary<string, (string? Value, string Description)> OptionHelp = new(StringComparer.Ordinal)
    {
        ["--inventory"] = ("<file>", "The immutable split-host inventory used by the failed prepare (schema 8 or retained schema 7)."),
        ["--mode"] = ("<isolated|persistent>", "The failed prepare's deployment mode."),
        ["--run-id"] = ("<id>", "The failed prepare's run ID."),
        ["--state-root"] = ("<path>", "The retained owner-only state directory."),
        ["--evidence-root"] = ("<path>", "The retained owner-only evidence directory."),
        ["--confirm"] = ("<run-id>", "Confirm cleanup of only artifacts created by this failed run."),
        [AnswersOption] = ("<file>", "Read the install from a JSON answers file with \"schemaVersion\": 1."),
        ["--config"] = ("<file>", "Deprecated: read the whole install from a JSON file. Use --answers."),
        ["--instance-id"] = ("<uuid>", "The instance's ID."),
        ["--friendly-name"] = ("<name>", "The instance's display name."),
        ["--owner-email"] = ("<email>", $"The owner's email, also a sign-in name (default {OwnerCredentialDefaults.Email})."),
        ["--bind-address"] = ("<address>", "The address CameraAgent listens on (default 127.0.0.1)."),
        ["--port"] = ("<port>", "The port CameraAgent listens on (default 5130)."),
        ["--product-root"] = ("<path>", "The product root (default: this host's production product root)."),
        ["--catalog-bundle"] = ("<path>", "The catalog bundle folder."),
        ["--catalog-version"] = ("<version>", "An installed catalog version."),
        ["--release"] = ("<folder>", "The release folder, holding the release's release.json and SHA256SUMS."),
        ["--image-source"] = ("<source>", "With --release: archive (default) loads its offline image archive; registry pulls its image."),
        ["--image-ref"] = ("<image>", "An immutable image, sha256:<id> or <repository>@sha256:<digest>, instead of a release's."),
        ["--image-archive"] = ("<path>", "With --image-ref: the image archive to load it from."),
        ["--image-archive-sha256"] = ("<sha256>", "The image archive's SHA-256, required with --image-archive."),
        ["--password-file"] = ("<path>", "An owner-only file holding the owner's temporary password."),
        ["--generate-password"] = (null, "Generate the owner's temporary password into an owner-only file."),
        ["--latitude"] = ("<degrees>", "The camera's latitude (default 0)."),
        ["--longitude"] = ("<degrees>", "The camera's longitude (default 0)."),
        ["--elevation"] = ("<metres>", "The camera's elevation (default 0)."),
        ["--time-zone"] = ("<zone>", "The camera's IANA time zone (default UTC)."),
        ["--replay-profile"] = ("<profile>", "Where archived replay runs: in-process (default) or local-runner."),
        ["--cpus"] = ("<count>", "The container's CPU limit."),
        ["--memory"] = ("<size>", "The container's memory limit, such as 4g."),
        ["--confirm-instance-id"] = ("<uuid>", "The instance's ID again, to confirm."),
        ["--confirm-removed-operation-id"] = ("<uuid>", "With --resume: confirm this interrupted purge's tree was removed elsewhere."),
        ["--operation-id"] = ("<uuid>", "With --resume --restore-only: the interrupted upgrade's operation ID."),
        [PlanOption] = (null, "Show what the command would change, and change nothing."),
        ["--dry-run"] = (null, "The older name of --plan."),
        ["--resume"] = (null, "Finish an interrupted run of the same command."),
        ["--json"] = (null, "Write the result, or the error, as JSON."),
        ["--repair"] = (null, "Re-record the current mount, keeping the product root already named by the host policy."),
        ["--acknowledge-plaintext-http"] = (null, "Accept plain HTTP on a --bind-address other than loopback."),
        ["--no-download"] = (null, "Pull nothing: the image must already be on this host or in an offline archive."),
        ["--restore-only"] = (null, "With --resume --operation-id: restore what an interrupted upgrade left, without upgrading."),
        [DeprecatedMigrationOption] = (null, "Deprecated, and has no effect.")
    };

    /// <summary>
    /// What a plan leaves on disk (#1315): nothing of the product's. The state preflight reads a database caught with an
    /// uncommitted transaction through a private temporary copy, because SQLite cannot read it safely in place, and
    /// removes the copy.
    /// </summary>
    private const string PlanScratchNote =
        "A plan writes no product file, lock or Compose file; a database caught mid-transaction is read through a private temporary copy that is removed.";

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
        // Help and the version are answered before the host policy is read, so they work even where that policy is
        // refused, and without Docker.
        if (HelpFor(args) is { } help)
        {
            return new HelpCommand(help);
        }
        if (args is [VersionOption])
        {
            return new VersionCommand(InstallerBuildIdentity.Current);
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
            exception.Usage = Find(args) is { } command
                ? CommandUsage(command)
                : args is [var group, ..] && IsGroup(group) ? GroupUsage(group) : GeneralUsage();
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
        if (args is [var only] && (IsHelpOption(only) || only == "help"))
        {
            return GeneralUsage();
        }
        if (args is [var group, var option] && IsGroup(group) && IsHelpOption(option))
        {
            return GroupUsage(group);
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
            .AppendLine(CultureInfo.InvariantCulture, $"Run '{Executable} {VersionOption}' to print this installer's version and commit.")
            .ToString();
    }

    /// <summary>
    /// The usage of a command group (<c>cameraagent</c>, <c>catalog</c>, <c>host</c>): its own commands and what each does.
    /// </summary>
    internal static string GroupUsage(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var usage = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Usage: {Executable} {group} <command> [options]")
            .AppendLine()
            .AppendLine("Commands:");
        AppendRows(usage, Commands.Where(command => command.Path[0] == group).Select(static command => (Synopsis(command), command.Description)));
        return usage
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Run '{Executable} {group} <command> --help' to list the options of one command.")
            .ToString();
    }

    /// <summary>The usage of one command: what it does, then each option its parser admits and what it does.</summary>
    internal static string CommandUsage(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var usage = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Usage: {Executable} {Synopsis(command)} [options]")
            .AppendLine()
            .AppendLine(command.Description)
            .AppendLine()
            .AppendLine("Options:");
        AppendRows(usage, [
            .. command.ValueOptions.Concat(command.Flags).Select(option => (command.Name == "partial-prepare-cleanup" && option == "--dry-run" ? ((string? Value, string Description))(null, "Inspect recovery candidates and write private evidence without changing a target.") : OptionHelp[option]) switch
            {
                (null, var description) => (option, description),
                var (value, description) => ($"{option} {value}", description)
            }),
            ("-h, --help", "Show this help.")
        ]);
        if (command.Note is not null)
        {
            usage.AppendLine().AppendLine(command.Note);
        }
        return usage.ToString();
    }

    /// <summary>Each name and what it is, with the descriptions aligned in one column.</summary>
    private static void AppendRows(StringBuilder usage, IEnumerable<(string Name, string Description)> rows)
    {
        var listed = rows.ToArray();
        var width = listed.Max(static row => row.Name.Length);
        foreach (var (name, description) in listed)
        {
            usage.AppendLine(CultureInfo.InvariantCulture, $"  {name.PadRight(width)}  {description}");
        }
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
        [var first, var second, ..] when IsHelpOption(first) || first == "help" || first == VersionOption =>
            $"Unexpected argument '{second}' after '{first}'.",
        [var group] when IsGroup(group) => $"Missing command after '{group}'.",
        [var group, var name, ..] when IsGroup(group) && name.StartsWith('-') => $"Missing command after '{group}'.",
        [var group, var name, ..] when IsGroup(group) => $"Unknown command '{group} {name}'.",
        [var name, ..] => $"Unknown command '{name}'."
    };

    private static DeploymentCommand ParseOperation(string[] args, CommandLineContext context)
    {
        // Repair must reach the privileged lock before reading stale mount metadata. The requested root is only
        // an expectation: the helper re-reads the trusted policy under that lock and refuses any different root.
        if (args.Length >= 3 && args[0] == "host" && args[1] == "set-product-root" &&
            args.Skip(2).Contains("--repair", StringComparer.Ordinal))
        {
            return ParseHost(args, context, args[2]);
        }
        string production;
        try
        {
            production = HostProductRootPolicy.Resolve(context.HostPolicy).Path;
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            // No command runs under a host policy that cannot be trusted, and resolving it changed nothing.
            throw InstallerException.Refused(exception.Message, exception);
        }
        if (args.Length > 0 && args[0] == "partial-prepare-cleanup") return PartialPrepareRequest.Parse(args);
        var allowTestProductRoot = context.AllowTestProductRoot;
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "install")
        {
            return new InstallDeploymentCommand(Parse(args, allowTestProductRoot, production))
            {
                AllowTestProductRoot = allowTestProductRoot,
                ProductionProductRoot = production,
                ParseWarnings = args.Skip(2).Contains("--config", StringComparer.Ordinal) ? [ConfigDeprecation] : []
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
        var lifecycle = ParseLifecycle(args, allowTestProductRoot, production);
        return lifecycle.MigrationBackwardCompatible ? lifecycle with { ParseWarnings = [MigrationAssertionDeprecation] } : lifecycle;
    }

    /// <summary>
    /// <c>--config</c> still installs exactly as before; <c>--answers</c> replaces it with a versioned, strict file of the
    /// same members (#1315).
    /// </summary>
    internal static readonly InstallationWarning ConfigDeprecation = new(
        "option-deprecated",
        "--config is deprecated: use --answers FILE, which reads the same members with \"schemaVersion\": 1 and without dryRun, resume and json.");

    /// <summary>
    /// <c>--migration-backward-compatible</c> has had no effect since the swap-based upgrade (#1340): the upgrade
    /// decides from the candidate's declared state contract. It stays accepted, and part of the journaled request
    /// identity, so an interrupted upgrade begun with it still resumes.
    /// </summary>
    internal static readonly InstallationWarning MigrationAssertionDeprecation = new(
        "option-deprecated",
        "--migration-backward-compatible is deprecated and has no effect: the upgrade decides from the candidate's declared state contract and snapshots the small databases when it declares a migration. Leave it out; it is still accepted so an interrupted upgrade begun with it resumes.");

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
            throw new InstallUsageException($"Usage: {Executable} cameraagent install [options]");
        }

        string? configPath = null;
        var (values, flags) = ReadInstallOptions(args);

        if (values.Remove(AnswersOption, out var answersPath))
        {
            if (values.ContainsKey("--config"))
            {
                throw new InstallUsageException("--answers cannot be combined with --config.");
            }
            if (values.Keys.FirstOrDefault(option => !AnswersOverrideOptions.Contains(option, StringComparer.Ordinal)) is { } other)
            {
                throw new InstallUsageException(
                    $"--answers can be combined only with --cpus, --memory, --release, --image-source, --catalog-bundle and --no-download, not '{other}'.");
            }
        }
        if (values.Remove("--config", out configPath) && values.Count > 0)
        {
            throw new InstallUsageException("--config cannot be combined with value options.");
        }

        InstallRequest request;
        if (answersPath is not null)
        {
            // An option given beside the answers replaces the file's member: the limits, so a re-run changes an installed
            // instance's limits, and the release and catalog install.sh hands over (#1311).
            request = InstallAnswers.Read(answersPath).ToRequest(productionProductRoot);
            if (values.TryGetValue("--cpus", out var cpus))
            {
                request = request with { Cpus = ParseCpus(cpus) };
            }
            if (values.TryGetValue("--memory", out var memory))
            {
                request = request with { Memory = memory };
            }
            if (values.TryGetValue("--release", out var release))
            {
                request = request with { Release = release };
            }
            if (values.TryGetValue("--image-source", out var imageSource))
            {
                request = request with { ImageSource = ParseImageSource(imageSource) };
            }
            if (values.TryGetValue("--catalog-bundle", out var catalogBundle))
            {
                request = request with { CatalogBundle = catalogBundle };
            }
        }
        else if (configPath is not null)
        {
            byte[] configuration;
            try
            {
                using var stream = SafeFileSystem.OpenRegularFileRead(configPath);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                configuration = buffer.ToArray();
            }
            catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
            {
                // The file is the operator's input, so one that cannot be read is invalid input, as an answers file is.
                throw new InstallUsageException($"The installer configuration '{configPath}' could not be read: {exception.Message}", exception);
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
                FriendlyName = Require(values, "--friendly-name", "--friendly-name is required."),
                OwnerEmail = Get(values, "--owner-email") ?? OwnerCredentialDefaults.Email,
                BindAddress = Get(values, "--bind-address") ?? "127.0.0.1",
                Port = ParseInt(Get(values, "--port"), 5130, "--port"),
                ProductRoot = Get(values, "--product-root") ?? productionProductRoot,
                CatalogBundle = Get(values, "--catalog-bundle"),
                Release = Get(values, "--release"),
                ImageSource = ParseImageSource(Get(values, "--image-source")),
                ImageReference = Get(values, "--release") is null
                    ? Require(values, "--image-ref", "--release or --image-ref is required.")
                    : Get(values, "--image-ref") ?? string.Empty,
                ImageArchive = Get(values, "--image-archive"),
                ImageArchiveSha256 = Get(values, "--image-archive-sha256"),
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
            DryRun = flags.Contains(PlanOption) || flags.Contains("--dry-run") || request.DryRun,
            Resume = flags.Contains("--resume") || request.Resume,
            Json = flags.Contains("--json") || request.Json,
            AcknowledgePlaintextHttp = flags.Contains("--acknowledge-plaintext-http") || request.AcknowledgePlaintextHttp,
            GeneratePassword = flags.Contains("--generate-password") || request.GeneratePassword,
            NoDownload = flags.Contains("--no-download") || request.NoDownload
        };
        request.Validate();
        return request;
    }

    /// <summary>
    /// The options of <c>cameraagent install</c> after its two words, as given: each value option with its value, and the
    /// flags. A positional argument, a value option without a value, or an option twice is a usage error. The installer's
    /// wizard reads the same options to know which questions the command line already answered (#1317).
    /// </summary>
    internal static (Dictionary<string, string?> Values, HashSet<string> Flags) ReadInstallOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        return (values, flags);
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
        // --config and --answers never reach this check: each is removed first, and --config admits no other value
        // option while --answers admits only the limits.
        var unknown = options.FirstOrDefault(option => !InstallValueOptions.Contains(option, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new InstallUsageException($"Unknown option '{unknown}'.");
        }
    }

    /// <summary>
    /// A value the install cannot go without. It is never asked for here: on a terminal the wizard asks before this
    /// command line is parsed, and without one nothing is asked, so a missing value is a usage error naming it (#1317).
    /// </summary>
    private static string Require(IReadOnlyDictionary<string, string?> values, string option, string missing)
        => Get(values, option) ?? throw new InstallUsageException(missing);

    private static string? Get(IReadOnlyDictionary<string, string?> values, string option)
        => values.TryGetValue(option, out var value) ? value : null;

    internal static Guid? ParseGuid(string? value, string option)
        => value is null ? null : Guid.TryParseExact(value, "D", out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be a canonical UUID.");

    internal static int ParseInt(string? value, int fallback, string option)
        => value is null ? fallback : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be an integer.");

    internal static double ParseDouble(string? value, double fallback, string option)
        => value is null ? fallback : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be a number.");

    internal static decimal? ParseCpus(string? value)
        => value is null ? null : decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException("--cpus must be a number.");

    internal static ReleaseImageSource? ParseImageSource(string? value)
        => value switch
        {
            null => null,
            "archive" => ReleaseImageSource.Archive,
            "registry" => ReleaseImageSource.Registry,
            _ => throw new InstallUsageException("--image-source must be archive or registry.")
        };

    internal static CameraAgentReplayProfile ParseReplayProfile(string? value)
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
            PolicyLocation = context.HostPolicy,
            RepairMount = flags.Contains("--repair")
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
            ["cameraagent", "backup", ..] => (LifecycleOperationKind.Backup, 2),
            ["cameraagent", "restore", ..] => (LifecycleOperationKind.Restore, 2),
            ["catalog", "install", ..] => (LifecycleOperationKind.CatalogInstall, 2),
            ["catalog", "select", ..] => (LifecycleOperationKind.CatalogSelect, 2),
            ["catalog", "rollback", ..] => (LifecycleOperationKind.CatalogRollback, 2),
            ["catalog", "gc", ..] => (LifecycleOperationKind.CatalogGarbageCollect, 2),
            _ => throw new InstallUsageException(UnknownCommand(args))
        };
        var command = Find(args)!;
        string? archive = null;
        if (operation == LifecycleOperationKind.Restore && optionOffset < args.Length &&
            !args[optionOffset].StartsWith("--", StringComparison.Ordinal))
        {
            archive = args[optionOffset++];
        }
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionOffset; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }
            if (command.Flags.Contains(option, StringComparer.Ordinal))
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
        RejectUnknown(values, command.ValueOptions);
        var request = new LifecycleRequest(
            operation,
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? productionProductRoot,
            flags.Contains(PlanOption) || flags.Contains("--dry-run"),
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
            Release = Get(values, "--release"),
            ImageSource = ParseImageSource(Get(values, "--image-source")),
            NoDownload = flags.Contains("--no-download"),
            MigrationBackwardCompatible = flags.Contains("--migration-backward-compatible"),
            ConfirmationInstanceId = ParseGuid(Get(values, "--confirm-instance-id"), "--confirm-instance-id"),
            ConfirmRemovedOperationId = ParseGuid(
                Get(values, "--confirm-removed-operation-id"), "--confirm-removed-operation-id"),
            CatalogBundle = Get(values, "--catalog-bundle"),
            CatalogVersion = Get(values, "--catalog-version"),
            Archive = archive
        };
        if (archive is not null && archive.Contains('/', StringComparison.Ordinal))
        {
            // A path names the archive only where the backup folder keeps it.
            var folder = Path.Combine(request.ProductRoot, BackupVault.FolderName);
            if (!Path.IsPathFullyQualified(archive) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(archive)), folder, StringComparison.Ordinal))
            {
                throw new InstallUsageException($"restore reads a backup only from {folder}; give its name or its path there.");
            }
            request = request with { Archive = Path.GetFileName(archive) };
        }
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
            ProductionProductRoot = productionProductRoot
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
            flags.Contains(PlanOption) || flags.Contains("--dry-run"),
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
/// One command: the words that name it, the positional argument it requires if any, what it does, the options its parser
/// admits, and a note help prints after them.
/// </summary>
internal sealed record CommandDefinition(
    IReadOnlyList<string> Path,
    string? Positional,
    string Description,
    IReadOnlyList<string> ValueOptions,
    IReadOnlyList<string> Flags,
    string? Note = null)
{
    public string Name => string.Join(' ', Path);
}
