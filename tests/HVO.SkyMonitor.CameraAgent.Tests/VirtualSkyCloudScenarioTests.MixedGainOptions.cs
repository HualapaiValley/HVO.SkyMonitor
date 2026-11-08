using System.Security.Claims;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
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
    public async Task MixedGainPanelUsesVirtualSkyRealRevisionSaveRestartActivationAndRollback()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-virtual-mixed-options");
        try
        {
            string sourceId;
            string savedId;
            string original;
            using (var provider = ProcessingGraphOperationsTests.CreateProvider(root))
            {
                var config = Config(CameraPixelFormat.Mono16, null) with
                {
                    AgentId = "virtual-mixed-options",
                    Pipeline = new([new("ProjectedScene", "scene", 5, DependsOn: ["$raw"]), new("RollingCombination", "mixed", 10,
                        JsonSerializer.SerializeToElement(new { recipe = "mixed-gain-normalized-mean", windowSize = 7,
                            outputVariant = "mixed-retained", future = new { keep = 17 } }), ["$raw"])])
                };
                var module = Module();
                await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
                var capture = await module.CaptureAsync(new(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(capture.Frame);
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
                sourceId = initial.ActiveRevisionId;
                original = JsonSerializer.Serialize(await operations.ReadRevisionPipelineAsync(sourceId, CancellationToken.None).ConfigureAwait(false));
                using var context = new BunitContext();
                context.JSInterop.Mode = JSRuntimeMode.Loose;
                var authorization = context.AddAuthorization();
                authorization.SetAuthorized("owner");
                authorization.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
                var owner = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "mixed-options-owner"),
                        new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)],
                    Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));
                Assert.AreEqual("mixed-options-owner", CameraAgentCredentialAccess.GetOwnerId(owner));
                context.Services.AddSingleton<AuthenticationStateProvider>(new MixedOptionsAuthenticationStateProvider(owner));
                var accessor = new CameraAgentConfigurationAccessor();
                accessor.SetConfiguration(config);
                context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(services => new CameraAgentProcessingGraphUiService(
                    services.GetRequiredService<AuthenticationStateProvider>(), services.GetRequiredService<IAuthorizationService>(),
                    operations, provider.GetRequiredService<ICaptureProcessingPipelineFactory>(), accessor, null, TimeProvider.System,
                    NullLogger<CameraAgentProcessingGraphUiService>.Instance));
                var navigation = context.Services.GetRequiredService<NavigationManager>();
                navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + sourceId);
                var cut = context.Render<ProcessingGraphEditorPage>();
                const string selector = "#node-options-1-windowSize";
                await cut.WaitForElement(selector).ChangeAsync("8").ConfigureAwait(false);
                await cut.Find("form").SubmitAsync().ConfigureAwait(false);
                Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
                await cut.Find(selector).ChangeAsync("0").ConfigureAwait(false);
                await cut.Find("form").SubmitAsync().ConfigureAwait(false);
                Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
                Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
                await cut.Find(selector).ChangeAsync("8").ConfigureAwait(false);
                await cut.Find("form").SubmitAsync().ConfigureAwait(false);
                await cut.Find("button.btn-primary").ClickAsync().ConfigureAwait(false);
                cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));
                var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(sourceId, registry.ActiveRevisionId);
                var saved = registry.Revisions.Single(revision => revision.RevisionId != sourceId);
                savedId = saved.RevisionId;
                cut.Dispose();
                navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + savedId);
                var reload = context.Render<ProcessingGraphEditorPage>();
                reload.WaitForAssertion(() => Assert.AreEqual("8", reload.Find(selector).GetAttribute("value")));
                await reload.Find("form").SubmitAsync().ConfigureAwait(false);
                Assert.IsTrue(reload.Find("button.btn-primary").HasAttribute("disabled"));
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.ActivateRevisionAsync(savedId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await operations.ValidateRevisionAsync(savedId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
                var active = await operations.ActivateRevisionAsync(savedId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
                await operations.RollbackRevisionAsync(sourceId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            }
            using var restarted = ProcessingGraphOperationsTests.CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var reopened = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.AreEqual(original, JsonSerializer.Serialize(await reopened.ReadRevisionPipelineAsync(sourceId, CancellationToken.None).ConfigureAwait(false)));
            var pipeline = await reopened.ReadRevisionPipelineAsync(savedId, CancellationToken.None).ConfigureAwait(false);
            var form = ProcessingGraphDraftModel.FromPipeline(pipeline!, savedId, "mixed", "3").Nodes[1].OptionsForm;
            Assert.IsNotNull(form);
            Assert.AreEqual("8", form.Get("windowSize"));
            Assert.AreEqual("mixed-gain-normalized-mean", pipeline!.Steps[1].Options!.Value.GetProperty("recipe").GetString());
            Assert.AreEqual(sourceId, (await reopened.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MixedOptionsAuthenticationStateProvider(ClaimsPrincipal owner) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(owner));
    }
}
