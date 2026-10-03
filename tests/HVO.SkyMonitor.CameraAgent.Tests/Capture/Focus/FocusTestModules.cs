using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

/// <summary>Real VirtualSky modules for focus tests: one catalog star at the zenith, which projects to the frame centre.</summary>
internal static class FocusTestModules
{
    public static readonly DateTimeOffset SceneUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private const double Longitude = -113.878;
    private const double Latitude = 35.347;

    /// <summary>Where the zenith star lands in the default 484x304 frame, measured from the rendered pixels.</summary>
    public static readonly PixelPoint ZenithStar = new(242.54, 152.12);

    public static VirtualSkyCameraModule Create(double magnitude = 2, ProjectedSceneStore? sceneStore = null)
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(SceneUtc, Longitude) / 15d;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("focus-star", "Focus Star", rightAscension, Latitude, magnitude, 0.65)
        ]);
        return new VirtualSkyCameraModule(TimeProvider.System, catalog, sceneStore ?? new ProjectedSceneStore());
    }

    public static CameraModuleConfig Config(
        VirtualSimulatedFocusOptions? focus = null,
        CameraPixelFormat format = CameraPixelFormat.Mono16,
        int width = 484,
        int height = 304)
    {
        var options = new VirtualSkyCameraModuleOptions
        {
            Seed = 1017,
            FixedSceneUtc = SceneUtc,
            SimulatedFocus = focus
        };
        return new CameraModuleConfig(
            new ObservatoryLocation(Latitude, Longitude, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(options)),
            new CameraRigConfig(
                new SensorProfile(
                    format switch
                    {
                        CameraPixelFormat.Mono16 => "VirtualAsi174Mm",
                        CameraPixelFormat.Rgb24 => "VirtualAsi174McRgb",
                        CameraPixelFormat.BayerRggb16 => "VirtualAsi178McRaw16",
                        _ => throw new ArgumentOutOfRangeException(nameof(format))
                    },
                    width, height, 5.86,
                    format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    format,
                    format switch
                    {
                        CameraPixelFormat.Mono16 => SensorResponseMode.Monochrome,
                        CameraPixelFormat.Rgb24 => SensorResponseMode.RenderedRgb,
                        CameraPixelFormat.BayerRggb16 => SensorResponseMode.BayerRaw,
                        _ => throw new ArgumentOutOfRangeException(nameof(format))
                    },
                    SensorRecipeVersion: format switch
                    {
                        CameraPixelFormat.Mono16 => "mono16-v1",
                        CameraPixelFormat.Rgb24 => "rgb24-compat-v1",
                        CameraPixelFormat.BayerRggb16 => "rggb16-v1",
                        _ => throw new ArgumentOutOfRangeException(nameof(format))
                    }),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    width / 2d, height / 2d, 0.98 * Math.Min(width, height) / 2d,
                    CalibrationVersion: "virtual-fisheye-180-equidistant-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            CapturePipelineConfig.Empty);
    }

    public static CaptureRequest Request(TimeSpan exposure, double gain)
        => new(SceneUtc, exposure, CaptureMode.Still, new CaptureSetpoint(exposure, gain, null, null));
}

/// <summary>
/// The real capture-owner boundary a focus preview crosses: a durable <see cref="CaptureAdmissionCoordinator"/> over a
/// temporary journal, the <see cref="CameraModuleOwnership"/> registry the capture owner publishes into, and the
/// production <see cref="CameraModuleManualFocusPreviewSource"/> joining them.
/// </summary>
internal sealed class FocusOwnerFixture : IAsyncDisposable
{
    private readonly CaptureControlTelemetry _telemetry;
    private readonly List<ICameraModule> _modules = [];

    private FocusOwnerFixture(string root, CaptureAdmissionCoordinator admission, CaptureControlTelemetry telemetry)
    {
        Root = root;
        Admission = admission;
        _telemetry = telemetry;
        Source = new CameraModuleManualFocusPreviewSource(Ownership, admission, TimeProvider.System);
    }

    public string Root { get; }

    public CaptureAdmissionCoordinator Admission { get; }

    public CameraModuleOwnership Ownership { get; } = new();

    public CameraModuleManualFocusPreviewSource Source { get; }

    public static async Task<FocusOwnerFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-focus-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var telemetry = new CaptureControlTelemetry();
        var admission = new CaptureAdmissionCoordinator(
            new JournalIngress(root),
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressSqliteBusyTimeoutSeconds = 1 }),
            TimeProvider.System,
            telemetry);
        await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        return new FocusOwnerFixture(root, admission, telemetry);
    }

    /// <summary>Initializes and publishes a VirtualSky module exactly as the capture owner does.</summary>
    public async Task<VirtualSkyCameraModule> PublishVirtualSkyAsync(
        double magnitude = 0,
        VirtualSimulatedFocusOptions? focus = null)
    {
        var module = FocusTestModules.Create(magnitude);
        var config = FocusTestModules.Config(focus);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        Publish(module, config);
        return module;
    }

    /// <summary>Wraps a VirtualSky module so a test can hold, fail, or count individual previews.</summary>
    public async Task<ScriptedPreviewModule> PublishScriptedAsync(double magnitude = 0)
    {
        var inner = FocusTestModules.Create(magnitude);
        var config = FocusTestModules.Config();
        await inner.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var module = new ScriptedPreviewModule(inner);
        Publish(module, config);
        return module;
    }

    public void Publish(ICameraModule module, CameraModuleConfig? config)
    {
        _modules.Add(module);
        Ownership.Publish(module, config);
    }

    /// <summary>Simulates an admitted normal capture holding the admission gate.</summary>
    public ValueTask<CaptureAdmissionCoordinator.CaptureAdmissionLease> EnterCaptureAsync(CancellationToken cancellationToken)
        => Admission.EnterAsync(cancellationToken);

    /// <summary>True when a normal capture could be admitted within <paramref name="wait"/>.</summary>
    public async Task<bool> CaptureAdmittedWithinAsync(TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            using var lease = await Admission.EnterAsync(timeout.Token).ConfigureAwait(false);
            lease.MarkNoPublicationRequired();
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Ownership.RevokeAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Ownership.Dispose();
        foreach (var module in _modules)
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
        Admission.Dispose();
        _telemetry.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    private sealed class JournalIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }
}

