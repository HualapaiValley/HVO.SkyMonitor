using System.Collections.Immutable;
using System.Globalization;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    internal SwpcStoreView ReadView(SqliteConnection db, SqliteTransaction tx, DateTimeOffset atUtc)
    {
        var products = ImmutableDictionary.CreateBuilder<SwpcProduct, SwpcStoredProduct>();
        using var command = Command(db, tx, "SELECT product,lkg,etag,last_modified,last_success,last_attempt,last_outcome,checkpoint,halt FROM swpc_products ORDER BY product;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var product = (SwpcProduct)reader.GetInt32(0);
            var success = reader.IsDBNull(4) ? (DateTimeOffset?)null : Time(reader.GetInt64(4));
            var threshold = _settings.Freshness[product];
            var age = success is { } stamp ? atUtc - stamp : TimeSpan.MaxValue;
            var freshness = success is null ? SwpcFreshness.None : age < threshold.BaseInterval * 3 ? SwpcFreshness.Fresh : age < threshold.OutdatedAfter ? SwpcFreshness.Stale : SwpcFreshness.Outdated;
            SwpcSnapshot? snapshot = null;
            if (!reader.IsDBNull(1))
            {
                using var stored = Command(db, tx, "SELECT s.envelope,r.body,r.sha FROM swpc_snapshots s JOIN swpc_raw r ON r.sha=s.raw_sha WHERE s.id=$id;", ("$id", reader.GetInt64(1)));
                using var body = stored.ExecuteReader();
                if (!body.Read()) throw new InvalidDataException("A last-known-good snapshot is missing.");
                var raw = (byte[])body[1];
                if (SwpcHash.Sha256UpperHex(raw) != body.GetString(2))
                    throw new InvalidDataException("A stored space-weather raw body failed its integrity check.");
                snapshot = Parse<SwpcSnapshot>(body.GetString(0)) with { DecodedBody = [.. raw] };
            }
            products.Add(product, new(snapshot, new(reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)),
                success, reader.IsDBNull(5) ? null : Time(reader.GetInt64(5)), reader.IsDBNull(6) ? null : (SwpcFetchOutcome)reader.GetInt32(6),
                Parse<SwpcFetchCheckpoint>(reader.GetString(7)), reader.IsDBNull(8) ? null : Parse<SwpcHalt>(reader.GetString(8)), freshness));
        }
        var halt = Scalar(db, tx, "SELECT provider_halt FROM swpc_meta WHERE id=1;") as string;
        return new(Revision(db, tx), products.ToImmutable(), halt is null ? null : Parse<SwpcHalt>(halt), ReadNotices(db, tx));
    }

    private static SwpcNoticeData ReadNotices(SqliteConnection db, SqliteTransaction tx)
    {
        var versions = ImmutableArray.CreateBuilder<SwpcStoredMessage>();
        using (var command = Command(db, tx, "SELECT record,original,sequence,snapshot_sha,retrieved FROM swpc_messages ORDER BY sequence;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var message = Parse<SwpcMessageRecord>(reader.GetString(0)) with { OriginalRecordJson = Utf8.GetString((byte[])reader[1]) };
                versions.Add(new(new(message, reader.GetInt64(2)), reader.GetString(3), Time(reader.GetInt64(4))));
            }
        var ranks = ImmutableDictionary.CreateBuilder<SwpcMessageNode, int>();
        using (var command = Command(db, tx, "SELECT code,serial,rank FROM swpc_ranks;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ranks.Add(new(reader.GetString(0), reader.GetInt32(1)), reader.GetInt32(2));
        var acks = ImmutableDictionary.CreateBuilder<SwpcMessageNode, SwpcAcknowledgement>();
        using (var command = Command(db, tx, "SELECT code,serial,actor,at_utc FROM swpc_acks;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) acks.Add(new(reader.GetString(0), reader.GetInt32(1)), new(reader.GetString(2), Time(reader.GetInt64(3))));
        var orphans = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        using (var command = Command(db, tx, "SELECT identity FROM swpc_orphans;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) orphans.Add(reader.GetString(0));
        return new(versions.ToImmutable(), SwpcGroupBuilder.Build(versions.Select(v => v.Version).ToArray()), orphans.ToImmutable(), ranks.ToImmutable(), acks.ToImmutable());
    }

    public ValueTask<SwpcStoreHealth> ReadHealthAsync(DateTimeOffset atUtc, CancellationToken ct) => ExecuteAsync(atUtc, (db, tx) =>
    {
        var view = ReadView(db, tx, atUtc);
        var products = view.Products.ToImmutableDictionary(p => p.Key, p => new SwpcProductHealth(p.Value.LastKnownGood?.Sha256, p.Value.LastKnownGood?.InFileStampUtc,
            p.Value.LastSuccessUtc, p.Value.LastAttemptUtc, p.Value.LastOutcome, p.Value.Freshness, p.Value.Checkpoint, p.Value.ProductHalt));
        var counts = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        using var command = Command(db, tx, """
            SELECT json_extract(completion,'$.ReasonCode'),count(*) FROM swpc_attempts
            WHERE completion IS NOT NULL GROUP BY json_extract(completion,'$.ReasonCode');
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read()) counts.Add(reader.GetString(0), reader.GetInt64(1));
        return new SwpcStoreHealth(SchemaVersion, view.Revision, products, view.ProviderHalt,
            Count(db, tx, "SELECT count(*) FROM swpc_snapshots;"), Count(db, tx, "SELECT coalesce(sum(length(body)),0) FROM swpc_raw;"),
            view.Notices.Versions.Length, Count(db, tx, "SELECT count(*) FROM swpc_attempts;"),
            checked((int)Count(db, tx, "SELECT count(*) FROM swpc_attempts WHERE completion IS NULL;")), counts.ToImmutable(),
            Scalar(db, tx, "SELECT min(started) FROM swpc_attempts;") is long oldest ? Time(oldest) : null);
    }, ct);

    public ValueTask<SwpcDiagnosticsPage> ReadDiagnosticsAsync(SwpcDiagnosticsQuery request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 200 || request.BeforeSequence is < 1) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Product is { } p && !Enum.IsDefined(p)) throw new ArgumentOutOfRangeException(nameof(request));
        return ExecuteAsync(request.AtUtc, (db, tx) =>
        {
            var entries = ImmutableArray.CreateBuilder<SwpcJournalEntry>();
            using var command = Command(db, tx, "SELECT sequence,id,product,start,completion,snapshot_sha,diagnostics FROM swpc_attempts WHERE ($p IS NULL OR product=$p) AND ($b IS NULL OR sequence<$b) ORDER BY sequence DESC LIMIT $limit;",
                ("$p", request.Product is { } product ? (int)product : null), ("$b", request.BeforeSequence), ("$limit", request.Limit + 1));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var start = Parse<SwpcAttemptStart>(reader.GetString(3));
                var end = reader.IsDBNull(4) ? null : Parse<SwpcAttemptCompletion>(reader.GetString(4));
                var diagnostics = reader.GetString(6);
                entries.Add(new(reader.GetInt64(0), start.AttemptId, (SwpcProduct)reader.GetInt32(2), start.Trigger, start.StartedUtc, end?.EndedUtc,
                    end?.Outcome, end?.HttpStatus, end?.ReasonCode, end?.BytesRead, end?.ValidatorOutcome, reader.IsDBNull(5) ? null : reader.GetString(5),
                    diagnostics is null ? [] : Parse<ImmutableArray<string>>(diagnostics)));
            }
            var more = entries.Count > request.Limit;
            if (more) entries.RemoveAt(entries.Count - 1);
            return new SwpcDiagnosticsPage(entries.ToImmutable(), more ? entries[^1].Sequence : null);
        }, ct);
    }

    private static void WriteCheckpoint(SqliteConnection db, SqliteTransaction tx, SwpcProduct product, SwpcFetchCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!Enum.IsDefined(product) || checkpoint.ConsecutiveTransientFailures < 0 || checkpoint.ConsecutiveRateLimited < 0 || checkpoint.ConsecutiveRejected < 0)
            throw new ArgumentException("Invalid durable fetch checkpoint.");
        foreach (var time in new[] { checkpoint.NextAttemptNotBeforeUtc, checkpoint.RetryNotBeforeUtc, checkpoint.RecordDriftStampUtc })
            if (time is { } t) RequireUtc(t);
        Exec(db, tx, "UPDATE swpc_products SET checkpoint=$c WHERE product=$p;", ("$c", Json(checkpoint)), ("$p", (int)product));
    }
}
