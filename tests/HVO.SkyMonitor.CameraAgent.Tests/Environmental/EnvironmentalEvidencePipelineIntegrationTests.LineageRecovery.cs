using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalEvidencePipelineIntegrationTests
{
    [TestMethod]
    public async Task ExpiredDerivedHistoryReleasesItsLocalLineageAfterDeletion()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationStore(clock);
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            foreach (var input in inputs) await store.CommitLocalAsync(root, input, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived, CancellationToken.None).ConfigureAwait(false);
            clock.Now = Epoch.AddDays(40);
            var first = await store.RetainLocalAsync(root, Epoch.AddDays(1), 10, CancellationToken.None).ConfigureAwait(false);
            var second = await store.RetainLocalAsync(root, Epoch.AddDays(1), 10, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(3, first.RemovedCount + second.RemovedCount);
            Assert.IsEmpty((await store.ReadLocalPageAsync(root, null, 10, null, CancellationToken.None).ConfigureAwait(false)).Items);
        }
        finally { Delete(root); }
    }
}
