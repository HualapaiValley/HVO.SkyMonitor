using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Sqlite;

internal static class SqliteConnectionConfigurationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async ValueTask OpenAndConfigureAsync(
        SqliteConnection connection,
        Func<SqliteConnection, CancellationToken, ValueTask> configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(configure);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await RunAsync(
                () => configure(connection, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryDisposeAfterFailureAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    internal static async ValueTask RunAsync(
        Func<ValueTask> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await callback().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Connection cleanup must not replace the configuration failure being propagated to diagnostics.")]
    internal static async ValueTask TryDisposeAfterFailureAsync(SqliteConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
