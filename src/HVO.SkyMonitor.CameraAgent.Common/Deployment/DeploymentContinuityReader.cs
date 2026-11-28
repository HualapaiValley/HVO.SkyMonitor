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

public sealed record DurableDeploymentContinuity(
    bool RawIngressDatabaseExists,
    IReadOnlyList<CaptureSequenceContinuity> CaptureSequences,
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

        var captureSequences = await ReadCaptureSequencesAsync(rawPath, cancellationToken).ConfigureAwait(false);
        var captureWindow = await ReadCaptureWindowAsync(
            rawPath, fromCaptureSequence, toCaptureSequence, cancellationToken).ConfigureAwait(false);
        return new DurableDeploymentContinuity(
            File.Exists(rawPath),
            captureSequences,
            captureWindow);
    }

    /// <summary>
    /// The durable per-agent capture sequences only. This is the narrow read a local automation
    /// capture-relative trigger needs; it deliberately avoids the capture window, artifact, and
    /// continuity reads the full report performs.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The reader is an injectable application service.")]
    public async Task<IReadOnlyList<CaptureSequenceContinuity>> ReadCaptureSequenceContinuityAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return await ReadCaptureSequencesAsync(
            Path.Combine(Path.GetFullPath(root), "journal", "raw-ingress.db"), cancellationToken)
            .ConfigureAwait(false);
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

    private static void EnsurePhysicalFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidDataException("Deployment continuity databases must not be symbolic links.");
        }
    }
}
