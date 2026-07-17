using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class EnvironmentalObservationDeliveryHostTests
{
    [TestMethod]
    public async Task ProvisionedPublisherDeliversThroughHostedOutboxToLogicHost()
    {
        var observationId = Guid.NewGuid();
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IEnvironmentalObservationPublisher>();

        var published = await publisher.PublishAsync(CreateFact(observationId)).ConfigureAwait(false);

        Assert.AreEqual(AssemblyHooks.Fixture.ObservatoryId, published.Observation.Target.SiteId);
        Assert.AreEqual(AssemblyHooks.Fixture.DevicePublicId, published.Observation.Target.AgentId);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline &&
            await AssemblyHooks.Fixture.CountEnvironmentalObservationsAsync(observationId).ConfigureAwait(false) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        Assert.AreEqual(
            1,
            await AssemblyHooks.Fixture.CountEnvironmentalObservationsAsync(observationId).ConfigureAwait(false));
    }

    private static EnvironmentalObservationFactV1 CreateFact(Guid observationId)
    {
        using var document = JsonDocument.Parse("{}");
        var parameters = document.RootElement.Clone();
        var now = DateTimeOffset.UtcNow;
        return new EnvironmentalObservationFactV1(
            EnvironmentalObservationFactV1.CurrentSchemaVersion,
            observationId,
            new EnvironmentalObservationSource(
                "host-integration-provider",
                "weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            now,
            null,
            null,
            now.AddMinutes(-1),
            now.AddMinutes(5),
            now.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good),
            []);
    }
}
