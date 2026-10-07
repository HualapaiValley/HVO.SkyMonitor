using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "MSTest constructs this class through the assembly's DiscoverInternals contract.")]
internal sealed class AstrometricCatalogSourceTests
{
    [TestMethod]
    public async Task InstalledFixturePreservesPackageIdentityWithoutClaimingCompleteSky()
    {
        using var installation = CatalogSnapshotResolverTests.CreateInstallation();
        var snapshot = CatalogSnapshotResolver.Resolve(new(installation.Root, "hyg-v42-fixture")
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture
        });
        var data = await snapshot.Catalog.ReadAsync(7, 2500).ConfigureAwait(false);

        Assert.HasCount(9, data.Stars);
        Assert.IsFalse(data.IsCompleteForRequestedMagnitude);
        Assert.AreEqual(7, data.CompletenessMagnitudeLimit);
        Assert.AreEqual(snapshot.Catalog.Metadata, data.Metadata);
        Assert.AreEqual(new AstrometricCatalogProvenance(snapshot.CatalogId, snapshot.SnapshotVersion,
            "fixture", snapshot.PreprocessingVersion), data.Provenance);
        Assert.AreEqual(AstrometricConventions.CoordinateModel, data.CoordinateModel);
    }

    [TestMethod]
    public async Task DirectSnapshotLoadCannotClaimApprovedGlobalCompleteness()
    {
        var catalog = DirectFixture();
        var data = await catalog.ReadAsync(7, 2500).ConfigureAwait(false);

        Assert.HasCount(9, data.Stars);
        Assert.IsNull(data.Provenance);
        Assert.IsFalse(data.IsCompleteForRequestedMagnitude);
    }

    [TestMethod]
    public async Task EntryBoundPreservesBrightnessOrderingAndExactMagnitudeBoundary()
    {
        var catalog = DirectFixture();
        var all = catalog.Query(new(7, 100));
        var magnitude = all[2].Magnitude;
        var selected = await catalog.ReadAsync(magnitude, 2).ConfigureAwait(false);
        var repeated = await catalog.ReadAsync(magnitude, 2).ConfigureAwait(false);

        CollectionAssert.AreEqual(all.Take(2).Select(x => x.Id).ToArray(), selected.Stars.Select(x => x.Id).ToArray());
        Assert.AreEqual(magnitude, selected.CompletenessMagnitudeLimit);
        Assert.AreEqual(selected.SelectionIdentitySha256, repeated.SelectionIdentitySha256);
        Assert.AreEqual(selected.IdentitySha256, repeated.IdentitySha256);
        var exact = await catalog.ReadAsync(magnitude, 3).ConfigureAwait(false);
        Assert.HasCount(3, exact.Stars);
        Assert.AreEqual(all[2].Id, exact.Stars[2].Id);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(double.PositiveInfinity)]
    public async Task NonfiniteMagnitudeIsRejected(double magnitude)
    {
        var catalog = DirectFixture();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await catalog.ReadAsync(magnitude, 2500).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(AstrometricCatalogData.MaterializationCeiling + 1)]
    [DataRow(int.MaxValue)]
    public async Task UnsupportedEntryBoundIsRejected(int maximumEntries)
    {
        var catalog = DirectFixture();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await catalog.ReadAsync(5, maximumEntries).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(AstrometricCatalogData.MaximumEntries + 1)]
    [DataRow(AstrometricCatalogData.MaterializationCeiling)]
    public async Task DeepSelectionBoundsUpToTheCeilingAreReadable(int maximumEntries)
    {
        var catalog = DirectFixture();
        var selection = await catalog.ReadAsync(7, maximumEntries).ConfigureAwait(false);

        Assert.HasCount(catalog.ObjectCount, selection.Stars);
        Assert.IsFalse(selection.IsCompleteForRequestedMagnitude, "A direct fixture load never declares complete coverage.");
    }

    [TestMethod]
    public async Task NegativeMagnitudeAndEmptySelectionsRemainValidRequests()
    {
        var catalog = DirectFixture();
        var bright = await catalog.ReadAsync(-1, 2500).ConfigureAwait(false);
        var empty = await catalog.ReadAsync(-100, 2500).ConfigureAwait(false);
        Assert.HasCount(1, bright.Stars);
        Assert.IsEmpty(empty.Stars);
        Assert.AreEqual(-100, empty.CompletenessMagnitudeLimit);
    }

    [TestMethod]
    public async Task CancellationIsObservedEvenForEmptySelection()
    {
        var catalog = DirectFixture();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await catalog.ReadAsync(-100, 1, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SolverRefusesFixtureCompletenessBeforeAttemptingNumericalFit()
    {
        var catalog = DirectFixture();
        var hash = new string('0', 64);
        var utc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var frame = new AstrometricFrameContext(Guid.NewGuid(), Guid.NewGuid(), hash, hash, hash,
            new(35, -114, 1000), utc, utc.AddSeconds(1), "test-measurements", hash);
        var calibration = new AstrometricCalibration(new(ProjectionModel.EquidistantFisheye,
            250, 250, 150, 150, 500, 500, ProjectionAperture.Circular, 240, EnforceSensorBounds: true), "fixture-calibration", hash);
        var result = await AstrometricSolver.SolveAsync(frame, calibration, catalog, [],
            new(MaximumCatalogMagnitude: 5)).ConfigureAwait(false);

        Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, result.Assessment.Status);
        Assert.AreEqual("catalog-incomplete", result.Assessment.ReasonCode);
        Assert.AreEqual(0, result.Metrics.Hypotheses);
        Assert.IsFalse(result.Assessment.HasMeasuredMapping);
    }

    private static SqliteCelestialCatalog DirectFixture() => new(new(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"),
        "F80689217769A6B13C1B9BFB9711485D3CB1AD8DE009D3D6B0F0B0A4F1FA9840", "2", "3", 9));
}
