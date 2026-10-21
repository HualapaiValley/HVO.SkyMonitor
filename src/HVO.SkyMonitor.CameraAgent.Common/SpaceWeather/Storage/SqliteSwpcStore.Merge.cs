using System.Collections.Immutable;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    internal static (int Added, ImmutableHashSet<SwpcMessageNode> Touched, ImmutableArray<string> Diagnostics) SaveSnapshot(
        SqliteConnection db, SqliteTransaction tx, SwpcSnapshot candidate, SwpcFetchOutcome outcome, DateTimeOffset atUtc)
    {
        var diagnostics = new List<string>();
        var touched = ImmutableHashSet.CreateBuilder<SwpcMessageNode>();
        var added = new List<SwpcMessageRecord>();
        if (candidate.Parsed is SwpcAlertsBody alerts && outcome != SwpcFetchOutcome.Unchanged)
        {
            var priorOrphanNodes = ReadNotices(db, tx).Groups.Where(group => group.Orphan)
                .SelectMany(group => group.Nodes).ToHashSet();
            diagnostics.AddRange(SwpcAlertsParser.Diagnostics(alerts.Value));
            var seen = new HashSet<(string Identity, string Hash)>();
            foreach (var record in alerts.Value.Records)
            {
                if (!seen.Add((record.Identity, record.ContentSha256))) continue;
                if (Scalar(db, tx, "SELECT 1 FROM swpc_messages WHERE identity=$i AND hash=$h;", ("$i", record.Identity), ("$h", record.ContentSha256)) is not null) continue;
                var existing = Scalar(db, tx, "SELECT 1 FROM swpc_messages WHERE identity=$i LIMIT 1;", ("$i", record.Identity)) is not null;
                if (existing && outcome == SwpcFetchOutcome.StaleSnapshot) { diagnostics.Add("stale-revision-ignored:" + record.Identity); continue; }
                if (existing) diagnostics.Add("source-revision:" + record.Identity);
                Exec(db, tx, "UPDATE swpc_meta SET ingest_sequence=ingest_sequence+1 WHERE id=1;");
                var sequence = Scalar(db, tx, "SELECT ingest_sequence FROM swpc_meta WHERE id=1;");
                Exec(db, tx, "INSERT INTO swpc_messages(identity,hash,sequence,record,original,snapshot_sha,retrieved) VALUES($i,$h,$s,$r,$o,$p,$t);",
                    ("$i", record.Identity), ("$h", record.ContentSha256), ("$s", sequence), ("$r", Json(record with { OriginalRecordJson = "" })),
                    ("$o", Utf8.GetBytes(record.OriginalRecordJson)), ("$p", candidate.Sha256), ("$t", Ticks(candidate.RetrievedUtc)));
                added.Add(record); touched.Add(record.Node);
            }
            var notices = ReadNotices(db, tx);
            var present = notices.Versions.Select(v => v.Version.Message.Node).ToHashSet();
            foreach (var message in added)
                if (message.Header.Link is { } link && link.TargetSerial != message.Serial && !present.Contains(new(message.Code, link.TargetSerial)))
                    diagnostics.Add("orphan-link:" + message.Identity);
            // A union preserves component nodes even when its lowest serial changes.
            foreach (var group in notices.Groups)
                if (!group.Orphan && group.Nodes.Overlaps(priorOrphanNodes))
                    diagnostics.Add("orphan-resolved:" + group.Identity);
            SynchronizeOrphanMarkers(db, tx, notices.Groups, notices.OrphanGroups);
        }
        if (outcome == SwpcFetchOutcome.Updated)
        {
            var rawHash = SwpcHash.Sha256UpperHex(candidate.DecodedBody.AsSpan());
            Exec(db, tx, "INSERT OR IGNORE INTO swpc_raw(sha,body) VALUES($h,$b);", ("$h", rawHash), ("$b", candidate.DecodedBody.ToArray()));
            Exec(db, tx, "INSERT INTO swpc_snapshots(product,sha,retrieved,envelope,raw_sha) VALUES($p,$h,$t,$e,$r);",
                ("$p", (int)candidate.Product), ("$h", candidate.Sha256), ("$t", Ticks(candidate.RetrievedUtc)),
                ("$e", Json(candidate with { DecodedBody = [] })), ("$r", rawHash));
            Exec(db, tx, "UPDATE swpc_products SET lkg=last_insert_rowid() WHERE product=$p;", ("$p", (int)candidate.Product));
        }
        if (outcome is SwpcFetchOutcome.Updated or SwpcFetchOutcome.Unchanged)
            Exec(db, tx, "UPDATE swpc_products SET etag=$e,last_modified=$m,last_success=$t WHERE product=$p;",
                ("$e", candidate.ResponseValidators.ETag), ("$m", candidate.ResponseValidators.LastModified),
                ("$t", Ticks(candidate.RetrievedUtc)), ("$p", (int)candidate.Product));
        if (outcome == SwpcFetchOutcome.StaleSnapshot) diagnostics.Add("stale-snapshot");
        Exec(db, tx, "UPDATE swpc_products SET last_outcome=$o WHERE product=$p;", ("$o", (int)outcome), ("$p", (int)candidate.Product));
        PruneRaw(db, tx, atUtc);
        return (added.Count, touched.ToImmutable(), [.. diagnostics.Order(StringComparer.Ordinal)]);
    }
    private static int SynchronizeOrphanMarkers(SqliteConnection db, SqliteTransaction tx,
        IEnumerable<SwpcNoticeGroup> groups, ImmutableHashSet<string> knownMarkers)
    {
        var expected = groups.Where(group => group.Orphan).Select(group => group.Identity).ToHashSet(StringComparer.Ordinal);
        var changed = 0;
        foreach (var identity in knownMarkers.Except(expected))
            changed += Exec(db, tx, "DELETE FROM swpc_orphans WHERE identity=$id;", ("$id", identity));
        foreach (var identity in expected.Except(knownMarkers, StringComparer.Ordinal))
            changed += Exec(db, tx, "INSERT INTO swpc_orphans(identity) VALUES($id);", ("$id", identity));
        return changed;
    }

}
