using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualEnvironmentalSourceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AllSupportedKindsProduceDeterministicCanonicalFacts()
    {
        var values = new (EnvironmentalObservationKind Kind, double? Numeric, bool? Boolean)[]
        {
            (EnvironmentalObservationKind.AirTemperature, 12.5, null),
            (EnvironmentalObservationKind.CameraSensorTemperature, -5, null),
            (EnvironmentalObservationKind.RelativeHumidity, 42, null),
            (EnvironmentalObservationKind.AtmosphericPressure, 101_325, null),
            (EnvironmentalObservationKind.WindSpeed, 4, null),
            (EnvironmentalObservationKind.WindDirection, 225, null),
            (EnvironmentalObservationKind.WindGust, 7, null),
            (EnvironmentalObservationKind.PrecipitationRate, 0, null),
            (EnvironmentalObservationKind.RainState, null, false),
            (EnvironmentalObservationKind.CloudCover, 0.25, null),
            (EnvironmentalObservationKind.SkyBrightness, 20.7, null),
            (EnvironmentalObservationKind.SkyQuality, 21.1, null)
        };
        var context = Context(Location("location-a"));

        foreach (var value in values)
        {
            var source = Source(value.Kind, value.Numeric, value.Boolean);

            var first = await source.AcquireAsync(context, CancellationToken.None).ConfigureAwait(false);
            var replay = await source.AcquireAsync(context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Produced, first.Outcome, value.Kind.ToString());
            Assert.IsNotNull(first.Fact);
            Assert.AreEqual(value.Kind, first.Fact.Value.Kind);
            Assert.AreEqual(
                value.Kind == EnvironmentalObservationKind.CameraSensorTemperature
                    ? EnvironmentalObservationSchemaVersions.V2
                    : EnvironmentalObservationSchemaVersions.V1,
                first.Fact.SchemaVersion);
            Assert.AreEqual(
                EnvironmentalObservationFactJson.ComputeContentSha256(first.Fact),
                EnvironmentalObservationFactJson.ComputeContentSha256(replay.Fact!));
            CollectionAssert.AreEqual(
                EnvironmentalObservationFactJson.Serialize(first.Fact),
                EnvironmentalObservationFactJson.Serialize(replay.Fact!));
            Assert.AreEqual(Epoch.AddSeconds(120), first.Fact.ValidThroughUtc);
            Assert.AreEqual(Epoch.AddSeconds(45), first.Fact.StaleAfterUtc);
            Assert.AreEqual(EnvironmentalObservationSourceKind.Simulated, first.Fact.Source.Kind);
        }
    }

    [TestMethod]
    public async Task SeedTimeAndLocationIdentityControlNoiseAndObservationIdentity()
    {
        var source = Source(EnvironmentalObservationKind.AirTemperature, 12.5, null, noise: 0.5);
        var first = (await source.AcquireAsync(Context(Location("location-a")), CancellationToken.None)
            .ConfigureAwait(false)).Fact!;
        var changedLocation = (await source.AcquireAsync(Context(Location("location-b")), CancellationToken.None)
            .ConfigureAwait(false)).Fact!;
        var changedTime = (await source.AcquireAsync(
            Context(Location("location-a")) with { ObservedAtUtc = Epoch.AddSeconds(30) },
            CancellationToken.None).ConfigureAwait(false)).Fact!;

        Assert.AreNotEqual(first.ObservationId, changedLocation.ObservationId);
        Assert.AreNotEqual(first.ObservationId, changedTime.ObservationId);
        Assert.AreNotEqual(first.Value.NumericValue, changedLocation.Value.NumericValue);
        Assert.IsTrue(first.Value.NumericValue is >= 12 and <= 13);
    }

    [TestMethod]
    public async Task MissingFailureAndCancellationNeverManufactureFacts()
    {
        var missing = Source(
            EnvironmentalObservationKind.RainState,
            null,
            false,
            mode: VirtualEnvironmentalSourceMode.Missing);
        var failed = Source(
            EnvironmentalObservationKind.CloudCover,
            0.5,
            null,
            mode: VirtualEnvironmentalSourceMode.Failed);
        var delayed = Source(
            EnvironmentalObservationKind.RelativeHumidity,
            42,
            null,
            delayMilliseconds: 10_000);

        var missingResult = await missing.AcquireAsync(Context(Location("location-a")), CancellationToken.None)
            .ConfigureAwait(false);
        var failedResult = await failed.AcquireAsync(Context(Location("location-a")), CancellationToken.None)
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Missing, missingResult.Outcome);
        Assert.IsNull(missingResult.Fact);
        Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Failed, failedResult.Outcome);
        Assert.IsNull(failedResult.Fact);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await delayed.AcquireAsync(Context(Location("location-a")), cancellation.Token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static VirtualEnvironmentalSource Source(
        EnvironmentalObservationKind kind,
        double? numeric,
        bool? boolean,
        double noise = 0,
        VirtualEnvironmentalSourceMode mode = VirtualEnvironmentalSourceMode.Normal,
        int delayMilliseconds = 0)
        => new(
            Descriptor(kind),
            new VirtualEnvironmentalSourceOptions(
                209,
                Epoch,
                numeric,
                boolean,
                noise,
                kind == EnvironmentalObservationKind.RainState ? null : 0.1,
                EnvironmentalObservationQuality.Good,
                mode,
                delayMilliseconds));

    private static EnvironmentalSourceDescriptor Descriptor(EnvironmentalObservationKind kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new EnvironmentalSourceDescriptor(
            $"virtual-{kind}",
            "VirtualEnvironment",
            kind,
            true,
            [EnvironmentalAcquisitionTrigger.Periodic],
            Epoch,
            30,
            3,
            120,
            45,
            kind == EnvironmentalObservationKind.CameraSensorTemperature ? "rig-1" : null,
            document.RootElement.Clone());
    }

    private static EnvironmentalSourceAcquisitionContext Context(DeploymentLocationSnapshot location)
        => new(EnvironmentalAcquisitionTrigger.Periodic, Epoch, location);

    private static DeploymentLocationSnapshot Location(string id)
        => DeploymentLocationSnapshot.Create(
            id,
            1,
            "test",
            null,
            Epoch.AddDays(-1),
            null,
            35.5599378,
            -113.9119818,
            520,
            "America/Phoenix");
}
