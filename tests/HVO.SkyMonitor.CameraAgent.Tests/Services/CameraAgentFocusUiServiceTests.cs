using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>
/// The focus UI service over the real coordinator, owner boundary and VirtualSky module: every read and command is
/// authorized against the circuit principal, and session ownership follows the canonical owner id across circuits.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentFocusUiServiceTests
{
    private static readonly ManualFocusSessionRequest Request = new(new(TimeSpan.FromMilliseconds(50), 20), 400);
    private static readonly string[] ReadOnly = [CameraAgentAuthorizationPolicyNames.OperationsReadV1];
    private static readonly string[] Operator =
        [CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1];

    [TestMethod]
    public async Task StatusDuringContinuousPublication_PairsTheLatestMeasurementAndItsImages()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System,
            ManualFocusSessionLimits.Default with { MinimumSamplePeriod = TimeSpan.Zero }, store);
        var service = Service(coordinator, store, User("alice"), Operator);
        var started = await service.StartAsync(Request, CancellationToken.None).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reads = 0;
        long observed = 0;
        while (observed < 8)
        {
            var result = await service.GetStatusAsync(deadline.Token).ConfigureAwait(false);
            var status = result.Value!;
            if (status.Session.Latest is { } latest)
            {
                Assert.IsNotNull(status.Images);
                Assert.AreEqual(latest.Sequence, status.Images.Sequence,
                    "The same publication must provide metrics, centroid and crop even when another frame arrives.");
                observed = latest.Sequence;
                reads++;
            }
            await Task.Delay(2, deadline.Token).ConfigureAwait(false);
        }
        await service.StopAsync(started.Value!.SessionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThan(8, reads, "The reader sampled between successive camera publications.");
    }

    [TestMethod]
    public async Task OwnerIdentity_SpansCircuitsWhileOtherOperatorsAreRefused()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, store: store);
        var aliceFirstCircuit = Service(coordinator, store, User("alice"), Operator);
        var aliceSecondCircuit = Service(coordinator, store, User("alice"), Operator);
        var bob = Service(coordinator, store, User("bob"), Operator);

        var started = await aliceFirstCircuit.StartAsync(Request, CancellationToken.None).ConfigureAwait(false);
        await FocusWait.UntilAsync(coordinator, static session => session.TotalSamples >= 2, "two samples").ConfigureAwait(false);
        var aliceView = await aliceSecondCircuit.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var bobView = await bob.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var heartbeat = await aliceSecondCircuit.ObserveAsync(started.Value!.SessionId, CancellationToken.None).ConfigureAwait(false);
        var bobStop = await bob.StopAsync(started.Value.SessionId, CancellationToken.None).ConfigureAwait(false);
        var bobObserve = await bob.ObserveAsync(started.Value.SessionId, CancellationToken.None).ConfigureAwait(false);
        var bobStart = await bob.StartAsync(Request, CancellationToken.None).ConfigureAwait(false);
        var stopped = await aliceSecondCircuit.StopAsync(started.Value.SessionId, CancellationToken.None).ConfigureAwait(false);
        var saved = await aliceFirstCircuit.SaveAsync(started.Value.SessionId, CancellationToken.None).ConfigureAwait(false);
        var history = await bob.GetSavedSessionsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, started.Kind, started.Message);
        Assert.AreEqual("alice", started.Value.OwnerId);
        Assert.IsTrue(aliceView.Value!.IsOwner);
        Assert.IsTrue(aliceView.Value.CanControl);
        Assert.IsNotNull(aliceView.Value.Images, "The latest preview images are visible to readers.");
        Assert.IsFalse(bobView.Value!.IsOwner);
        Assert.IsTrue(bobView.Value.CanControl);
        Assert.AreEqual(OperatorUiResultKind.Success, heartbeat.Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, bobStop.Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, bobObserve.Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, bobStart.Kind);
        Assert.AreEqual(ManualFocusSessionState.Stopped, stopped.Value!.State);
        Assert.AreEqual(ManualFocusReasonCodes.StoppedByOperator, stopped.Value.EndReason);
        Assert.AreEqual(ManualFocusRetentionState.Saved, saved.Value!.Retention);
        var record = history.Value!.Single();
        Assert.AreEqual(saved.Value.SavedRecordId, record.RecordId);
        Assert.IsTrue(record.Verified);
        Assert.AreEqual("alice", record.SavedBy);
        Assert.AreEqual(Request.Settings, record.BestSettings);
        Assert.AreEqual(400d, record.BestSimulatedFocusPosition);
        Assert.AreEqual(ManualFocusTargetSource.Automatic, record.BestTargetSource);
    }

    [TestMethod]
    public async Task ReadersSeeTheSessionButOnlyMutatorsWithAnOwnerIdCanCommand()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, store: store);
        var reader = Service(coordinator, store, User("carol"), ReadOnly);
        var system = Service(coordinator, store, System(), Operator);
        var anonymous = Service(coordinator, store, new ClaimsPrincipal(new ClaimsIdentity()), []);

        var readerView = await reader.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var readerStart = await reader.StartAsync(Request, CancellationToken.None).ConfigureAwait(false);
        var systemStart = await system.StartAsync(Request, CancellationToken.None).ConfigureAwait(false);
        var anonymousView = await anonymous.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var anonymousHistory = await anonymous.GetSavedSessionsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, readerView.Kind);
        Assert.IsFalse(readerView.Value!.CanControl);
        Assert.IsFalse(readerView.Value.IsOwner);
        Assert.IsTrue(readerView.Value.Availability.Available);
        Assert.IsTrue(readerView.Value.RetentionAvailable);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, readerStart.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, systemStart.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, anonymousView.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, anonymousHistory.Kind);
        Assert.AreEqual(ManualFocusSessionState.Idle, coordinator.Snapshot.State, "No refused command reached the coordinator.");
    }

    [TestMethod]
    public async Task InvalidAndUnavailableCommands_MapToSanitizedResultKinds()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, store: store);
        var alice = Service(coordinator, store, User("alice"), Operator);

        var withoutModule = await alice.StartAsync(Request, CancellationToken.None).ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        var gainTooHigh = await alice.StartAsync(Request with { Settings = new(TimeSpan.FromSeconds(1), 5000) }, CancellationToken.None)
            .ConfigureAwait(false);
        var missingSession = await alice.StopAsync("no-such-session", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, withoutModule.Kind);
        Assert.AreEqual(ManualFocusReasonCodes.NoModule, withoutModule.Message);
        Assert.AreEqual(OperatorUiResultKind.Invalid, gainTooHigh.Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, missingSession.Kind);
        Assert.AreEqual(ManualFocusSessionState.Idle, coordinator.Snapshot.State);
    }

    private static CameraAgentFocusUiService Service(
        ManualFocusSessionCoordinator coordinator,
        IManualFocusSessionStore store,
        ClaimsPrincipal principal,
        IReadOnlyCollection<string> policies)
        => new(new FixedAuthenticationStateProvider(principal), new PolicyAuthorizationService(principal, policies), coordinator, store,
            NullLogger<CameraAgentFocusUiService>.Instance);

    private static ClaimsPrincipal User(string ownerId) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ownerId),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ],
        IdentityConstants.ApplicationScheme));

    private static ClaimsPrincipal System() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "system-1"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.SystemAccountType)
        ],
        IdentityConstants.ApplicationScheme));

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class PolicyAuthorizationService(ClaimsPrincipal principal, IReadOnlyCollection<string> policies)
        : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
            => Task.FromResult(ReferenceEquals(user, principal) && policies.Contains(policyName)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
            => throw new NotSupportedException("The focus UI service authorizes by policy name.");
    }
}
