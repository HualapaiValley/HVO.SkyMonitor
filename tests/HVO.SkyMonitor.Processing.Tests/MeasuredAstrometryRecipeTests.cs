using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest test class.")]
public sealed class MeasuredAstrometryRecipeTests
{
    private static readonly Lazy<Task<MeasuredAstrometryFixture>> Fixture = new(MeasuredAstrometryFixture.CreateAsync);
    private static readonly Lazy<Task<MeasuredAstrometrySolutionV1>> Accepted = new(async () =>
        Solution(await new ProcessingRecipeExecutor().ExecuteAsync((await Fixture.Value.ConfigureAwait(false)).Request()).ConfigureAwait(false)));

    private static MeasuredAstrometrySolutionV1 Solution(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        return MeasuredAstrometryJson.Parse(outcome.Products.Single().Payload);
    }

    [TestMethod]
    public async Task RenderedMeasuredInputAcceptsAndMatchesNativeSolverWithoutTruthOrNewAssociation()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var solution = await Accepted.Value.ConfigureAwait(false);
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, solution.Assessment.Status, solution.Assessment.Reason);
        var detections = f.Associations.Associations.Where(a => a.LabelEligible && !a.Saturated && !a.Trailed && a.Flux > 0)
            .Select(a => new AstrometricDetection(a.DetectionIndex, new(a.MeasuredX, a.MeasuredY), a.Flux)).ToArray();
        var direct = AstrometricSolver.Solve(f.Context.Frame, f.Calibration, f.Catalog, detections, MeasuredAstrometryFixture.Options.Solver);
        Assert.AreEqual(direct.Assessment.IdentitySha256, solution.Assessment.IdentitySha256);
        Assert.IsLessThan(.1, solution.Assessment.Quality!.FittingRmsPixels);
        Assert.AreEqual(72, solution.Assessment.Parameters!.BoresightAltitudeDegrees, .01);
        Assert.AreEqual(243, solution.Assessment.Parameters.BoresightAzimuthDegrees, .02);
        Assert.AreEqual(17.04, solution.Assessment.Parameters.RollDegrees, .01);
        Assert.AreEqual(1, solution.Assessment.Parameters.FocalScale, .001);
        Assert.AreEqual(f.Associations.AssociationIdentitySha256, solution.MeasuredIdentitySha256);
        var accelerated = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(raw: f.Raw with
        { ObservationEndedUtc = f.Raw.ObservationStartedUtc })).ConfigureAwait(false));
        Assert.AreEqual(solution.Assessment.IdentitySha256, accelerated.Assessment.IdentitySha256);
        Assert.IsNotNull(solution.Uncertainty);
        Assert.AreNotEqual("available", solution.Uncertainty.Total.Status, "No session covariance was supplied.");
        Assert.IsFalse(solution.Assessment.Quality.IsCalibratedProbability);
    }

    [TestMethod]
    public async Task RetainedDiagnosticsReproduceFitEvidenceAndTypedGeometryWithoutChangingSolution()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var solution = await Accepted.Value.ConfigureAwait(false);
        var bytes = MeasuredAstrometryJson.Serialize(solution);
        var diagnostics = MeasuredAstrometryJson.ReconstructDiagnostics(solution, f.Context, f.Associations, MeasuredAstrometryFixture.Options);
        Assert.AreEqual(solution.Assessment.IdentitySha256, diagnostics.AssessmentIdentitySha256);
        Assert.AreEqual(solution.Residuals.NativeAssociations.Count, diagnostics.Residuals.Count);
        Assert.AreEqual(solution.Residuals.FittingRmsPixels!.Value, diagnostics.FittingRmsPixels!.Value, 1e-10);
        Assert.AreEqual(solution.Residuals.VerificationRmsPixels!.Value, diagnostics.VerificationRmsPixels!.Value, 1e-10);
        var measured = AstrometricDiagnosticPresentation.CreatePayload(solution.SolutionIdentitySha256, diagnostics, 512, 512, false);
        var residuals = AstrometricDiagnosticPresentation.CreatePayload(solution.SolutionIdentitySha256, diagnostics, 512, 512, true);
        Assert.AreEqual(diagnostics.Residuals.Count, measured.Markers.Count);
        Assert.IsEmpty(measured.Segments);
        Assert.AreEqual(diagnostics.Residuals.Count, residuals.Markers.Count);
        for (var i = 0; i < residuals.Segments.Count; i++)
        {
            Assert.AreEqual(diagnostics.Residuals[i].Predicted, residuals.Segments[i].From);
            Assert.AreEqual(diagnostics.Residuals[i].Measured, residuals.Segments[i].To);
        }
        CollectionAssert.AreEqual(bytes, MeasuredAstrometryJson.Serialize(solution));
        Assert.Throws<ArgumentException>(() => AstrometricDiagnosticPresentation.CreatePayload(solution.SolutionIdentitySha256, diagnostics, 2, 2, true));
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ReconstructDiagnostics(solution, f.Context with { CalibrationVersion = "different" }, f.Associations, MeasuredAstrometryFixture.Options));
        Assert.Throws<OperationCanceledException>(() => MeasuredAstrometryJson.ReconstructDiagnostics(solution, f.Context, f.Associations,
            MeasuredAstrometryFixture.Options, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task CanonicalRoundTripRejectsTamperingMissingDuplicateUnknownAndOversizedMembers()
    {
        var solution = await Accepted.Value.ConfigureAwait(false);
        var bytes = MeasuredAstrometryJson.Serialize(solution);
        CollectionAssert.AreEqual(bytes, MeasuredAstrometryJson.Serialize(MeasuredAstrometryJson.Parse(bytes)));
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["Assessment"]!["Parameters"]!["FocalScale"] = 1.05;
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root)));
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Throws<JsonException>(() => MeasuredAstrometryJson.Parse(System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":\"duplicate\"," + json[1..])));
        root = JsonNode.Parse(bytes)!.AsObject(); root.Remove("MeasuredIdentitySha256");
        Assert.Throws<JsonException>(() => MeasuredAstrometryJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root)));
        root = JsonNode.Parse(bytes)!.AsObject(); root["unknown"] = 1;
        Assert.Throws<JsonException>(() => MeasuredAstrometryJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root)));
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.Parse(new byte[MeasuredAstrometryJson.MaximumSolutionBytes + 1]));
        Assert.IsFalse(json.Contains("CatalogStars", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("ExpectedX", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Elapsed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RetainedMappingIsSeparateBoundedAndRequiresExactCalibrationAndMeasuredIdentity()
    {
        var f = await Fixture.Value.ConfigureAwait(false); var solution = await Accepted.Value.ConfigureAwait(false);
        var mapping = MeasuredAstrometryJson.ReconstructMapping(solution, f.Calibration, f.Associations);
        var pixel = new PixelPoint(280, 240);
        var sky = MeasuredAstrometryJson.ApplyPixels(mapping, [pixel]).Single()!;
        var roundtrip = mapping.SkyToPixel(sky.EquatorialMeanOfDate)!.Value;
        Assert.AreEqual(pixel.X, roundtrip.X, 1e-7); Assert.AreEqual(pixel.Y, roundtrip.Y, 1e-7);
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ReconstructMapping(solution,
            new(f.Calibration.Projection, "other", f.Calibration.ReadoutIdentitySha256), f.Associations));
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ReconstructMapping(solution, f.Calibration,
            f.Associations with { Measurement = f.Associations.Measurement with { AlgorithmVersion = "other" } }));
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ApplyPixels(mapping, new PixelPoint[4097]));
        Assert.Throws<OperationCanceledException>(() => MeasuredAstrometryJson.ApplyPixels(mapping, [pixel], new CancellationToken(true)));
    }

    [TestMethod]
    public async Task NoMeasuredDataAndSparseInputRetainRejectedOutcomesWithoutMapping()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        foreach (var n in new[] { 0, 7 })
        {
            var ids = f.Associations.Associations.Take(n).Select(a => a.DetectionIndex).ToHashSet();
            var sparse = f.Keep(a => ids.Contains(a.DetectionIndex));
            var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(sparse.Request()).ConfigureAwait(false));
            Assert.AreEqual(AstrometricAssessmentStatus.Rejected, value.Assessment.Status);
            Assert.AreEqual("insufficient-detections", value.Assessment.ReasonCode);
            Assert.IsNull(value.Assessment.Parameters); Assert.AreEqual(sparse.Associations.Associations.Count(a => a.LabelEligible && !a.Saturated && !a.Trailed), value.Residuals.OutlierDetectionIndexes.Count);
            Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ReconstructMapping(value, f.Calibration, sparse.Associations));
        }
    }

    [TestMethod]
    public async Task IncompleteMeasurementRetainsUnavailableInsteadOfTreatingPartialDataAsComplete()
    {
        var f = await Fixture.Value.ConfigureAwait(false); var empty = f.Keep(_ => false);
        empty = empty.WithAssociations(empty.Associations with
        {
            Measurement = empty.Associations.Measurement with
            {
                Status = HVO.SkyMonitor.Imaging.StellarMeasurementStatus.CandidateBudgetExceeded,
                DetectionCount = 0,
                ExclusionCount = 0
            },
            UnassociatedDetections = [],
            UnmatchedPredictions = empty.Associations.UnmatchedPredictions.Select(a => a with
            { ReasonCode = MeasuredStellarAssociationReasonCodes.MeasurementIncomplete }).ToArray()
        });
        var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(empty.Request()).ConfigureAwait(false));
        Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, value.Assessment.Status);
        Assert.AreEqual("measurement-incomplete", value.Assessment.ReasonCode); Assert.IsNull(value.Assessment.Parameters);
    }

    [TestMethod]
    public async Task MissingMalformedOrMismatchedMeasuredEvidenceFailsBeforeFitting()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var request = f.Request();
        foreach (var bad in new[]
        {
            request with { AuxiliaryInputs = request.AuxiliaryInputs!.Where(a => a.Kind != ProcessingAuxiliaryInputKind.Artifact).ToArray() },
            f.Request(context: f.Context with { MeasuredIdentitySha256 = MeasuredAstrometryFixture.Hash("other") }),
            f.Request(measured: f.Measured with { Payload = "{}"u8.ToArray() }),
            f.Request(measured: f.Measured with { SourceArtifactIds = [Guid.NewGuid()] }),
            f.Request(raw: f.Raw with { DescriptorIdentitySha256 = MeasuredAstrometryFixture.Hash("wrong-descriptor") }),
            f.Request(raw: f.Raw with { Payload = new byte[f.Raw.Payload.Length] })
        })
        {
            var result = await new ProcessingRecipeExecutor().ExecuteAsync(bad).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, result.Status); Assert.IsEmpty(result.Products);
        }
    }

    [TestMethod]
    public async Task BadCatalogTimeObserverCalibrationReadoutLineageFailsClosed()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var contexts = new[]
        {
            f.Context with { CatalogMetadata = f.Context.CatalogMetadata with { Checksum = MeasuredAstrometryFixture.Hash("wrong-catalog") } },
            f.Context with { Frame = f.Context.Frame with { ExposureStartUtc = f.Context.Frame.ExposureStartUtc.AddSeconds(1), ExposureEndUtc = f.Context.Frame.ExposureEndUtc.AddSeconds(1) } },
            f.Context with { Frame = f.Context.Frame with { Observer = new(1, 0, 0) } },
            f.Context with { CalibrationVersion = "wrong-calibration" },
            f.Context with { Projection = f.Context.Projection with { PrincipalPointX = 257 } },
            f.Context with { ReadoutIdentitySha256 = MeasuredAstrometryFixture.Hash("wrong-readout") },
            f.Context with { CatalogPackage = f.Context.CatalogPackage with { PreprocessingVersion = "wrong-preprocessing" } },
            f.Context with { Frame = f.Context.Frame with { DetectionSettingsIdentitySha256 = MeasuredAstrometryFixture.Hash("wrong-detector") } }
        };
        foreach (var context in contexts)
        {
            var result = await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(context)).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, result.Status, result.ReasonCode);
        }
    }

    [TestMethod]
    public async Task IncompleteOrUnsupportedCatalogRetainsUnavailableWithExactSelectionIdentity()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        foreach (var context in new[] { f.Context with { CatalogComplete = false }, f.Context with { CompletenessMagnitudeLimit = 6 },
                     f.Context with { CatalogCoordinateModel = "unsupported" } })
        {
            var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(context)).ConfigureAwait(false));
            Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, value.Assessment.Status);
            Assert.IsNull(value.Assessment.Parameters);
        }
    }

    [TestMethod]
    public async Task ClusteredAndDegenerateSourcesCannotPassIndependentCoverageAndVerification()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        foreach (var subset in new[] { f.Keep(a => a.MeasuredX < 200 && a.MeasuredY < 220), f.Keep(a => Math.Abs(a.MeasuredY - 256) < 15) })
        {
            var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(subset.Request()).ConfigureAwait(false));
            Assert.AreNotEqual(AstrometricAssessmentStatus.Accepted, value.Assessment.Status);
            Assert.IsNull(value.Assessment.Parameters);
        }
    }

    [TestMethod]
    public async Task DisplacedOutliersRemainExplicitWithoutChangingAcceptedPrior()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var changed = f.WithAssociations(f.Associations with
        {
            Associations = f.Associations.Associations.Select((a, i) =>
            i % 10 == 0 ? a with { MeasuredX = a.MeasuredX + 1.8 } : a).ToArray()
        });
        var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(changed.Request()).ConfigureAwait(false));
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, value.Assessment.Status, value.Assessment.Reason);
        Assert.IsNotEmpty(value.Residuals.OutlierDetectionIndexes);
        Assert.AreEqual(value.Residuals.QualifiedCount, value.Residuals.FittingDetectionIndexes.Count +
            value.Residuals.VerificationDetectionIndexes.Count + value.Residuals.OutlierDetectionIndexes.Count);
    }

    [TestMethod]
    public async Task MislabeledCatalogCorrespondencesCannotBecomeAcceptedMapping()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var entries = f.Associations.Associations.ToArray();
        var first = entries[0]; var second = entries[1];
        entries[0] = first with { MeasuredX = second.MeasuredX, MeasuredY = second.MeasuredY, DetectionIndex = second.DetectionIndex };
        entries[1] = second with { MeasuredX = first.MeasuredX, MeasuredY = first.MeasuredY, DetectionIndex = first.DetectionIndex };
        var changed = f.WithAssociations(f.Associations with { Associations = entries });
        var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(changed.Request()).ConfigureAwait(false));
        Assert.AreEqual(AstrometricAssessmentStatus.Rejected, value.Assessment.Status);
        Assert.AreEqual("measured-catalog-correspondence-mismatch", value.Assessment.ReasonCode);
    }

    [TestMethod]
    public async Task CallerCancellationAndWorkBudgetsAreBoundedAndNeverReturnMapping()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ProcessingRecipeExecutor().ExecuteAsync(
            f.Request(), new CancellationToken(true)).ConfigureAwait(false)).ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(5));
        var cancellationTimer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ProcessingRecipeExecutor().ExecuteAsync(
            f.Request(), cancel.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsLessThan(3000, cancellationTimer.Elapsed.TotalMilliseconds);
        var timer = Stopwatch.StartNew();
        var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(options: MeasuredAstrometryFixture.Options with
        { Solver = MeasuredAstrometryFixture.Options.Solver! with { ColdBudgetMilliseconds = .000001 } })).ConfigureAwait(false));
        Assert.AreEqual(AstrometricAssessmentStatus.BudgetExceeded, value.Assessment.Status);
        Assert.IsLessThan(3000, timer.Elapsed.TotalMilliseconds); Assert.IsNull(value.Assessment.Parameters);
        value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(options: MeasuredAstrometryFixture.Options with
        { MaximumExecutionMilliseconds = .000001 })).ConfigureAwait(false));
        Assert.AreEqual(AstrometricAssessmentStatus.BudgetExceeded, value.Assessment.Status); Assert.IsNull(value.Assessment.Parameters);
    }

    [TestMethod]
    public async Task CompatibleWarmPriorIsDeterministicAndNeverMutatesRetainedEvidence()
    {
        var f = await MeasuredAstrometryFixture.CreateAtAsync(2).ConfigureAwait(false); var prior = await Accepted.Value.ConfigureAwait(false);
        Assert.AreNotEqual(prior.Assessment.Frame.CaptureId, f.Context.Frame.CaptureId);
        var context = f.Context with { PreviousAccepted = prior.Assessment };
        var before = AstrometricEvidenceJson.Serialize(prior.Assessment);
        var a = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(context)).ConfigureAwait(false));
        var b = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(context)).ConfigureAwait(false));
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, a.Assessment.Status, a.Assessment.Reason);
        Assert.AreEqual(AstrometricSolveMode.Warm, a.Assessment.Mode);
        Assert.AreEqual(prior.Assessment.IdentitySha256, a.Assessment.PreviousAssessmentIdentitySha256);
        Assert.AreEqual(a.SolutionIdentitySha256, b.SolutionIdentitySha256);
        CollectionAssert.AreEqual(before, AstrometricEvidenceJson.Serialize(prior.Assessment));
    }

    [TestMethod]
    public async Task IncompatibleWarmSettingsAndCatalogPackageAreRejectedWithoutColdFallback()
    {
        var f = await Fixture.Value.ConfigureAwait(false); var prior = await Accepted.Value.ConfigureAwait(false);
        var context = f.Context with { PreviousAccepted = prior.Assessment };
        foreach (var request in new[]
        {
            f.Request(context, options: MeasuredAstrometryFixture.Options with { Solver = MeasuredAstrometryFixture.Options.Solver! with { MaximumWarmAgeSeconds = 1 } }),
            f.Request(context with { CatalogPackage = context.CatalogPackage with { PackageVersion = "2" } })
        })
        {
            var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false));
            Assert.AreEqual("warm-context-incompatible", value.Assessment.ReasonCode);
            Assert.AreEqual(AstrometricSolveMode.Warm, value.Assessment.Mode); Assert.IsNull(value.Assessment.Parameters);
        }
    }

    [TestMethod]
    public async Task RegisteredMetadataContractPinsRecipeSourcesAndSemanticContent()
    {
        var f = await Fixture.Value.ConfigureAwait(false); var request = f.Request();
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        var product = outcome.Products.Single();
        var contract = BuiltInProcessingRecipes.CreateProductContract(request, product.Recipe);
        Assert.IsTrue(StructuredProcessingProductContracts.IsSupported(product.MediaType, product.SchemaVersion!));
        CollectionAssert.AreEqual(new[] { f.Raw.ArtifactId, f.Measured.ArtifactId }, product.SourceArtifactIds.ToArray());
        Assert.IsTrue(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request, contract, product.Payload, product.ContentIdentitySha256,
            product.Recipe.IdentitySha256, product.Algorithms));
        Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request, contract, product.Payload, MeasuredAstrometryFixture.Hash("wrong"),
            product.Recipe.IdentitySha256, product.Algorithms));
        var wrongRaw = f.Request(raw: f.Raw with { Payload = new byte[f.Raw.Payload.Length] });
        Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(wrongRaw,
            BuiltInProcessingRecipes.CreateProductContract(wrongRaw, product.Recipe), product.Payload, product.ContentIdentitySha256,
            product.Recipe.IdentitySha256, product.Algorithms));
        var changed = f.Request(context: f.Context with { CatalogPackage = f.Context.CatalogPackage with { PackageVersion = "2" } });
        var changedOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(changed).ConfigureAwait(false);
        Assert.AreNotEqual(product.Recipe.IdentitySha256, changedOutcome.Products.Single().Recipe.IdentitySha256);
    }
    [TestMethod]
    public async Task RoundedOrMissingCovarianceIsWithheldWithoutInventingPrecision()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        foreach (var missing in new[] { true, false })
        {
            var changed = f.WithAssociations(f.Associations with
            {
                Associations = f.Associations.Associations.Select(a => a with
                { CovarianceXX = missing ? null : 0, CovarianceXY = missing ? null : 0, CovarianceYY = missing ? null : 0 }).ToArray()
            });
            var value = Solution(await new ProcessingRecipeExecutor().ExecuteAsync(changed.Request()).ConfigureAwait(false));
            Assert.AreEqual(AstrometricAssessmentStatus.Accepted, value.Assessment.Status, value.Assessment.Reason);
            Assert.AreEqual("withheld", value.Uncertainty!.ConditionalFit.Status);
            Assert.AreNotEqual("available", value.UncertaintyReasonCode);
        }
    }

    [TestMethod]
    public async Task ChangedCatalogCoordinatesWithSameIdsAndPackageCannotReuseSceneLineage()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var context = f.Context with
        {
            CatalogStars = f.Context.CatalogStars.Select(s => s with
            { RightAscensionHours = (s.RightAscensionHours + .01) % 24 }).ToArray()
        };
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(f.Request(context)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual("astrometry.catalog-selection-mismatch", outcome.ReasonCode);
    }

    [TestMethod]
    public async Task PayloadValidatorRejectsValidEvidenceFromAnotherCaptureDespiteRehashedContent()
    {
        var f = await Fixture.Value.ConfigureAwait(false); var request = f.Request();
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        var product = outcome.Products.Single(); var solution = MeasuredAstrometryJson.Parse(product.Payload);
        var assessment = solution.Assessment with
        {
            IdentitySha256 = string.Empty,
            Frame = solution.Assessment.Frame with { CaptureId = Guid.NewGuid() }
        };
        assessment = assessment with
        {
            IdentitySha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(assessment)))
        };
        // Keep the native uncertainty lineage valid too; a native self-hash alone cannot bind a Processing request.
        var uncertainty = solution.Uncertainty! with { IdentitySha256 = string.Empty, AssessmentIdentitySha256 = assessment.IdentitySha256 };
        uncertainty = uncertainty with
        {
            IdentitySha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(uncertainty)))
        };
        var changed = solution with { Assessment = assessment, Uncertainty = uncertainty };
        var bytes = MeasuredAstrometryJson.Serialize(changed);
        Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request,
            BuiltInProcessingRecipes.CreateProductContract(request, product.Recipe), bytes, changed.SolutionIdentitySha256,
            product.Recipe.IdentitySha256, product.Algorithms));
    }

}
