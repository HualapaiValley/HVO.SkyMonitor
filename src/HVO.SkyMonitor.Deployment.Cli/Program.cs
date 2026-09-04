using System.Text.Json;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Deployment;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "internal-prepare")
        {
            return PrivilegedPreparation.RunInternal(args);
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
        var jsonErrors = args.Contains("--json", StringComparer.Ordinal);
        try
        {
            var command = CommandLine.ParseCommand(args);
            // A --config install can select JSON output without a --json token, so the error stream mode follows
            // the parsed command once parsing succeeds; the argv guess only covers a parse failure.
            jsonErrors = command.Json;
            if (command is InstallDeploymentCommand install)
            {
                var result = await CameraAgentInstaller.InstallAsync(install.Request, cancellation.Token).ConfigureAwait(false);
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
                var report = await CameraAgentStatePreflightManager.ExecuteAsync(preflight, cancellation.Token)
                    .ConfigureAwait(false);
                await Console.Out.WriteLineAsync(preflight.Json
                    ? JsonSerializer.Serialize(report, DeploymentJsonContext.Default.CameraAgentStatePreflightReport)
                    : CameraAgentStatePreflight.Render(report)).ConfigureAwait(false);
                if (report.Compatible)
                {
                    return 0;
                }
                await WriteErrorAsync(
                    preflight.Json,
                    "state-incompatible",
                    "The persisted CameraAgent state is incompatible with the candidate image.").ConfigureAwait(false);
                return 1;
            }
            else if (command is CameraAgentStateResetRequest reset)
            {
                var result = await CameraAgentStateResetManager.ExecuteAsync(reset, cancellation.Token)
                    .ConfigureAwait(false);
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
            else if (command is OwnerRecoveryRequest recovery)
            {
                OwnerRecoveryResult result;
                try
                {
                    result = await OwnerRecoveryManager.ExecuteAsync(recovery, cancellation.Token).ConfigureAwait(false);
                }
                catch (OwnerRecoveryProtocolException)
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
                var lifecycle = await CameraAgentLifecycleManager.ExecuteAsync((LifecycleRequest)command, cancellation.Token)
                    .ConfigureAwait(false);
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
            return 0;
        }
        catch (InstallUsageException exception)
        {
            await WriteErrorAsync(jsonErrors, "usage", exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(jsonErrors, "canceled", "Deployment operation canceled.").ConfigureAwait(false);
            return 130;
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
            return 1;
        }
        catch (InstallerException exception)
        {
            await WriteErrorAsync(jsonErrors, "installation-failed", exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static Task WriteErrorAsync(bool json, string code, string message)
        => Console.Error.WriteLineAsync(json
            ? JsonSerializer.Serialize(new { schemaVersion = 1, errorCode = code, message = Redaction.SafeDiagnostic(message) })
            : $"Installation failed: {Redaction.SafeDiagnostic(message)}");

    private static async Task WriteHumanResultAsync(HVO.SkyMonitor.Deployment.Contracts.InstallationResult result)
    {
        await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Instance: {result.InstanceId:D} ({result.FriendlyName})").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"URL: {result.Url}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Owner: {result.OwnerEmail}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Temporary password file: {result.PasswordFile}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Instance root: {result.InstanceRoot}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Configuration: {result.ConfigurationSha256}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Rig profile: {result.RigProfileName} / {result.RigProfileVersion}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Schedule: {result.ScheduleState} / {result.ScheduleSchemaVersion}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Catalog: {result.Catalog.CatalogId} / {result.Catalog.PackageVersion}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Image: {result.Image.ImageId}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Runtime: {result.RuntimeUid}:{result.RuntimeGid}; alive={result.Alive}; healthy={result.Healthy}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"State: {result.OwnerBootstrapState}").ConfigureAwait(false);
    }

    private static async Task WriteLifecycleResultAsync(HVO.SkyMonitor.Deployment.Contracts.LifecycleResult result)
    {
        await Console.Out.WriteLineAsync($"Outcome: {result.Outcome}").ConfigureAwait(false);
        if (result.Operation is not null) await Console.Out.WriteLineAsync($"Operation: {result.Operation} / {result.OperationId}").ConfigureAwait(false);
        if (result.InstanceId is not null) await Console.Out.WriteLineAsync($"Instance: {result.InstanceId:D} / {result.LifecycleCondition}").ConfigureAwait(false);
        if (result.Image is not null) await Console.Out.WriteLineAsync($"Image: {result.Image.ImageId}").ConfigureAwait(false);
        if (result.Catalog is not null) await Console.Out.WriteLineAsync($"Catalog: {result.Catalog.CatalogId} / {result.Catalog.PackageVersion}").ConfigureAwait(false);
        if (result.Running is not null) await Console.Out.WriteLineAsync($"Runtime: running={result.Running}; healthy={result.Healthy}").ConfigureAwait(false);
        foreach (var path in result.PreservedPaths) await Console.Out.WriteLineAsync($"Preserved: {path}").ConfigureAwait(false);
        if (result.ResumeCommand is not null) await Console.Out.WriteLineAsync($"Resume: {result.ResumeCommand}").ConfigureAwait(false);
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
