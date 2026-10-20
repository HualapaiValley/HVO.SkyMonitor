using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SatellitePlanningCompositionTests
{
    [TestMethod]
    public async Task ActualHostResolvesPlanningWithTheInstalledReaderAndDisabledTimeInputsAsync()
    {
        using var first = AssemblyHooks.Fixture.CreateCameraAgentScope();
        using var second = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var planner = first.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanner>();
        var reader = first.ServiceProvider.GetRequiredService<IOrbitalSnapshotReader>();
        var time = first.ServiceProvider.GetRequiredService<ICameraAgentOrbitalTimeSource>();
        Assert.IsInstanceOfType<CameraAgentSatellitePlanner>(planner);
        Assert.IsInstanceOfType<OrbitalSnapshotReader>(reader);
        Assert.IsInstanceOfType<CameraAgentPinnedOrbitalTimeSource>(time);
        Assert.AreSame(planner, second.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanner>());
        Assert.AreSame(reader, second.ServiceProvider.GetRequiredService<IOrbitalSnapshotReader>());
        Assert.AreSame(time, second.ServiceProvider.GetRequiredService<ICameraAgentOrbitalTimeSource>());
        var ui = first.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>();
        Assert.AreSame(ui, first.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>());
        Assert.AreNotSame(ui, second.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>());
        var options = first.ServiceProvider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>().CurrentValue;
        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.AllowStaleDiagnostics);
        Assert.IsNull(options.EarthOrientationPath);
        Assert.IsNull(options.EarthOrientationSha256);
        Assert.IsNull(options.LeapSecondsPath);
        Assert.IsNull(options.LeapSecondsSha256);
        Assert.IsNull(await time.ReadAsync(CancellationToken.None).ConfigureAwait(false));
    }
}
