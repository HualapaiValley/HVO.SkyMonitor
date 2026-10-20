using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The runtime client's system operations (install contract section 8, #1317): <c>GET info</c> (versions, the process,
/// the catalog, restart support and who the caller is) and <c>POST restart</c>, which requests the same restart as the
/// Camera &amp; rig page's Restart now. Reads need the operations read policy; a restart needs the mutate policy and,
/// for a browser session, an antiforgery token.
/// </summary>
internal static partial class CameraAgentSystemOperationsEndpoints
{
    internal const string InfoRoute = "/api/v1/operations/info";

    internal const string RestartRoute = "/api/v1/operations/restart";

    /// <summary>The version of the info document, so a client can refuse a shape it does not understand.</summary>
    internal const int InfoSchemaVersion = 1;

    internal const string UnsupervisedCode = "restart.unsupervised";

    internal static IEndpointRouteBuilder MapCameraAgentSystemOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(InfoRoute, GetInfo)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Operations")
            .WithName("GetCameraAgentOperationsInfo")
            .Produces<CameraAgentOperationsInfo>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapPost(RestartRoute, RequestRestart)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithTags("CameraAgent Operations")
            .WithName("RestartCameraAgent")
            .Produces<CameraAgentRestartResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static IResult GetInfo(
        HttpContext context,
        CatalogSnapshotResult catalog,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider)
    {
        var informational = typeof(CameraAgentSystemOperationsEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var now = timeProvider.GetUtcNow();
        return Results.Ok(new CameraAgentOperationsInfo(
            InfoSchemaVersion,
            new CameraAgentApplicationInfo(
                "CameraAgent",
                plus < 0 ? informational : informational[..plus],
                plus < 0 ? null : informational[(plus + 1)..],
                informational),
            new CameraAgentHostInfo(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.RuntimeIdentifier,
                started,
                Math.Max(0, (now - started).TotalSeconds)),
            new CameraAgentCatalogInfo(
                catalog.CatalogId,
                catalog.CatalogVersion,
                catalog.PackageKind.ToString(),
                catalog.SnapshotVersion,
                catalog.ManifestVersion,
                catalog.SchemaVersion,
                catalog.PreprocessingVersion,
                catalog.RowCount,
                catalog.DatabaseLength,
                catalog.DatabaseSha256),
            new CameraAgentRestartInfo(CameraAgentRestart.IsSupervised(configuration), CameraAgentRestart.IsRequested(lifetime)),
            Caller(context.User)));
    }

    private static IResult RequestRestart(
        HttpContext context,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The restart request is invalid.");
        }

        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The restart request is not authorized.");
        }

        var logger = loggerFactory.CreateLogger(typeof(CameraAgentSystemOperationsEndpoints).FullName!);
        var disposition = CameraAgentRestart.Request(
            lifetime, configuration, actor, CameraAgentRestart.DefaultDelay, timeProvider, logger, LogRestartRequested);
        return disposition == CameraAgentRestartDisposition.Unsupervised
            ? Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "CameraAgent is not supervised here, so it cannot restart itself: nothing would start it again.",
                extensions: new Dictionary<string, object?> { ["code"] = UnsupervisedCode })
            : Results.Json(
                new CameraAgentRestartResponse(disposition.ToString(), CameraAgentRestart.DefaultDelay.TotalSeconds),
                statusCode: StatusCodes.Status202Accepted);
    }

    private static CameraAgentCallerInfo Caller(ClaimsPrincipal principal)
        => CameraAgentCredentialAccess.IsApiKey(principal)
            ? new CameraAgentCallerInfo("ApiKey", principal.FindFirstValue(ApiKeyClaims.ApiKeyId), principal.FindFirstValue(ApiKeyClaims.AccessLevel))
            : new CameraAgentCallerInfo("Session", null, null);

    [LoggerMessage(LogLevel.Information, "CameraAgent restart requested by {Actor} through the operations API.")]
    private static partial void LogRestartRequested(ILogger logger, string actor);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}

/// <summary>Body of <c>GET /api/v1/operations/info</c>.</summary>
internal sealed record CameraAgentOperationsInfo(
    int SchemaVersion,
    CameraAgentApplicationInfo Application,
    CameraAgentHostInfo Host,
    CameraAgentCatalogInfo Catalog,
    CameraAgentRestartInfo Restart,
    CameraAgentCallerInfo Caller);

/// <param name="InformationalVersion">The build identity, <c>&lt;version&gt;+&lt;40-character commit&gt;</c>.</param>
internal sealed record CameraAgentApplicationInfo(string Name, string Version, string? Revision, string InformationalVersion);

/// <param name="ProcessStartedUtc">When this process started; a restart's caller waits for it to change.</param>
internal sealed record CameraAgentHostInfo(string Runtime, string RuntimeIdentifier, DateTimeOffset ProcessStartedUtc, double UptimeSeconds);

internal sealed record CameraAgentCatalogInfo(
    string CatalogId,
    string CatalogVersion,
    string PackageKind,
    string SnapshotVersion,
    int ManifestVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    long RowCount,
    long DatabaseBytes,
    string DatabaseSha256);

/// <param name="Supervised">True when something starts CameraAgent again after it stops, so it may restart itself.</param>
/// <param name="Requested">True once a restart was requested and this process is about to stop.</param>
internal sealed record CameraAgentRestartInfo(bool Supervised, bool Requested);

/// <summary>Who CameraAgent takes the caller to be: <c>Session</c>, or <c>ApiKey</c> with its public identifier and access.</summary>
internal sealed record CameraAgentCallerInfo(string Credential, string? ApiKeyId, string? Access);

/// <summary>Body of an accepted restart: <c>Scheduled</c>, or <c>AlreadyRequested</c> when another caller asked first.</summary>
internal sealed record CameraAgentRestartResponse(string Status, double DelaySeconds);
