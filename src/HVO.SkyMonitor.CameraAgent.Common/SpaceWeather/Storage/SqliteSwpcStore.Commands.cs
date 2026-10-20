using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    public ValueTask<SwpcWriteResult> ChangeHaltAsync(SwpcHaltCommand request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Change);
        ArgumentNullException.ThrowIfNull(request.Checkpoints);
        ValidateHalt(request.Change);
        return WriteAsync("halt", request.CommandId, request.ExpectedRevision, request.AtUtc,
            Fingerprint(request with { ExpectedRevision = 0 }), (db, tx) =>
            {
                WriteHalt(db, tx, request.Change);
                foreach (var (product, checkpoint) in request.Checkpoints) WriteCheckpoint(db, tx, product, checkpoint);
                return Result(db, tx, request.AtUtc);
            }, ct);
    }
    public ValueTask<SwpcWriteResult> AcknowledgeAsync(SwpcAcknowledgeCommand request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireUtc(request.Acknowledgement.AtUtc);
        RequireBounded(request.Acknowledgement.Operator, 128);
        RequireBounded(request.Group, 32);
        return WriteAsync("ack", request.CommandId, request.ExpectedRevision, request.AtUtc,
            Fingerprint(request with { ExpectedRevision = 0 }), (db, tx) =>
            {
                var group = ReadNotices(db, tx).Groups.FirstOrDefault(g => g.Identity == request.Group);
                if (group is null) return Result(db, tx, request.AtUtc, SwpcWriteStatus.NotFound, "space-weather.group-not-found");
                var node = group.CanonicalNode;
                Exec(db, tx, "INSERT INTO swpc_acks(code,serial,actor,at_utc) VALUES($c,$s,$a,$t) ON CONFLICT(code,serial) DO UPDATE SET actor=excluded.actor,at_utc=excluded.at_utc;",
                    ("$c", node.Code), ("$s", node.Serial), ("$a", request.Acknowledgement.Operator), ("$t", Ticks(request.Acknowledgement.AtUtc)));
                return Result(db, tx, request.AtUtc);
            }, ct);
    }
    private ValueTask<SwpcWriteResult> WriteAsync(string kind, Guid id, long expectedRevision, DateTimeOffset atUtc, string fingerprint,
        Func<SqliteConnection, SqliteTransaction, SwpcWriteResult> action, CancellationToken ct)
    {
        RequireUtc(atUtc);
        if (id == Guid.Empty || expectedRevision < 0) throw new ArgumentException("A durable command requires an identity and revision.");
        return ExecuteAsync(null, (db, tx) =>
        {
            using (var receipt = Command(db, tx, "SELECT fingerprint,added,diagnostics FROM swpc_receipts WHERE kind=$k AND id=$id;", ("$k", kind), ("$id", id.ToString("N"))))
            using (var reader = receipt.ExecuteReader())
                if (reader.Read())
                    return reader.GetString(0) == fingerprint
                        ? Result(db, tx, atUtc, SwpcWriteStatus.Replayed, added: reader.GetInt32(1), diagnostics: Parse<ImmutableArray<string>>(reader.GetString(2)))
                        : Result(db, tx, atUtc, SwpcWriteStatus.Conflict, "space-weather.idempotency-conflict");
            Purge(db, tx, atUtc);
            if (Revision(db, tx) != expectedRevision) return Result(db, tx, atUtc, SwpcWriteStatus.Conflict, "space-weather.revision-conflict");
            var result = action(db, tx);
            if (result.Status != SwpcWriteStatus.Applied) return result;
            Bump(db, tx);
            Exec(db, tx, "INSERT INTO swpc_receipts(kind,id,created,fingerprint,added,diagnostics) VALUES($k,$id,$t,$f,$a,$d);",
                ("$k", kind), ("$id", id.ToString("N")), ("$t", Ticks(atUtc)), ("$f", fingerprint), ("$a", result.AddedVersions), ("$d", Json(result.Diagnostics)));
            PruneAudit(db, tx, atUtc);
            return result with { View = ReadView(db, tx, atUtc) };
        }, ct);
    }

    private SwpcWriteResult Result(SqliteConnection db, SqliteTransaction tx, DateTimeOffset atUtc, SwpcWriteStatus status = SwpcWriteStatus.Applied,
        string? reason = null, int added = 0, ImmutableArray<string> diagnostics = default, ImmutableArray<SwpcAttentionEvent> attention = default)
        => new(status, reason, ReadView(db, tx, atUtc), added, diagnostics.IsDefault ? [] : diagnostics, attention.IsDefault ? [] : attention);

    private static void ValidateHalt(SwpcHaltChange change)
    {
        if (change.Product is { } p && !Enum.IsDefined(p)) throw new ArgumentOutOfRangeException(nameof(change));
        if (change.Value is { } halt) { RequireUtc(halt.SinceUtc); RequireCode(halt.ReasonCode); }
    }
    private static void WriteHalt(SqliteConnection db, SqliteTransaction tx, SwpcHaltChange change)
    {
        ValidateHalt(change);
        var value = change.Value is null ? null : Json(change.Value);
        if (change.Product is { } p) Exec(db, tx, "UPDATE swpc_products SET halt=$h WHERE product=$p;", ("$h", value), ("$p", (int)p));
        else Exec(db, tx, "UPDATE swpc_meta SET provider_halt=$h WHERE id=1;", ("$h", value));
    }
    private static void RequireBounded(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("The value must be bounded, nonblank and free of control characters.");
    }
    private static void RequireCode(string value)
    {
        RequireBounded(value, 128);
        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))
            throw new ArgumentException("A reason code contains unsupported characters.");
    }
    private static string Fingerprint<T>(T value)
    {
        using var document = JsonDocument.Parse(Json(value));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Canonical(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    private static void Canonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Canonical(item, writer); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
}
