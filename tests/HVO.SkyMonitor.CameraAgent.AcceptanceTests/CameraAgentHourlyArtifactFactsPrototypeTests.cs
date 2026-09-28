using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
    Justification = "Each disposable fixture is owned by a using declaration inside its fault-case loop.")]
public sealed class CameraAgentHourlyArtifactFactsPrototypeTests
{
    private static readonly string[] CorruptSources = ["ffffffffffffffffffffffffffffffff"];
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OfflineProjectionRetainsTypedMetadataDescriptorAndSources()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        using var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await journal.OpenAsync().ConfigureAwait(false);
        using var select = journal.CreateCommand();
        select.CommandText = """
            SELECT raw.capture_id,raw.raw_artifact_id,raw.manifest_json,output.recipe_identity_sha256,
                   output.algorithms_json,output.compatibility_json
            FROM raw_captures raw JOIN processing_outputs output ON output.capture_id=raw.capture_id
            WHERE raw.capture_sequence=1 LIMIT 1;
            """;
        using var source = await select.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await source.ReadAsync().ConfigureAwait(false));
        var captureId = source.GetString(0);
        var rawArtifactId = Guid.ParseExact(source.GetString(1), "N");
        var raw = CaptureContractJson.ParseManifest((byte[])source.GetValue(2)).Document?.Manifest;
        Assert.IsNotNull(raw);
        var recipe = raw.Descriptor.Artifact.Recipe;
        _ = source.GetString(3);
        var algorithms = (byte[])source.GetValue(4);
        var compatibility = (byte[])source.GetValue(5);
        await source.DisposeAsync().ConfigureAwait(false);
        var variant = "typed-projected-scene";
        var nodeId = "typed-scene";
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, variant,
            recipeIdentity, [rawArtifactId]);
        var artifactId = ProcessingIdentity.CreateArtifactId(outputIdentity);
        var payload = "{\"schemaVersion\":\"projected-scene-v1\"}"u8.ToArray();
        var payloadChecksum = Convert.ToHexString(SHA256.HashData(payload));
        var contentIdentity = new string('C', 64);
        var artifact = raw.Descriptor.Artifact with
        {
            ArtifactId = artifactId,
            Role = FrameArtifactRole.Metadata,
            Variant = variant,
            SourceId = nodeId,
            SourceArtifactIds = [rawArtifactId],
            MediaType = "application/json",
            ChecksumSha256 = payloadChecksum
        };
        using var nullLayout = JsonDocument.Parse("null");
        var relative = $"derived/{artifactId:N}.json";
        var manifest = new DurableTypedMetadataProductManifestV3(
            DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
            raw.Descriptor.Capture, artifact, outputIdentity,
            JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(algorithms, WebOptions)!,
            JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(compatibility, WebOptions)!,
            0, payload.Length, relative, nullLayout.RootElement.Clone(), ProcessingProductKind.Metadata,
            "projected-scene-v1", contentIdentity);
        var descriptor = DurableProcessingProductManifestJson.Serialize(manifest);
        using var insert = journal.CreateCommand();
        insert.CommandText = """
            INSERT INTO processing_nodes(capture_id,node_id,required,dependencies_json,recipe_name,
                output_role,output_variant,plan_sha256,status,attempt,completed_unix_ms)
            VALUES($capture,$node,1,'[]','projected-scene','Metadata',$variant,$plan,'Completed',1,0);
            INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,
                role,variant,payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,
                algorithms_json,compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms,
                product_kind,product_schema_version,content_identity_sha256)
            VALUES($identity,$capture,$agent,$node,$artifact,'Metadata',$variant,$path,$sidecar,$descriptor,
                $recipe,$algorithms,$compatibility,0,1,0,'Metadata','projected-scene-v1',$content);
            INSERT INTO processing_output_sources(output_identity_sha256,source_ordinal,source_artifact_id)
            VALUES($identity,0,$source);
            """;
        insert.Parameters.AddWithValue("$capture", captureId);
        insert.Parameters.AddWithValue("$node", nodeId);
        insert.Parameters.AddWithValue("$variant", variant);
        insert.Parameters.AddWithValue("$plan", new string('A', 64));
        insert.Parameters.AddWithValue("$identity", outputIdentity);
        insert.Parameters.AddWithValue("$agent", raw.Descriptor.Capture.AgentId);
        insert.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        insert.Parameters.AddWithValue("$path", relative);
        insert.Parameters.AddWithValue("$sidecar", relative + ".manifest.json");
        insert.Parameters.AddWithValue("$descriptor", descriptor);
        insert.Parameters.AddWithValue("$recipe", recipeIdentity);
        insert.Parameters.AddWithValue("$algorithms", algorithms);
        insert.Parameters.AddWithValue("$compatibility", compatibility);
        insert.Parameters.AddWithValue("$content", contentIdentity);
        insert.Parameters.AddWithValue("$source", rawArtifactId.ToString("N"));
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(9L, projection.Measure().Outputs);
        var projected = Path.Combine(root, "products", outputIdentity + ".json");
        using var fact = JsonDocument.Parse(await File.ReadAllBytesAsync(projected).ConfigureAwait(false));
        CollectionAssert.AreEqual(descriptor, fact.RootElement.GetProperty("Descriptor").GetBytesFromBase64());
        Assert.AreEqual(rawArtifactId.ToString("N"), fact.RootElement.GetProperty("Sources")[0].GetString());
        var snapshotPath = Path.Combine(fixture.Root, "typed-index.db");
        using (var indexed = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Mode=ReadOnly;Pooling=False"))
        using (var backup = new SqliteConnection($"Data Source={snapshotPath};Pooling=False"))
        {
            await indexed.OpenAsync().ConfigureAwait(false);
            await backup.OpenAsync().ConfigureAwait(false);
            indexed.BackupDatabase(backup);
        }
        await VerifySnapshotAsync(root, snapshotPath).ConfigureAwait(false);
        using var conflict = journal.CreateCommand();
        conflict.CommandText = """
            UPDATE processing_outputs SET content_identity_sha256=$wrong
            WHERE output_identity_sha256=$identity;
            """;
        conflict.Parameters.AddWithValue("$wrong", new string('D', 64));
        conflict.Parameters.AddWithValue("$identity", outputIdentity);
        Assert.AreEqual(1, await conflict.ExecuteNonQueryAsync().ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        conflict.CommandText = "UPDATE processing_outputs SET content_identity_sha256=$content WHERE output_identity_sha256=$identity;";
        conflict.Parameters.AddWithValue("$content", contentIdentity);
        Assert.AreEqual(1, await conflict.ExecuteNonQueryAsync().ConfigureAwait(false));
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        conflict.CommandText = """
            UPDATE processing_outputs SET total_integration_ticks=0, algorithms_json=x'5B5D'
            WHERE output_identity_sha256=$identity;
            """;
        Assert.AreEqual(1, await conflict.ExecuteNonQueryAsync().ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        conflict.CommandText = "UPDATE processing_outputs SET algorithms_json=$algorithms WHERE output_identity_sha256=$identity;";
        conflict.Parameters.AddWithValue("$algorithms", algorithms);
        Assert.AreEqual(1, await conflict.ExecuteNonQueryAsync().ConfigureAwait(false));
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        conflict.CommandText = """
            UPDATE processing_outputs SET compatibility_json=x'7B7D'
            WHERE output_identity_sha256=$identity;
            """;
        Assert.AreEqual(1, await conflict.ExecuteNonQueryAsync().ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        conflict.CommandText = "UPDATE processing_outputs SET compatibility_json=$compatibility, total_integration_ticks=1 WHERE output_identity_sha256=$identity;";
        conflict.Parameters.AddWithValue("$compatibility", compatibility);
        Assert.AreEqual(1, await conflict.ExecuteNonQueryAsync().ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OfflineProjectionRetainsDurableMetadataAndEncodedDescriptors()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT raw.manifest_json, output.algorithms_json, output.compatibility_json
            FROM raw_captures raw JOIN processing_outputs output ON output.capture_id=raw.capture_id
            WHERE raw.capture_sequence=1 LIMIT 1;
            """;
        using var row = await select.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await row.ReadAsync().ConfigureAwait(false));
        var raw = CaptureContractJson.ParseManifest((byte[])row.GetValue(0)).Document?.Manifest;
        Assert.IsNotNull(raw);
        var algorithmsBytes = (byte[])row.GetValue(1);
        var compatibilityBytes = (byte[])row.GetValue(2);
        await row.DisposeAsync().ConfigureAwait(false);
        var algorithms = JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(algorithmsBytes, WebOptions)!;
        var compatibility = JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(compatibilityBytes, WebOptions)!;
        var sourceId = raw.Descriptor.Artifact.ArtifactId;
        var root = Path.Combine(fixture.Root, "shadow");
        foreach (var encoded in new[] { false, true })
        {
            var role = encoded ? FrameArtifactRole.Preview : FrameArtifactRole.Metadata;
            var node = encoded ? "encoded-probe" : "metadata-probe";
            var variant = encoded ? "encoded-probe-v2" : "metadata-probe-v1";
            var mediaType = encoded ? "image/jpeg" : "application/json";
            var recipe = raw.Descriptor.Artifact.Recipe;
            var recipeHash = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
            var identity = ProcessingIdentity.CreateOutputIdentity(role, variant, recipeHash, [sourceId]);
            var artifactId = ProcessingIdentity.CreateArtifactId(identity);
            var artifact = raw.Descriptor.Artifact with
            {
                ArtifactId = artifactId,
                Role = role,
                Variant = variant,
                SourceId = node,
                SourceArtifactIds = [sourceId],
                MediaType = mediaType
            };
            using var nullLayout = JsonDocument.Parse("null");
            var relative = $"derived/{artifactId:N}.bin";
            IDurableProcessingProductManifest evidence = encoded
                ? new DurableEncodedProductManifestV2(DurableEncodedProductManifestV2.CurrentSchemaVersion,
                    raw.Descriptor.Capture, artifact, identity, algorithms, compatibility, 0, 4,
                    relative, nullLayout.RootElement.Clone(), 2, 2, CameraPixelFormat.Mono8, node.ToUpperInvariant())
                : new DurableProcessingProductManifestV1(DurableProcessingProductManifestV1.CurrentSchemaVersion,
                    raw.Descriptor.Capture, artifact, identity, algorithms, compatibility, 0, 4,
                    relative, nullLayout.RootElement.Clone());
            var descriptor = DurableProcessingProductManifestJson.Serialize(evidence);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO processing_nodes(capture_id,node_id,required,dependencies_json,recipe_name,
                    output_role,output_variant,plan_sha256,status,attempt,completed_unix_ms)
                VALUES($capture,$node,1,'[]','probe',$role,$variant,$plan,'Completed',1,0);
                INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,
                    role,variant,payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,
                    algorithms_json,compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms)
                VALUES($identity,$capture,$agent,$node,$artifact,$role,$variant,$path,$sidecar,$descriptor,
                    $recipe,$algorithms,$compatibility,0,1,0);
                INSERT INTO processing_output_sources(output_identity_sha256,source_ordinal,source_artifact_id)
                VALUES($identity,0,$source);
                """;
            insert.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
            insert.Parameters.AddWithValue("$node", node);
            insert.Parameters.AddWithValue("$role", role.ToString());
            insert.Parameters.AddWithValue("$variant", variant);
            insert.Parameters.AddWithValue("$plan", new string('A', 64));
            insert.Parameters.AddWithValue("$identity", identity);
            insert.Parameters.AddWithValue("$agent", raw.Descriptor.Capture.AgentId);
            insert.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            insert.Parameters.AddWithValue("$path", relative);
            insert.Parameters.AddWithValue("$sidecar", relative + ".manifest.json");
            insert.Parameters.AddWithValue("$descriptor", descriptor);
            insert.Parameters.AddWithValue("$recipe", recipeHash);
            insert.Parameters.AddWithValue("$algorithms", algorithmsBytes);
            insert.Parameters.AddWithValue("$compatibility", compatibilityBytes);
            insert.Parameters.AddWithValue("$source", sourceId.ToString("N"));
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
            await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
            using var fact = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(root, "products", identity + ".json")).ConfigureAwait(false));
            CollectionAssert.AreEqual(descriptor, fact.RootElement.GetProperty("Descriptor").GetBytesFromBase64());
            Assert.AreEqual(sourceId.ToString("N"), fact.RootElement.GetProperty("Sources")[0].GetString());
        }
        Assert.AreEqual(10L, new HourlyArtifactFactsPrototype(root, fixture.DatabasePath).Measure().Outputs);
        var backupPath = Path.Combine(fixture.Root, "durable-index.db");
        using (var indexed = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Mode=ReadOnly;Pooling=False"))
        using (var backup = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
        {
            await indexed.OpenAsync().ConfigureAwait(false);
            await backup.OpenAsync().ConfigureAwait(false);
            indexed.BackupDatabase(backup);
        }
        await VerifySnapshotAsync(root, backupPath).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OfflineDenseHourMeasuresShadowLayoutAndIndexOnlyBackup()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(360, sceneBearingOutputs: true,
            tenSecondCadence: true).ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var conversionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        var conversionCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var rssAfterConversion = process.WorkingSet64;
        var sizes = projection.Measure();
        Assert.AreEqual(360L, sizes.Captures);
        Assert.AreEqual(1440L, sizes.Outputs);
        Assert.AreEqual(360, Directory.EnumerateFiles(Path.Combine(root, "hours"), "*.json",
            SearchOption.AllDirectories).Count());
        Assert.AreEqual(1, Directory.EnumerateDirectories(Path.Combine(root, "hours")).Count());
        Assert.AreEqual(1440, Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json").Count());
        var indexPath = Path.Combine(root, "mutable.db");
        var backupPath = Path.Combine(fixture.Root, "index-backup.db");
        double backupMs;
        using (var source = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
        using (var target = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
        {
            await source.OpenAsync().ConfigureAwait(false);
            await target.OpenAsync().ConfigureAwait(false);
            started = Stopwatch.GetTimestamp();
            source.BackupDatabase(target);
            backupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        process.Refresh();
        var rssBeforeVerification = process.WorkingSet64;
        cpuBefore = process.TotalProcessorTime;
        started = Stopwatch.GetTimestamp();
        await VerifySnapshotAsync(root, backupPath).ConfigureAwait(false);
        var verifyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        var verificationCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var rssAfterVerification = process.WorkingSet64;
        using (var restoredIndex = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            await restoredIndex.OpenAsync().ConfigureAwait(false);
            using var integrity = restoredIndex.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.AreEqual("ok", await integrity.ExecuteScalarAsync().ConfigureAwait(false));
            integrity.CommandText = "SELECT count(*) FROM pragma_foreign_key_check;";
            Assert.AreEqual(0L, Convert.ToInt64(await integrity.ExecuteScalarAsync().ConfigureAwait(false),
                CultureInfo.InvariantCulture));
            integrity.CommandText = "SELECT (SELECT count(*) FROM capture_lookup),(SELECT count(*) FROM output_lookup);";
            using (var counts = await integrity.ExecuteReaderAsync().ConfigureAwait(false))
            {
                Assert.IsTrue(await counts.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(360L, counts.GetInt64(0));
                Assert.AreEqual(1440L, counts.GetInt64(1));
            }
            using var query = restoredIndex.CreateCommand();
            query.CommandText = """
                SELECT fact_path,fact_sha256 FROM capture_lookup
                WHERE agent_id='issue-106-gallery' AND sequence=180;
                """;
            using var selected = await query.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await selected.ReadAsync().ConfigureAwait(false));
            var retainedBytes = await File.ReadAllBytesAsync(Path.Combine(root, selected.GetString(0)))
                .ConfigureAwait(false);
            Assert.AreEqual(selected.GetString(1), Convert.ToHexString(SHA256.HashData(retainedBytes)));
        }
        var rawIds = new List<string>();
        using (var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            await journal.OpenAsync().ConfigureAwait(false);
            using var ids = journal.CreateCommand();
            ids.CommandText = "SELECT capture_id FROM raw_captures ORDER BY capture_sequence;";
            using var reader = await ids.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) rawIds.Add(reader.GetString(0));
        }
        var lookupTimes = new double[35];
        for (var i = 0; i < lookupTimes.Length; i++)
        {
            var id = rawIds[i * 137 % rawIds.Count];
            started = Stopwatch.GetTimestamp();
            var fact = projection.ReadFact(id);
            Assert.IsGreaterThan(0, fact.Length);
            lookupTimes[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(lookupTimes);
        TestContext.WriteLine($"W3M-style 360 captures / 1440 outputs in one 10-second-cadence UTC hour: " +
            $"source SQLite {new FileInfo(fixture.DatabasePath).Length} B, shadow index {sizes.IndexBytes} B, " +
            $"index WAL {sizes.WalBytes} B, raw facts {sizes.FactsBytes} B, output facts {sizes.ProductBytes} B, " +
            $"index backup {new FileInfo(backupPath).Length} B; conversion {conversionMs:F1} ms, backup-copy call " +
            $"{backupMs:F1} ms, full fact reference verification {verifyMs:F1} ms, warm indexed fact lookup " +
            $"median {lookupTimes[17]:F3} ms p95 {lookupTimes[33]:F3} ms (35 operations, one trial)");
        TestContext.WriteLine($"Process-wide sample: conversion CPU {conversionCpuMs:F1} ms, " +
            $"RSS {rssBefore}/{rssAfterConversion} B before/after; fact verification CPU {verificationCpuMs:F1} ms, " +
            $"RSS {rssBeforeVerification}/{rssAfterVerification} B before/after (not peak memory)");
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE1057_EXPORT"), "1", StringComparison.Ordinal))
        {
            var label = ExportLabel();
            var output = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-1057", label);
            if (Directory.Exists(output) || File.Exists(output))
                Assert.Fail($"Refusing to overwrite an existing disposable export at {output}.");
            var parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            RawIngressFileStore.EnsureNoSymbolicLinks(AppContext.BaseDirectory, parent);
            var pending = output + ".pending-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(pending);
            var exportStarted = Stopwatch.GetTimestamp();
            try
            {
                using (var original = new SqliteConnection($"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False"))
                using (var retained = new SqliteConnection($"Data Source={Path.Combine(pending, "source.db")};Pooling=False"))
                {
                    await original.OpenAsync().ConfigureAwait(false);
                    await retained.OpenAsync().ConfigureAwait(false);
                    original.BackupDatabase(retained);
                }
                File.Copy(backupPath, Path.Combine(pending, "mutable.db"));
                using (var selected = new SqliteConnection($"Data Source={Path.Combine(pending, "mutable.db")};Mode=ReadOnly;Pooling=False"))
                {
                    await selected.OpenAsync().ConfigureAwait(false);
                    using var references = selected.CreateCommand();
                    references.CommandText = """
                        SELECT fact_path FROM capture_lookup
                        UNION ALL SELECT fact_path FROM output_lookup;
                        """;
                    using var rows = await references.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await rows.ReadAsync().ConfigureAwait(false))
                    {
                        var relative = rows.GetString(0);
                        var sourcePath = Path.GetFullPath(Path.Combine(root, relative));
                        RawIngressFileStore.EnsureNoSymbolicLinks(root, sourcePath);
                        var targetPath = Path.GetFullPath(Path.Combine(pending, relative));
                        if (!targetPath.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            throw new InvalidDataException("Indexed fact path escapes offline export.");
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        File.Copy(sourcePath, targetPath);
                    }
                }
                await VerifySnapshotAsync(pending, Path.Combine(pending, "mutable.db")).ConfigureAwait(false);
                var identity = new
                {
                    Schema = "issue-1057-offline-dense-hour-v1",
                    Captures = 360,
                    Outputs = 1440,
                    SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                        Path.Combine(pending, "source.db")).ConfigureAwait(false))),
                    IndexSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                        Path.Combine(pending, "mutable.db")).ConfigureAwait(false))),
                    RawFacts = 360,
                    OutputFacts = 1440
                };
                await File.WriteAllBytesAsync(Path.Combine(pending, "dataset.json"),
                    JsonSerializer.SerializeToUtf8Bytes(identity)).ConfigureAwait(false);
                Directory.Move(pending, output);
                TestContext.WriteLine($"Offline export (source snapshot + index snapshot + 1800 retained facts " +
                    $"+ reference verification + final rename): {Stopwatch.GetElapsedTime(exportStarted).TotalMilliseconds:F1} ms");
            }
            finally
            {
                if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            }
            TestContext.WriteLine($"Disposable offline dataset exported to {output}; source and index are SQLite snapshots");
        }

    }

    [TestMethod]
    public async Task ExportedDenseHourComparesEquivalentRawLookupBytes()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-1057", ExportLabel());
        if (!File.Exists(Path.Combine(root, "source.db")) || !File.Exists(Path.Combine(root, "mutable.db")) ||
            !File.Exists(Path.Combine(root, "dataset.json")))
            Assert.Inconclusive("Run OfflineDenseHourMeasuresShadowLayoutAndIndexOnlyBackup with HVO_ISSUE1057_EXPORT=1 first.");
        using (var identity = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "dataset.json"))
            .ConfigureAwait(false)))
        {
            var metadata = identity.RootElement;
            Assert.AreEqual("issue-1057-offline-dense-hour-v1", metadata.GetProperty("Schema").GetString());
            Assert.AreEqual(360, metadata.GetProperty("Captures").GetInt32());
            Assert.AreEqual(1440, metadata.GetProperty("Outputs").GetInt32());
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Combine(root, "source.db")).ConfigureAwait(false))), metadata.GetProperty("SourceSha256").GetString());
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Combine(root, "mutable.db")).ConfigureAwait(false))), metadata.GetProperty("IndexSha256").GetString());
            Assert.AreEqual(360, Directory.EnumerateFiles(Path.Combine(root, "hours"), "*.json",
                SearchOption.AllDirectories).Count());
            Assert.AreEqual(1440, Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json").Count());
        }
        await VerifySnapshotAsync(root, Path.Combine(root, "mutable.db")).ConfigureAwait(false);
        using var source = new SqliteConnection($"Data Source={Path.Combine(root, "source.db")};Mode=ReadOnly;Pooling=False");
        using var index = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        await index.OpenAsync().ConfigureAwait(false);
        using (var sourceRows = source.CreateCommand())
        using (var shadowRows = index.CreateCommand())
        {
            sourceRows.CommandText = """
                SELECT capture_id,manifest_json,state,retention_hold,agent_id,capture_sequence,
                       exposure_started_unix_ms / 3600000 FROM raw_captures ORDER BY capture_sequence;
                """;
            shadowRows.CommandText = """
                SELECT capture_id,fact_path,state,retention_hold,agent_id,sequence,exposure_hour
                FROM capture_lookup ORDER BY sequence;
                """;
            using var originals = await sourceRows.ExecuteReaderAsync().ConfigureAwait(false);
            using var projected = await shadowRows.ExecuteReaderAsync().ConfigureAwait(false);
            var compared = 0;
            while (await originals.ReadAsync().ConfigureAwait(false))
            {
                Assert.IsTrue(await projected.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(originals.GetString(0), projected.GetString(0));
                Assert.AreEqual(originals.GetString(2), projected.GetString(2));
                Assert.AreEqual(originals.GetInt64(3), projected.GetInt64(3));
                Assert.AreEqual(originals.GetString(4), projected.GetString(4));
                Assert.AreEqual(originals.GetInt64(5), projected.GetInt64(5));
                Assert.AreEqual(originals.GetInt64(6), projected.GetInt64(6));
                using var fact = JsonDocument.Parse(await File.ReadAllBytesAsync(
                    Path.Combine(root, projected.GetString(1))).ConfigureAwait(false));
                CollectionAssert.AreEqual((byte[])originals.GetValue(1), fact.RootElement.GetProperty("Manifest").GetBytesFromBase64());
                compared++;
            }
            Assert.IsFalse(await projected.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(360, compared);
        }
        using (var sourceRows = source.CreateCommand())
        using (var shadowRows = index.CreateCommand())
        {
            sourceRows.CommandText = """
                SELECT output_identity_sha256,descriptor_json,availability_state,capture_id,committed_unix_ms
                FROM processing_outputs
                ORDER BY output_identity_sha256;
                """;
            shadowRows.CommandText = """
                SELECT output_identity,fact_path,availability,capture_id,committed_unix_ms
                FROM output_lookup ORDER BY output_identity;
                """;
            using var originals = await sourceRows.ExecuteReaderAsync().ConfigureAwait(false);
            using var projected = await shadowRows.ExecuteReaderAsync().ConfigureAwait(false);
            var compared = 0;
            while (await originals.ReadAsync().ConfigureAwait(false))
            {
                Assert.IsTrue(await projected.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(originals.GetString(0), projected.GetString(0));
                Assert.AreEqual(originals.GetString(2), projected.GetString(2));
                Assert.AreEqual(originals.GetString(3), projected.GetString(3));
                Assert.AreEqual(originals.GetInt64(4), projected.GetInt64(4));
                using var fact = JsonDocument.Parse(await File.ReadAllBytesAsync(
                    Path.Combine(root, projected.GetString(1))).ConfigureAwait(false));
                CollectionAssert.AreEqual((byte[])originals.GetValue(1), fact.RootElement.GetProperty("Descriptor").GetBytesFromBase64());
                using var links = source.CreateCommand();
                links.CommandText = """
                    SELECT source_artifact_id FROM processing_output_sources
                    WHERE output_identity_sha256=$identity ORDER BY source_ordinal;
                    """;
                links.Parameters.AddWithValue("$identity", originals.GetString(0));
                using var linkRows = await links.ExecuteReaderAsync().ConfigureAwait(false);
                var sourceIds = new List<string>();
                while (await linkRows.ReadAsync().ConfigureAwait(false)) sourceIds.Add(linkRows.GetString(0));
                CollectionAssert.AreEqual(sourceIds, fact.RootElement.GetProperty("Sources").EnumerateArray()
                    .Select(static element => element.GetString()!).ToArray());
                compared++;
            }
            Assert.IsFalse(await projected.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(1440, compared);
        }
        using var old = source.CreateCommand();
        old.CommandText = "SELECT capture_id,manifest_json FROM raw_captures WHERE agent_id='issue-106-gallery' AND capture_sequence=$sequence;";
        var oldSequence = old.Parameters.Add("$sequence", SqliteType.Integer);
        using var shadow = index.CreateCommand();
        shadow.CommandText = "SELECT capture_id,fact_path,fact_sha256 FROM capture_lookup WHERE agent_id='issue-106-gallery' AND sequence=$sequence;";
        var newSequence = shadow.Parameters.Add("$sequence", SqliteType.Integer);
        var oldSamples = new double[35];
        var newSamples = new double[35];
        for (var i = 0; i < oldSamples.Length; i++)
        {
            var sequence = i * 137 % 360 + 1;
            oldSequence.Value = sequence;
            newSequence.Value = sequence;
            var started = Stopwatch.GetTimestamp();
            using var original = await old.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await original.ReadAsync().ConfigureAwait(false));
            var id = original.GetString(0);
            var oldBytes = (byte[])original.GetValue(1);
            var oldHash = Convert.ToHexString(SHA256.HashData(oldBytes));
            var oldManifest = CaptureContractJson.ParseManifest(oldBytes);
            Assert.IsTrue(oldManifest.IsValid);
            oldSamples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            using var projected = await shadow.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await projected.ReadAsync().ConfigureAwait(false));
            var projectedId = projected.GetString(0);
            var fact = await File.ReadAllBytesAsync(Path.Combine(root, projected.GetString(1))).ConfigureAwait(false);
            Assert.AreEqual(projected.GetString(2), Convert.ToHexString(SHA256.HashData(fact)));
            using var document = JsonDocument.Parse(fact);
            var manifest = document.RootElement.GetProperty("Manifest").GetBytesFromBase64();
            var newHash = Convert.ToHexString(SHA256.HashData(manifest));
            Assert.IsTrue(CaptureContractJson.ParseManifest(manifest).IsValid);
            Assert.AreEqual(id, projectedId);
            Assert.AreEqual(oldHash, newHash);
            newSamples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            CollectionAssert.AreEqual(oldBytes, manifest);
        }
        Array.Sort(oldSamples);
        Array.Sort(newSamples);
        TestContext.WriteLine($"Exported 360-capture hour, 35 varied warm raw reads: source DB+manifest SHA/parse median " +
            $"{oldSamples[17]:F3} ms p95 {oldSamples[33]:F3} ms; index+fact read+SHA+JSON decode median " +
            $"{newSamples[17]:F3} ms p95 {newSamples[33]:F3} ms. Candidate additionally hashes and decodes the wrapper.");
    }

    [TestMethod]
    public async Task ShadowProjectionPreservesCommittedEvidenceAndLateState()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(100, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var shadowRoot = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var first = projection.Measure();
        Assert.AreEqual(100L, first.Captures);
        Assert.AreEqual(400L, first.Outputs);
        Assert.IsGreaterThan(0, first.FactsBytes);
        Assert.IsGreaterThan(0, first.ProductBytes);
        var factCount = Directory.EnumerateFiles(Path.Combine(shadowRoot, "hours"), "*.json",
            SearchOption.AllDirectories).Count();
        var productCount = Directory.EnumerateFiles(Path.Combine(shadowRoot, "products"), "*.json").Count();
        Assert.AreEqual(100, factCount);
        Assert.AreEqual(400, productCount);
        TestContext.WriteLine($"100 captures: mutable DB {first.IndexBytes} B, WAL {first.WalBytes} B, " +
            $"{factCount} raw fact files {first.FactsBytes} B, {productCount} output fact files {first.ProductBytes} B");
        using var source = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        using var list = source.CreateCommand();
        list.CommandText = "SELECT capture_id,manifest_json FROM raw_captures ORDER BY capture_sequence;";
        using var reader = await list.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var fact = projection.ReadFact(reader.GetString(0));
            using var document = JsonDocument.Parse(fact);
            var manifest = document.RootElement.GetProperty("Manifest").GetBytesFromBase64();
            CollectionAssert.AreEqual((byte[])reader.GetValue(1), manifest);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(manifest)),
                document.RootElement.GetProperty("ManifestSha256").GetString());
            Assert.AreEqual(reader.GetString(0), document.RootElement.GetProperty("CaptureId").GetString());
        }
        var replayStarted = Stopwatch.GetTimestamp();
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        TestContext.WriteLine($"Unchanged 100-capture reproject: {Stopwatch.GetElapsedTime(replayStarted).TotalMilliseconds:F1} ms");
        var repeated = projection.Measure();
        Assert.AreEqual(first, repeated);
        using (var mutate = source.CreateCommand())
        {
            mutate.CommandText = """
                UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;
                UPDATE processing_outputs SET availability_state='Missing'
                WHERE capture_sequence=1;
                """;
            await mutate.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var late = projection.Measure();
        Assert.AreEqual(first.FactsBytes, late.FactsBytes);
        Assert.AreEqual(first.ProductBytes, late.ProductBytes);
        Assert.AreEqual(first.Captures, late.Captures);
        Assert.AreEqual(first.Outputs, late.Outputs);
        using var index = new SqliteConnection($"Data Source={Path.Combine(shadowRoot, "mutable.db")};Mode=ReadOnly;Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var check = index.CreateCommand();
        check.CommandText = "SELECT retention_hold FROM capture_lookup WHERE sequence=1;";
        Assert.AreEqual(0L, Convert.ToInt64(await check.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        check.CommandText = "SELECT count(*) FROM output_lookup WHERE availability='Missing';";
        Assert.AreEqual(4L, Convert.ToInt64(await check.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        using var outputs = source.CreateCommand();
        outputs.CommandText = """
            SELECT output.output_identity_sha256, output.descriptor_json, source.source_artifact_id
            FROM processing_outputs output
            JOIN processing_output_sources source ON source.output_identity_sha256=output.output_identity_sha256
            ORDER BY output.output_identity_sha256, source.source_ordinal;
            """;
        using var outputReader = await outputs.ExecuteReaderAsync().ConfigureAwait(false);
        var checkedOutputs = 0;
        while (await outputReader.ReadAsync().ConfigureAwait(false))
        {
            var productPath = Path.Combine(shadowRoot, "products", outputReader.GetString(0) + ".json");
            using var product = JsonDocument.Parse(await File.ReadAllBytesAsync(productPath).ConfigureAwait(false));
            CollectionAssert.AreEqual((byte[])outputReader.GetValue(1),
                product.RootElement.GetProperty("Descriptor").GetBytesFromBase64());
            Assert.AreEqual(outputReader.GetString(2),
                product.RootElement.GetProperty("Sources")[0].GetString());
            checkedOutputs++;
        }
        Assert.AreEqual(400, checkedOutputs);
        using (var backfill = source.CreateCommand())
        {
            backfill.CommandText = """
                SELECT output_identity_sha256, capture_id, agent_id, artifact_id, payload_relative_path,
                       sidecar_relative_path, descriptor_json, recipe_identity_sha256, algorithms_json,
                       compatibility_json, total_integration_ticks, capture_sequence, committed_unix_ms
                FROM processing_outputs WHERE capture_sequence=1 LIMIT 1;
                """;
            using var sample = await backfill.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await sample.ReadAsync().ConfigureAwait(false));
            var original = CaptureContractJson.ParseManifest((byte[])sample.GetValue(6)).Document?.Manifest;
            Assert.IsNotNull(original);
            var variant = "late-replay-preview";
            var derived = original with
            {
                Descriptor = original.Descriptor with
                {
                    Artifact = original.Descriptor.Artifact with
                    {
                        Variant = variant,
                        ArtifactId = Guid.NewGuid()
                    }
                }
            };
            var identity = ProcessingIdentity.CreateOutputIdentity(derived.Descriptor.Artifact.Role,
                variant, sample.GetString(7), derived.Descriptor.Artifact.SourceArtifactIds);
            derived = derived with
            {
                Descriptor = derived.Descriptor with
                {
                    Artifact = derived.Descriptor.Artifact with { ArtifactId = ProcessingIdentity.CreateArtifactId(identity) }
                }
            };
            using var insert = source.CreateCommand();
            insert.CommandText = """
                INSERT INTO processing_nodes(capture_id,node_id,required,dependencies_json,recipe_name,output_role,
                    output_variant,plan_sha256,status,attempt,completed_unix_ms)
                VALUES ($capture,$node,1,'[]','gallery-preview','Preview',$variant,$plan,'Completed',1,$time);
                INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,role,
                    variant,payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,
                    algorithms_json,compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms)
                VALUES ($identity,$capture,$agent,$node,$artifact,'Preview',$variant,$path,$sidecar,$descriptor,$recipe,
                    $algorithms,$compatibility,$integration,$sequence,$time);
                INSERT INTO processing_output_sources(output_identity_sha256,source_ordinal,source_artifact_id)
                VALUES ($identity,0,$source);
                """;
            insert.Parameters.AddWithValue("$capture", sample.GetString(1));
            insert.Parameters.AddWithValue("$node", variant);
            insert.Parameters.AddWithValue("$variant", variant);
            insert.Parameters.AddWithValue("$plan", new string('A', 64));
            insert.Parameters.AddWithValue("$time", sample.GetInt64(12) + 86_400_000);
            insert.Parameters.AddWithValue("$identity", identity);
            insert.Parameters.AddWithValue("$agent", sample.GetString(2));
            insert.Parameters.AddWithValue("$artifact", derived.Descriptor.Artifact.ArtifactId.ToString("N"));
            insert.Parameters.AddWithValue("$path", derived.RelativeArtifactPath);
            insert.Parameters.AddWithValue("$sidecar", sample.GetString(5) + ".late");
            insert.Parameters.AddWithValue("$descriptor", CaptureContractJson.Serialize(derived));
            insert.Parameters.AddWithValue("$recipe", sample.GetString(7));
            insert.Parameters.AddWithValue("$algorithms", sample.GetValue(8));
            insert.Parameters.AddWithValue("$compatibility", sample.GetValue(9));
            insert.Parameters.AddWithValue("$integration", sample.GetInt64(10));
            insert.Parameters.AddWithValue("$sequence", sample.GetInt64(11));
            insert.Parameters.AddWithValue("$source", derived.Descriptor.Artifact.SourceArtifactIds.Single().ToString("N"));
            await sample.DisposeAsync().ConfigureAwait(false);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var backfilled = projection.Measure();
        Assert.AreEqual(first.FactsBytes, backfilled.FactsBytes);
        Assert.AreEqual(first.Captures, backfilled.Captures);
        Assert.AreEqual(first.Outputs + 1, backfilled.Outputs);
        Assert.IsGreaterThan(first.ProductBytes, backfilled.ProductBytes);
        var existingProduct = Directory.EnumerateFiles(Path.Combine(shadowRoot, "products"), "*.json").First();
        var originalProductBytes = await File.ReadAllBytesAsync(existingProduct).ConfigureAwait(false);
        await File.AppendAllTextAsync(existingProduct, "corrupt").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(existingProduct, originalProductBytes).ConfigureAwait(false);
        var sealedHour = Directory.EnumerateFiles(Path.Combine(shadowRoot, "hours"), "*.json",
            SearchOption.AllDirectories).First();
        await File.AppendAllTextAsync(sealedHour, "corrupt").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MissingIndexedProductOrHourFailsClosed()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var productPath = Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json").First();
        File.Delete(productPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);

        // A separate fixture isolates the hour case from the already-missing product.
        using var hourFixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var hourRoot = Path.Combine(hourFixture.Root, "shadow");
        var hourProjection = new HourlyArtifactFactsPrototype(hourRoot, hourFixture.DatabasePath);
        await hourProjection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        File.Delete(Directory.EnumerateFiles(Path.Combine(hourRoot, "hours"), "*.json",
            SearchOption.AllDirectories).First());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => hourProjection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IndexedRawPathEscapeAndSourceOrdinalGapAreRejected()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var source = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        using var identity = source.CreateCommand();
        identity.CommandText = "SELECT output_identity_sha256 FROM processing_outputs LIMIT 1;";
        var outputId = (string)(await identity.ExecuteScalarAsync().ConfigureAwait(false))!;
        using (var gap = source.CreateCommand())
        {
            gap.CommandText = """
                UPDATE processing_output_sources SET source_ordinal=2
                WHERE output_identity_sha256=$output;
                """;
            gap.Parameters.AddWithValue("$output", outputId);
            Assert.AreEqual(1, await gap.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        using (var repair = source.CreateCommand())
        {
            repair.CommandText = "UPDATE processing_output_sources SET source_ordinal=0 WHERE output_identity_sha256=$output;";
            repair.Parameters.AddWithValue("$output", outputId);
            Assert.AreEqual(1, await repair.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var index = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var update = index.CreateCommand();
        update.CommandText = """
            UPDATE capture_lookup SET fact_path='../outside.json' WHERE sequence=1;
            """;
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
        using var capture = source.CreateCommand();
        capture.CommandText = "SELECT capture_id FROM raw_captures WHERE capture_sequence=1;";
        var captureId = (string)(await capture.ExecuteScalarAsync().ConfigureAwait(false))!;
        Assert.ThrowsExactly<InvalidDataException>(() => projection.ReadFact(captureId));
    }

    [TestMethod]
    public async Task LateRawCaptureInExistingExposureHourAddsFactWithoutRewritingNeighbor()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await journal.OpenAsync().ConfigureAwait(false);
        using var lookup = journal.CreateCommand();
        lookup.CommandText = """
            SELECT capture_id,raw_artifact_id,agent_id,descriptor_sha256,manifest_sha256,payload_sha256,
                payload_length,payload_relative_path,sidecar_relative_path,manifest_json,exposure_started_unix_ms,
                durable_ingress_unix_ms,committed_unix_ms,state,retention_hold,evidence_origin
            FROM raw_captures WHERE capture_sequence=1;
            """;
        using var sample = await lookup.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await sample.ReadAsync().ConfigureAwait(false));
        var existingId = sample.GetString(0);
        var before = projection.ReadFact(existingId);
        var newCapture = Guid.NewGuid();
        var newArtifact = Guid.NewGuid();
        var original = CaptureContractJson.ParseManifest((byte[])sample.GetValue(9)).Document?.Manifest;
        Assert.IsNotNull(original);
        var manifest = original with
        {
            Descriptor = original.Descriptor with
            {
                Capture = original.Descriptor.Capture with { CaptureId = newCapture, CaptureSequence = 3 },
                Artifact = original.Descriptor.Artifact with { ArtifactId = newArtifact }
            },
            RelativeArtifactPath = $"raw/{newArtifact:N}.bin"
        };
        using var insert = journal.CreateCommand();
        insert.CommandText = """
            INSERT INTO raw_capture_assignments VALUES ($capture,$artifact,$agent,3);
            INSERT INTO raw_captures(capture_id,raw_artifact_id,agent_id,capture_sequence,descriptor_sha256,
                manifest_sha256,payload_sha256,payload_length,payload_relative_path,sidecar_relative_path,
                manifest_json,exposure_started_unix_ms,durable_ingress_unix_ms,committed_unix_ms,state,
                retention_hold,evidence_origin)
            VALUES ($capture,$artifact,$agent,3,$descriptor,$manifest,$payload,$length,$path,$sidecar,
                $json,$exposure,$durable,$committed,$state,$hold,$origin);
            """;
        insert.Parameters.AddWithValue("$capture", newCapture.ToString("N"));
        insert.Parameters.AddWithValue("$artifact", newArtifact.ToString("N"));
        insert.Parameters.AddWithValue("$agent", sample.GetString(2));
        insert.Parameters.AddWithValue("$descriptor", CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor));
        insert.Parameters.AddWithValue("$manifest", CaptureContractJson.ComputeManifestSha256(manifest));
        insert.Parameters.AddWithValue("$payload", sample.GetString(5));
        insert.Parameters.AddWithValue("$length", sample.GetInt64(6));
        insert.Parameters.AddWithValue("$path", manifest.RelativeArtifactPath);
        insert.Parameters.AddWithValue("$sidecar", $"raw/{newArtifact:N}.json");
        insert.Parameters.AddWithValue("$json", CaptureContractJson.Serialize(manifest));
        insert.Parameters.AddWithValue("$exposure", sample.GetInt64(10));
        insert.Parameters.AddWithValue("$durable", sample.GetInt64(11));
        insert.Parameters.AddWithValue("$committed", sample.GetInt64(12));
        insert.Parameters.AddWithValue("$state", sample.GetString(13));
        insert.Parameters.AddWithValue("$hold", sample.GetInt64(14));
        insert.Parameters.AddWithValue("$origin", sample.GetString(15));
        await sample.DisposeAsync().ConfigureAwait(false);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(before, projection.ReadFact(existingId));
        Assert.AreEqual(3L, projection.Measure().Captures);
        Assert.IsGreaterThan(0, projection.ReadFact(newCapture.ToString("N")).Length);
        var retained = Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json")
            .ToDictionary(static file => file, static file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        using var remove = journal.CreateCommand();
        remove.CommandText = "DELETE FROM processing_outputs WHERE capture_sequence=1;";
        Assert.AreEqual(4, await remove.ExecuteNonQueryAsync().ConfigureAwait(false));
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var shadow = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Mode=ReadOnly;Pooling=False");
        await shadow.OpenAsync().ConfigureAwait(false);
        using var removed = shadow.CreateCommand();
        removed.CommandText = "SELECT count(*) FROM output_lookup WHERE availability='RemovedFromSource';";
        Assert.AreEqual(4L, Convert.ToInt64(await removed.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (file, digest) in retained)
            Assert.AreEqual(digest, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file).ConfigureAwait(false))));
    }

    [TestMethod]
    public async Task PublicationInterruptionsReconcileFromAuthoritativeJournal()
    {
        foreach (var point in new[] { "before-rename", "after-rename" })
        {
            using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
                .ConfigureAwait(false);
            var root = Path.Combine(fixture.Root, "shadow");
            var injected = false;
            var interrupted = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath, observed =>
            {
                if (observed == point && !injected)
                {
                    injected = true;
                    throw new IOException(point);
                }
            });
            await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.ProjectAsync(CancellationToken.None))
                .ConfigureAwait(false);
            var restarted = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
            await restarted.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, restarted.Measure().Captures);
            Assert.AreEqual(8L, restarted.Measure().Outputs);
            await restarted.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, restarted.Measure().Captures);
        }
    }

    [TestMethod]
    public async Task IndexOnlySnapshotVerifiesRetainedFactsWithoutCopyingThem()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshotPath = Path.Combine(fixture.Root, "index-snapshot.db");
        using (var source = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Mode=ReadOnly;Pooling=False"))
        using (var target = new SqliteConnection($"Data Source={snapshotPath};Pooling=False"))
        {
            await source.OpenAsync().ConfigureAwait(false);
            await target.OpenAsync().ConfigureAwait(false);
            source.BackupDatabase(target);
        }
        await VerifySnapshotAsync(root, snapshotPath).ConfigureAwait(false);
        for (var mutation = 0; mutation < 3; mutation++)
        {
            var tamperedPath = Path.Combine(fixture.Root, $"tampered-index-{mutation}.db");
            File.Copy(snapshotPath, tamperedPath);
            using var index = new SqliteConnection($"Data Source={tamperedPath};Pooling=False");
            await index.OpenAsync().ConfigureAwait(false);
            using var change = index.CreateCommand();
            if (mutation == 0)
                change.CommandText = "UPDATE capture_lookup SET exposure_unix_ms=exposure_unix_ms+1 WHERE capture_id=(SELECT capture_id FROM capture_lookup LIMIT 1);";
            else if (mutation == 1)
                change.CommandText = "UPDATE output_lookup SET role='Raw' WHERE output_identity=(SELECT output_identity FROM output_lookup LIMIT 1);";
            else
                change.CommandText = "UPDATE output_lookup SET variant='tampered' WHERE output_identity=(SELECT output_identity FROM output_lookup LIMIT 1);";
            Assert.AreEqual(1, await change.ExecuteNonQueryAsync().ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VerifySnapshotAsync(root, tamperedPath))
                .ConfigureAwait(false);
        }
        var changedProduct = Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json").First();
        var original = await File.ReadAllBytesAsync(changedProduct).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(original))
        {
            var identity = document.RootElement.GetProperty("Identity").GetString();
            var descriptor = document.RootElement.GetProperty("Descriptor").GetBytesFromBase64();
            await File.WriteAllBytesAsync(changedProduct, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Identity = identity,
                Descriptor = descriptor,
                Sources = CorruptSources
            })).ConfigureAwait(false);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VerifySnapshotAsync(root, snapshotPath))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(changedProduct, original).ConfigureAwait(false);
        var linkedProduct = Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json").First();
        var linkTarget = Path.Combine(fixture.Root, "external-product.json");
        await File.WriteAllBytesAsync(linkTarget, await File.ReadAllBytesAsync(linkedProduct).ConfigureAwait(false))
            .ConfigureAwait(false);
        File.Delete(linkedProduct);
        File.CreateSymbolicLink(linkedProduct, linkTarget);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VerifySnapshotAsync(root, snapshotPath))
            .ConfigureAwait(false);
        File.Delete(linkedProduct);
        await File.WriteAllBytesAsync(linkedProduct, original).ConfigureAwait(false);
        File.Delete(Directory.EnumerateFiles(Path.Combine(root, "products"), "*.json").First());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VerifySnapshotAsync(root, snapshotPath))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ConcurrentSourceCommitDuringScanLeavesShadowUnready()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var wrote = false;
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath, point =>
        {
            if (point != "during-scan" || wrote) return;
            wrote = true;
            using var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
            writer.Open();
            using var update = writer.CreateCommand();
            update.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=2;";
            update.ExecuteNonQuery();
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidDataException>(() => projection.ReadFact("unused"));
        var restarted = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await restarted.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, restarted.Measure().Captures);
    }

    [TestMethod]
    public async Task DisposableSourceRevisionTriggersTrackCommittedChangesAndRollback()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        using var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await journal.OpenAsync().ConfigureAwait(false);
        using (var setup = journal.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE shadow_revision(revision INTEGER NOT NULL);
                INSERT INTO shadow_revision VALUES (0);
                CREATE TRIGGER shadow_raw_update AFTER UPDATE ON raw_captures BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                CREATE TRIGGER shadow_output_insert AFTER INSERT ON processing_outputs BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                CREATE TRIGGER shadow_output_delete AFTER DELETE ON processing_outputs BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                CREATE TRIGGER shadow_source_insert AFTER INSERT ON processing_output_sources BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                CREATE TRIGGER shadow_source_delete AFTER DELETE ON processing_output_sources BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                CREATE TRIGGER shadow_raw_pin_insert AFTER INSERT ON processing_execution_input_pins BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                CREATE TRIGGER shadow_raw_pin_update AFTER UPDATE ON processing_execution_input_pins BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                """;
            await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var revision = journal.CreateCommand();
        revision.CommandText = "SELECT revision FROM shadow_revision;";
        Assert.AreEqual(0L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        using (var transaction = (SqliteTransaction)await journal.BeginTransactionAsync().ConfigureAwait(false))
        {
            using var change = journal.CreateCommand();
            change.Transaction = transaction;
            change.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;";
            await change.ExecuteNonQueryAsync().ConfigureAwait(false);
            revision.Transaction = transaction;
            Assert.AreEqual(1L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        revision.Transaction = null;
        Assert.AreEqual(0L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        using (var change = journal.CreateCommand())
        {
            change.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;";
            await change.ExecuteNonQueryAsync().ConfigureAwait(false);
            change.CommandText = "DELETE FROM processing_outputs WHERE capture_sequence=1;";
            Assert.AreEqual(4, await change.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        Assert.AreEqual(9L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        using (var seed = journal.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO processing_graph_revisions(
                    revision_id,graph_name,revision_name,lifecycle,definition_identity_sha256,
                    shared_plan_identity_sha256,local_plan_identity_sha256,pipeline_json,definition_json,
                    frozen_plan_json,nodes_json,created_unix_ms,validated_unix_ms,activated_unix_ms)
                VALUES($revision,'trigger-test','1','Active',$sha,$sha,$sha,x'7B7D',x'7B7D',x'7B7D',x'5B5D',0,0,0);
                INSERT INTO processing_executions(execution_id,execution_class,status,capture_id,primary_artifact_id,
                    graph_revision_id,definition_identity_sha256,shared_plan_identity_sha256,
                    local_plan_identity_sha256,frozen_plan_json,configuration_json,trigger_kind,priority,payload_bytes,
                    accepted_unix_ms,available_unix_ms,deadline_unix_ms,maximum_age_unix_ms,allow_automatic_publication)
                VALUES($execution,'Replay','Pending',$capture,$artifact,$revision,$sha,$sha,$sha,
                    x'7B7D',x'7B7D','trigger-test',0,0,0,0,0,0,1);
                INSERT INTO processing_execution_input_pins(execution_id,raw_capture_row_id,artifact_id)
                SELECT $execution,raw_capture_row_id,raw_artifact_id FROM raw_captures WHERE capture_sequence=2;
                """;
            seed.Parameters.AddWithValue("$revision", new string('A', 64));
            seed.Parameters.AddWithValue("$sha", new string('B', 64));
            seed.Parameters.AddWithValue("$execution", Guid.NewGuid().ToString("N"));
            seed.Parameters.AddWithValue("$capture", new string('2', 32));
            seed.Parameters.AddWithValue("$artifact", new string('3', 32));
            await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
            Assert.AreEqual(10L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
                CultureInfo.InvariantCulture));
            seed.CommandText = "UPDATE processing_execution_input_pins SET released_flag=1 WHERE execution_id=$execution;";
            Assert.AreEqual(1, await seed.ExecuteNonQueryAsync().ConfigureAwait(false));
            Assert.AreEqual(11L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
                CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public async Task PopulatedDisposableRevisionMigrationRollsBackAndRetriesWithoutLosingEvidence()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        using var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await journal.OpenAsync().ConfigureAwait(false);
        using var before = journal.CreateCommand();
        before.CommandText = "SELECT manifest_json FROM raw_captures WHERE capture_sequence=1;";
        var original = (byte[])(await before.ExecuteScalarAsync().ConfigureAwait(false))!;
        using (var transaction = (SqliteTransaction)await journal.BeginTransactionAsync().ConfigureAwait(false))
        {
            using var migration = journal.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE shadow_revision(epoch TEXT NOT NULL, revision INTEGER NOT NULL);
                INSERT INTO shadow_revision VALUES ('test-epoch',0);
                CREATE TRIGGER shadow_raw_insert AFTER INSERT ON raw_captures BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                """;
            await migration.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        using var check = journal.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name IN ('shadow_revision','shadow_raw_insert');";
        Assert.AreEqual(0L, Convert.ToInt64(await check.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
        CollectionAssert.AreEqual(original, (byte[])(await before.ExecuteScalarAsync().ConfigureAwait(false))!);
        using (var transaction = (SqliteTransaction)await journal.BeginTransactionAsync().ConfigureAwait(false))
        {
            using var migration = journal.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE shadow_revision(epoch TEXT NOT NULL, revision INTEGER NOT NULL);
                INSERT INTO shadow_revision VALUES ('test-epoch',0);
                CREATE TRIGGER shadow_raw_insert AFTER INSERT ON raw_captures BEGIN
                    UPDATE shadow_revision SET revision=revision+1;
                END;
                """;
            await migration.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(2L, Convert.ToInt64(await check.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
        CollectionAssert.AreEqual(original, (byte[])(await before.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed, internally declared table and event identifiers form disposable migration SQL.")]
    public async Task DisposableFullRevisionMatrixMigratesAtomicallyAndOldValidatorRefuses()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var raw = connection.CreateCommand();
        raw.CommandText = "SELECT manifest_json FROM raw_captures WHERE capture_sequence=1;";
        var original = (byte[])(await raw.ExecuteScalarAsync().ConfigureAwait(false))!;
        var epoch = Guid.NewGuid().ToString("N");
        var tables = new[]
        {
            "raw_captures", "processing_outputs", "processing_output_sources",
            "processing_execution_input_pins", "processing_execution_output_input_pins"
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE shadow_revision(epoch TEXT PRIMARY KEY, revision INTEGER NOT NULL CHECK(revision >= 0));
                INSERT INTO shadow_revision VALUES ($epoch,0);
                """;
            migration.Parameters.AddWithValue("$epoch", epoch);
            await migration.ExecuteNonQueryAsync().ConfigureAwait(false);
            foreach (var table in tables)
                foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                {
                    migration.CommandText = $"""
                    CREATE TRIGGER shadow_revision_{table}_{operation}
                    AFTER {operation} ON {table} BEGIN
                        UPDATE shadow_revision SET revision=revision+1;
                    END;
                    """;
                    await migration.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            migration.CommandText = "PRAGMA user_version=14;";
            await migration.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (attempt == 0)
                await transaction.RollbackAsync().ConfigureAwait(false);
            else
                await transaction.CommitAsync().ConfigureAwait(false);

            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(attempt == 0 ? 13L : 14L, Convert.ToInt64(
                await check.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
            check.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'shadow_revision_%';";
            Assert.AreEqual(attempt == 0 ? 0L : 15L, Convert.ToInt64(
                await check.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
            CollectionAssert.AreEqual(original, (byte[])(await raw.ExecuteScalarAsync().ConfigureAwait(false))!);
        }
        using var revision = connection.CreateCommand();
        revision.CommandText = "SELECT epoch,revision FROM shadow_revision;";
        using (var row = await revision.ExecuteReaderAsync().ConfigureAwait(false))
        {
            Assert.IsTrue(await row.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(epoch, row.GetString(0));
            Assert.AreEqual(0L, row.GetInt64(1));
        }
        using var change = connection.CreateCommand();
        change.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;";
        Assert.AreEqual(1, await change.ExecuteNonQueryAsync().ConfigureAwait(false));
        revision.CommandText = "SELECT revision FROM shadow_revision;";
        Assert.AreEqual(1L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
            CultureInfo.InvariantCulture));
        var oldJournal = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => oldJournal.InitializeAsync(CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DisposableRevisionTriggerMeasuresWriteAmplification()
    {
        const int updates = 1000;
        var samples = new List<(bool Trigger, double Milliseconds, long WalBytes)>();
        for (var trial = 0; trial < 5; trial++)
            foreach (var enabled in new[] { false, true })
            {
                using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
                    .ConfigureAwait(false);
                using var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
                await journal.OpenAsync().ConfigureAwait(false);
                using (var setup = journal.CreateCommand())
                {
                    setup.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA wal_autocheckpoint=0;";
                    await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (enabled)
                    {
                        setup.CommandText = """
                        CREATE TABLE shadow_revision(revision INTEGER NOT NULL);
                        INSERT INTO shadow_revision VALUES (0);
                        CREATE TRIGGER shadow_raw_update AFTER UPDATE ON raw_captures BEGIN
                            UPDATE shadow_revision SET revision=revision+1;
                        END;
                        """;
                        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
                        setup.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                using var change = journal.CreateCommand();
                change.CommandText = "UPDATE raw_captures SET retention_hold=$hold WHERE capture_sequence=1;";
                var hold = change.Parameters.Add("$hold", SqliteType.Integer);
                for (var i = 0; i < 20; i++)
                {
                    hold.Value = i % 2;
                    await change.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                using (var checkpoint = journal.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < updates; i++)
                {
                    hold.Value = i % 2;
                    await change.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var wal = fixture.DatabasePath + "-wal";
                samples.Add((enabled, milliseconds, File.Exists(wal) ? new FileInfo(wal).Length : 0));
                if (enabled)
                {
                    using var revision = journal.CreateCommand();
                    revision.CommandText = "SELECT revision FROM shadow_revision;";
                    Assert.AreEqual(updates + 20L, Convert.ToInt64(await revision.ExecuteScalarAsync().ConfigureAwait(false),
                        CultureInfo.InvariantCulture));
                }
            }
        foreach (var enabled in new[] { false, true })
        {
            var times = samples.Where(sample => sample.Trigger == enabled).Select(sample => sample.Milliseconds)
                .Order().ToArray();
            var walBytes = samples.Where(sample => sample.Trigger == enabled).Select(sample => sample.WalBytes)
                .Distinct().ToArray();
            Assert.HasCount(1, walBytes);
            TestContext.WriteLine($"Five independent disposable fixtures, 1000 autocommit hold updates; " +
                $"trigger={enabled}: median {times[2]:F1} ms, min {times[0]:F1} ms, " +
                $"max {times[4]:F1} ms, WAL {walBytes[0]} B");
        }
    }

    internal static async Task VerifySnapshotAsync(string root, string snapshotPath,
        long? fromUnixMs = null, long? toUnixMs = null, long? minSequence = null,
        long? maxSequence = null, string? agentId = null)
    {
        if (fromUnixMs.HasValue != toUnixMs.HasValue || fromUnixMs >= toUnixMs)
            throw new ArgumentOutOfRangeException(nameof(fromUnixMs));
        if (minSequence.HasValue != maxSequence.HasValue ||
            minSequence.HasValue && (agentId is null || minSequence > maxSequence))
            throw new ArgumentOutOfRangeException(nameof(minSequence));
        using var snapshot = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly;Pooling=False");
        await snapshot.OpenAsync().ConfigureAwait(false);
        using (var ready = snapshot.CreateCommand())
        {
            ready.CommandText = "SELECT ready FROM projection_state WHERE id=1;";
            if (Convert.ToInt64(await ready.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("The indexed generation is incomplete.");
        }
        using (var integrity = snapshot.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(await integrity.ExecuteScalarAsync().ConfigureAwait(false) as string, "ok",
                StringComparison.Ordinal))
                throw new InvalidDataException("The index database is corrupt.");
            integrity.CommandText = "SELECT count(*) FROM pragma_foreign_key_check;";
            if (Convert.ToInt64(await integrity.ExecuteScalarAsync().ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("The index has orphaned references.");
        }
        using var command = snapshot.CreateCommand();
        command.CommandText = """
            SELECT fact_path, fact_sha256, 0, capture_id, agent_id, sequence, exposure_hour,
                   exposure_unix_ms, NULL, NULL FROM capture_lookup
            WHERE ($from IS NULL OR (exposure_unix_ms >= $from AND exposure_unix_ms < $to))
              AND ($min_sequence IS NULL OR (agent_id=$agent AND sequence BETWEEN $min_sequence AND $max_sequence))
            UNION ALL
            SELECT output.fact_path, output.fact_sha256, 1, output.output_identity, output.capture_id,
                   capture.sequence, capture.exposure_hour, capture.exposure_unix_ms,
                   output.role, output.variant
            FROM output_lookup output LEFT JOIN capture_lookup capture ON capture.capture_id=output.capture_id
            WHERE ($from IS NULL OR (capture.exposure_unix_ms >= $from AND capture.exposure_unix_ms < $to))
              AND ($min_sequence IS NULL OR (capture.agent_id=$agent AND capture.sequence BETWEEN $min_sequence AND $max_sequence));
            """;
        command.Parameters.AddWithValue("$from", fromUnixMs.HasValue ? fromUnixMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("$to", toUnixMs.HasValue ? toUnixMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("$min_sequence", minSequence.HasValue ? minSequence.Value : DBNull.Value);
        command.Parameters.AddWithValue("$max_sequence", maxSequence.HasValue ? maxSequence.Value : DBNull.Value);
        command.Parameters.AddWithValue("$agent", agentId ?? (object)DBNull.Value);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(5).ConfigureAwait(false) ||
                await reader.IsDBNullAsync(6).ConfigureAwait(false))
                throw new InvalidDataException("Output lookup has no indexed capture.");
            var relative = reader.GetString(0);
            if (!referenced.Add(relative))
                throw new InvalidDataException("Duplicate indexed fact path.");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            var expectedRoot = Path.GetFullPath(Path.Combine(root, reader.GetInt64(2) == 1 ? "products" : "hours"));
            if (!path.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(path))
                throw new InvalidDataException("Snapshot references a missing or escaped retained fact.");
            try { RawIngressFileStore.EnsureNoSymbolicLinks(root, path); }
            catch (IOException exception) { throw new InvalidDataException("Snapshot fact path is linked.", exception); }
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != reader.GetString(1))
                throw new InvalidDataException("Snapshot references a changed retained fact.");
            if (reader.GetInt64(2) == 1)
            {
                try
                {
                    using var product = JsonDocument.Parse(bytes);
                    if (product.RootElement.GetProperty("Identity").GetString() != reader.GetString(3))
                        throw new InvalidDataException("Product fact identity conflicts with index.");
                    var descriptor = product.RootElement.GetProperty("Descriptor").GetBytesFromBase64();
                    using var schemaDocument = JsonDocument.Parse(descriptor);
                    var schema = schemaDocument.RootElement.GetProperty("schemaVersion").GetString();
                    var productManifest = schema is DurableProcessingProductManifestV1.CurrentSchemaVersion or
                        DurableEncodedProductManifestV2.CurrentSchemaVersion or
                        DurableTypedMetadataProductManifestV3.CurrentSchemaVersion
                        ? DurableProcessingProductManifestJson.Parse(descriptor) : null;
                    var parsed = productManifest is null ? CaptureContractJson.ParseManifest(descriptor).Document?.Manifest : null;
                    var artifact = productManifest?.Artifact ?? parsed?.Descriptor.Artifact;
                    var capture = productManifest?.Capture ?? parsed?.Descriptor.Capture;
                    using var rawReference = snapshot.CreateCommand();
                    rawReference.CommandText = "SELECT fact_path FROM capture_lookup WHERE capture_id=$capture;";
                    rawReference.Parameters.AddWithValue("$capture", reader.GetString(4));
                    var rawPath = await rawReference.ExecuteScalarAsync().ConfigureAwait(false) as string;
                    if (rawPath is null)
                        throw new InvalidDataException("Product fact has no retained raw capture.");
                    using var rawFact = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, rawPath))
                        .ConfigureAwait(false));
                    var rawManifest = CaptureContractJson.ParseManifest(rawFact.RootElement.GetProperty("Manifest")
                        .GetBytesFromBase64()).Document?.Manifest;
                    if (artifact is null || capture is null ||
                        rawManifest is null || rawManifest.Descriptor.Capture.CaptureId != capture.CaptureId ||
                        rawManifest.Descriptor.Capture.AgentId != capture.AgentId ||
                        ProcessingIdentity.CreateOutputIdentity(artifact.Role, artifact.Variant,
                            ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256,
                            artifact.SourceArtifactIds) != reader.GetString(3) ||
                        artifact.Role.ToString() != reader.GetString(8) ||
                        artifact.Variant != reader.GetString(9) ||
                        capture.CaptureId.ToString("N") != reader.GetString(4) ||
                        capture.CaptureSequence != reader.GetInt64(5) ||
                        rawManifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() != reader.GetInt64(7) ||
                        parsed is not null && parsed.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() / 3_600_000 != reader.GetInt64(6))
                        throw new InvalidDataException("Product fact descriptor conflicts with index.");
                    var sources = product.RootElement.GetProperty("Sources").EnumerateArray()
                        .Select(static item => item.GetString()).ToArray();
                    if (!sources.SequenceEqual(artifact.SourceArtifactIds
                        .Select(static id => id.ToString("N")), StringComparer.Ordinal))
                        throw new InvalidDataException("Product fact sources conflict with descriptor.");
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
                {
                    throw new InvalidDataException("Product fact is malformed.", exception);
                }
            }
            else
            {
                try
                {
                    using var raw = JsonDocument.Parse(bytes);
                    var fact = raw.RootElement;
                    var manifestBytes = fact.GetProperty("Manifest").GetBytesFromBase64();
                    var parsed = CaptureContractJson.ParseManifest(manifestBytes).Document?.Manifest;
                    if (parsed is null || fact.GetProperty("CaptureId").GetString() != reader.GetString(3) ||
                        fact.GetProperty("AgentId").GetString() != reader.GetString(4) ||
                        fact.GetProperty("Sequence").GetInt64() != reader.GetInt64(5) ||
                        fact.GetProperty("Hour").GetInt64() != reader.GetInt64(6) ||
                        fact.GetProperty("ManifestSha256").GetString() != CaptureContractJson.ComputeManifestSha256(manifestBytes) ||
                        parsed.Descriptor.Capture.CaptureId.ToString("N") != reader.GetString(3) ||
                        parsed.Descriptor.Capture.AgentId != reader.GetString(4) ||
                        parsed.Descriptor.Capture.CaptureSequence != reader.GetInt64(5) ||
                        parsed.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() != reader.GetInt64(7) ||
                        parsed.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() / 3_600_000 != reader.GetInt64(6))
                        throw new InvalidDataException("Raw fact identity conflicts with index.");
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
                {
                    throw new InvalidDataException("Raw fact is malformed.", exception);
                }
            }
        }
        if (fromUnixMs is null && minSequence is null)
        {
            foreach (var directory in new[] { "hours", "products" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file);
                    if (!referenced.Contains(relative))
                        throw new InvalidDataException("Unindexed fact file in offline dataset.");
                }
            }
        }
    }

    private static string ExportLabel()
    {
        var label = Environment.GetEnvironmentVariable("HVO_ISSUE1057_EXPORT_LABEL") ?? "dense-hour";
        if (label.Length is < 1 or > 64 || label.Any(static character =>
            character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            throw new InvalidDataException("Export label must contain 1-64 ASCII letters, digits, hyphens or underscores.");
        return label;
    }
}
