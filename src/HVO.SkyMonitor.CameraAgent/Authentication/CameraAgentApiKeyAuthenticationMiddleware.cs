using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.CameraAgent.Authentication;

/// <summary>
/// Authenticates an owner-created API key on the operations API. A key is accepted only as
/// <c>Authorization: Bearer hvo_sky_…</c> and only under <see cref="OperationsPath"/>; anywhere else, including the web
/// UI, account and key management, and the internal endpoints, the header is ignored and the browser session decides.
/// </summary>
/// <remarks>
/// <para>A presented key decides the request on its own: any session cookie on the same request is ignored, so a key
/// never borrows the session's access. A key that does not match is refused with 401 at once, never treated as
/// anonymous, and the refusal is logged with the key identifier when it has one, never with the key.</para>
/// <para>The key's principal is the owner account it belongs to, with that account's current security stamp, an
/// <see cref="ApiKeyAuthenticationOptions.AuthenticationScheme"/> identity and the key's access level. The operations
/// policies then apply the same site-owner checks as for the session, plus the key's access level; nothing else
/// accepts a key's principal.</para>
/// </remarks>
internal sealed partial class CameraAgentApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    ILogger<CameraAgentApiKeyAuthenticationMiddleware> logger)
{
    /// <summary>The only path under which an API key authenticates.</summary>
    internal const string OperationsPath = "/api/v1/operations";

    private const string BearerScheme = "Bearer";

    public async Task InvokeAsync(
        HttpContext context,
        CameraAgentApiKeyStore store,
        UserManager<ApplicationUser> userManager,
        IUserClaimsPrincipalFactory<ApplicationUser> principalFactory,
        CameraAgentOperatorTelemetry telemetry)
    {
        if (!context.Request.Path.StartsWithSegments(OperationsPath, StringComparison.OrdinalIgnoreCase) ||
            !PresentsApiKey(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var token = ReadSingleBearerToken(context.Request);
        if (token is null || !CameraAgentApiKeyToken.TryReadId(token, out var presentedId))
        {
            await RejectAsync(context, telemetry, "malformed", keyId: null).ConfigureAwait(false);
            return;
        }

        var match = await store.FindAsync(token, context.RequestAborted).ConfigureAwait(false);
        var user = match is null ? null : await userManager.FindByIdAsync(match.OwnerId).ConfigureAwait(false);
        if (match is null || user is null)
        {
            // An unknown identifier, a revoked key and a wrong secret are refused alike.
            await RejectAsync(context, telemetry, "unknown-or-revoked", presentedId).ConfigureAwait(false);
            return;
        }

        context.User = await CreatePrincipalAsync(principalFactory, user, match).ConfigureAwait(false);
        telemetry.RecordApiKeyAuthentication("accepted");
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            LogAuthenticatedRead(logger, match.Id, context.Request.Method, context.Request.Path.Value);
        }
        else
        {
            LogAuthenticatedChange(logger, match.Id, match.Access, context.Request.Method, context.Request.Path.Value);
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The literal key prefix at the start of an Authorization value or after a non-token character presents a key,
    /// under any scheme. Token characters are ASCII letters, digits, '-', '.', '_', '~', '+' and '/'. Detect the
    /// attempt before validating syntax, including coalesced or duplicate values; an embedded prefix in an unrelated
    /// token belongs to the rest of the pipeline.
    /// </summary>
    private static bool PresentsApiKey(HttpRequest request)
    {
        foreach (var value in request.Headers.Authorization)
        {
            if (value is null) continue;
            for (var index = 0; index < value.Length; index++)
            {
                if ((index == 0 || !IsTokenCharacter(value[index - 1])) &&
                    value.AsSpan(index).StartsWith(CameraAgentApiKeyToken.Prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTokenCharacter(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or
            '-' or '.' or '_' or '~' or '+' or '/';

    /// <summary>The bearer token when the request carries exactly one <c>Authorization</c> value; otherwise null.</summary>
    private static string? ReadSingleBearerToken(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        return values.Count == 1 ? TryReadBearer(values[0]) : null;
    }

    private static string? TryReadBearer(string? value)
    {
        if (value is null || value.Length <= BearerScheme.Length + 1 ||
            !value.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) || value[BearerScheme.Length] != ' ')
        {
            return null;
        }

        var token = value[(BearerScheme.Length + 1)..].Trim();
        return token.Length == 0 || token.Any(char.IsWhiteSpace) ? null : token;
    }

    private static async Task<ClaimsPrincipal> CreatePrincipalAsync(
        IUserClaimsPrincipalFactory<ApplicationUser> principalFactory,
        ApplicationUser user,
        CameraAgentApiKeyMatch match)
    {
        // The owner's own claims, including the account type and the current security stamp, so the site-owner policy
        // checks the key exactly as it checks the session. Only the identity type and the key claims differ.
        var owner = await principalFactory.CreateAsync(user).ConfigureAwait(false);
        var claims = owner.Claims
            .Where(claim => claim.Type is not (ApiKeyClaims.AccessLevel or ApiKeyClaims.ApiKeyId or
                ApiKeyClaims.AuthenticationType or ApiKeyClaims.ObservatoryId))
            .Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer))
            .Append(new Claim(ApiKeyClaims.ApiKeyId, match.Id))
            .Append(new Claim(ApiKeyClaims.AccessLevel, match.Access.ToString()))
            .Append(new Claim(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.AuthenticationScheme));
    }

    private Task RejectAsync(HttpContext context, CameraAgentOperatorTelemetry telemetry, string reason, string? keyId)
    {
        telemetry.RecordApiKeyAuthentication("rejected");
        LogRejected(
            logger,
            reason,
            keyId ?? "none",
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            context.Request.Method,
            context.Request.Path.Value);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer error=\"invalid_token\"";
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return context.Response.WriteAsJsonAsync(new
        {
            status = StatusCodes.Status401Unauthorized,
            title = "The API key is not valid."
        });
    }

    [LoggerMessage(4193, LogLevel.Warning,
        "Rejected an API key ({Reason}, key {KeyId}) from {RemoteAddress} for {Method} {Path}")]
    private static partial void LogRejected(ILogger logger, string reason, string keyId, string remoteAddress,
        string method, string? path);

    [LoggerMessage(4194, LogLevel.Information, "API key {KeyId} with {Access} access authenticated {Method} {Path}")]
    private static partial void LogAuthenticatedChange(ILogger logger, string keyId, ApiKeyAccessLevel access,
        string method, string? path);

    [LoggerMessage(4195, LogLevel.Debug, "API key {KeyId} authenticated {Method} {Path}")]
    private static partial void LogAuthenticatedRead(ILogger logger, string keyId, string method, string? path);
}
