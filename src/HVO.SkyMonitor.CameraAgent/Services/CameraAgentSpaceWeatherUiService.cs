using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The operator surface of CameraAgent-local space weather: the shared projection read, and the refresh, resume and
/// acknowledge commands of <see cref="SpaceWeatherOperations"/>. Nothing here fetches, schedules or polls.
/// </summary>
internal interface ICameraAgentSpaceWeatherUiService
{
    ValueTask<OperatorUiResult<SpaceWeatherUiView>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<SpaceWeatherCommandView>> RefreshAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<SpaceWeatherCommandView>> ResumeAsync(
        SpaceWeatherResumeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<SpaceWeatherCommandView>> AcknowledgeAsync(
        string group,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

/// <summary>One shared projection and whether the current operator may run commands against it.</summary>
internal sealed record SpaceWeatherUiView(SpaceWeatherUiProjection Projection, bool CanMutate);

/// <summary>A command's disposition, typed reason and the status the operations service read after it.</summary>
internal sealed record SpaceWeatherCommandView(
    SpaceWeatherOperations.Disposition Disposition,
    string ReasonCode,
    string Outcome,
    SpaceWeatherOperations.Status Status);

/// <summary>
/// The halt a resume targets: its scope, product token for product scope, and the halt's unformatted UTC time parsed
/// with a zero offset.
/// </summary>
internal sealed record SpaceWeatherResumeRequest(string Scope, string? Product, DateTimeOffset ExpectedHaltSinceUtc);

/// <summary>
/// Reads need <c>OperationsReadV1</c>; commands need <c>OperationsMutateV1</c> and an owner identity of at most 128
/// characters. Every command, whatever its outcome, drops the shared projection so the next read is current. Logs
/// carry exception type names and fixed tokens only.
/// </summary>
internal sealed class CameraAgentSpaceWeatherUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    SpaceWeatherOperations operations,
    SpaceWeatherUiProjectionSource source,
    ILogger<CameraAgentSpaceWeatherUiService> logger) : ICameraAgentSpaceWeatherUiService
{
    private const int MaximumActorLength = 128;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<SpaceWeatherUiView>> GetAsync(CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is null)
        {
            SpaceWeatherUiLog.AccessDenied(logger, "read");
            return OperatorUiResult<SpaceWeatherUiView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        var canMutate = ActorOf(await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)) is not null;
        try
        {
            var projection = await source.GetAsync(cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<SpaceWeatherUiView>.Success(new SpaceWeatherUiView(projection, canMutate));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SpaceWeatherUiLog.ReadFailed(logger, exception.GetType().Name);
            return OperatorUiResult<SpaceWeatherUiView>.Failure(
                OperatorUiResultKind.Unavailable, "Space-weather data is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<SpaceWeatherCommandView>> RefreshAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        return ExecuteAsync(
            "refresh",
            actor => operations.RefreshAsync(idempotencyKey, actor, cancellationToken),
            cancellationToken);
    }

    public ValueTask<OperatorUiResult<SpaceWeatherCommandView>> ResumeAsync(
        SpaceWeatherResumeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        return ExecuteAsync(
            "resume",
            _ => operations.ResumeAsync(
                request.Scope, request.Product, request.ExpectedHaltSinceUtc, idempotencyKey, cancellationToken),
            cancellationToken);
    }

    public ValueTask<OperatorUiResult<SpaceWeatherCommandView>> AcknowledgeAsync(
        string group,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        return ExecuteAsync(
            "acknowledge",
            actor => operations.AcknowledgeAsync(group, actor, idempotencyKey, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Authorizes before the command runs, then maps all seven dispositions: Accepted, Applied and Replayed are
    /// Success; every other keeps its reason code and the status read after the command as the value.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<SpaceWeatherCommandView>> ExecuteAsync(
        string command,
        Func<string, ValueTask<SpaceWeatherOperations.CommandResult>> run,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        if (principal is null)
        {
            SpaceWeatherUiLog.AccessDenied(logger, command);
            return OperatorUiResult<SpaceWeatherCommandView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (ActorOf(principal) is not { } actor)
        {
            return OperatorUiResult<SpaceWeatherCommandView>.Failure(
                OperatorUiResultKind.Invalid, "The signed-in owner identity cannot be recorded for this command.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        SpaceWeatherOperations.CommandResult result;
        try
        {
            result = await run(actor).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SpaceWeatherUiLog.CommandFailed(logger, command, exception.GetType().Name);
            return OperatorUiResult<SpaceWeatherCommandView>.Failure(
                OperatorUiResultKind.Unavailable, "The space-weather command could not be completed.");
        }
        finally
        {
            source.Invalidate();
        }
        var kind = result.Disposition switch
        {
            SpaceWeatherOperations.Disposition.Accepted or SpaceWeatherOperations.Disposition.Applied or
                SpaceWeatherOperations.Disposition.Replayed => OperatorUiResultKind.Success,
            SpaceWeatherOperations.Disposition.Invalid => OperatorUiResultKind.Invalid,
            SpaceWeatherOperations.Disposition.NotFound => OperatorUiResultKind.NotFound,
            SpaceWeatherOperations.Disposition.Conflict => OperatorUiResultKind.Conflict,
            _ => OperatorUiResultKind.Unavailable
        };
        return new OperatorUiResult<SpaceWeatherCommandView>(
            kind,
            new SpaceWeatherCommandView(result.Disposition, result.ReasonCode, result.Outcome, result.Status),
            SpaceWeatherOperatorText.Describe(result.ReasonCode));
    }

    private static string? ActorOf(ClaimsPrincipal? principal)
        => principal is not null &&
            CameraAgentCredentialAccess.GetOwnerId(principal) is { Length: > 0 and <= MaximumActorLength } actor &&
            !string.IsNullOrWhiteSpace(actor)
                ? actor
                : null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}
