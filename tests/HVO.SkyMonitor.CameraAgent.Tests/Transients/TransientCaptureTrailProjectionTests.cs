using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Extensions.DependencyInjection;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

public sealed partial class TransientWorkerRuntimeTests
{
    [TestMethod]
    public async Task CaptureTrailProjection_BindsActualRetainedCaptureAndRejectsAlteredChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-capture-trails", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var configuration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
            await StageVirtualFramesAsync(provider, configuration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var iteration = 0; iteration < 24; iteration++)
            {
                if (!await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false) &&
                    !await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false)) break;
            }

            var projection = provider.GetRequiredService<ICameraAgentTransientOperatorProjection>();
            var page = await projection.GetPageAsync(new(), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, page.Items.Count);
            var detail = await projection.GetCandidateAsync(page.Items[0].CandidateId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var source = detail.Sources!.Single(value => value.EvidenceId == detail.CandidateEvidence.CenterEvidenceId);

            var result = await projection.GetCaptureTrailsAsync(source.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("MeasuredEvidence", result.State);
            Assert.IsFalse(result.Incomplete, result.Reason);
            var trail = result.Trails.Single();
            Assert.AreEqual(source.ArtifactId, trail.SourceManifest.Descriptor.Artifact.ArtifactId);
            Assert.AreEqual(source.EvidenceId, trail.Geometry.SourceEvidenceId);
            Assert.AreEqual(source.ObservationStartedUtc, trail.DetectorInput.Source.ObservationStartedUtc);
            Assert.AreEqual(source.ObservationEndedUtc, trail.DetectorInput.Source.ObservationEndedUtc);
            Assert.AreEqual(trail.Geometry.CoordinateWidth, trail.DetectorInput.Layout.Width);
            Assert.IsTrue(trail.Geometry.Polyline.Count >= 2);
            Assert.IsTrue(source.CaptureSequence < 7, "The projection must also work for a historical capture, not only the latest frame.");
            if (detail.FinalEvidence.EventState == "Validated" && detail.AssessmentEvidence.Authority == "Authoritative" &&
                detail.AssessmentEvidence.Classification == "Meteor")
            {
                Assert.AreEqual("ConfirmedMeteor", trail.State);
            }
            else
            {
                Assert.AreNotEqual("ConfirmedMeteor", trail.State);
            }
            await AssertRetainedTrailAuthorityAsync(root, projection, source.CaptureId, trail.CandidateId)
                .ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);
            using var recovered = CreateProvider(root);
            // A recovered owner acquires the journal only after the original owner has released its process lock.
            // Retained facts remain sufficient even with no current camera configuration loaded.
            projection = recovered.GetRequiredService<ICameraAgentTransientOperatorProjection>();
            var historical = await projection.GetCaptureTrailsAsync(source.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(trail.CandidateIdentitySha256, historical.Trails.Single().CandidateIdentitySha256);
            Assert.AreEqual(trail.DetectorInput.InputIdentitySha256, historical.Trails.Single().DetectorInput.InputIdentitySha256);
            CollectionAssert.AreEqual(trail.Geometry.Polyline.ToArray(), historical.Trails.Single().Geometry.Polyline.ToArray());

            var background = detail.Sources!.First(value => value.EvidenceId != source.EvidenceId);
            var backgroundResult = await projection.GetCaptureTrailsAsync(background.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("NoRecordedCandidate", backgroundResult.State);
            Assert.AreEqual(0, backgroundResult.Trails.Count);

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            using var alter = connection.CreateCommand();
            alter.CommandText = "UPDATE raw_captures SET payload_sha256 = $checksum WHERE capture_id = $capture;";
            alter.Parameters.AddWithValue("$checksum", new string('F', 64));
            alter.Parameters.AddWithValue("$capture", source.CaptureId.ToString("N"));
            await alter.ExecuteNonQueryAsync().ConfigureAwait(false);

            var altered = await projection.GetCaptureTrailsAsync(source.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("Unavailable", altered.State);
            Assert.IsTrue(altered.Incomplete);
            Assert.AreEqual(0, altered.Trails.Count);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task AssertRetainedTrailAuthorityAsync(string root,
        ICameraAgentTransientOperatorProjection projection, Guid captureId, Guid candidateId)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT finalization_payload, state FROM transient_candidates WHERE candidate_id = $candidate;";
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        byte[] originalBytes;
        string originalState;
        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
        {
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            originalBytes = await reader.GetFieldValueAsync<byte[]>(0).ConfigureAwait(false);
            originalState = reader.GetString(1);
        }
        var receipt = TransientCandidateDeliveryJson.ParseFinalization(originalBytes).Value;
        Assert.IsNotNull(receipt);
        var originalEvent = receipt.Event;
        var observation = originalEvent.Observations.Single();
        var meteor = originalEvent.Assessments.Single() with
        {
            Authority = TransientAssessmentAuthority.Authoritative,
            Classification = TransientClassification.Meteor,
            MeteorSeverity = TransientMeteorSeverity.Meteor,
            CreatedUtc = originalEvent.EventCreatedUtc,
            EvidenceObservationIds = [observation.ObservationId],
            SupersedesAssessmentId = null
        };
        var validated = originalEvent with
        {
            State = TransientEventState.Validated,
            VersionCreatedUtc = originalEvent.VersionCreatedUtc.AddSeconds(1),
            Assessments = [meteor],
            Reviews = [],
            Notifications = []
        };
        var conflicting = meteor with
        {
            AssessmentId = Guid.NewGuid(),
            CreatedUtc = validated.VersionCreatedUtc,
            Classification = TransientClassification.Satellite,
            MeteorSeverity = null
        };
        var variants = new (TransientEventV1 Event, bool Confirmed, string Description)[]
        {
            (validated, true, "exact authoritative meteor"),
            (validated with { Assessments = [meteor with { Authority = TransientAssessmentAuthority.Provisional }] }, false, "provisional classification"),
            (validated with { Assessments = [meteor, conflicting] }, false, "conflicting active authority"),
            (validated with { Assessments = [meteor, conflicting with { SupersedesAssessmentId = meteor.AssessmentId }] }, false, "superseded meteor"),
            (validated with { Observations = [observation with { Geometry = observation.Geometry with
            {
                Polyline = observation.Geometry.Polyline.Select((point, index) => index == 0 ? point with { X = point.X + 0.01 } : point).ToArray()
            } }] }, false, "different measured geometry")
        };
        try
        {
            foreach (var variant in variants)
            {
                var changed = receipt with { Event = variant.Event };
                changed = changed with { ReceiptIdentitySha256 = TransientCandidateDeliveryJson.ComputeFinalizationIdentitySha256(changed) };
                // Persist complete, canonically valid immutable receipt evidence; changing only the UI summary cannot confirm a trail.
                var bytes = TransientCandidateDeliveryJson.Serialize(changed);
                await WriteReceiptAsync(bytes, changed.ReceiptIdentitySha256, "validated").ConfigureAwait(false);
                var projected = await projection.GetCaptureTrailsAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(projected.Incomplete, projected.Reason);
                Assert.AreEqual(variant.Confirmed, projected.Trails.Single().State == "ConfirmedMeteor", variant.Description);
            }
        }
        finally
        {
            await WriteReceiptAsync(originalBytes, receipt.ReceiptIdentitySha256, originalState).ConfigureAwait(false);
        }

        async Task WriteReceiptAsync(byte[] bytes, string identity, string state)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE transient_candidates SET finalization_payload = $payload, finalization_receipt_identity_sha256 = $identity, state = $state WHERE candidate_id = $candidate;";
            update.Parameters.AddWithValue("$payload", bytes);
            update.Parameters.AddWithValue("$identity", identity);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task CaptureTrailProjection_UnknownCaptureIsUnavailableRatherThanNoCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-capture-trails-missing", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var result = await provider.GetRequiredService<ICameraAgentTransientOperatorProjection>()
                .GetCaptureTrailsAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("Unavailable", result.State);
            Assert.AreEqual(0, result.Trails.Count);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
