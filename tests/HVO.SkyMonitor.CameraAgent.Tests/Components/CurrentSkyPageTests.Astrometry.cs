using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using Bunit;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    private static readonly Lazy<Task<MeasuredAstrometryFixture>> AstrometryFixture = new(MeasuredAstrometryFixture.CreateAsync);

    private static async Task<CameraAgentRetainedAstrometry> AstrometryEvidenceAsync(string scenario = "accepted")
    {
        if (scenario == "accepted")
        {
            // Component consumers use real retained input; a cold solve belongs in producer tests.
            var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retained-astrometry");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "accepted-solution.json")).ConfigureAwait(false);
            Assert.AreEqual("947DC629346E531E252FC89E251182BBC6B44FD569E4A49B97664E2970DA6AB1", ProcessingIdentity.ComputePayloadSha256(bytes));
            var retained = MeasuredAstrometryJson.Parse(bytes);
            var calibration = JsonSerializer.Deserialize<AstrometricCalibration>(
                await File.ReadAllBytesAsync(Path.Combine(root, "calibration.json")).ConfigureAwait(false));
            Assert.IsNotNull(calibration);
            Assert.AreEqual(AstrometricAssessmentStatus.Accepted, retained.Assessment.Status);
            Assert.AreEqual(calibration.ReadoutIdentitySha256, retained.ReadoutIdentitySha256);
            return new(retained.Assessment.Frame.CaptureId, "Retained", retained.Assessment.ReasonCode,
                SolutionIdentitySha256: retained.SolutionIdentitySha256, GeometryBasis: "MeasuredFit", Solution: retained)
            {
                Mapping = new AstrometricMapping(calibration, retained.Assessment),
                CalibrationVersion = calibration.CalibrationVersion,
                WidthPixels = calibration.Projection.WidthPixels,
                HeightPixels = calibration.Projection.HeightPixels
            };
        }
        var f = await AstrometryFixture.Value.ConfigureAwait(false);
        if (scenario == "rejected") f = f.Keep(_ => false);
        var request = f.Request(context: scenario == "unavailable" ? f.Context with { CatalogComplete = false } : null,
            options: scenario == "budget" ? MeasuredAstrometryFixture.Options with { MaximumExecutionMilliseconds = .000001 } : null);
        var result = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(result.Products.Single().Payload);
        return new(solution.Assessment.Frame.CaptureId, "Retained", solution.Assessment.ReasonCode,
            SolutionIdentitySha256: solution.SolutionIdentitySha256, GeometryBasis: solution.Assessment.HasMeasuredMapping ? "MeasuredFit" : "Unavailable", Solution: solution)
        {
            Mapping = solution.Assessment.HasMeasuredMapping ? MeasuredAstrometryJson.ReconstructMapping(solution, f.Calibration, f.Associations) : null,
            CalibrationVersion = f.Calibration.CalibrationVersion,
            WidthPixels = 512,
            HeightPixels = 512
        };
    }

    private static CameraAgentCurrentImagePresentation AstrometryImage(CameraAgentRetainedAstrometry evidence)
    {
        var p = OperatorUiTestData.CurrentImage();
        var frame = evidence.Solution!.Assessment.Frame;
        var capture = p.DisplayCapture! with { CaptureId = evidence.CaptureId, ExposureStartedUtc = frame.ExposureStartUtc };
        return p with
        {
            DisplayCapture = capture,
            LatestCapture = capture,
            SelectedStage = CameraAgentPresentationStage.Raw,
            Stages = p.Stages.Select(s => s.Stage == CameraAgentPresentationStage.Raw ? s with { ArtifactId = frame.SourceArtifactId, DisplayArtifactId = frame.SourceArtifactId } : s).ToArray()
        };
    }

    [TestMethod]
    [DataRow("accepted", "Accepted", true)]
    [DataRow("rejected", "Rejected", false)]
    [DataRow("unavailable", "Unavailable", false)]
    [DataRow("budget", "Budget exceeded", false)]
    public async Task AstrometryShowsRetainedStatusAndOnlyAcceptedSourceAllowsCalculators(string scenario, string status, bool enabled)
    {
        var evidence = await AstrometryEvidenceAsync(scenario).ConfigureAwait(false);
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(AstrometryImage(evidence)));
        var reads = 0;
        service.AstrometryHandler = (_, _) => { reads++; return ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(evidence)); };
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(status, cut.Find(".astrometry-card .state-chip").TextContent));
        Assert.AreEqual(!enabled, cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".astrometry-provenance").TextContent, evidence.Solution!.ReadoutIdentitySha256, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".astrometry-card").TextContent, "no calibration activation", StringComparison.Ordinal);
        if (!enabled) return;
        await cut.Find(".pixel-sky-calculator input").ChangeAsync("280").ConfigureAwait(false);
        await cut.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240").ConfigureAwait(false);
        await cut.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
        StringAssert.Contains(cut.Find(".astrometry-calculator-result").TextContent, "mean of date", StringComparison.Ordinal);
        await cut.Find("form.sky-pixel-calculator").SubmitAsync().ConfigureAwait(false);
        var x = double.Parse(cut.FindAll(".pixel-sky-calculator input")[0].GetAttribute("value")!, CultureInfo.InvariantCulture);
        var y = double.Parse(cut.FindAll(".pixel-sky-calculator input")[1].GetAttribute("value")!, CultureInfo.InvariantCulture);
        Assert.AreEqual(280, x, 1e-7); Assert.AreEqual(240, y, 1e-7);
        Assert.AreEqual(1, reads, "Calculator clicks must not reopen raw evidence.");
        var horizontal = evidence.Mapping!.PixelToSky(new(280, 240))!.Horizontal;
        await cut.Find("form.sky-pixel-calculator select").ChangeAsync("horizontal").ConfigureAwait(false);
        await cut.FindAll("form.sky-pixel-calculator input")[0].ChangeAsync(horizontal.AltitudeDegrees.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await cut.FindAll("form.sky-pixel-calculator input")[1].ChangeAsync(horizontal.AzimuthDegrees.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await cut.Find("form.sky-pixel-calculator").SubmitAsync().ConfigureAwait(false);
        x = double.Parse(cut.FindAll(".pixel-sky-calculator input")[0].GetAttribute("value")!, CultureInfo.InvariantCulture);
        y = double.Parse(cut.FindAll(".pixel-sky-calculator input")[1].GetAttribute("value")!, CultureInfo.InvariantCulture);
        Assert.AreEqual(280, x, 1e-7); Assert.AreEqual(240, y, 1e-7);
        await cut.Find(".pixel-sky-calculator input").ChangeAsync("999999").ConfigureAwait(false);
        await cut.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
        StringAssert.Contains(cut.Find(".astrometry-calculator-result").TextContent, "inside the retained aperture", StringComparison.Ordinal);
        await cut.Find("form.sky-pixel-calculator select").ChangeAsync("horizontal").ConfigureAwait(false);
        await cut.FindAll("form.sky-pixel-calculator input")[0].ChangeAsync("-1").ConfigureAwait(false);
        await cut.FindAll("form.sky-pixel-calculator input")[1].ChangeAsync("30").ConfigureAwait(false);
        await cut.Find("form.sky-pixel-calculator").SubmitAsync().ConfigureAwait(false);
        StringAssert.Contains(cut.Find(".astrometry-calculator-result").TextContent, "above the horizon", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("foreign")]
    [DataRow("denied")]
    [DataRow("failure")]
    public async Task AstrometryAbsenceMismatchAndDenialFailClosed(string scenario)
    {
        var accepted = await AstrometryEvidenceAsync().ConfigureAwait(false);
        using var context = new BunitContext();
        var service = Configure(context);
        service.AstrometryHandler = (id, _) => scenario switch
        {
            "denied" => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Failure(OperatorUiResultKind.Unauthorized, "Denied")),
            "foreign" => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(accepted)),
            "failure" => throw new IOException("sensitive-path"),
            _ => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(new(id, "NotRetained", "assessment-not-retained")))
        };
        var cut = context.Render<CurrentSkyPage>();
        if (scenario == "denied")
        {
            cut.WaitForAssertion(() => Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
            Assert.IsEmpty(cut.FindAll(".sky-image-stage img"));
            Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
        }
        else
        {
            cut.WaitForAssertion(() => Assert.AreEqual("Unavailable", cut.Find(".astrometry-card .state-chip").TextContent));
            Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            Assert.IsFalse(cut.Markup.Contains("sensitive-path", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
        }
    }

    [TestMethod]
    public async Task LateAstrometryCannotFollowAChangedCaptureOrImageStage()
    {
        var accepted = await AstrometryEvidenceAsync().ConfigureAwait(false);
        using var context = new BunitContext();
        var service = Configure(context);
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentRetainedAstrometry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = AstrometryImage(accepted);
        var current = first;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.AstrometryHandler = (id, _) => id == accepted.CaptureId ? new(pending.Task) : ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(new(id, "NotRetained", "assessment-not-retained")));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("Loading", cut.Find(".astrometry-card .state-chip").TextContent));
        current = OperatorUiTestData.CurrentImage() with { DisplayCapture = OperatorUiTestData.CurrentImage().DisplayCapture! with { ExposureStartedUtc = first.DisplayCapture!.ExposureStartedUtc.AddMinutes(1) } };
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        pending.SetResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(accepted));
        cut.WaitForAssertion(() => Assert.AreEqual("Unavailable", cut.Find(".astrometry-card .state-chip").TextContent));
        Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
        Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
    }
    [TestMethod]
    public async Task MeasuredTogglesUseFrozenTypedManifestAndKeepExpectedLayerIndependent()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        var associationId = new string('E', 64);
        var residualId = new string('F', 64);
        service.PresentationHandler = (id, _) =>
        {
            var p = Layered(id, new string('D', 64)) with { ManifestArtifactId = Guid.NewGuid() };
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(p with
            {
                Layers = [.. p.Layers,
                    new(associationId, AstrometricDiagnosticPresentation.AssociationsLayer, "measured-associations", 600, false, 1_000_000, "v1", "v1"),
                    new(residualId, AstrometricDiagnosticPresentation.ResidualsLayer, "measured-residuals", 610, false, 1_000_000, "v1", "v1")]
            }));
        };
        IReadOnlyList<string>? saved = null;
        service.FrozenMaterializationHandler = (id, manifestId, selected, _) =>
        {
            Assert.AreEqual(new string('A', 64), manifestId);
            saved = selected;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(new(id,
                Guid.NewGuid(), new string('A', 64), new string('B', 64), 100, false)));
        };
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        module.SetupVoid("downloadUrl", _ => true).SetVoidResult();
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled")));
        var baseUrl = cut.Find(".sky-layer-canvas img").GetAttribute("src");
        await cut.Find(".sky-layer-controls input[data-layer-target='measured-residuals']").ChangeAsync(true).ConfigureAwait(false);
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { new string('D', 64), residualId }, saved!.ToArray());
        Assert.IsFalse(cut.Find(".sky-layer-controls input[data-layer-target='measured-associations']").HasAttribute("checked"));
        Assert.AreEqual(baseUrl, cut.Find(".sky-layer-canvas img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "actual 1× pixel vectors", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ArchivedAstrometryUsesSelectedCaptureAndStageAndClearsSensitiveEvidenceOnDenial()
    {
        var accepted = await AstrometryEvidenceAsync().ConfigureAwait(false);
        using var context = new BunitContext();
        var service = Configure(context);
        var image = AstrometryImage(accepted);
        var archive = new CameraAgentCaptureDetailView(OperatorUiTestData.Capture(accepted.CaptureId) with { ExposureStartedUtc = accepted.Solution!.Assessment.Frame.ExposureStartUtc },
            new(CameraAgentPresentationStage.Raw, image.Stages));
        service.AstrometryHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(accepted));
        var cut = context.Render<CurrentSkyPage>(p => p.Add(c => c.ArchivedView, archive)
            .Add(c => c.Evidence, (RenderFragment)(builder => builder.AddContent(0, "Sensitive archive evidence"))));
        cut.WaitForAssertion(() => Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent));
        Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        StringAssert.Contains(cut.Markup, "Sensitive archive evidence", StringComparison.Ordinal);
        await cut.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
        Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"), "Annotated pixels without a compatible base cannot use the raw mapping.");
        var other = new CameraAgentCaptureDetailView(OperatorUiTestData.Capture(), new(CameraAgentPresentationStage.Raw, OperatorUiTestData.CurrentImage().Stages));
        service.AstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Failure(OperatorUiResultKind.Unauthorized, "Denied"));
        cut.Render(p => p.Add(c => c.ArchivedView, other));
        cut.WaitForAssertion(() => Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
        Assert.IsEmpty(cut.FindAll(".sky-image-stage img"));
        Assert.IsFalse(cut.Markup.Contains("Sensitive archive evidence", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".current-sky-page"));
    }

    [TestMethod]
    public async Task PendingAssessmentAppearsOnSameCaptureRefreshWithoutReloadingAnAcceptedFit()
    {
        var accepted = await AstrometryEvidenceAsync().ConfigureAwait(false);
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(AstrometryImage(accepted)));
        var reads = 0;
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
            Layered(id, new string('D', 64)) with { Astrometry = ++reads == 1 ? new(id, "NotRetained", "assessment-not-retained") : accepted }));
        service.AstrometryHandler = (_, _) => throw new InvalidOperationException("An enriched layer read already includes the assessment.");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("Unavailable", cut.Find(".astrometry-card .state-chip").TextContent));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent));
        Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        Assert.AreEqual(2, reads);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(2, reads, "An immutable accepted assessment is retained without repeated raw reads.");
    }

    [TestMethod]
    [DataRow("accepted")]
    [DataRow("failure")]
    [DataRow("timeout")]
    [DataRow("denied")]
    public async Task OlderStandaloneAstrometryCannotReplaceNewerSameCaptureLayerEvidence(string completion)
    {
        var accepted = await AstrometryEvidenceAsync().ConfigureAwait(false);
        using var context = new BunitContext();
        var service = Configure(context);
        var image = WithStructuredBase();
        var capture = image.DisplayCapture! with { CaptureId = accepted.CaptureId, ExposureStartedUtc = accepted.Solution!.Assessment.Frame.ExposureStartUtc };
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            image with { DisplayCapture = capture, LatestCapture = capture }));
        // Complete on the renderer, allowing the awaiting continuation to finish synchronously before assertions.
        var pending = new DeferredAstrometryRead();
        var standaloneReads = 0;
        service.AstrometryHandler = (_, _) => { standaloneReads++; return pending.Read(); };
        var enriched = accepted with { CompatibleBaseArtifactId = image.Stages.Single(s => s.Stage == CameraAgentPresentationStage.Combined).DisplayArtifactId };
        var layerReads = 0;
        service.PresentationHandler = (id, _) => ++layerReads == 1
            ? ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.NotFound, "Pending layers"))
            : ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64)) with { Astrometry = enriched }));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, standaloneReads));
        Assert.AreEqual("Loading", cut.Find(".astrometry-card .state-chip").TextContent);
        await cut.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent));
        Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        await cut.InvokeAsync(() =>
        {
            if (completion == "failure") pending.Complete(error: new IOException("private-path"));
            else if (completion == "timeout") pending.Complete(error: new OperationCanceledException("read deadline"));
            else pending.Complete(completion == "denied"
                ? OperatorUiResult<CameraAgentRetainedAstrometry>.Failure(OperatorUiResultKind.Unauthorized, "Denied")
                : OperatorUiResult<CameraAgentRetainedAstrometry>.Success(accepted));
        }).ConfigureAwait(false);
        if (completion == "denied")
        {
            cut.WaitForAssertion(() => Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
            Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
            Assert.IsEmpty(cut.FindAll(".sky-image-stage img"));
        }
        else
        {
            Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent);
            Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"), "The newer compatible base must survive the older standalone completion.");
            await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            Assert.AreEqual(2, layerReads, "The newer immutable enriched snapshot remains current.");
            Assert.AreEqual(1, standaloneReads);
            Assert.IsFalse(cut.Markup.Contains("private-path", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task AStaleLayerAuthorizationDenialClearsTheNewCapturesSensitiveEvidence()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var current = WithStructuredBase();
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        service.PresentationHandler = (id, _) => ++reads == 2 ? new(pending.Task)
            : ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".sky-layer-canvas img");
        var oldRead = cut.Find("button.refresh-measured-evidence").ClickAsync();
        cut.WaitForAssertion(() => Assert.AreEqual(2, reads));
        var next = current.DisplayCapture! with { CaptureId = Guid.NewGuid(), ExposureStartedUtc = current.DisplayCapture.ExposureStartedUtc.AddMinutes(1) };
        current = current with { DisplayCapture = next, LatestCapture = next };
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual(3, reads));
        pending.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unauthorized, "Denied"));
        await oldRead.ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        Assert.IsEmpty(cut.FindAll(".sky-image-stage img"));
        Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
    }

    // A controllable service completion drives the actual component continuation on the renderer and
    // lets the test observe its final state without sleeps or a race against a detached read task.
    private sealed class DeferredAstrometryRead : IValueTaskSource<OperatorUiResult<CameraAgentRetainedAstrometry>>
    {
        private Action<object?>? _continuation;
        private object? _state;
        private OperatorUiResult<CameraAgentRetainedAstrometry>? _result;
        private Exception? _error;
        private bool _completed;
        public ValueTask<OperatorUiResult<CameraAgentRetainedAstrometry>> Read() => new(this, 0);
        public ValueTaskSourceStatus GetStatus(short token) => _completed ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
        public OperatorUiResult<CameraAgentRetainedAstrometry> GetResult(short token) => _error is { } error ? throw error : _result!;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _continuation = continuation;
            _state = state;
        }
        public void Complete(OperatorUiResult<CameraAgentRetainedAstrometry>? result = null, Exception? error = null)
        {
            Assert.IsNotNull(_continuation, "The initial missing-layer response must start the pending standalone read.");
            _result = result;
            _error = error;
            _completed = true;
            _continuation(_state);
        }
    }

}
