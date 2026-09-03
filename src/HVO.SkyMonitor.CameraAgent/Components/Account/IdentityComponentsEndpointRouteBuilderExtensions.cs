using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    private const int RecoveryRequestSizeLimit = 8 * 1024;
    private const int RecoveryAttestationNonceLength = 32;
    private const int MaximumRecoveryChallengeBytes = 4096;
    private const int MaximumRecoveryPasswordBytes = 256;
    private const byte RecoveryCompletionProtocolVersion = 1;
    private const string RecoveryOperationHeader = "X-HVO-Recovery-Operation";
    private const string RecoveryAttestationPurpose = "HVO.SkyMonitor.CameraAgent.OwnerRecovery.Attestation.v1";
    private static readonly EventId RecoveryEndpointFailureEvent = new(4187, "OwnerPasswordRecoveryEndpointFailed");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal _,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            var redirectTarget = BuildLocalRedirectPath(returnUrl);
            return TypedResults.LocalRedirect(redirectTarget);
        });

        var recoveryGroup = endpoints.MapGroup("/api/internal/owner-bootstrap/recovery")
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(RecoveryRequestSizeLimit))
            .ExcludeFromDescription();
        recoveryGroup.MapPost("/attestation", CreateRecoveryAttestationAsync);
        recoveryGroup.MapPost("/challenge", CreateRecoveryChallengeAsync);
        recoveryGroup.MapPost("/complete", CompleteRecoveryAsync);

        return accountGroup;

        static string BuildLocalRedirectPath(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return "~/";
            }

            var trimmed = candidate.Trim();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("\\\\", StringComparison.Ordinal)
                || trimmed.Contains("://", StringComparison.Ordinal)
                || !Uri.TryCreate(trimmed, UriKind.Relative, out _))
            {
                return "~/";
            }

            return $"~/{trimmed.TrimStart('/')}";
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The privileged endpoint must not expose exception details.")]
    private static async Task<IResult> CreateRecoveryAttestationAsync(
        HttpContext context,
        IConfiguration configuration,
        OwnerRecoveryTransport recoveryTransport,
        ILogger<OwnerPasswordRecoveryService> logger,
        CancellationToken cancellationToken)
    {
        SetPrivateResponseHeaders(context.Response);
        try
        {
            if (!recoveryTransport.IsLocal(context))
            {
                return Results.NotFound();
            }
            if (!Guid.TryParse(context.Request.Headers[RecoveryOperationHeader], out var operationId) ||
                operationId == Guid.Empty ||
                !HasBinaryContentType(context.Request) ||
                string.IsNullOrEmpty(configuration["LifecycleControl:Token"]))
            {
                return Results.Unauthorized();
            }

            var nonce = new byte[RecoveryAttestationNonceLength + 1];
            var total = 0;
            while (total < nonce.Length)
            {
                var read = await context.Request.Body.ReadAsync(nonce.AsMemory(total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            if (total != RecoveryAttestationNonceLength)
            {
                return Results.BadRequest();
            }

            var proof = CreateAttestationProof(
                configuration["LifecycleControl:Token"]!, operationId, nonce.AsSpan(0, RecoveryAttestationNonceLength));
            return Results.Bytes(proof, "application/octet-stream");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RecoveryEndpointFailure(logger, "attestation");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The privileged endpoint must not expose exception details.")]
    private static async Task<IResult> CreateRecoveryChallengeAsync(
        HttpContext context,
        IConfiguration configuration,
        OwnerRecoveryTransport recoveryTransport,
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<LocalIdentityOptions> identityOptions,
        ILogger<OwnerPasswordRecoveryService> logger,
        CancellationToken cancellationToken)
    {
        SetPrivateResponseHeaders(context.Response);
        try
        {
            if (!recoveryTransport.IsLocal(context))
            {
                return Results.NotFound();
            }
            if (!IsRecoveryAuthorized(context, configuration, recoveryTransport) ||
                !TryGetRecoveryOperation(context, out var operationId) ||
                !HasBinaryContentType(context.Request) ||
                !await HasEmptyBodyAsync(context.Request.Body, cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            var service = new OwnerPasswordRecoveryService(
                dbContext,
                userManager,
                dataProtectionProvider,
                identityOptions,
                logger);
            var result = await service.CreateChallengeAsync(operationId, cancellationToken).ConfigureAwait(false);
            return result switch
            {
                { Outcome: OwnerPasswordRecoveryOutcome.Completed, Challenge: not null } =>
                    Results.Bytes(StrictUtf8.GetBytes(result.Challenge), "application/octet-stream"),
                _ => Results.BadRequest()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RecoveryEndpointFailure(logger, "challenge");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The privileged endpoint must not expose exception details.")]
    private static async Task<IResult> CompleteRecoveryAsync(
        HttpContext context,
        IConfiguration configuration,
        OwnerRecoveryTransport recoveryTransport,
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<LocalIdentityOptions> identityOptions,
        ILogger<OwnerPasswordRecoveryService> logger,
        CancellationToken cancellationToken)
    {
        SetPrivateResponseHeaders(context.Response);
        try
        {
            if (!recoveryTransport.IsLocal(context))
            {
                return Results.NotFound();
            }
            if (!IsRecoveryAuthorized(context, configuration, recoveryTransport) ||
                !TryGetRecoveryOperation(context, out var operationId) ||
                !HasBinaryContentType(context.Request))
            {
                return Results.Unauthorized();
            }

            var request = await ReadCompletionAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return Results.BadRequest();
            }

            var service = new OwnerPasswordRecoveryService(
                dbContext,
                userManager,
                dataProtectionProvider,
                identityOptions,
                logger);
            var outcome = await service.CompleteAsync(
                operationId,
                request.Challenge,
                request.TemporaryPassword,
                cancellationToken).ConfigureAwait(false);
            return outcome switch
            {
                OwnerPasswordRecoveryOutcome.Completed or OwnerPasswordRecoveryOutcome.AlreadyCompleted =>
                    Results.Bytes(
                        StrictUtf8.GetBytes(OwnerBootstrapStates.PasswordChangeRequired),
                        "application/octet-stream"),
                OwnerPasswordRecoveryOutcome.AlreadyCompletedPasswordReplaced =>
                    Results.Bytes(StrictUtf8.GetBytes(OwnerBootstrapStates.Ready), "application/octet-stream"),
                _ => Results.BadRequest()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RecoveryEndpointFailure(logger, "completion");
        }
    }

    internal static bool IsRecoveryAuthorized(
        HttpContext context,
        IConfiguration configuration,
        OwnerRecoveryTransport recoveryTransport)
        => recoveryTransport.IsLocal(context) &&
           CameraAgentLifecycleEndpoints.IsAuthorized(context, configuration);

    internal static bool IsRecoveryAuthorized(
        HttpContext context,
        IConfiguration configuration,
        OwnerRecoveryTransport recoveryTransport,
        EndPoint localEndpoint)
        => recoveryTransport.IsLocalEndpoint(localEndpoint) &&
           CameraAgentLifecycleEndpoints.IsAuthorized(context, configuration);

    private static bool TryGetRecoveryOperation(HttpContext context, out Guid operationId)
        => Guid.TryParse(context.Request.Headers[RecoveryOperationHeader], out operationId) &&
           operationId != Guid.Empty;

    private static bool HasBinaryContentType(HttpRequest request)
        => string.Equals(request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HasEmptyBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        return await body.ReadAsync(value, cancellationToken).ConfigureAwait(false) == 0;
    }

    private static async Task<RecoveryCompletionRequest?> ReadCompletionAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[RecoveryRequestSizeLimit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        if (total < 9 || total > RecoveryRequestSizeLimit || buffer[0] != RecoveryCompletionProtocolVersion)
        {
            return null;
        }

        var challengeLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(1, 4));
        var passwordLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(5, 4));
        if (challengeLength is < 1 or > MaximumRecoveryChallengeBytes ||
            passwordLength is < 16 or > MaximumRecoveryPasswordBytes ||
            9 + challengeLength + passwordLength != total)
        {
            return null;
        }

        try
        {
            return new RecoveryCompletionRequest(
                StrictUtf8.GetString(buffer, 9, challengeLength),
                StrictUtf8.GetString(buffer, 9 + challengeLength, passwordLength));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static byte[] CreateAttestationProof(string token, Guid operationId, ReadOnlySpan<byte> nonce)
    {
        var prefix = Encoding.UTF8.GetBytes($"{RecoveryAttestationPurpose}\n{operationId:D}\n");
        var payload = new byte[prefix.Length + nonce.Length];
        prefix.CopyTo(payload, 0);
        nonce.CopyTo(payload.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), payload);
    }

    private static void SetPrivateResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static IResult RecoveryEndpointFailure(
        ILogger<OwnerPasswordRecoveryService> logger,
        string phase)
    {
        logger.LogError(
            RecoveryEndpointFailureEvent,
            "Local owner recovery failed during {RecoveryPhase}; internal-error",
            phase);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    private sealed record RecoveryCompletionRequest(
        string Challenge,
        string TemporaryPassword);
}
