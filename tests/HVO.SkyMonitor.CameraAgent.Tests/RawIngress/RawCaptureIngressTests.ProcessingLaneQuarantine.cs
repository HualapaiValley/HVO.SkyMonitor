using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task ProcessingLaneQuarantine_AcknowledgementReleasesTheLaneAndLeavesCommittedRawIntact()
    {
        var root = CreateRoot();
        using var ingress = CreateIngress(root, new(TimeProvider.System));
        try
        {
            var first = (await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(1), [9, 8, 7, 6]), CancellationToken.None).ConfigureAwait(false))!;
            var second = (await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(2), [5, 4, 3, 2]), CancellationToken.None).ConfigureAwait(false))!;
            var firstBytes = await File.ReadAllBytesAsync(first.StoredFrame.AbsolutePath).ConfigureAwait(false);
            using (var setup = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                await ExecuteTestSqlAsync(setup, """
                    UPDATE capture_lane_work
                    SET state = 'quarantined', failure_reason = 'handler-failure'
                    WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 1);
                    UPDATE capture_lane_work
                    SET state = 'quarantined', failure_reason = 'evidence-invalid'
                    WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 2);
                    """).ConfigureAwait(false);
            }

            var rawPage = await ingress.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, rawPage.Items.Count);
            var page = await ingress.ReadPageAsync(10, null, CancellationToken.None).ConfigureAwait(false);
            var item = page.Items.Single();
            Assert.AreEqual(first.Manifest.Descriptor.Capture.CaptureId, item.Target.CaptureId);
            Assert.AreEqual("handler-failure", item.Target.FailureReason);
            Assert.AreEqual(first.CommittedManifestSha256, item.Target.ManifestSha256);

            var changed = item.Target with { FailureReason = "other-failure" };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ingress.AcknowledgeAsync(
                changed, "ack-1", "owner", IProcessingLaneQuarantineOperations.ReasonCode, CancellationToken.None).AsTask())
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => ingress.AcknowledgeAsync(
                item.Target, "ack-1", "owner", "operator-approved-loss", CancellationToken.None).AsTask())
                .ConfigureAwait(false);

            var receipt = await ingress.AcknowledgeAsync(
                item.Target, "ack-1", "owner", IProcessingLaneQuarantineOperations.ReasonCode, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(receipt.Duplicate);
            Assert.AreEqual("owner", receipt.Audit.Actor);
            Assert.AreEqual(IProcessingLaneQuarantineOperations.ReasonCode, receipt.Audit.Reason);
            var duplicate = await ingress.AcknowledgeAsync(
                item.Target, "ack-1", "owner", IProcessingLaneQuarantineOperations.ReasonCode, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsTrue(duplicate.Duplicate);

            Assert.AreEqual(0, (await ingress.ReadPageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Count);
            CollectionAssert.AreEqual(firstBytes, await File.ReadAllBytesAsync(first.StoredFrame.AbsolutePath).ConfigureAwait(false));
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual("abandoned", await ScalarStringAsync(verify, """
                SELECT state FROM capture_lane_work
                WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 1);
                """).ConfigureAwait(false));
            Assert.AreEqual("handler-failure", await ScalarStringAsync(verify, """
                SELECT failure_reason FROM capture_lane_work
                WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 1);
                """).ConfigureAwait(false));
            Assert.AreEqual("committed", await ScalarStringAsync(verify, """
                SELECT state FROM raw_captures WHERE capture_sequence = 1;
                """).ConfigureAwait(false));
            Assert.AreEqual("quarantined", await ScalarStringAsync(verify, """
                SELECT state FROM capture_lane_work
                WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 2);
                """).ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(verify, """
                SELECT COUNT(*) FROM raw_capture_stage_events event
                JOIN raw_captures raw ON raw.raw_capture_row_id = event.raw_capture_row_id
                WHERE raw.capture_sequence = 1 AND event.stage_key GLOB 'processing-lane-ack.*';
                """).ConfigureAwait(false));
            Assert.AreEqual(second.CommittedManifestSha256, await ScalarStringAsync(verify, """
                SELECT manifest_sha256 FROM raw_captures WHERE capture_sequence = 2;
                """).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(() =>
                ingress.EnsureCanAcceptAsync(4, CancellationToken.None).AsTask()).ConfigureAwait(false);
            await ExecuteTestSqlAsync(verify, """
                UPDATE capture_lane_work SET state = 'completed'
                WHERE failure_reason = 'evidence-invalid';
                """).ConfigureAwait(false);
            await ingress.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ingress.Dispose();
            DeleteRoot(root);
        }
    }
}
