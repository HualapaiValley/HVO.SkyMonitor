using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods do not require context-free continuations.")]
public sealed class SqliteNightlyProductStoreTests : IDisposable
{
    private static readonly string FingerprintOne = new('1', 64);
    private static readonly string FingerprintTwo = new('2', 64);
    private static readonly HVO.SkyMonitor.CameraAgent.Common.Automation.LocalAutomationOccurrence Occurrence =
        NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, windowKind: HVO.SkyMonitor.CameraAgent.Common.Automation.LocalAutomationSourceWindowKind.CompletedCivilHour,
            hourStart: new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero));
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);

    private string _root = null!;
    private SqliteNightlyProductStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-store");
        _store = new SqliteNightlyProductStore(
            NightlyProductFixture.HostOptions(_root, NightlyProductFixture.Options()),
            new NightlyClock(new DateTimeOffset(2026, 10, 2, 19, 10, 0, TimeSpan.Zero)));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Publish_WritesFilesAndExactLineage_AndReadsBackVerified()
    {
        var (product, lineage) = await StarTrail(1, 2, 3);

        var detail = await _store.PublishAsync(Publication(product, lineage), CancellationToken.None);

        Assert.AreEqual(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), detail.Summary.ProductId);
        Assert.AreEqual(product.ChecksumSha256.ToUpperInvariant(), detail.PayloadSha256);
        Assert.AreEqual(3, detail.Summary.SourceCount);
        Assert.AreEqual(lineage[0].ObservationStartedUtc, detail.Summary.FirstObservationUtc);
        Assert.AreEqual(lineage[^1].ObservationStartedUtc, detail.Summary.LastObservationUtc);
        CollectionAssert.AreEqual(lineage.ToArray(), detail.Sources.ToArray());
        var stem = detail.Summary.ProductId.ToString("N");
        var files = Directory.GetFiles(Path.Combine(_root, SqliteNightlyProductStore.ProductDirectoryName), stem + "*",
            SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { stem + ".bin", stem + ".jpg", stem + ".provenance.json" }, files);

        var stored = await _store.ReadStoredProductAsync(detail.Summary.ProductId, CancellationToken.None);
        CollectionAssert.AreEqual(product.Payload.ToArray(), stored.Payload.ToArray());
        Assert.AreEqual(product.Compatibility, stored.Compatibility);
        var rendition = await _store.OpenRenditionAsync(detail.Summary.ProductId, CancellationToken.None);
        Assert.AreEqual(SqliteNightlyProductStore.RenditionMediaType, rendition!.MediaType);
        Assert.AreEqual(0xFF, rendition.Content.Span[0]);
        Assert.AreEqual(0xD8, rendition.Content.Span[1]);
        using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(Directory.GetFiles(
            _root, stem + ".provenance.json", SearchOption.AllDirectories)[0]));
        Assert.AreEqual(product.OutputIdentitySha256, provenance.RootElement.GetProperty("outputIdentitySha256").GetString());
        Assert.AreEqual(3, provenance.RootElement.GetProperty("sources").GetArrayLength());
    }

    [TestMethod]
    public async Task Republish_OfTheSameIdentity_ReturnsTheExistingProduct_AndADifferentPayloadIsRefused()
    {
        var (product, lineage) = await StarTrail(1, 2);
        var first = await _store.PublishAsync(Publication(product, lineage), CancellationToken.None);

        var again = await _store.PublishAsync(Publication(product, lineage), CancellationToken.None);
        Assert.AreEqual(first.Summary, again.Summary);
        CollectionAssert.AreEqual(first.Sources.ToArray(), again.Sources.ToArray());

        var payload = product.Payload.ToArray();
        payload[0] ^= 0xFF;
        var tampered = product with { Payload = payload, ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload) };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await _store.PublishAsync(Publication(tampered, lineage), CancellationToken.None));
    }

    [TestMethod]
    public async Task Publish_RejectsLineageThatDoesNotMatchTheProduct()
    {
        var (product, lineage) = await StarTrail(1, 2);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await _store.PublishAsync(Publication(product, [.. lineage.Reverse()]), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await _store.PublishAsync(Publication(product, [lineage[0]]), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await _store.PublishAsync(Publication(product with { ChecksumSha256 = new string('0', 64) }, lineage),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Publish_RejectsANightlyProductSourceThatIsNotPublished()
    {
        var (segment, segmentLineage) = await StarTrail(1, 2);
        var segmentDetail = await _store.PublishAsync(Publication(segment, segmentLineage), CancellationToken.None);
        var (rollup, _) = await StarTrail(3, 4);
        var unpublished = new NightlyProductSource(
            0, NightlyProductSourceKind.NightlyProduct, rollup.SourceArtifactIds[0], segmentDetail.OutputIdentitySha256,
            null, WindowStart);
        var forged = rollup with { SourceArtifactIds = [rollup.SourceArtifactIds[0]] };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await _store.PublishAsync(Publication(forged, [unpublished]), CancellationToken.None));
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Fixed literal statements probe the immutability triggers.")]
    public async Task PublishedRowsAndLineage_AreImmutable()
    {
        var (product, lineage) = await StarTrail(1, 2);
        await _store.PublishAsync(Publication(product, lineage), CancellationToken.None);
        var database = Path.Combine(_root, SqliteNightlyProductStore.DirectoryName, SqliteNightlyProductStore.FileName);

        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        foreach (var statement in new[]
                 {
                     "UPDATE nightly_products SET variant = 'forged';",
                     "DELETE FROM nightly_products;",
                     "UPDATE nightly_product_sources SET ordinal = ordinal + 10;",
                     "DELETE FROM nightly_product_sources;"
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            var error = Assert.ThrowsExactly<SqliteException>(() => command.ExecuteNonQuery(), statement);
            StringAssert.Contains(error.Message, "immutable", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task TamperedPayloadOrRendition_IsRefusedOnRead()
    {
        var (product, lineage) = await StarTrail(1, 2);
        var detail = await _store.PublishAsync(Publication(product, lineage), CancellationToken.None);
        var stem = detail.Summary.ProductId.ToString("N");
        var payload = Directory.GetFiles(_root, stem + ".bin", SearchOption.AllDirectories)[0];
        var rendition = Directory.GetFiles(_root, stem + ".jpg", SearchOption.AllDirectories)[0];

        var bytes = await File.ReadAllBytesAsync(payload);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(payload, bytes);
        await File.AppendAllTextAsync(rendition, "x");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await _store.ReadStoredProductAsync(detail.Summary.ProductId, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await _store.OpenRenditionAsync(detail.Summary.ProductId, CancellationToken.None));
    }

    [TestMethod]
    public async Task RecordWindow_ReplacesCurrentPointers_AndEarlierProductsStayPublishedButSuperseded()
    {
        var (a, aLineage) = await StarTrail(1, 2);
        var (b, bLineage) = await StarTrail(1, 2, 3);
        var first = await _store.PublishAsync(Publication(a, aLineage), CancellationToken.None);
        var second = await _store.PublishAsync(Publication(b, bLineage), CancellationToken.None);

        await _store.RecordWindowAsync(Status(NightlyProductWindowDisposition.Produced), FingerprintOne, [first.Summary.ProductId],
            CancellationToken.None);
        Assert.IsTrue((await _store.GetAsync(first.Summary.ProductId, CancellationToken.None))!.Summary.IsCurrent);
        Assert.IsFalse((await _store.GetAsync(second.Summary.ProductId, CancellationToken.None))!.Summary.IsCurrent);

        await _store.RecordWindowAsync(Status(NightlyProductWindowDisposition.Produced), FingerprintTwo, [second.Summary.ProductId],
            CancellationToken.None);

        var listed = await _store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.HasCount(2, listed);
        Assert.IsFalse(listed.Single(p => p.ProductId == first.Summary.ProductId).IsCurrent);
        Assert.IsTrue(listed.Single(p => p.ProductId == second.Summary.ProductId).IsCurrent);
        var state = await _store.ReadWindowAsync(
            NightlyProductKind.StarTrail, NightlyProductScope.Segment, WindowStart, CancellationToken.None);
        Assert.AreEqual(FingerprintTwo, state!.FingerprintSha256);
        CollectionAssert.AreEqual(new[] { second.Summary.ProductId }, state.ProductIds.ToArray());
    }

    [TestMethod]
    public async Task RecordWindow_RefusesRollupScopeAndInconsistentPointers()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await _store.RecordWindowAsync(
            Status(NightlyProductWindowDisposition.NoSources) with { Scope = NightlyProductScope.Rollup }, FingerprintOne, [],
            CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await _store.RecordWindowAsync(
            Status(NightlyProductWindowDisposition.Produced), FingerprintOne, [], CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await _store.RecordWindowAsync(
            Status(NightlyProductWindowDisposition.NoSources), FingerprintOne, [Guid.NewGuid()], CancellationToken.None));
    }

    [TestMethod]
    public async Task Catalog_ReturnsNullForUnknownProducts()
    {
        Assert.IsNull(await _store.GetAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.IsNull(await _store.OpenRenditionAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.IsNull(await _store.FindByOutputIdentityAsync(new string('D', 64), CancellationToken.None));
        Assert.IsEmpty(await _store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
    }

    private static NightlyProductWindowStatus Status(NightlyProductWindowDisposition disposition) => new(
        NightlyProductKind.StarTrail, NightlyProductScope.Segment, NightlyProductFixture.ObservingDate, WindowStart,
        WindowStart.AddHours(1), disposition, null, 3, 3, new Dictionary<string, int>(), WindowStart.AddHours(2)) { Occurrence = Occurrence };

    private static NightlyProductPublication Publication(ProcessingProduct product, IReadOnlyList<NightlyProductSource> lineage) =>
        new(NightlyProductKind.StarTrail, NightlyProductScope.Segment, NightlyProductFixture.ObservingDate, WindowStart,
            WindowStart.AddHours(1), 0, BuiltInProcessingRecipes.StarTrail, product, lineage, 90) { Occurrence = Occurrence };

    private static async Task<(ProcessingProduct Product, IReadOnlyList<NightlyProductSource> Lineage)> StarTrail(
        params int[] indexes)
    {
        var frames = indexes.Select(i => NightlyProductFixture.Frame(i, WindowStart.AddMinutes(i))).ToArray();
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.StarTrail,
                JsonSerializer.SerializeToElement(new StarTrailRecipeOptions(NightlyProductRecipeLimits.MaximumSourceCount)),
                ProcessingInputSelector.RecipeResult(
                    FrameArtifactRole.Preview, NightlyProductFixture.PreviewVariant, NightlyProductFixture.PreviewRecipe),
                [.. frames.Select(static frame => frame.Artifact)],
                NightlyProductGenerator.StarTrailSegmentVariant, AuxiliaryInputs: [NightlyProductPreset.BindOccurrence(Occurrence)]),
            CancellationToken.None);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products[0];
        var byId = frames.ToDictionary(static frame => frame.Artifact.ArtifactId);
        return (product, [.. product.SourceArtifactIds.Select((id, ordinal) => new NightlyProductSource(
            ordinal, NightlyProductSourceKind.PreviewFrame, id, byId[id].Candidate.OutputIdentitySha256,
            byId[id].Candidate.CaptureId, byId[id].Candidate.ExposureStartedUtc))]);
    }
}
