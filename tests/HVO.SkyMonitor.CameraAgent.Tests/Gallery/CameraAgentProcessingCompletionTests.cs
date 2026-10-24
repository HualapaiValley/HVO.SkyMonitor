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

            AssertCompletion(await ReadAsync(completion, second).ConfigureAwait(false), true);

            await SetStandardStateAsync(root, first, "completed").ConfigureAwait(false);
            await SetStandardStateAsync(root, second, "quarantined").ConfigureAwait(false);
            await SetStandardStateAsync(root, third, "leased").ConfigureAwait(false);
            AssertCompletion(await ReadAsync(completion, first).ConfigureAwait(false), false);
            AssertCompletion(await ReadAsync(completion, second).ConfigureAwait(false), false);
            AssertCompletion(await ReadAsync(completion, third).ConfigureAwait(false), true, second, first);
            AssertCompletion(await ReadAsync(completion, fourth).ConfigureAwait(false), true, second, first);

            // Work abandoned without overlays has ended too; retrying work has not. The candidates are bounded.
            await SetStandardStateAsync(root, third, "abandoned").ConfigureAwait(false);
            await SetStandardStateAsync(root, fourth, "retry_wait").ConfigureAwait(false);
            AssertCompletion(await ReadAsync(completion, third).ConfigureAwait(false), false);
            AssertCompletion(await ReadAsync(completion, fourth).ConfigureAwait(false), true, third, second, first);
            AssertCompletion(await completion.ReadAsync(fourth, 2, CancellationToken.None).ConfigureAwait(false), true, third, second);
            // The next page starts after the last candidate tried, whatever that candidate's own state.
            CollectionAssert.AreEqual(new[] { second, first },
                (await completion.ReadFinishedBeforeAsync(third, 2, CancellationToken.None).ConfigureAwait(false)).ToArray());
            CollectionAssert.AreEqual(new[] { first },
                (await completion.ReadFinishedBeforeAsync(second, 2, CancellationToken.None).ConfigureAwait(false)).ToArray());
            Assert.IsEmpty(await completion.ReadFinishedBeforeAsync(first, 2, CancellationToken.None).ConfigureAwait(false));

            // A capture without standard work has nothing to wait for.
            await EdgeUpgradeLaneTests.ExecuteAsync(root, """
                DELETE FROM capture_lane_work WHERE lane_name = 'standard'
                  AND raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture);
                """, ("$capture", fourth.ToString("N"))).ConfigureAwait(false);
            AssertCompletion(await ReadAsync(completion, fourth).ConfigureAwait(false), false);
        }
        finally
        {
            EdgeUpgradeLaneTests.Cleanup(root);
        }
    }

    /// <summary>
    /// Capture sequences are unique only per agent, so a history kept across an agent identity change has tied
    /// sequences. Each page continues after the last candidate tried in the whole order, sequence and then row, so a
    /// capture that ties it is neither skipped nor read again (#1409).
    /// </summary>
    [TestMethod]
    public async Task TiedSequencesOfTwoAgentIdentitiesAreNeitherSkippedNorRepeatedAcrossAPageBoundary()
    {
        var root = EdgeUpgradeLaneTests.CreateRoot("processing-completion-ties");
        try
        {
            using var provider = EdgeUpgradeLaneTests.CreateProvider(
                root, new EdgeUpgradeLaneTests.MutableTimeProvider(DateTimeOffset.UtcNow));
            var fixture = await EdgeUpgradeLaneTests.StartAsync(provider, root).ConfigureAwait(false);
            var former = fixture with { Configuration = fixture.Configuration with { AgentId = "former-agent" } };
            // The former identity's captures came first: sequences 1 and 2, then the current identity's 1 to 6.
            var formerCaptures = new[]
            {
                await former.AcceptAsync(0).ConfigureAwait(false),
                await former.AcceptAsync(1).ConfigureAwait(false)
            };
            var current = new List<Guid>();
            for (var index = 2; index < 8; index++)
            {
                current.Add(await fixture.AcceptAsync(index).ConfigureAwait(false));
            }
            Assert.AreEqual(
                "former-agent:1 former-agent:2 edge-upgrade-agent:1 edge-upgrade-agent:2 edge-upgrade-agent:3 "
                    + "edge-upgrade-agent:4 edge-upgrade-agent:5 edge-upgrade-agent:6",
                string.Join(' ', formerCaptures.Concat(current).Select(id => fixture.Receipts[id].Manifest.Descriptor.Capture)
                    .Select(static capture => $"{capture.AgentId}:{capture.CaptureSequence}")));
            foreach (var captureId in formerCaptures.Concat(current.Take(5)))
            {
                await SetStandardStateAsync(root, captureId, "completed").ConfigureAwait(false);
            }
            var completion = provider.GetRequiredService<ICameraAgentProcessingCompletion>();

            // Behind the pending 6: 5, 4, 3 and the current identity's 2, the later of the two 2s.
            AssertCompletion(await ReadAsync(completion, current[5]).ConfigureAwait(false), true,
                current[4], current[3], current[2], current[1]);
            // The next page starts with the former identity's 2, which ties the last candidate tried.
            CollectionAssert.AreEqual(new[] { formerCaptures[1], current[0], formerCaptures[0] },
                (await completion.ReadFinishedBeforeAsync(current[1], 4, CancellationToken.None).ConfigureAwait(false)).ToArray());
            // A boundary between the two 1s, one candidate a page.
            CollectionAssert.AreEqual(new[] { formerCaptures[0] },
                (await completion.ReadFinishedBeforeAsync(current[0], 1, CancellationToken.None).ConfigureAwait(false)).ToArray());
            Assert.IsEmpty(await completion.ReadFinishedBeforeAsync(formerCaptures[0], 1, CancellationToken.None).ConfigureAwait(false));

            // The first page behind a pending capture takes in an earlier capture that ties it too.
            await SetStandardStateAsync(root, current[1], "pending").ConfigureAwait(false);
            AssertCompletion(await ReadAsync(completion, current[1]).ConfigureAwait(false), true,
                formerCaptures[1], current[0], formerCaptures[0]);
        }
        finally
        {
            EdgeUpgradeLaneTests.Cleanup(root);
        }
    }

    private static async Task<CameraAgentProcessingCompletion> ReadAsync(ICameraAgentProcessingCompletion completion, Guid captureId)
        => await completion.ReadAsync(captureId, 4, CancellationToken.None).ConfigureAwait(false);

    private static void AssertCompletion(CameraAgentProcessingCompletion completion, bool pending, params Guid[] finished)
    {
        Assert.AreEqual(pending, completion.Pending);
        CollectionAssert.AreEqual(finished, completion.NewestFinishedCaptureIds.ToArray());
    }

    private static Task SetStandardStateAsync(string root, Guid captureId, string state)
        => EdgeUpgradeLaneTests.ExecuteAsync(root, """
            UPDATE capture_lane_work SET state = $state
            WHERE lane_name = 'standard'
              AND raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture);
            """, ("$state", state), ("$capture", captureId.ToString("N")));
}
