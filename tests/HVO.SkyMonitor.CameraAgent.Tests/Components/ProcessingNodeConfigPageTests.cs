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
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Ui")]
public sealed class ProcessingNodeConfigPageTests
{
    [TestMethod]
    public void DedicatedPageEditsColorFontGlyphAndSavesNewImmutableRevision()
    {
        using var context = new BunitContext();
        const string sourceJson = """{ "labelFont": "MonoBold", "future": [1e+02] }""";
        var pipeline = new CapturePipelineConfig(
        [
            new("ProjectedScene", "scene-query", 10, null, ["$raw"], Enabled: true),
            new(SceneStylePresentation.StepAlias, "scene-style", 20,
                JsonDocument.Parse(sourceJson).RootElement.Clone(), ["scene-query"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "scene-style"));

        cut.WaitForElement(".config-form");
        StringAssert.Contains(cut.Find("h1").TextContent, "Scene overlay style", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, SceneStylePresentation.Descriptor.DescriptorId, StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, SceneStylePresentation.ProducerVersion, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("#node-options-1-constellationLineColor-help").TextContent, "Default: #60a0ff.", StringComparison.Ordinal);
        Assert.HasCount(2, cut.FindAll(".node-switcher__item"));
        Assert.AreEqual("page", cut.Find(".node-switcher__item--current").GetAttribute("aria-current"));

        // Edit color, font, glyph, and whole number.
        cut.Find("#node-options-1-constellationLineColor").Change("#11aaee");
        cut.Find("#node-options-1-labelFont").Change("PlexRegular");
        cut.Find("#node-options-1-pointGlyph").Change("Crosshair");
        cut.Find("#node-options-1-constellationLineThickness").Change("4");

        // Invalid input blocks preview and save.
        cut.Find("#node-options-1-constellationLineThickness").Change("99");
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);

        // Fix invalid value, preview, and save.
        cut.Find("#node-options-1-constellationLineThickness").Change("4");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();

        var created = service.Created.Single();
        Assert.AreEqual("2", created.Revision);
        Assert.AreEqual(sourceJson, service.RevisionDetail!.Pipeline.Steps[1].Options!.Value.GetRawText(),
            "Source revision must never be mutated in place.");
        var savedOptions = created.Pipeline.Steps[1].Options!.Value;
        Assert.AreEqual(17, savedOptions.GetProperty("constellationLineRed").GetInt32());
        Assert.AreEqual(170, savedOptions.GetProperty("constellationLineGreen").GetInt32());
        Assert.AreEqual(238, savedOptions.GetProperty("constellationLineBlue").GetInt32());
        Assert.AreEqual("PlexRegular", savedOptions.GetProperty("labelFont").GetString());
        Assert.AreEqual("Crosshair", savedOptions.GetProperty("pointGlyph").GetString());
        Assert.AreEqual(4, savedOptions.GetProperty("constellationLineThickness").GetInt32());
        Assert.AreEqual("[1e+02]", savedOptions.GetProperty("future").GetRawText());
        Assert.IsEmpty(service.Commands, "Saving must not validate or activate implicitly.");
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/rev-new']"));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs']"));
    }

