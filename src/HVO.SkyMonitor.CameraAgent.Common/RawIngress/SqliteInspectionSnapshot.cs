using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

/// <summary>
/// Provides a coherent read-only transaction for raw-ingress schema inspection. SQLite reads the main file
/// and committed WAL together without copying retained history into temporary storage.
/// </summary>
internal static class SqliteInspectionSnapshot
{
    private const int SqliteDbConfigNoCheckpointOnClose = 1006;

    private const int MaxAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Opens the database read-only and keeps all inspector queries in one deferred read transaction.
    /// The source-open seam precedes the first read, including when the last writer checkpoints on close.
    /// </summary>
    internal static async ValueTask<TResult> InspectAsync<TResult>(
        string databasePath,
        int busyTimeoutSeconds,
        Action ensureDatabaseFilesArePhysical,
        Func<SqliteConnection, CancellationToken, ValueTask<TResult>> inspectAsync,
        Action? sourceOpenedSeam,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureDatabaseFilesArePhysical);
        ArgumentNullException.ThrowIfNull(inspectAsync);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await InspectOnceAsync(
                    databasePath,
                    busyTimeoutSeconds,
                    ensureDatabaseFilesArePhysical,
                    inspectAsync,
                    sourceOpenedSeam,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (attempt < MaxAttempts && IsContention(exception))
            {
                // Only transient lock contention is retried; corruption and schema failures propagate to the
                // inspector that owns their diagnostics.
                await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The busy timeout is a validated integer option; no SQL value is user supplied.")]
    private static async ValueTask<TResult> InspectOnceAsync<TResult>(
        string databasePath,
        int busyTimeoutSeconds,
        Action ensureDatabaseFilesArePhysical,
        Func<SqliteConnection, CancellationToken, ValueTask<TResult>> inspectAsync,
        Action? sourceOpenedSeam,
        CancellationToken cancellationToken)
    {
        ensureDatabaseFilesArePhysical();
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = busyTimeoutSeconds
        }.ToString());
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
            source,
            async (connection, token) =>
            {
                ensureDatabaseFilesArePhysical();
                var configurationResult = SQLitePCL.raw.sqlite3_db_config(
                    connection.Handle,
                    SqliteDbConfigNoCheckpointOnClose,
                    1,
                    out var checkpointDisabled);
                if (configurationResult != SQLitePCL.raw.SQLITE_OK || checkpointDisabled != 1)
                {
                    throw new InvalidOperationException(
                        $"Raw ingress SQLite inspection could not disable checkpoint-on-close for '{databasePath}'.");
                }
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA busy_timeout = {busyTimeoutSeconds * 1000};";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        sourceOpenedSeam?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(
            static handle => SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)handle!), source.Handle);
        try
        {
            // Use SQL transaction control so existing inspectors can issue commands without passing a managed
            // SqliteTransaction. The first read pins one committed image for every subsequent schema query.
            using var begin = source.CreateCommand();
            begin.CommandText = "PRAGMA query_only = ON; PRAGMA cache_size = -2048; PRAGMA mmap_size = 0; BEGIN DEFERRED;";
            await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var result = await inspectAsync(source, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ensureDatabaseFilesArePhysical();
            return result;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        // Disposing the non-pooled read-only connection rolls back and releases the read lock on every exit.
    }

    private static bool IsContention(SqliteException exception)
        => exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_BUSY ||
            exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_LOCKED;
}
