using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest test class.")]
public sealed class MeasuredAstrometryIntegrityTests
{
    private static readonly Lazy<Task<MeasuredAstrometryFixture>> Fixture = new(MeasuredAstrometryFixture.CreateAsync);
    private static readonly Lazy<Task<ProcessingProduct>> Product = new(async () =>
    {
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync((await Fixture.Value.ConfigureAwait(false)).Request()).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        return outcome.Products.Single();
    });

    [TestMethod]
    [DataRow("nonexistent")]
    [DataRow("fitting-outlier")]
    [DataRow("fitting-verification")]
    public async Task F1PublicationAndReconstructionRejectInventedOrReassignedMembership(string mutation)
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        var r = solution.Residuals;
        if (mutation == "nonexistent")
            r = r with
            {
                FittingDetectionIndexes = Enumerable.Range(3000, r.FittingDetectionIndexes.Count).ToArray(),
                VerificationDetectionIndexes = Enumerable.Range(3200, r.VerificationDetectionIndexes.Count).ToArray(),
                OutlierDetectionIndexes = Enumerable.Range(3400, r.OutlierDetectionIndexes.Count).ToArray()
            };
        else
        {
            var fitting = mutation == "fitting-outlier" ? 5 : r.FittingDetectionIndexes[0];
            var other = mutation == "fitting-outlier" ? 41 : r.VerificationDetectionIndexes[0];
            CollectionAssert.Contains(r.FittingDetectionIndexes.ToArray(), fitting);
            CollectionAssert.Contains((mutation == "fitting-outlier" ? r.OutlierDetectionIndexes : r.VerificationDetectionIndexes).ToArray(), other);
            int[] Swap(IReadOnlyList<int> values, int from, int to) => values.Select(i => i == from ? to : i).Order().ToArray();
            r = r with
            {
                FittingDetectionIndexes = Swap(r.FittingDetectionIndexes, fitting, other),
                OutlierDetectionIndexes = mutation == "fitting-outlier" ? Swap(r.OutlierDetectionIndexes, other, fitting) : r.OutlierDetectionIndexes,
                VerificationDetectionIndexes = mutation == "fitting-verification" ? Swap(r.VerificationDetectionIndexes, other, fitting) : r.VerificationDetectionIndexes
            };
        }
        var changed = solution with { Residuals = r };
        AssertRejected(f.Request(), product, changed, mutation);
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ReconstructMapping(changed, f.Calibration, f.Associations));
    }

    [TestMethod]
    public async Task F2PublicationRejectsAvailableTotalWithWithheldContributorsDespiteValidNativeHash()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        Assert.AreEqual("withheld", solution.Uncertainty!.ConditionalFit.Status);
        Assert.AreEqual("withheld", solution.Uncertainty.SharedCalibration.Status);
        var u = solution.Uncertainty with
        {
            Status = "available",
            ReasonCode = "available",
            Reason = "available",
            Total = new("available", "available", new double[16], 0, 0, 0)
        };
        AssertRejected(f.Request(), product, Rehash(solution, u), "available total with missing contributors");
    }

    [TestMethod]
    [DataRow("measurement")]
    [DataRow("readout")]
    [DataRow("time-scale")]
    [DataRow("midpoint")]
    [DataRow("pixel-convention")]
    [DataRow("epoch")]
    [DataRow("all-reviewer-bindings")]
    public async Task F2PublicationRejectsRehashedUncertaintyWithDifferentFrozenInputs(string mutation)
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        var u = solution.Uncertainty!;
        u = mutation switch
        {
            "measurement" => u with { MeasurementInputIdentitySha256 = new string('a', 64) },
            "readout" => u with { ReadoutIdentitySha256 = new string('b', 64) },
            "time-scale" => u with { Conventions = u.Conventions with { TimeScale = "tai" } },
            "midpoint" => u with { Conventions = u.Conventions with { MidpointUtc = u.Conventions.MidpointUtc.AddHours(1) } },
            "pixel-convention" => u with { Conventions = u.Conventions with { PixelCoordinates = "pixel-center" } },
            "epoch" => u with { Conventions = u.Conventions with { CatalogEpoch = "J2050.0" } },
            _ => u with
            {
                MeasurementInputIdentitySha256 = new string('a', 64),
                ReadoutIdentitySha256 = new string('b', 64),
                Conventions = u.Conventions with { TimeScale = "tai", MidpointUtc = u.Conventions.MidpointUtc.AddHours(1) }
            }
        };
        AssertRejected(f.Request(), product, Rehash(solution, u), mutation);
    }

    [TestMethod]
    [DataRow("dimension")]
    [DataRow("asymmetric")]
    [DataRow("indefinite")]
    [DataRow("negative-diagonal")]
    [DataRow("negative-error")]
    [DataRow("withheld-values")]
    [DataRow("missing-error")]
    public async Task F2PublicationRejectsMalformedCovarianceAndComponentClaims(string mutation)
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        var matrix = new double[16];
        for (var i = 0; i < 4; i++) matrix[i * 4 + i] = 1;
        if (mutation == "asymmetric") matrix[1] = .1;
        if (mutation == "indefinite") matrix[1] = matrix[4] = 2;
        if (mutation == "negative-diagonal") matrix[0] = -1;
        var component = new AstrometricPoseCovarianceComponent("available", "available",
            mutation == "dimension" ? new double[15] : matrix, 1, 1, 1);
        if (mutation == "negative-error") component = component with { RollStandardErrorDegrees = -1 };
        if (mutation == "withheld-values") component = component with { Status = "withheld" };
        if (mutation == "missing-error") component = component with { FocalScaleStandardError = null };
        AssertRejected(f.Request(), product, Rehash(solution, solution.Uncertainty! with { ConditionalFit = component }), mutation);
    }

    [TestMethod]
    [DataRow("missing-descriptor")]
    [DataRow("name")]
    [DataRow("version")]
    [DataRow("implementation")]
    [DataRow("descriptor-hash")]
    [DataRow("kind")]
    [DataRow("role")]
    [DataRow("media")]
    [DataRow("schema")]
    [DataRow("parent")]
    [DataRow("missing-parent")]
    [DataRow("missing-content")]
    public async Task F3PublicationRejectsEveryMeasuredProducerEnvelopeRejectedByExecution(string mutation)
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var measured = mutation switch
        {
            "missing-descriptor" => f.Measured with { RecipeDescriptor = null },
            "name" => f.Measured with { RecipeDescriptor = f.Measured.RecipeDescriptor! with { Name = "image-quality" } },
            "version" => f.Measured with { RecipeDescriptor = f.Measured.RecipeDescriptor! with { SemanticVersion = "2.0.0" } },
            "implementation" => f.Measured with { RecipeDescriptor = f.Measured.RecipeDescriptor! with { ImplementationVersion = "unsupported" } },
            "descriptor-hash" => f.Measured with { RecipeDescriptor = f.Measured.RecipeDescriptor! with { Options = JsonSerializer.SerializeToElement(new { changed = true }) } },
            "kind" => f.Measured with { ProductKind = ProcessingProductKind.PixelData },
            "role" => f.Measured with { Role = FrameArtifactRole.Preview },
            "media" => f.Measured with { MediaType = "application/json" },
            "schema" => f.Measured with { SchemaVersion = "unsupported" },
            "parent" => f.Measured with { SourceArtifactIds = [Guid.NewGuid()] },
            "missing-parent" => f.Measured with { SourceArtifactIds = [] },
            _ => f.Measured with { ContentIdentitySha256 = null }
        };
        var request = f.Request(measured: measured);
        var execution = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, execution.Status, mutation);
        Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request,
            BuiltInProcessingRecipes.CreateProductContract(f.Request(), product.Recipe), product.Payload,
            product.ContentIdentitySha256, product.Recipe.IdentitySha256, product.Algorithms), mutation);
    }

    [TestMethod]
    public async Task AvailableConditionalEvidenceWithMissingSharedCalibrationAndClockStillPublishes()
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var usable = f.Keep(a => a.CovarianceXX is > 0 && a.CovarianceYY is > 0 &&
            a.CovarianceXX * a.CovarianceYY - a.CovarianceXY * a.CovarianceXY > 0);
        var request = usable.Request();
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        Assert.AreEqual("available", solution.Uncertainty!.Status, solution.Uncertainty.Reason);
        Assert.AreEqual("withheld", solution.Uncertainty.Total.Status);
        Assert.AreEqual("withheld", solution.Uncertainty.ClockComponent.Status);
        Assert.IsTrue(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request,
            BuiltInProcessingRecipes.CreateProductContract(request, product.Recipe), product.Payload,
            product.ContentIdentitySha256, product.Recipe.IdentitySha256, product.Algorithms));
        Assert.IsNotNull(MeasuredAstrometryJson.ReconstructMapping(solution, usable.Calibration, usable.Associations));
    }

    [TestMethod]
    [DataRow("residual-precision")]
    [DataRow("catalog-id")]
    [DataRow("verification")]
    public async Task F1NativeAssociationEvidenceIsBoundWithoutRecomputingResiduals(string mutation)
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        var associations = solution.Residuals.NativeAssociations.ToArray();
        var first = associations[0];
        associations[0] = mutation switch
        {
            "residual-precision" => first with { ResidualPixels = Math.BitIncrement(first.ResidualPixels) },
            "catalog-id" => first with { CatalogId = "different" },
            _ => first with { Verification = !first.Verification }
        };
        AssertRejected(f.Request(), product, solution with { Residuals = solution.Residuals with { NativeAssociations = associations } }, mutation);
    }

    [TestMethod]
    [DataRow("nonexistent")]
    [DataRow("catalog-mismatch")]
    public async Task F1PublicationBindsSelfConsistentNativeAssociationsToQualifiedMeasuredRows(string mutation)
    {
        var f = await Fixture.Value.ConfigureAwait(false);
        var product = await Product.Value.ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        var r = solution.Residuals;
        var all = r.FittingDetectionIndexes.Concat(r.VerificationDetectionIndexes).Concat(r.OutlierDetectionIndexes).ToArray();
        var translated = all.Select((id, i) => (id, replacement: 3000 + i)).ToDictionary(a => a.id, a => a.replacement);
        int Index(int id) => mutation == "nonexistent" ? translated[id] : id;
        r = r with
        {
            FittingDetectionIndexes = r.FittingDetectionIndexes.Select(Index).Order().ToArray(),
            VerificationDetectionIndexes = r.VerificationDetectionIndexes.Select(Index).Order().ToArray(),
            OutlierDetectionIndexes = r.OutlierDetectionIndexes.Select(Index).Order().ToArray(),
            NativeAssociations = r.NativeAssociations.Select(a => a with
            {
                DetectionIndex = Index(a.DetectionIndex),
                CatalogId = mutation == "catalog-mismatch" ? "wrong-" + a.CatalogId : a.CatalogId
            }).ToArray()
        };
        var assessment = solution.Assessment with
        {
            IdentitySha256 = string.Empty,
            AssociationIdentitySha256 = NativeHash(r.NativeAssociations)
        };
        assessment = assessment with { IdentitySha256 = NativeHash(assessment) };
        var changed = Rehash(solution with { Assessment = assessment, Residuals = r },
            solution.Uncertainty! with { AssessmentIdentitySha256 = assessment.IdentitySha256 });
        var bytes = MeasuredAstrometryJson.Serialize(changed);
        Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(f.Request(),
            BuiltInProcessingRecipes.CreateProductContract(f.Request(), product.Recipe), bytes, changed.SolutionIdentitySha256,
            product.Recipe.IdentitySha256, product.Algorithms), mutation);
        Assert.Throws<ArgumentException>(() => MeasuredAstrometryJson.ReconstructMapping(changed, f.Calibration, f.Associations));
    }

    private static string NativeHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static MeasuredAstrometrySolutionV1 Rehash(MeasuredAstrometrySolutionV1 solution, AstrometricFrameUncertainty uncertainty)
    {
        uncertainty = uncertainty with { IdentitySha256 = string.Empty };
        uncertainty = uncertainty with { IdentitySha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(uncertainty))) };
        return solution with { Uncertainty = uncertainty, UncertaintyReasonCode = uncertainty.ReasonCode };
    }

    private static void AssertRejected(ProcessingExecutionRequest request, ProcessingProduct product, MeasuredAstrometrySolutionV1 changed, string message)
    {
        byte[] bytes;
        try { bytes = MeasuredAstrometryJson.Serialize(changed); }
        catch (ArgumentException) { return; }
        Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request,
            BuiltInProcessingRecipes.CreateProductContract(request, product.Recipe), bytes, changed.SolutionIdentitySha256,
            product.Recipe.IdentitySha256, product.Algorithms), message);
    }
}
