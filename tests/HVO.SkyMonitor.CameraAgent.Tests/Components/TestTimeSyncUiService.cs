using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>Replaceable handlers for the Time panel on System control, with read counts and the saved lists.</summary>
internal sealed class TestTimeSyncUiService : ICameraAgentTimeSyncUiService
{
    internal Func<CancellationToken, ValueTask<OperatorUiResult<TimeSyncView>>> GetHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<TimeSyncView>.Success(View()));

    internal Func<CancellationToken, ValueTask<OperatorUiResult<TimeSyncCheckView>>> CheckHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<TimeSyncCheckView>.Success(new(ClockCheckOutcome.Measured, null)));

    internal Func<IReadOnlyList<string>, string, ValueTask<OperatorUiResult<TimeSyncServersSetting>>> SaveHandler { get; set; } =
        (servers, _) => ValueTask.FromResult(OperatorUiResult<TimeSyncServersSetting>.Success(
            new(servers, servers.Count == 0, "version-2", false)));

    internal int Reads { get; private set; }

    internal int Checks { get; private set; }

    internal List<(IReadOnlyList<string> Servers, string Version)> Saves { get; } = [];

    public ValueTask<OperatorUiResult<TimeSyncView>> GetAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return GetHandler(cancellationToken);
    }

    public ValueTask<OperatorUiResult<TimeSyncCheckView>> CheckNowAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return CheckHandler(cancellationToken);
    }

    public ValueTask<OperatorUiResult<TimeSyncServersSetting>> SaveServersAsync(
        IReadOnlyList<string> servers,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        Saves.Add((servers, expectedVersion));
        return SaveHandler(servers, expectedVersion);
    }

    /// <summary>
    /// A measured clock 12 ms ahead of pool.ntp.org, which answered at stratum 2, with a host time service reporting
    /// a 3 ms estimated error and a time server list saved in the operator settings file.
    /// </summary>
    internal static TimeSyncView View(
        ClockSyncStatus status = ClockSyncStatus.InTolerance,
        bool canChange = true,
        TimeSyncServersSetting? setting = null,
        int ignoredEntries = 0,
        KernelClockState? kernel = null,
        bool measured = true)
    {
        var selected = new TimeSyncServerView(
            "pool.ntp.org", null, TimeSpan.FromMilliseconds(-12), TimeSpan.FromMilliseconds(18), 2, true);
        IReadOnlyList<TimeSyncServerView> servers = measured
            ? [selected, new("time.example.org", SntpFailure.Timeout, null, null, null, false)]
            : [];
        return new TimeSyncView(
            OperatorUiTestData.Now,
            "America/Phoenix",
            TimeSpan.FromHours(-7),
            status,
            "The clock is within the 500 ms tolerance of network time.",
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMinutes(30),
            measured ? OperatorUiTestData.Now.AddMinutes(-4) : null,
            measured ? selected : null,
            servers,
            ignoredEntries,
            kernel ?? new KernelClockState(KernelClockStatus.Synchronized, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(3)),
            setting ?? new TimeSyncServersSetting(["pool.ntp.org", "time.example.org"], false, "version-1", false),
            canChange);
    }
}