/// <summary>A real VirtualSky module whose previews a test can hold, fail, or count.</summary>
internal sealed class ScriptedPreviewModule(VirtualSkyCameraModule inner) : ICameraModule, ICameraFocusPreviewCapture
{
    private int _previews;

    public TaskCompletionSource PreviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Runs before preview <c>n</c> (1-based) renders, with the token the source passed in.</summary>
    public Func<int, CancellationToken, Task>? BeforePreview { get; set; }

    public int Previews => Volatile.Read(ref _previews);

    public int CancelledPreviews { get; private set; }

    public string Id => inner.Id;

    public string DisplayName => inner.DisplayName;

    public string ModuleType => inner.ModuleType;

    public CameraModuleCapabilities Capabilities => inner.Capabilities;

    public CameraFocusPreviewFidelity FocusPreviewFidelity => inner.FocusPreviewFidelity;

    public CameraSimulatedFocusModel? SimulatedFocus => inner.SimulatedFocus;

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        => inner.InitializeAsync(config, cancellationToken);

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        => inner.CaptureAsync(request, cancellationToken);

    public async Task<CaptureResult> CaptureFocusPreviewAsync(
        CameraFocusPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _previews);
        PreviewStarted.TrySetResult();
        try
        {
            if (BeforePreview is { } hook)
            {
                await hook(number, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelledPreviews++;
            throw;
        }
        return await inner.CaptureFocusPreviewAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>A module that can capture but declares no focus preview.</summary>
internal sealed class CaptureOnlyModule : ICameraModule
{
    public string Id => "capture-only";

    public string DisplayName => "Capture only";

    public string ModuleType => "CaptureOnly";

    public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Not used.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class FocusWait
{
    public static async Task<ManualFocusSessionSnapshot> UntilAsync(
        ManualFocusSessionCoordinator coordinator,
        Func<ManualFocusSessionSnapshot, bool> condition,
        string description,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var snapshot = coordinator.Snapshot;
            if (condition(snapshot))
            {
                return snapshot;
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {description}; state {snapshot.State}, end {snapshot.EndReason}, " +
                    $"samples {snapshot.TotalSamples}, last failure {snapshot.LastFailure}.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public static Task<ManualFocusSessionSnapshot> EndedAsync(ManualFocusSessionCoordinator coordinator, TimeSpan? timeout = null)
        => UntilAsync(coordinator, static snapshot => snapshot.EndedUtc is not null, "the session to end", timeout);
}
