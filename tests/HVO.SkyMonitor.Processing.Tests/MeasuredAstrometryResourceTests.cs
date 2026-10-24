using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest test class.")]
public sealed class MeasuredAstrometryResourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReferenceFieldReportsNativeAndRecipeColdWarmResourcesAndOutputParity()
    {
        var f = await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
        var detections = f.Associations.Associations.Where(a => a.LabelEligible && !a.Saturated && !a.Trailed && a.Flux > 0)
            .Select(a => new AstrometricDetection(a.DetectionIndex, new(a.MeasuredX, a.MeasuredY), a.Flux)).ToArray();
        var covariances = f.Associations.Associations.Where(a => detections.Any(d => d.Index == a.DetectionIndex) &&
                a.CovarianceXX is > 0 && a.CovarianceYY is > 0 && a.CovarianceXX * a.CovarianceYY - a.CovarianceXY * a.CovarianceXY > 0)
            .Select(a => new AstrometricPixelCovariance(a.DetectionIndex, a.CovarianceXX!.Value, a.CovarianceXY!.Value, a.CovarianceYY!.Value)).ToArray();
        var solver = MeasuredAstrometryFixture.Options.Solver!;
        var prior = AstrometricSolver.Solve(f.Context.Frame, f.Calibration, f.Catalog, detections, solver).Assessment;
        Assert.IsTrue(prior.HasMeasuredMapping, prior.Reason);
        var rows = new List<object>();
        var executor = new ProcessingRecipeExecutor();
        MeasuredAstrometrySolutionV1? retained = null;
        byte[]? payload = null;
        foreach (var warm in new[] { false, true })
        {
            var request = f.Request(context: f.Context with { PreviousAccepted = warm ? prior : null });
            foreach (var recipe in new[] { false, true })
            {
                async ValueTask<AstrometricFrameAssessment> Run()
                {
                    if (recipe)
                    {
                        var outcome = await executor.ExecuteAsync(request).ConfigureAwait(false);
                        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
                        payload = outcome.Products.Single().Payload.ToArray();
                        retained = MeasuredAstrometryJson.Parse(payload);
                        return retained.Assessment;
                    }
                    var fit = warm ? AstrometricSolver.Refine(f.Context.Frame, f.Calibration, f.Catalog, detections, prior, solver)
                        : AstrometricSolver.Solve(f.Context.Frame, f.Calibration, f.Catalog, detections, solver);
                    _ = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, solver, fit, detections, covariances: covariances);
                    _ = AstrometricUncertaintyEstimator.Estimate(f.Calibration, f.Catalog, solver, fit, detections, covariances,
                        f.Context.Clock, options: new());
                    _ = AstrometricEvidenceJson.Serialize(fit.Assessment);
                    return fit.Assessment;
                }
                for (var i = 0; i < 3; i++) Assert.IsTrue((await Run().ConfigureAwait(false)).HasMeasuredMapping);
                var timings = new double[10];
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocations = GC.GetTotalAllocatedBytes(true);
                string? assessmentIdentity = null;
                for (var i = 0; i < timings.Length; i++)
                {
                    var clock = Stopwatch.StartNew();
                    var assessment = await Run().ConfigureAwait(false);
                    timings[i] = clock.Elapsed.TotalMilliseconds;
                    Assert.IsTrue(assessment.HasMeasuredMapping, assessment.Reason);
                    assessmentIdentity ??= assessment.IdentitySha256;
                    Assert.AreEqual(assessmentIdentity, assessment.IdentitySha256);
                    if (!warm) Assert.AreEqual(prior.IdentitySha256, assessment.IdentitySha256);
                }
                var allocated = GC.GetTotalAllocatedBytes(true) - allocations;
                process.Refresh();
                rows.Add(new
                {
                    mode = warm ? "warm" : "cold",
                    path = recipe ? "recipe-plus-retained-parse" : "native-solver-diagnostics-uncertainty-evidence",
                    operations = timings.Length,
                    warmup = 3,
                    p50Milliseconds = timings.Order().ElementAt(4),
                    p95Milliseconds = timings.Max(),
                    cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    allocatedBytesPerOperation = allocated / timings.Length,
                    workingSetBytes = process.WorkingSet64,
                    processPeakWorkingSetBytes = process.PeakWorkingSet64,
                    assessmentIdentity
                });
            }
        }
        Assert.IsNotNull(retained); Assert.IsNotNull(payload);
        var report = new
        {
            schema = "issue-1642-reference-resources-v1",
            seed = 934817,
            noiseSeed = 1205,
            noiseSigma = 4,
            width = 512,
            height = 512,
            rawBytes = f.Raw.Payload.Length,
            catalogRows = f.Catalog.Stars.Count,
            qualifiedCentroids = detections.Length,
            usableCovariances = covariances.Length,
            measuredBytes = f.Measured.Payload.Length,
            contextBytes = MeasuredAstrometryJson.ContextInput(f.Context).Payload.Length,
            solutionBytes = payload.Length,
            solutionSha256 = ProcessingIdentity.ComputePayloadSha256(payload),
            retainedIdentity = retained.SolutionIdentitySha256,
            quality = retained.Assessment.Quality,
            uncertaintyReason = retained.UncertaintyReasonCode,
            conditional = retained.Uncertainty!.ConditionalFit.Status,
            total = retained.Uncertainty.Total.Status,
            rows
        };
        var json = JsonSerializer.Serialize(report);
        TestContext.WriteLine(json);
        if (Environment.GetEnvironmentVariable("HVO_ISSUE1642_EVIDENCE") is { } directory && Directory.Exists(directory))
            await File.WriteAllTextAsync(Path.Combine(directory, "reference-resources.json"), json).ConfigureAwait(false);
        Assert.IsLessThan(MeasuredAstrometryJson.MaximumSolutionBytes, payload.Length);
    }
}
