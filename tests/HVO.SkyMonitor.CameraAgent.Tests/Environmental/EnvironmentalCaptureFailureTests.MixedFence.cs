using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using Moq;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnclaimedPreparationDefersThenFencesOnlyItsSnapshotAcrossRestart(bool restart)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var acquisitionId = Guid.NewGuid();
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisitionId, Epoch).ConfigureAwait(false);
        var capture = raw.Manifest.Descriptor.Capture;
        await Assert.ThrowsExactlyAsync<EnvironmentalCaptureAcquisitionPendingException>(async () =>
            await fixture.AssociateAsync(capture.CaptureId, capture.CaptureSequence, Epoch, acquisitionId).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.HasCount(0, await fixture.Store.ReadAssociationsAsync(fixture.Root, capture.CaptureId, CancellationToken.None).ConfigureAwait(false));
        await fixture.StopAsync().ConfigureAwait(false);
        if (restart) fixture.Reopen();
        fixture.Clock.Now = Epoch.AddSeconds(3);
        await fixture.Store.RecoverCaptureAcquisitionsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        var unavailable = await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root, capture.CaptureId, acquisitionId,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(unavailable.Pending);
        Assert.AreEqual("sqm", unavailable.UnavailableSources.Single());
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing,
            (await fixture.AssociateAsync(capture.CaptureId, capture.CaptureSequence, Epoch, acquisitionId).ConfigureAwait(false)).Status);
        var independent = new EnvironmentalAssociationService(fixture.Store, fixture.Store, fixture.HostOptions, fixture.Clock,
            acquisitionStates: fixture.Store);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, (await independent.AssociateAsync(Guid.NewGuid(), 2,
            Epoch, Epoch.AddSeconds(20), null, [EnvironmentalObservationKind.SkyQuality], CancellationToken.None).ConfigureAwait(false)).Single().Status);
    }

    [TestMethod]
    public async Task UnavailableCaptureStillRecordsItsRegimeWithoutBindingMissingClaims()
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, Guid.NewGuid(), Epoch,
            unavailable: true).ConfigureAwait(false);
        var state = new Mock<IEnvironmentalAcquisitionStateStore>();
        state.Setup(store => store.RecordCaptureRegimeAsync(fixture.Root, raw.Manifest.Descriptor.Capture.CaptureSequence,
            raw.Manifest.Descriptor.Capture.CaptureId, CaptureSolarRegime.Night, Epoch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var submission = new CaptureLoopSubmission(new(Epoch, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint),
            new(null, setpoint, TimeSpan.Zero, CaptureMode.Still, false), Epoch, TimeSpan.FromSeconds(1), TimeSpan.Zero)
        { CycleEvidence = raw.Manifest.Descriptor.CycleEvidence! with { SolarRegime = CaptureSolarRegime.Night } };
        await fixture.CreateBridge(state.Object).AfterCaptureAsync(raw, submission, CancellationToken.None).ConfigureAwait(false);
        state.VerifyAll();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BeforeCaptureFencePreservesFreshPeriodicReadingsOfAnotherKind(bool changedTriggers)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var temperature = EnvironmentalAdapterContractsTests.Fact("periodic-temperature", EnvironmentalObservationKind.AirTemperature, 18)
            with
        { ObservedAtUtc = Epoch, ValidFromUtc = Epoch, ValidThroughUtc = Epoch.AddMinutes(5), StaleAfterUtc = Epoch.AddMinutes(5) };
        await fixture.Store.CommitLocalAsync(fixture.Root, temperature, CancellationToken.None).ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, Guid.NewGuid(), Epoch,
            unavailable: true).ConfigureAwait(false);
        var capture = raw.Manifest.Descriptor.Capture;
        var mixedOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            EnvironmentalAcquisition = new()
            {
                Enabled = true,
                Sources = [changedTriggers ? new()
                {
                    Id = "sqm", Type = "SkyQualityMeter", Kind = EnvironmentalObservationKind.SkyQuality,
                    Triggers = [EnvironmentalAcquisitionTrigger.Periodic]
                } : fixture.HostOptions.Value.EnvironmentalAcquisition.Sources.Single(), new()
                {
                    Id = "periodic-temperature", Type = "VirtualEnvironment", Kind = EnvironmentalObservationKind.AirTemperature,
                    Triggers = [EnvironmentalAcquisitionTrigger.Periodic]
                }]
            }
        });
        var service = new EnvironmentalAssociationService(fixture.Store, fixture.Store, mixedOptions, fixture.Clock,
            acquisitionStates: fixture.Store);
        var result = await service.AssociateAsync(capture.CaptureId, capture.CaptureSequence, Epoch, Epoch.AddSeconds(20), null,
            [EnvironmentalObservationKind.SkyQuality, EnvironmentalObservationKind.AirTemperature], CancellationToken.None,
            raw.Manifest.Descriptor.CycleEvidence!.AcquisitionAttemptId).ConfigureAwait(false);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, result.Single(row => row.Kind == EnvironmentalObservationKind.SkyQuality).Status);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, result.Single(row => row.Kind == EnvironmentalObservationKind.AirTemperature).Status);
    }
}
