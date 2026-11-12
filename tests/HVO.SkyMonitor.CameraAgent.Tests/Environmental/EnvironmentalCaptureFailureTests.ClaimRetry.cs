using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using Microsoft.Data.Sqlite;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    [DataRow(5, 1, false)]
    [DataRow(5, 3, true)]
    [DataRow(6, 1, true)]
    [DataRow(6, 3, false)]
    public async Task CaptureClaimRetriesContentionAndRetainsItsOutcomeAcrossRestart(int errorCode, int failures, bool readFails)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var attempts = new List<(string Key, string Payload)>();
        var bridge = ClaimRetryBridge(fixture, attempts, (attempt, _) => attempt <= failures
            ? new SqliteException("scripted writer contention", errorCode) : null);

        var acquisition = await bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquisition);
        Assert.AreEqual(failures + 1, attempts.Count);
        Assert.HasCount(1, attempts.Distinct().ToArray(), "Retries must preserve the durable acquisition identity.");
        Assert.AreEqual(attempts[0].Key, acquisition.Claims.Single().Key);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(2, readFails);
        await acquisition.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        fixture.Reopen();
        await fixture.Store.RecoverCaptureAcquisitionsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        var association = await fixture.AssociateAsync(raw.Manifest.Descriptor.Capture.CaptureId,
            raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, acquisition.Id).ConfigureAwait(false);
        Assert.AreEqual(readFails ? LocalEnvironmentalAssociationStatus.Missing : LocalEnvironmentalAssociationStatus.Fresh,
            association.Status, "A failed capture read cannot reuse the fixture's earlier successful sample.");
        if (readFails) Assert.IsNull(association.SelectedRecordId);
        Assert.AreEqual(association.AssociationIdentitySha256, (await fixture.AssociateAsync(
            raw.Manifest.Descriptor.Capture.CaptureId, raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, acquisition.Id)
            .ConfigureAwait(false)).AssociationIdentitySha256);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    public async Task PersistentCaptureClaimContentionStopsAfterFourAttempts(int errorCode)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var attempts = new List<(string Key, string Payload)>();
        var error = new SqliteException("scripted persistent contention", errorCode);
        var bridge = ClaimRetryBridge(fixture, attempts, (_, _) => error);

        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false));
        Assert.AreEqual(4, attempts.Count);
        Assert.HasCount(1, attempts.Distinct().ToArray());
        Assert.AreEqual(0L, (await CaptureMetadataCountsAsync(fixture.Root).ConfigureAwait(false)).Receipts,
            "Exhaustion must propagate before any acquisition request is admitted.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(10)]
    [DataRow(19)]
    public async Task CaptureClaimDoesNotRetryOtherSqliteFailures(int errorCode)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var attempts = new List<(string Key, string Payload)>();
        var error = new SqliteException("scripted non-contention failure", errorCode);
        var bridge = ClaimRetryBridge(fixture, attempts, (_, _) => error);

        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false));
        Assert.HasCount(1, attempts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CaptureClaimHonorsHostCancellationBeforeTheNextAttempt(bool cancelBeforeClaim)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var attempts = new List<(string Key, string Payload)>();
        var bridge = ClaimRetryBridge(fixture, attempts, (_, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            cancellation.Cancel();
            return new SqliteException("scripted contention at shutdown", 5);
        });
        if (cancelBeforeClaim) await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await bridge.BeforeCaptureAsync(Epoch, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(cancelBeforeClaim ? 0 : 1, attempts.Count);
    }

    [TestMethod]
    [DataRow(EnvironmentalOnDemandClaimDisposition.Busy)]
    [DataRow(EnvironmentalOnDemandClaimDisposition.Completed)]
    public async Task CaptureClaimIdentityConflictIsNeverRetried(EnvironmentalOnDemandClaimDisposition disposition)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var attempts = new List<(string Key, string Payload)>();
        var bridge = ClaimRetryBridge(fixture, attempts, (_, _) => null, disposition);

        await Assert.ThrowsExactlyAsync<EnvironmentalOnDemandCommandConflictException>(async () =>
            await bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.HasCount(1, attempts);
    }

    private static EnvironmentalCaptureTriggerBridge ClaimRetryBridge(AttributionFixture fixture,
        List<(string Key, string Payload)> attempts, Func<int, CancellationToken, Exception?> failure,
        EnvironmentalOnDemandClaimDisposition? disposition = null)
    {
        var state = new Mock<IEnvironmentalAcquisitionStateStore>(MockBehavior.Strict);
        state.As<IEnvironmentalOnDemandCommandStore>().Setup(value => value.ClaimOnDemandAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string root, string key, string payload, string sourceId, string actorId, string? reason,
                DateTimeOffset observed, TimeSpan lease, CancellationToken token) =>
            {
                attempts.Add((key, payload));
                var exception = failure(attempts.Count, token);
                if (exception is not null) return ValueTask.FromException<EnvironmentalOnDemandCommandClaim>(exception);
                return disposition.HasValue
                    ? ValueTask.FromResult(new EnvironmentalOnDemandCommandClaim(disposition.Value, string.Empty, observed, null))
                    : fixture.Store.ClaimOnDemandAsync(root, key, payload, sourceId, actorId, reason, observed, lease, token);
            });
        return fixture.CreateBridge(state.Object);
    }
}
