using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

/// <summary>
/// Current Sky waits only for standard work that can still produce a capture's overlays, and the newest capture it can
/// show meanwhile is the newest earlier one whose standard work has ended, with or without its overlays (#1382).
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CameraAgentProcessingCompletionTests
{
    [TestMethod]
    public async Task OnlyUnfinishedStandardWorkIsPendingAndTheNewestFinishedEarlierCaptureIsNamed()
    {
        var root = EdgeUpgradeLaneTests.CreateRoot("processing-completion");
        try
        {
            using var provider = EdgeUpgradeLaneTests.CreateProvider(
                root, new EdgeUpgradeLaneTests.MutableTimeProvider(DateTimeOffset.UtcNow));
            var fixture = await EdgeUpgradeLaneTests.StartAsync(provider, root).ConfigureAwait(false);
            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            var second = await fixture.AcceptAsync(1).ConfigureAwait(false);
            var third = await fixture.AcceptAsync(2).ConfigureAwait(false);
            var fourth = await fixture.AcceptAsync(3).ConfigureAwait(false);
            var completion = provider.GetRequiredService<ICameraAgentProcessingCompletion>();

            Assert.AreEqual(new CameraAgentProcessingCompletion(true, null), await ReadAsync(completion, second).ConfigureAwait(false));

            await SetStandardStateAsync(root, first, "completed").ConfigureAwait(false);
            await SetStandardStateAsync(root, second, "quarantined").ConfigureAwait(false);
            await SetStandardStateAsync(root, third, "leased").ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentProcessingCompletion(false, null), await ReadAsync(completion, first).ConfigureAwait(false));
            Assert.AreEqual(new CameraAgentProcessingCompletion(false, null), await ReadAsync(completion, second).ConfigureAwait(false));
            Assert.AreEqual(new CameraAgentProcessingCompletion(true, second), await ReadAsync(completion, third).ConfigureAwait(false));
            Assert.AreEqual(new CameraAgentProcessingCompletion(true, second), await ReadAsync(completion, fourth).ConfigureAwait(false));

            // Work abandoned without overlays has ended too; retrying work has not.
            await SetStandardStateAsync(root, third, "abandoned").ConfigureAwait(false);
            await SetStandardStateAsync(root, fourth, "retry_wait").ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentProcessingCompletion(false, null), await ReadAsync(completion, third).ConfigureAwait(false));
            Assert.AreEqual(new CameraAgentProcessingCompletion(true, third), await ReadAsync(completion, fourth).ConfigureAwait(false));

            // A capture without standard work has nothing to wait for.
            await EdgeUpgradeLaneTests.ExecuteAsync(root, """
                DELETE FROM capture_lane_work WHERE lane_name = 'standard'
                  AND raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture);
                """, ("$capture", fourth.ToString("N"))).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentProcessingCompletion(false, null), await ReadAsync(completion, fourth).ConfigureAwait(false));
        }
        finally
        {
            EdgeUpgradeLaneTests.Cleanup(root);
        }
    }

    private static async Task<CameraAgentProcessingCompletion> ReadAsync(ICameraAgentProcessingCompletion completion, Guid captureId)
        => await completion.ReadAsync(captureId, CancellationToken.None).ConfigureAwait(false);

    private static Task SetStandardStateAsync(string root, Guid captureId, string state)
        => EdgeUpgradeLaneTests.ExecuteAsync(root, """
            UPDATE capture_lane_work SET state = $state
            WHERE lane_name = 'standard'
              AND raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture);
            """, ("$state", state), ("$capture", captureId.ToString("N")));
}
