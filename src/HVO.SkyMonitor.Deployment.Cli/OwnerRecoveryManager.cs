using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record OwnerRecoveryOperationState(
    int SchemaVersion,
    Guid OperationId,
    Guid InstanceId,
    string RequestSha256,
    bool Completed,
    bool RetryAllowed,
    DateTimeOffset UpdatedUtc);

internal sealed record OwnerRecoveryResult(
    int SchemaVersion,
    string Outcome,
    Guid OperationId,
    Guid InstanceId,
    [property: JsonIgnore] string? PasswordFile,
    string OwnerBootstrapState,
    DateTimeOffset CompletedUtc);

internal static class OwnerRecoveryManager
{
    private const string PasswordChangeRequiredState = "owner-password-change-required";
    private const string ReadyState = "owner-ready";

    internal static Task<OwnerRecoveryResult> ExecuteAsync(
        OwnerRecoveryRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            request,
            null,
            NativeLinux.getuid(),
            NativeLinux.getgid(),
            cancellationToken);

    internal static async Task<OwnerRecoveryResult> ExecuteAsync(
        OwnerRecoveryRequest request,
        Func<string, IOwnerRecoveryClient>? recoveryClientFactory,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteCoreAsync(
                request,
                recoveryClientFactory,
                uid,
                gid,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InstallerException or OwnerRecoveryProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            throw new InstallerException("Owner recovery could not read or update its private deployment state.", exception);
        }
    }

