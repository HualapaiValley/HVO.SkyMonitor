using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Revokes again, in a restored Identity database, the credentials and authority the live instance revoked after the
/// backup was taken (#1353). A restore puts back the database as it was at the backup, and the store keeps no tombstones:
/// a revoked API key is a deleted row, a changed or recovered password is a new hash and security stamp, and a demotion is
/// a cleared flag or a deleted role or claim row. So the live store the restore replaces is the evidence, and what it no
/// longer grants is not granted again:
/// <list type="bullet">
/// <item>an API key the restored database holds and the live one does not is deleted;</item>
/// <item>an account whose live password hash or security stamp differs takes the live ones, which also signs out every
/// session the old stamp issued;</item>
/// <item>an account the live store no longer marks as the site owner is not the site owner;</item>
/// <item>a role or claim the live store no longer gives an account is removed;</item>
/// <item>an account the live store no longer has keeps no password, role, claim or site ownership, and is locked out.</item>
/// </list>
/// Each change is reported. Where there is no live store, nothing can be compared, and the caller warns loudly instead.
/// </summary>
internal static class RestoredCredentials
{
    /// <summary>The login provider under which CameraAgent keeps API keys (<c>CameraAgentApiKeyStore.LoginProvider</c>).</summary>
    public const string ApiKeyProvider = "HVO.SkyMonitor.CameraAgent.ApiKey";

    private static readonly string[] CredentialColumns = ["PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PasswordChangeRequired"];

    /// <summary>What a re-revocation changed, each change described for the operator.</summary>
    internal sealed record Outcome(int ApiKeysRevoked, int AccountsReset, int AuthorityRevoked, IReadOnlyList<string> Changes);

