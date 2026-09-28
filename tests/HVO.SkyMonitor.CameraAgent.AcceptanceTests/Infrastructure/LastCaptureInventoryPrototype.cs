using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed record InventoryOutput(string Identity, string FactSha256);
internal sealed record InventoryCapture(long Sequence, string CaptureId, string FactSha256,
    InventoryOutput[] Outputs);
internal sealed record LastCaptureInventory(string AgentId, long MaximumSequence, InventoryCapture[] Captures);

internal static class LastCaptureInventoryPrototype
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
        Justification = "SQLite deferred snapshot and durable file sync require synchronous APIs here.")]
    internal static async Task PublishAsync(string sourcePath, string indexPath, string path,
        Action<string>? fault = null)
    {
        if (File.Exists(path) || File.Exists(path + ".pending"))
            throw new InvalidDataException("Inventory publication already exists or is incomplete.");
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        using var index = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        await index.OpenAsync().ConfigureAwait(false);
        using var sourceVersion = source.CreateCommand();
        sourceVersion.CommandText = "PRAGMA data_version;";
        var beforeSource = Convert.ToInt64(await sourceVersion.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        using var indexVersion = index.CreateCommand();
        indexVersion.CommandText = "PRAGMA data_version;";
        var beforeIndex = Convert.ToInt64(await indexVersion.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        using var transaction = source.BeginTransaction(deferred: true);
        using var indexSnapshot = index.BeginTransaction(deferred: true);
        using var captures = source.CreateCommand();
        captures.Transaction = transaction;
        captures.CommandText = """
            SELECT agent_id,capture_sequence,capture_id FROM raw_captures
            ORDER BY capture_sequence DESC LIMIT 500;
            """;
        var rows = new List<(string Agent, long Sequence, string Id)>();
        using (var reader = await captures.ExecuteReaderAsync().ConfigureAwait(false))
            while (await reader.ReadAsync().ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        if (rows.Count != 500 || rows.Any(row => row.Agent != rows[0].Agent) ||
            rows.Where((row, i) => row.Sequence != rows[0].Sequence - i).Any())
            throw new InvalidDataException("Source last-500 publication is incomplete or spans agents.");
        var entries = new List<InventoryCapture>(500);
        foreach (var row in rows.OrderBy(static row => row.Sequence))
        {
            using var fact = index.CreateCommand();
            fact.Transaction = indexSnapshot;
            fact.CommandText = "SELECT fact_path,fact_sha256 FROM capture_lookup WHERE capture_id=$id;";
            fact.Parameters.AddWithValue("$id", row.Id);
            using var rawRow = await fact.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await rawRow.ReadAsync().ConfigureAwait(false))
                throw new InvalidDataException("Source capture lacks projected fact.");
            var rawFact = await ReadFactAsync(Path.GetDirectoryName(indexPath)!, rawRow.GetString(0), "hours")
                .ConfigureAwait(false);
            var rawHash = Convert.ToHexString(SHA256.HashData(rawFact));
            if (rawHash != rawRow.GetString(1)) throw new InvalidDataException("Raw index hash mismatch.");
            using var rawDocument = JsonDocument.Parse(rawFact);
            using var sourceRaw = source.CreateCommand();
            sourceRaw.Transaction = transaction;
            sourceRaw.CommandText = "SELECT manifest_json FROM raw_captures WHERE capture_id=$id;";
            sourceRaw.Parameters.AddWithValue("$id", row.Id);
            if (await sourceRaw.ExecuteScalarAsync().ConfigureAwait(false) is not byte[] manifest ||
                !rawDocument.RootElement.GetProperty("Manifest").GetBytesFromBase64().AsSpan().SequenceEqual(manifest))
                throw new InvalidDataException("Retained raw fact conflicts with source.");
            using var outputs = source.CreateCommand();
            outputs.Transaction = transaction;
            outputs.CommandText = "SELECT output_identity_sha256,descriptor_json FROM processing_outputs WHERE capture_id=$id ORDER BY output_identity_sha256;";
            outputs.Parameters.AddWithValue("$id", row.Id);
            var expected = new List<InventoryOutput>();
            using (var reader = await outputs.ExecuteReaderAsync().ConfigureAwait(false))
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var identity = reader.GetString(0);
                    using var lookup = index.CreateCommand();
                    lookup.Transaction = indexSnapshot;
                    lookup.CommandText = "SELECT fact_path,fact_sha256,capture_id FROM output_lookup WHERE output_identity=$identity;";
                    lookup.Parameters.AddWithValue("$identity", identity);
                    using var indexed = await lookup.ExecuteReaderAsync().ConfigureAwait(false);
                    if (!await indexed.ReadAsync().ConfigureAwait(false))
                        throw new InvalidDataException("Source output lacks projected fact.");
                    if (indexed.GetString(2) != row.Id)
                        throw new InvalidDataException("Projected output belongs to a different capture.");
                    var outputFact = await ReadFactAsync(Path.GetDirectoryName(indexPath)!, indexed.GetString(0), "products")
                        .ConfigureAwait(false);
                    var hash = Convert.ToHexString(SHA256.HashData(outputFact));
                    if (hash != indexed.GetString(1)) throw new InvalidDataException("Output index hash mismatch.");
                    using var product = JsonDocument.Parse(outputFact);
                    if (product.RootElement.GetProperty("Identity").GetString() != identity ||
                        !product.RootElement.GetProperty("Descriptor").GetBytesFromBase64().AsSpan()
                            .SequenceEqual((byte[])reader.GetValue(1)))
                        throw new InvalidDataException("Retained output fact conflicts with source.");
                    expected.Add(new InventoryOutput(identity, hash));
                }
            entries.Add(new InventoryCapture(row.Sequence, row.Id, rawHash, expected.ToArray()));
        }
        transaction.Commit();
        indexSnapshot.Commit();
        var inventory = new LastCaptureInventory(rows[0].Agent, rows[0].Sequence, entries.ToArray());
        var pending = path + ".pending";
        var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, inventory).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        fault?.Invoke("before-rename");
        if (Convert.ToInt64(await sourceVersion.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != beforeSource ||
            Convert.ToInt64(await indexVersion.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != beforeIndex)
            throw new InvalidDataException("Source or index changed during inventory publication.");
        File.Move(pending, path);
        HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(path)!);
    }

    internal static async Task<LastCaptureInventory> ReadAsync(string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        var inventory = document.Deserialize<LastCaptureInventory>()
            ?? throw new InvalidDataException("Missing inventory.");
        if (inventory.MaximumSequence < 500 || inventory.Captures.Length != 500 ||
            inventory.Captures.Where((row, i) => row.Sequence != inventory.MaximumSequence - 499 + i ||
                row.Outputs is null || row.Outputs.Select(static output => output.Identity).Distinct().Count() != row.Outputs.Length).Any())
            throw new InvalidDataException("Invalid last-500 inventory.");
        return inventory;
    }

    private static async Task<byte[]> ReadFactAsync(string root, string relative, string directory)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.Combine(root, directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Inventory fact escaped retained root.");
        HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
        Justification = "SQLite deferred snapshot is required to keep every membership query on one revision.")]
    internal static async Task VerifyMembershipAsync(string inventoryPath, string indexPath)
    {
        var inventory = await ReadAsync(inventoryPath).ConfigureAwait(false);
        using var index = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var snapshot = index.BeginTransaction(deferred: true);
        using var high = index.CreateCommand();
        high.Transaction = snapshot;
        high.CommandText = "SELECT max(sequence) FROM capture_lookup WHERE agent_id=$agent;";
        high.Parameters.AddWithValue("$agent", inventory.AgentId);
        if (Convert.ToInt64(await high.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != inventory.MaximumSequence)
            throw new InvalidDataException("Index high-water mark conflicts with retained inventory.");
        foreach (var capture in inventory.Captures)
        {
            using var row = index.CreateCommand();
            row.Transaction = snapshot;
            row.CommandText = "SELECT capture_id,fact_sha256 FROM capture_lookup WHERE agent_id=$agent AND sequence=$sequence;";
            row.Parameters.AddWithValue("$agent", inventory.AgentId);
            row.Parameters.AddWithValue("$sequence", capture.Sequence);
            using var reader = await row.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetString(0) != capture.CaptureId ||
                reader.GetString(1) != capture.FactSha256)
                throw new InvalidDataException("Capture membership conflicts with retained inventory.");
            using var outputs = index.CreateCommand();
            outputs.Transaction = snapshot;
            outputs.CommandText = "SELECT output_identity,fact_sha256 FROM output_lookup WHERE capture_id=$id ORDER BY output_identity;";
            outputs.Parameters.AddWithValue("$id", capture.CaptureId);
            var actual = new List<InventoryOutput>();
            using (var products = await outputs.ExecuteReaderAsync().ConfigureAwait(false))
                while (await products.ReadAsync().ConfigureAwait(false))
                    actual.Add(new InventoryOutput(products.GetString(0), products.GetString(1)));
            if (!actual.SequenceEqual(capture.Outputs))
                throw new InvalidDataException("Output membership conflicts with retained inventory.");
        }
    }
}
