using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Extensions.DependencyInjection;

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
