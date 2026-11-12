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
            else if (args[i] is "--instance-id" or "--product-root")
            {
                var option = args[i];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InstallUsageException($"Option '{option}' requires a value.");
                if (!values.TryAdd(option, args[++i])) throw new InstallUsageException($"Duplicate option '{option}'.");
            }
            else throw new InstallUsageException($"Unknown option '{args[i]}'.");
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
    /// <summary>Read under deployment.lock before starting deployment work; never creates a journal while planning.</summary>
    internal static async Task EnsureControlSettledAsync(InstallationPaths paths, CancellationToken cancellationToken,
        LifecycleOperationKind? kind = null, DockerClient? docker = null)
    {
        var path = Path.Combine(paths.OperationsRoot, $"control-worker-{Guid.Parse(Path.GetFileName(paths.InstanceRoot)):D}.json");
        SafeFileSystem.EnsureSafeExistingAncestors(path);
        if (!File.Exists(path) && new FileInfo(path).LinkTarget is null) return;
        var journal = new InstallerControlJournal(path);
        await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!journal.Receipts.Any(item => !item.IsTerminal) && !journal.Unpublished.Any() && journal.Refused.Count == 0)
            return;
        // Keep definite refusals quarantined. Only transport-blocked evidence can admit an explicit recovery;
        // neither this read-only gate nor the recovery discards the worker's journal.
        if (journal.Refused.Count == 0 && docker is not null && kind is
            (LifecycleOperationKind.Reinstall or LifecycleOperationKind.Uninstall or LifecycleOperationKind.Restore or LifecycleOperationKind.Rollback))
        {
            var runtime = await docker.InspectContainerAsync(
                $"hvo-skymonitor-{Guid.Parse(Path.GetFileName(paths.InstanceRoot)):N}", cancellationToken).ConfigureAwait(false);
            if (!runtime.Exists || !runtime.Running || runtime.LivenessStatus == "unhealthy") return;
        }
        throw InstallerException.Refused("Settle installer runtime control with lifecycle-worker while CameraAgent is reachable. " +
            "If CameraAgent is down, use uninstall then restore/reinstall, or rollback subject to its recovery preconditions. " +
            "Retain both journals and refused evidence for reconciliation; do not edit them.");
    }

    internal static string FormatControlWorkerDiagnostic(string message) => CameraAgentStateDiagnostics.Message(message);

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
        async Task<IDisposable> AcquireDeploymentAsync(CancellationToken token)
        {
            var deploymentLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), TimeSpan.Zero, token);
            try
            {
                var operation = await ReadOperationAsync(paths.LifecycleStatePath, token).ConfigureAwait(false);
                if (operation is { Status: not InstallationStatus.Completed } && !IsRefused(operation) && !IsRestored(operation))
                    throw InstallerException.Refused("An installer lifecycle operation must be recovered before runtime control.");
                return deploymentLock;
            }
            catch { deploymentLock.Dispose(); throw; }
        }
        await new InstallerControlWorker(client, journal, AcquireDeploymentAsync, TimeProvider.System,
            message => Console.Error.WriteLine(FormatControlWorkerDiagnostic(message))).RunAsync(request.Once, cancellationToken).ConfigureAwait(false);
    }
}
