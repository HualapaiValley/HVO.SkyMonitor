using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            var snapshot = reader.IsDBNull(1) ? null : ReadLastKnownGood(db, tx, product, reader.GetInt64(1));
            products.Add(product, new(snapshot, new(reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)),
                success, reader.IsDBNull(5) ? null : Time(reader.GetInt64(5)), reader.IsDBNull(6) ? null : (SwpcFetchOutcome)reader.GetInt32(6),
                Parse<SwpcFetchCheckpoint>(reader.GetString(7)), reader.IsDBNull(8) ? null : Parse<SwpcHalt>(reader.GetString(8)), freshness));
        }
        var halt = Scalar(db, tx, "SELECT provider_halt FROM swpc_meta WHERE id=1;") as string;
        return new(Revision(db, tx), products.ToImmutable(), halt is null ? null : Parse<SwpcHalt>(halt), ReadNotices(db, tx));
    }

    /// <summary>
    /// Every read re-hashes the stored body, so corruption still fails closed. The hash streams from
    /// SQLite, and the parsed snapshot and its decoded bytes are reused only when the row, the stored
    /// envelope bytes and the verified body hash all equal the last ones this store decoded. Both
    /// columns are read as blobs, so a reused snapshot costs no text decoding.
    /// </summary>
    private SwpcSnapshot ReadLastKnownGood(SqliteConnection db, SqliteTransaction tx, SwpcProduct product, long id)
    {
        using var stored = Command(db, tx, "SELECT r.sha,r.rowid FROM swpc_snapshots s JOIN swpc_raw r ON r.sha=s.raw_sha WHERE s.id=$id;", ("$id", id));
        using var row = stored.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("A last-known-good snapshot is missing.");
        var sha = row.GetString(0);
        using var envelope = new SqliteBlob(db, "swpc_snapshots", "envelope", id, readOnly: true);
        using var body = new SqliteBlob(db, "swpc_raw", "body", row.GetInt64(1), readOnly: true);
        if (_decoded.TryGetValue(product, out var known) && known.Id == id && known.RawSha == sha && Matches(envelope, known.Envelope))
        {
            if (Convert.ToHexString(SHA256.HashData(body)) != sha) throw CorruptRawBody();
            return known.Snapshot;
        }
        var text = new byte[checked((int)envelope.Length)];
        envelope.Position = 0;
        envelope.ReadExactly(text);
        var raw = new byte[checked((int)body.Length)];
        body.ReadExactly(raw);
        if (SwpcHash.Sha256UpperHex(raw) != sha) throw CorruptRawBody();
        var snapshot = Parse<SwpcSnapshot>(text) with { DecodedBody = ImmutableCollectionsMarshal.AsImmutableArray(raw) };
        _decoded[product] = new(id, text, sha, snapshot);
        return snapshot;
    }
    private static bool Matches(Stream stored, byte[] expected)
    {
        if (stored.Length != expected.Length) return false;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (var offset = 0; offset < expected.Length;)
            {
                var read = stored.Read(buffer.AsSpan(0, Math.Min(buffer.Length, expected.Length - offset)));
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read))) return false;
                offset += read;
            }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static InvalidDataException CorruptRawBody() => new("A stored space-weather raw body failed its integrity check.");
    private sealed record DecodedSnapshot(long Id, byte[] Envelope, string RawSha, SwpcSnapshot Snapshot);

    /// <summary>
    /// Message rows are inserted once and never updated, and each sequence is unique. A sequence is
    /// reused only after its insert rolled back, and any transaction that does not commit drops this
    /// cache. A row is decoded again unless its sequence, identity, hash, snapshot and retrieval time
    /// all equal the cached row's, and groups are rebuilt only when the ordered row set changes.
    /// Ranks, acknowledgements and orphan markers are read on every call.
    /// </summary>
    private SwpcNoticeData ReadNotices(SqliteConnection db, SqliteTransaction tx)
    {
        var keys = new List<MessageKey>(_notices?.Keys.Length ?? 0);
        var stored = new List<SwpcStoredMessage>(keys.Capacity);
        using (var command = Command(db, tx, "SELECT sequence,identity,hash,snapshot_sha,retrieved,record,original FROM swpc_messages ORDER BY sequence;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var key = new MessageKey(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
                if (!_messages.TryGetValue(key.Sequence, out var known) || known.Key != key)
                {
                    var message = Parse<SwpcMessageRecord>(reader.GetString(5)) with { OriginalRecordJson = Utf8.GetString((byte[])reader[6]) };
                    _messages[key.Sequence] = known = new(key, new(new(message, key.Sequence), key.SnapshotSha, Time(key.Retrieved)));
                }
                keys.Add(key); stored.Add(known.Stored);
            }
        if (_notices is not { } notices || !notices.Keys.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(keys)))
        {
            ImmutableArray<SwpcStoredMessage> versions = [.. stored];
            _notices = notices = new([.. keys], versions, SwpcGroupBuilder.Build(versions.Select(v => v.Version).ToArray()));
            if (_messages.Count > keys.Count)
            {
                var live = keys.Select(k => k.Sequence).ToHashSet();
                foreach (var sequence in _messages.Keys.Where(s => !live.Contains(s)).ToArray()) _messages.Remove(sequence);
            }
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
        return new(notices.Versions, notices.Groups, orphans.ToImmutable(), ranks.ToImmutable(), acks.ToImmutable());
    }
    private readonly record struct MessageKey(long Sequence, string Identity, string Hash, string SnapshotSha, long Retrieved);
    private sealed record DecodedMessage(MessageKey Key, SwpcStoredMessage Stored);
    private sealed record DecodedNotices(ImmutableArray<MessageKey> Keys, ImmutableArray<SwpcStoredMessage> Versions, ImmutableArray<SwpcNoticeGroup> Groups);

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
