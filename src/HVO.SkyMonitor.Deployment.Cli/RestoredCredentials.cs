using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Revokes again, in a restored Identity database, the credentials the live instance revoked after the backup was taken
/// (#1353). A restore puts back the database as it was at the backup, and the store keeps no tombstones: a revoked API
/// key is a deleted row, and a changed or recovered owner password is a new hash and security stamp. So the live store
/// the restore replaces is the evidence. An API key the restored database holds and the live one does not is deleted, and
/// an account whose live security stamp differs from the restored one takes the live password hash and stamp, which also
/// signs out every session the old stamp issued. Where there is no live store, nothing can be compared, and the caller
/// warns loudly instead.
/// </summary>
internal static class RestoredCredentials
{
    /// <summary>The login provider under which CameraAgent keeps API keys (<c>CameraAgentApiKeyStore.LoginProvider</c>).</summary>
    public const string ApiKeyProvider = "HVO.SkyMonitor.CameraAgent.ApiKey";

    private static readonly string[] AccountColumns = ["PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PasswordChangeRequired"];

    /// <summary>What a re-revocation changed, and what it could not compare.</summary>
    internal sealed record Outcome(int ApiKeysRevoked, int AccountsReset, int AccountsNotLive);

    /// <summary>
    /// Revokes in <paramref name="restoredDatabase"/> what <paramref name="liveDatabase"/> shows was revoked after the
    /// backup, in one transaction. Running it again changes nothing more.
    /// </summary>
    public static Outcome Revoke(string restoredDatabase, string liveDatabase)
    {
        ArgumentNullException.ThrowIfNull(restoredDatabase);
        ArgumentNullException.ThrowIfNull(liveDatabase);
        HashSet<(string UserId, string Name)> liveKeys;
        Dictionary<string, Dictionary<string, object?>> liveAccounts;
        using (var live = CameraAgentStatePreflight.ReadOnlyDatabase.Open(liveDatabase))
        {
            liveKeys = ReadApiKeys(live.Connection, null);
            liveAccounts = ReadAccounts(live.Connection, null);
        }
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = restoredDatabase,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        restored.Open();
        using var transaction = restored.BeginTransaction();
        var revoked = 0;
        foreach (var (userId, name) in ReadApiKeys(restored, transaction).Except(liveKeys))
        {
            using var delete = restored.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM AspNetUserTokens WHERE UserId = $user AND LoginProvider = $provider AND Name = $name;";
            delete.Parameters.AddWithValue("$user", userId);
            delete.Parameters.AddWithValue("$provider", ApiKeyProvider);
            delete.Parameters.AddWithValue("$name", name);
            revoked += delete.ExecuteNonQuery();
        }
        var reset = 0;
        var notLive = 0;
        foreach (var (id, account) in ReadAccounts(restored, transaction))
        {
            if (!liveAccounts.TryGetValue(id, out var liveAccount))
            {
                notLive++;
                continue;
            }
            if (Equals(account.GetValueOrDefault("SecurityStamp"), liveAccount.GetValueOrDefault("SecurityStamp")) &&
                Equals(account.GetValueOrDefault("PasswordHash"), liveAccount.GetValueOrDefault("PasswordHash")))
            {
                continue;
            }
            using var update = restored.CreateCommand();
            update.Transaction = transaction;
            var columns = liveAccount.Keys.Where(account.ContainsKey).ToArray();
#pragma warning disable CA2100 // The column names come from the fixed AccountColumns list, never from input.
            update.CommandText = $"UPDATE AspNetUsers SET {string.Join(", ", columns.Select(static column => $"{column} = ${column}"))} WHERE Id = $id;";
#pragma warning restore CA2100
            foreach (var column in columns) update.Parameters.AddWithValue($"${column}", liveAccount[column] ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            reset += update.ExecuteNonQuery();
        }
        transaction.Commit();
        return new Outcome(revoked, reset, notLive);
    }

    private static HashSet<(string, string)> ReadApiKeys(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var keys = new HashSet<(string, string)>();
        if (!HasTable(connection, transaction, "AspNetUserTokens")) return keys;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT UserId, Name FROM AspNetUserTokens WHERE LoginProvider = $provider;";
        command.Parameters.AddWithValue("$provider", ApiKeyProvider);
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add((reader.GetString(0), reader.GetString(1)));
        return keys;
    }

    /// <summary>Each account's credential columns, as far as this schema has them, by account ID.</summary>
    private static Dictionary<string, Dictionary<string, object?>> ReadAccounts(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var accounts = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (!HasTable(connection, transaction, "AspNetUsers")) return accounts;
        var present = Columns(connection, transaction, "AspNetUsers");
        var columns = AccountColumns.Where(present.Contains).ToArray();
        if (columns.Length == 0) return accounts;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // The column names come from the fixed AccountColumns list, never from input.
        command.CommandText = $"SELECT Id, {string.Join(", ", columns)} FROM AspNetUsers;";
#pragma warning restore CA2100
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < columns.Length; index++)
            {
                values[columns[index]] = reader.IsDBNull(index + 1) ? null : reader.GetValue(index + 1);
            }
            accounts[reader.GetString(0)] = values;
        }
        return accounts;
    }

    private static bool HasTable(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static HashSet<string> Columns(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // The table name is one of this class's constants, never input.
        command.CommandText = $"PRAGMA table_info({table});";
#pragma warning restore CA2100
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }
}
