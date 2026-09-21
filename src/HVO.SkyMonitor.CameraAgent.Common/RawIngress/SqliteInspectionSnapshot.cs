using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

/// <summary>
/// Produces the coherent read-only snapshot every raw-ingress schema inspector reads before a database is
/// opened for normal use. The snapshot is taken with the SQLite online backup API from a single read-only
/// source connection, so a concurrent checkpoint can never remove the write-ahead log between an existence
/// check and a file copy, and no recovery side file is copied or later interpreted.
/// </summary>
internal static class SqliteInspectionSnapshot
{
    private const int SqliteDbConfigNoCheckpointOnClose = 1006;

    private const int MaxAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Opens <paramref name="databasePath"/> read-only, backs it up into a private temporary database, and runs
    /// <paramref name="inspectAsync"/> against that backup. <paramref name="sourceOpenedSeam"/> runs once the
    /// source connection is open and before the backup step, once per attempt. The temporary directory is always
    /// removed.
    /// </summary>
    internal static async ValueTask<TResult> InspectAsync<TResult>(
        string databasePath,
        int busyTimeoutSeconds,
        string temporaryDirectoryPrefix,
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
                    temporaryDirectoryPrefix,
                    ensureDatabaseFilesArePhysical,
                    inspectAsync,
                    sourceOpenedSeam,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (attempt < MaxAttempts && IsContention(exception))
            {
                // Only transient lock contention is retried; corruption and schema failures propagate to the
                // inspector that owns their diagnostics. sqlite3_backup_step can return BUSY without ever invoking
                // the busy handler, so the connection-local busy timeout alone does not space these attempts out.
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
        string temporaryDirectoryPrefix,
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
        // Coherence does not come from holding this connection open: it comes from the read transaction that the
        // backup step below opens, which yields one committed image of the database however the write-ahead log has
        // moved in the meantime. This is why no side file is copied and why nothing here needs an explicit
        // BEGIN DEFERRED. Tests bind this seam to close the last writer exactly here, which is the race that used to
        // delete the write-ahead log between the enumeration and the copy.
        sourceOpenedSeam?.Invoke();
        DirectoryInfo? snapshotRoot = null;
        try
        {
            snapshotRoot = Directory.CreateTempSubdirectory(temporaryDirectoryPrefix);
            var snapshotPath = Path.Combine(snapshotRoot.FullName, Path.GetFileName(databasePath));
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = busyTimeoutSeconds
            }.ToString());
            await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
                snapshot,
                async (connection, token) =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"PRAGMA busy_timeout = {busyTimeoutSeconds * 1000};";
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(snapshot);
            return await inspectAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteSnapshotRoot(snapshotRoot);
        }
    }

    private static void DeleteSnapshotRoot(DirectoryInfo? snapshotRoot)
    {
        if (snapshotRoot is null)
        {
            return;
        }
        try
        {
            snapshotRoot.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The snapshot directory is private temporary state. Failing to remove it must never replace a
            // corruption or schema diagnostic that is already propagating out of the inspection.
        }
    }

    private static bool IsContention(SqliteException exception)
        => exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_BUSY ||
            exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_LOCKED;
}
