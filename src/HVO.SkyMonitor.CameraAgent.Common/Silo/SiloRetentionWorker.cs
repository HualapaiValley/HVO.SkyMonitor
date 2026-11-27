using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed partial class SiloRetentionWorker(SiloInstalledStorage storage, RawCaptureIngress raw,
    IOptions<CameraAgentHostOptions> options, CaptureMaintenanceGate maintenance, IServiceProvider services,
    SiloArchiveWorker archive, TimeProvider clock, ILogger<SiloRetentionWorker> logger) : BackgroundService, ICameraAgentSiloRetention
{
    public bool Ready => storage.BindingVerified && raw.SiloAdmissionsReady;

    public ValueTask SetPolicyAsync(CameraAgentSiloRetentionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.Roles);
        if (policy.Roles.Count > 128 || policy.Roles.Any(static item => item is null))
            throw new ArgumentException("Invalid Silo role policies.", nameof(policy));
        return storage.Retention.SetPolicyAsync(new(policy.ExpiryHeld, policy.Roles.Select(static role =>
            new SiloRoleRetention(role.Role, role.MaximumAge, role.ExpireUnderPressure, role.AllowLastCopyExpiry)).ToArray()),
            cancellationToken);
    }

    public ValueTask SetPinAsync(CameraAgentSiloPin pin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var kind = pin.Kind switch
        {
            "member" => SiloIdentityKind.Member,
            "manifest" => SiloIdentityKind.Manifest,
            _ => throw new ArgumentException("A pin targets a member or manifest.", nameof(pin))
        };
        SiloText.RequireToken(pin.PinId, nameof(pin), 128);
        SiloText.RequireText(pin.ArtifactId, nameof(pin), 256);
        SiloText.RequireText(pin.Revision, nameof(pin), 256);
        SiloText.RequireText(pin.Reason, nameof(pin), 512);
        return storage.Retention.SetPinAsync(pin.PinId, new(kind, pin.ArtifactId, pin.Revision), pin.Active, pin.Reason, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<CameraAgentSiloRetentionCandidate>> DryRunAsync(
        DateTimeOffset asOfUtc, bool underPressure, CancellationToken cancellationToken)
    {
        var lifecycle = RawIngressLifecycleLock.ForRoot(options.Value.RawIngressRoot);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var holds = await ReadHoldsAsync(cancellationToken).ConfigureAwait(false);
            var candidates = await storage.Retention.DryRunAsync(asOfUtc, underPressure, holds, cancellationToken).ConfigureAwait(false);
            return candidates.Select(static item => new CameraAgentSiloRetentionCandidate(item.Locator.Key, item.Locator.VersionId,
                item.Decision, item.References.Select(static reference => reference.Role).Distinct(StringComparer.Ordinal).ToArray(),
                item.References.Count > 0 ? item.References[0].Identity.Length : 0)).ToArray();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            string status;
            try
            {
                status = await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                SiloConflictException or SiloDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                status = "reconciliation-blocked";
                ReportFailure(logger, exception);
            }
            if (status != previous) ReportState(logger, status);
            previous = status;
        } while (await NextTickAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async ValueTask<bool> NextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<string> RunAsync(CancellationToken cancellationToken)
    {
        storage.DiskBudget.Observe();
        if (!storage.BindingVerified) return "awaiting-storage";

        using var lease = maintenance.TryEnterIfInitialized();
        var lifecycle = RawIngressLifecycleLock.ForRoot(options.Value.RawIngressRoot);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var controls = services.GetService<CameraAgentControlStore>();
            var canStartDelete = raw.SiloAdmissionsReady && lease is not null && controls?.AdmissionHeld != true &&
                !(await storage.Index.ReadRetentionPolicyAsync(cancellationToken).ConfigureAwait(false)).ExpiryHeld;
            var holds = canStartDelete ? await ReadHoldsAsync(cancellationToken).ConfigureAwait(false) : [];
            return await storage.Retention.SweepAsync(canStartDelete,
                _ => ValueTask.FromResult(holds), archive, cancellationToken,
                canIssue: () => controls?.AdmissionHeld != true).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadHoldsAsync(CancellationToken cancellationToken)
    {
        var root = options.Value.RawIngressRoot;
        var rawHolds = await raw.GetRetentionHoldsUnderLifecycleLockAsync(root, cancellationToken).ConfigureAwait(false);
        var processing = (IRawIngressLifecycleProcessingRetentionHolds)services.GetRequiredService<IProcessingRetentionHolds>();
        var processingHolds = await processing.GetRetentionHoldsUnderLifecycleLockAsync(root, cancellationToken).ConfigureAwait(false);
        var masterIds = await services.GetRequiredService<SqliteCalibrationFamilyStore>().GetActiveMasterArtifactIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        var videoHolds = await services.GetRequiredService<SqliteTimeLapseStore>().ReadSiloTimeLapseHoldsAsync(cancellationToken)
            .ConfigureAwait(false);
        var nightlyHolds = await ReadNightlyHoldsAsync(cancellationToken).ConfigureAwait(false);
        var satelliteHolds = await services.GetRequiredService<CaptureSatelliteWorkStore>().ReadSiloArchiveHoldsAsync(cancellationToken)
            .ConfigureAwait(false);
        return rawHolds.Select(static hold => new SiloRetentionHold(SiloHoldKind.ProcessingInput, hold.ArtifactId.ToString("D")))
            .Concat(processingHolds.Select(static hold => new SiloRetentionHold(SiloHoldKind.ProcessingInput, hold.ArtifactId.ToString("D"))))
            .Concat(masterIds.Select(static id => new SiloRetentionHold(SiloHoldKind.CalibrationMaster, id.ToString("D"))))
            .Concat(videoHolds)
            .Concat(nightlyHolds)
            .Concat(satelliteHolds)
            .Distinct().ToArray();
    }

    private async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadNightlyHoldsAsync(CancellationToken cancellationToken)
    {
        var automation = services.GetRequiredService<ILocalAutomationStore>();
        var state = await automation.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var catalog = services.GetRequiredService<INightlyProductCatalog>();
        var result = new HashSet<SiloRetentionHold>();
        foreach (var run in state.ActiveRuns)
        {
            if (run.Occurrence is not { SourceWindow: { } window } occurrence ||
                occurrence.Definition.TaskKind != LocalAutomationTaskKind.StillImageGeneration ||
                !NightlyProductPreset.TryParseTarget(occurrence.Definition.TaskTarget, out var kind))
                continue;
            var day = await catalog.GetDayAsync(window.ReportingPeriod.ReportDate, cancellationToken).ConfigureAwait(false);
            foreach (var summary in day.Products)
            {
                if ((summary.Kind != kind && kind != NightlyProductKind.DailySummary) ||
                    summary.WindowStartUtc < window.StartUtc || summary.WindowEndUtc > window.EndUtc)
                    continue;
                var detail = await catalog.GetAsync(summary.ProductId, cancellationToken).ConfigureAwait(false);
                if (detail?.Occurrence.SourceWindow?.ReportingPeriod.IdentitySha256 != window.ReportingPeriod.IdentitySha256)
                    continue;
                var id = summary.ProductId.ToString("D");
                result.Add(new(SiloHoldKind.NightlyInput, id, detail.OutputIdentitySha256));
                result.Add(new(SiloHoldKind.NightlyInput, id, "rendition-1"));
                result.Add(new(SiloHoldKind.NightlyInput, id, "provenance-1"));
            }
        }
        return result.ToArray();
    }

    [LoggerMessage(7398, LogLevel.Information, "CameraAgent Silo retention {State}")]
    private static partial void ReportState(ILogger logger, string state);

    [LoggerMessage(7399, LogLevel.Warning, "CameraAgent Silo retention reconciliation is blocked")]
    private static partial void ReportFailure(ILogger logger, Exception exception);
}
