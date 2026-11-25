using System.Security.Claims;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Ui")]
public sealed class ProcessingGraphEditorPageTests
{
    private static readonly string[] RawOnly = ["$raw"];
    private static readonly string[] ReorderedIds = ["telemetry", "preview"];

    [TestMethod]
    public void EmptyDraft_AddsNodesPreviewsAndSavesWithARetainedKey()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService();
        CapturePipelineConfig? previewed = null;
        service.Preview = pipeline =>
        {
            previewed = pipeline;
            var nodes = pipeline.Steps.Select(static step => new CaptureProcessingPlanNode(step.Id!, step.Type, step.Enabled ?? true, step.Required, step.Order, null, step.DependsOn, null, null, null)).ToArray();
            return OperatorUiResult<CaptureProcessingPlanPreview>.Success(new CaptureProcessingPlanPreview(pipeline.SchemaVersion, pipeline.DependencyPolicy, new string('D', 64), new string('E', 64), nodes, nodes));
        };
        var attempts = 0;
        service.Create = command => ++attempts == 1
            ? OperatorUiResult<ProcessingGraphRevisionState>.Failure(OperatorUiResultKind.Unavailable, "Response was unavailable.")
            : OperatorUiResult<ProcessingGraphRevisionState>.Success(new ProcessingGraphRevisionState("rev-new", command.Name, command.Revision, ProcessingGraphRevisionLifecycle.Draft, new string('N', 64), new string('S', 64), new string('L', 64), DateTimeOffset.UtcNow, null, null, null));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.WaitForElement(".editor-form");

        cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Graph name", StringComparison.Ordinal)).QuerySelector("input")!.Change("night");
        cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Revision label", StringComparison.Ordinal)).QuerySelector("input")!.Change("1");
        cut.Find("select[aria-label='New node type']").Change("preview");
        cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node").Click();
        cut.Find("select[aria-label='New node type']").Change("telemetry");
        cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node").Click();
        Assert.HasCount(2, cut.FindAll(".node-row"));
        // The second node may depend on the first: tick "preview" in its inputs.
        var telemetryInputs = cut.FindAll(".node-row")[1].QuerySelectorAll(".node-row__dependencies input");
        Assert.HasCount(2, telemetryInputs);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("svg[role='group']")));
        Assert.HasCount(2, previewed!.Steps);
        Assert.AreEqual("preview", previewed.Steps[0].Id);
        CollectionAssert.AreEqual(RawOnly, previewed.Steps[1].DependsOn!.ToArray());
        Assert.IsNotNull(cut.Find("svg title"));
        Assert.IsTrue(cut.Find("svg desc").TextContent.Contains("preview", StringComparison.Ordinal));
        Assert.HasCount(2, cut.FindAll("svg g[role='button'][tabindex='0']"));
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));

        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Response was unavailable.", StringComparison.Ordinal)));
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("saved as an immutable revision", StringComparison.Ordinal)));
        Assert.HasCount(2, service.Created);
        Assert.AreEqual(service.Created[0].Key, service.Created[1].Key);
        Assert.AreEqual(("night", "1"), (service.Created[1].Name, service.Created[1].Revision));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/rev-new']"));
    }

    [TestMethod]
    public void DraftFromRevision_LoadsNodesAndReordersFromTheList()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            RevisionDetail = new CameraAgentProcessingGraphRevisionDetail(
                ProcessingGraphPagesTests.Registry().Revisions[1],
                new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig("Preview", "preview", 10, null, ["$raw"], Enabled: true),
                    new CaptureProcessingStepConfig("Telemetry", "telemetry", 20, null, ["preview"], Required: false, Enabled: true)
                ]),
                null,
                null)
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/operations/pipeline/graphs/new?from=rev-validated");

        var cut = context.Render<ProcessingGraphEditorPage>();

        cut.WaitForElement(".node-row");
        Assert.IsTrue(cut.Markup.Contains("Started from revision", StringComparison.Ordinal));
        Assert.AreEqual("night", cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Graph name", StringComparison.Ordinal)).QuerySelector("input")!.GetAttribute("value"));
        Assert.AreEqual("3", cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Revision label", StringComparison.Ordinal)).QuerySelector("input")!.GetAttribute("value"));
        Assert.IsTrue(cut.Find("button[aria-label='Move preview up']").HasAttribute("disabled"));

        cut.Find("button[aria-label='Move telemetry up']").Click();

        var ids = cut.FindAll(".node-row input[aria-label='Node identifier']").Select(static input => input.GetAttribute("value")).ToArray();
        CollectionAssert.AreEqual(ReorderedIds, ids);
        // telemetry now sits first, so it can no longer name preview as an input.
        Assert.HasCount(1, cut.FindAll(".node-row")[0].QuerySelectorAll(".node-row__dependencies input"));
    }

    [TestMethod]
    public void InvalidDraft_ReportsProblemsWithoutCallingTheService()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService();
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.WaitForElement(".editor-form");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".editor-banner[role='alert']").TextContent, "The graph needs a name.", StringComparison.Ordinal));
        Assert.IsEmpty(service.Created);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Diagram_DescribesEveryNodeAndFocusesRowsByKeyboard()
    {
        using var context = new BunitContext();
        var nodes = new[]
        {
            new CaptureProcessingPlanNode("preview", "Preview", true, true, 10, null, ["$raw"], null, null, null),
            new CaptureProcessingPlanNode("telemetry", "Telemetry", false, false, 20, null, ["preview"], null, null, null)
        };
        string? selected = null;

        var cut = context.Render<GraphDependencyDiagram>(parameters => parameters
            .Add(static diagram => diagram.Nodes, nodes)
            .Add(static diagram => diagram.NodeSelected, EventCallback.Factory.Create<string>(this, id => selected = id)));

        var svg = cut.Find("svg");
        // A group keeps the focusable node buttons in the accessibility tree; an img role would prune them.
        Assert.AreEqual("group", svg.GetAttribute("role"));
        StringAssert.Contains(cut.Find("svg desc").TextContent, "telemetry depends on preview.", StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll("path.diagram-edge"));
        Assert.IsNotNull(cut.Find("g.diagram-node--disabled"));
        cut.FindAll("g[role='button']")[1].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.AreEqual("telemetry", selected);
    }

    [TestMethod]
    public async Task GraphEditorE2E_UntouchedOverlayManifestWithQuotedZOrderSurvivesUnrelatedJpegEditThroughPreviewAndSave()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-editor-quoted-zorder");
        try
        {
            const string manifestJson = """{ "outputVariant": "overlay-manifest-v1", "basePreviewVariant": "combined-preview", "layers": [ { "kind": "scene-constellations", "enabled": false, "zOrder": "12", "future": 1e+02 } ] }""";
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var pipeline = new CapturePipelineConfig(
            [
                new("Calibration", "calibration", 10, null, ["$raw"], Enabled: true),
                new("RollingCombination", "rolling", 20, null, ["calibration"], Enabled: true),
                new("CombinedPreview", "preview", 30, null, ["rolling"], Enabled: true),
                new("ProjectedScene", "scene", 40, null, ["$raw"], Enabled: true),
                new("ScenePresentationLayer", "scene-layer", 50, null, ["scene"], Enabled: true),
                new("EnvironmentPresentationLayer", "env-layer", 60,
                    JsonDocument.Parse("""{ "widthPixels": 1936, "heightPixels": 1216 }""").RootElement.Clone(), ["scene", "preview"], Enabled: true),
                new("OverlayManifest", "manifest", 70,
                    JsonDocument.Parse(manifestJson).RootElement.Clone(), ["preview", "scene-layer", "env-layer"], Enabled: true),
                new("PresentationMaterializer", "materializer", 75,
                    null, ["preview", "manifest", "scene-layer", "env-layer"], Enabled: true),
                new("JpegEncoding", "jpeg", 80,
                    JsonDocument.Parse("""{ "jpegQuality": 85 }""").RootElement.Clone(), ["materializer"], Enabled: true)
            ]);
            var profile = SchedulePageTests.Profile();
            var config = new CameraModuleConfig(
                new ObservatoryLocation(35.0, -114.0, 1000, "UTC"),
                profile.Module,
                profile.Rig,
                pipeline,
                AgentId: "editor-quoted-zorder-e2e")
            {
                Schedule = profile.Schedule
            };
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var storedInitialPipeline = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            var storedInitialManifestRaw = storedInitialPipeline!.Steps.Single(static step => step.Id == "manifest").Options!.Value.GetRawText();
            StringAssert.Contains(storedInitialManifestRaw, "\"zOrder\":\"12\"", StringComparison.Ordinal);
            StringAssert.Contains(storedInitialManifestRaw, "\"future\":1e+02", StringComparison.Ordinal);

            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var auth = context.AddAuthorization();
            auth.SetAuthorized("owner");
            auth.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "editor-owner"),
                 new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)],
                Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));
            context.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthProvider(owner));
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(config);
            context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(services => new CameraAgentProcessingGraphUiService(
                services.GetRequiredService<AuthenticationStateProvider>(),
                services.GetRequiredService<IAuthorizationService>(),
                operations,
                provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
                accessor,
                null,
                TimeProvider.System,
                NullLogger<CameraAgentProcessingGraphUiService>.Instance));

            var navigation = context.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo($"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(initial.ActiveRevisionId)}");
            var cut = context.Render<ProcessingGraphEditorPage>();

            var quality = cut.WaitForElement(".node-row[data-node-alias='JpegEncoding'] input[id$='-jpegQuality']");
            Assert.AreEqual("85", quality.GetAttribute("value"));
            await quality.ChangeAsync(new ChangeEventArgs { Value = "92" }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            cut.WaitForElement(".editor-preview .plan-view");
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".editor-actions .btn-primary").HasAttribute("disabled")));
            await cut.Find(".editor-actions .btn-primary").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));

            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            var draft = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            var savedPipeline = await operations.ReadRevisionPipelineAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(savedPipeline);
            Assert.AreEqual(92, savedPipeline.Steps.Single(static step => step.Id == "jpeg").Options!.Value.GetProperty("jpegQuality").GetInt32());
            Assert.AreEqual(storedInitialManifestRaw, savedPipeline.Steps.Single(static step => step.Id == "manifest").Options!.Value.GetRawText(),
                "Graph editor preview and save must pass sourceRevisionId so an untouched OverlayManifest with quoted zOrder survives byte-for-byte.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("+6")]
    [DataRow("+2.5")]
    public async Task GraphEditorE2E_UntouchedSceneLayerWithLeadingPlusQuotedDecimalSurvivesUnrelatedJpegEditThroughPreviewAndSave(string quotedMagnitude)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-editor-plus-decimal");
        try
        {
            var sceneLayerJson = $$"""{ "maximumLabelMagnitude": "{{quotedMagnitude}}", "future": 1e+02 }""";
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var pipeline = new CapturePipelineConfig(
            [
                new("Calibration", "calibration", 10, null, ["$raw"], Enabled: true),
                new("RollingCombination", "rolling", 20, null, ["calibration"], Enabled: true),
                new("CombinedPreview", "preview", 30, null, ["rolling"], Enabled: true),
                new("ProjectedScene", "scene", 40, null, ["$raw"], Enabled: true),
                new("ScenePresentationLayer", "scene-layer", 50,
                    JsonDocument.Parse(sceneLayerJson).RootElement.Clone(), ["scene"], Enabled: true),
                new("EnvironmentPresentationLayer", "env-layer", 60,
                    JsonDocument.Parse("""{ "widthPixels": 1936, "heightPixels": 1216 }""").RootElement.Clone(), ["scene", "preview"], Enabled: true),
                new("OverlayManifest", "manifest", 70,
                    null, ["preview", "scene-layer", "env-layer"], Enabled: true),
                new("PresentationMaterializer", "materializer", 75,
                    null, ["preview", "manifest", "scene-layer", "env-layer"], Enabled: true),
                new("JpegEncoding", "jpeg", 80,
                    JsonDocument.Parse("""{ "jpegQuality": 85 }""").RootElement.Clone(), ["materializer"], Enabled: true)
            ]);
            var profile = SchedulePageTests.Profile();
            var config = new CameraModuleConfig(
                new ObservatoryLocation(35.0, -114.0, 1000, "UTC"),
                profile.Module,
                profile.Rig,
                pipeline,
                AgentId: "editor-plus-decimal-e2e")
            {
                Schedule = profile.Schedule
            };
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var storedInitialPipeline = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            var storedInitialSceneOptions = storedInitialPipeline!.Steps.Single(static step => step.Id == "scene-layer").Options!.Value;
            var storedInitialSceneRaw = storedInitialSceneOptions.GetRawText();
            var storedMagnitude = storedInitialSceneOptions.GetProperty("maximumLabelMagnitude");
            Assert.AreEqual(JsonValueKind.String, storedMagnitude.ValueKind);
            Assert.AreEqual(quotedMagnitude, storedMagnitude.GetString());
            StringAssert.Contains(storedInitialSceneRaw, "\"future\":1e+02", StringComparison.Ordinal);

            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var auth = context.AddAuthorization();
            auth.SetAuthorized("owner");
            auth.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "editor-owner"),
                 new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)],
                Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));
            context.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthProvider(owner));
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(config);
            context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(services => new CameraAgentProcessingGraphUiService(
                services.GetRequiredService<AuthenticationStateProvider>(),
                services.GetRequiredService<IAuthorizationService>(),
                operations,
                provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
                accessor,
                null,
                TimeProvider.System,
                NullLogger<CameraAgentProcessingGraphUiService>.Instance));

            var navigation = context.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo($"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(initial.ActiveRevisionId)}");
            var cut = context.Render<ProcessingGraphEditorPage>();

            var quality = cut.WaitForElement(".node-row[data-node-alias='JpegEncoding'] input[id$='-jpegQuality']");
            Assert.AreEqual("85", quality.GetAttribute("value"));
            await quality.ChangeAsync(new ChangeEventArgs { Value = "92" }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            cut.WaitForElement(".editor-preview .plan-view");
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".editor-actions .btn-primary").HasAttribute("disabled")));
            await cut.Find(".editor-actions .btn-primary").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));

            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            var draft = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            var savedPipeline = await operations.ReadRevisionPipelineAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(savedPipeline);
            Assert.AreEqual(92, savedPipeline.Steps.Single(static step => step.Id == "jpeg").Options!.Value.GetProperty("jpegQuality").GetInt32());
            Assert.AreEqual(storedInitialSceneRaw, savedPipeline.Steps.Single(static step => step.Id == "scene-layer").Options!.Value.GetRawText(),
                "Graph editor preview and save must preserve an untouched ScenePresentationLayer step with leading-'+' quoted decimal options byte-for-byte.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubAuthProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
