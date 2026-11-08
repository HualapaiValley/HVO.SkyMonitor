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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    private static readonly string[] InitialVirtualAnnotationTokens = ["utc"];

    [TestMethod]
    public async Task AnnotationSelectionVirtualPageEditsRejectsSavesAndReloadsThroughTheRealGraphService()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-virtual-annotation-selections");
        try
        {
            using var provider = ProcessingGraphOperationsTests.CreateProvider(root);
            var config = Config(CameraPixelFormat.Mono16, null) with
            {
                AgentId = "virtual-annotation-selections",
                Pipeline = new([
                    new("ProjectedScene", "scene", 10, DependsOn: ["$raw"]),
                    new("Preview", "preview", 20, JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" }), ["$raw"]),
                    new("Annotation", "annotation", 30, JsonSerializer.SerializeToElement(new { drawMetadataCorners = true, topLeftTokens = InitialVirtualAnnotationTokens }), ["scene", "preview"])
                ])
            };
            var module = Module();
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(new(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(capture.Frame);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var authorization = context.AddAuthorization();
            authorization.SetAuthorized("owner");
            authorization.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "annotation-owner"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)],
                Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));
            Assert.AreEqual("annotation-owner", CameraAgentCredentialAccess.GetOwnerId(owner));
            context.Services.AddSingleton<AuthenticationStateProvider>(new AnnotationSelectionAuthenticationStateProvider(owner));
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(config);
            context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(services => new CameraAgentProcessingGraphUiService(
                services.GetRequiredService<AuthenticationStateProvider>(), services.GetRequiredService<IAuthorizationService>(),
                operations, provider.GetRequiredService<ICaptureProcessingPipelineFactory>(), accessor, null, TimeProvider.System,
                NullLogger<CameraAgentProcessingGraphUiService>.Instance));
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + initial.ActiveRevisionId);
            var cut = context.Render<ProcessingGraphEditorPage>();
            const string corner = "#node-options-2-topLeftTokens";
            await cut.WaitForElement(corner + "-0").ChangeAsync("catalog").ConfigureAwait(false);
            await cut.Find(corner + "-add").ClickAsync().ConfigureAwait(false);
            await cut.Find(corner + "-1").ChangeAsync("catalog").ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
            await cut.Find(corner + "-1").ChangeAsync("utc").ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
            await cut.Find("button.btn-primary").ClickAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, registry.ActiveRevisionId);
            var saved = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            cut.Dispose();
            navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + saved.RevisionId);
            var restored = context.Render<ProcessingGraphEditorPage>();
            restored.WaitForAssertion(() => Assert.AreEqual("catalog", restored.Find(corner + "-0").GetAttribute("value")));
            Assert.AreEqual("utc", restored.Find(corner + "-1").GetAttribute("value"));
            await restored.Find("form").SubmitAsync().ConfigureAwait(false);
            Assert.IsTrue(restored.Find("button.btn-primary").HasAttribute("disabled"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class AnnotationSelectionAuthenticationStateProvider(ClaimsPrincipal owner) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(owner));
    }
}
