using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class RetainedAstrometryBrowserAcceptanceTests
{
    private static readonly string[] AcceptedMappingControls = ["associations", "residuals-1x", "pixel-to-sky", "equatorial-to-pixel", "horizontal-to-pixel", "configured-image-circle", "predicted-sky-annotations", "frozen-residual-export", "restore-defaults", "refresh-measured-evidence"];

    private static async Task<ProjectedSceneV1> VerifyCoexistingScenesAsync(CameraAgentKestrelFixture host,
        CameraAgentLatestAstrometry accepted, string output)
    {
        var store = host.Services.GetRequiredService<SqliteCaptureProcessingStore>();
        var contentService = host.Services.GetRequiredService<ICameraAgentArtifactService>();
        var astrometry = await host.Services.GetRequiredService<ICameraAgentRetainedAstrometryService>()
            .GetAsync(accepted.CaptureId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(astrometry.SourceDescriptor);
        var candidates = new List<DurableCaptureProduct>();
        foreach (var schema in ProjectedSceneV1.SupportedSchemaVersions)
            candidates.AddRange(await store.ReadCaptureProductsAsync(accepted.CaptureId,
                schema, 3, CancellationToken.None).ConfigureAwait(false));
        Assert.HasCount(2, candidates);
        var canonicalProduct = candidates.Single(p => p.NodeId == "ProjectedScene");
        var predictionProduct = candidates.Single(p => p.NodeId == AstrometryPredictionCaptureProcessingStep.Alias);
        var canonical = await ReadSceneAsync(canonicalProduct).ConfigureAwait(false);
        var prediction = await ReadSceneAsync(predictionProduct).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, canonical.Kind);
        Assert.HasCount(6, canonical.Selection.ConstellationIds);
        Assert.HasCount(7, canonical.Selection.SolarSystemBodies);
        Assert.IsNotEmpty(canonical.Segments);
        Assert.AreEqual(ProjectedSceneKind.Predicted, prediction.Kind);
        Assert.IsEmpty(prediction.Selection.ConstellationIds);
        Assert.IsEmpty(prediction.Selection.SolarSystemBodies);
        Assert.AreNotEqual(canonical.SceneIdentitySha256, prediction.SceneIdentitySha256);
        Assert.AreEqual(canonical.Source, prediction.Source);
        var canonicalUpload = await host.Services.GetRequiredService<CaptureProcessingPersistence>()
            .FindCommittedSceneUploadAsync(astrometry.SourceDescriptor, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(canonicalUpload);
        Assert.AreEqual(canonicalProduct.ArtifactId, canonicalUpload.Descriptor.Artifact.ArtifactId);
        var result = await contentService.OpenContentAsync(accepted.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
        await using var content = result.Content!;
        Assert.AreEqual(predictionProduct.ArtifactId, content.ProductManifest!.Artifact.SourceArtifactIds[2]);
        await File.WriteAllTextAsync(Path.Combine(output, "coexisting-scenes.json"), JsonSerializer.Serialize(new
        {
            canonicalProduct,
            predictionProduct,
            canonical.Kind,
            canonical.Selection,
            constellationSegments = canonical.Segments.Count,
            solarSystemObjects = canonical.Objects.Where(o => o.Kind == CelestialObjectKind.SolarSystemBody),
            independentKind = prediction.Kind,
            independentSelection = prediction.Selection,
            canonical.Source,
            accepted.SolutionIdentitySha256
        }, AstrometryBrowserJson)).ConfigureAwait(false);
        return canonical;

        async Task<ProjectedSceneV1> ReadSceneAsync(DurableCaptureProduct product)
        {
            var read = await contentService.OpenContentAsync(product.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, read.Status);
            await using var lease = read.Content!;
            var payload = new byte[checked((int)lease.ByteLength)];
            await lease.ReadExactlyAsync(payload).ConfigureAwait(false);
            var parsed = ProjectedSceneJson.Parse(payload);
            Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
            Assert.AreEqual(product.ContentIdentitySha256, parsed.Scene!.SceneIdentitySha256);
            return parsed.Scene;
        }
    }

    private static async Task VerifyCoexistingReplayAsync(CameraAgentKestrelFixture host,
        CameraAgentLatestAstrometry accepted, string output)
    {
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var live = (await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 4, deadline.Token)
            .ConfigureAwait(false)).Single(e => e.CaptureId == accepted.CaptureId);
        var submission = await operations.SubmitReplayAsync(
            new(accepted.CaptureId, live.GraphRevisionId, live.PrimaryArtifactId, Reason: "Verify both frozen scenes"),
            "issue1901-dual-scene-replay", "disposable-proof", deadline.Token).ConfigureAwait(false);
        ProcessingGraphExecutionState? state;
        do
        {
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            state = await operations.ReadExecutionAsync(submission.Execution.ExecutionId, deadline.Token).ConfigureAwait(false);
        } while (state?.Status is ProcessingGraphExecutionStatus.Pending or ProcessingGraphExecutionStatus.Running);
        Assert.IsNotNull(state);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, state.Status, state.FailureReason);
        var replay = await operations.ReadExecutionDetailAsync(state.ExecutionId, deadline.Token).ConfigureAwait(false);
        var original = await operations.ReadExecutionDetailAsync(live.ExecutionId, deadline.Token).ConfigureAwait(false);
        Assert.IsNotNull(replay);
        Assert.IsNotNull(original);
        foreach (var id in (string[])["ProjectedScene", AstrometryPredictionCaptureProcessingStep.Alias])
        {
            var node = replay.Nodes.Single(n => n.NodeId == id);
            var liveProduct = original.Nodes.Single(n => n.NodeId == id).Outputs.Single();
            Assert.AreEqual(liveProduct.ArtifactId, node.Inputs.Single(i => i.OutputIdentitySha256 is not null).ArtifactId);
            Assert.AreEqual(liveProduct.OutputIdentitySha256, node.Outputs.Single().OutputIdentitySha256);
        }
        Assert.IsTrue(replay.Nodes.Single(n => n.NodeId == "Astrometry").Outputs.Any(o => o.ArtifactId == accepted.ArtifactId));
        Assert.IsFalse(replay.Nodes.SelectMany(n => n.Outputs).Any(o => o.Published));
        await File.WriteAllTextAsync(Path.Combine(output, "coexisting-replay.json"), JsonSerializer.Serialize(replay, AstrometryBrowserJson))
            .ConfigureAwait(false);
    }

    private static async Task VerifyRichSceneControlsAsync(IPage page, ProjectedSceneV1 canonical, string output)
    {
        await VerifySceneLayerControlsAsync(page, ["Constellation lines"]).ConfigureAwait(false);
        var constellationGroup = await page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Constellation lines" })
            .Locator("input").GetAttributeAsync("data-layer-target").ConfigureAwait(false);
        var constellations = page.Locator("#" + constellationGroup).Locator("line");
        Assert.HasCount(canonical.Segments.Count, await constellations.AllAsync().ConfigureAwait(false));
        var annotations = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Sky annotations" }).Locator("input");
        await annotations.CheckAsync().ConfigureAwait(false);
        var markers = await ReadMeasuredGeometryAsync(page, annotations, false).ConfigureAwait(false);
        var bodies = canonical.Objects.Where(o => o.Kind == CelestialObjectKind.SolarSystemBody).ToArray();
        Assert.IsNotEmpty(bodies, "The disposable capture should have visible solar-system targets.");
        foreach (var body in bodies)
        {
            Assert.IsTrue(markers.Any(m => Math.Abs(m[0] - body.Pixel.X) < 1e-5 && Math.Abs(m[1] - body.Pixel.Y) < 1e-5),
                $"The canonical annotation should contain the projected marker for {body.DisplayName}.");
        }
        var associations = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Measured stellar associations" }).Locator("input");
        await associations.CheckAsync().ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, associations, true).ConfigureAwait(false);
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "current-rich-scene-and-solution.png"), FullPage = true }).ConfigureAwait(false);
    }

    private static async Task VerifyCurrentMeasuredControlsAsync(IPage page, CameraAgentKestrelFixture host,
        CameraAgentLatestAstrometry accepted, string output)
    {
        CameraAgentLayeredPresentation? shown;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            do
            {
                shown = (await host.Services.GetRequiredService<ICameraAgentLayeredPresentationService>()
                    .GetAsync(accepted.CaptureId, deadline.Token).ConfigureAwait(false)).Presentation;
                if (shown?.Astrometry?.CompatibleBaseArtifactId is null) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            } while (shown?.Astrometry?.CompatibleBaseArtifactId is null);
        }
        var evidence = shown.Astrometry!;
        Assert.AreEqual(accepted.CaptureId, shown.CaptureId);
        Assert.AreEqual(accepted.SolutionIdentitySha256, evidence.SolutionIdentitySha256);
        Assert.AreEqual(shown.BaseArtifactId, evidence.CompatibleBaseArtifactId);
        Assert.IsNotNull(evidence.Diagnostics);
        Assert.IsNotNull(evidence.Mapping);
        await page.Locator(".stage-selector button").Filter(new() { Has = page.Locator("span", new() { HasTextString = "Processed" }) }).ClickAsync().ConfigureAwait(false);
        await page.Locator("button.refresh-measured-evidence").ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("expected => document.querySelector('.sky-layer-workspace')?.dataset.presentationIdentity === expected", shown.PresentationIdentitySha256).ConfigureAwait(false);
        await page.Locator(".sky-layer-canvas").WaitForAsync().ConfigureAwait(false);
        var baseImageSource = await page.Locator(".sky-layer-canvas > img").GetAttributeAsync("src").ConfigureAwait(false);
        Assert.IsNotNull(baseImageSource);
        Assert.Contains(shown.BaseArtifactId.ToString("D"), baseImageSource, StringComparison.OrdinalIgnoreCase);
        var associations = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Measured stellar associations" }).Locator("input");
        var residuals = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Astrometric residuals" }).Locator("input");
        await associations.UncheckAsync().ConfigureAwait(false);
        await residuals.UncheckAsync().ConfigureAwait(false);
        await associations.CheckAsync().ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, associations, true).ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, residuals, false).ConfigureAwait(false);
        var measured = await ReadMeasuredGeometryAsync(page, associations, false).ConfigureAwait(false);
        var expectedMeasured = evidence.Diagnostics.Residuals.Select(r => new[] { r.Measured.X, r.Measured.Y }).OrderBy(p => p[0]).ThenBy(p => p[1]).ToArray();
        AssertGeometry(expectedMeasured, measured);
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "current-measured-associations.png"), FullPage = true }).ConfigureAwait(false);
        await associations.UncheckAsync().ConfigureAwait(false);
        await residuals.CheckAsync().ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, associations, false).ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, residuals, true).ConfigureAwait(false);
        var vectors = await ReadMeasuredGeometryAsync(page, residuals, true).ConfigureAwait(false);
        var expectedVectors = evidence.Diagnostics.Residuals.Select(r => new[] { r.Predicted.X, r.Predicted.Y, r.Measured.X, r.Measured.Y })
            .OrderBy(p => p[0]).ThenBy(p => p[1]).ToArray();
        AssertGeometry(expectedVectors, vectors);
        var expected = evidence.Mapping.PixelToSky(new(560, 490))!;
        await page.GetByLabel("X pixel", new() { Exact = true }).FillAsync("560").ConfigureAwait(false);
        await page.GetByLabel("Y pixel", new() { Exact = true }).FillAsync("490").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Pixel to sky", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.Locator(".astrometry-calculator-result").Filter(new() { HasText = "RA " }).WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(expected.EquatorialMeanOfDate.RightAscensionHours, double.Parse(await page.GetByLabel("RA hours", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-9);
        Assert.AreEqual(expected.EquatorialMeanOfDate.DeclinationDegrees, double.Parse(await page.GetByLabel("Declination degrees", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-8);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sky to pixel", Exact = true }).ClickAsync().ConfigureAwait(false);
        await AssertCalculatedPixelAsync(page).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Coordinates" }).SelectOptionAsync("horizontal").ConfigureAwait(false);
        await page.GetByLabel("Altitude degrees", new() { Exact = true }).FillAsync(expected.Horizontal.AltitudeDegrees.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await page.GetByLabel("Azimuth degrees", new() { Exact = true }).FillAsync(expected.Horizontal.AzimuthDegrees.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await page.GetByLabel("X pixel", new() { Exact = true }).FillAsync("1").ConfigureAwait(false);
        await page.GetByLabel("Y pixel", new() { Exact = true }).FillAsync("1").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sky to pixel", Exact = true }).ClickAsync().ConfigureAwait(false);
        await AssertCalculatedPixelAsync(page).ConfigureAwait(false);
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "current-residuals-calculator.png"), FullPage = true }).ConfigureAwait(false);
        await VerifyConfiguredPredictedControlsAsync(page).ConfigureAwait(false);
        var residualTarget = await residuals.GetAttributeAsync("data-layer-target").ConfigureAwait(false);
        foreach (var box in await page.Locator(".sky-layer-controls input[data-layer-target]").AllAsync().ConfigureAwait(false))
            if (await box.GetAttributeAsync("data-layer-target").ConfigureAwait(false) != residualTarget) await box.UncheckAsync().ConfigureAwait(false);
        await residuals.CheckAsync().ConfigureAwait(false);
        await SaveAstrometrySelectionAsync(page, host, shown.CaptureId, shown.ManifestIdentitySha256, output, 1024).ConfigureAwait(false);
        await page.Locator("button.restore-layers").ClickAsync().ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, associations, false).ConfigureAwait(false);
        await AssertAstrometryGroupAsync(page, residuals, false).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "exact-frame-controls.json"), JsonSerializer.Serialize(new
        {
            accepted.CaptureId,
            accepted.SolutionIdentitySha256,
            rawArtifactId = evidence.Solution!.Assessment.Frame.SourceArtifactId,
            shown.BaseArtifactId,
            shown.ManifestIdentitySha256,
            shown.PresentationIdentitySha256,
            measured,
            vectors,
            expectedMeasured,
            expectedVectors,
            expectedPixel = new { X = 560, Y = 490 },
            expectedSky = expected,
            uncertainty = "Point uncertainty withheld; no calibration mutation",
            controls = AcceptedMappingControls
        }, AstrometryBrowserJson)).ConfigureAwait(false);
    }

    private static async Task AssertCalculatedPixelAsync(IPage page)
    {
        await page.WaitForFunctionAsync("() => Math.abs(Number(document.querySelector('.pixel-sky-calculator input').value) - 560) < 1e-7").ConfigureAwait(false);
        Assert.AreEqual(560d, double.Parse(await page.GetByLabel("X pixel", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-7);
        Assert.AreEqual(490d, double.Parse(await page.GetByLabel("Y pixel", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-7);
    }

    private static async Task<double[][]> ReadMeasuredGeometryAsync(IPage page, ILocator checkbox, bool vectors)
    {
        var group = await checkbox.GetAttributeAsync("data-layer-target").ConfigureAwait(false);
        var data = await page.Locator("#" + group).Locator(vectors ? "line" : "circle").EvaluateAllAsync<double[][]>(vectors
            ? "es => es.map(e => ['x1','y1','x2','y2'].map(a => Number(e.getAttribute(a))))"
            : "es => es.map(e => ['cx','cy'].map(a => Number(e.getAttribute(a))))").ConfigureAwait(false);
        Assert.IsNotEmpty(data);
        return data.OrderBy(p => p[0]).ThenBy(p => p[1]).ToArray();
    }

    private static void AssertGeometry(double[][] expected, double[][] actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (var row = 0; row < expected.Length; row++)
            for (var axis = 0; axis < expected[row].Length; axis++) Assert.AreEqual(expected[row][axis], actual[row][axis], 1e-5);
    }

    private static Task VerifyConfiguredPredictedControlsAsync(IPage page)
        => VerifySceneLayerControlsAsync(page, ["Image geometry", "Sky annotations"]);

    private static async Task VerifySceneLayerControlsAsync(IPage page, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var box = page.Locator(".sky-layer-controls label").Filter(new() { HasText = name }).Locator("input");
            await box.UncheckAsync().ConfigureAwait(false);
            await AssertAstrometryGroupAsync(page, box, false).ConfigureAwait(false);
            await box.CheckAsync().ConfigureAwait(false);
            Assert.IsFalse(await box.IsDisabledAsync().ConfigureAwait(false), name);
            await AssertAstrometryGroupAsync(page, box, true).ConfigureAwait(false);
            var group = await box.GetAttributeAsync("data-layer-target").ConfigureAwait(false);
            Assert.IsGreaterThan(0, await page.Locator("#" + group).Locator("circle,ellipse,line,text,path").CountAsync().ConfigureAwait(false), name);
        }
    }

    private static async Task VerifyUnsolvedMeasuredControlsAsync(IPage page, CameraAgentLatestAstrometry accepted, string output)
    {
        foreach (var name in new[] { "Measured stellar associations", "Astrometric residuals" })
            Assert.IsTrue(await page.Locator(".sky-layer-controls label").Filter(new() { HasText = name }).Locator("input").IsDisabledAsync().ConfigureAwait(false));
        foreach (var name in new[] { "Pixel to sky", "Sky to pixel" })
            Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).IsDisabledAsync().ConfigureAwait(false));
        Assert.IsEmpty(await page.Locator("g[data-layer-kind='" + AstrometricDiagnosticPresentation.AssociationsLayer + "'],g[data-layer-kind='" + AstrometricDiagnosticPresentation.ResidualsLayer + "']").AllAsync().ConfigureAwait(false));
        Assert.IsEmpty(await page.Locator(".astrometry-calculator-result").AllAsync().ConfigureAwait(false));
        Assert.IsTrue(await page.GetByRole(AriaRole.Combobox, new() { Name = "Coordinates" }).IsDisabledAsync().ConfigureAwait(false));
        Assert.IsEmpty(await page.Locator(".pixel-sky-calculator input:not([disabled])").AllAsync().ConfigureAwait(false));
        Assert.AreEqual(accepted.MidpointUtc.ToString("O"), await page.Locator(".latest-measured-solution time").GetAttributeAsync("datetime").ConfigureAwait(false));
        await VerifyConfiguredPredictedControlsAsync(page).ConfigureAwait(false);
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "unsolved-measured-withheld-predicted-working.png"), FullPage = true }).ConfigureAwait(false);
    }
}
