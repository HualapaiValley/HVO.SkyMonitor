using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class EnvironmentalAdapterCompatibilityTests
{
    [TestMethod]
    [DataRow("LocalWeatherStation", false)]
    [DataRow("LocalWeatherStation", true)]
    [DataRow("SkyQualityMeter", false)]
    [DataRow("SkyQualityMeter", true)]
    public async Task LegacyParameterShapeSurvivesRestartAssociationAndDelivery(string type, bool alreadyQueued)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapter-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = EnvironmentalHardwareAdapterTests.Source(type);
            var context = EnvironmentalHardwareAdapterTests.Context();
            var current = (await source.AcquireAsync(context, CancellationToken.None).ConfigureAwait(false)).Fact!;
            var parameters = EnvironmentalAdapterProvenanceTests.LegacyParameters(source.Descriptor, new("virtual-device"));
            var oldSource = current.Source with
            {
                Version = EnvironmentalAdapterProvenanceTests.LegacySourceVersion(parameters, true),
                Provenance = new(new("environmental-adapter", "adapter-v1"), parameters, CaptureContractJson.ComputeCanonicalJsonSha256(parameters))
            };
            var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                source = oldSource,
                value = current.Value,
                SampledAtUtc = context.ObservedAtUtc,
                context.DeploymentLocation.CanonicalSha256,
                from = current.ObservedFromUtc,
                lineage = (IReadOnlyList<EnvironmentalObservationReference>?)null
            });
            var legacy = current with { Source = oldSource, ObservationId = new Guid(Convert.FromHexString(identity).AsSpan(0, 16)) };
            var originalBytes = EnvironmentalObservationFactJson.Serialize(legacy);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            long recordId;
            using (var original = new SqliteEnvironmentalObservationOutbox())
            {
                recordId = (await original.CommitLocalAsync(root, legacy, alreadyQueued ? target : null, CancellationToken.None).ConfigureAwait(false)).Record.RecordId;
            }
            using var restarted = new SqliteEnvironmentalObservationOutbox();
            var retained = (await restarted.ReadLocalDetailAsync(root, recordId, CancellationToken.None).ConfigureAwait(false))!;
            CollectionAssert.AreEqual(originalBytes, EnvironmentalObservationFactJson.Serialize(retained.Fact));
            Assert.AreEqual(EnvironmentalObservationFactJson.ComputeContentSha256(legacy), retained.ContentSha256);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new()
                {
                    Enabled = true,
                    Sources = [new EnvironmentalSourceConfiguration
                { Id = source.Descriptor.Id, Type = type, Kind = source.Descriptor.Kind, Required = false }]
                }
            });
            var association = (await new EnvironmentalAssociationService(restarted, restarted, options, TimeProvider.System)
                .AssociateAsync(Guid.NewGuid(), 1, context.ObservedAtUtc, context.ObservedAtUtc.AddSeconds(20), null,
                    [source.Descriptor.Kind], CancellationToken.None).ConfigureAwait(false)).Single();
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, association.Status);
            Assert.AreEqual(recordId, association.SelectedRecordId);
            if (!alreadyQueued) Assert.AreEqual(1, await restarted.AssignUnprojectedAsync(root, target, 10, CancellationToken.None).ConfigureAwait(false));
            var lease = await restarted.ClaimAsync(root, "compatibility", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var expected = legacy.Enrich(new(target.ObservatoryId, target.DevicePublicId, target.RigId));
            CollectionAssert.AreEqual(EnvironmentalObservationJson.Serialize(expected), EnvironmentalObservationJson.Serialize(lease.Record.Observation));
            Assert.AreEqual(EnvironmentalObservationJson.ComputeContentSha256(expected), lease.Record.ContentSha256);
            // The receiver validates bounded method/version strings and the submitted parameter hash, not a version allowlist.
            Assert.IsTrue(EnvironmentalObservationJson.Validate(lease.Record.Observation).IsValid);
            Assert.IsTrue(EnvironmentalObservationJson.Validate(current.Enrich(new(target.ObservatoryId, target.DevicePublicId))).IsValid);
            Assert.AreEqual("adapter-v1", current.Source.Provenance.Method.Version);
            Assert.AreNotEqual(legacy.Source.Version, current.Source.Version);
            await restarted.AcknowledgeAsync(root, lease, new(EnvironmentalObservationAcknowledgement.CurrentSchemaVersion, expected.ObservationId,
                lease.Record.SourceIdentitySha256, lease.Record.ContentSha256, DateTimeOffset.UtcNow,
                EnvironmentalObservationDeliveryDisposition.Accepted), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, (await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
