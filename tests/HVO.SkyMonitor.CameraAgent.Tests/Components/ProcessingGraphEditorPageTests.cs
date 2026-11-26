using System.Security.Claims;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
    public void RowHeaderTypeSelectReplacesTheOptionsForARetainedAlias()
    {
        const string retainedAlias = "ProjectedScene";
        const string replacementAlias = "CombinedPreview";
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            StepAliases = [replacementAlias],
            RevisionDetail = new CameraAgentProcessingGraphRevisionDetail(
                ProcessingGraphPagesTests.Registry().Revisions[1],
                new CapturePipelineConfig([new(retainedAlias, "scene", DependsOn: ["$raw"], Required: false, Enabled: true)]),
                null,
                null)
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-validated");

        var cut = context.Render<ProcessingGraphEditorPage>();

        var header = cut.WaitForElement(".node-row__head");
        var typeSelect = (IHtmlSelectElement)header.QuerySelector("select[aria-label='Node type']")!;
        Assert.AreEqual(retainedAlias, typeSelect.Value);
        Assert.IsNotNull(typeSelect.QuerySelector($"option[value='{retainedAlias}']"));
        Assert.IsNotNull(cut.Find(".node-options-section[aria-label='Projected scene query']"));

        typeSelect.Change(replacementAlias);

        var row = cut.Find($".node-row[data-node-alias='{replacementAlias}']");
        var updatedSelect = (IHtmlSelectElement)row.QuerySelector(".node-row__head select[aria-label='Node type']")!;
        Assert.AreEqual(replacementAlias, updatedSelect.Value);
        Assert.IsNull(updatedSelect.QuerySelector($"option[value='{retainedAlias}']"));
        Assert.AreEqual("scene", row.QuerySelector(".node-row__head input[aria-label='Node identifier']")?.GetAttribute("value"));
        Assert.IsNotNull(row.QuerySelector(".node-row__head button[aria-label='Remove scene']"));
        Assert.IsNull(row.QuerySelector(".node-options-section[aria-label='Projected scene query']"));
        Assert.IsNotNull(row.QuerySelector(".node-options-section[aria-label='Combined preview']"));
    }

    [TestMethod]
    public void RowHeaderTypeSelectWiresRegionalVisibilityAndLinksToTheRetainedMeasurementStage()
    {
        const string initialAlias = "CombinedPreview";
        const string measurementAlias = ExposureMeasurementProducts.StepAlias;
        const string predictionAlias = ExposureCatalogPredictionProducts.StepAlias;
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var pipeline = new CapturePipelineConfig(
        [
            new(measurementAlias, measurementAlias, 4,
                null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(initialAlias, "preview", 10, null, ["$raw"], Required: true, Enabled: true)
        ]);
        var service = CreateEditorService(pipeline);
        service.StepAliases = [initialAlias, RegionalStellarVisibilityProducts.StepAlias];
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

        var cut = context.Render<ProcessingGraphEditorPage>();

        var predictionSelector = $"li.node-row[data-node-alias='{predictionAlias}']";
        var typeSelect = cut.WaitForElement($".node-row[data-node-alias='{initialAlias}'] .node-row__head select[aria-label='Node type']");
        Assert.IsEmpty(cut.FindAll(predictionSelector));

        typeSelect.Change(RegionalStellarVisibilityProducts.StepAlias);

        Assert.HasCount(1, cut.FindAll(predictionSelector));
        var inspect = cut.Find($"li.node-row[data-node-alias='{measurementAlias}'] .node-row--builtin a");
        Assert.AreEqual("Inspect built-in stage", inspect.TextContent.Trim());
        Assert.AreEqual($"/operations/pipeline/graphs/rev-active/nodes/{measurementAlias}", inspect.GetAttribute("href"));

        cut.Find("form").Submit();
        cut.Find(".editor-actions button.btn-primary").Click();

        var regional = service.Created.Single().Pipeline.Steps.Single(static step => step.Id == "preview");
        Assert.AreEqual(RegionalStellarVisibilityProducts.StepAlias, regional.Type);
        Assert.IsFalse(regional.Required);
        Assert.AreEqual(durable, regional.Publication);
        CollectionAssert.AreEquivalent(
            new[] { "$raw", measurementAlias, predictionAlias }, regional.DependsOn!.ToArray());
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
    public async Task CenteredGraphWithoutPredictionBranch_AddsRegionalVisibilitySavesAndReopensInRenderedEditor()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-editor-regional-vis");
        try
        {
            var baseline = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var location = DeploymentLocationSnapshot.Create(
                "editor-regional-test", 1, "virtual", null, DateTimeOffset.UnixEpoch, null,
                35.347, -113.878, 1200, "America/Phoenix");
            var profile = baseline with
            {
                DeploymentLocation = location,
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1925,
                    illuminationMode = "ControlledNight",
                    asi178Sensor = new { enabled = false }
                })),
                Rig = baseline.Rig with
                {
                    Sensor = baseline.Rig.Sensor with
                    {
                        WidthPixels = 256,
                        HeightPixels = 256,
                        ColorMode = SensorColorMode.Mono,
                        PixelFormat = CameraPixelFormat.Mono16,
                        ResponseMode = SensorResponseMode.Monochrome
                    },
                    Optics = baseline.Rig.Optics with
                    {
                        PrincipalPointX = 128,
                        PrincipalPointY = 128,
                        ImageCircleRadiusPixels = 122
                    }
                },
                CenteredExposure = new("editor-cam", CenteredExposureProfile.CurrentExecutionVersion),
                Pipeline = baseline.Pipeline with
                {
                    Steps =
                    [
                        new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new { strategy = "None", outputVariant = "qualified-linear" })),
                        new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
                        new("Storage", "storage", Order: 75, DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                            new FileStorageCaptureProcessingStepOptions
                            {
                                StorageRoot = Path.Combine(root, "storage"),
                                QueueForUpload = false,
                                UpdateLatestFrame = false
                            }))
                    ]
                }
            };

            using var provider = ProcessingGraphOperationsTests.CreateProvider(root, configureServices: services =>
            {
                HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
                services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
                var locations = new Mock<IDeploymentLocationStore>();
                locations.Setup(store => store.Resolve(location.ToProvenance(), It.IsAny<DateTimeOffset?>())).Returns(location);
                services.AddSingleton(locations.Object);
            });
            await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var operations = provider.GetRequiredService<IProcessingGraphOperations>();
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            var initialPipeline = (await operations.ReadRevisionPipelineAsync(registry.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!;
            Assert.IsFalse(initialPipeline.Steps.Any(static s =>
                string.Equals(s.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));

            var authentication = new Mock<AuthenticationStateProvider>();
            authentication.Setup(service => service.GetAuthenticationStateAsync()).ReturnsAsync(
                new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "operator-1"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
                ], IdentityConstants.ApplicationScheme))));
            var authorization = new Mock<IAuthorizationService>();
            authorization.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
                .ReturnsAsync(AuthorizationResult.Success());
            var uiService = new CameraAgentProcessingGraphUiService(
                authentication.Object,
                authorization.Object,
                operations,
                provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
                provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
                provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>(),
                TimeProvider.System,
                NullLogger<CameraAgentProcessingGraphUiService>.Instance,
                provider.GetRequiredService<IExposureMeasurementRunReader>(),
                provider.GetRequiredService<IRegionalStellarVisibilityRunReader>());

            // 1. Open the centered revision without a prediction branch, add RegionalStellarVisibility, configure, preview, and save.
            string savedRevisionId;
            using (var context = new BunitContext())
            {
                context.JSInterop.Mode = JSRuntimeMode.Loose;
                context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(uiService);
                var navigation = context.Services.GetRequiredService<NavigationManager>();
                navigation.NavigateTo($"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(registry.ActiveRevisionId)}");

                var cut = context.Render<ProcessingGraphEditorPage>();
                cut.WaitForElement(".node-row");

                await cut.Find("select[aria-label='New node type']")
                    .ChangeAsync(new ChangeEventArgs { Value = RegionalStellarVisibilityProducts.StepAlias }).ConfigureAwait(false);
                await cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node")
                    .ClickAsync().ConfigureAwait(false);

                var regionalRow = cut.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
                Assert.IsNotNull(regionalRow);
                Assert.IsNotNull(cut.Find($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"));

                var gridColsInput = regionalRow.QuerySelector("input[id$='-gridColumns']")!;
                await gridColsInput.ChangeAsync(new ChangeEventArgs { Value = "6" }).ConfigureAwait(false);

                await cut.Find("form").SubmitAsync().ConfigureAwait(false);
                cut.WaitForAssertion(() =>
                {
                    Assert.IsNotNull(cut.Find("svg[role='group']"));
                    Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
                });

                await cut.Find("button.btn-primary").ClickAsync().ConfigureAwait(false);
                cut.WaitForAssertion(() =>
                    Assert.IsTrue(cut.Markup.Contains("saved as an immutable revision", StringComparison.Ordinal), cut.Markup));

                var savedLink = cut.Find("a[href^='/operations/pipeline/graphs/']");
                savedRevisionId = Uri.UnescapeDataString(savedLink.GetAttribute("href")!["/operations/pipeline/graphs/".Length..]);
            }

            // Verify persisted revision satisfies the production validator and carries durable optional regional wiring.
            var savedPipeline = (await operations.ReadRevisionPipelineAsync(savedRevisionId, CancellationToken.None).ConfigureAwait(false))!;
            var savedRegional = savedPipeline.Steps.Single(static s =>
                string.Equals(s.Type, RegionalStellarVisibilityProducts.StepAlias, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(savedRegional.Required);
            Assert.AreEqual(durable, savedRegional.Publication);
            Assert.IsTrue(savedRegional.DependsOn!.Contains("$raw", StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(savedRegional.DependsOn!.Contains(ExposureMeasurementProducts.StepAlias, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.OrdinalIgnoreCase));
            Assert.AreEqual(6, savedRegional.Options!.Value.GetProperty("gridColumns").GetInt32());
            Assert.AreEqual(
                JsonSerializer.Serialize(initialPipeline.Steps.Single(static step => step.Id == "storage")),
                JsonSerializer.Serialize(savedPipeline.Steps.Single(static step => step.Id == "storage")));

            // 2. Reopen the saved draft in a fresh rendered ProcessingGraphEditorPage and verify round-trip fidelity.
            using (var reopenContext = new BunitContext())
            {
                reopenContext.JSInterop.Mode = JSRuntimeMode.Loose;
                reopenContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(uiService);
                var navigation = reopenContext.Services.GetRequiredService<NavigationManager>();
                navigation.NavigateTo($"/operations/pipeline/graphs/new?from={Uri.EscapeDataString(savedRevisionId)}");

                var reopened = reopenContext.Render<ProcessingGraphEditorPage>();
                reopened.WaitForElement($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
                Assert.IsNotNull(reopened.Find($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"));
                var reopenedRegionalRow = reopened.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
                Assert.AreEqual("6", reopenedRegionalRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));
            }
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GraphEditorRegionalVisibilityResetDoesNotAddUnselectedSolveDependencyAndAllowsPreviewAndSave(bool solveBeforeRegional)
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
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
        var service = CreateEditorService(new CapturePipelineConfig(steps));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

        var cut = context.Render<ProcessingGraphEditorPage>();
        var regionalRow = cut.WaitForElement($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
        Assert.AreEqual("6", regionalRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));

        regionalRow.QuerySelector("button[id$='-reset-defaults']")!.Click();
        regionalRow = cut.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
        Assert.AreEqual("4", regionalRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));

        cut.Find("form").Submit();
        Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"),
            "Resetting regional options in the graph editor must not add an invalid or unselected dependency.");
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("svg[role='group']")));
        Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
        cut.Find(".editor-actions button.btn-primary").Click();

        var savedStep = service.Created.Single().Pipeline.Steps.Single(static step => step.Id == "regional");
        Assert.AreNotEqual(false, savedStep.Enabled);
        Assert.AreEqual(4, savedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(savedStep.DependsOn!.Contains("solve", StringComparer.Ordinal),
            "Resetting options on an already-enabled regional node must not add the unselected solve dependency.");
        Assert.IsTrue(savedStep.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
    }

    [TestMethod]
    public void GraphEditorDisabledRegionalVisibilityResetPreservesDisabledStateAndDependencies()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
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
        var service = CreateEditorService(pipeline);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

        var cut = context.Render<ProcessingGraphEditorPage>();
        var regionalRow = cut.WaitForElement($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
        Assert.AreEqual("6", regionalRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));
        Assert.IsFalse(regionalRow.QuerySelector("input[id$='-enabled']")!.HasAttribute("checked"));
        Assert.IsFalse(FindMainRowEnabledCheckbox(regionalRow).HasAttribute("checked"));
        Assert.IsFalse(regionalRow.QuerySelector("button[id$='-reset-defaults']")!.HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"));

        // Resetting options on a disabled regional node resets parameters while keeping the node disabled and unwired.
        regionalRow.QuerySelector("button[id$='-reset-defaults']")!.Click();
        regionalRow = cut.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
        Assert.AreEqual("4", regionalRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));
        Assert.IsFalse(regionalRow.QuerySelector("input[id$='-enabled']")!.HasAttribute("checked"),
            "Reset to code defaults must preserve a disabled node's Enabled=false state.");
        Assert.IsFalse(FindMainRowEnabledCheckbox(regionalRow).HasAttribute("checked"),
            "Main-row Enabled checkbox must stay unchecked after Reset to code defaults.");
        Assert.IsTrue(regionalRow.QuerySelector("button[id$='-reset-defaults']")!.HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"),
            "Resetting a disabled regional node must not add ExposureCatalogPrediction.");

        cut.Find("form").Submit();
        Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"));
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("svg[role='group']")));
        Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
        cut.Find(".editor-actions button.btn-primary").Click();

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
    public void GraphEditorLegacyV1DisabledCloudAssessmentResetPreservesDisabledStateThroughPreviewAndSave()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var pipeline = new CapturePipelineConfig(
        [
            new("CloudAssessment", "cloud", 10,
                JsonDocument.Parse("""{ "enabled": false, "gridColumns": 8 }""").RootElement.Clone(),
                ["$raw"])
        ],
        CapturePipelineSchemaVersions.LegacyV1,
        CapturePipelineDependencyPolicy.LegacyInference);
        var service = CreateEditorService(pipeline);
        CapturePipelineConfig? previewed = null;
        var basePreview = service.Preview!;
        service.Preview = candidate =>
        {
            previewed = candidate;
            return basePreview(candidate);
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

        var cut = context.Render<ProcessingGraphEditorPage>();
        var cloudRow = cut.WaitForElement("li.node-row[data-node-alias='CloudAssessment']");
        Assert.AreEqual("8", cloudRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));
        Assert.IsFalse(cloudRow.QuerySelector("input[id$='-enabled']")!.HasAttribute("checked"));
        Assert.IsFalse(cloudRow.QuerySelector("button[id$='-reset-defaults']")!.HasAttribute("disabled"),
            "Reset to code defaults must be enabled when a legacy v1 step has non-default options.");

        cloudRow.QuerySelector("button[id$='-reset-defaults']")!.Click();
        cloudRow = cut.Find("li.node-row[data-node-alias='CloudAssessment']");
        Assert.AreEqual("16", cloudRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));
        Assert.IsFalse(cloudRow.QuerySelector("input[id$='-enabled']")!.HasAttribute("checked"),
            "Reset to code defaults must preserve a legacy v1 step's options.enabled=false state.");
        Assert.IsTrue(cloudRow.QuerySelector("button[id$='-reset-defaults']")!.HasAttribute("disabled"));

        cut.Find("form").Submit();
        Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"));
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("svg[role='group']")));
        Assert.IsNotNull(previewed);
        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, previewed.SchemaVersion);
        var previewedStep = previewed.Steps.Single(static step => step.Id == "cloud");
        Assert.AreEqual(16, previewedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(previewedStep.Options.Value.GetProperty("enabled").GetBoolean(),
            "Preview must retain options.enabled=false for a retained legacy v1 step after Reset.");

        Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
        cut.Find(".editor-actions button.btn-primary").Click();

        var savedPipeline = service.Created.Single().Pipeline;
        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, savedPipeline.SchemaVersion);
        var savedStep = savedPipeline.Steps.Single(static step => step.Id == "cloud");
        Assert.AreEqual(16, savedStep.Options!.Value.GetProperty("gridColumns").GetInt32());
        Assert.IsFalse(savedStep.Options.Value.GetProperty("enabled").GetBoolean(),
            "Save must retain options.enabled=false for a retained legacy v1 step after Reset.");
    }

    [TestMethod]
    public void GraphEditorLegacyV1DisabledCloudAssessmentAtCodeDefaultsKeepsResetDisabled()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var pipeline = new CapturePipelineConfig(
        [
            new("CloudAssessment", "cloud", 10,
                JsonDocument.Parse("""{ "enabled": false }""").RootElement.Clone(),
                ["$raw"])
        ],
        CapturePipelineSchemaVersions.LegacyV1,
        CapturePipelineDependencyPolicy.LegacyInference);
        var service = CreateEditorService(pipeline);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

        var cut = context.Render<ProcessingGraphEditorPage>();
        var cloudRow = cut.WaitForElement("li.node-row[data-node-alias='CloudAssessment']");
        Assert.AreEqual("16", cloudRow.QuerySelector("input[id$='-gridColumns']")!.GetAttribute("value"));
        Assert.IsFalse(cloudRow.QuerySelector("input[id$='-enabled']")!.HasAttribute("checked"));
        Assert.IsTrue(cloudRow.QuerySelector("button[id$='-reset-defaults']")!.HasAttribute("disabled"),
            "Reset to code defaults must stay disabled when a legacy v1 step only has options.enabled=false.");
    }

    [TestMethod]
    public void GraphEditorRegionalVisibilityMainRowEnabledCheckboxPrunesUnusedPredictionAndReWiresPredecessors()
    {
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var enabledPipeline = new CapturePipelineConfig(
        [
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(ExposureCatalogPredictionProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias, 5, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias, "solve", 20, null,
                ["$raw", ExposureMeasurementProducts.StepAlias], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                ["$raw", ExposureMeasurementProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias],
                Required: false, Enabled: true, Publication: durable)
        ]);

        // 1. Disabling via the main-row Enabled checkbox (ProcessingGraphEditorPage.razor:84) prunes unused ExposureCatalogPrediction.
        CapturePipelineConfig disabledPipeline;
        using (var disableContext = new BunitContext())
        {
            disableContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(enabledPipeline);
            disableContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            disableContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = disableContext.Render<ProcessingGraphEditorPage>();
            cut.WaitForElement($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']");
            var regionalRow = cut.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
            FindMainRowEnabledCheckbox(regionalRow).Change(false);

            Assert.IsEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"),
                "Disabling the last regional consumer via the row checkbox must remove the unused ExposureCatalogPrediction row.");

            cut.Find("form").Submit();
            Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"));
            Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
            cut.Find(".editor-actions button.btn-primary").Click();

            disabledPipeline = service.Created.Single().Pipeline;
            Assert.IsFalse(disabledPipeline.Steps.Any(static step =>
                string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
            var savedRegional = disabledPipeline.Steps.Single(static step => step.Id == "regional");
            Assert.AreEqual(false, savedRegional.Enabled);
            Assert.IsFalse(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
        }

        // 2. Re-enabling via the main-row Enabled checkbox wires ExposureCatalogPrediction and eligible predecessor solve cleanly.
        using (var enableContext = new BunitContext())
        {
            enableContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(disabledPipeline);
            enableContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            enableContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = enableContext.Render<ProcessingGraphEditorPage>();
            var regionalRow = cut.WaitForElement($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
            Assert.IsEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"));
            FindMainRowEnabledCheckbox(regionalRow).Change(true);

            Assert.IsNotEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"),
                "Re-enabling the regional consumer via the row checkbox must wire ExposureCatalogPrediction back.");

            cut.Find("form").Submit();
            Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"));
            Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
            cut.Find(".editor-actions button.btn-primary").Click();

            var reEnabledPipeline = service.Created.Single().Pipeline;
            Assert.IsTrue(reEnabledPipeline.Steps.Any(static step =>
                string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
            var savedRegional = reEnabledPipeline.Steps.Single(static step => step.Id == "regional");
            Assert.AreNotEqual(false, savedRegional.Enabled);
            Assert.IsTrue(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
            Assert.IsTrue(savedRegional.DependsOn!.Contains("solve", StringComparer.Ordinal),
                "Re-enabling the regional consumer via the row checkbox must wire the eligible solve predecessor.");
        }
    }

    private static AngleSharp.Dom.IElement FindMainRowEnabledCheckbox(AngleSharp.Dom.IElement nodeRow)
        => nodeRow.QuerySelectorAll(".node-row__head label.check")
            .Single(static label => label.TextContent.Contains("Enabled", StringComparison.Ordinal))
            .QuerySelector("input[type='checkbox']")!;

    [TestMethod]
    public void GraphEditorRegionalVisibilityToggleDisabledRemovesUnusedPredictionAndReEnableWiresItBack()
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

        // 1. Disabling via the options editor toggle (ProcessingGraphEditorPage.razor:119) prunes unused ExposureCatalogPrediction.
        CapturePipelineConfig disabledPipeline;
        using (var disableContext = new BunitContext())
        {
            disableContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(enabledPipeline);
            disableContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            disableContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = disableContext.Render<ProcessingGraphEditorPage>();
            cut.WaitForElement($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']");
            cut.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}'] input[id$='-enabled']").Change(false);

            Assert.IsEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"),
                "Disabling the last regional consumer must remove the unused ExposureCatalogPrediction row.");

            cut.Find("form").Submit();
            Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"));
            Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
            cut.Find(".editor-actions button.btn-primary").Click();

            disabledPipeline = service.Created.Single().Pipeline;
            Assert.IsFalse(disabledPipeline.Steps.Any(static step =>
                string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
            var savedRegional = disabledPipeline.Steps.Single(static step => step.Id == "regional");
            Assert.AreEqual(false, savedRegional.Enabled);
            Assert.IsFalse(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
        }

        // 2. Re-enabling via the options editor toggle wires ExposureCatalogPrediction back cleanly.
        using (var enableContext = new BunitContext())
        {
            enableContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(disabledPipeline);
            enableContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            enableContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = enableContext.Render<ProcessingGraphEditorPage>();
            cut.WaitForElement($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
            Assert.IsEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"));

            cut.Find($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}'] input[id$='-enabled']").Change(true);
            Assert.IsNotEmpty(cut.FindAll($"li.node-row[data-node-alias='{ExposureCatalogPredictionProducts.StepAlias}']"),
                "Re-enabling the regional consumer must wire ExposureCatalogPrediction back.");

            cut.Find("form").Submit();
            Assert.IsEmpty(cut.FindAll(".editor-banner--error[role='alert']"));
            Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
            cut.Find(".editor-actions button.btn-primary").Click();

            var reEnabledPipeline = service.Created.Single().Pipeline;
            Assert.IsTrue(reEnabledPipeline.Steps.Any(static step =>
                string.Equals(step.Type, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase)));
            var savedRegional = reEnabledPipeline.Steps.Single(static step => step.Id == "regional");
            Assert.AreNotEqual(false, savedRegional.Enabled);
            Assert.IsTrue(savedRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
        }
    }

    [TestMethod]
    public void GraphEditorRegionalVisibilitySectionDeepLinkShowsAnchorWhenPresentAndNoteRoleWhenAbsent()
    {
        using (var presentContext = new BunitContext())
        {
            presentContext.JSInterop.Mode = JSRuntimeMode.Loose;
            presentContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(CreateEditorService(
                new CapturePipelineConfig([new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null, ["$raw"], Required: false, Enabled: true)])));
            presentContext.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/operations/pipeline/graphs/new?section=regional-stellar-visibility#regional-stellar-visibility");

            var present = presentContext.Render<ProcessingGraphEditorPage>();
            present.WaitForElement("#regional-stellar-visibility");
            Assert.IsFalse(present.FindAll("p[role='note']").Any(static notice =>
                notice.TextContent.Contains("no regional stellar visibility node", StringComparison.Ordinal)));
        }

        using (var absentContext = new BunitContext())
        {
            absentContext.JSInterop.Mode = JSRuntimeMode.Loose;
            absentContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(CreateEditorService(
                new CapturePipelineConfig([new("Preview", "preview", 10, null, ["$raw"], Enabled: true)])));
            absentContext.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/operations/pipeline/graphs/new?section=regional-stellar-visibility#regional-stellar-visibility");

            var absent = absentContext.Render<ProcessingGraphEditorPage>();
            absent.WaitForElement(".node-row");
            Assert.IsEmpty(absent.FindAll("#regional-stellar-visibility"));
            Assert.IsTrue(
                absent.FindAll("p[role='note']").Any(static notice =>
                    notice.TextContent.Contains("This graph has no regional stellar visibility node.", StringComparison.Ordinal)),
                "Expected regional stellar visibility missing-node notice with role='note'.");
            Assert.IsFalse(
                absent.FindAll("p[role='status']").Any(static notice =>
                    notice.TextContent.Contains("This graph has no regional stellar visibility node.", StringComparison.Ordinal)),
                "Regional stellar visibility missing-node notice must use role='note', not role='status'.");
        }
    }

    [TestMethod]
    public void GraphEditorHidesInspectLinkForUnsavedAutoAddedBuiltInStageWhileKeepingSavedStageLinkEnabled()
    {
        const string measurementAlias = ExposureMeasurementProducts.StepAlias;
        const string predictionAlias = ExposureCatalogPredictionProducts.StepAlias;
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);

        // 1. Source revision contains ExposureMeasurement and disabled regional, without ExposureCatalogPrediction.
        using (var unsavedContext = new BunitContext())
        {
            unsavedContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var pipelineWithoutPrediction = new CapturePipelineConfig(
            [
                new(measurementAlias, measurementAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
                new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                    ["$raw", measurementAlias], Required: false, Enabled: false, Publication: durable)
            ]);
            var service = CreateEditorService(pipelineWithoutPrediction);
            service.StepAliases = ["Preview", RegionalStellarVisibilityProducts.StepAlias];
            unsavedContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            unsavedContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = unsavedContext.Render<ProcessingGraphEditorPage>();
            var regionalRow = cut.WaitForElement($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}']");
            FindMainRowEnabledCheckbox(regionalRow).Change(true);

            // Saved built-in stage keeps its Inspect built-in stage link.
            var savedBuiltInLink = cut.Find($"li.node-row[data-node-alias='{measurementAlias}'] .node-row--builtin a");
            Assert.AreEqual("Inspect built-in stage", savedBuiltInLink.TextContent.Trim());
            Assert.AreEqual($"/operations/pipeline/graphs/rev-active/nodes/{measurementAlias}", savedBuiltInLink.GetAttribute("href"));

            // Auto-added unsaved built-in stage must not render a broken inspect link to rev-active.
            var unsavedBuiltInRow = cut.Find($"li.node-row[data-node-alias='{predictionAlias}'] .node-row--builtin");
            Assert.IsEmpty(unsavedBuiltInRow.QuerySelectorAll("a"),
                "Auto-added ExposureCatalogPrediction does not exist in rev-active and must not render a broken inspect link.");
            StringAssert.Contains(unsavedBuiltInRow.TextContent, "unsaved draft", StringComparison.OrdinalIgnoreCase);

            // Newly added configurable node also omits Configure on its own page until saved.
            cut.Find("select[aria-label='New node type']").Change("Preview");
            cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node").Click();
            var addedPreviewRow = cut.Find("li.node-row[data-node-alias='Preview']");
            Assert.IsEmpty(addedPreviewRow.QuerySelectorAll("a.node-options-page-link"),
                "Newly added editable node does not exist in rev-active and must not render a broken dedicated-page link.");
            Assert.IsNotEmpty(cut.FindAll($"li.node-row[data-node-alias='{RegionalStellarVisibilityProducts.StepAlias}'] a.node-options-page-link"),
                "Saved editable node must retain its Configure on its own page link.");

            // After saving the draft, the built-in row updates from unsaved wording to post-save wording.
            cut.Find("form").Submit();
            Assert.IsFalse(cut.Find(".editor-actions button.btn-primary").HasAttribute("disabled"));
            cut.Find(".editor-actions button.btn-primary").Click();
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));
            unsavedBuiltInRow = cut.Find($"li.node-row[data-node-alias='{predictionAlias}'] .node-row--builtin");
            Assert.IsEmpty(unsavedBuiltInRow.QuerySelectorAll("a"));
            StringAssert.Contains(unsavedBuiltInRow.TextContent, "Added in the saved draft", StringComparison.Ordinal);
            Assert.IsFalse(unsavedBuiltInRow.TextContent.Contains("unsaved draft", StringComparison.OrdinalIgnoreCase),
                "Post-save notice must not keep 'unsaved draft' wording after saving.");
        }

        // 2. When ExposureCatalogPrediction already exists in the source revision, its Inspect built-in stage link stays enabled.
        using (var savedContext = new BunitContext())
        {
            savedContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var pipelineWithPrediction = new CapturePipelineConfig(
            [
                new(measurementAlias, measurementAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
                new(predictionAlias, predictionAlias, 5, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
                new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                    ["$raw", measurementAlias, predictionAlias], Required: false, Enabled: true, Publication: durable)
            ]);
            savedContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(CreateEditorService(pipelineWithPrediction));
            savedContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = savedContext.Render<ProcessingGraphEditorPage>();
            var predictionLink = cut.WaitForElement($"li.node-row[data-node-alias='{predictionAlias}'] .node-row--builtin a");
            Assert.AreEqual("Inspect built-in stage", predictionLink.TextContent.Trim());
            Assert.AreEqual($"/operations/pipeline/graphs/rev-active/nodes/{predictionAlias}", predictionLink.GetAttribute("href"));
        }
    }

    [TestMethod]
    public void GraphEditorHidesDedicatedPageLinkOnDeletedIdReuseTypeChangeAndDuplicateId()
    {
        var pipeline = new CapturePipelineConfig(
        [
            new("ImageQuality", "quality", 10, null, ["$raw"], Enabled: true),
            new("Preview", "preview", 20, null, ["$raw"], Enabled: true)
        ]);

        // 1. Deleting a saved node and renaming another node to its ID hides Configure on its own page.
        using (var reuseContext = new BunitContext())
        {
            reuseContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(pipeline);
            service.StepAliases = ["ImageQuality", "Preview", "CombinedPreview"];
            reuseContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            reuseContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = reuseContext.Render<ProcessingGraphEditorPage>();
            cut.WaitForElement("li.node-row[data-node-alias='Preview'] a.node-options-page-link");
            cut.Find("button[aria-label='Remove quality']").Click();
            cut.Find("li.node-row[data-node-alias='Preview'] input[aria-label='Node identifier']").Change("quality");

            Assert.IsEmpty(cut.FindAll("li.node-row[data-node-alias='Preview'] a.node-options-page-link"),
                "Renaming a surviving row to a deleted saved node's ID must hide Configure on its own page.");
        }

        // 2. Changing a saved node's type hides Configure on its own page.
        using (var typeContext = new BunitContext())
        {
            typeContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(pipeline);
            service.StepAliases = ["ImageQuality", "Preview", "CombinedPreview"];
            typeContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            typeContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = typeContext.Render<ProcessingGraphEditorPage>();
            cut.WaitForElement("li.node-row[data-node-alias='Preview'] a.node-options-page-link");
            cut.Find("li.node-row[data-node-alias='Preview'] select[aria-label='Node type']").Change("CombinedPreview");

            Assert.IsEmpty(cut.FindAll("li.node-row[data-node-alias='CombinedPreview'] a.node-options-page-link"),
                "Changing a saved row's type must hide Configure on its own page.");
            Assert.IsNotEmpty(cut.FindAll("li.node-row[data-node-alias='ImageQuality'] a.node-options-page-link"),
                "Unchanged saved rows must keep Configure on its own page.");
        }

        // 3. Duplicating a saved ID hides Configure on its own page on both conflicting rows.
        using (var duplicateContext = new BunitContext())
        {
            duplicateContext.JSInterop.Mode = JSRuntimeMode.Loose;
            var service = CreateEditorService(pipeline);
            service.StepAliases = ["ImageQuality", "Preview"];
            duplicateContext.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            duplicateContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");

            var cut = duplicateContext.Render<ProcessingGraphEditorPage>();
            cut.WaitForElement("li.node-row[data-node-alias='Preview'] a.node-options-page-link");
            cut.Find("select[aria-label='New node type']").Change("Preview");
            cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node").Click();
            var previewRows = cut.FindAll("li.node-row[data-node-alias='Preview']");
            Assert.HasCount(2, previewRows);
            previewRows[1].QuerySelector("input[aria-label='Node identifier']")!.Change("preview");

            Assert.IsEmpty(cut.FindAll("li.node-row[data-node-alias='Preview'] a.node-options-page-link"),
                "Duplicating a saved ID must hide Configure on its own page for all conflicting rows.");
        }
    }

    private static ProcessingExecutionPagesTests.GraphUiService CreateEditorService(CapturePipelineConfig pipeline)
    {
        var registry = ProcessingGraphPagesTests.Registry();
        return new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = registry,
            StepAliases = pipeline.Steps.Select(static step => step.Type).Distinct(StringComparer.Ordinal).ToArray(),
            RevisionDetail = new(registry.Revisions[0], pipeline, null, null),
            Preview = candidate =>
            {
                var nodes = candidate.Steps.Select(static step =>
                    new CaptureProcessingPlanNode(step.Id!, step.Type, step.Enabled ?? true, step.Required, step.Order, null, step.DependsOn, null, null, null)).ToArray();
                return OperatorUiResult<CaptureProcessingPlanPreview>.Success(
                    new(candidate.SchemaVersion, candidate.DependencyPolicy, new string('D', 64), new string('E', 64), nodes, nodes));
            },
            Create = command => OperatorUiResult<ProcessingGraphRevisionState>.Success(
                new("rev-new", command.Name, command.Revision, ProcessingGraphRevisionLifecycle.Draft,
                    new string('N', 64), new string('S', 64), new string('L', 64), DateTimeOffset.UtcNow, null, null, null))
        };
    }

    private sealed class StubAuthProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
