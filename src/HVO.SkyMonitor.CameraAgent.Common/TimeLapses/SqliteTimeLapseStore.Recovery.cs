using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

internal sealed partial class SqliteTimeLapseStore
{
    /// <summary>Called under the exclusive worker lease before taking new work. Silo owns recovery of the exact video versions.</summary>
    private async ValueTask RecoverPublicationsAsync(SqliteConnection connection, CancellationToken token)
    {
        var pending = new List<string>();
        using (var command = Command(connection, null, "SELECT product_id FROM publications LIMIT 65;"))
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) pending.Add(reader.GetString(0));
        if (pending.Count > 64) throw new InvalidDataException("Too many interrupted local video publications.");
        foreach (var id in pending)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid interrupted video identity.");
            // Product insertion and intent removal commit together; a committed row is never deleted by recovery.
            if (await ScalarAsync(connection, null, "SELECT product_id FROM products WHERE product_id=$id;", token, ("$id", id)).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A committed video still has a publication intent.");
            await ExecuteAsync(connection, null, "DELETE FROM publications WHERE product_id=$id;", token, ("$id", id)).ConfigureAwait(false);
        }
    }
}