    [TestMethod]
    public void DedicatedPageResetToCodeDefaultsRestoresOverlayDefaultsLosslessly()
    {
        using var context = new BunitContext();
        const string sourceJson = """{ "constellationLineThickness": 7, "future": { "keep": 1 } }""";
        var pipeline = new CapturePipelineConfig(
        [
            new("ScenePresentationLayer", "scene", 10,
                JsonDocument.Parse(sourceJson).RootElement.Clone(), ["$raw"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "scene"));

        cut.WaitForElement("#node-options-0-reset-defaults");
        Assert.IsFalse(cut.Find("#node-options-0-reset-defaults").HasAttribute("disabled"));
        Assert.AreEqual("7", cut.Find("#node-options-0-constellationLineThickness").GetAttribute("value"));

        cut.Find("#node-options-0-reset-defaults").Click();
        Assert.AreEqual("2", cut.Find("#node-options-0-constellationLineThickness").GetAttribute("value"));
        Assert.IsTrue(cut.Find("#node-options-0-reset-defaults").HasAttribute("disabled"));

        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();

        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(2, saved.GetProperty("constellationLineThickness").GetInt32());
        Assert.AreEqual("{ \"keep\": 1 }", saved.GetProperty("future").GetRawText());
    }

    [TestMethod]
    [DataRow(ExposureMeasurementProducts.StepAlias)]
    [DataRow(ExposureCatalogPredictionProducts.StepAlias)]
    public void BuiltInCenteredMeasurementStageIsReadOnlyInspectionWithNoEnableSwitch(string alias)
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("Calibration", "linear", 2, null, ["$raw"], Required: false, Enabled: true),
            new(alias, alias, 4, null, ["$raw", "linear"], Required: false, Enabled: true)
        ]);
        Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, alias));

        cut.WaitForElement(".config-card--builtin");
        var card = cut.Find(".config-card--builtin");
        StringAssert.Contains(card.TextContent, "Built-in mandatory stage", StringComparison.Ordinal);
        StringAssert.Contains(card.TextContent, "raw capture", StringComparison.Ordinal);
        StringAssert.Contains(card.TextContent, "linear", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("input, select, button"), "Built-in measurement stages must never offer an enable switch or edit controls.");
    }

    [TestMethod]
    public void ActiveRevisionConvenienceRouteLoadsActiveRevisionAndResolvesNode()
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("RollingCombination", "rolling", 25, null, ["$raw"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.NodeId, "rolling"));

        cut.WaitForElement("#node-options-0-windowSize");
        Assert.AreEqual("5", cut.Find("#node-options-0-windowSize").GetAttribute("value"));
        cut.Find("#node-options-0-windowSize").Change("9");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();

        var created = service.Created.Single();
        Assert.AreEqual(9, created.Pipeline.Steps[0].Options!.Value.GetProperty("windowSize").GetInt32());
    }

    [TestMethod]
    public void DedicatedPageRendersFrameFactsAndOpaqueFallbackAndMissingNodeAndUnauthorized()
    {
        using (var context = new BunitContext())
        {
            var pipeline = new CapturePipelineConfig(
            [
                new(FrameFactsPresentation.StepAlias, "facts", 72, null, ["$raw"], Enabled: true),
                new("UnknownCustomStep", "custom", 90, JsonDocument.Parse("""{"opaque":true}""").RootElement.Clone(), ["$raw"], Enabled: true)
            ]);
            Configure(context, pipeline);

            var facts = context.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, "facts"));
            facts.WaitForElement(".frame-facts-editor");
            facts.Dispose();

            var opaque = context.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, "custom"));
            opaque.WaitForElement("#opaque-stage-heading");
            StringAssert.Contains(opaque.Markup, "preserved byte-for-byte", StringComparison.Ordinal);
            Assert.IsEmpty(opaque.FindAll("form"));
            opaque.Dispose();

            var missing = context.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, "does-not-exist"));
            missing.WaitForElement(".missing-node-list");
            Assert.HasCount(2, missing.FindAll(".missing-node-list li"));
        }

        using (var deniedContext = new BunitContext())
        {
            var deniedService = Configure(deniedContext, CapturePipelineConfig.Empty);
            deniedService.RevisionDetailFailure = OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>.Failure(
                OperatorUiResultKind.Unauthorized, "Denied");
            _ = deniedContext.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, "any"));
            StringAssert.EndsWith(deniedContext.Services.GetRequiredService<NavigationManager>().Uri,
                "/Account/AccessDenied", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("rolling-combination", "RollingCombination")]
    [DataRow("image-quality", "ImageQuality")]
    [DataRow("capture-telemetry", "Telemetry")]
    [DataRow("simulated-cloud-observations", "VirtualSkyCloudObservation")]
    [DataRow("astrometry-prediction", "AstrometryPrediction")]
    [DataRow("measured-stellar-associations", "MeasuredStellarAssociations")]
    [DataRow("measured-astrometry", "MeasuredAstrometry")]
    [DataRow("exposure-stellar-associations", ExposureAssociationProducts.StepAlias)]
    [DataRow("exposure-measured-astrometry", CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias)]
    [DataRow("capture-satellite-tracks", "CaptureSatelliteTracks")]
    [DataRow("artifact-storage", "Storage")]
    [DataRow("presentation-materializer", "PresentationMaterializer")]
    [DataRow("preview", "Preview")]
    [DataRow("calibrated-preview", "CalibratedPreview")]
    [DataRow("combined-preview", "CombinedPreview")]
    [DataRow("jpeg-encoding", "JpegEncoding")]
    public void EditorSectionDeepLinkLoadsActiveRevisionAndMatchesUniqueSectionOrMissingNotice(string section, string alias)
    {
        using var presentContext = new BunitContext();
        Configure(presentContext, new CapturePipelineConfig([new(alias, "node", 10, null, ["$raw"], Enabled: true)]));
        presentContext.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/operations/pipeline/graphs/new?section={section}#{section}");

        var present = presentContext.Render<ProcessingGraphEditorPage>();
        present.WaitForElement("#" + section);
        Assert.IsNotNull(present.Find("#" + section));
        Assert.IsEmpty(present.FindAll("#constellations"), $"Section '{section}' must not collide with #constellations.");
        Assert.IsNotNull(present.Find("a[href='/operations/pipeline/graphs/rev-active/nodes/node']"));
        present.Dispose();

        using var absentContext = new BunitContext();
        Configure(absentContext, new CapturePipelineConfig([new("Telemetry", "other", 10, null, ["$raw"], Enabled: true)]));
        if (alias == "Telemetry")
        {
            Configure(absentContext, new CapturePipelineConfig([new("Preview", "other", 10, null, ["$raw"], Enabled: true)]));
        }
        absentContext.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/operations/pipeline/graphs/new?section={section}#{section}");

        var absent = absentContext.Render<ProcessingGraphEditorPage>();
        absent.WaitForElement(".node-row");
        StringAssert.Contains(absent.Find("p[role='status']").TextContent, "no supported", StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailPageLinksEveryNodeToItsDedicatedConfigurationOrInspectionPage()
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("Preview", "preview", 10, null, ["$raw"], Enabled: true),
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true)
        ]);
        Configure(context, pipeline);

        var cut = context.Render<ProcessingGraphDetailPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active"));

        cut.WaitForElement(".definition-list");
        var previewLink = cut.Find("a[href='/operations/pipeline/graphs/rev-active/nodes/preview']");
        Assert.AreEqual("Configure", previewLink.TextContent.Trim());
        var measurementLink = cut.Find($"a[href='/operations/pipeline/graphs/rev-active/nodes/{ExposureMeasurementProducts.StepAlias}']");
        Assert.AreEqual("Inspect built-in stage", measurementLink.TextContent.Trim());
        StringAssert.Contains(cut.Markup, "Preview (encoded-preview-v1)", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "built-in fixed settings", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DedicatedPageEndToEndCreatesValidatesActivatesAndRollsBackRevision()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-node-config-e2e");
        try
        {
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var pipeline = ProcessingGraphOperationsTests.RemainingOptionsPipeline(root);
            var profile = SchedulePageTests.Profile();
            var config = new CameraModuleConfig(
                new ObservatoryLocation(35.0, -114.0, 1000, "UTC"),
                profile.Module,
                profile.Rig,
                pipeline,
                AgentId: "node-config-e2e")
            {
                Schedule = profile.Schedule
            };
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);

            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var auth = context.AddAuthorization();
            auth.SetAuthorized("owner");
            auth.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "config-owner"),
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

            var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, initial.ActiveRevisionId)
                .Add(static page => page.NodeId, "astrometry"));

            var stride = cut.WaitForElement("#node-options-2-captureStride");
            Assert.AreEqual("60", stride.GetAttribute("value"));
            await stride.ChangeAsync(new() { Value = "180" }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
            await cut.Find("button.btn-primary").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));

            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, registry.ActiveRevisionId, "Saving a draft must not activate it.");
            var draft = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);

            var validated = await operations.ValidateRevisionAsync(draft.RevisionId, "val-1", "config-owner", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, validated.Lifecycle);

            var activated = await operations.ActivateRevisionAsync(draft.RevisionId, registry.StateVersion, "act-1", "config-owner", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, activated.ActiveRevisionId);
            var activePipeline = await operations.ReadRevisionPipelineAsync(activated.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            var astrometryOptions = activePipeline!.Steps.Single(static step => step.Id == "astrometry").Options!.Value;
            var decoded = ProcessingNodeOptionsCodec.Decode(CaptureAnalysisOptionDescriptors.Astrometry, astrometryOptions);
            Assert.IsTrue(decoded.IsValid, string.Join(' ', decoded.Diagnostics));
            Assert.AreEqual(180, decoded.Get("captureStride").Value.AsWholeNumber());

            var rolledBack = await operations.RollbackRevisionAsync(initial.ActiveRevisionId, activated.StateVersion, "rb-1", "config-owner", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, rolledBack.ActiveRevisionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DelayedPreviewFromRevisionACannotEnableOrExecuteSaveOnRevisionB()
    {
        using var context = new BunitContext();
        var pipelineA = new CapturePipelineConfig(
        [
            new(SceneStylePresentation.StepAlias, "scene-style", 10,
                JsonDocument.Parse("""{ "constellationLineThickness": 2 }""").RootElement.Clone(), ["$raw"], Enabled: true)
        ]);
        var pipelineB = new CapturePipelineConfig(
        [
            new(SceneStylePresentation.StepAlias, "scene-style", 10,
                JsonDocument.Parse("""{ "constellationLineThickness": 5 }""").RootElement.Clone(), ["$raw"], Enabled: true)
        ]);
        var detailA = new CameraAgentProcessingGraphRevisionDetail(
            new("rev-a", "graph-a", "1", ProcessingGraphRevisionLifecycle.Active,
                new string('1', 64), new string('2', 64), new string('3', 64), DateTimeOffset.UtcNow, null, null, null),
            pipelineA, null, null);
        var detailB = new CameraAgentProcessingGraphRevisionDetail(
            new("rev-b", "graph-b", "7", ProcessingGraphRevisionLifecycle.Draft,
                new string('4', 64), new string('5', 64), new string('6', 64), DateTimeOffset.UtcNow, null, null, null),
            pipelineB, null, null);

        var delayedPreviewA = new TaskCompletionSource<OperatorUiResult<CaptureProcessingPlanPreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken previewAToken = default;
        var previewCalls = 0;

        var service = Configure(context, pipelineA);
        service.RevisionDetailHandler = (revisionId, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>.Success(revisionId == "rev-a" ? detailA : detailB));
        service.PreviewHandler = (candidate, token) =>
        {
            previewCalls++;
            if (previewCalls == 1)
            {
                previewAToken = token;
                previewAStarted.TrySetResult();
                return new ValueTask<OperatorUiResult<CaptureProcessingPlanPreview>>(delayedPreviewA.Task);
            }
            return ValueTask.FromResult(OperatorUiResult<CaptureProcessingPlanPreview>.Success(
                new(candidate.SchemaVersion, candidate.DependencyPolicy, new string('D', 64), new string('E', 64), [], [])));
        };

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-a")
            .Add(static page => page.NodeId, "scene-style"));
        cut.WaitForElement("#node-options-0-constellationLineThickness");
        Assert.AreEqual("2", cut.Find("#node-options-0-constellationLineThickness").GetAttribute("value"));

        // Edit rev-a and start a preview that stays in flight.
        await cut.Find("#node-options-0-constellationLineThickness").ChangeAsync(new() { Value = "3" }).ConfigureAwait(false);
        var pendingPreviewA = cut.Find("form").TriggerEventAsync("onsubmit", EventArgs.Empty);
        await previewAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(previewAToken.IsCancellationRequested);

        // Navigate to rev-b while rev-a's preview is still pending.
        cut.Render(parameters => parameters
            .Add(static page => page.RevisionId, "rev-b")
            .Add(static page => page.NodeId, "scene-style"));
        cut.WaitForAssertion(() => Assert.AreEqual("5", cut.Find("#node-options-0-constellationLineThickness").GetAttribute("value")));
        Assert.IsTrue(previewAToken.IsCancellationRequested, "Navigating to another revision must cancel in-flight preview work.");

        // Even if the stale preview completes with a valid result ignoring cancellation, it must be discarded.
        delayedPreviewA.SetResult(OperatorUiResult<CaptureProcessingPlanPreview>.Success(
            new(pipelineA.SchemaVersion, pipelineA.DependencyPolicy, new string('A', 64), new string('B', 64), [], [])));
        await pendingPreviewA.ConfigureAwait(false);

        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"),
            "A delayed preview from revision A must never enable Save on revision B.");
        Assert.IsEmpty(cut.FindAll("#config-preview-heading"));
        Assert.IsEmpty(service.Created);

        // Editing, previewing, and saving on revision B submits only revision B's identity and pipeline.
        await cut.Find("#node-options-0-constellationLineThickness").ChangeAsync(new() { Value = "6" }).ConfigureAwait(false);
        await cut.Find("form").SubmitAsync().ConfigureAwait(false);
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        await cut.Find("button.btn-primary").ClickAsync(new()).ConfigureAwait(false);

        var created = service.Created.Single();
        Assert.AreEqual("graph-b", created.Name);
        Assert.AreEqual("8", created.Revision);
        Assert.AreEqual(6, created.Pipeline.Steps[0].Options!.Value.GetProperty("constellationLineThickness").GetInt32());
        StringAssert.Contains(created.Reason, "rev-b", StringComparison.Ordinal);

        // Disposing while a preview is pending cancels the token cleanly.
        var disposeToken = CancellationToken.None;
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposePending = new TaskCompletionSource<OperatorUiResult<CaptureProcessingPlanPreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PreviewHandler = (_, token) =>
        {
            disposeToken = token;
            disposeStarted.TrySetResult();
            return new ValueTask<OperatorUiResult<CaptureProcessingPlanPreview>>(disposePending.Task);
        };
        await cut.Find("#node-options-0-constellationLineThickness").ChangeAsync(new() { Value = "7" }).ConfigureAwait(false);
        var inFlightOnDispose = cut.Find("form").TriggerEventAsync("onsubmit", EventArgs.Empty);
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await context.DisposeComponentsAsync().ConfigureAwait(false);
        Assert.IsTrue(disposeToken.IsCancellationRequested);
        disposePending.SetCanceled(disposeToken);
        await inFlightOnDispose.ConfigureAwait(false);
    }

    [TestMethod]
    public void DotDotAndArbitraryNodeIdsUseQueryParameterLinksAndResolveToConfigPage()
    {
        var dotDotHref = ProcessingNodeConfigPage.NodeConfigHref("rev-active", "..");
        var resolvedDotDot = new Uri(new Uri("https://localhost"), dotDotHref);
        Assert.AreEqual("/operations/pipeline/graphs/rev-active/node", resolvedDotDot.AbsolutePath);
        Assert.AreEqual("?nodeId=..", resolvedDotDot.Query);

        var slashHref = ProcessingNodeConfigPage.NodeConfigHref("rev-active", "a/b?x=1");
        var resolvedSlash = new Uri(new Uri("https://localhost"), slashHref);
        Assert.AreEqual("/operations/pipeline/graphs/rev-active/node", resolvedSlash.AbsolutePath);
        Assert.AreEqual("?nodeId=a%2Fb%3Fx%3D1", resolvedSlash.Query);

        var activeDotDotHref = ProcessingNodeConfigPage.ActiveNodeConfigHref("..");
        var resolvedActiveDotDot = new Uri(new Uri("https://localhost"), activeDotDotHref);
        Assert.AreEqual("/operations/pipeline/configure", resolvedActiveDotDot.AbsolutePath);
        Assert.AreEqual("?nodeId=..", resolvedActiveDotDot.Query);

        // Safe node IDs keep the backward-compatible path helper format.
        Assert.AreEqual("/operations/pipeline/graphs/rev-active/nodes/preview",
            ProcessingNodeConfigPage.NodeConfigHref("rev-active", "preview"));

        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("Preview", "..", 10, null, ["$raw"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        // Detail page renders the query-parameter link for node ID "..".
        var detail = context.Render<ProcessingGraphDetailPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active"));
        detail.WaitForElement(".definition-list");
        var link = detail.Find(".definition-node__link");
        Assert.AreEqual(dotDotHref, link.GetAttribute("href"));
        detail.Dispose();

        // Following that href resolves ProcessingNodeConfigPage via the query parameter.
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(dotDotHref);
        var configPage = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active"));
        configPage.WaitForElement(".config-form");
        StringAssert.Contains(configPage.Find("h1").TextContent, "..", StringComparison.Ordinal);
        configPage.Find("#node-options-0-blackPercentile").Change("0.25");
        configPage.Find("form").Submit();
        Assert.IsFalse(configPage.Find("button.btn-primary").HasAttribute("disabled"));
        configPage.Find("button.btn-primary").Click();
        Assert.AreEqual("..", service.Created.Single().Pipeline.Steps[0].Id);
        Assert.AreEqual(0.25, service.Created.Single().Pipeline.Steps[0].Options!.Value.GetProperty("blackPercentile").GetDouble());
    }

    [TestMethod]
    public void CurrentSkyFrameFactsLinkShowsMissingNoticeOnAnnotationOnlyGraph()
    {
        using var context = new BunitContext();
        Configure(context, new CapturePipelineConfig(
        [
            new("Annotation", "legacy-annot", 10, null, ["$raw"], Enabled: true)
        ]));
        context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/operations/pipeline/graphs/new?section=frame-facts#frame-facts");

        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.WaitForElement("#legacy-annotation");
        Assert.IsEmpty(cut.FindAll("#frame-facts"), "An Annotation-only graph renders no #frame-facts target.");
        Assert.IsTrue(
            cut.FindAll("p[role='status']").Any(static notice =>
                notice.TextContent.Contains("This graph has no Frame Facts node.", StringComparison.Ordinal)),
            "Expected Frame Facts missing-node notice on an Annotation-only graph.");
    }

    [TestMethod]
    public void DedicatedFrameFactsPageShowsDescriptorGuidanceBoundsAndResetInvalidatesBothPreviews()
    {
        using var context = new BunitContext();
        const string sourceJson = """
            {
              "widthPixels": 1936,
              "heightPixels": 1216,
              "inset": 20,
              "lineSpacing": 6,
              "topLeftTemplate": "Seq {{capture.sequence}}",
              "topLeftFont": "MonoBold",
              "topLeftSizeMilliPixels": 24000,
              "topLeftRed": 17,
              "topLeftGreen": 170,
              "topLeftBlue": 238,
              "topLeftMissingPolicy": "DropLine",
              "future": [1e+02]
            }
            """;
        var pipeline = new CapturePipelineConfig(
        [
            new(FrameFactsPresentation.StepAlias, "facts", 72,
                JsonDocument.Parse(sourceJson).RootElement.Clone(), ["$raw"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "facts"));
        cut.WaitForElement(".frame-facts-editor");

        // Descriptor-backed help, bounds, and defaults on corner style fields.
        Assert.AreEqual("node-options-0-topLeftSizeMilliPixels-help",
            cut.Find("#node-options-0-topLeftSizeMilliPixels").GetAttribute("aria-describedby"));
        var sizeHelp = cut.Find("#node-options-0-topLeftSizeMilliPixels-help").TextContent;
        StringAssert.Contains(sizeHelp, "Font size in thousandths of an image pixel.", StringComparison.Ordinal);
        StringAssert.Contains(sizeHelp, "4000–64000 milli-px.", StringComparison.Ordinal);
        StringAssert.Contains(sizeHelp, "Default: 16000 milli-px.", StringComparison.Ordinal);

        Assert.AreEqual("node-options-0-topLeftFont-help",
            cut.Find("#node-options-0-topLeftFont").GetAttribute("aria-describedby"));
        StringAssert.Contains(cut.Find("#node-options-0-topLeftFont-help").TextContent, "Default: MonoRegular.", StringComparison.Ordinal);

        Assert.AreEqual("node-options-0-topLeftColor-help",
            cut.Find("#node-options-0-topLeftColor").GetAttribute("aria-describedby"));
        StringAssert.Contains(cut.Find("#node-options-0-topLeftColor-help").TextContent, "Default: #ffffff.", StringComparison.Ordinal);

        Assert.AreEqual("node-options-0-topLeftMissingPolicy-help",
            cut.Find("#node-options-0-topLeftMissingPolicy").GetAttribute("aria-describedby"));
        StringAssert.Contains(cut.Find("#node-options-0-topLeftMissingPolicy-help").TextContent, "Default: Placeholder.", StringComparison.Ordinal);

        // Produce both the rendered facts preview and the compiled graph preview.
        cut.Find(".frozen-preview button").Click();
        Assert.IsNotNull(cut.Find(".preview-image"));
        cut.Find("#node-options-0-topLeftTemplate").Change("Edited {{capture.sequence}}");
        cut.Find(".frozen-preview button").Click();
        Assert.IsNotNull(cut.Find(".preview-image"));
        cut.Find("form").Submit();
        Assert.IsNotNull(cut.Find("#config-preview-heading"));
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));

        // Reset restores defaults, preserves presentation dimensions & unknown fields, and invalidates both previews.
        var resetButton = cut.Find("#node-options-0-reset-defaults");
        Assert.IsFalse(resetButton.HasAttribute("disabled"));
        resetButton.Click();

        Assert.IsEmpty(cut.FindAll(".preview-image"), "Reset must invalidate the rendered frozen facts preview.");
        Assert.IsEmpty(cut.FindAll("#config-preview-heading"), "Reset must invalidate the compiled graph preview.");
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("#node-options-0-reset-defaults").HasAttribute("disabled"));
        Assert.AreEqual(string.Empty, cut.Find("#node-options-0-topLeftTemplate").GetAttribute("value"));
        Assert.AreEqual("MonoRegular", cut.Find("#node-options-0-topLeftFont").GetAttribute("value"));
        Assert.AreEqual("16000", cut.Find("#node-options-0-topLeftSizeMilliPixels").GetAttribute("value"));
        Assert.AreEqual("#ffffff", cut.Find("#node-options-0-topLeftColor").GetAttribute("value"));
        Assert.AreEqual("Placeholder", cut.Find("#node-options-0-topLeftMissingPolicy").GetAttribute("value"));
        Assert.AreEqual("12", cut.Find("#node-options-0-inset").GetAttribute("value"));
        Assert.AreEqual("2", cut.Find("#node-options-0-lineSpacing").GetAttribute("value"));
        Assert.AreEqual("1936", cut.Find("#node-options-0-widthPixels").GetAttribute("value"));
        Assert.AreEqual("1216", cut.Find("#node-options-0-heightPixels").GetAttribute("value"));

        // Frozen preview still renders after reset, and saving preserves unknown JSON members.
        cut.Find(".frozen-preview button").Click();
        Assert.IsNotNull(cut.Find(".preview-image"));
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();

        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(1936, saved.GetProperty("widthPixels").GetInt32());
        Assert.AreEqual(1216, saved.GetProperty("heightPixels").GetInt32());
        Assert.AreEqual(12, saved.GetProperty("inset").GetInt32());
        Assert.AreEqual(2, saved.GetProperty("lineSpacing").GetInt32());
        Assert.AreEqual(16_000, saved.GetProperty("topLeftSizeMilliPixels").GetInt32());
        Assert.AreEqual("[1e+02]", saved.GetProperty("future").GetRawText());
    }

    [TestMethod]
    public void DedicatedPagesConfigureDeepSkyStyleLabelAndQuerySettingsWithValidationAndLosslessReset()
    {
        using var context = new BunitContext();
        const string sceneSource = """{ "deepSkyMaximumLabels": 10, "future": [1e+02] }""";
        const string querySource = """{ "deepSkyEnabled": true, "future": [1e+02] }""";
        var pipeline = new CapturePipelineConfig(
        [
            new("ProjectedScene", "scene-query", 10, JsonDocument.Parse(querySource).RootElement.Clone(), ["$raw"], Enabled: true),
            new(SceneStylePresentation.StepAlias, "scene-style", 20, JsonDocument.Parse(sceneSource).RootElement.Clone(), ["scene-query"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        // Scene node exposes all deep-sky style and label controls, validates bounds, resets losslessly, and persists.
        var sceneCut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "scene-style"));
        sceneCut.WaitForElement("#node-options-1-deepSkyMaximumLabels");

        sceneCut.Find("#node-options-1-deepSkyMaximumLabels").Change("30");
        sceneCut.Find("form").Submit();
        Assert.IsNotEmpty(sceneCut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(sceneCut.Find("button.btn-primary").HasAttribute("disabled"));

        sceneCut.Find("#node-options-1-applyInlineDeepSkyStyle").Change(true);
        sceneCut.Find("#node-options-1-deepSkyMaximumLabels").Change("8");
        sceneCut.Find("#node-options-1-deepSkyMaximumLabelCharacters").Change("14");
        sceneCut.Find("#node-options-1-deepSkyLabelCatalogObjects").Change(false);
        sceneCut.Find("#node-options-1-deepSkyMinimumGlyphRadius").Change("10");
        sceneCut.Find("#node-options-1-deepSkyLineThickness").Change("4");
        sceneCut.Find("#node-options-1-deepSkyLabelScale").Change("3");
        sceneCut.Find("#node-options-1-deepSkyColor").Change("#40c0ff");
        sceneCut.Find("#node-options-1-deepSkyFont").Change("MonoBold");
        sceneCut.Find("form").Submit();
        Assert.IsFalse(sceneCut.Find("button.btn-primary").HasAttribute("disabled"));
        sceneCut.Find("button.btn-primary").Click();

        var savedScene = service.Created.Single().Pipeline.Steps[1].Options!.Value;
        Assert.IsTrue(savedScene.GetProperty("applyInlineDeepSkyStyle").GetBoolean());
        Assert.AreEqual(8, savedScene.GetProperty("deepSkyMaximumLabels").GetInt32());
        Assert.AreEqual(14, savedScene.GetProperty("deepSkyMaximumLabelCharacters").GetInt32());
        Assert.IsFalse(savedScene.GetProperty("deepSkyLabelCatalogObjects").GetBoolean());
        Assert.AreEqual(10, savedScene.GetProperty("deepSkyMinimumGlyphRadius").GetInt32());
        Assert.AreEqual(4, savedScene.GetProperty("deepSkyLineThickness").GetInt32());
        Assert.AreEqual(3, savedScene.GetProperty("deepSkyLabelScale").GetInt32());
        Assert.AreEqual(64, savedScene.GetProperty("deepSkyRed").GetInt32());
        Assert.AreEqual(192, savedScene.GetProperty("deepSkyGreen").GetInt32());
        Assert.AreEqual(255, savedScene.GetProperty("deepSkyBlue").GetInt32());
        Assert.AreEqual("MonoBold", savedScene.GetProperty("deepSkyFont").GetString());
        Assert.AreEqual("[1e+02]", savedScene.GetProperty("future").GetRawText());

        // Reset restores deep-sky defaults while keeping unknown fields.
        sceneCut.Find("#node-options-1-reset-defaults").Click();
        Assert.AreEqual("24", sceneCut.Find("#node-options-1-deepSkyMaximumLabels").GetAttribute("value"));
        Assert.AreEqual("#ffc478", sceneCut.Find("#node-options-1-deepSkyColor").GetAttribute("value"));
        Assert.AreEqual("MonoRegular", sceneCut.Find("#node-options-1-deepSkyFont").GetAttribute("value"));
        sceneCut.Find("form").Submit();
        sceneCut.Find("button.btn-primary").Click();
        Assert.AreEqual("[1e+02]", service.Created[^1].Pipeline.Steps[1].Options!.Value.GetProperty("future").GetRawText());
        sceneCut.Dispose();

        // ProjectedScene page validates deep-sky min <= max size bounds.
        var queryCut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "scene-query"));
        queryCut.WaitForElement("#node-options-0-deepSkyMinimumSizeArcminutes");
        queryCut.Find("#node-options-0-deepSkyMinimumSizeArcminutes").Change("50");
        queryCut.Find("#node-options-0-deepSkyMaximumSizeArcminutes").Change("10");
        queryCut.Find("form").Submit();
        Assert.IsNotEmpty(queryCut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(queryCut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    [DataRow("ScenePresentationLayer")]
    [DataRow("SceneStylePresentationLayerV1")]
    public void CurrentSkyDeepSkyLinkTargetsAnchorOnPresentGraphAndShowsMissingNoticeWhenAbsent(string alias)
    {
        using var presentContext = new BunitContext();
        Configure(presentContext, new CapturePipelineConfig(
        [
            new("ProjectedScene", "scene", 10, null, ["$raw"], Enabled: true),
            new(alias, "overlay", 20, null, ["scene"], Enabled: true)
        ]));
        presentContext.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/operations/pipeline/graphs/new?section=deep-sky#deep-sky");
        var presentCut = presentContext.Render<ProcessingGraphEditorPage>();
        presentCut.WaitForElement("#deep-sky");
        Assert.IsFalse(
            presentCut.FindAll("p[role='status']").Any(static notice =>
                notice.TextContent.Contains("no supported scene overlay node for deep-sky styling", StringComparison.Ordinal)),
            "Present scene overlay graph must not render a missing-node notice.");

        using var missingContext = new BunitContext();
        Configure(missingContext, new CapturePipelineConfig(
        [
            new("Annotation", "legacy-annot", 10, null, ["$raw"], Enabled: true)
        ]));
        missingContext.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/operations/pipeline/graphs/new?section=deep-sky#deep-sky");
        var missingCut = missingContext.Render<ProcessingGraphEditorPage>();
        missingCut.WaitForElement("#legacy-annotation");
        Assert.IsEmpty(missingCut.FindAll("#deep-sky"), "A graph without a scene overlay node must not render #deep-sky.");
        Assert.IsTrue(
            missingCut.FindAll("p[role='status']").Any(static notice =>
                notice.TextContent.Contains("no supported scene overlay node for deep-sky styling", StringComparison.Ordinal)),
            "Expected deep-sky missing-node notice on a graph without a scene overlay node.");
    }

    [TestMethod]
    public void FullEditorHrefUsesIndexedSectionAnchorForDuplicateAliasesAndMixedSceneNodes()
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("Preview", "preview-primary", 10, null, ["$raw"], Enabled: true),
            new("Preview", "preview-secondary", 20, null, ["$raw"], Enabled: true),
            new("ScenePresentationLayer", "scene-legacy", 30, null, ["$raw"], Enabled: true),
            new(SceneStylePresentation.StepAlias, "scene-styled", 40, null, ["$raw"], Enabled: true)
        ]);
        Configure(context, pipeline);

        // Verify the full editor renders the exact section IDs we expect.
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");
        var editor = context.Render<ProcessingGraphEditorPage>();
        editor.WaitForElement("#preview");
        Assert.IsNotNull(editor.Find("#preview-1"));
        Assert.IsNotNull(editor.Find("#constellations"));
        Assert.IsNotNull(editor.Find("#constellations-3"));
        editor.Dispose();

        // Verify each dedicated node page's FullEditorHref matches the full editor's actual section ID.
        AssertFullEditorHref("preview-primary", "/operations/pipeline/graphs/new?from=rev-active#preview");
        AssertFullEditorHref("preview-secondary", "/operations/pipeline/graphs/new?from=rev-active#preview-1");
        AssertFullEditorHref("scene-legacy", "/operations/pipeline/graphs/new?from=rev-active#constellations");
        AssertFullEditorHref("scene-styled", "/operations/pipeline/graphs/new?from=rev-active#constellations-3");

        void AssertFullEditorHref(string nodeId, string expectedHref)
        {
            var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, nodeId));
            cut.WaitForElement(".config-form");
            var fullEditorLink = cut.FindAll("a").Single(static link =>
                link.TextContent.Contains("Open in full graph editor", StringComparison.Ordinal));
            Assert.AreEqual(expectedHref, fullEditorLink.GetAttribute("href"));
            cut.Dispose();
        }
    }

    [TestMethod]
    public async Task EditingUnrelatedNodePreservesRetainedOverlayManifestQuotedZOrderByteForByteThroughPreviewAndSave()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-quoted-zorder-e2e");
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
                AgentId: "quoted-zorder-e2e")
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
                [new Claim(ClaimTypes.NameIdentifier, "config-owner"),
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

            var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, initial.ActiveRevisionId)
                .Add(static page => page.NodeId, "jpeg"));

            var quality = cut.WaitForElement("#node-options-8-jpegQuality");
            Assert.AreEqual("85", quality.GetAttribute("value"));
            await quality.ChangeAsync(new() { Value = "92" }).ConfigureAwait(false);
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
            await cut.Find("button.btn-primary").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));

            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            var draft = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            var savedPipeline = await operations.ReadRevisionPipelineAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(savedPipeline);
            Assert.AreEqual(92, savedPipeline.Steps.Single(static step => step.Id == "jpeg").Options!.Value.GetProperty("jpegQuality").GetInt32());
            Assert.AreEqual(storedInitialManifestRaw, savedPipeline.Steps.Single(static step => step.Id == "manifest").Options!.Value.GetRawText(),
                "Untouched OverlayManifest with quoted zOrder and unknown JSON fields must survive preview and save byte-for-byte.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly string[] ExpectedDeepSkyFonts = ["MonoRegular", "MonoBold"];

    [TestMethod]
    [DataRow("ScenePresentationLayer")]
    [DataRow("SceneStylePresentationLayerV1")]
    public void LegacyInactivePlexRegularFontIsOmittedFromPickerSurfacedAsRepairableAndAllowsUnrelatedEdits(string alias)
    {
        using var context = new BunitContext();
        const string sourceJson = """{ "includeDeepSkyLayer": false, "deepSkyFont": "PlexRegular", "future": [1e+02] }""";
        var pipeline = new CapturePipelineConfig(
        [
            new("ProjectedScene", "scene", 10, null, ["$raw"], Enabled: true),
            new(alias, "overlay", 20, JsonDocument.Parse(sourceJson).RootElement.Clone(), ["scene"], Enabled: true)
        ]);
        var service = Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "overlay"));

        var select = cut.WaitForElement("#node-options-1-deepSkyFont");
        var options = select.QuerySelectorAll("option").Select(static item => item.GetAttribute("value")).ToArray();
        CollectionAssert.AreEqual(ExpectedDeepSkyFonts, options, "PlexRegular must stay out of the deep-sky font picker.");
        StringAssert.Contains(cut.Find("#node-options-1-deepSkyFont-source-notice").TextContent, "PlexRegular", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find("#node-options-1-reset-defaults").HasAttribute("disabled"), "Reset must be enabled to repair legacy PlexRegular.");

        // Unrelated edit succeeds without breaking on untouched inactive PlexRegular.
        cut.Find("#node-options-1-constellationLineThickness").Change("4");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();
        var unrelatedSaved = service.Created.Single().Pipeline.Steps[1].Options!.Value;
        Assert.AreEqual(4, unrelatedSaved.GetProperty("constellationLineThickness").GetInt32());
        Assert.AreEqual("PlexRegular", unrelatedSaved.GetProperty("deepSkyFont").GetString());
        Assert.AreEqual("[1e+02]", unrelatedSaved.GetProperty("future").GetRawText());

        // Clicking Reset repairs deepSkyFont to MonoRegular, disables Reset, and clears the legacy notice.
        cut.Find("#node-options-1-reset-defaults").Click();
        Assert.IsTrue(cut.Find("#node-options-1-reset-defaults").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("#node-options-1-deepSkyFont-source-notice"));
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();
        var repaired = service.Created[^1].Pipeline.Steps[1].Options!.Value;
        Assert.AreEqual("MonoRegular", repaired.GetProperty("deepSkyFont").GetString());
        Assert.AreEqual("[1e+02]", repaired.GetProperty("future").GetRawText());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DedicatedPageRegionalVisibilityResetDoesNotAddUnselectedSolveDependencyAndAllowsPreviewAndSave(bool solveBeforeRegional)
    {
        using var context = new BunitContext();
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var measurement = new CaptureProcessingStepConfig(
            ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable);
        var prediction = new CaptureProcessingStepConfig(
            ExposureCatalogPredictionProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias, 5, null, ["$raw"], Required: false, Enabled: true, Publication: durable);
        var regional = new CaptureProcessingStepConfig(
            RegionalStellarVisibilityProducts.StepAlias, "regional", 30,
            JsonDocument.Parse("""{ "gridColumns": 6 }""").RootElement.Clone(),
            ["$raw", ExposureMeasurementProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias],
            Required: false, Enabled: true, Publication: durable);
        var solve = new CaptureProcessingStepConfig(
            CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias, "solve", solveBeforeRegional ? 20 : 40,
            null, ["$raw", ExposureMeasurementProducts.StepAlias], Required: false, Enabled: true, Publication: durable);
        CaptureProcessingStepConfig[] steps = solveBeforeRegional
            ? [measurement, prediction, solve, regional]
            : [measurement, prediction, regional, solve];
        var regionalIndex = solveBeforeRegional ? 3 : 2;
        var service = Configure(context, new CapturePipelineConfig(steps));

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "regional"));

        var gridColumnsId = $"#node-options-{regionalIndex}-gridColumns";
        var resetButtonId = $"#node-options-{regionalIndex}-reset-defaults";
        cut.WaitForElement(gridColumnsId);
        Assert.AreEqual("6", cut.Find(gridColumnsId).GetAttribute("value"));
        Assert.IsFalse(cut.Find(resetButtonId).HasAttribute("disabled"));

        // Resetting options on an already-enabled regional node must not rerun wiring or add unselected solve.
        cut.Find(resetButtonId).Click();
        Assert.AreEqual("4", cut.Find(gridColumnsId).GetAttribute("value"));
        Assert.IsTrue(cut.Find(resetButtonId).HasAttribute("disabled"));

        cut.Find("form").Submit();
        Assert.IsEmpty(cut.FindAll(".config-banner--error[role='alert']"),
            "Resetting regional options must not introduce an invalid or unselected dependency that blocks preview.");
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();

        var savedStep = service.Created.Single().Pipeline.Steps.Single(static step => step.Id == "regional");
        Assert.AreNotEqual(false, savedStep.Enabled);
        Assert.AreEqual(4, savedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(savedStep.DependsOn!.Contains("solve", StringComparer.Ordinal),
            "Resetting options on an already-enabled regional node must not add the unselected solve dependency.");
        Assert.IsTrue(savedStep.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
    }

    [TestMethod]
    public void DedicatedPageDisabledRegionalVisibilityResetPreservesDisabledStateAndDependencies()
    {
        using var context = new BunitContext();
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var pipeline = new CapturePipelineConfig(
        [
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias, "solve", 20, null,
                ["$raw", ExposureMeasurementProducts.StepAlias], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30,
                JsonDocument.Parse("""{ "gridColumns": 6 }""").RootElement.Clone(),
                ["$raw", ExposureMeasurementProducts.StepAlias],
                Required: false, Enabled: false, Publication: durable)
        ]);
        var service = Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "regional"));

        cut.WaitForElement("#node-options-2-gridColumns");
        Assert.AreEqual("6", cut.Find("#node-options-2-gridColumns").GetAttribute("value"));
        Assert.IsFalse(cut.Find("#node-options-2-enabled").HasAttribute("checked"));
        Assert.IsFalse(cut.Find("#node-options-2-reset-defaults").HasAttribute("disabled"));
        Assert.IsFalse(cut.FindAll(".node-switcher__item").Any(static item =>
            item.TextContent.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparison.Ordinal)));

        // Resetting options on a disabled regional node resets parameters while keeping the node disabled and unwired.
        cut.Find("#node-options-2-reset-defaults").Click();
        Assert.AreEqual("4", cut.Find("#node-options-2-gridColumns").GetAttribute("value"));
        Assert.IsFalse(cut.Find("#node-options-2-enabled").HasAttribute("checked"),
            "Reset to code defaults must preserve a disabled node's Enabled=false state.");
        Assert.IsTrue(cut.Find("#node-options-2-reset-defaults").HasAttribute("disabled"));
        Assert.IsFalse(cut.FindAll(".node-switcher__item").Any(static item =>
            item.TextContent.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparison.Ordinal)),
            "Resetting a disabled regional node must not add ExposureCatalogPrediction.");

        cut.Find("form").Submit();
        Assert.IsEmpty(cut.FindAll(".config-banner--error[role='alert']"));
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();

        var savedPipeline = service.Created.Single().Pipeline;
        Assert.IsFalse(savedPipeline.Steps.Any(static step =>
            string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
        var savedStep = savedPipeline.Steps.Single(static step => step.Id == "regional");
        Assert.AreEqual(false, savedStep.Enabled);
        Assert.AreEqual(4, savedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(savedStep.Options.Value.TryGetProperty("enabled", out _),
            "Reset to code defaults must not materialize an enabled option override.");
        Assert.IsFalse(savedStep.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
        Assert.IsFalse(savedStep.DependsOn!.Contains("solve", StringComparer.Ordinal));
    }

    [TestMethod]
    public void DedicatedPageLegacyV1DisabledCloudAssessmentResetPreservesDisabledStateThroughPreviewAndSave()
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("CloudAssessment", "cloud", 10,
                JsonDocument.Parse("""{ "enabled": false, "gridColumns": 8 }""").RootElement.Clone(),
                ["$raw"])
        ],
        CapturePipelineSchemaVersions.LegacyV1,
        CapturePipelineDependencyPolicy.LegacyInference);
        var service = Configure(context, pipeline);
        CapturePipelineConfig? previewed = null;
        var basePreview = service.Preview!;
        service.Preview = candidate =>
        {
            previewed = candidate;
            return basePreview(candidate);
        };

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "cloud"));

        cut.WaitForElement("#node-options-0-gridColumns");
        Assert.AreEqual("8", cut.Find("#node-options-0-gridColumns").GetAttribute("value"));
        Assert.IsFalse(cut.Find("#node-options-0-enabled").HasAttribute("checked"));
        Assert.IsFalse(cut.Find("#node-options-0-reset-defaults").HasAttribute("disabled"),
            "Reset to code defaults must be enabled when a legacy v1 step has non-default options.");

        cut.Find("#node-options-0-reset-defaults").Click();
        Assert.AreEqual("16", cut.Find("#node-options-0-gridColumns").GetAttribute("value"));
        Assert.IsFalse(cut.Find("#node-options-0-enabled").HasAttribute("checked"),
            "Reset to code defaults must preserve a legacy v1 step's options.enabled=false state.");
        Assert.IsTrue(cut.Find("#node-options-0-reset-defaults").HasAttribute("disabled"));

        cut.Find("form").Submit();
        Assert.IsEmpty(cut.FindAll(".config-banner--error[role='alert']"));
        Assert.IsNotNull(previewed);
        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, previewed.SchemaVersion);
        var previewedStep = previewed.Steps.Single(static step => step.Id == "cloud");
        Assert.AreEqual(16, previewedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(previewedStep.Options.Value.GetProperty("enabled").GetBoolean(),
            "Preview must retain options.enabled=false for a retained legacy v1 step after Reset.");

        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();

        var savedPipeline = service.Created.Single().Pipeline;
        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, savedPipeline.SchemaVersion);
        var savedStep = savedPipeline.Steps.Single(static step => step.Id == "cloud");
        Assert.AreEqual(16, savedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(savedStep.Options.Value.GetProperty("enabled").GetBoolean(),
            "Save must retain options.enabled=false for a retained legacy v1 step after Reset.");
    }

    [TestMethod]
    public void DedicatedPageLegacyV1DisabledCloudAssessmentAtCodeDefaultsKeepsResetDisabled()
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new("CloudAssessment", "cloud", 10,
                JsonDocument.Parse("""{ "enabled": false }""").RootElement.Clone(),
                ["$raw"])
        ],
        CapturePipelineSchemaVersions.LegacyV1,
        CapturePipelineDependencyPolicy.LegacyInference);
        Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "cloud"));

        cut.WaitForElement("#node-options-0-gridColumns");
        Assert.AreEqual("16", cut.Find("#node-options-0-gridColumns").GetAttribute("value"));
        Assert.IsFalse(cut.Find("#node-options-0-enabled").HasAttribute("checked"));
        Assert.IsTrue(cut.Find("#node-options-0-reset-defaults").HasAttribute("disabled"),
            "Reset to code defaults must stay disabled when a legacy v1 step only has options.enabled=false.");
    }

    [TestMethod]
    public void DedicatedPageRegionalVisibilityToggleDisabledRemovesUnusedPredictionAndReEnableWiresItBack()
    {
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var enabledPipeline = new CapturePipelineConfig(
        [
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(ExposureCatalogPredictionProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias, 5, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                ["$raw", ExposureMeasurementProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias],
                Required: false, Enabled: true, Publication: durable)
        ]);

        // 1. Disabling the regional node removes the unused auto-added ExposureCatalogPrediction step and dependency.
        CapturePipelineConfig disabledPipeline;
        using (var disableContext = new BunitContext())
        {
            var service = Configure(disableContext, enabledPipeline);
            var cut = disableContext.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, "regional"));
            cut.WaitForElement("#node-options-2-enabled");
            Assert.IsTrue(cut.FindAll(".node-switcher__item").Any(static item =>
                item.TextContent.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparison.Ordinal)));

            cut.Find("#node-options-2-enabled").Change(false);
            Assert.IsFalse(cut.FindAll(".node-switcher__item").Any(static item =>
                item.TextContent.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparison.Ordinal)),
                "Disabling the last regional consumer must prune the unused ExposureCatalogPrediction step.");

            cut.Find("form").Submit();
            Assert.IsEmpty(cut.FindAll(".config-banner--error[role='alert']"));
            Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("button.btn-primary").Click();

            disabledPipeline = service.Created.Single().Pipeline;
            Assert.IsFalse(disabledPipeline.Steps.Any(static step =>
                string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
            var savedRegional = disabledPipeline.Steps.Single(static step => step.Id == "regional");
            Assert.AreEqual(false, savedRegional.Enabled);
            Assert.IsFalse(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
        }

        // 2. Re-enabling the regional node wires ExposureCatalogPrediction back cleanly.
        using (var enableContext = new BunitContext())
        {
            var service = Configure(enableContext, disabledPipeline);
            var cut = enableContext.Render<ProcessingNodeConfigPage>(parameters => parameters
                .Add(static page => page.RevisionId, "rev-active")
                .Add(static page => page.NodeId, "regional"));
            cut.WaitForElement("#node-options-1-enabled");
            Assert.IsFalse(cut.FindAll(".node-switcher__item").Any(static item =>
                item.TextContent.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparison.Ordinal)));

            cut.Find("#node-options-1-enabled").Change(true);
            Assert.IsTrue(cut.FindAll(".node-switcher__item").Any(static item =>
                item.TextContent.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparison.Ordinal)),
                "Re-enabling the regional consumer must wire ExposureCatalogPrediction back.");

            cut.Find("form").Submit();
            Assert.IsEmpty(cut.FindAll(".config-banner--error[role='alert']"));
            Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("button.btn-primary").Click();

            var reEnabledPipeline = service.Created.Single().Pipeline;
            Assert.IsTrue(reEnabledPipeline.Steps.Any(static step =>
                string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
            var savedRegional = reEnabledPipeline.Steps.Single(static step => step.Id == "regional");
            Assert.AreNotEqual(false, savedRegional.Enabled);
            Assert.IsTrue(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
        }
    }

    [TestMethod]
    public void ConfigPageSwitcherDisablesNavigationForUnsavedAutoAddedNodesAndHandlesMissingNodeCleanly()
    {
        const string measurementAlias = ExposureMeasurementProducts.StepAlias;
        const string predictionAlias = ExposureCatalogPredictionProducts.StepAlias;
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var pipelineWithoutPrediction = new CapturePipelineConfig(
        [
            new(measurementAlias, measurementAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                ["$raw", measurementAlias], Required: false, Enabled: false, Publication: durable)
        ]);

        using var context = new BunitContext();
        Configure(context, pipelineWithoutPrediction);

        // 1. Enabling regional auto-adds ExposureCatalogPrediction in memory; the switcher must not link to a missing node in rev-active.
        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "regional"));
        cut.WaitForElement("#node-options-1-enabled");
        cut.Find("#node-options-1-enabled").Change(true);

        var switcherItems = cut.FindAll(".node-switcher__item");
        var measurementItem = switcherItems.Single(item => item.TextContent.Contains(measurementAlias, StringComparison.Ordinal));
        Assert.AreEqual("A", measurementItem.TagName);
        Assert.AreEqual($"/operations/pipeline/graphs/rev-active/nodes/{measurementAlias}", measurementItem.GetAttribute("href"));

        var regionalItem = switcherItems.Single(item => item.TextContent.Contains("regional", StringComparison.Ordinal));
        Assert.AreEqual("A", regionalItem.TagName);
        Assert.AreEqual("page", regionalItem.GetAttribute("aria-current"));

        var predictionItem = switcherItems.Single(item => item.TextContent.Contains(predictionAlias, StringComparison.Ordinal));
        Assert.AreNotEqual("A", predictionItem.TagName,
            "An unsaved auto-added node missing from rev-active must not render as a navigating anchor in the switcher.");
        Assert.IsNull(predictionItem.GetAttribute("href"));
        Assert.AreEqual("true", predictionItem.GetAttribute("aria-disabled"));
        StringAssert.Contains(predictionItem.ClassName, "node-switcher__item--unsaved", StringComparison.Ordinal);
        StringAssert.Contains(predictionItem.TextContent, "Unsaved draft", StringComparison.Ordinal);

        // 1b. After saving the draft, the switcher badge updates from 'Unsaved draft' to post-save wording.
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));
        var savedPredictionItem = cut.FindAll(".node-switcher__item")
            .Single(item => item.TextContent.Contains(predictionAlias, StringComparison.Ordinal));
        Assert.AreNotEqual("A", savedPredictionItem.TagName);
        Assert.AreEqual("true", savedPredictionItem.GetAttribute("aria-disabled"));
        StringAssert.Contains(savedPredictionItem.TextContent, "Saved in new draft", StringComparison.Ordinal);
        Assert.IsFalse(savedPredictionItem.TextContent.Contains("Unsaved draft", StringComparison.Ordinal),
            "Post-save switcher badge must not keep 'Unsaved draft' wording after saving.");
        var savedTooltip = savedPredictionItem.GetAttribute("title") ?? string.Empty;
        StringAssert.Contains(savedTooltip, "save banner", StringComparison.Ordinal);
        Assert.IsFalse(savedTooltip.Contains("above", StringComparison.OrdinalIgnoreCase),
            "Post-save switcher tooltip must not point 'above' when the save banner renders below the switcher.");

        // 2. Navigating directly to a missing node renders the clean fallback list without broken switcher/form state,
        //    and switching to a valid node in the revision recovers cleanly.
        cut.Render(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, predictionAlias));
        cut.WaitForElement(".missing-node-list");
        StringAssert.Contains(cut.Markup, "Filter or overlay not found", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".node-switcher"));
        Assert.IsEmpty(cut.FindAll(".config-form"));
        var fallbackHrefs = cut.FindAll(".missing-node-list a")
            .Select(static link => link.GetAttribute("href"))
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[]
            {
                $"/operations/pipeline/graphs/rev-active/nodes/{measurementAlias}",
                "/operations/pipeline/graphs/rev-active/nodes/regional"
            },
            fallbackHrefs);

        cut.Render(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "regional"));
        cut.WaitForElement(".config-form");
        Assert.IsEmpty(cut.FindAll(".missing-node-list"));
        Assert.AreEqual("page", cut.Find(".node-switcher__item--current").GetAttribute("aria-current"));
    }

    [TestMethod]
    public void ConfigPageExactRegionalToggleReversalRestoresSwitcherLinkAfterNoOpPreview()
    {
        const string measurementAlias = ExposureMeasurementProducts.StepAlias;
        const string predictionAlias = ExposureCatalogPredictionProducts.StepAlias;
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var pipeline = new CapturePipelineConfig(
        [
            new(measurementAlias, measurementAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                ["$raw", measurementAlias, predictionAlias], Required: false, Enabled: true, Publication: durable),
            new(predictionAlias, predictionAlias, DependsOn: ["$raw"], Required: false, Publication: durable)
        ]);

        using var context = new BunitContext();
        Configure(context, pipeline);

        var cut = context.Render<ProcessingNodeConfigPage>(parameters => parameters
            .Add(static page => page.RevisionId, "rev-active")
            .Add(static page => page.NodeId, "regional"));
        cut.WaitForElement("#node-options-1-enabled");

        // Toggling regional off and back on regenerates ExposureCatalogPrediction as an unsaved switcher item prior to preview.
        cut.Find("#node-options-1-enabled").Change(false);
        Assert.IsFalse(cut.FindAll(".node-switcher__item").Any(static item =>
            item.TextContent.Contains(predictionAlias, StringComparison.Ordinal)));
        cut.Find("#node-options-1-enabled").Change(true);

        var predictionItem = cut.FindAll(".node-switcher__item")
            .Single(static item => item.TextContent.Contains(predictionAlias, StringComparison.Ordinal));
        Assert.AreNotEqual("A", predictionItem.TagName);
        StringAssert.Contains(predictionItem.TextContent, "Unsaved draft", StringComparison.Ordinal);

        // Previewing the exact reversal confirms no graph changes (Save disabled) and restores the source switcher link.
        cut.Find("form").Submit();
        StringAssert.Contains(cut.Find(".config-banner[role='status']").TextContent, "No graph changes.", StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"),
            "Save must remain disabled when preview confirms no graph changes.");
        predictionItem = cut.FindAll(".node-switcher__item")
            .Single(static item => item.TextContent.Contains(predictionAlias, StringComparison.Ordinal));
        Assert.AreEqual("A", predictionItem.TagName,
            "An exact toggle reversal confirmed unchanged by preview must restore the source-revision switcher link instead of telling the operator to save.");
        Assert.AreEqual($"/operations/pipeline/graphs/rev-active/nodes/{predictionAlias}", predictionItem.GetAttribute("href"));
        Assert.IsFalse(predictionItem.TextContent.Contains("Unsaved draft", StringComparison.Ordinal));

        // Making a real edit afterward clears the unchanged preview and returns the regenerated stage to the unsaved badge.
        cut.Find("#node-options-1-gridColumns").Change("6");
        predictionItem = cut.FindAll(".node-switcher__item")
            .Single(static item => item.TextContent.Contains(predictionAlias, StringComparison.Ordinal));
        Assert.AreNotEqual("A", predictionItem.TagName);
        StringAssert.Contains(predictionItem.TextContent, "Unsaved draft", StringComparison.Ordinal);
    }

    private static ProcessingExecutionPagesTests.GraphUiService Configure(BunitContext context, CapturePipelineConfig pipeline)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = ProcessingGraphPagesTests.Registry(),
            StepAliases = pipeline.Steps.Select(static step => step.Type).Distinct(StringComparer.Ordinal).ToArray(),
            RevisionDetail = new(ProcessingGraphPagesTests.Registry().Revisions[0], pipeline, null, null),
            Preview = candidate => OperatorUiResult<CaptureProcessingPlanPreview>.Success(
                new(candidate.SchemaVersion, candidate.DependencyPolicy, new string('D', 64), new string('E', 64), [], [])),
            Create = command => OperatorUiResult<ProcessingGraphRevisionState>.Success(
                new("rev-new", command.Name, command.Revision, ProcessingGraphRevisionLifecycle.Draft,
                    new string('N', 64), new string('S', 64), new string('L', 64), DateTimeOffset.UtcNow, null, null, null))
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        return service;
    }

    private sealed class StubAuthProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
}
