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
