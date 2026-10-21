using System.Collections.Immutable;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    public ValueTask<SwpcPurgeResult> ApplyRetentionAsync(DateTimeOffset atUtc, CancellationToken ct)
    {
        RequireUtc(atUtc);
        return ExecuteAsync(null, (db, tx) => Purge(db, tx, atUtc), ct);
    }

    private static SwpcPurgeResult Purge(SqliteConnection db, SqliteTransaction tx, DateTimeOffset atUtc)
    {
        var notices = ReadNotices(db, tx);
        var expired = notices.Groups.Where(g => SwpcNoticeLifecycle.IsRetentionExpired(SwpcNoticeLifecycle.Evaluate(g, notices.Groups, atUtc), atUtc)).ToArray();
        var versions = 0;
        foreach (var group in expired)
        {
            foreach (var identity in group.Versions.Select(v => v.Message.Identity).Distinct(StringComparer.Ordinal))
                versions += Exec(db, tx, "DELETE FROM swpc_messages WHERE identity=$id;", ("$id", identity));
            foreach (var node in group.Nodes)
            {
                Exec(db, tx, "DELETE FROM swpc_ranks WHERE code=$c AND serial=$s;", ("$c", node.Code), ("$s", node.Serial));
                Exec(db, tx, "DELETE FROM swpc_acks WHERE code=$c AND serial=$s;", ("$c", node.Code), ("$s", node.Serial));
            }
        }
        var orphans = SynchronizeOrphanMarkers(db, tx, notices.Groups.Except(expired), notices.OrphanGroups);
        var audit = PruneAudit(db, tx, atUtc);
        var (snapshots, bytes) = PruneRaw(db, tx, atUtc);
        if (expired.Length > 0 || snapshots > 0 || audit > 0 || orphans > 0) Bump(db, tx);
        return new(Revision(db, tx), expired.Length, versions, snapshots, bytes);
    }

    private static int PruneAudit(SqliteConnection db, SqliteTransaction tx, DateTimeOffset atUtc)
    {
        var cutoff = atUtc.UtcTicks >= TimeSpan.FromDays(35).Ticks ? atUtc.UtcTicks - TimeSpan.FromDays(35).Ticks : long.MinValue;
        var deleted = Exec(db, tx, "DELETE FROM swpc_attempts WHERE completion IS NOT NULL AND started<=$cutoff;", ("$cutoff", cutoff));
        deleted += Exec(db, tx, """
            DELETE FROM swpc_attempts WHERE sequence IN (
                SELECT sequence FROM swpc_attempts WHERE completion IS NOT NULL
                ORDER BY started DESC,sequence DESC LIMIT -1 OFFSET 50000);
            """);
        deleted += Exec(db, tx, "DELETE FROM swpc_receipts WHERE created<=$cutoff;", ("$cutoff", cutoff));
        deleted += Exec(db, tx, """
            DELETE FROM swpc_receipts WHERE rowid IN (
                SELECT rowid FROM swpc_receipts ORDER BY created DESC,rowid DESC LIMIT -1 OFFSET 10000);
            """);
        return deleted;
    }

    internal static (int Snapshots, long Bytes) PruneRaw(SqliteConnection db, SqliteTransaction tx, DateTimeOffset atUtc, long cap = RawBodyLimit)
    {
        var before = Count(db, tx, "SELECT coalesce(sum(length(body)),0) FROM swpc_raw;");
        var cutoff = atUtc.UtcTicks >= TimeSpan.FromDays(35).Ticks ? atUtc.UtcTicks - TimeSpan.FromDays(35).Ticks : long.MinValue;
        var deleted = Exec(db, tx, "DELETE FROM swpc_snapshots WHERE id NOT IN (SELECT lkg FROM swpc_products WHERE lkg IS NOT NULL) AND (product=$alerts OR retrieved<=$cutoff);",
            ("$alerts", (int)SwpcProduct.Alerts), ("$cutoff", cutoff));
        Exec(db, tx, "DELETE FROM swpc_raw WHERE sha NOT IN (SELECT raw_sha FROM swpc_snapshots);");
        var victims = new List<long>();
        using (var command = Command(db, tx, "SELECT id FROM swpc_snapshots WHERE id NOT IN (SELECT lkg FROM swpc_products WHERE lkg IS NOT NULL) ORDER BY retrieved,id;"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) victims.Add(reader.GetInt64(0));
        foreach (var id in victims)
        {
            if (Count(db, tx, "SELECT coalesce(sum(length(body)),0) FROM swpc_raw;") <= cap) break;
            deleted += Exec(db, tx, "DELETE FROM swpc_snapshots WHERE id=$id;", ("$id", id));
            Exec(db, tx, "DELETE FROM swpc_raw WHERE sha NOT IN (SELECT raw_sha FROM swpc_snapshots);");
        }
        return (deleted, before - Count(db, tx, "SELECT coalesce(sum(length(body)),0) FROM swpc_raw;"));
    }
}