    /// <summary>
    /// Revokes in <paramref name="restoredDatabase"/> what <paramref name="liveDatabase"/> shows was revoked after the
    /// backup, in one transaction. Running it again changes nothing more.
    /// </summary>
    public static Outcome Revoke(string restoredDatabase, string liveDatabase)
    {
        ArgumentNullException.ThrowIfNull(restoredDatabase);
        ArgumentNullException.ThrowIfNull(liveDatabase);
        Snapshot live;
        using (var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(liveDatabase))
        {
            live = Read(database.Connection, null);
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
        var before = Read(restored, transaction);
        var changes = new List<string>();
        var keys = 0;
        var reset = 0;
        var authority = 0;
        foreach (var (userId, name) in before.ApiKeys.Except(live.ApiKeys).Order())
        {
            keys += Execute(restored, transaction,
                "DELETE FROM AspNetUserTokens WHERE UserId = $user AND LoginProvider = $provider AND Name = $name;",
                ("$user", userId), ("$provider", ApiKeyProvider), ("$name", name));
            changes.Add($"API key {CameraAgentStateDiagnostics.Field(name)} of account {CameraAgentStateDiagnostics.Field(userId)} revoked again");
        }
        foreach (var (id, account) in before.Accounts.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (!live.Accounts.TryGetValue(id, out var liveAccount))
            {
                var revoked = RevokeAccount(restored, transaction, before, id, account);
                if (revoked > 0)
                {
                    authority += revoked;
                    changes.Add($"account {CameraAgentStateDiagnostics.Field(id)} no longer exists in the live store: its password, roles, claims and site ownership are revoked, and it is locked out");
                }
                continue;
            }
            var credentials = CredentialColumns.Where(column => account.ContainsKey(column) && liveAccount.ContainsKey(column)).ToArray();
            if (credentials.Any(column => !Equals(account[column], liveAccount[column])) &&
                (!Equals(account.GetValueOrDefault("SecurityStamp"), liveAccount.GetValueOrDefault("SecurityStamp")) ||
                 !Equals(account.GetValueOrDefault("PasswordHash"), liveAccount.GetValueOrDefault("PasswordHash"))))
            {
                reset += Update(restored, transaction, id, credentials.Select(column => (column, liveAccount[column])).ToArray());
                changes.Add($"account {CameraAgentStateDiagnostics.Field(id)} keeps the password and security stamp the live instance had");
            }
            if (account.GetValueOrDefault("IsSiteOwner") is long owner && owner != 0 && liveAccount.GetValueOrDefault("IsSiteOwner") is long liveOwner && liveOwner == 0)
            {
                authority += Update(restored, transaction, id, [("IsSiteOwner", 0L)]);
                changes.Add($"account {CameraAgentStateDiagnostics.Field(id)} is no longer the site owner, as the live instance had demoted it");
            }
        }
        foreach (var (userId, roleId) in before.Roles.Except(live.Roles).Where(role => live.Accounts.ContainsKey(role.UserId)).Order())
        {
            authority += Execute(restored, transaction, "DELETE FROM AspNetUserRoles WHERE UserId = $user AND RoleId = $role;",
                ("$user", userId), ("$role", roleId));
            changes.Add($"role {CameraAgentStateDiagnostics.Field(roleId)} of account {CameraAgentStateDiagnostics.Field(userId)} removed again");
        }
        foreach (var (userId, type, value) in before.Claims.Except(live.Claims).Where(claim => live.Accounts.ContainsKey(claim.UserId)).Order())
        {
            authority += Execute(restored, transaction,
                "DELETE FROM AspNetUserClaims WHERE UserId = $user AND ClaimType IS $type AND ClaimValue IS $value;",
                ("$user", userId), ("$type", type), ("$value", value));
            changes.Add($"claim {CameraAgentStateDiagnostics.Assignment(type, value)} of account {CameraAgentStateDiagnostics.Field(userId)} removed again");
        }
        transaction.Commit();
        return new Outcome(keys, reset, authority, changes);
    }

    /// <summary>The columns that make an account's identity and lockout, which a colliding account the live store has takes from it.</summary>
    private static readonly string[] IdentityColumns = ["UserName", "NormalizedUserName", "Email", "NormalizedEmail", "LockoutEnabled", "LockoutEnd"];

    /// <summary>The columns of CameraAgent's <c>AspNetUsers</c> table, the only ones an account is copied by.</summary>
    private static readonly string[] AccountColumns =
    [
        "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PasswordHash", "SecurityStamp",
        "ConcurrencyStamp", "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled",
        "AccessFailedCount", "IsSiteOwner", "PasswordChangeRequired"
    ];

    /// <summary>
    /// Puts the live owner's account in <paramref name="restoredDatabase"/> when the restore keeps a live owner email,
    /// <paramref name="ownerEmail"/>, that names an account the backup does not hold (#1353 PR-1369-R2 F14): the live owner
    /// was installed after the backup, under another email or under the same one with a new Identity account
    /// (PR-1369-R3 F25). The installer withdraws the temporary owner password once the owner is seeded, so CameraAgent's
    /// startup seeder can then only find the owner, by its email, never create it. One mechanism serves every case: each
    /// restored account that holds the live owner's normalized email or sign-in name gives them up, before the live
    /// account is carried in as the live store has it, password, security stamp, site ownership and lockout included.
    /// <list type="bullet">
    /// <item>An account the live store no longer has takes a sign-in name no other account has, chosen in the same
    /// transaction (PR-1369-R3 F21), whatever it held (PR-1369-R4 F27), loses a colliding email, and is locked out as a
    /// removed account is.</item>
    /// <item>An account the live store still has takes the sign-in name, email and lockout the live store gives it. One an
    /// earlier restore of the same archive gave way is in that state already, so restoring the archive again, or resuming,
    /// changes nothing more (PR-1369-R4 F26).</item>
    /// </list>
    /// Returns each change.
    /// </summary>
    public static IReadOnlyList<string> CarryLiveOwner(string restoredDatabase, string liveDatabase, string ownerEmail)
    {
        ArgumentNullException.ThrowIfNull(restoredDatabase);
        ArgumentNullException.ThrowIfNull(liveDatabase);
        ArgumentException.ThrowIfNullOrEmpty(ownerEmail);
        if (!File.Exists(restoredDatabase) || !File.Exists(liveDatabase)) return [];
        var email = ownerEmail.ToUpperInvariant();
        Dictionary<string, object?>? owner;
        Dictionary<string, Dictionary<string, object?>> liveAccounts;
        using (var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(liveDatabase))
        {
            owner = ReadAccount(database.Connection, null, email);
            liveAccounts = ReadIdentities(database.Connection);
        }
        if (owner is null) return [];
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = restoredDatabase,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        restored.Open();
        using var transaction = restored.BeginTransaction();
        if (!HasTable(restored, transaction, "AspNetUsers")) return [];
        var present = Columns(restored, transaction, "AspNetUsers");
        if (!present.IsSupersetOf(["NormalizedEmail", "NormalizedUserName", "UserName", "Email"])) return [];
        var id = (string)owner["Id"]!;
        using (var existing = Command(restored, transaction, "SELECT COUNT(*) FROM AspNetUsers WHERE Id = $id;", ("$id", id)))
        {
            // The live owner's own account is in the backup: re-revocation has given it its live credentials.
            if (Convert.ToInt64(existing.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return [];
        }
        var name = owner.GetValueOrDefault("NormalizedUserName") as string;
        var holders = new List<(string Id, bool Email, bool Name)>();
        using (var holding = Command(restored, transaction,
                   "SELECT Id, NormalizedEmail IS $email, NormalizedUserName IS $name FROM AspNetUsers WHERE NormalizedEmail = $email OR NormalizedUserName = $name ORDER BY Id;",
                   ("$email", email), ("$name", name)))
        using (var reader = holding.ExecuteReader())
        {
            while (reader.Read()) holders.Add((reader.GetString(0), reader.GetInt64(1) != 0, reader.GetInt64(2) != 0));
        }
        var changes = new List<string>();
        var before = Read(restored, transaction);
        foreach (var holder in holders)
        {
            var held = holder.Email && holder.Name ? "email and sign-in name" : holder.Email ? "email" : "sign-in name";
            var update = new List<(string Column, object? Value)>();
            if (liveAccounts.TryGetValue(holder.Id, out var live))
            {
                // The live store has it under another identity, as one an earlier restore gave way: it takes that again.
                update.AddRange(IdentityColumns.Where(column => live.ContainsKey(column) && present.Contains(column)).Select(column => (column, live[column])));
                if (live.GetValueOrDefault("NormalizedUserName") is string liveName && NameTaken(restored, transaction, liveName, holder.Id))
                {
                    var free = FreeUserName(restored, transaction, holder.Id);
                    update.RemoveAll(static entry => entry.Column is "UserName" or "NormalizedUserName");
                    update.AddRange([("UserName", free), ("NormalizedUserName", free.ToUpperInvariant())]);
                }
                if (present.Contains("ConcurrencyStamp")) update.Add(("ConcurrencyStamp", Guid.NewGuid().ToString("D")));
                Update(restored, transaction, holder.Id, [.. update]);
                var given = update.FirstOrDefault(static entry => entry.Column == "UserName").Value;
                changes.Add($"account {CameraAgentStateDiagnostics.Field(holder.Id)} held the live owner's {held} only in the backup; it takes the identity the live store gives it, '{CameraAgentStateDiagnostics.Field(Convert.ToString(given, CultureInfo.InvariantCulture))}'");
                continue;
            }
            var freed = FreeUserName(restored, transaction, holder.Id);
            update.AddRange([("UserName", freed), ("NormalizedUserName", freed.ToUpperInvariant())]);
            if (holder.Email) update.AddRange([("Email", null), ("NormalizedEmail", null)]);
            if (present.Contains("ConcurrencyStamp")) update.Add(("ConcurrencyStamp", Guid.NewGuid().ToString("D")));
            Update(restored, transaction, holder.Id, [.. update]);
            RevokeAccount(restored, transaction, before, holder.Id, before.Accounts[holder.Id]);
            changes.Add($"account {CameraAgentStateDiagnostics.Field(holder.Id)} held the live owner's {held}; it gave {(holder.Email && holder.Name ? "them" : "it")} up as '{CameraAgentStateDiagnostics.Field(freed)}' and is locked out");
        }
        var columns = AccountColumns.Where(column => owner.ContainsKey(column) && present.Contains(column)).ToArray();
        using (var insert = Command(restored, transaction,
                   $"INSERT INTO AspNetUsers ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(static (_, index) => string.Create(CultureInfo.InvariantCulture, $"$v{index}")))});"))
        {
            for (var index = 0; index < columns.Length; index++)
            {
                insert.Parameters.AddWithValue(string.Create(CultureInfo.InvariantCulture, $"$v{index}"), owner[columns[index]] ?? DBNull.Value);
            }
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        changes.Add($"account {CameraAgentStateDiagnostics.Field(id)}, the live owner, which the backup does not hold, was brought over from the live instance with its live credentials");
        return changes;
    }

    /// <summary>Whether an account other than <paramref name="id"/> has the sign-in name <paramref name="normalizedName"/>.</summary>
    private static bool NameTaken(SqliteConnection connection, SqliteTransaction transaction, string normalizedName, string id)
    {
        using var taken = Command(connection, transaction, "SELECT COUNT(*) FROM AspNetUsers WHERE NormalizedUserName = $name AND Id <> $id;",
            ("$name", normalizedName), ("$id", id));
        return Convert.ToInt64(taken.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Every account of the store, by its <see cref="IdentityColumns"/>.</summary>
    private static Dictionary<string, Dictionary<string, object?>> ReadIdentities(SqliteConnection connection)
    {
        var accounts = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (!HasTable(connection, null, "AspNetUsers")) return accounts;
        var columns = IdentityColumns.Where(Columns(connection, null, "AspNetUsers").Contains).ToArray();
        using var command = Command(connection, null, $"SELECT Id{string.Concat(columns.Select(static column => $", {column}"))} FROM AspNetUsers;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < columns.Length; index++) values[columns[index]] = reader.IsDBNull(index + 1) ? null : reader.GetValue(index + 1);
            accounts[reader.GetString(0)] = values;
        }
        return accounts;
    }

    /// <summary>
    /// The first of <c>removed-&lt;id&gt;</c>, <c>removed-&lt;id&gt;-2</c>, ... that no account in the store has, read in
    /// <paramref name="transaction"/>, so the account that gives up its name never collides (PR-1369-R3 F21).
    /// </summary>
    private static string FreeUserName(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? $"removed-{id}" : string.Create(CultureInfo.InvariantCulture, $"removed-{id}-{attempt}");
            using var taken = Command(connection, transaction, "SELECT COUNT(*) FROM AspNetUsers WHERE NormalizedUserName = $name;",
                ("$name", candidate.ToUpperInvariant()));
            if (Convert.ToInt64(taken.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return candidate;
        }
    }

    /// <summary>The account <paramref name="normalizedEmail"/> names, by <see cref="AccountColumns"/>, or <see langword="null"/>.</summary>
    private static Dictionary<string, object?>? ReadAccount(SqliteConnection connection, SqliteTransaction? transaction, string normalizedEmail)
    {
        if (!HasTable(connection, transaction, "AspNetUsers")) return null;
        var present = Columns(connection, transaction, "AspNetUsers");
        if (!present.Contains("NormalizedEmail")) return null;
        var columns = AccountColumns.Where(present.Contains).ToArray();
        using var command = Command(connection, transaction,
            $"SELECT {string.Join(", ", columns)} FROM AspNetUsers WHERE NormalizedEmail = $email;", ("$email", normalizedEmail));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var account = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < columns.Length; index++)
        {
            account[columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }
        return account;
    }

    /// <summary>
    /// Takes every credential and every grant from an account the live store no longer has, and locks it out. An account
    /// already revoked this way is left as it is, so running it again changes nothing.
    /// </summary>
    private static int RevokeAccount(
        SqliteConnection connection, SqliteTransaction transaction, Snapshot before, string id, Dictionary<string, object?> account)
    {
        const string Forever = "9999-12-31 23:59:59+00:00";
        var changed = 0;
        var columns = new List<(string Column, object? Value)>();
        if (account.TryGetValue("PasswordHash", out var hash) && hash is not null)
        {
            columns.Add(("PasswordHash", null));
            if (account.ContainsKey("SecurityStamp")) columns.Add(("SecurityStamp", Guid.NewGuid().ToString("N").ToUpperInvariant()));
        }
        if (account.GetValueOrDefault("IsSiteOwner") is long owner && owner != 0) columns.Add(("IsSiteOwner", 0L));
        if (account.TryGetValue("LockoutEnabled", out var lockout) && lockout is not 1L) columns.Add(("LockoutEnabled", 1L));
        if (account.TryGetValue("LockoutEnd", out var end) && !Equals(end, Forever)) columns.Add(("LockoutEnd", Forever));
        if (columns.Count > 0) changed += Update(connection, transaction, id, [.. columns]);
        if (before.Roles.Any(role => role.UserId == id))
            changed += Execute(connection, transaction, "DELETE FROM AspNetUserRoles WHERE UserId = $user;", ("$user", id));
        if (before.Claims.Any(claim => claim.UserId == id))
            changed += Execute(connection, transaction, "DELETE FROM AspNetUserClaims WHERE UserId = $user;", ("$user", id));
        return changed;
    }

    private sealed record Snapshot(
        HashSet<(string UserId, string Name)> ApiKeys,
        Dictionary<string, Dictionary<string, object?>> Accounts,
        HashSet<(string UserId, string RoleId)> Roles,
        HashSet<(string UserId, string? Type, string? Value)> Claims);

    private static Snapshot Read(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var keys = new HashSet<(string, string)>();
        if (HasTable(connection, transaction, "AspNetUserTokens"))
        {
            using var command = Command(connection, transaction, "SELECT UserId, Name FROM AspNetUserTokens WHERE LoginProvider = $provider;", ("$provider", ApiKeyProvider));
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add((reader.GetString(0), reader.GetString(1)));
        }
        var accounts = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (HasTable(connection, transaction, "AspNetUsers"))
        {
            var present = Columns(connection, transaction, "AspNetUsers");
            var columns = CredentialColumns.Concat(["IsSiteOwner", "LockoutEnabled", "LockoutEnd"]).Where(present.Contains).ToArray();
            using var command = Command(connection, transaction, $"SELECT Id{string.Concat(columns.Select(static column => $", {column}"))} FROM AspNetUsers;");
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
        }
        var roles = new HashSet<(string, string)>();
        if (HasTable(connection, transaction, "AspNetUserRoles"))
        {
            using var command = Command(connection, transaction, "SELECT UserId, RoleId FROM AspNetUserRoles;");
            using var reader = command.ExecuteReader();
            while (reader.Read()) roles.Add((reader.GetString(0), reader.GetString(1)));
        }
        var claims = new HashSet<(string, string?, string?)>();
        if (HasTable(connection, transaction, "AspNetUserClaims"))
        {
            using var command = Command(connection, transaction, "SELECT UserId, ClaimType, ClaimValue FROM AspNetUserClaims;");
            using var reader = command.ExecuteReader();
            while (reader.Read()) claims.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return new Snapshot(keys, accounts, roles, claims);
    }

    private static int Update(SqliteConnection connection, SqliteTransaction transaction, string id, (string Column, object? Value)[] columns)
    {
        if (columns.Length == 0) return 0;
        var assignments = string.Join(", ", columns.Select(static (column, index) => string.Create(CultureInfo.InvariantCulture, $"{column.Column} = $v{index}")));
        using var command = Command(connection, transaction, $"UPDATE AspNetUsers SET {assignments} WHERE Id = $id;", ("$id", id));
        for (var index = 0; index < columns.Length; index++)
        {
            command.Parameters.AddWithValue(string.Create(CultureInfo.InvariantCulture, $"$v{index}"), columns[index].Value ?? DBNull.Value);
        }
        return command.ExecuteNonQuery();
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>A command whose text comes from this class's constants and the fixed column lists above, never from input.</summary>
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Statements and identifiers come from this class's constants, never from input.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static bool HasTable(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = Command(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;", ("$name", table));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static HashSet<string> Columns(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var command = Command(connection, transaction, $"PRAGMA table_info({table});");
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }
}
