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
            if (!runtime.Exists || !runtime.Running) return;
            if (runtime.LivenessStatus == "unhealthy" &&
                !await docker.IsContainerAliveNowAsync($"hvo-skymonitor-{Guid.Parse(Path.GetFileName(paths.InstanceRoot)):N}", cancellationToken)
                    .ConfigureAwait(false)) return;
        }
        throw InstallerException.Refused("Settle installer runtime control with lifecycle-worker while CameraAgent is reachable. " +
            "If CameraAgent is down, use uninstall then restore/reinstall, or rollback subject to its recovery preconditions. " +
            "Retain both journals and refused evidence for reconciliation; do not edit them.");
    }

    /// <summary>Only settlement of retained receipts may enter an incomplete recovery.</summary>
    internal static async Task<IDisposable> AcquireControlDeploymentAsync(InstallationPaths paths, CancellationToken cancellationToken,
        bool settleCommittedRecovery = false,
        Func<CancellationToken, Task<bool>>? returnedProcess = null, bool requireRecovery = false,
        Func<CancellationToken, Task<InstanceLifecycleCondition>>? readCondition = null)
    {
        var deploymentLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), TimeSpan.Zero, cancellationToken);
        try
        {
            if (readCondition is not null)
            {
                var condition = await readCondition(cancellationToken).ConfigureAwait(false);
                settleCommittedRecovery = condition == InstanceLifecycleCondition.Installed;
                requireRecovery = !settleCommittedRecovery;
            }
            var operation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
            if (requireRecovery && operation is not
                { Status: not InstallationStatus.Completed, Kind: LifecycleOperationKind.Reinstall or LifecycleOperationKind.Rollback })
                throw InstallerException.Refused("An uninstalled instance requires an incomplete reinstall or rollback for settlement.");
            // Already admissible local settlement must not depend on an unnecessary process probe.
            if (operation is null || operation.Status == InstallationStatus.Completed || IsRefused(operation) || IsRestored(operation))
                return deploymentLock;
            var committedRecovery = settleCommittedRecovery && operation is
            {
                Status: InstallationStatus.Running, Phase: LifecycleOperationPhase.Committed, MutationStarted: true,
                Kind: LifecycleOperationKind.Reinstall or LifecycleOperationKind.Rollback,
                PostMutationContinuity: { CaptureState: "Paused" }
            } && operation.InstanceId == Guid.Parse(Path.GetFileName(paths.InstanceRoot)) &&
                IsValidRecoveryBoundary(operation.PostMutationContinuity);
            // A different process prevents reuse of the old process's resume authority. Its durable
            // pause must also be proven: a crash before the original pause can leave Running capture
            // fenced only by the receipt, and terminal publication would otherwise reopen admission.
            var precommitRecovery = !committedRecovery && returnedProcess is not null && operation is
            {
                Status: not InstallationStatus.Completed,
                Kind: LifecycleOperationKind.Reinstall or LifecycleOperationKind.Rollback
            } && operation.InstanceId == Guid.Parse(Path.GetFileName(paths.InstanceRoot)) &&
                await returnedProcess(cancellationToken).ConfigureAwait(false);
            if (operation is { Status: not InstallationStatus.Completed } && !IsRefused(operation) && !IsRestored(operation) && !committedRecovery && !precommitRecovery)
                throw InstallerException.Refused("An installer lifecycle operation must be recovered before runtime control.");
            return deploymentLock;
        }
        catch { deploymentLock.Dispose(); throw; }
    }

    internal static string FormatControlWorkerDiagnostic(string message) => CameraAgentStateDiagnostics.Message(message);

    /// <summary>Explicitly started by the operator, never spawned by the web host or by installation.</summary>
    internal static Task RunControlWorkerAsync(LifecycleWorkerRequest request, CancellationToken cancellationToken) =>
        RunControlWorkerAsync(request, handler: null, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Both operation locks are using declarations spanning the worker or one entire verified lifecycle.")]
    internal static async Task RunControlWorkerAsync(LifecycleWorkerRequest request, HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var paths = InstallationPaths.Create(request.ProductRoot, request.InstanceId, ProductionCatalog.DefaultCatalogId);
        InstanceManifest manifest;
        InstallationResult result;
        using (OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), TimeSpan.Zero, cancellationToken))
            (manifest, result) = await ReadWorkerIdentityAsync(paths, request.InstanceId, cancellationToken).ConfigureAwait(false);
        if (NativeLinux.getuid() == 0 || manifest.RuntimeUid != NativeLinux.getuid() || manifest.RuntimeGid != NativeLinux.getgid())
            throw InstallerException.Refused("Run the lifecycle worker as the recorded runtime account, never as root.");
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
        using var client = new InstallerControlHttpClient(address, token, workerId, handler);
        Task<InstanceLifecycleCondition> ReadConditionAsync(CancellationToken cancellation) => ReadConditionCoreAsync(false, cancellation);
        Task<InstanceLifecycleCondition> ReadRecoveryConditionAsync(CancellationToken cancellation) => ReadConditionCoreAsync(true, cancellation);
        async Task<InstanceLifecycleCondition> ReadConditionCoreAsync(bool allowPartialRollback, CancellationToken cancellation)
        {
            var (current, _) = await ReadWorkerIdentityAsync(paths, request.InstanceId, cancellation, allowPartialRollback).ConfigureAwait(false);
            ValidateLifecycleControlToken(current, token);
            if (current.InstallationId != manifest.InstallationId || current.ApplicationIdentity != manifest.ApplicationIdentity || current.RuntimeUid != manifest.RuntimeUid ||
                current.RuntimeGid != manifest.RuntimeGid || current.BindAddress != manifest.BindAddress || current.Port != manifest.Port)
                throw InstallerException.Refused("Restart the lifecycle worker after its runtime identity or listener changes.");
            return current.LifecycleCondition;
        }
        async Task<IDisposable> AcquireDeploymentAsync(CancellationToken cancellation)
        {
            var lease = await AcquireControlDeploymentAsync(paths, cancellation).ConfigureAwait(false);
            try
            {
                if (await ReadConditionAsync(cancellation).ConfigureAwait(false) != InstanceLifecycleCondition.Installed)
                    throw InstallerException.Refused("New runtime control requires an installed instance.");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        await new InstallerControlWorker(client, journal, AcquireDeploymentAsync, TimeProvider.System,
            message => Console.Error.WriteLine(FormatControlWorkerDiagnostic(message)),
            acquireInterruptedRecovery: (receipt, token) => AcquireInterruptedControlAsync(paths, manifest.LifecycleCondition,
                receipt, client, token, ReadRecoveryConditionAsync))
            .RunAsync(request.Once, cancellationToken).ConfigureAwait(false);
    }

    // The caller holds deployment.lock so manifest/result publication cannot be observed halfway through a live write.
    private static async Task<(InstanceManifest Manifest, InstallationResult Result)> ReadWorkerIdentityAsync(
        InstallationPaths paths, Guid instanceId, CancellationToken cancellationToken, bool allowPartialRollback = false)
    {
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        try { EnsureCorrelated(paths, instanceId, manifest, result, allowTransactionalDrift: false); }
        catch (InstallerException)
        {
            var operation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
            if (operation is
                {
                    Kind: LifecycleOperationKind.Rollback, Phase: LifecycleOperationPhase.CandidateVerified,
                    MutationStarted: true, Status: not InstallationStatus.Completed
                } && operation.InstanceId == instanceId)
            {
                EnsureCorrelated(paths, instanceId, manifest, result, allowTransactionalDrift: true);
                EnsureTransactionalCorrelation(operation, manifest, result);
                // A running worker may settle its retained receipt after a safe replacement probe.
                // Startup and fresh work still refuse this partial publication explicitly.
                if (!allowPartialRollback)
                    throw InstallerException.Refused("lifecycle.rollback-partial-commit: rollback manifest/result publication was interrupted. " +
                        "Runtime control remains fenced. Retain both journals and escalate for transaction-aware recovery; do not edit records.");
            }
            else throw;
        }
        if (manifest.LifecycleCondition is not (InstanceLifecycleCondition.Installed or InstanceLifecycleCondition.Uninstalled))
            throw InstallerException.Refused("The lifecycle worker requires a retained instance.");
        return (manifest, result);
    }

    internal static Task<IDisposable> AcquireInterruptedControlAsync(InstallationPaths paths,
        InstanceLifecycleCondition condition, CameraAgentControlReceipt receipt, IInstallerControlClient client,
        CancellationToken token, Func<CancellationToken, Task<InstanceLifecycleCondition>>? readCondition = null) => AcquireControlDeploymentAsync(paths, token,
            settleCommittedRecovery: condition == InstanceLifecycleCondition.Installed,
            returnedProcess: async cancellation =>
            {
                if (receipt.OriginalProcessId is null || receipt.OriginalProcessId == Guid.Empty) return false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var snapshot = await client.ExchangeAsync(null, timeout.Token).ConfigureAwait(false);
                return snapshot.Initialized && snapshot.CaptureState == "Paused" && snapshot.ProcessId != Guid.Empty &&
                    snapshot.ProcessId != receipt.OriginalProcessId;
            }, requireRecovery: condition != InstanceLifecycleCondition.Installed, readCondition: readCondition);
}
