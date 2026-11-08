using HVO.SkyMonitor.CameraAgent.Common.Automation;
using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Common.Security;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.Catalog.Sqlite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Read projections for the System pages: registered health checks, host process facts, the running software and
/// installed catalog, and the durable control receipts. Every value is mapped to a fixed label or a number; no
/// health-check description or data, filesystem path, actor identifier or idempotency key leaves this service.
/// </summary>
internal interface ICameraAgentSystemUiService
{
    ValueTask<OperatorUiResult<CameraAgentControlView>> GetLifecycleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(false, [])));

    ValueTask<OperatorUiResult<CameraAgentControlReceipt>> RequestDrainAsync(Guid operationId, long expectedVersion,
        string reason, CancellationToken cancellationToken) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentControlReceipt>.Failure(OperatorUiResultKind.Unavailable, "The installer lifecycle worker is unavailable."));

    ValueTask<OperatorUiResult<bool>> CancelLifecycleAsync(Guid operationId, CancellationToken cancellationToken, string reason = "Operator requested lifecycle cancellation") =>
        ValueTask.FromResult(OperatorUiResult<bool>.Failure(OperatorUiResultKind.Unavailable, "The installer lifecycle worker is unavailable."));

    ValueTask<OperatorUiResult<SystemHealthView>> GetHealthAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<SystemSoftwareView>> GetSoftwareAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<IReadOnlyList<SystemControlReceipt>>> GetControlReceiptsAsync(
        int limit,
        CancellationToken cancellationToken);
}

internal enum SystemCheckState
{
    Healthy,
    Degraded,
    Unhealthy,
    Disabled
}

internal sealed record SystemHealthCheck(
    string Name,
    string Label,
    string Scope,
    SystemCheckState State,
    TimeSpan Duration,
    string? Href);

internal sealed record SystemHostFacts(
    DateTimeOffset StartedUtc,
    TimeSpan Uptime,
    long WorkingSetBytes,
    double? AverageCpuPercent,
    int ProcessorCount,
    string Runtime,
    string RuntimeIdentifier);

/// <param name="Drift">This agent's clock minus network time, positive when it runs ahead; null when not measured.</param>
internal sealed record SystemClockFact(ClockSyncStatus Status, TimeSpan? Drift, DateTimeOffset? MeasuredUtc);

internal sealed record SystemHealthView(
    DateTimeOffset EvaluatedUtc,
    SystemCheckState Overall,
    IReadOnlyList<SystemHealthCheck> Checks,
    SystemHostFacts Host,
    bool CentralIntegrationEnabled,
    SystemClockFact? Clock = null);

