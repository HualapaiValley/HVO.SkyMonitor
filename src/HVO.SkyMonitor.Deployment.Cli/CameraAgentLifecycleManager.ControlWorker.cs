using System.Net;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record LifecycleWorkerRequest(Guid InstanceId, string ProductRoot, bool Once, bool Json) : DeploymentCommand(Json)
{
    internal void Validate()
    {
        if (InstanceId == Guid.Empty) throw new InstallUsageException("--instance-id is required.");
        ProductRootAdmission.Validate(ProductRoot, ProductionProductRoot, AllowTestProductRoot);
    }

    internal static LifecycleWorkerRequest Parse(string[] args, string production, bool allowTest)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] is "--once" or "--json")
            {
                if (!flags.Add(args[i])) throw new InstallUsageException("Duplicate lifecycle-worker flag.");
            }
            else if (args[i] is "--instance-id" or "--product-root" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (!values.TryAdd(args[i], args[++i])) throw new InstallUsageException("Duplicate lifecycle-worker option.");
            }
            else throw new InstallUsageException("Unsupported lifecycle-worker option.");
        }
        if (!Guid.TryParse(values.GetValueOrDefault("--instance-id"), out var id)) throw new InstallUsageException("--instance-id is required.");
        var result = new LifecycleWorkerRequest(id, values.GetValueOrDefault("--product-root") ?? production,
            flags.Contains("--once"), flags.Contains("--json"))
        { AllowTestProductRoot = allowTest, ProductionProductRoot = production };
        result.Validate();
        return result;
    }
}

internal sealed partial class CameraAgentLifecycleManager
{
    /// <summary>Explicitly started by the operator, never spawned by the web host or by installation.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Both operation locks are using declarations spanning the worker or one entire verified lifecycle.")]
    internal static async Task RunControlWorkerAsync(LifecycleWorkerRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var paths = InstallationPaths.Create(request.ProductRoot, request.InstanceId, ProductionCatalog.DefaultCatalogId);
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        if (NativeLinux.getuid() == 0 || manifest.RuntimeUid != NativeLinux.getuid() || manifest.RuntimeGid != NativeLinux.getgid())
            throw InstallerException.Refused("Run the lifecycle worker as the recorded runtime account, never as root.");
        EnsureCorrelated(paths, request.InstanceId, manifest, result, allowTransactionalDrift: false);
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw InstallerException.Refused("The lifecycle worker requires an installed instance.");
        var token = await ReadLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
        ValidateLifecycleControlToken(manifest, token);
        SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
        using var workerLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, $"control-worker-{request.InstanceId:D}.lock"),
            TimeSpan.Zero, cancellationToken);
        var journal = new InstallerControlJournal(Path.Combine(paths.OperationsRoot, $"control-worker-{request.InstanceId:D}.json"));
        await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var workerId = journal.WorkerId;
        // The installation manifest owns the local listener; neither UI nor automation supplies an address.
        var address = IPAddress.TryParse(manifest.BindAddress, out var binding) && IPAddress.Any.Equals(binding)
            ? new Uri($"http://127.0.0.1:{manifest.Port}") : result.Url;
        using var client = new InstallerControlHttpClient(address, token, workerId);
        // An interrupted installer never reissues an ambiguously acknowledged pause/restart. Settle its retained
        // operation as failed, preserve capture state, and deliver the actual failure when CameraAgent is reachable.
        await journal.RecoverInterruptedAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var receipt in journal.Unpublished.ToArray())
                {
                    await client.ExchangeAsync(receipt, cancellationToken).ConfigureAwait(false);
                    await journal.AcknowledgeAsync(receipt.Request.OperationId).ConfigureAwait(false);
                }
                var snapshot = await client.ExchangeAsync(null, cancellationToken).ConfigureAwait(false);
                if (snapshot.Current is { } current)
                {
                    var retained = journal.Receipts.FirstOrDefault(item => item.Request.OperationId == current.Request.OperationId);
                    if (retained is { IsTerminal: true })
                        await client.ExchangeAsync(retained, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        try
                        {
                            using var deploymentLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"),
                                TimeSpan.Zero, cancellationToken);
                            var operation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
                            if (operation is { Status: not InstallationStatus.Completed } && !IsRefused(operation) && !IsRestored(operation))
                                throw InstallerException.Refused("An installer lifecycle operation must be recovered before runtime control.");
                            var verifier = new InstallerControlVerifier(client, journal.SaveAsync, TimeProvider.System);
                            await verifier.ExecuteAsync(current, cancellationToken).ConfigureAwait(false);
                        }
                        catch (InstallerException)
                        {
                            var failed = current with
                            {
                                Phase = CameraAgentControlPhase.Failed,
                                Events = [.. current.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "lifecycle.installer-busy")]
                            };
                            await journal.SaveAsync(failed).ConfigureAwait(false);
                            await client.ExchangeAsync(failed, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (HttpRequestException) when (!request.Once) { /* Heartbeat expires; durable evidence remains queued. */ }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && !request.Once) { }
            if (request.Once) return;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        } while (true);
    }
}
