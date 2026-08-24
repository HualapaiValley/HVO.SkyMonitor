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
            var request = CommandLine.Parse(args);
            var result = await CameraAgentInstaller.InstallAsync(request, cancellation.Token).ConfigureAwait(false);
            if (request.Json)
            {
                await Console.Out.WriteLineAsync(
                    JsonSerializer.Serialize(result, DeploymentJsonContext.Default.InstallationResult)).ConfigureAwait(false);
            }
            else
            {
                await WriteHumanResultAsync(result).ConfigureAwait(false);
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
            await WriteErrorAsync(jsonErrors, "canceled", "Installation canceled.").ConfigureAwait(false);
            return 130;
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
}