internal sealed record SystemCatalogView(
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

internal sealed record SystemSoftwareView(
    string Version,
    string? SourceRevision,
    string Runtime,
    string RuntimeIdentifier,
    DateTimeOffset StartedUtc,
    TimeSpan Uptime,
    SystemCatalogView Catalog);

internal enum SystemControlReceiptKind
{
    PauseCapture,
    ResumeCapture,
    OverrideCreated,
    OverrideConsumed,
    OverrideCleared,
    Drain,
    ScheduledRestart
}

internal enum SystemControlActor
{
    LocalOwner,
    Installer,
    System
}

internal enum SystemControlOutcome
{
    Applied,
    NoChange,
    Pending,
    Failed,
    Cancelled,
    Skipped
}

internal sealed record SystemControlReceipt(
    DateTimeOffset OccurredUtc,
    SystemControlReceiptKind Kind,
    SystemControlActor Actor,
    string? Reason,
    SystemControlOutcome Outcome,
    CaptureScheduleOverrideMode? OverrideMode,
    DateTimeOffset? OverrideStartUtc,
    DateTimeOffset? OverrideEndUtc,
    string? Detail = null);

internal sealed class CameraAgentSystemUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    HealthCheckService healthChecks,
    CaptureAdmissionCoordinator captureControl,
    SqliteCaptureScheduleStore scheduleStore,
    CatalogSnapshotResult catalog,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<CameraAgentSystemUiService> logger,
    IClockSyncMonitor? clockMonitor = null,
    CameraAgentControlStore? controls = null, SqliteLocalAutomationStore? automations = null) : ICameraAgentSystemUiService
{
    internal const int MaxReceipts = 25;
    internal static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(15);

    // Fixed presentation for every registered check. A name missing here still renders, under its registration
    // name and the "Other" scope, so a new check is never silently hidden.
    private static readonly Dictionary<string, (string Label, string Scope, string? Href)> CheckCatalog =
        new(StringComparer.Ordinal)
        {
            ["self"] = ("Host process", "Host", null),
            ["identity-database"] = ("Identity database", "Local", null),
            ["owner-bootstrap"] = ("Owner account", "Local", null),
            ["catalog"] = ("Celestial catalog", "Local", "/operations/software"),
            ["orbital-elements"] = ("Orbital elements", "Local", "/operations/software"),
            ["space-weather"] = ("Space weather", "Local", "/operations/space-weather"),
            ["deployment-location"] = ("Deployment location", "Local", "/operations/site"),
            ["disk-pressure"] = ("Storage capacity", "Local", "/operations/storage"),
            ["camera-configuration"] = ("Camera configuration", "Acquisition", "/operations/camera"),
            ["capture-admission"] = ("Capture admission", "Acquisition", "/operations/control"),
            ["clock"] = ("Clock", "Acquisition", "/operations/control"),
            ["raw-ingress"] = ("Raw ingress", "Acquisition", "/operations/storage"),
            ["capture-lanes"] = ("Capture lanes", "Acquisition", "/operations/storage"),
            ["environmental-acquisition"] = ("Environmental acquisition", "Acquisition", "/operations/environment"),
            ["capture-processing"] = ("Capture processing", "Processing", "/operations/pipeline"),
            ["processing-graph-delivery"] = ("Processing graph delivery", "Processing", "/operations/pipeline"),
            ["calibration-library"] = ("Calibration library", "Processing", "/operations/calibration"),
            ["transient-worker"] = ("Transient worker", "Processing", "/operations/transients"),
            ["artifact-outbox"] = ("Artifact delivery", "Central", "/operations/delivery"),
            ["fleet-heartbeat"] = ("Fleet heartbeat", "Central", "/operations/registration"),
            ["deployment-location-reconciliation"] = ("Location acknowledgement", "Central", "/operations/site"),
            ["environmental-delivery"] = ("Environmental delivery", "Central", "/operations/environment"),
            ["transient-candidate-delivery"] = ("Transient delivery", "Central", "/operations/transients"),
        };

    private static readonly string[] ScopeOrder = ["Host", "Acquisition", "Processing", "Local", "Central", "Other"];

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<SystemHealthView>> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return Denied<SystemHealthView>();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HealthTimeout);
        try
        {
            var report = await healthChecks.CheckHealthAsync(timeout.Token).ConfigureAwait(false);
            var centralEnabled = options.Value.CentralIntegration.Mode != CentralIntegrationMode.Disabled;
            var checks = report.Entries
                .Select(entry => MapCheck(entry.Key, entry.Value, centralEnabled))
                .OrderBy(static check => Array.IndexOf(ScopeOrder, check.Scope))
                .ThenBy(static check => check.Label, StringComparer.Ordinal)
                .ToArray();
            var overall = checks
                .Where(static check => check.State != SystemCheckState.Disabled)
                .Select(static check => check.State)
                .DefaultIfEmpty(SystemCheckState.Healthy)
                .Max();
            return OperatorUiResult<SystemHealthView>.Success(new SystemHealthView(
                timeProvider.GetUtcNow(), overall, checks, SampleHost(), centralEnabled, ClockFact()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable<SystemHealthView>(
                $"The health checks did not finish within {HealthTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("System health projection failed with {ExceptionType}.", exception.GetType().Name);
            return Unavailable<SystemHealthView>("The health checks could not be run.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<SystemSoftwareView>> GetSoftwareAsync(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return Denied<SystemSoftwareView>();
        }
        try
        {
            var informational = typeof(CameraAgentSystemUiService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var (version, revision) = SplitVersion(
                informational ?? typeof(CameraAgentSystemUiService).Assembly.GetName().Version?.ToString());
            var host = SampleHost();
            return OperatorUiResult<SystemSoftwareView>.Success(new SystemSoftwareView(
                version,
                revision,
                host.Runtime,
                host.RuntimeIdentifier,
                host.StartedUtc,
                host.Uptime,
                new SystemCatalogView(
                    catalog.CatalogId,
                    catalog.CatalogVersion,
                    catalog.PackageKind.ToString(),
                    catalog.SnapshotVersion,
                    catalog.ManifestVersion,
                    catalog.SchemaVersion,
                    catalog.PreprocessingVersion,
                    catalog.RowCount,
                    catalog.DatabaseLength,
                    catalog.DatabaseSha256)));
        }
        catch (Exception exception)
        {
            logger.LogWarning("Software projection failed with {ExceptionType}.", exception.GetType().Name);
            return Unavailable<SystemSoftwareView>("The software inventory could not be read.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<IReadOnlyList<SystemControlReceipt>>> GetControlReceiptsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return Denied<IReadOnlyList<SystemControlReceipt>>();
        }
        var bounded = Math.Clamp(limit, 1, MaxReceipts);
        try
        {
            var commands = await captureControl.GetRecentCommandsAsync(bounded, cancellationToken).ConfigureAwait(false);
            var overrides = await scheduleStore.GetRecentOverrideEventsAsync(bounded, cancellationToken)
                .ConfigureAwait(false);
            var lifecycle = controls is null ? new CameraAgentControlView(false, []) : await controls.ReadAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SystemControlReceipt> receipts = commands
                .Select(static command => new SystemControlReceipt(
                    command.CompletedUtc ?? command.RequestedUtc,
                    command.TargetState == CaptureAdmissionState.Paused
                        ? SystemControlReceiptKind.PauseCapture
                        : SystemControlReceiptKind.ResumeCapture,
                    MapActor(command.Actor),
                    command.Reason,
                    !command.IsComplete
                        ? SystemControlOutcome.Pending
                        : command.Changed ? SystemControlOutcome.Applied : SystemControlOutcome.NoChange,
                    null,
                    null,
                    null))
                .Concat(overrides.Select(static item => new SystemControlReceipt(
                    item.OccurredUtc,
                    item.Kind switch
                    {
                        CaptureScheduleOverrideEventKind.Created => SystemControlReceiptKind.OverrideCreated,
                        CaptureScheduleOverrideEventKind.Consumed => SystemControlReceiptKind.OverrideConsumed,
                        _ => SystemControlReceiptKind.OverrideCleared
                    },
                    MapActor(item.Actor),
                    item.Reason,
                    SystemControlOutcome.Applied,
                    item.Mode,
                    item.StartUtc,
                    item.EndUtc)))
                .Concat(lifecycle.Receipts.Select(static item => new SystemControlReceipt(item.Events[^1].Utc,
                    item.Request.Kind == CameraAgentControlKind.Drain ? SystemControlReceiptKind.Drain : SystemControlReceiptKind.ScheduledRestart,
                    MapActor(item.Request.Actor), item.Request.Reason, item.Phase switch
                    {
                        CameraAgentControlPhase.Succeeded => SystemControlOutcome.Applied,
                        CameraAgentControlPhase.Failed => SystemControlOutcome.Failed,
                        CameraAgentControlPhase.Cancelled => SystemControlOutcome.Cancelled,
                        CameraAgentControlPhase.Skipped => SystemControlOutcome.Skipped,
                        _ => SystemControlOutcome.Pending
                    }, null, null, null, LifecycleDetail(item))))
                .OrderByDescending(static receipt => receipt.OccurredUtc)
                .Take(bounded)
                .ToArray();
            return OperatorUiResult<IReadOnlyList<SystemControlReceipt>>.Success(receipts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Control receipt projection failed with {ExceptionType}.", exception.GetType().Name);
            return Unavailable<IReadOnlyList<SystemControlReceipt>>("Control receipts could not be read.");
        }
    }

    public async ValueTask<OperatorUiResult<CameraAgentControlView>> GetLifecycleAsync(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false)) return Denied<CameraAgentControlView>();
        if (controls is null) return OperatorUiResult<CameraAgentControlView>.Success(new(false, []));
        try
        {
            var view = await controls.ReadAsync(cancellationToken).ConfigureAwait(false);
            var principal = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            var canMutate = (await authorizationService.AuthorizeAsync(principal,
                CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded;
            return OperatorUiResult<CameraAgentControlView>.Success(view with { Available = view.Available && canMutate });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Unavailable<CameraAgentControlView>("Installer lifecycle state could not be read.");
        }
    }

    public async ValueTask<OperatorUiResult<CameraAgentControlReceipt>> RequestDrainAsync(Guid operationId, long expectedVersion,
        string reason, CancellationToken cancellationToken)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (!(await authorizationService.AuthorizeAsync(principal, CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false)).Succeeded || CameraAgentCredentialAccess.GetOwnerId(principal) is not { Length: > 0 } actor)
            return Denied<CameraAgentControlReceipt>();
        if (controls is null) return Unavailable<CameraAgentControlReceipt>("The installer lifecycle worker is unavailable.");
        try
        {
            var request = new CameraAgentControlRequest(operationId, CameraAgentControlKind.Drain, actor, reason.Trim(),
                expectedVersion, timeProvider.GetUtcNow());
            var existing = await controls.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!existing.Receipts.Any(item => item.Request.OperationId == operationId) &&
                (!captureControl.Snapshot.IsInitialized || captureControl.Snapshot.Version != expectedVersion ||
                    captureControl.Snapshot.State is not (CaptureAdmissionState.Running or CaptureAdmissionState.Paused)))
                return OperatorUiResult<CameraAgentControlReceipt>.Failure(OperatorUiResultKind.Conflict, "Capture state changed. Refresh and confirm again.");
            return OperatorUiResult<CameraAgentControlReceipt>.Success(await controls.SubmitAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException) { return OperatorUiResult<CameraAgentControlReceipt>.Failure(OperatorUiResultKind.Invalid, "A reason of 1 to 512 printable characters is required."); }
        catch (InvalidOperationException) { return OperatorUiResult<CameraAgentControlReceipt>.Failure(OperatorUiResultKind.Conflict, "The installer worker is unavailable or another lifecycle operation is pending."); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { return Unavailable<CameraAgentControlReceipt>("Installer lifecycle intent could not be recorded. Refresh before retrying."); }
    }

    public async ValueTask<OperatorUiResult<bool>> CancelLifecycleAsync(Guid operationId, CancellationToken cancellationToken, string reason = "Operator requested lifecycle cancellation")
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (!(await authorizationService.AuthorizeAsync(principal, CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false)).Succeeded) return Denied<bool>();
        if (controls is null) return Unavailable<bool>("The installer lifecycle worker is unavailable.");
        try
        {
            var actor = CameraAgentCredentialAccess.GetOwnerId(principal);
            if (string.IsNullOrWhiteSpace(actor)) return Denied<bool>();
            await controls.CancelAsync(operationId, cancellationToken, actor, reason.Trim()).ConfigureAwait(false);
            var receipt = (await controls.ReadAsync(cancellationToken).ConfigureAwait(false)).Receipts
                .FirstOrDefault(item => item.Request.OperationId == operationId);
            if (receipt is { IsTerminal: true } && automations is not null)
                await automations.CompleteInstallerLifecycleAsync(receipt, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<bool>.Success(true);
        }
        catch (ArgumentException) { return OperatorUiResult<bool>.Failure(OperatorUiResultKind.Invalid, "A cancellation reason of 1 to 512 printable characters is required."); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { return Unavailable<bool>("Lifecycle cancellation could not be recorded. Refresh before retrying."); }
    }

    internal static string LifecycleDetail(CameraAgentControlReceipt receipt) => LifecycleOutcomeDetail(receipt) +
        (receipt.Cancellation is { } cancelled ? $" Cancelled by {cancelled.Actor} at {cancelled.Utc:O}: {cancelled.Reason}." : string.Empty);

    private static string LifecycleOutcomeDetail(CameraAgentControlReceipt receipt) => receipt.Phase switch
    {
        CameraAgentControlPhase.Queued => "Waiting for the installer worker.",
        CameraAgentControlPhase.Draining => "Stopping acquisition and settling accepted processing and delivery.",
        CameraAgentControlPhase.Restarting => "Installer requested a supervised CameraAgent restart.",
        CameraAgentControlPhase.Verifying => "Installer is waiting for a new process and verifying durable continuity.",
        CameraAgentControlPhase.Succeeded when receipt.Request.Kind == CameraAgentControlKind.Drain => "Drained; capture remains paused until explicitly resumed.",
        CameraAgentControlPhase.Succeeded => receipt.ResumeAfterVerification ? "Restart verified; prior running state restored." : "Restart verified; prior paused state preserved.",
        CameraAgentControlPhase.Skipped => "Restart skipped: capture window or supervisor requirements were not met.",
        _ when receipt.ResumeConfirmed && (!receipt.RestartRequested || receipt.RestartRefused) => "Restart was not issued; prior running state restored. " + receipt.Events[^1].Code,
        _ when receipt.ResumeRequested => "Resume was not confirmed; capture was paused before this failure receipt was published. " + receipt.Events[^1].Code,
        _ => "Lifecycle did not complete. No automatic resume was issued; check capture state and the installer receipt. " + receipt.Events[^1].Code
    };

    internal static SystemControlActor MapActor(string actor)
        => actor.StartsWith("installer-lifecycle:", StringComparison.Ordinal)
            ? SystemControlActor.Installer
            : (actor.StartsWith("automation:", StringComparison.Ordinal) || string.Equals(actor, "system", StringComparison.Ordinal))
                ? SystemControlActor.System
                : SystemControlActor.LocalOwner;

    internal static (string Version, string? Revision) SplitVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return ("unknown", null);
        }
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informational, null);
        }
        var revision = informational[(plus + 1)..];
        return (informational[..plus], revision.Length > 12 ? revision[..12] : revision);
    }

    private static SystemHealthCheck MapCheck(string name, HealthReportEntry entry, bool centralEnabled)
    {
        var (label, scope, href) = CheckCatalog.TryGetValue(name, out var known)
            ? known
            : (name, "Other", (string?)null);
        var disabled = (entry.Data.TryGetValue("Availability", out var availability) &&
                string.Equals(availability as string, "Disabled", StringComparison.Ordinal)) ||
            (!centralEnabled && string.Equals(scope, "Central", StringComparison.Ordinal));
        var state = disabled
            ? SystemCheckState.Disabled
            : entry.Status switch
            {
                HealthStatus.Healthy => SystemCheckState.Healthy,
                HealthStatus.Degraded => SystemCheckState.Degraded,
                _ => SystemCheckState.Unhealthy
            };
        return new SystemHealthCheck(name, label, scope, state, entry.Duration, href);
    }

    private SystemClockFact? ClockFact()
    {
        if (clockMonitor is null)
        {
            return null;
        }
        var settings = clockMonitor.Settings;
        var latest = clockMonitor.Latest;
        var status = ClockAssessment.Evaluate(settings, latest).Status;
        if (!settings.Enabled || latest is null || !latest.Enabled)
        {
            return new SystemClockFact(status, null, null);
        }
        return new SystemClockFact(status, latest.Selected?.Offset?.Negate(), latest.MeasuredUtc);
    }

    private SystemHostFacts SampleHost()
    {
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var now = timeProvider.GetUtcNow();
        var uptime = now > started ? now - started : TimeSpan.Zero;
        var processors = Environment.ProcessorCount;
        double? cpu = uptime.TotalSeconds >= 1 && processors > 0
            ? Math.Clamp(process.TotalProcessorTime.TotalSeconds / (uptime.TotalSeconds * processors) * 100, 0, 100)
            : null;
        return new SystemHostFacts(
            started,
            uptime,
            Environment.WorkingSet,
            cpu,
            processors,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier);
    }

    private async ValueTask<bool> IsAuthorizedAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return (await authorizationService.AuthorizeAsync(
            state.User, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded;
    }

    private static OperatorUiResult<T> Denied<T>() => OperatorUiResult<T>.Failure(
        OperatorUiResultKind.Unauthorized,
        "You are not authorized for this operation.");

    private static OperatorUiResult<T> Unavailable<T>(string message) =>
        OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);
}
