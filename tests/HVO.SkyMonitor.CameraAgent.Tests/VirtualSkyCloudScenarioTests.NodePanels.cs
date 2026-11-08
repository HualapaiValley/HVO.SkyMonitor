using System.Security.Claims;
using System.Security.Cryptography;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    [TestMethod]
    [DataRow("MeasuredStellarAssociations", 1, "outputVariant", "virtual-measured", " ")]
    [DataRow("MeasuredAstrometry", 2, "captureStride", "120", "59")]
    [DataRow("CaptureSatelliteTracks", 3, "outputVariant", "virtual-tracks", " ")]
    [DataRow("CaptureSatelliteLayer", 4, "inputVariant", "virtual-tracks", " ")]
    [DataRow("Storage", 5, "retentionDays", "60", "0")]
    public async Task NodePanelsUseVirtualSkyAndRealRevisionServiceForEditRejectSaveReload(
        string alias, int index, string key, string value, string invalid)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-virtual-node-panels");
        try
        {
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var config = Config(CameraPixelFormat.Mono16, null) with
            {
                AgentId = "virtual-node-panels",
                Pipeline = ProcessingGraphOperationsTests.RemainingOptionsPipeline(root)
            };
            var module = Module();
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(new(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(capture.Frame);
            TestContext.WriteLine($"VirtualSky {alias} input SHA256={Convert.ToHexStringLower(SHA256.HashData(capture.Frame.PixelData.Span))}");

            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var authorization = context.AddAuthorization();
            authorization.SetAuthorized("owner");
            authorization.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "virtual-owner"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)],
                Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));
            Assert.AreEqual("virtual-owner", CameraAgentCredentialAccess.GetOwnerId(owner));
            context.Services.AddSingleton<AuthenticationStateProvider>(new NodePanelAuthenticationStateProvider(owner));
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(config);
            context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(services => new CameraAgentProcessingGraphUiService(
                services.GetRequiredService<AuthenticationStateProvider>(), services.GetRequiredService<IAuthorizationService>(),
                operations, provider.GetRequiredService<ICaptureProcessingPipelineFactory>(), accessor, null, TimeProvider.System,
                NullLogger<CameraAgentProcessingGraphUiService>.Instance));
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + initial.ActiveRevisionId);
            var source = await context.Services.GetRequiredService<ICameraAgentProcessingGraphUiService>()
                .GetRevisionDetailAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(source.IsSuccess, source.Message);
            var cut = context.Render<ProcessingGraphEditorPage>();
            var selector = "#node-options-" + index + "-" + key;
            await cut.WaitForElement(selector).ChangeAsync(new() { Value = value }).ConfigureAwait(false);
            if (alias == "CaptureSatelliteTracks") await cut.Find("#node-options-4-inputVariant").ChangeAsync(new() { Value = value }).ConfigureAwait(false);
            if (alias == "CaptureSatelliteLayer") await cut.Find("#node-options-3-outputVariant").ChangeAsync(new() { Value = value }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
            await cut.Find(selector).ChangeAsync(new() { Value = invalid }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
            await cut.Find(selector).ChangeAsync(new() { Value = value }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
            await cut.Find("button.btn-primary").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, registry.ActiveRevisionId);
            var saved = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, saved.Lifecycle);
            cut.Dispose();
            navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + saved.RevisionId);
            var reload = context.Render<ProcessingGraphEditorPage>();
            reload.WaitForAssertion(() => Assert.AreEqual(value, reload.Find(selector).GetAttribute("value")));
            await reload.Find("form").SubmitAsync().ConfigureAwait(false);
            Assert.IsTrue(reload.Find("button.btn-primary").HasAttribute("disabled"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NodePanelAuthenticationStateProvider(ClaimsPrincipal owner) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(owner));
    }
}
