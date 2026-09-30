using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentRegistrationUiServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentity Identity = new("device-identity-1", "PAIR1234", Now.AddDays(-2));
    private static readonly Guid DevicePublicId = Guid.Parse("0b9c7ab1-4d47-4e2b-9f5f-6c1c1b8e2a10");
    private static readonly Guid ObservatoryId = Guid.Parse("5d0f3c2e-1a6b-4f7d-8e9a-2b3c4d5e6f70");

    [TestMethod]
    public async Task GetRegistration_WithoutReadRights_ReadsNothingAsync()
    {
        // Strict stores with no setups fail the test if a denied read touches them.
        var service = CreateService(new Harness { ReadAllowed = false });

        var result = await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task GetRegistration_WhenStandalone_CreatesNoIdentityAsync()
    {
        var service = CreateService(new Harness { Central = false });

        var result = await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var view = result.Value!;
        Assert.AreEqual(RegistrationState.Standalone, view.State);
        Assert.IsNull(view.Identity);
        Assert.IsNull(view.Record);
        Assert.IsNull(view.LogicHost);
        Assert.IsNull(view.RegistrationUri);
        Assert.IsTrue(view.CanImport);
    }

    [TestMethod]
    public async Task GetRegistration_WithoutAnImportedEnvelope_IsNotRegisteredAsync()
    {
        var service = CreateService(new Harness());

        var result = await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false);

        var view = result.Value!;
        Assert.AreEqual(RegistrationState.NotRegistered, view.State);
        Assert.AreEqual(Identity.DeviceId, view.Identity!.DeviceId);
        Assert.AreEqual(Identity.VerificationCode, view.Identity.VerificationCode);
        Assert.IsNull(view.Record);
        Assert.AreEqual("logic.example", view.LogicHost);
        Assert.AreEqual(new Uri("https://logic.example/devices/register"), view.RegistrationUri);
        Assert.IsTrue(view.CaptureIdentityAligned);
    }

    [TestMethod]
    public async Task GetRegistration_WithAFreshAcknowledgement_IsActiveAsync()
    {
        var harness = new Harness { Secrets = Secrets(Now.AddHours(-1)) };
        harness.Heartbeat.Update(Outbox(), FleetAvailability.Available, "ready", Now.AddSeconds(-10));
        harness.Outbox.MarkInitialized();
        var service = CreateService(harness);

        var view = (await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(RegistrationState.Active, view.State);
        Assert.IsTrue(view.Heartbeat.Ready);
        Assert.IsTrue(view.Delivery.Ready);
        Assert.AreEqual("Roof camera", view.Record!.FriendlyName);
        Assert.AreEqual(DeploymentLocationResolutionStatus.Acknowledged, view.Record.LocationReview);
    }

    [TestMethod]
    [DataRow(-3_601, DisplayName = "Acknowledged before the envelope was imported")]
    [DataRow(-31, DisplayName = "Acknowledged longer ago than three heartbeat intervals")]
    public async Task GetRegistration_WithoutACurrentAcknowledgement_IsWaitingAsync(int acknowledgedSecondsAgo)
    {
        var harness = new Harness { Secrets = Secrets(Now.AddHours(-1)) };
        harness.Heartbeat.Update(Outbox(), FleetAvailability.Available, "ready", Now.AddSeconds(acknowledgedSecondsAgo));
        var service = CreateService(harness);

        var view = (await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(RegistrationState.Waiting, view.State);
        Assert.IsFalse(view.Heartbeat.Ready);
    }

    [TestMethod]
    public async Task GetRegistration_WhenLogicHostRefusesTheCredentials_IsRejectedAsync()
    {
        var harness = new Harness { Secrets = Secrets(Now.AddHours(-1)) };
        harness.Heartbeat.Update(
            Outbox(), FleetAvailability.Degraded, CameraAgentRegistrationUiService.CredentialsRejectedReason, Now.AddMinutes(-20));
        var service = CreateService(harness);

        var view = (await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(RegistrationState.Rejected, view.State);
        Assert.IsTrue(view.Heartbeat.CredentialsRejected);
    }

    [TestMethod]
    public async Task GetRegistration_WhenTheProtectedRecordCannotBeRead_IsUnavailableAndKeepsTheIdentityAsync()
    {
        var service = CreateService(new Harness { SecretFailure = new CryptographicException("key ring missing") });

        var result = await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(RegistrationState.Unavailable, result.Value!.State);
        Assert.AreEqual(Identity.DeviceId, result.Value.Identity!.DeviceId);
        Assert.IsNull(result.Value.Record);
    }

    [TestMethod]
    public async Task GetRegistration_WithoutChangeRights_CannotImportAsync()
    {
        var service = CreateService(new Harness { MutateAllowed = false });

        var view = (await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsFalse(view.CanImport);
    }

    [TestMethod]
    public async Task GetRegistration_WhenCaptureIdentityDiffers_SaysSoAsync()
    {
        var service = CreateService(new Harness { CaptureAgentId = "legacy-agent" });

        var view = (await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsFalse(view.CaptureIdentityAligned);
    }

    [TestMethod]
    public async Task GetRegistration_ProjectsNoCredentialOrCentralIdentifierAsync()
    {
        var harness = new Harness { Secrets = Secrets(Now.AddHours(-1)) };
        harness.Heartbeat.Update(Outbox(), FleetAvailability.Available, "ready", Now.AddSeconds(-5));
        var service = CreateService(harness);

        var view = (await service.GetRegistrationAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
        var projected = JsonSerializer.Serialize(view);

        foreach (var secret in new[]
                 {
                     "registration-token-value", "device-key-value", "client-id-value", "client-secret-value",
                     "/api/heartbeat", "/api/device/profile/rig", "logichost:8080",
                     DevicePublicId.ToString(), ObservatoryId.ToString(),
                 })
        {
            Assert.IsFalse(projected.Contains(secret, StringComparison.OrdinalIgnoreCase), secret);
        }
    }

    [TestMethod]
    public async Task ImportEnvelope_WithoutChangeRights_SendsNothingAsync()
    {
        var service = CreateService(new Harness { MutateAllowed = false });

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
    }

    [TestMethod]
    public async Task ImportEnvelope_SendsTheTrimmedEnvelopeAndReturnsOnlyTheReceiptAsync()
    {
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync("envelope-body", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Secrets(Now) with { DeploymentLocationAcknowledgment = null });
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("  envelope-body\n", CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Roof camera", result.Value!.FriendlyName);
        Assert.AreEqual(DeploymentLocationResolutionStatus.Pending, result.Value.LocationReview);
        harness.Workflow.VerifyAll();
    }

    [TestMethod]
    [DataRow("", DisplayName = "Empty")]
    [DataRow(" \n\t ", DisplayName = "Whitespace")]
    public async Task ImportEnvelope_WithNothingPasted_AsksForTheEnvelopeAsync(string envelope)
    {
        var service = CreateService(new Harness());

        var result = await service.ImportEnvelopeAsync(envelope, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual("Paste the envelope LogicHost issued for this device.", result.Message);
    }

    [TestMethod]
    public async Task ImportEnvelope_LongerThanAnyEnvelope_IsRefusedBeforeSendingAsync()
    {
        var service = CreateService(new Harness());

        var result = await service.ImportEnvelopeAsync(
            new string('e', CameraAgentRegistrationUiService.EnvelopeMaxLength + 1), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        StringAssert.StartsWith(result.Message, "That is longer than any envelope LogicHost issues.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenStandalone_IsRefusedBeforeSendingAsync()
    {
        var service = CreateService(new Harness { Central = false });

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual("Central integration is off, so this CameraAgent cannot register with LogicHost.", result.Message);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenLogicHostRejectsIt_AsksForANewEnvelopeAsync()
    {
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gone", null, HttpStatusCode.Gone));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("expired-envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        StringAssert.StartsWith(result.Message, "LogicHost rejected the envelope.", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(HttpRequestError.NameResolutionError, DisplayName = "Name resolution")]
    [DataRow(HttpRequestError.ConnectionError, DisplayName = "Connection")]
    [DataRow(HttpRequestError.SecureConnectionError, DisplayName = "TLS")]
    public async Task ImportEnvelope_WhenLogicHostCannotBeReached_NeverPromisesTheEnvelopeIsUnusedAsync(HttpRequestError error)
    {
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(error, "Connection failed"));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        StringAssert.StartsWith(result.Message, "LogicHost could not be reached, and nothing was stored here.", StringComparison.Ordinal);
        StringAssert.Contains(result.Message, "If LogicHost now shows the envelope as used, issue a new one", StringComparison.Ordinal);
        Assert.IsFalse(result.Message!.Contains("was not used", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenLogicHostFailsAfterReceivingIt_SaysTheEnvelopeMayBeUsedAsync()
    {
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable", null, HttpStatusCode.ServiceUnavailable));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        StringAssert.StartsWith(result.Message, "LogicHost's answer did not arrive, and nothing was stored here.", StringComparison.Ordinal);
        StringAssert.Contains(result.Message, "may still have used the envelope", StringComparison.Ordinal);
        Assert.IsFalse(result.Message!.Contains("can be used until it expires", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenLogicHostDoesNotAnswer_SaysItTimedOutAsync()
    {
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("HttpClient timeout"));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        StringAssert.StartsWith(result.Message, "LogicHost did not answer in time, and nothing was stored here.", StringComparison.Ordinal);
        StringAssert.Contains(result.Message, "may still have used the envelope", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenAStepAfterSavingFails_ReportsTheStoredRegistrationAsync()
    {
        var harness = new Harness { SecretReads = [null, Secrets(Now)] };
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("staging failed"));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value!.SetupIncomplete);
        Assert.AreEqual("Roof camera", result.Value.FriendlyName);
        Assert.AreEqual(DeploymentLocationResolutionStatus.Acknowledged, result.Value.LocationReview);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenTheEarlierReadFailed_DoesNotCreditTheImportWithTheStoredRecordAsync()
    {
        var harness = new Harness { SecretReads = [new IOException("file busy"), Secrets(Now.AddDays(-5))] };
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Deployment location is not initialized."));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        StringAssert.StartsWith(result.Message, "The import stopped partway, and this CameraAgent could not confirm whether a registration was stored.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ImportEnvelope_WithAnUnreadableRecordAndLogicHostUnreachable_ReportsTheConnectionFailureAsync()
    {
        var harness = new Harness { SecretReads = [new CryptographicException("key ring lost"), new CryptographicException("key ring lost")] };
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        StringAssert.StartsWith(result.Message, "LogicHost could not be reached, and nothing was stored here.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenTheExistingRegistrationIsUnchanged_ReportsTheFailureAsync()
    {
        var harness = new Harness { SecretReads = [Secrets(Now.AddDays(-5)), Secrets(Now.AddDays(-5))] };
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Conflict", null, HttpStatusCode.Conflict));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        StringAssert.StartsWith(result.Message, "LogicHost rejected the envelope.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenTheStoreCannotBeReadAfterAFailure_SaysTheOutcomeIsUnknownAsync()
    {
        var harness = new Harness { SecretReads = [null, new InvalidDataException("torn write")] };
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        StringAssert.StartsWith(result.Message, "The import stopped partway, and this CameraAgent could not confirm whether a registration was stored.", StringComparison.Ordinal);
        Assert.IsFalse(result.Message!.Contains("nothing was stored", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenThePageIsClosed_PropagatesTheCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, token) =>
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return Secrets(Now);
            });
        var service = CreateService(harness);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ImportEnvelopeAsync("envelope", cancellation.Token).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ImportEnvelope_WhenTheResponseCannotBeDecrypted_NamesTheLikelyCauseAsync()
    {
        var harness = new Harness();
        harness.Workflow.Setup(value => value.BootstrapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CryptographicException("padding is invalid"));
        var service = CreateService(harness);

        var result = await service.ImportEnvelopeAsync("envelope", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        StringAssert.Contains(result.Message, "could not be decrypted", StringComparison.Ordinal);
        Assert.IsFalse(result.Message!.Contains("padding", StringComparison.Ordinal));
    }

    private static CameraAgentRegistrationUiService CreateService(Harness harness)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(harness.ReadAllowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(harness.MutateAllowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var identityStore = new Mock<IDeviceIdentityStore>(MockBehavior.Strict);
        var secretStore = new Mock<IDeviceSecretStore>(MockBehavior.Strict);
        var configuration = new CameraAgentConfigurationAccessor();
        if (harness.Central && harness.ReadAllowed)
        {
            identityStore.Setup(value => value.GetOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Identity);
            if (harness.SecretReads is { } reads)
            {
                var sequence = secretStore.SetupSequence(value => value.GetAsync(It.IsAny<CancellationToken>()));
                foreach (var read in reads)
                {
                    sequence = read is Exception readFailure
                        ? sequence.ThrowsAsync(readFailure)
                        : sequence.ReturnsAsync((DeviceSecrets?)read);
                }
            }
            else
            {
                var secrets = secretStore.Setup(value => value.GetAsync(It.IsAny<CancellationToken>()));
                if (harness.SecretFailure is { } failure)
                {
                    secrets.ThrowsAsync(failure);
                }
                else
                {
                    secrets.ReturnsAsync(harness.Secrets);
                }
            }
            configuration.SetConfiguration(ModuleConfig(harness.CaptureAgentId ?? Identity.DeviceId));
        }

        return new CameraAgentRegistrationUiService(
            new FixedAuthenticationStateProvider(principal),
            authorization.Object,
            identityStore.Object,
            secretStore.Object,
            harness.Workflow.Object,
            configuration,
            harness.Heartbeat,
            harness.Outbox,
            Options.Create(new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions
                {
                    Mode = harness.Central ? CentralIntegrationMode.Enabled : CentralIntegrationMode.Disabled,
                },
                CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = true },
            }),
            Options.Create(new SkyMonitorClientOptions
            {
                BaseUrl = new Uri("http://logichost:8080/"),
                PublicBaseUrl = new Uri("https://logic.example/"),
            }),
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentRegistrationUiService>.Instance);
    }

    private static CameraModuleConfig ModuleConfig(string agentId) => new(
        new ObservatoryLocation(0, 0, 0, "UTC"),
        new CameraModuleDescriptor("Test"),
        new CameraRigConfig(
            new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new OpticsProfile("EquidistantFisheye", 0, 180, 0),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
        CapturePipelineConfig.Empty,
        AgentId: agentId);

    private static FleetStatusOutboxSnapshot Outbox() => new(0, 0, 0, 0, 0, 0, 0, null, Now);

    private static DeviceSecrets Secrets(DateTimeOffset issuedUtc) => new(
        DevicePublicId,
        ObservatoryId,
        "Roof camera",
        "registration-token-value",
        "/api/heartbeat",
        10,
        issuedUtc,
        issuedUtc.AddDays(30),
        "device-key-value",
        new CentralIdentityOptions
        {
            ServiceUrl = new Uri("http://logichost:8080/"),
            ClientCredentials = new ClientCredentialsOptions { ClientId = "client-id-value", ClientSecret = "client-secret-value" },
        },
        DeploymentLocationAcknowledgment: Acknowledgment(issuedUtc));

    private static DeploymentLocationAcknowledgment Acknowledgment(DateTimeOffset evaluatedUtc) => new(
        ObservatoryLocationSnapshot.Create(
            ObservatoryId, 1, DateTimeOffset.UnixEpoch, 35.347, -113.878, 520, "America/Phoenix", 1000),
        DeploymentLocationSnapshot.Create(
            "registration-location", 1, "operator", 1, DateTimeOffset.UnixEpoch, null,
            35.347, -113.878, 520, "America/Phoenix"),
        DeploymentLocationSourceKind.Gps,
        DeploymentLocationResolutionStatus.Acknowledged,
        "within-observatory-boundary",
        evaluatedUtc,
        evaluatedUtc);

    private sealed class Harness
    {
        public bool ReadAllowed { get; init; } = true;

        public bool MutateAllowed { get; init; } = true;

        public bool Central { get; init; } = true;

        public DeviceSecrets? Secrets { get; init; }

        public Exception? SecretFailure { get; init; }

        /// <summary>Successive store reads, each a <see cref="DeviceSecrets"/>, null, or an exception to throw.</summary>
        public IReadOnlyList<object?>? SecretReads { get; init; }

        public string? CaptureAgentId { get; init; }

        public Mock<IDeviceBootstrapWorkflow> Workflow { get; } = new(MockBehavior.Strict);

        public FleetHeartbeatState Heartbeat { get; } = new();

        public ArtifactOutboxState Outbox { get; } = new();
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }
}
