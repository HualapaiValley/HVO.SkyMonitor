using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOperatorUiServiceTests
{
    [TestMethod]
    public async Task EveryReadAndMutation_ReReadsPrincipalAndReauthorizesAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-id")],
            "test"));
        var authentication = new CountingAuthenticationStateProvider(principal);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(
                principal,
                null,
                It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Failed());
        var service = CreateService(authentication, authorization.Object);

        var operations = await service.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
        var gallery = await service.GetGalleryPageAsync(new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);
        var quarantine = await service.GetQuarantinePageAsync("Artifact", null, null, 25, CancellationToken.None).ConfigureAwait(false);
        var detail = await service.GetGalleryCaptureAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var system = await service.GetSystemStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var capture = await service.SetCapturePausedAsync(true, 1, "operation-key", CancellationToken.None).ConfigureAwait(false);
        var outbox = await service.ResolveOutboxAsync(
            "Artifact",
            OutboxOperationAction.Replay,
            "action-token",
            "evidence-restored",
            "operation-key",
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(7, authentication.ReadCount);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, operations.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, gallery.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, quarantine.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, detail.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, system.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, capture.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, outbox.Kind);
        authorization.Verify(service => service.AuthorizeAsync(
            principal,
            null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Exactly(5));
        authorization.Verify(service => service.AuthorizeAsync(
            principal,
            null,
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Exactly(2));
    }

    [TestMethod]
    public void SystemAliases_ResolveRegisteredAssemblyTypesAndRejectUnknownTypes()
    {
        var implementationType = typeof(CameraAgentOperatorUiServiceTests);
        var assemblyQualified = implementationType.AssemblyQualifiedName!;
        var processing = new CaptureProcessingStepRegistration(
            "SafeProcessing",
            implementationType,
            typeof(object));
        var module = new CameraModuleRegistration("SafeModule", implementationType);

        Assert.AreEqual(
            "SafeProcessing",
            CameraAgentOperatorUiService.ResolveProcessingAlias(assemblyQualified, [processing]));
        Assert.AreEqual(
            "SafeModule",
            CameraAgentOperatorUiService.ResolveModuleAlias(assemblyQualified, [module]));
        Assert.AreEqual(
            "Unavailable",
            CameraAgentOperatorUiService.ResolveProcessingAlias("Unknown.Type, Unknown.Assembly", [processing]));
        Assert.AreEqual(
            "Unavailable",
            CameraAgentOperatorUiService.ResolveModuleAlias("Unknown.Type, Unknown.Assembly", [module]));
    }

    [TestMethod]
    public void SnapshotIdentityIsCanonicalAndChangesOnlyWithAllowlistedDtoFacts()
    {
        var status = OperatorUiTestData.SystemStatus() with
        {
            SnapshotIdentity = "ignored",
            Pipeline =
            [
                new("z-node", "Preview", true, ["b", "a"]),
                new("a-node", "Calibration", false, [])
            ]
        };
        var reordered = status with
        {
            SnapshotIdentity = "another-ignored-value",
            Pipeline =
            [
                new("a-node", "Calibration", false, []),
                new("z-node", "Preview", true, ["a", "b"])
            ]
        };
        var changed = status with { Capture = status.Capture with { NightGain = status.Capture.NightGain + 1 } };
        var standalone = status with { CentralIntegration = "Disabled" };

        var identity = CameraAgentOperatorUiService.ComputeSnapshotIdentity(status);

        Assert.AreEqual(identity, CameraAgentOperatorUiService.ComputeSnapshotIdentity(reordered));
        Assert.AreNotEqual(identity, CameraAgentOperatorUiService.ComputeSnapshotIdentity(changed));
        Assert.AreNotEqual(identity, CameraAgentOperatorUiService.ComputeSnapshotIdentity(standalone));
        Assert.AreEqual(64, identity.Length);
        Assert.IsTrue(identity.All(Uri.IsHexDigit));
        var serialized = System.Text.Json.JsonSerializer.Serialize(status with { SnapshotIdentity = identity });
        Assert.IsFalse(serialized.Contains("/private/config", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("client-secret", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("options", StringComparison.OrdinalIgnoreCase));
    }

    private static CameraAgentOperatorUiService CreateService(
        AuthenticationStateProvider authentication,
        IAuthorizationService authorization) => new(
            authentication,
            authorization,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            [],
            [],
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = "/unused" }),
            null!,
            TimeProvider.System,
            NullLogger<CameraAgentOperatorUiService>.Instance);

    private sealed class CountingAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        internal int ReadCount { get; private set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            ReadCount++;
            return Task.FromResult(new AuthenticationState(principal));
        }
    }
}
