using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// Installer-only pull transport: browser authentication cannot register the worker, supply installation credentials,
/// publish receipts or invoke the restart mechanism. All routes are fixed and bound to retained lifecycle intent.
/// </summary>
internal static partial class CameraAgentControlLifecycleEndpoints
{
    private const string Prefix = "/api/internal/deployment/lifecycle/control";

    internal static IEndpointRouteBuilder MapCameraAgentControlLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Prefix + "/exchange", ExchangeAsync).AllowAnonymous();
        endpoints.MapPost(Prefix + "/restart", RestartAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> ExchangeAsync(CameraAgentControlExchange exchange, HttpContext context,
        IConfiguration configuration, CameraAgentControlStore controls, CaptureAdmissionCoordinator capture,
        ICameraAgentControlRestartWindow window, SqliteLocalAutomationStore automations,
        CancellationToken cancellationToken)
    {
        if (!CameraAgentLifecycleEndpoints.IsAuthorized(context, configuration)) return Results.Unauthorized();
        try
        {
            var newReceipt = await controls.ValidateReceiptAsync(exchange, cancellationToken).ConfigureAwait(false);
            if (newReceipt && exchange.Receipt is { IsTerminal: true, ResumeRequested: true } failed &&
                failed.Phase != CameraAgentControlPhase.Succeeded)
            {
                // Admission is still fenced by the nonterminal projection. Settle an ambiguously acknowledged
                // installer resume before publishing failure; replayed receipts must never undo a later owner resume.
                if (capture.Snapshot.State == CaptureAdmissionState.Running)
                {
                    if (failed.PausedVersion is not { } paused || capture.Snapshot.Version != checked(paused + 1))
                        return Results.Conflict();
                    await capture.PauseAsync($"lifecycle-settlement:{failed.Request.OperationId:D}", capture.Snapshot.Version,
                        $"installer-lifecycle:{failed.Request.OperationId:D}", "Unconfirmed lifecycle resume; preserve paused admission",
                        cancellationToken).ConfigureAwait(false);
                }
                if (capture.Snapshot.State != CaptureAdmissionState.Paused)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            var (current, cancelled) = await controls.ExchangeAsync(exchange, cancellationToken).ConfigureAwait(false);
            if (exchange.Receipt is { IsTerminal: true } receipt)
                await automations.CompleteInstallerLifecycleAsync(receipt, cancellationToken).ConfigureAwait(false);
            var safeWindow = current?.Request.Kind == CameraAgentControlKind.Restart &&
                await window.IsSafeAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = capture.Snapshot;
            return Results.Ok(new CameraAgentControlSnapshot(current, cancelled, controls.ProcessId,
                snapshot.State.ToString(), snapshot.Version, snapshot.IsInitialized,
                CameraAgentRestart.IsSupervised(configuration), safeWindow));
        }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (InvalidOperationException) { return Results.Conflict(); }
    }

    /// <summary>Manual pause and unsafe/unknown solar data are never mistaken for a closed capture window.</summary>
    internal static bool IsRestartWindowSafe(CaptureScheduleDecision decision, DateTimeOffset now) =>
        !decision.Admitted && decision.Reason is not (CaptureScheduleAdmissionReason.ManualPause or
            CaptureScheduleAdmissionReason.NoSolarEvent or CaptureScheduleAdmissionReason.SafetyUnavailable) &&
        decision.NextTransitionUtc is { } next && next >= now + TimeSpan.FromMinutes(4);

    private static async Task<IResult> RestartAsync(CameraAgentControlRestart command, HttpContext context,
        IConfiguration configuration, CameraAgentControlStore controls, CaptureAdmissionCoordinator capture,
        IHostApplicationLifetime lifetime, TimeProvider clock, ICameraAgentControlRestartWindow window, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!CameraAgentLifecycleEndpoints.IsAuthorized(context, configuration)) return Results.Unauthorized();
        if (!await window.IsSafeAsync(cancellationToken).ConfigureAwait(false) || capture.Snapshot.State != CaptureAdmissionState.Paused ||
            !await controls.MayRestartAsync(command, capture.Snapshot.Version, cancellationToken).ConfigureAwait(false))
            return Results.Conflict();
        var disposition = CameraAgentRestart.Request(lifetime, configuration, $"installer-lifecycle:{command.OperationId:D}",
            CameraAgentRestart.DefaultDelay, clock, loggers.CreateLogger("InstallerLifecycle"),
            LogRestartRequested);
        return disposition == CameraAgentRestartDisposition.Unsupervised ? Results.Conflict() : Results.Accepted();
    }
    [LoggerMessage(LogLevel.Information, "Installer lifecycle restart requested by {Actor}.")]
    private static partial void LogRestartRequested(ILogger logger, string actor);
}
