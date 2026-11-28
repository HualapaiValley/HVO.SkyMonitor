using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
[TestProperty("Area", "Environmental")]
public sealed class EnvironmentalAdapterCompatibilityTests
{
    [TestMethod]
    [DataRow("LocalWeatherStation")]
    [DataRow("SkyQualityMeter")]
    public async Task LegacyParameterShapeSurvivesRestartAssociationAndDelivery(string type)
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
            long recordId;
            using (var original = new SqliteEnvironmentalObservationStore())
            {
                recordId = (await original.CommitLocalAsync(root, legacy, CancellationToken.None).ConfigureAwait(false)).Record.RecordId;
            }
            using var restarted = new SqliteEnvironmentalObservationStore();
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
            Assert.AreEqual("adapter-v1", current.Source.Provenance.Method.Version);
            Assert.AreNotEqual(legacy.Source.Version, current.Source.Version);

        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
