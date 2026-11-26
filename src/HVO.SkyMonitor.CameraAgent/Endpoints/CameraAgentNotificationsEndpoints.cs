using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// Owner-only operator notifications configuration, status, and safe test-send endpoints. Never returns stored SMTP
/// credentials, webhook URLs, auth tokens, or Pushover keys — only sanitized presence metadata and host names.
/// </summary>
internal static class CameraAgentNotificationsEndpoints
{
    internal const string RoutePrefix = "/api/v1/operations/notifications";

    internal static IEndpointRouteBuilder MapCameraAgentNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RoutePrefix).WithTags("CameraAgent Operator Notifications");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentOperatorNotifications")
            .Produces<NotificationsEndpointState>()
            .ProducesProblem(503)
            .Produces(401)
            .Produces(403);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("UpdateCameraAgentOperatorNotifications")
            .Produces<NotificationsEndpointState>()
            .ProducesProblem(400)
            .ProducesProblem(409)
            .ProducesProblem(503)
            .Produces(401)
            .Produces(403);

        group.MapPost("/test", TestAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("TestCameraAgentOperatorNotifications")
            .Produces<OperatorNotificationTestResult>()
            .ProducesProblem(400)
            .ProducesProblem(429)
            .ProducesProblem(503)
            .Produces(401)
            .Produces(403);

        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Authenticated boundary returns fixed sanitized errors without exposing internal or secret details.")]
    private static async Task<IResult> GetAsync(
        IOperatorNotificationService service,
        CancellationToken ct)
    {
        try
        {
            var configuration = await service.GetConfigurationAsync(ct).ConfigureAwait(false);
            var status = service.GetStatus();
            return Results.Ok(new NotificationsEndpointState(configuration, status));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Authenticated boundary returns fixed sanitized errors without exposing internal or secret details.")]
    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        [FromBody] UpdateNotificationsBody? body,
        IOperatorNotificationService service,
        CancellationToken ct)
    {
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A valid antiforgery token is required to change operator notification settings.",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reasonCode"] = "notifications.antiforgery-invalid"
                });
        }

        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The notification update is not authorized.");
        }

        if (body is null || body.Options is null || string.IsNullOrWhiteSpace(body.ExpectedVersion))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A valid notification configuration payload and expectedVersion are required.",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reasonCode"] = "notifications.invalid-request"
                });
        }

        try
        {
            var request = new OperatorNotificationsUpdateRequest(
                body.ExpectedVersion,
                body.Options,
                body.SmtpUsername,
                body.ClearSmtpUsername,
                body.SmtpPassword,
                body.ClearSmtpPassword,
                body.WebhookUrl,
                body.ClearWebhookUrl,
                body.WebhookAuthToken,
                body.ClearWebhookAuthToken,
                body.WebhookUserKey,
                body.ClearWebhookUserKey,
                body.WebhookNtfyTopic,
                body.ClearWebhookNtfyTopic);

            var result = await service.UpdateConfigurationAsync(request, actor, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                OperatorNotificationsSaveOutcome.Applied when result.Snapshot is not null =>
                    Results.Ok(new NotificationsEndpointState(result.Snapshot, service.GetStatus())),
                OperatorNotificationsSaveOutcome.Invalid => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: result.Message ?? "One or more notification settings are invalid.",
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["reasonCode"] = "notifications.invalid-configuration",
                        ["problems"] = result.Problems
                    }),
                OperatorNotificationsSaveOutcome.Conflict => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: result.Message ?? "Notification settings changed since this page was loaded.",
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["reasonCode"] = "notifications.version-conflict",
                        ["currentConfiguration"] = result.Snapshot
                    }),
                _ => Unavailable()
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Authenticated boundary returns fixed sanitized errors without exposing internal or secret details.")]
    private static async Task<IResult> TestAsync(
        HttpContext context,
        [FromBody] TestNotificationBody? body,
        IOperatorNotificationService service,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A valid antiforgery token is required to send a test notification.",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reasonCode"] = "notifications.antiforgery-invalid"
                });
        }

        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Test notification is not authorized.");
        }

        var target = NotificationTestTarget.AllEnabled;
        if (!string.IsNullOrWhiteSpace(body?.Target) &&
            (!Enum.TryParse(body.Target, ignoreCase: true, out target) || !Enum.IsDefined(target)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unsupported test notification target. Use AllEnabled, Smtp, or Webhook.",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reasonCode"] = "notifications.invalid-target"
                });
        }

        try
        {
            var result = await service.SendTestAsync(target, actor, ct).ConfigureAwait(false);
            if (result.RateLimited)
            {
                var retrySeconds = result.RetryAfterUtc is { } retryAt
                    ? Math.Max(1, (int)Math.Ceiling((retryAt - timeProvider.GetUtcNow()).TotalSeconds))
                    : OperatorNotificationsOptions.DefaultTestSendMinimumIntervalSeconds;
                context.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Please wait before sending another test notification.",
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["reasonCode"] = NotificationReasonCodes.RateLimited,
                        ["retryAfterUtc"] = result.RetryAfterUtc
                    });
            }

            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }
    }

    private static IResult Unavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Operator notifications service is unavailable.",
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reasonCode"] = "notifications.unavailable"
        });

    internal sealed record NotificationsEndpointState(
        OperatorNotificationsConfigurationSnapshot Configuration,
        OperatorNotificationDispatcherSnapshot Status);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record UpdateNotificationsBody(
        string? ExpectedVersion = null,
        OperatorNotificationsOptions? Options = null,
        string? SmtpUsername = null,
        bool ClearSmtpUsername = false,
        string? SmtpPassword = null,
        bool ClearSmtpPassword = false,
        string? WebhookUrl = null,
        bool ClearWebhookUrl = false,
        string? WebhookAuthToken = null,
        bool ClearWebhookAuthToken = false,
        string? WebhookUserKey = null,
        bool ClearWebhookUserKey = false,
        string? WebhookNtfyTopic = null,
        bool ClearWebhookNtfyTopic = false);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record TestNotificationBody(string? Target = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();
        public bool RequiresValidation => true;
    }
}
