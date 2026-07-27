using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentScheduleUiService
{
    ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
        string profileJson,
        string basisRevisionId,
        int dayCount,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
        string profileJson,
        string basisRevisionId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
        CaptureScheduleOverride scheduleOverride,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
        string overrideId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineOperatorState>.Failure(
            OperatorUiResultKind.Unavailable, "Current processing graph data is unavailable."));

    ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> PreviewPipelineAsync(
        string profileJson,
        string basisRevisionId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineProfilePreview>.Failure(
            OperatorUiResultKind.Unavailable, "The processing graph preview could not be completed."));

    ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> TogglePipelineAsync(
        string profileJson,
        string basisRevisionId,
        string nodeId,
        bool enabled,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineProfilePreview>.Failure(
            OperatorUiResultKind.Unavailable, "The processing graph preview could not be completed."));
}

internal sealed class CameraAgentScheduleUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    CaptureScheduleRuntimeCoordinator runtime,
    SqliteCaptureScheduleStore store,
    ICameraAgentConfigurationAccessor configurationAccessor,
    ICaptureProcessingPipelineFactory pipelineFactory,
    ILogger<CameraAgentScheduleUiService> logger) : ICameraAgentScheduleUiService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service returns fixed sanitized failures.")]
    public async ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CaptureScheduleOperatorState>();
        }
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<CaptureScheduleOperatorState>.Success(
                CameraAgentScheduleOperatorProjection.Sanitize(
                    await runtime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent schedule UI read failed.");
            return Unavailable<CaptureScheduleOperatorState>("Current schedule data is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
        string profileJson,
        string basisRevisionId,
        int dayCount,
        CancellationToken cancellationToken)
        => ExecuteReadAsync(
            profileJson,
            basisRevisionId,
            profile => runtime.Preview(profile, dayCount),
            cancellationToken);

    public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
        string profileJson,
        string basisRevisionId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            profileJson,
            basisRevisionId,
            (profile, actor, token) => runtime.StageFromBasisAsync(
                profile!, basisRevisionId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            profileJson: null,
            basisRevisionId: null,
            (_, actor, token) => runtime.ActivateAsync(
                revisionId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
        CaptureScheduleOverride scheduleOverride,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            profileJson: null,
            basisRevisionId: null,
            (_, actor, token) => store.AddOverrideAsync(
                scheduleOverride, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
        string overrideId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            profileJson: null,
            basisRevisionId: null,
            (_, actor, token) => store.ClearOverrideAsync(
                overrideId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized graph state.")]
    public async ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CameraAgentPipelineOperatorState>();
        }
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var current = runtime.Snapshot ?? throw new InvalidOperationException("The pipeline runtime is unavailable.");
            var state = await runtime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentPipelineOperatorState>.Success(
                CameraAgentPipelineOperatorProjection.CreateState(state, current.Configuration, pipelineFactory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent pipeline UI read failed.");
            return Unavailable<CameraAgentPipelineOperatorState>("Current processing graph data is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> PreviewPipelineAsync(
        string profileJson,
        string basisRevisionId,
        CancellationToken cancellationToken)
        => ExecutePipelinePreviewAsync(
            profileJson,
            basisRevisionId,
            static profile => profile,
            cancellationToken);

    public ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> TogglePipelineAsync(
        string profileJson,
        string basisRevisionId,
        string nodeId,
        bool enabled,
        CancellationToken cancellationToken)
        => ExecutePipelinePreviewAsync(
            profileJson,
            basisRevisionId,
            profile => CameraAgentPipelineOperatorProjection.Toggle(profile, nodeId, enabled),
            cancellationToken);

    internal static string SerializeProfile(LocalCaptureProfileDefinition profile)
        => JsonSerializer.Serialize(CameraAgentScheduleOperatorProjection.Sanitize(profile), SerializerOptions);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<T>> ExecuteReadAsync<T>(
        string profileJson,
        string basisRevisionId,
        Func<LocalCaptureProfileDefinition, T> operation,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<T>();
        }
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var basis = await store.GetRevisionAsync(basisRevisionId, cancellationToken).ConfigureAwait(false);
            var profile = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
                ParseProfile(profileJson),
                basis.Profile);
            return OperatorUiResult<T>.Success(operation(profile));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or CaptureProfileCompatibilityException)
        {
            return Invalid<T>("The local profile or schedule is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent schedule UI preview failed.");
            return Unavailable<T>("The schedule preview could not be completed.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ExecuteMutationAsync(
        string? profileJson,
        string? basisRevisionId,
        Func<LocalCaptureProfileDefinition?, string, CancellationToken, Task<CaptureScheduleStoreSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        if (principal is null)
        {
            return Denied<CaptureScheduleStoreSnapshot>();
        }
        var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<CaptureScheduleStoreSnapshot>();
        }
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            LocalCaptureProfileDefinition? profile = null;
            if (profileJson is not null)
            {
                var candidate = ParseProfile(profileJson);
                var basis = await store.GetRevisionAsync(
                    basisRevisionId ?? throw new ArgumentException("A basis revision is required."),
                    cancellationToken).ConfigureAwait(false);
                profile = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
                    candidate,
                    basis.Profile);
            }
            return OperatorUiResult<CaptureScheduleStoreSnapshot>.Success(
                CameraAgentScheduleOperatorProjection.Sanitize(
                    await operation(profile, actor, cancellationToken).ConfigureAwait(false)));
        }
        catch (ArgumentException)
        {
            return Invalid<CaptureScheduleStoreSnapshot>("The schedule command is invalid.");
        }
        catch (CaptureProfileCompatibilityException)
        {
            return Invalid<CaptureScheduleStoreSnapshot>(
                "The local capture profile is incompatible with this CameraAgent.");
        }
        catch (KeyNotFoundException)
        {
            return OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.NotFound, "The schedule revision or override was not found.");
        }
        catch (CaptureScheduleStoreConflictException)
        {
            return OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Conflict, "Schedule state changed. Refresh before retrying.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent schedule UI mutation failed.");
            return Unavailable<CaptureScheduleStoreSnapshot>("The schedule command could not be completed.");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (runtime.Snapshot is null)
        {
            var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
            _ = await runtime.InitializeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsAuthorizedAsync(string policy)
        => await GetAuthorizedPrincipalAsync(policy).ConfigureAwait(false) is not null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }

    private static LocalCaptureProfileDefinition ParseProfile(string json)
        => JsonSerializer.Deserialize<LocalCaptureProfileDefinition>(json, SerializerOptions)
            ?? throw new JsonException("The local profile is empty.");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service returns fixed sanitized graph failures.")]
    private async ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> ExecutePipelinePreviewAsync(
        string profileJson,
        string basisRevisionId,
        Func<LocalCaptureProfileDefinition, LocalCaptureProfileDefinition> transform,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CameraAgentPipelineProfilePreview>();
        }
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var basis = await store.GetRevisionAsync(basisRevisionId, cancellationToken).ConfigureAwait(false);
            var profile = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
                ParseProfile(profileJson), basis.Profile);
            profile = transform(profile);
            var current = runtime.Snapshot ?? throw new InvalidOperationException("The pipeline runtime is unavailable.");
            var plan = CameraAgentPipelineOperatorProjection.Preview(
                profile, current.Configuration, pipelineFactory);
            return OperatorUiResult<CameraAgentPipelineProfilePreview>.Success(new(
                basisRevisionId,
                SerializeProfile(profile),
                plan));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid<CameraAgentPipelineProfilePreview>(
                CameraAgentPipelineOperatorProjection.SanitizeValidationFailure(exception));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or KeyNotFoundException or
            CaptureProfileCompatibilityException or System.ComponentModel.DataAnnotations.ValidationException)
        {
            return Invalid<CameraAgentPipelineProfilePreview>("The local profile or desired graph is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent pipeline UI preview failed.");
            return Unavailable<CameraAgentPipelineProfilePreview>("The processing graph preview could not be completed.");
        }
    }

    private static OperatorUiResult<T> Denied<T>()
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

    private static OperatorUiResult<T> Invalid<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, message);

    private static OperatorUiResult<T> Unavailable<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);
}
