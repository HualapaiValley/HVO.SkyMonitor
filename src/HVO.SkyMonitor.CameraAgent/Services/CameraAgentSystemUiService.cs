using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
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

internal sealed record SystemHealthView(
    DateTimeOffset EvaluatedUtc,
    SystemCheckState Overall,
    IReadOnlyList<SystemHealthCheck> Checks,
    SystemHostFacts Host,
    bool CentralIntegrationEnabled);

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
    OverrideCleared
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
    Pending
}

internal sealed record SystemControlReceipt(
    DateTimeOffset OccurredUtc,
    SystemControlReceiptKind Kind,
    SystemControlActor Actor,
    string? Reason,
    SystemControlOutcome Outcome,
    CaptureScheduleOverrideMode? OverrideMode,
    DateTimeOffset? OverrideStartUtc,
    DateTimeOffset? OverrideEndUtc);

internal sealed class CameraAgentSystemUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    HealthCheckService healthChecks,
    CaptureAdmissionCoordinator captureControl,
    SqliteCaptureScheduleStore scheduleStore,
    CatalogSnapshotResult catalog,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<CameraAgentSystemUiService> logger) : ICameraAgentSystemUiService
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
            ["deployment-location"] = ("Deployment location", "Local", "/operations/site"),
            ["disk-pressure"] = ("Storage capacity", "Local", "/operations/storage"),
            ["camera-configuration"] = ("Camera configuration", "Acquisition", "/operations/camera"),
            ["capture-admission"] = ("Capture admission", "Acquisition", "/operations/control"),
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
                timeProvider.GetUtcNow(), overall, checks, SampleHost(), centralEnabled));
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

    internal static SystemControlActor MapActor(string actor)
        => actor.StartsWith("installer-lifecycle:", StringComparison.Ordinal)
            ? SystemControlActor.Installer
            : string.Equals(actor, "system", StringComparison.Ordinal)
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
