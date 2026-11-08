using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned harness owns both disposable services.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL comes only from a frozen repository fixture and a fixed table allowlist.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused integration tests.")]
public sealed class DailySummaryIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvo-daily-summary-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryNightlySourceReader _reader = new();

    public void Dispose() => Directory.Delete(_root, true);

    [TestMethod]
    public async Task CompleteSyntheticDayPublishesOnceAndSurvivesRestartWithExactProvenance()
    {
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
        var window = occurrence.SourceWindow!;
        var count = (int)Math.Ceiling((window.EndUtc - window.StartUtc).TotalMinutes / 5);
        _reader.Add([.. Enumerable.Range(0, count).Select(index => NightlyProductFixture.Frame(index + 1,
            window.StartUtc.AddMinutes(index * 5)))]);
        Guid id;
        using (var harness = Create())
        {
            var registry = new NightlyProductAutomationTaskRegistry(harness.Generator, NightlyProductFixture.HostOptions(_root, NightlyProductFixture.Options()));
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await registry.ExecuteAsync(occurrence, CancellationToken.None)).Outcome);
            var page = await harness.Store.ListLibraryAsync(new(12, NightlyProductKind.DailySummary), CancellationToken.None);
            var entry = page.Entries.Single();
            Assert.AreEqual(NightlyProductLibraryState.Produced, entry.State);
            id = entry.Product!.ProductId;
            var presentation = (await harness.Store.GetPresentationAsync(id, CancellationToken.None))!;
            Assert.AreEqual(count, presentation.LineageFrameCount);
            Assert.AreEqual(count, presentation.DailySummary!.SampledBins);
            Assert.IsFalse(presentation.DailySummary.Partial);
            Assert.IsNull(presentation.DailySummary.EventCount);
            Assert.AreEqual(1, _reader.Restores, "Only one representative payload is restored.");
            var provenance = (await harness.Store.OpenProvenanceAsync(id, CancellationToken.None))!;
            Assert.AreEqual(provenance.ChecksumSha256, Convert.ToHexString(SHA256.HashData(provenance.Content.Span)));
            var rendition = (await harness.Store.OpenRenditionAsync(id, CancellationToken.None))!;
            Assert.AreEqual(rendition.ChecksumSha256, Convert.ToHexString(SHA256.HashData(rendition.Content.Span)));
        }
        using (var restarted = Create())
        {
            var again = await restarted.Generator.RunAsync(occurrence, CancellationToken.None);
            Assert.AreEqual(1, again.WindowsUnchanged);
            Assert.AreEqual(0, again.ProductsPublished);
            Assert.AreEqual(1, _reader.Restores);
            Assert.IsNotNull(await restarted.Store.GetAsync(id, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task SparseSummaryKeepsExclusionsProductLinksAndImmutablePriorVersions()
    {
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
        var start = occurrence.SourceWindow!.StartUtc;
        _reader.Add(NightlyProductFixture.Frame(1, start.AddHours(16)),
            NightlyProductFixture.Frame(2, start.AddHours(17), new string('0', 64)),
            NightlyProductFixture.Frame(3, occurrence.SourceWindow.EndUtc));
        using var harness = Create();
        await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        var withoutLinks = (await harness.Store.ListLibraryAsync(new(12, NightlyProductKind.DailySummary), CancellationToken.None)).Entries.Single().Product!;
        await harness.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail), CancellationToken.None);
        var linked = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(1, linked.ProductsPublished, "New product links must invalidate the summary identity without new captures.");
        var entry = (await harness.Store.ListLibraryAsync(new(12, NightlyProductKind.DailySummary,
            NightlyProductLibraryState.Partial), CancellationToken.None)).Entries.Single();
        var oldId = entry.Product!.ProductId;
        Assert.AreNotEqual(withoutLinks.ProductId, oldId);
        var presentation = (await harness.Store.GetPresentationAsync(oldId, CancellationToken.None))!;
        var facts = presentation.DailySummary!;
        Assert.AreEqual(2, facts.CandidateCount, "The exposure at the exclusive end belongs to the next window.");
        Assert.AreEqual(1, facts.CaptureCount);
        Assert.AreEqual(1, facts.Exclusions[NightlyProductContract.ExcludedRigReasonCode]);
        Assert.AreEqual(1, facts.Products.Count);
        Assert.AreEqual(1, presentation.LineageFrameCount, "Linked product lineage must not double-count captures.");
        Assert.IsTrue(presentation.Detail.Sources.Any(static source => source.SourceKind == NightlyProductSourceKind.NightlyProduct));
        var oldBytes = (await harness.Store.OpenProvenanceAsync(oldId, CancellationToken.None))!.Content.ToArray();
        _reader.Add(NightlyProductFixture.Frame(4, start.AddHours(18)));
        var changed = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(1, changed.ProductsPublished);
        var next = (await harness.Store.ListLibraryAsync(new(12, NightlyProductKind.DailySummary), CancellationToken.None)).Entries.Single();
        Assert.AreNotEqual(oldId, next.Product!.ProductId);
        CollectionAssert.AreEqual(oldBytes, (await harness.Store.OpenProvenanceAsync(oldId, CancellationToken.None))!.Content.ToArray());
    }

    [TestMethod]
    public async Task EmptyDayRecordsNotProducedAndCorruptRetryFailsWithoutSubstitution()
    {
        using var harness = Create();
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
        await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        var empty = (await harness.Store.ListLibraryAsync(new(12, NightlyProductKind.DailySummary), CancellationToken.None)).Entries.Single();
        Assert.AreEqual(NightlyProductLibraryState.NotProduced, empty.State);
        Assert.AreEqual(NightlyProductWindowDisposition.NoSources, empty.Daily.Status.Disposition);
        Assert.IsNull(empty.Product);
        _reader.Add(NightlyProductFixture.Frame(1, occurrence.SourceWindow!.StartUtc.AddHours(10)));
        await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        var product = (await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Single();
        var file = Directory.GetFiles(_root, product.ProductId.ToString("N") + ".jpg", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(file, [0, 1, 2]);
        var retry = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(1, retry.FailedWindows);
        Assert.AreEqual(0, retry.ProductsPublished);
    }

    [TestMethod]
    public async Task SchemaTwoUpgradePreservesPublishedFilesLineagePointersAndImmutability()
    {
        _reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        NightlyProductDetail before;
        using (var harness = Create())
        {
            await harness.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail), CancellationToken.None);
            var id = (await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None))
                .Single(static product => product.Scope == NightlyProductScope.Final).ProductId;
            before = (await harness.Store.GetAsync(id, CancellationToken.None))!;
        }
        var database = Path.Combine(_root, SqliteNightlyProductStore.DirectoryName, SqliteNightlyProductStore.FileName);
        SqliteConnection.ClearAllPools();
        var source = database + ".source";
        File.Move(database, source);
        using (var connection = new SqliteConnection("Data Source=" + database))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nightly-products-schema-v2.sql"));
            await command.ExecuteNonQueryAsync();
            command.CommandText = "ATTACH DATABASE $source AS original;";
            command.Parameters.AddWithValue("$source", source);
            await command.ExecuteNonQueryAsync();
            foreach (var table in new[] { "nightly_products", "nightly_product_sources", "nightly_windows", "nightly_window_products" })
            {
                command.CommandText = $"INSERT INTO {table} SELECT * FROM original.{table};";
                await command.ExecuteNonQueryAsync();
            }
        }
        var hashes = Directory.GetFiles(Path.Combine(_root, SqliteNightlyProductStore.ProductDirectoryName), "*", SearchOption.AllDirectories)
            .ToDictionary(static path => path, static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        using (var upgraded = Create())
        {
            var after = (await upgraded.Store.GetAsync(before.Summary.ProductId, CancellationToken.None))!;
            Assert.AreEqual(before.Summary, after.Summary);
            CollectionAssert.AreEqual(before.Sources.ToArray(), after.Sources.ToArray());
            Assert.AreEqual(before.ProvenanceSha256, after.ProvenanceSha256);
            await upgraded.Store.VerifyPublicationAsync(after.Summary.ProductId, CancellationToken.None);
            await upgraded.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary), CancellationToken.None);
            Assert.IsTrue((await upgraded.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None))
                .Any(static product => product.Kind == NightlyProductKind.DailySummary));
        }
        foreach (var pair in hashes)
            Assert.AreEqual(pair.Value, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pair.Key))));
        using var verify = new SqliteConnection("Data Source=" + database);
        await verify.OpenAsync();
        using var check = verify.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(3L, await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_check;";
        Assert.AreEqual(0L, await check.ExecuteScalarAsync());
        check.CommandText = "DELETE FROM nightly_products;";
        await Assert.ThrowsExactlyAsync<SqliteException>(async () => await check.ExecuteNonQueryAsync());
    }

    [TestMethod]
    public async Task InvalidSchemaTwoReferencesRollBackTheEntireConversion()
    {
        Directory.CreateDirectory(Path.Combine(_root, SqliteNightlyProductStore.DirectoryName));
        var database = Path.Combine(_root, SqliteNightlyProductStore.DirectoryName, SqliteNightlyProductStore.FileName);
        using (var connection = new SqliteConnection("Data Source=" + database))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nightly-products-schema-v2.sql"));
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                INSERT INTO nightly_product_sources VALUES (
                    '00000000000000000000000000000001', 0, 'PreviewFrame', '00000000000000000000000000000002',
                    'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', NULL, 1);
                """;
            await command.ExecuteNonQueryAsync();
        }
        using (var harness = Create())
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await harness.Store.GetDayAsync(
                NightlyProductFixture.ObservingDate, CancellationToken.None));
        using var verify = new SqliteConnection("Data Source=" + database);
        await verify.OpenAsync();
        using var check = verify.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(2L, await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%_v3';";
        Assert.AreEqual(0L, await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM nightly_product_sources;";
        Assert.AreEqual(1L, await check.ExecuteScalarAsync());
    }

    private Harness Create()
    {
        Directory.CreateDirectory(_root);
        var host = NightlyProductFixture.HostOptions(_root, NightlyProductFixture.Options());
        var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
        var store = new SqliteNightlyProductStore(host, clock);
        var generator = new NightlyProductGenerator(host, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
            _reader, store, new AstronomyEnginePlanetEphemeris(), static () => null, clock);
        return new(store, generator);
    }

    private sealed record Harness(SqliteNightlyProductStore Store, NightlyProductGenerator Generator) : IDisposable
    {
        public void Dispose() { Generator.Dispose(); Store.Dispose(); }
    }
}
