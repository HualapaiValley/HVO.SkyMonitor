using System.Security.Claims;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
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
}
