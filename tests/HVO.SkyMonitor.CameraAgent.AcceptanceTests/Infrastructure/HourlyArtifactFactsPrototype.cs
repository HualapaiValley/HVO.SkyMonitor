using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

// Disposable shadow layout; CameraAgent continues to use the source journal.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
    Justification = "The prototype uses SQLite deferred reads and explicit durable file synchronization.")]
internal sealed class HourlyArtifactFactsPrototype(string root, string journalPath, Action<string>? fault = null)
{
    private static readonly JsonSerializerOptions ProcessingOptions = CreateProcessingOptions();
    private readonly string _root = Path.GetFullPath(root);
    private readonly string _journalPath = Path.GetFullPath(journalPath);
    private readonly Action<string>? _fault = fault;
    private string IndexPath => Path.Combine(_root, "mutable.db");

    private static JsonSerializerOptions CreateProcessingOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    internal async Task ProjectAsync(CancellationToken cancellationToken)
    {
        if (new DirectoryInfo(_root).LinkTarget is not null)
            throw new InvalidDataException("Linked shadow root is not allowed.");
        Directory.CreateDirectory(_root);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, IndexPath);
        using var journal = new SqliteConnection($"Data Source={_journalPath};Mode=ReadOnly;Pooling=False");
        await journal.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var version = journal.CreateCommand();
        version.CommandText = "PRAGMA data_version;";
        var beforeVersion = Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        using var index = new SqliteConnection($"Data Source={IndexPath};Pooling=False");
        await index.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var schema = index.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS capture_lookup(
                    capture_id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                    exposure_hour INTEGER NOT NULL, exposure_unix_ms INTEGER NOT NULL,
                    fact_path TEXT NOT NULL, fact_sha256 TEXT NOT NULL,
                    state TEXT NOT NULL, retention_hold INTEGER NOT NULL, UNIQUE(agent_id,sequence));
                CREATE INDEX IF NOT EXISTS ix_shadow_capture_time ON capture_lookup(exposure_unix_ms,sequence);
                CREATE TABLE IF NOT EXISTS output_lookup(
                    output_identity TEXT PRIMARY KEY, capture_id TEXT NOT NULL, committed_unix_ms INTEGER NOT NULL,
                    role TEXT NOT NULL, variant TEXT NOT NULL, fact_sha256 TEXT NOT NULL,
                    availability TEXT NOT NULL, fact_path TEXT NOT NULL,
                    FOREIGN KEY(capture_id) REFERENCES capture_lookup(capture_id));
                CREATE INDEX IF NOT EXISTS ix_shadow_output_capture ON output_lookup(capture_id,output_identity);
                CREATE TABLE IF NOT EXISTS projection_state(
                    id INTEGER PRIMARY KEY CHECK(id=1), ready INTEGER NOT NULL CHECK(ready IN (0,1)));
                INSERT OR IGNORE INTO projection_state VALUES (1,0);
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var invalidate = index.CreateCommand())
        {
            invalidate.CommandText = "UPDATE projection_state SET ready=0 WHERE id=1;";
            await invalidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using var snapshot = journal.BeginTransaction(deferred: true);
        using (var seen = index.CreateCommand())
        {
            seen.CommandText = """
                CREATE TEMP TABLE seen_captures(capture_id TEXT PRIMARY KEY);
                CREATE TEMP TABLE seen_outputs(output_identity TEXT PRIMARY KEY);
                """;
            await seen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using var captures = journal.CreateCommand();
        captures.Transaction = snapshot;
        captures.CommandText = """
            SELECT capture_id,agent_id,capture_sequence,exposure_started_unix_ms,manifest_json,
                   state,retention_hold,manifest_sha256,descriptor_sha256,raw_artifact_id,
                   payload_sha256,payload_length,payload_relative_path
            FROM raw_captures ORDER BY agent_id,capture_sequence;
            """;
        using var reader = await captures.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _fault?.Invoke("during-scan");
            var capture = reader.GetString(0);
            var agent = reader.GetString(1);
            var sequence = reader.GetInt64(2);
            var hour = reader.GetInt64(3) / 3_600_000;
            var manifestBytes = (byte[])reader.GetValue(4);
            var parsed = CaptureContractJson.ParseManifest(manifestBytes);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
                CaptureContractJson.ComputeManifestSha256(manifestBytes) != reader.GetString(7) ||
                CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor) != reader.GetString(8) ||
                manifest.Descriptor.Capture.CaptureId.ToString("N") != capture ||
                manifest.Descriptor.Capture.CaptureSequence != sequence ||
                manifest.Descriptor.Capture.AgentId != agent ||
                manifest.Descriptor.Artifact.ArtifactId.ToString("N") != reader.GetString(9) ||
                manifest.Descriptor.Artifact.ChecksumSha256 != reader.GetString(10) ||
                manifest.Descriptor.Layout.ByteLength != reader.GetInt64(11) ||
                manifest.RelativeArtifactPath != reader.GetString(12) ||
                manifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() != reader.GetInt64(3))
                throw new InvalidDataException("Authoritative raw manifest identity mismatch.");
            var fact = JsonSerializer.SerializeToUtf8Bytes(new
            {
                CaptureId = capture,
                AgentId = agent,
                Sequence = sequence,
                Hour = hour,
                Manifest = manifestBytes,
                ManifestSha256 = reader.GetString(7)
            });
            var relative = Path.Combine("hours", hour.ToString(System.Globalization.CultureInfo.InvariantCulture),
                capture + ".json");
            var hash = Convert.ToHexString(SHA256.HashData(fact));
            await PublishAsync(index, relative, fact, "capture_lookup", "capture_id", capture, cancellationToken)
                .ConfigureAwait(false);
            using (var seen = index.CreateCommand())
            {
                seen.CommandText = "INSERT INTO seen_captures VALUES ($capture);";
                seen.Parameters.AddWithValue("$capture", capture);
                await seen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var update = index.CreateCommand())
            {
                update.CommandText = """
                    INSERT INTO capture_lookup VALUES ($capture,$agent,$sequence,$hour,$exposure,$path,$hash,$state,$hold)
                    ON CONFLICT(capture_id) DO UPDATE SET state=excluded.state,retention_hold=excluded.retention_hold
                    WHERE capture_lookup.agent_id=excluded.agent_id AND capture_lookup.sequence=excluded.sequence
                      AND capture_lookup.exposure_hour=excluded.exposure_hour
                      AND capture_lookup.exposure_unix_ms=excluded.exposure_unix_ms
                      AND capture_lookup.fact_path=excluded.fact_path
                      AND capture_lookup.fact_sha256=excluded.fact_sha256;
                    """;
                update.Parameters.AddWithValue("$capture", capture);
                update.Parameters.AddWithValue("$agent", agent);
                update.Parameters.AddWithValue("$sequence", sequence);
                update.Parameters.AddWithValue("$hour", hour);
                update.Parameters.AddWithValue("$exposure", reader.GetInt64(3));
                update.Parameters.AddWithValue("$path", relative);
                update.Parameters.AddWithValue("$hash", hash);
                update.Parameters.AddWithValue("$state", reader.GetString(5));
                update.Parameters.AddWithValue("$hold", reader.GetInt64(6));
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("Indexed raw fact identity changed.");
            }
            using var outputs = journal.CreateCommand();
            outputs.Transaction = snapshot;
            outputs.CommandText = """
                SELECT output_identity_sha256,descriptor_json,committed_unix_ms,availability_state,
                       agent_id,capture_sequence,artifact_id,recipe_identity_sha256,role,variant,node_id,
                       payload_relative_path,product_kind,product_schema_version,content_identity_sha256,
                       algorithms_json,compatibility_json,total_integration_ticks
                FROM processing_outputs WHERE capture_id=$capture ORDER BY node_id,output_identity_sha256;
                """;
            outputs.Parameters.AddWithValue("$capture", capture);
            using var outputReader = await outputs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await outputReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = outputReader.GetString(0);
                var descriptor = (byte[])outputReader.GetValue(1);
                using var schemaDocument = JsonDocument.Parse(descriptor);
                var schema = schemaDocument.RootElement.GetProperty("schemaVersion").GetString();
                var productManifest = schema is DurableProcessingProductManifestV1.CurrentSchemaVersion or
                    DurableEncodedProductManifestV2.CurrentSchemaVersion or
                    DurableTypedMetadataProductManifestV3.CurrentSchemaVersion
                    ? DurableProcessingProductManifestJson.Parse(descriptor) : null;
                var parsedOutput = productManifest is null ? CaptureContractJson.ParseManifest(descriptor) : null;
                var derived = parsedOutput?.Document?.Manifest;
                var artifact = productManifest?.Artifact ?? derived?.Descriptor.Artifact;
                var outputCapture = productManifest?.Capture ?? derived?.Descriptor.Capture;
                if (artifact is null || outputCapture is null || productManifest is null && parsedOutput?.IsValid != true ||
                    outputCapture.CaptureId != manifest.Descriptor.Capture.CaptureId ||
                    outputCapture.CaptureSequence != sequence || outputCapture.AgentId != agent ||
                    outputReader.GetString(4) != agent || outputReader.GetInt64(5) != sequence ||
                    outputReader.GetString(6) != artifact.ArtifactId.ToString("N") ||
                    outputReader.GetString(7) != ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256 ||
                    outputReader.GetString(8) != artifact.Role.ToString() ||
                    outputReader.GetString(9) != artifact.Variant ||
                    productManifest is not null && outputReader.GetString(11) != productManifest.RelativeArtifactPath ||
                    derived is not null && outputReader.GetString(11) != derived.RelativeArtifactPath ||
                    productManifest is not null && !string.Equals(outputReader.GetString(10),
                        productManifest.ProducerStepId ?? artifact.SourceId,
                        productManifest.ProducerStepId is null ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) ||
                    productManifest is DurableTypedMetadataProductManifestV3 typed &&
                        (outputReader.GetString(12) != typed.Kind.ToString() ||
                         outputReader.GetString(13) != typed.ProductSchemaVersion ||
                         outputReader.GetString(14) != typed.ContentIdentitySha256) ||
                    productManifest is not DurableTypedMetadataProductManifestV3 &&
                        (!outputReader.IsDBNull(12) || !outputReader.IsDBNull(13) || !outputReader.IsDBNull(14)) ||
                    productManifest is not null &&
                        (!JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(
                            (byte[])outputReader.GetValue(15), ProcessingOptions)!.SequenceEqual(productManifest.Algorithms) ||
                         JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(
                            (byte[])outputReader.GetValue(16), ProcessingOptions) != productManifest.Compatibility ||
                         outputReader.GetInt64(17) != productManifest.TotalIntegrationTicks) ||
                    artifact.ArtifactId != ProcessingIdentity.CreateArtifactId(identity) ||
                    ProcessingIdentity.CreateOutputIdentity(artifact.Role, artifact.Variant,
                        ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256,
                        artifact.SourceArtifactIds) != identity)
                    throw new InvalidDataException("Authoritative output identity mismatch.");
                using (var node = journal.CreateCommand())
                {
                    node.Transaction = snapshot;
                    node.CommandText = "SELECT output_role,output_variant FROM processing_nodes WHERE capture_id=$capture AND node_id=$node;";
                    node.Parameters.AddWithValue("$capture", capture);
                    node.Parameters.AddWithValue("$node", outputReader.GetString(10));
                    using var nodeReader = await node.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (!await nodeReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                        nodeReader.GetString(0) != outputReader.GetString(8) ||
                        nodeReader.GetString(1) != outputReader.GetString(9))
                        throw new InvalidDataException("Output routing conflicts with its processing node.");
                }
                using var sources = journal.CreateCommand();
                sources.Transaction = snapshot;
                sources.CommandText = """
                    SELECT source_ordinal,source_artifact_id FROM processing_output_sources
                    WHERE output_identity_sha256=$identity ORDER BY source_ordinal;
                    """;
                sources.Parameters.AddWithValue("$identity", identity);
                var sourceIds = new List<string>();
                using (var sourceReader = await sources.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    while (await sourceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (sourceReader.GetInt64(0) != sourceIds.Count)
                            throw new InvalidDataException("Output source ordinals are not contiguous.");
                        sourceIds.Add(sourceReader.GetString(1));
                    }
                if (!sourceIds.SequenceEqual(artifact.SourceArtifactIds.Select(static id => id.ToString("N")),
                    StringComparer.Ordinal))
                    throw new InvalidDataException("Committed output source links conflict with descriptor.");
                var productFact = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Identity = identity,
                    Descriptor = descriptor,
                    Sources = sourceIds
                });
                var productRelative = Path.Combine("products", identity + ".json");
                var productHash = Convert.ToHexString(SHA256.HashData(productFact));
                await PublishAsync(index, productRelative, productFact, "output_lookup", "output_identity", identity,
                    cancellationToken).ConfigureAwait(false);
                using (var seen = index.CreateCommand())
                {
                    seen.CommandText = "INSERT INTO seen_outputs VALUES ($identity);";
                    seen.Parameters.AddWithValue("$identity", identity);
                    await seen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using var outputUpdate = index.CreateCommand();
                outputUpdate.CommandText = """
                    INSERT INTO output_lookup VALUES ($identity,$capture,$committed,$role,$variant,$hash,$availability,$path)
                    ON CONFLICT(output_identity) DO UPDATE SET availability=excluded.availability
                    WHERE output_lookup.capture_id=excluded.capture_id
                      AND output_lookup.committed_unix_ms=excluded.committed_unix_ms
                      AND output_lookup.role=excluded.role AND output_lookup.variant=excluded.variant
                      AND output_lookup.fact_sha256=excluded.fact_sha256
                      AND output_lookup.fact_path=excluded.fact_path;
                    """;
                outputUpdate.Parameters.AddWithValue("$identity", identity);
                outputUpdate.Parameters.AddWithValue("$capture", capture);
                outputUpdate.Parameters.AddWithValue("$committed", outputReader.GetInt64(2));
                outputUpdate.Parameters.AddWithValue("$role", artifact.Role.ToString());
                outputUpdate.Parameters.AddWithValue("$variant", artifact.Variant);
                outputUpdate.Parameters.AddWithValue("$hash", productHash);
                outputUpdate.Parameters.AddWithValue("$availability", outputReader.GetString(3));
                outputUpdate.Parameters.AddWithValue("$path", productRelative);
                if (await outputUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("Indexed output fact identity changed.");
            }
        }
        using (var settle = index.CreateCommand())
        {
            settle.CommandText = """
                UPDATE capture_lookup SET state='RemovedFromSource', retention_hold=0
                WHERE NOT EXISTS (SELECT 1 FROM seen_captures WHERE seen_captures.capture_id=capture_lookup.capture_id);
                UPDATE output_lookup SET availability='RemovedFromSource'
                WHERE NOT EXISTS (SELECT 1 FROM seen_outputs WHERE seen_outputs.output_identity=output_lookup.output_identity);
                """;
            await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var counts = journal.CreateCommand())
        {
            counts.Transaction = snapshot;
            counts.CommandText = "SELECT (SELECT count(*) FROM raw_captures), (SELECT count(*) FROM processing_outputs);";
            using var authoritative = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await authoritative.ReadAsync(cancellationToken).ConfigureAwait(false);
            using var projected = index.CreateCommand();
            projected.CommandText = """
                SELECT (SELECT count(*) FROM capture_lookup WHERE state!='RemovedFromSource'),
                       (SELECT count(*) FROM output_lookup WHERE availability!='RemovedFromSource');
                """;
            using var indexed = await projected.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await indexed.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (authoritative.GetInt64(0) != indexed.GetInt64(0) || authoritative.GetInt64(1) != indexed.GetInt64(1))
                throw new InvalidDataException("Indexed history contains absent source rows; reconciliation is required.");
        }
        snapshot.Commit();
        var afterVersion = Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (afterVersion != beforeVersion)
            throw new InvalidDataException("Source changed during the shadow scan; retry reconciliation.");
        using (var complete = index.CreateCommand())
        {
            complete.CommandText = "UPDATE projection_state SET ready=1 WHERE id=1;";
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(SqliteConnection index, string relative, byte[] bytes, string table,
        string keyColumn, string key, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, relative);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        if (File.Exists(path))
        {
            var existingBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(existingBytes))
                throw new InvalidDataException("Immutable fact changed.");
            return;
        }
        using var existing = index.CreateCommand();
        if (table == "capture_lookup" && keyColumn == "capture_id")
            existing.CommandText = "SELECT 1 FROM capture_lookup WHERE capture_id=$key;";
        else
            existing.CommandText = "SELECT 1 FROM output_lookup WHERE output_identity=$key;";
        existing.Parameters.AddWithValue("$key", key);
        if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidDataException("Indexed immutable fact is missing.");
        var pending = path + ".pending";
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, pending);
        if (File.Exists(pending)) File.Delete(pending);
        var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        RawIngressFileStore.SyncDirectoryHierarchy(_root, Path.GetDirectoryName(path)!);
        _fault?.Invoke("before-rename");
        File.Move(pending, path);
        RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(path)!);
        _fault?.Invoke("after-rename");
    }

    internal byte[] ReadFact(string captureId)
    {
        using var index = new SqliteConnection($"Data Source={IndexPath};Mode=ReadOnly;Pooling=False");
        index.Open();
        using (var ready = index.CreateCommand())
        {
            ready.CommandText = "SELECT ready FROM projection_state WHERE id=1;";
            if (Convert.ToInt64(ready.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Shadow projection requires reconciliation.");
        }
        using var command = index.CreateCommand();
        command.CommandText = "SELECT fact_path,fact_sha256 FROM capture_lookup WHERE capture_id=$capture;";
        command.Parameters.AddWithValue("$capture", captureId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException(captureId);
        var path = Path.GetFullPath(Path.Combine(_root, reader.GetString(0)));
        if (!path.StartsWith(Path.Combine(_root, "hours") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Indexed capture fact escaped its root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != reader.GetString(1))
            throw new InvalidDataException("Fact checksum mismatch.");
        return bytes;
    }

    internal (long Captures, long Outputs, long IndexBytes, long WalBytes, long FactsBytes, long ProductBytes) Measure()
    {
        using var index = new SqliteConnection($"Data Source={IndexPath};Mode=ReadOnly;Pooling=False");
        index.Open();
        using var command = index.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM capture_lookup),(SELECT count(*) FROM output_lookup);";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), new FileInfo(IndexPath).Length,
            File.Exists(IndexPath + "-wal") ? new FileInfo(IndexPath + "-wal").Length : 0,
            Directory.EnumerateFiles(Path.Combine(_root, "hours"), "*.json", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length),
            Directory.EnumerateFiles(Path.Combine(_root, "products"), "*.json").Sum(file => new FileInfo(file).Length));
    }
}
