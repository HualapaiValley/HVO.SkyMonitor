using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The delivered composition below the host: the real orbital store, refresh job and automation adapter registered in
/// the real automation registry, a real durable automation store, the runner, and the operator operations. The only
/// substitutes are the scripted provider client and the manual clock, so nothing here can reach the network.
/// </summary>
internal sealed class OrbitalAutomationFixture : IDisposable
{
    private readonly PeriodicRegistryHarness _registry;

    private OrbitalAutomationFixture(OrbitalRefreshTestHarness orbital, string automationRoot)
    {
        Orbital = orbital;
        Availability = new OrbitalElementsAvailability(orbital.Store, new OrbitalTestLogger<OrbitalElementsAvailability>());
        Adapter = new OrbitalElementsAutomationAdapter(Availability, orbital.Job);
        _registry = new PeriodicRegistryHarness(orbital.Clock, [Adapter]);
        AutomationOptions = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = automationRoot,
            OrbitalElements = orbital.Options.Value.OrbitalElements
        });
        Automation = new SqliteLocalAutomationStore(_registry.Registry, Captures, AutomationOptions, orbital.Clock,
            NullLogger<SqliteLocalAutomationStore>.Instance);
        Logger = new OrbitalTestLogger<OrbitalElementsOperations>();
        Operations = new OrbitalElementsOperations(orbital.Intercept, Availability, orbital.Job, Automation,
            orbital.Options, orbital.Clock, Logger);
    }

    public OrbitalRefreshTestHarness Orbital { get; }

    public OrbitalElementsAvailability Availability { get; }

    public OrbitalElementsAutomationAdapter Adapter { get; }

    public LocalAutomationTaskRegistry Registry => _registry.Registry;

    public Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions> AutomationOptions { get; }

    public StubCaptureSequenceSource Captures { get; } = new();

    public SqliteLocalAutomationStore Automation { get; }

    public OrbitalTestLogger<OrbitalElementsOperations> Logger { get; }

    public OrbitalElementsOperations Operations { get; }

    public SignalingTimeProvider Clock => Orbital.Clock;

    /// <summary>
    /// Creates the fixture with the orbital store at <paramref name="orbitalRoot"/> and the automation database under
    /// its parent, as the host lays them out. The orbital store is initialized unless <paramref name="initialize"/> is
    /// false, which lets a test hold the root with another owner first.
    /// </summary>
    public static async Task<OrbitalAutomationFixture> CreateAsync(string orbitalRoot, bool enabled = true,
        bool initialize = true, IOrbitalStorageFaultInjector? faults = null)
    {
        var orbital = await OrbitalRefreshTestHarness.CreateAsync(orbitalRoot, enabled, faults, initialize).ConfigureAwait(false);
        OrbitalAutomationFixture fixture;
        try
        {
            fixture = new OrbitalAutomationFixture(orbital, Path.GetDirectoryName(orbitalRoot)!);
        }
        catch
        {
            orbital.Dispose();
            throw;
        }
        try
        {
            await fixture.Automation.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>Saves the reserved definition through the real automation store, so the adapter validates it.</summary>
    public async Task<LocalAutomationCommandResult> SaveDefinitionAsync(bool enabled = true, int interval = 7_200,
        long expectedVersion = 0, string? key = null)
        => await Automation.SaveAsync(new LocalAutomationSaveRequest(OrbitalElementsAutomationAdapter.DefinitionId,
            "Orbital elements refresh", enabled, LocalAutomationTaskKind.OrbitalElementsRefresh, CelesTrakGpSource.DatasetKey,
            LocalAutomationTriggerKind.Periodic, interval, expectedVersion, key ?? Guid.NewGuid().ToString("N"), "owner",
            "orbital schedule"), CancellationToken.None).ConfigureAwait(false);

    public LocalAutomationRunnerService CreateRunner() => new(Automation, Registry, Captures, AutomationOptions, Clock,
        NullLogger<LocalAutomationRunnerService>.Instance);

    public Task<LocalAutomationOperatorState> AutomationStateAsync()
        => Automation.GetStateAsync(CancellationToken.None).AsTask();

    public void Dispose()
    {
        Automation.Dispose();
        _registry.Dispose();
        Orbital.Dispose();
    }
}
