using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Deployment;

public sealed record CaptureSequenceContinuity(string AgentId, long LastSequence);

public sealed record LocalCaptureContinuity(
    long CaptureSequence,
    Guid CaptureId,
    Guid RawArtifactId,
    string RawChecksumSha256,
    long RawByteLength,
    string State);

public sealed record LocalArtifactContinuity(
    Guid ArtifactId,
    string AgentId,
    string Role,
    long CaptureSequence,
    string ChecksumSha256,
    long ByteLength,
    string Status);

public sealed record DurableDeploymentContinuity(
    bool RawIngressDatabaseExists,
    IReadOnlyList<CaptureSequenceContinuity> CaptureSequences,
    bool ArtifactOutboxDatabaseExists,
    long ArtifactOutboxMaximumRecordId,
    long ArtifactOutboxMaximumAuditId,
    bool FleetDatabaseExists,
    Guid? FleetAgentInstanceId,
    long? FleetNextSequence,
    long? FleetMaximumSequence,
    IReadOnlyList<LocalArtifactContinuity> LatestArtifacts,
    IReadOnlyList<LocalCaptureContinuity> CaptureWindow);

public sealed class DeploymentContinuityReader
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The reader is an injectable application service.")]
    public async Task<DurableDeploymentContinuity> ReadAsync(
        string root,
        long? fromCaptureSequence = null,
        long? toCaptureSequence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        var rawPath = Path.Combine(root, "journal", "raw-ingress.db");
        var artifactPath = Path.Combine(root, "outbox", "artifact-outbox.db");
        var fleetPath = Path.Combine(root, ".fleet", "fleet-status.db");

        var captureSequences = await ReadCaptureSequencesAsync(rawPath, cancellationToken).ConfigureAwait(false);
        var captureWindow = await ReadCaptureWindowAsync(
            rawPath, fromCaptureSequence, toCaptureSequence, cancellationToken).ConfigureAwait(false);
        var (maximumRecordId, maximumAuditId) = await ReadArtifactSequencesAsync(artifactPath, cancellationToken)
            .ConfigureAwait(false);
        var latestArtifacts = await ReadLatestArtifactsAsync(artifactPath, cancellationToken).ConfigureAwait(false);
        var fleet = await ReadFleetSequencesAsync(fleetPath, cancellationToken).ConfigureAwait(false);
        return new DurableDeploymentContinuity(
            File.Exists(rawPath),
            captureSequences,
            File.Exists(artifactPath),
            maximumRecordId,
            maximumAuditId,
            File.Exists(fleetPath),
            fleet.AgentInstanceId,
            fleet.NextSequence,
            fleet.MaximumSequence,
            latestArtifacts,
            captureWindow);
    }

    private static async Task<IReadOnlyList<LocalCaptureContinuity>> ReadCaptureWindowAsync(
        string path,
        long? fromCaptureSequence,
        long? toCaptureSequence,
        CancellationToken cancellationToken)
    {
        if (fromCaptureSequence is null && toCaptureSequence is null) return [];
        if (fromCaptureSequence is null or < 1 || toCaptureSequence is null || toCaptureSequence < fromCaptureSequence ||
            toCaptureSequence - fromCaptureSequence >= 256)
        {
            throw new ArgumentOutOfRangeException(nameof(fromCaptureSequence), "The capture continuity window is invalid.");
        }
        if (!File.Exists(path)) return [];
        EnsurePhysicalFile(path);
        using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "raw_captures", cancellationToken).ConfigureAwait(false)) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capture_sequence, capture_id, raw_artifact_id, payload_sha256, payload_length, state
            FROM raw_captures
            WHERE capture_sequence BETWEEN $from AND $to
            ORDER BY capture_sequence;
            """;
        command.Parameters.AddWithValue("$from", fromCaptureSequence.Value);
        command.Parameters.AddWithValue("$to", toCaptureSequence.Value);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LocalCaptureContinuity>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LocalCaptureContinuity(
                reader.GetInt64(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                reader.GetString(3).ToUpperInvariant(),
                reader.GetInt64(4),
                reader.GetString(5)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<CaptureSequenceContinuity>> ReadCaptureSequencesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        EnsurePhysicalFile(path);
        using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "raw_capture_sequences", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_id, last_sequence FROM raw_capture_sequences ORDER BY agent_id;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CaptureSequenceContinuity>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CaptureSequenceContinuity(reader.GetString(0), reader.GetInt64(1)));
        }
        return result;
    }

    private static async Task<(long MaximumRecordId, long MaximumAuditId)> ReadArtifactSequencesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (0, 0);
        }
        EnsurePhysicalFile(path);
        using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "artifact_outbox_records", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, "artifact_outbox_audit", cancellationToken).ConfigureAwait(false))
        {
            return (0, 0);
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(record_id), 0), (SELECT COALESCE(MAX(audit_id), 0) FROM artifact_outbox_audit) FROM artifact_outbox_records;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<LocalArtifactContinuity>> ReadLatestArtifactsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        EnsurePhysicalFile(path);
        using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        if (!await ArtifactTableHasColumnsAsync(connection,
                ["artifact_id", "role", "payload_sha256", "payload_length", "status", "manifest_bytes"], cancellationToken)
            .ConfigureAwait(false))
        {
            return [];
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, role, payload_sha256, payload_length, status, manifest_bytes
            FROM artifact_outbox_records
            WHERE artifact_id IS NOT NULL AND role IS NOT NULL AND payload_sha256 IS NOT NULL
              AND payload_length IS NOT NULL AND status = 'acknowledged'
            ORDER BY record_id DESC LIMIT 16;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LocalArtifactContinuity>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var parsed = CaptureContractJson.ParseManifest((byte[])reader[5]);
            var captureSequence = parsed.Document?.Manifest?.Descriptor.Capture.CaptureSequence;
            if (captureSequence is null or < 1) continue;
            result.Add(new LocalArtifactContinuity(
                Guid.ParseExact(reader.GetString(0), "N"),
                parsed.Document!.Manifest!.Descriptor.Capture.AgentId,
                reader.GetString(1),
                captureSequence.Value,
                reader.GetString(2).ToUpperInvariant(),
                reader.GetInt64(3),
                reader.GetString(4)));
        }
        return result;
    }

    private static async Task<(Guid? AgentInstanceId, long? NextSequence, long? MaximumSequence)> ReadFleetSequencesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (null, null, null);
        }
        EnsurePhysicalFile(path);
        using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "fleet_status_metadata", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, "fleet_status_records", cancellationToken).ConfigureAwait(false))
        {
            return (null, null, null);
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_instance_id, next_sequence, (SELECT MAX(sequence) FROM fleet_status_records) FROM fleet_status_metadata WHERE metadata_key = 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Fleet continuity metadata is missing.");
        }
        Guid? agentInstanceId = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
            ? null
            : Guid.Parse(reader.GetString(0));
        long? maximumSequence = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetInt64(2);
        return (agentInstanceId, reader.GetInt64(1), maximumSequence);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table);";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ArtifactTableHasColumnsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> requiredColumns,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "artifact_outbox_records", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(artifact_outbox_records);";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }
        return requiredColumns.All(columns.Contains);
    }

    private static void EnsurePhysicalFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidDataException("Deployment continuity databases must not be symbolic links.");
        }
    }
}
