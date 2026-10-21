using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentAutomationUiServiceTests
{
    private const string SyntheticFailurePath = "/synthetic/automation/path";
    private const string SyntheticFailureMessage = "Synthetic automation I/O failure at " + SyntheticFailurePath;

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
    public async Task SaveAsync_WhenTheOwnerIdHas128Characters_AcceptsTheOwnerIdentityAsync()
    {
        var store = new RecordingStore();
        var ownerId = new string('o', 128);
        var authorization = CreateAuthorization(out var principal, succeeded: true, ownerId);
        var service = CreateService(store, principal, authorization.Object);

        var result = await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(ownerId, store.SaveRequests.Single().Actor);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commands_WhenTheOwnerIdHas129Characters_DenyWithoutTouchingTheStoreAsync(bool remove)
    {
        var store = new RecordingStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true, new string('o', 129));
        var service = CreateService(store, principal, authorization.Object);

        var result = remove
            ? await service.RemoveAsync(new LocalAutomationRemoveRequest("sky-temperature", 3, "key-1", "caller", null),
                CancellationToken.None).ConfigureAwait(false)
            : await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, store.Reads);
        Assert.IsEmpty(store.SaveRequests);
        Assert.IsEmpty(store.RemoveRequests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commands_WhenTheOwnerIdIsWhitespace_DenyWithoutTouchingTheStoreAsync(bool remove)
    {
        var store = new RecordingStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true, " \t ");
        var service = CreateService(store, principal, authorization.Object);

        var result = remove
            ? await service.RemoveAsync(new LocalAutomationRemoveRequest("sky-temperature", 3, "key-1", "caller", null),
                CancellationToken.None).ConfigureAwait(false)
            : await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, store.Reads);
        Assert.IsEmpty(store.SaveRequests);
        Assert.IsEmpty(store.RemoveRequests);
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
        var logger = new OrbitalTestLogger<CameraAgentAutomationUiService>();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(store, principal, authorization.Object, logger);

        var result = await service.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.IsFalse(result.Message!.Contains("/secret/automation/path", StringComparison.Ordinal));
        Assert.AreEqual("The automation command could not be completed.", result.Message);
        AssertSanitizedFailureLog(logger, "command");
    }

    [TestMethod]
    public async Task GetAsync_WhenTheStoreFails_ReturnsAFixedUnavailableStateWithoutLoggingExceptionTextAsync()
    {
        var store = new RecordingStore { Throw = true };
        var logger = new OrbitalTestLogger<CameraAgentAutomationUiService>();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(store, principal, authorization.Object, logger);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("Local automation state is unavailable.", result.Message);
        AssertSanitizedFailureLog(logger, "read");
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
    [DataRow("orbital.reserved-definition-id", "definition.definitionId",
        "The orbital refresh task accepts only the identifier orbital-elements-refresh.")]
    [DataRow("orbital.interval-below-provider-floor", "definition.triggerInterval",
        "The interval must be at least 7,200 seconds; the provider allows at most one attempt every two hours.")]
    [DataRow("orbital.interval-out-of-range", "definition.triggerInterval",
        "The interval must be at most 86,400 seconds.")]
    [DataRow("orbital.store-unavailable", null, "The orbital store is unavailable.")]
    public void DescribeFailure_UsesTheOrbitalTasksOwnText(string reasonCode, string? fieldPath, string expected)
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid, reasonCode, fieldPath, LocalAutomationOperatorState.Empty));

        Assert.AreEqual(expected, message);
    }

    [TestMethod]
    public void DescribeFailure_AnUnknownOrbitalCodeFallsBackToTheFieldText()
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid,
                "orbital.future-rule",
                "definition.definitionId",
                LocalAutomationOperatorState.Empty));

        StringAssert.Contains(message, "lower-case letter", StringComparison.Ordinal);
        Assert.IsFalse(message.Contains("orbital.future-rule", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("space-weather.reserved-definition-id", "definition.definitionId",
        "The space-weather refresh task accepts only the identifier space-weather-refresh.")]
    [DataRow("space-weather.interval-out-of-range", "definition.triggerInterval",
        "The interval must be between 60 and 120 seconds.")]
    public void DescribeFailure_UsesTheSpaceWeatherTasksOwnText(string reasonCode, string fieldPath, string expected)
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid, reasonCode, fieldPath, LocalAutomationOperatorState.Empty));

        Assert.AreEqual(expected, message);
    }

    [TestMethod]
    public void DescribeFailure_AnUnknownSpaceWeatherCodeFallsBackToTheFieldText()
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid,
                "space-weather.future-rule",
                "definition.definitionId",
                LocalAutomationOperatorState.Empty));

        StringAssert.Contains(message, "lower-case letter", StringComparison.Ordinal);
        Assert.IsFalse(message.Contains("space-weather.future-rule", StringComparison.Ordinal));
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

    [TestMethod]
    [DataRow("definition.definitionId", "lower-case letter")]
    [DataRow("definition.name", "single line")]
    [DataRow("definition.taskTarget", "single line")]
    [DataRow("definition.unknownField", "rejected before anything durable changed")]
    public void DescribeFailure_ExplainsEveryRejectedField(string fieldPath, string expected)
    {
        var message = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode,
                fieldPath,
                LocalAutomationOperatorState.Empty));

        StringAssert.Contains(message, expected, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DescribeFailure_QuotesTheContractBoundsRatherThanRepeatingThemAsLiterals()
    {
        var name = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode,
                "definition.name",
                LocalAutomationOperatorState.Empty));
        var target = CameraAgentAutomationUiService.DescribeFailure(
            new LocalAutomationCommandResult(
                LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode,
                "definition.taskTarget",
                LocalAutomationOperatorState.Empty));

        // A bound that moves must move the guidance with it rather than quietly lying.
        StringAssert.Contains(
            name,
            LocalAutomationContract.MaximumNameLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        StringAssert.Contains(
            target,
            LocalAutomationContract.MaximumTargetLength.ToString(
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

    private static void AssertSanitizedFailureLog(OrbitalTestLogger<CameraAgentAutomationUiService> logger, string operation)
    {
        Assert.HasCount(1, logger.Entries);
        foreach (var entry in logger.Entries)
        {
            Assert.IsNull(entry.Exception, "An exception object can disclose paths through the logging provider.");
            Assert.DoesNotContain(SyntheticFailurePath, entry.Message);
            Assert.DoesNotContain(SyntheticFailureMessage, entry.Message);
            Assert.AreEqual(LogLevel.Warning, entry.Level);
            Assert.AreEqual($"CameraAgent local automation {operation} failed (IOException).", entry.Message);
        }
    }

    private static Mock<IAuthorizationService> CreateAuthorization(
        out ClaimsPrincipal principal, bool succeeded, string ownerId = "owner-id")
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ownerId),
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
        IAuthorizationService authorization,
        ILogger<CameraAgentAutomationUiService>? logger = null)
        => new(
            new StubAuthenticationStateProvider(principal),
            authorization,
            store,
            logger ?? NullLogger<CameraAgentAutomationUiService>.Instance);

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
                    new IOException(SyntheticFailureMessage));
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
                    new IOException(SyntheticFailureMessage));
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
