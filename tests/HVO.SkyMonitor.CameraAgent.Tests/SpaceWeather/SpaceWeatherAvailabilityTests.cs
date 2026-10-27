using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherAvailabilityTests
{
    [TestMethod]
    public async Task LockedStorageFailsClosedAndRecoversAfterTheRootIsReleased()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        using var store = new SqliteSwpcStore(SpaceWeatherServiceCollectionExtensions.CreateStoreSettings(host.Options.Value), NullLogger<SqliteSwpcStore>.Instance);
        using var available = new SpaceWeatherAvailability(store, NullLogger<SpaceWeatherAvailability>.Instance);
        Assert.IsFalse(available.IsAvailable); Assert.IsFalse(await available.EnsureAvailableAsync(default).ConfigureAwait(false));
        host.Sqlite.Dispose(); Assert.IsTrue(await available.EnsureAvailableAsync(default).ConfigureAwait(false)); Assert.IsTrue(available.IsAvailable);
    }
    [TestMethod]
    public async Task InitializationCancellationPropagatesWithoutBecomingAvailabilityFailure()
    {
        using var host = new SpaceWeatherTestHost(); using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Availability.EnsureAvailableAsync(cancellation.Token).AsTask()).ConfigureAwait(false);
        Assert.IsFalse(host.Availability.IsAvailable); Assert.IsEmpty(host.Logs);
    }
}
