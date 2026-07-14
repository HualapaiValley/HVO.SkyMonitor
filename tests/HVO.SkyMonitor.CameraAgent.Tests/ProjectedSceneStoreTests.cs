using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProjectedSceneStoreTests
{
    [TestMethod]
    public async Task Put_WhenCapacityIsExceeded_EvictsOldestScene()
    {
        var scene = await CreateSceneAsync().ConfigureAwait(false);
        var store = new ProjectedSceneStore();

        for (var index = 0; index < 33; index++)
        {
            store.Put($"scene-{index}", scene);
        }

        Assert.AreEqual(32, store.Count);
        Assert.IsFalse(store.TryGet("scene-0", out _));
        Assert.IsTrue(store.TryGet("scene-1", out _));
        Assert.IsTrue(store.TryGet("scene-32", out var newest));
        Assert.AreSame(scene, newest);
    }

    [TestMethod]
    public async Task Put_WhenExistingSceneIsRefreshed_PreservesItDuringNextEviction()
    {
        var scene = await CreateSceneAsync().ConfigureAwait(false);
        var store = new ProjectedSceneStore();
        for (var index = 0; index < 32; index++)
        {
            store.Put($"scene-{index}", scene);
        }

        store.Put("scene-0", scene);
        store.Put("scene-32", scene);

        Assert.AreEqual(32, store.Count);
        Assert.IsTrue(store.TryGet("scene-0", out _));
        Assert.IsFalse(store.TryGet("scene-1", out _));
    }

    private static ValueTask<VisibleScene> CreateSceneAsync()
        => new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(
            new VisibleSceneRequest(
                DateTimeOffset.UnixEpoch,
                new ObserverLocation(0, 0, 0),
                new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
                new CatalogQuery(6.5, 10),
                new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('0', 64), "test", "1")));
}
