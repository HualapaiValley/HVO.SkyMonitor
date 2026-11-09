using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalEvidencePipelineIntegrationTests
{
    [TestMethod]
    [DataRow("capacity")]
    [DataRow("identity")]
    [DataRow("io")]
    [DataRow("sqlite")]
    public async Task InfraredInputPublicationFailuresIdentifyTheJournalRatherThanTheDevice(string failure)
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var source = new EnvironmentalSourceConfiguration
            {
                Id = "cloud",
                Type = "InfraredSky",
                Kind = EnvironmentalObservationKind.CloudCover,
                Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
                StaleAfterSeconds = 300,
                ValidForSeconds = 900,
                Options = JsonSerializer.SerializeToElement(EnvironmentalHardwareAdapterTests.Options(EnvironmentalHardwareAdapterTests.Infrared), JsonOptions)
            };
            var options = Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = root, EnvironmentalAcquisition = new() { Enabled = true, Sources = [source] } });
            Exception fault = failure switch
            {
                "capacity" => new LocalEnvironmentalObservationCapacityException("full"),
                "identity" => new EnvironmentalObservationIdentityConflictException("conflict"),
                "io" => new IOException("disk"),
                _ => new SqliteException("disk", 10)
            };
            var publisher = new FaultingPublisher(fault);
            using var provider = new ServiceCollection().AddSingleton<IEnvironmentalObservationPublisher>(publisher).BuildServiceProvider();
            using var coordinator = new EnvironmentalAcquisitionCoordinator(new(provider, EnvironmentalAdapterPipelineTests.Registrations()),
                publisher, new Locations(), options, clock, store);
            var receipt = await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, Epoch).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Failed, receipt.Disposition);
            Assert.AreEqual("journal-unavailable", receipt.Reason);
            Assert.AreEqual(receipt.Reason, (await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false)).Single().LastReason);
        }
        finally { Delete(root); }
    }

    private sealed class FaultingPublisher(Exception fault) : IEnvironmentalObservationPublisher
    {
        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
            => throw fault;
    }
}
