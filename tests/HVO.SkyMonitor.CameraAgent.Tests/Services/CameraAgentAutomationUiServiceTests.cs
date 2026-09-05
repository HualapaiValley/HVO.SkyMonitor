using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentAutomationUiServiceTests
{
    [TestMethod]
    public async Task GetAsync_WhenTheReadPolicyFails_DeniesWithoutTouchingTheStoreAsync()
    {
        var store = new RecordingStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, store.Reads);
    }

    [TestMethod]
    public async Task SaveAsync_WhenAuthorized_ReplacesTheActorWithTheOwnerIdentityAsync()
    {
        var store = new RecordingStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.SaveAsync(SaveRequest("caller-supplied"), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        // The caller never chooses the recorded actor; the authenticated owner identity does.
        Assert.AreEqual("owner-id", store.SaveRequests.Single().Actor);
        authorization.Verify(
            candidate => candidate.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1),
            Times.Once);
    }

    [TestMethod]
    public async Task SaveAsync_WhenTheMutatePolicyFails_DeniesWithoutTouchingTheStoreAsync()
    {
        var store = new RecordingStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsEmpty(store.SaveRequests);
    }

    [TestMethod]
    public async Task RemoveAsync_WhenAuthorized_ReplacesTheActorAndForwardsTheExpectedVersionAsync()
    {
        var store = new RecordingStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.RemoveAsync(
            new LocalAutomationRemoveRequest("sky-temperature", 3, "key-1", "caller-supplied", "retired"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var request = store.RemoveRequests.Single();
        Assert.AreEqual("owner-id", request.Actor);
        Assert.AreEqual(3L, request.ExpectedVersion);
        Assert.AreEqual("key-1", request.IdempotencyKey);
    }

    [TestMethod]
    [DataRow(LocalAutomationCommandStatus.Conflict, "Conflict")]
    [DataRow(LocalAutomationCommandStatus.NotFound, "NotFound")]
    [DataRow(LocalAutomationCommandStatus.Invalid, "Invalid")]
    [DataRow(LocalAutomationCommandStatus.Applied, "Success")]
    [DataRow(LocalAutomationCommandStatus.Replayed, "Success")]
    [DataRow(LocalAutomationCommandStatus.Unchanged, "Success")]
    public async Task SaveAsync_MapsEveryDispositionOntoAnOperatorResultKindAsync(
        LocalAutomationCommandStatus status,
        string expectedKind)
    {
        var expected = Enum.Parse<OperatorUiResultKind>(expectedKind);
        var store = new RecordingStore { Status = status };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Kind);
    }

    [TestMethod]
    public async Task SaveAsync_WhenTheStoreFails_ReturnsAFixedUnavailableStateAsync()
    {
        var store = new RecordingStore { Throw = true };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.IsFalse(result.Message!.Contains("/secret/automation/path", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(LocalAutomationContract.ExpectedVersionConflictReasonCode, "Refresh before retrying")]
    [DataRow(LocalAutomationContract.IdempotencyKeyConflictReasonCode, "already recorded with different content")]
    [DataRow(LocalAutomationContract.UnknownDefinitionReasonCode, "no longer exists")]
    [DataRow(LocalAutomationContract.DefinitionLimitReasonCode, "maximum")]
    [DataRow(LocalAutomationContract.UnregisteredCombinationReasonCode, "not registered")]
    [DataRow(LocalAutomationContract.UnregisteredTargetReasonCode, "not a registered target")]
    public void DescribeFailure_ExplainsEveryContractReasonCode(string reasonCode, string expected)
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Conflict, reasonCode, null, LocalAutomationOperatorState.Empty));

        StringAssert.Contains(message, expected, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DescribeFailure_ExplainsAFieldRejectionWithinItsAdvertisedBounds()
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode,
                "definition.triggerInterval",
                LocalAutomationOperatorState.Empty));

        StringAssert.Contains(
            message,
            LocalAutomationContract.MinimumPeriodicIntervalSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        StringAssert.Contains(
            message,
            LocalAutomationContract.MaximumCaptureInterval.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static LocalAutomationSaveRequest SaveRequest(string actor = "caller-supplied")
        => new(
            "sky-temperature",
            "Sky temperature",
            true,
            LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            "virtual-sky-temperature",
            LocalAutomationTriggerKind.Periodic,
            3600,
            0,
            "key-1",
            actor,
            null);

    private static Mock<IAuthorizationService> CreateAuthorization(out ClaimsPrincipal principal, bool succeeded)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        principal = user;
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, It.IsAny<string>()))
            .ReturnsAsync(succeeded ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization;
    }

    private static CameraAgentAutomationUiService CreateService(
        ILocalAutomationStore store,
        ClaimsPrincipal principal,
        IAuthorizationService authorization)
        => new(
            new StubAuthenticationStateProvider(principal),
            authorization,
            store,
            NullLogger<CameraAgentAutomationUiService>.Instance);

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class RecordingStore : ILocalAutomationStore
    {
        internal int Reads { get; private set; }

        internal List<LocalAutomationSaveRequest> SaveRequests { get; } = [];

        internal List<LocalAutomationRemoveRequest> RemoveRequests { get; } = [];

        internal LocalAutomationCommandStatus Status { get; set; } = LocalAutomationCommandStatus.Applied;

        internal bool Throw { get; set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<LocalAutomationOperatorState> GetStateAsync(CancellationToken cancellationToken)
        {
            if (Throw)
            {
                return ValueTask.FromException<LocalAutomationOperatorState>(
                    new IOException("/secret/automation/path"));
            }
            Reads++;
            return ValueTask.FromResult(LocalAutomationOperatorState.Empty);
        }

        public ValueTask<LocalAutomationCommandResult> SaveAsync(
            LocalAutomationSaveRequest request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                return ValueTask.FromException<LocalAutomationCommandResult>(
                    new IOException("/secret/automation/path"));
            }
            SaveRequests.Add(request);
            return ValueTask.FromResult(new LocalAutomationCommandResult(
                Status, null, null, LocalAutomationOperatorState.Empty));
        }

        public ValueTask<LocalAutomationCommandResult> RemoveAsync(
            LocalAutomationRemoveRequest request,
            CancellationToken cancellationToken)
        {
            RemoveRequests.Add(request);
            return ValueTask.FromResult(new LocalAutomationCommandResult(
                Status, null, null, LocalAutomationOperatorState.Empty));
        }

        public ValueTask<IReadOnlyList<LocalAutomationRunnerEntry>> GetRunnerViewAsync(
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<bool> TryBeginRunAsync(
            LocalAutomationRunnerEntry entry,
            string runKey,
            DateTimeOffset scheduledForUtc,
            long? observedCaptureSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask CompleteRunAsync(
            string runKey,
            LocalAutomationRunOutcome outcome,
            string detail,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RecordTerminalRunAsync(
            LocalAutomationRunnerEntry entry,
            string runKey,
            DateTimeOffset scheduledForUtc,
            LocalAutomationRunOutcome outcome,
            string detail,
            long? observedCaptureSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask SetCaptureBaselineAsync(
            string definitionId,
            long captureSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
