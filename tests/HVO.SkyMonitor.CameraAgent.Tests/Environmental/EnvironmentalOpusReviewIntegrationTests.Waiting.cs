using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalOpusReviewIntegrationTests
{
    [TestMethod]
    public async Task WaitingProjectionStopsAfterOneCapacityRefusalPerPass()
    {
        var root = Root();
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(new Clock(), maximumRecords: 1);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            var fact = EnvironmentalAdapterReviewUnitTests.Fact("station", EnvironmentalObservationKind.AirTemperature, 20);
            await store.CommitLocalAsync(root, fact, target, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, fact with { ObservationId = Guid.NewGuid() }, target, CancellationToken.None).ConfigureAwait(false);
            var before = await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, await store.ProjectWaitingAsync(root, 100, CancellationToken.None).ConfigureAwait(false));
            var after = await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(before.OverflowCount + 1, after.OverflowCount);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task WaitingProjectionRotatesEachUnresolvedRecordOnlyOncePerPass()
    {
        var root = Root();
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(new Clock());
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            var (_, derived) = await InfraredAsync().ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived with { ObservationId = Guid.NewGuid() }, target, CancellationToken.None).ConfigureAwait(false);
            var before = await ProjectionUpdatesAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0, await store.ProjectWaitingAsync(root, 100, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(2L, await ProjectionUpdatesAsync(root).ConfigureAwait(false) - before);
        }
        finally { Delete(root); }
    }

    private static async Task<long> ProjectionUpdatesAsync(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(updated_unix_ms) FROM environmental_observation_central_projection WHERE status = 'waiting';";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
