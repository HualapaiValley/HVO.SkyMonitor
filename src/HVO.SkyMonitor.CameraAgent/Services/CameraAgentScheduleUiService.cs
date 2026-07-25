using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentScheduleUiService
{
    ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
        string profileJson,
        int dayCount,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
        string profileJson,
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
}

internal sealed class CameraAgentScheduleUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    CaptureScheduleRuntimeCoordinator runtime,
    SqliteCaptureScheduleStore store,
    ICameraAgentConfigurationAccessor configurationAccessor,
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
                await runtime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false));
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
        int dayCount,
        CancellationToken cancellationToken)
        => ExecuteReadAsync(
            profileJson,
            profile => runtime.Preview(profile, dayCount),
            cancellationToken);

    public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
        string profileJson,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            profileJson,
            (profile, actor, token) => runtime.StageAsync(
                profile!, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            profileJson: null,
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
            (_, actor, token) => store.ClearOverrideAsync(
                overrideId, idempotencyKey, expectedVersion, actor, reason, token),
            cancellationToken);

    internal static string SerializeProfile(LocalCaptureProfileDefinition profile)
        => JsonSerializer.Serialize(profile, SerializerOptions);

    private async ValueTask<OperatorUiResult<T>> ExecuteReadAsync<T>(
        string profileJson,
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
            return OperatorUiResult<T>.Success(operation(ParseProfile(profileJson)));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return Invalid<T>("The local profile or schedule is invalid.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ExecuteMutationAsync(
        string? profileJson,
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
            var profile = profileJson is null ? null : ParseProfile(profileJson);
            return OperatorUiResult<CaptureScheduleStoreSnapshot>.Success(
                await operation(profile, actor, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Invalid<CaptureScheduleStoreSnapshot>("The schedule command is invalid.");
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

    private static OperatorUiResult<T> Denied<T>()
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

    private static OperatorUiResult<T> Invalid<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, message);

    private static OperatorUiResult<T> Unavailable<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);
}
