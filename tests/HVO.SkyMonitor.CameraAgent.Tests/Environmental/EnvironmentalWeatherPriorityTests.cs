using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class EnvironmentalWeatherPriorityTests
{
    [TestMethod]
    [DataRow("local-first", "local")]
    [DataRow("provider-first", "provider")]
    [DataRow("local-failed", "provider")]
    [DataRow("local-suspect", "provider")]
    [DataRow("simulated", "provider")]
    [DataRow("no-fallback", "missing")]
    public async Task ExplicitPrioritySelectsOnlyEligibleFreshSources(string scenario, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-priority-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            // Each fixture source owns its own alias; cloning an adapter fact while retaining its acquisition owner
            // would describe a different configuration than the local/provider sources under test.
            var fact = EnvironmentalAdapterReviewUnitTests.Fact("local", EnvironmentalObservationKind.AirTemperature, 20);
            var measured = fact with
            {
                Source = fact.Source with { SourceId = "local", Kind = scenario == "simulated" ? EnvironmentalObservationSourceKind.Simulated : EnvironmentalObservationSourceKind.Measured },
                Value = fact.Value with { Quality = scenario == "local-suspect" ? EnvironmentalObservationQuality.Suspect : EnvironmentalObservationQuality.Good }
            };
            var imported = fact with { ObservationId = Guid.NewGuid(), Source = fact.Source with { SourceId = "provider", Kind = EnvironmentalObservationSourceKind.Imported } };
            await store.CommitLocalAsync(root, measured, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, imported, CancellationToken.None).ConfigureAwait(false);
            var sources = new[]
            {
                new EnvironmentalSourceConfiguration { Id = "local", Type = "LocalWeatherStation", Kind = fact.Value.Kind },
                new EnvironmentalSourceConfiguration { Id = "provider", Type = "OnlineWeather", Kind = fact.Value.Kind }
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    Sources = sources,
                    WeatherSourcePriority = scenario == "no-fallback" ? ["local"] : scenario == "provider-first" ? ["provider", "local"] : ["local", "provider"]
                }
            });
            if (scenario is "local-failed" or "no-fallback")
                await store.RecordAttemptAsync(root, sources[0].ToDescriptor(), new("local", EnvironmentalAcquisitionTrigger.Periodic,
                    EnvironmentalAcquisitionDisposition.Failed, "device-disconnected", null,
                    EnvironmentalHardwareAdapterTests.Epoch, EnvironmentalHardwareAdapterTests.Epoch), null, null, CancellationToken.None).ConfigureAwait(false);
            var association = new EnvironmentalAssociationService(store, store, options, TimeProvider.System, acquisitionStates: store);
            var captureId = Guid.NewGuid();
            var selected = (await association.AssociateAsync(captureId, 1, EnvironmentalHardwareAdapterTests.Epoch,
                EnvironmentalHardwareAdapterTests.Epoch.AddSeconds(1), null, [fact.Value.Kind], CancellationToken.None).ConfigureAwait(false))[0];
            if (expected == "missing") Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, selected.Status);
            else
            {
                Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, selected.Status);
                var detail = await store.ReadLocalDetailAsync(root, selected.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(expected, detail!.Fact.Source.SourceId);
            }
            var history = await store.ReadLocalPageAsync(root, null, 100, null, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, history.Items);
            var recovered = await association.ReadCompletedAsync(captureId, 1, EnvironmentalHardwareAdapterTests.Epoch,
                EnvironmentalHardwareAdapterTests.Epoch.AddSeconds(1), null, [fact.Value.Kind], CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(selected.AssociationIdentitySha256, recovered![0].AssociationIdentitySha256);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
