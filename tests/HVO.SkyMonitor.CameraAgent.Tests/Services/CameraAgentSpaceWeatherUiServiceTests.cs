using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>
/// The space-weather operator service over the real store, operations service and shared projection read. Only
/// authorization, the clock and the provider client are scripted.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSpaceWeatherUiServiceTests
{
    private const string Owner = "owner-id";

    private static readonly string[] DeniedCommands =
    [
        "Space-weather UI access denied (refresh).",
        "Space-weather UI access denied (resume).",
        "Space-weather UI access denied (acknowledge)."
    ];

    [TestMethod]
    public async Task GetAsync_WhenTheReadPolicyFails_IsUnauthorizedBeforeAnyStoreReadAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var service = CreateService(host, read: false, mutate: true, out var authorization);

        var result = await service.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsNull(result.Value);
        Assert.IsEmpty(host.Store.Calls);
        Assert.AreEqual("Space-weather UI access denied (read).", LogsOf(host, 7837).Single());
        authorization.Verify(candidate => candidate.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null,
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetAsync_ReportsWhetherTheOperatorMayRunCommandsAsync(bool mutate)
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: mutate, out _);

        var result = await service.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(SpaceWeatherUiState.Ready, result.Value!.Projection.State);
        Assert.AreEqual(mutate, result.Value.CanMutate);
        Assert.IsEmpty(LogsOf(host, 7837));
    }

    [TestMethod]
    public async Task GetAsync_WithAnOwnerIdentityOver128Characters_CannotRunCommandsAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _, new string('o', 129));

        var result = await service.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.IsFalse(result.Value!.CanMutate);
    }

    [TestMethod]
    public async Task Commands_WhenTheMutatePolicyFails_AreUnauthorizedAndRunNothingAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        await host.SaveAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: false, out _);

        var refresh = await service.RefreshAsync("ui-refresh", default).ConfigureAwait(false);
        var resume = await service.ResumeAsync(
            new SpaceWeatherResumeRequest("provider", null, host.Clock.GetUtcNow()), "ui-resume", default).ConfigureAwait(false);
        var acknowledge = await service.AcknowledgeAsync("ALTZZZ/1", "ui-ack", default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, refresh.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, resume.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, acknowledge.Kind);
        Assert.IsEmpty(host.Store.Calls);
        Assert.IsEmpty((await host.Automation.GetStateAsync(default).ConfigureAwait(false)).Runs);
        CollectionAssert.AreEqual(DeniedCommands, LogsOf(host, 7837));
    }

    [TestMethod]
    [DataRow(129, false)]
    [DataRow(128, true)]
    public async Task Commands_RecordTheOwnerIdentityOnlyUpTo128CharactersAsync(int length, bool recorded)
    {
        var expected = recorded ? OperatorUiResultKind.Success : OperatorUiResultKind.Invalid;
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        await host.SaveAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _, new string('o', length));

        var result = await service.RefreshAsync("ui-refresh", default).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Kind);
        Assert.AreEqual(expected == OperatorUiResultKind.Success ? 1 : 0,
            (await host.Automation.GetStateAsync(default).ConfigureAwait(false)).Runs.Count);
        if (expected == OperatorUiResultKind.Invalid)
        {
            Assert.IsNull(result.Value);
            Assert.IsEmpty(host.Store.Calls);
        }
    }

    [TestMethod]
    public async Task Commands_MapAllSevenDispositionsAsync()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]);
        await host.InitializeAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _);

        var conflict = await service.RefreshAsync("ui-missing", default).ConfigureAwait(false);
        await host.SaveAsync().ConfigureAwait(false);
        var accepted = await service.RefreshAsync("ui-refresh", default).ConfigureAwait(false);
        var replayed = await service.RefreshAsync("ui-refresh", default).ConfigureAwait(false);
        var applied = await service.ResumeAsync(
            new SpaceWeatherResumeRequest("provider", null, host.Clock.GetUtcNow()), "ui-resume", default).ConfigureAwait(false);
        var invalid = await service.RefreshAsync(
            new string('k', LocalAutomationContract.MaximumIdempotencyKeyLength + 1), default).ConfigureAwait(false);
        var notFound = await service.AcknowledgeAsync("ALTZZZ/999", "ui-ack", default).ConfigureAwait(false);
        host.Store.ReadFailure = new SwpcStorageException();
        var unavailable = await service.RefreshAsync("ui-unavailable", default).ConfigureAwait(false);

        AssertMapped(accepted, OperatorUiResultKind.Success, SpaceWeatherOperations.Disposition.Accepted);
        AssertMapped(replayed, OperatorUiResultKind.Success, SpaceWeatherOperations.Disposition.Replayed);
        AssertMapped(applied, OperatorUiResultKind.Success, SpaceWeatherOperations.Disposition.Applied);
        AssertMapped(conflict, OperatorUiResultKind.Conflict, SpaceWeatherOperations.Disposition.Conflict);
        AssertMapped(invalid, OperatorUiResultKind.Invalid, SpaceWeatherOperations.Disposition.Invalid);
        AssertMapped(notFound, OperatorUiResultKind.NotFound, SpaceWeatherOperations.Disposition.NotFound);
        AssertMapped(unavailable, OperatorUiResultKind.Unavailable, SpaceWeatherOperations.Disposition.Unavailable);
        Assert.AreEqual("space-weather.definition-missing", conflict.Value!.ReasonCode);
        Assert.AreEqual(SpaceWeatherAvailability.StoreUnavailableReasonCode, unavailable.Value!.ReasonCode);
        Assert.IsFalse(unavailable.Value.Status.StoreAvailable);
    }

    [TestMethod]
    public async Task Resume_PassesTheHaltInstantItWasGivenAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var since = await host.HaltAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _);

        var stale = await service.ResumeAsync(
            new SpaceWeatherResumeRequest("provider", null, since.AddTicks(1)), "ui-stale", default).ConfigureAwait(false);
        var resumed = await service.ResumeAsync(
            new SpaceWeatherResumeRequest("provider", null, since), "ui-resume", default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, stale.Kind);
        Assert.AreEqual("space-weather.resume-halt-changed", stale.Value!.ReasonCode);
        Assert.AreEqual(OperatorUiResultKind.Success, resumed.Kind);
        Assert.AreEqual("resumed", resumed.Value!.Outcome);
        Assert.IsNull(resumed.Value.Status.ProviderHalt);
    }

    [TestMethod]
    public async Task Acknowledge_RecordsTheSignedInOwnerAsync()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]);
        await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Body = _ => System.Text.Encoding.UTF8.GetBytes(SwpcStoreTests.Message());
        await host.RunAsync().ConfigureAwait(false);
        var group = (await host.ViewAsync().ConfigureAwait(false)).Notices.Groups.Single().Identity;
        var service = CreateService(host, read: true, mutate: true, out _);

        var result = await service.AcknowledgeAsync(group, "ui-ack", default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual("acknowledged", result.Value!.Outcome);
        Assert.AreEqual(Owner, (await host.ViewAsync().ConfigureAwait(false)).Notices.Acknowledgements.Single().Value.Operator);
    }

    [TestMethod]
    public async Task Commands_DropTheSharedProjectionSoTheNextReadIsCurrentAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _);
        await service.GetAsync(default).ConfigureAwait(false);
        await service.GetAsync(default).ConfigureAwait(false);
        Assert.AreEqual(2, Reads(host), "A second read within the time-to-live reuses the shared projection.");

        var refresh = await service.RefreshAsync("ui-missing", default).ConfigureAwait(false);
        var afterCommand = Reads(host);
        await service.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, refresh.Kind);
        Assert.AreEqual(afterCommand + 2, Reads(host));
    }

    [TestMethod]
    public async Task ACommandFailure_IsUnavailableLogsOnlyItsTypeAndStillDropsTheProjectionAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _);
        await service.GetAsync(default).ConfigureAwait(false);
        host.Store.ReadFailure = new InvalidOperationException("secret-detail at a private location");

        var result = await service.RefreshAsync("ui-refresh", default).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.IsNull(result.Value);
        Assert.AreEqual("The space-weather command could not be completed.", result.Message);
        Assert.AreEqual("Space-weather UI command failed (refresh, InvalidOperationException).", LogsOf(host, 7836).Single());

        var read = await service.GetAsync(default).ConfigureAwait(false);
        Assert.AreEqual(SpaceWeatherUiState.ReadFailed, read.Value!.Projection.State, "The command dropped the cached projection.");
        Assert.AreEqual("Space-weather UI read failed (InvalidOperationException).", LogsOf(host, 7835).Single());
        lock (host.Logs)
        {
            Assert.IsFalse(host.Logs.Any(static entry => entry.Text.Contains("secret-detail", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task Cancellation_PropagatesWithoutStartingAReadOrACommandAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        await host.SaveAsync().ConfigureAwait(false);
        var service = CreateService(host, read: true, mutate: true, out _);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.GetAsync(cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.RefreshAsync("ui-refresh", cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(0, Reads(host));
        Assert.IsEmpty((await host.Automation.GetStateAsync(default).ConfigureAwait(false)).Runs);
        Assert.IsEmpty(LogsOf(host, 7835));
        Assert.IsEmpty(LogsOf(host, 7836));
    }

    [TestMethod]
    public async Task Cancellation_WhileWaitingForTheSharedReadPropagatesAndLeavesTheReadRunningAsync()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.ReadOverride = async (at, ct) =>
        {
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
        };
        var service = CreateService(host, read: true, mutate: true, out _);
        using var leaving = new CancellationTokenSource();

        var left = service.GetAsync(leaving.Token).AsTask();
        await leaving.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => left).ConfigureAwait(false);
        release.SetResult();

        Assert.AreEqual(SpaceWeatherUiState.Ready, (await service.GetAsync(default).ConfigureAwait(false)).Value!.Projection.State,
            "The shared read a cancelled caller left still completes for the next caller.");
        Assert.AreEqual(2, Reads(host));
        Assert.IsEmpty(LogsOf(host, 7835));
    }

    private static void AssertMapped(
        OperatorUiResult<SpaceWeatherCommandView> result,
        OperatorUiResultKind kind,
        SpaceWeatherOperations.Disposition disposition)
    {
        Assert.AreEqual(kind, result.Kind, disposition.ToString());
        Assert.AreEqual(disposition, result.Value!.Disposition);
        Assert.AreEqual(SpaceWeatherOperatorText.Describe(result.Value.ReasonCode), result.Message);
    }

    private static CameraAgentSpaceWeatherUiService CreateService(
        SpaceWeatherTestHost host,
        bool read,
        bool mutate,
        out Mock<IAuthorizationService> authorization,
        string owner = Owner)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, owner),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(read ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(mutate ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var source = new SpaceWeatherUiProjectionSource(host.Availability, host.Store, host.Operations,
            new FixedLocations(SpaceWeatherUiTestData.Site()), host.Options, host.Clock,
            host.Logger<SpaceWeatherUiProjectionSource>(), new TestSun());
        return new CameraAgentSpaceWeatherUiService(
            new StubAuthenticationStateProvider(user),
            authorization.Object,
            host.Operations,
            source,
            host.Logger<CameraAgentSpaceWeatherUiService>());
    }

    private static int Reads(SpaceWeatherTestHost host)
        => host.Store.Calls.Count(static call => call == "read");

    private static string[] LogsOf(SpaceWeatherTestHost host, int id)
    {
        lock (host.Logs)
        {
            return host.Logs.Where(entry => entry.Id == id).Select(static entry => entry.Text).ToArray();
        }
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedLocations(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => active;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken) => ValueTask.FromResult(active);

        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => active;
    }
}
