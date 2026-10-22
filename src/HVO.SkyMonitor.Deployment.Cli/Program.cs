using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Wizard;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The installer's exit codes (install contract section 5). Every command ends with one of these, and only
/// <see cref="Program.RunAsync"/> chooses which.
/// </summary>
internal enum InstallerExitCode
{
    /// <summary>Done; with <c>--plan</c>, the plan can be carried out.</summary>
    Done = 0,

    /// <summary>A step failed. Re-running the same command continues (with <c>--resume</c> where it says so).</summary>
    Failed = 1,

    /// <summary>The command line or the answers file is invalid, or a question had no one to answer it.</summary>
    Usage = 2,

    /// <summary>The installer refused, and nothing was changed.</summary>
    Refused = 3,

    /// <summary>Interrupted, by Ctrl+C or SIGTERM.</summary>
    Interrupted = 130
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == PrivilegedPathInstaller.CommandName)
        {
            return PrivilegedPathInstaller.RunInternal(args);
        }
        if (args.Length > 0 && args[0] == "internal-prepare")
        {
            return PrivilegedPreparation.RunInternal(args);
        }
        if (args.Length > 0 && args[0] == PrivilegedPurgeNamespace.CommandName)
        {
            return PrivilegedPurgeNamespace.RunInternal(args);
        }
        if (args.Length > 0 && args[0] == PrivilegedHostPolicy.CommandName)
        {
            return PrivilegedHostPolicy.RunInternal(args);
        }
        if (args.Length > 0 && args[0] == InstallRecordStore.CommandName)
        {
            return InstallRecordStore.RunInternal(args);
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        return await RunAsync(args, ProgramServices.Production, cancellation.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses and runs one command and returns its exit code. The services are the real ones in production; a test
    /// substitutes them to pin the exit code of every command without a Docker host.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every command must end with a contract exit code, so an unforeseen failure is reported as a failed step (1) with a redacted message rather than an unhandled-exception crash.")]
    internal static async Task<int> RunAsync(string[] args, ProgramServices services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        var jsonErrors = args.Contains("--json", StringComparer.Ordinal);
        try
        {
            // On a terminal, an install whose command line leaves a question unanswered runs the wizard, which asks it and
            // then runs this same path with --answers (install contract section 5, #1317). Anywhere else nothing is asked.
            if (services.Wizard is { } wizard && InstallWizardGate.Wants(args, services.Terminal))
            {
                return await wizard(args, services, cancellationToken).ConfigureAwait(false);
            }
            var command = services.Parse(args);
            // A --config or --answers install can select JSON output without a --json token, so the error stream mode
            // follows the parsed command once parsing succeeds; the argv guess only covers a parse failure.
            jsonErrors = command.Json;
            if (command is HelpCommand help)
            {
                await Console.Out.WriteAsync(help.Usage).ConfigureAwait(false);
                return (int)InstallerExitCode.Done;
            }
            if (command is VersionCommand version)
            {
                await Console.Out.WriteLineAsync(version.Identity).ConfigureAwait(false);
                return (int)InstallerExitCode.Done;
            }
            // Interrupted before it began: nothing ran, and the command exits as interrupted, whichever it is.
            cancellationToken.ThrowIfCancellationRequested();
            if (!command.Json)
            {
                // Without --json an advisory is said before anything runs; with --json it is in the result instead.
                foreach (var warning in command.ParseWarnings)
                {
                    await Console.Error.WriteLineAsync($"Warning: {CameraAgentStateDiagnostics.Message(warning.Message)}").ConfigureAwait(false);
                }
            }
            if (command is InstallDeploymentCommand install)
            {
                var result = await services.Install(install.Request, cancellationToken).ConfigureAwait(false);
                if (install.ParseWarnings.Count > 0)
                {
                    result = result with { Warnings = [.. install.ParseWarnings, .. result.Warnings ?? []] };
                }
                if (install.Json)
                {
                    await Console.Out.WriteLineAsync(
                        JsonSerializer.Serialize(result, DeploymentJsonContext.Default.InstallationResult)).ConfigureAwait(false);
                }
                else
                {
                    await WriteHumanResultAsync(result).ConfigureAwait(false);
                }
            }
            else if (command is CameraAgentStatePreflightRequest preflight)
            {
                var report = await services.Preflight(preflight, cancellationToken).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(preflight.Json
                    ? JsonSerializer.Serialize(CameraAgentStatePreflight.ForOutput(report), DeploymentJsonContext.Default.CameraAgentStatePreflightReport)
                    : CameraAgentStatePreflight.Render(report)).ConfigureAwait(false);
                if (report.Compatible)
                {
                    return (int)InstallerExitCode.Done;
                }
                // A read-only check that finds the state incompatible refuses the candidate; it changed nothing.
                await WriteErrorAsync(
                    preflight.Json,
                    "state-incompatible",
                    "The persisted CameraAgent state is incompatible with the candidate image.",
                    refused: true).ConfigureAwait(false);
                return (int)InstallerExitCode.Refused;
            }
            else if (command is CameraAgentStateResetRequest reset)
            {
                var result = await services.Reset(reset, cancellationToken).ConfigureAwait(false);
                if (reset.Json)
                {
                    await Console.Out.WriteLineAsync(
                        JsonSerializer.Serialize(result, DeploymentJsonContext.Default.CameraAgentStateResetResult))
                        .ConfigureAwait(false);
                }
                else
                {
                    await WriteStateResetResultAsync(result).ConfigureAwait(false);
                }
            }
            else if (command is HostProductRootRequest host)
            {
                var result = await services.HostPolicy(host, cancellationToken).ConfigureAwait(false);
                if (host.Json)
                {
                    await Console.Out.WriteLineAsync(
                        JsonSerializer.Serialize(result, DeploymentJsonContext.Default.HostPolicyResult)).ConfigureAwait(false);
                }
                else
                {
                    await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
                    await Console.Out.WriteLineAsync($"Policy: {result.PolicyPath}").ConfigureAwait(false);
                    await Console.Out.WriteLineAsync($"Previous product root: {result.PreviousProductRoot}").ConfigureAwait(false);
                    await Console.Out.WriteLineAsync($"Product root: {result.ProductRoot}").ConfigureAwait(false);
                }
            }
            else if (command is OwnerRecoveryRequest recovery)
            {
                OwnerRecoveryResult result;
                try
                {
                    result = await services.OwnerRecovery(recovery, cancellationToken).ConfigureAwait(false);
                }
                catch (OwnerRecoveryProtocolException)
                {
                    throw;
                }
                catch (InstallerException exception) when (exception.IsRefusal)
                {
                    throw;
                }
                catch (InstallerException exception)
                {
                    throw new InstallerException(
                        "Owner recovery failed. Inspect the owner-only deployment state and retry as directed.",
                        exception);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new InstallerException(
                        "Owner recovery failed. Inspect the owner-only deployment state and retry as directed.",
                        exception);
                }
                if (recovery.Json)
                {
                    await Console.Out.WriteLineAsync(
                        JsonSerializer.Serialize(result, DeploymentJsonContext.Default.OwnerRecoveryResult)).ConfigureAwait(false);
                }
                else
                {
                    await WriteOwnerRecoveryResultAsync(result).ConfigureAwait(false);
                }
            }
            else
            {
                var lifecycle = await services.Lifecycle((LifecycleRequest)command, cancellationToken).ConfigureAwait(false);
                if (command.ParseWarnings.Count > 0)
                {
                    lifecycle = lifecycle with { Warnings = [.. command.ParseWarnings, .. lifecycle.Warnings ?? []] };
                }
                if (command.Json)
                {
                    await Console.Out.WriteLineAsync(
                        JsonSerializer.Serialize(lifecycle, DeploymentJsonContext.Default.LifecycleResult)).ConfigureAwait(false);
                }
                else
                {
                    await WriteLifecycleResultAsync(lifecycle).ConfigureAwait(false);
                }
            }
            return (int)InstallerExitCode.Done;
        }
        catch (InstallUsageException exception) when (exception.Usage is not null && !jsonErrors)
        {
            // A command-line mistake is not an installation attempt: say what was wrong, then how to use the command.
            await Console.Error.WriteLineAsync(CameraAgentStateDiagnostics.Message(exception.Message)).ConfigureAwait(false);
            await Console.Error.WriteAsync(exception.Usage).ConfigureAwait(false);
            return (int)InstallerExitCode.Usage;
        }
        catch (InstallUsageException exception)
        {
            await WriteErrorAsync(jsonErrors, "usage", exception.Message).ConfigureAwait(false);
            return (int)InstallerExitCode.Usage;
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(jsonErrors, "canceled", "Deployment operation canceled.").ConfigureAwait(false);
            return (int)InstallerExitCode.Interrupted;
        }
        catch (HostPrerequisiteException exception)
        {
            // Every finding and its remedy is reported at once: as the structured report on standard output for
            // --json, whose standard error carries only one-line JSON objects, and rendered on standard error
            // otherwise. The prerequisites are evaluated before anything is created, so this is a refusal.
            if (exception.Report is { } report)
            {
                if (jsonErrors)
                {
                    await Console.Out.WriteLineAsync(
                        JsonSerializer.Serialize(HostPrerequisites.ForOutput(report), DeploymentJsonContext.Default.HostPrerequisiteReport)).ConfigureAwait(false);
                }
                else
                {
                    await Console.Error.WriteLineAsync(HostPrerequisites.Render(report)).ConfigureAwait(false);
                }
            }
            await WriteErrorAsync(jsonErrors, "host-prerequisites", exception.Message, refused: true).ConfigureAwait(false);
            return (int)InstallerExitCode.Refused;
        }
        catch (OwnerRecoveryProtocolException exception)
        {
            var (code, message) = exception.Disposition switch
            {
                OwnerRecoveryFailureDisposition.FreshOperationRequired => (
                    "fresh-operation-required",
                    "CameraAgent rejected owner recovery before completion; start a new recovery operation."),
                OwnerRecoveryFailureDisposition.ResumeRequired => (
                    "resume-required",
                    "CameraAgent owner recovery may be incomplete; resume the retained operation."),
                _ => (
                    "recovery-unsupported",
                    "CameraAgent owner recovery is unsupported or is not enabled for this installation.")
            };
            await WriteErrorAsync(jsonErrors, code, message).ConfigureAwait(false);
            return (int)InstallerExitCode.Failed;
        }
        catch (InstallerException exception) when (exception.IsRefusal)
        {
            await WriteErrorAsync(jsonErrors, "refused", exception.Message, refused: true, formatted: exception.IsFormattedDiagnostic)
                .ConfigureAwait(false);
            return (int)InstallerExitCode.Refused;
        }
        catch (InstallerException exception)
        {
            await WriteErrorAsync(jsonErrors, "installation-failed", exception.Message, formatted: exception.IsFormattedDiagnostic)
                .ConfigureAwait(false);
            return (int)InstallerExitCode.Failed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await WriteErrorAsync(jsonErrors, "installation-failed", $"Unexpected {exception.GetType().Name}: {exception.Message}")
                .ConfigureAwait(false);
            return (int)InstallerExitCode.Failed;
        }
    }

    /// <summary>
    /// Writes the error line. A message the shared formatter composed (<see cref="InstallerException.IsFormattedDiagnostic"/>)
    /// is already redacted and escaped, so it is written as it is: scanning it again could redact the fixed wording after a
    /// hostile name in it (#1491). Every other message is redacted and its remaining controls escaped here (#1499).
    /// </summary>
    private static Task WriteErrorAsync(bool json, string code, string message, bool refused = false, bool formatted = false)
    {
        var safe = formatted ? message : CameraAgentStateDiagnostics.Message(message);
        return Console.Error.WriteLineAsync(json
            ? JsonSerializer.Serialize(new { schemaVersion = 1, errorCode = code, message = safe })
            : refused
                ? $"Installation refused: {safe} Nothing was changed."
                : $"Installation failed: {safe}");
    }

    private static async Task WriteHumanResultAsync(InstallationResult result)
    {
        await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Instance: {result.InstanceId:D} ({result.FriendlyName})").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"URL: {result.Url}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Owner: {result.OwnerEmail}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync(OwnerSignInInstruction(result)).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Instance root: {result.InstanceRoot}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Configuration: {result.ConfigurationSha256}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Rig profile: {result.RigProfileName} / {result.RigProfileVersion}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Schedule: {result.ScheduleState} / {result.ScheduleSchemaVersion}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Catalog: {result.Catalog.CatalogId} / {result.Catalog.PackageVersion}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Image: {result.Image.ImageId}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Runtime: {result.RuntimeUid}:{result.RuntimeGid}; alive={result.Alive}; healthy={result.Healthy}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"State: {result.OwnerBootstrapState}").ConfigureAwait(false);
        await WritePlanAsync(result.Plan, result.Outcome == InstallationOutcome.Planned).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the operator how to sign in for the first time. The documented default is named outright because it is
    /// public by design; any other temporary password is only ever referred to by its owner-only file.
    /// </summary>
    internal static string OwnerSignInInstruction(InstallationResult result)
        => result.OwnerPasswordIsDefault
            ? $"Sign in as {OwnerCredentialDefaults.UserName} / {OwnerCredentialDefaults.Password}; a password change is required at first sign-in."
            : $"Temporary password file: {result.PasswordFile}";

    private static async Task WriteLifecycleResultAsync(LifecycleResult result)
    {
        await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
        if (result.Operation is not null) await Console.Out.WriteLineAsync($"Operation: {result.Operation} / {result.OperationId}").ConfigureAwait(false);
        if (result.InstanceId is not null) await Console.Out.WriteLineAsync($"Instance: {result.InstanceId:D} / {result.LifecycleCondition}").ConfigureAwait(false);
        if (result.Image is not null) await Console.Out.WriteLineAsync($"Image: {result.Image.ImageId}").ConfigureAwait(false);
        if (result.Catalog is not null) await Console.Out.WriteLineAsync($"Catalog: {result.Catalog.CatalogId} / {result.Catalog.PackageVersion}").ConfigureAwait(false);
        if (result.Running is not null) await Console.Out.WriteLineAsync($"Runtime: running={result.Running}; healthy={result.Healthy}").ConfigureAwait(false);
        foreach (var path in result.PreservedPaths) await Console.Out.WriteLineAsync($"Preserved: {path}").ConfigureAwait(false);
        if (result.ResumeCommand is not null) await Console.Out.WriteLineAsync($"Resume: {result.ResumeCommand}").ConfigureAwait(false);
        await WritePlanAsync(result.Plan, result.Outcome == "planned").ConfigureAwait(false);
    }

    /// <summary>
    /// The plan as an operator reads it: each item's action, kind, target and detail, then the summary, which a re-run
    /// that changes nothing ends with <see cref="DeploymentPlan.NothingToChange"/>.
    /// </summary>
    private static async Task WritePlanAsync(DeploymentPlan? plan, bool planned)
    {
        if (plan is null)
        {
            return;
        }
        await Console.Out.WriteLineAsync(planned ? "Plan:" : "Changes:").ConfigureAwait(false);
        foreach (var line in DeploymentPlanText.Lines(plan))
        {
            await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static async Task WriteStateResetResultAsync(CameraAgentStateResetResult result)
    {
        await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Operation: cameraagent state reset / {result.OperationId:D}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Instance: {result.InstanceId:D}").ConfigureAwait(false);
        foreach (var path in result.DeletedPaths)
        {
            await Console.Out.WriteLineAsync($"Destructive: {path}").ConfigureAwait(false);
        }
        foreach (var path in result.PreservedPaths)
        {
            await Console.Out.WriteLineAsync($"Preserved: {path}").ConfigureAwait(false);
        }
        await Console.Out.WriteLineAsync($"Evidence: {result.EvidencePath}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Next: {result.NextCommand}").ConfigureAwait(false);
    }

    private static async Task WriteOwnerRecoveryResultAsync(OwnerRecoveryResult result)
    {
        await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Operation: owner recovery / {result.OperationId:D}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Instance: {result.InstanceId:D}").ConfigureAwait(false);
        if (result.PasswordFile is not null)
        {
            await Console.Out.WriteLineAsync($"Temporary password file: {result.PasswordFile}").ConfigureAwait(false);
        }
        await Console.Out.WriteLineAsync($"State: {result.OwnerBootstrapState}").ConfigureAwait(false);
    }
}

/// <summary>
/// What <see cref="Program.RunAsync"/> parses with and runs each command through. Production uses the real parser and
/// managers; a test substitutes them so every command's exit code is pinned without a Docker host.
/// </summary>
internal sealed record ProgramServices(
    Func<string[], DeploymentCommand> Parse,
    Func<InstallRequest, CancellationToken, Task<InstallationResult>> Install,
    Func<CameraAgentStatePreflightRequest, CancellationToken, Task<CameraAgentStatePreflightReport>> Preflight,
    Func<CameraAgentStateResetRequest, CancellationToken, Task<CameraAgentStateResetResult>> Reset,
    Func<HostProductRootRequest, CancellationToken, Task<HostPolicyResult>> HostPolicy,
    Func<OwnerRecoveryRequest, CancellationToken, Task<OwnerRecoveryResult>> OwnerRecovery,
    Func<LifecycleRequest, CancellationToken, Task<LifecycleResult>> Lifecycle)
{
    /// <summary>The terminal, if any: the wizard runs only on one (#1317).</summary>
    public InstallTerminal Terminal { get; init; } = InstallTerminal.Current();

    /// <summary>
    /// The installer's wizard, which <see cref="Program.RunAsync"/> hands an install to when <see cref="InstallWizardGate"/>
    /// wants it; null where it must not run, as on the path the wizard itself takes.
    /// </summary>
    public Func<string[], ProgramServices, CancellationToken, Task<int>>? Wizard { get; init; } = InstallWizardRunner.RunAsync;

    public static ProgramServices Production { get; } = new(
        CommandLine.ParseCommand,
        CameraAgentInstaller.InstallAsync,
        CameraAgentStatePreflightManager.ExecuteAsync,
        CameraAgentStateResetManager.ExecuteAsync,
        PrivilegedHostPolicy.SetProductRootAsync,
        OwnerRecoveryManager.ExecuteAsync,
        CameraAgentLifecycleManager.ExecuteAsync);
}

/// <summary>How a <see cref="DeploymentPlan"/> reads in the human output.</summary>
internal static class DeploymentPlanText
{
    public static IReadOnlyList<string> Lines(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>(plan.Items.Count + 1);
        foreach (var item in plan.Items)
        {
            var detail = item.Detail is null ? string.Empty : $" ({item.Detail})";
            lines.Add($"  {Word(item.Action),-9}  {item.Kind,-9}  {item.Target}{detail}");
        }
        lines.Add(plan.Summary);
        return lines;
    }

    public static string Word(DeploymentPlanAction action) => action switch
    {
        DeploymentPlanAction.Create => "create",
        DeploymentPlanAction.Change => "change",
        DeploymentPlanAction.Unchanged => "unchanged",
        DeploymentPlanAction.Remove => "remove",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };
}
