using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Testing;

// Compile-linked into the HTTP and browser harnesses. Both artifacts are real
// published captures; only the auxiliary binding and corruption are fixture setup.
internal sealed class AuxiliaryRunManifestEvidenceFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly string consumerIdentity;
    private readonly byte[] consumerDescriptor;
    private readonly byte[] referenceDescriptor;

    private AuxiliaryRunManifestEvidenceFixture(SqliteConnection connection, string consumerIdentity,
        byte[] consumerDescriptor, Guid referenceArtifactId, string referenceIdentity, byte[] referenceDescriptor)
    {
        this.connection = connection;
        this.consumerIdentity = consumerIdentity;
        this.consumerDescriptor = consumerDescriptor;
        this.referenceDescriptor = referenceDescriptor;
        ReferenceArtifactId = referenceArtifactId;
        ReferenceIdentity = referenceIdentity;
    }

    internal Guid ReferenceArtifactId { get; }
    internal string ReferenceIdentity { get; }

    internal static async Task<AuxiliaryRunManifestEvidenceFixture> CreateAsync(string root, Guid consumerArtifactId)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT output_identity_sha256, capture_id, descriptor_json FROM processing_outputs WHERE artifact_id=$artifact;";
            read.Parameters.AddWithValue("$artifact", consumerArtifactId.ToString("N"));
            string identity;
            string capture;
            byte[] descriptor;
            using (var reader = await read.ExecuteReaderAsync().ConfigureAwait(false))
            {
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                identity = reader.GetString(0);
                capture = reader.GetString(1);
                descriptor = (byte[])reader[2];
            }
            read.CommandText = "SELECT artifact_id, output_identity_sha256, descriptor_json FROM processing_outputs WHERE capture_id<>$capture ORDER BY capture_id, artifact_id LIMIT 1;";
            read.Parameters.AddWithValue("$capture", capture);
            Guid reference;
            string referenceIdentity;
            byte[] referenceDescriptor;
            using (var reader = await read.ExecuteReaderAsync().ConfigureAwait(false))
            {
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false), "The harness must publish two different captures before binding an auxiliary source.");
                reference = Guid.ParseExact(reader.GetString(0), "N");
                referenceIdentity = reader.GetString(1);
                referenceDescriptor = (byte[])reader[2];
            }
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            using var bind = connection.CreateCommand();
            bind.Transaction = transaction;
            bind.CommandText = """
                INSERT INTO processing_output_sources(output_identity_sha256, source_ordinal, source_artifact_id)
                SELECT $identity, COALESCE(MAX(source_ordinal) + 1, 0), $source
                FROM processing_output_sources WHERE output_identity_sha256=$identity;
                UPDATE processing_outputs
                SET descriptor_json=CAST(json_insert(CAST(descriptor_json AS TEXT),
                    CASE WHEN json_type(CAST(descriptor_json AS TEXT), '$.descriptor.artifact')='object'
                    THEN '$.descriptor.artifact.sourceArtifactIds[#]' ELSE '$.artifact.sourceArtifactIds[#]' END, $sourceJson) AS BLOB)
                WHERE output_identity_sha256=$identity;
                """;
            bind.Parameters.AddWithValue("$identity", identity);
            bind.Parameters.AddWithValue("$source", reference.ToString("N"));
            bind.Parameters.AddWithValue("$sourceJson", reference.ToString("D"));
            Assert.AreEqual(2, await bind.ExecuteNonQueryAsync().ConfigureAwait(false));
            await transaction.CommitAsync().ConfigureAwait(false);
            return new(connection, identity, descriptor, reference, referenceIdentity, referenceDescriptor);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task CorruptAsync()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_outputs SET descriptor_json=CAST('{not-json' AS BLOB) WHERE output_identity_sha256=$identity;";
        command.Parameters.AddWithValue("$identity", ReferenceIdentity);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    internal async Task RestoreReferenceAsync()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_outputs SET descriptor_json=$json WHERE output_identity_sha256=$identity;";
        command.Parameters.AddWithValue("$identity", ReferenceIdentity);
        command.Parameters.AddWithValue("$json", referenceDescriptor);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RestoreReferenceAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM processing_output_sources WHERE output_identity_sha256=$identity AND source_artifact_id=$source;
                UPDATE processing_outputs SET descriptor_json=$json WHERE output_identity_sha256=$identity;
                """;
            command.Parameters.AddWithValue("$identity", consumerIdentity);
            command.Parameters.AddWithValue("$source", ReferenceArtifactId.ToString("N"));
            command.Parameters.AddWithValue("$json", consumerDescriptor);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { await connection.DisposeAsync().ConfigureAwait(false); }
    }
}