    private static async Task<OwnerRecoveryResult> ExecuteCoreAsync(
        OwnerRecoveryRequest request,
        Func<string, IOwnerRecoveryClient>? recoveryClientFactory,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (uid == 0)
        {
            throw new InstallerException("Run owner recovery as the CameraAgent deployment runtime user, not as root.");
        }

        var instanceId = request.InstanceId!.Value;
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.CatalogId);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        var installation = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, cancellationToken)
            .ConfigureAwait(false);
        ValidateInstance(paths, instanceId, manifest, installation, uid, gid);

        SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
        using var productLock = OperationLock.Acquire(
            Path.Combine(paths.OperationsRoot, "deployment.lock"),
            cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(
            Path.Combine(paths.InstanceRoot, ".deployment.lock"),
            cancellationToken: cancellationToken);

        manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        installation = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, cancellationToken)
            .ConfigureAwait(false);
        ValidateInstance(paths, instanceId, manifest, installation, uid, gid);
        var lifecycleControlToken = await CameraAgentLifecycleManager.ReadLifecycleControlTokenAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        CameraAgentLifecycleManager.ValidateLifecycleControlToken(manifest, lifecycleControlToken);

        var statePath = Path.Combine(paths.OperationsRoot, $"cameraagent-{instanceId:D}.owner-recovery.json");
        var requestSha256 = request.ComputeRequestSha256();
        var retained = await ReadStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        OwnerRecoveryOperationState state;
        string passwordPath;
        if (request.Resume)
        {
            state = retained ?? throw new InstallerException("No owner recovery operation is available to resume.");
            if (state.SchemaVersion != 1 || state.InstanceId != instanceId ||
                !string.Equals(state.RequestSha256, requestSha256, StringComparison.Ordinal))
            {
                throw new InstallerException("The retained owner recovery operation does not match this request.");
            }
            if (state.RetryAllowed)
            {
                throw new InstallerException("The retained owner recovery operation was rejected; start a new recovery operation.");
            }
            var operationRoot = Path.Combine(paths.OperationsRoot, "owner-recovery", state.OperationId.ToString("D"));
            passwordPath = Path.Combine(operationRoot, "temporary-password");
            if (!File.Exists(passwordPath))
            {
                throw new InstallerException("The staged owner recovery credential is unavailable; start a new recovery operation.");
            }
            SafeFileSystem.ValidateOwnerFile(passwordPath);
        }
        else
        {
            if (retained is { Completed: false, RetryAllowed: false })
            {
                var retainedPasswordPath = Path.Combine(
                    paths.OperationsRoot,
                    "owner-recovery",
                    retained.OperationId.ToString("D"),
                    "temporary-password");
                if (File.Exists(retainedPasswordPath))
                {
                    throw new InstallerException("An owner recovery operation is incomplete; rerun this command with --resume.");
                }
            }

            var operationId = Guid.NewGuid();
            var operationRoot = Path.Combine(paths.OperationsRoot, "owner-recovery", operationId.ToString("D"));
            var generatedPasswordPath = Path.Combine(operationRoot, "temporary-password");
            passwordPath = await CredentialFile.GetOrCreateAsync(
                request.GeneratePassword ? null : request.PasswordFile,
                generatedPasswordPath,
                cancellationToken).ConfigureAwait(false);
            _ = await ReadPasswordAsync(passwordPath, cancellationToken).ConfigureAwait(false);
            state = new OwnerRecoveryOperationState(
                SchemaVersion: 1,
                OperationId: operationId,
                InstanceId: instanceId,
                RequestSha256: requestSha256,
                Completed: false,
                RetryAllowed: false,
                UpdatedUtc: DateTimeOffset.UtcNow);
            await WriteStateAsync(statePath, state, cancellationToken).ConfigureAwait(false);
        }

        var temporaryPassword = await ReadPasswordAsync(passwordPath, cancellationToken).ConfigureAwait(false);
        var socketPath = OwnerRecoverySocket.ValidateForRecovery(paths, uid, gid);

        var recoveryClient = recoveryClientFactory?.Invoke(socketPath) ?? new OwnerRecoveryClient(socketPath);
        string ownerState;
        try
        {
            ownerState = await recoveryClient.RecoverAsync(
                state.OperationId,
                lifecycleControlToken,
                temporaryPassword,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerRecoveryProtocolException exception)
        {
            state = state with
            {
                RetryAllowed = exception.Disposition == OwnerRecoveryFailureDisposition.FreshOperationRequired,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await WriteStateAsync(statePath, state, cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (!string.Equals(ownerState, PasswordChangeRequiredState, StringComparison.Ordinal) &&
            !string.Equals(ownerState, ReadyState, StringComparison.Ordinal))
        {
            throw new InstallerException("CameraAgent owner recovery did not require temporary password replacement.");
        }

        state = state with { Completed = true, RetryAllowed = false, UpdatedUtc = DateTimeOffset.UtcNow };
        await WriteStateAsync(statePath, state, cancellationToken).ConfigureAwait(false);
        var passwordChangeRequired = string.Equals(ownerState, PasswordChangeRequiredState, StringComparison.Ordinal);
        return new OwnerRecoveryResult(
            SchemaVersion: 1,
            Outcome: passwordChangeRequired ? "completed" : "already-completed",
            OperationId: state.OperationId,
            InstanceId: instanceId,
            PasswordFile: passwordChangeRequired ? passwordPath : null,
            OwnerBootstrapState: ownerState,
            CompletedUtc: state.UpdatedUtc);
    }

    private static void ValidateInstance(
        InstallationPaths paths,
        Guid instanceId,
        InstanceManifest manifest,
        InstallationResult installation,
        uint uid,
        uint gid)
    {
        CameraAgentLifecycleManager.EnsureCorrelated(
            paths,
            instanceId,
            manifest,
            installation,
            allowTransactionalDrift: false);
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
        {
            throw new InstallerException("Owner recovery requires an installed CameraAgent instance.");
        }
        if (manifest.RuntimeUid != uid || manifest.RuntimeGid != gid)
        {
            throw new InstallerException("Run owner recovery as the CameraAgent deployment runtime user.");
        }
    }

    private static async Task<OwnerRecoveryOperationState?> ReadStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        return await JsonSerializer.DeserializeAsync(
                stream,
                DeploymentJsonContext.Default.OwnerRecoveryOperationState,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InstallerException("The retained owner recovery operation is empty.");
    }

    private static Task WriteStateAsync(
        string path,
        OwnerRecoveryOperationState state,
        CancellationToken cancellationToken)
        => SafeFileSystem.WriteJsonAtomicAsync(
            path,
            state,
            DeploymentJsonContext.Default.OwnerRecoveryOperationState,
            cancellationToken);

    private static async Task<string> ReadPasswordAsync(string path, CancellationToken cancellationToken)
    {
        SafeFileSystem.ValidateOwnerFile(path);
        if (new FileInfo(path).Length is < 16 or > 256)
        {
            throw new InstallerException("The owner recovery password file length is invalid.");
        }

        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string password;
        try
        {
            password = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InstallerException("The owner recovery password file is not valid UTF-8.", exception);
        }
        if (password.Length is < 16 or > 256 || password.Any(static character => character is < '!' or > '~'))
        {
            throw new InstallerException("The owner recovery password file content is invalid.");
        }
        return password;
    }
}
