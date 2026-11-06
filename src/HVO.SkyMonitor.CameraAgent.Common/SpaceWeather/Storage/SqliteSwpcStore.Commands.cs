using System.Buffers;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            var identityConflict = false;
            using (var receipt = Command(db, tx, "SELECT fingerprint,added,diagnostics FROM swpc_receipts WHERE kind=$k AND id=$id;", ("$k", kind), ("$id", id.ToString("N"))))
            using (var reader = receipt.ExecuteReader())
                if (reader.Read())
                {
                    if (reader.GetString(0) == fingerprint)
                        return Result(db, tx, atUtc, SwpcWriteStatus.Replayed, added: reader.GetInt32(1), diagnostics: Parse<ImmutableArray<string>>(reader.GetString(2)));
                    identityConflict = true;
                }
            Purge(db, tx, atUtc);
            if (identityConflict && Scalar(db, tx, "SELECT 1 FROM swpc_receipts WHERE kind=$k AND id=$id;", ("$k", kind), ("$id", id.ToString("N"))) is not null)
                return Result(db, tx, atUtc, SwpcWriteStatus.Conflict, "space-weather.idempotency-conflict");
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
    /// <summary>
    /// SHA-256 of the serializer's UTF-8 JSON with object members ordered ordinally. Stored fingerprints
    /// are a durable replay format: the bytes hashed must stay identical to every earlier binary's.
    /// A byte array is serialized as a placeholder and expanded only in the canonical output, as the
    /// same number array, so a decoded body is neither parsed back nor rewritten value by value.
    /// </summary>
    internal static string Fingerprint<T>(T value)
    {
        var bodies = DeferredBodies ??= [];
        try
        {
            using var serialized = new PooledBuffer();
            using (var writer = new Utf8JsonWriter(serialized)) JsonSerializer.Serialize(writer, value, FingerprintSerializer);
            using var document = JsonDocument.Parse(serialized.Written);
            using var canonical = new PooledBuffer();
            using (var writer = new Utf8JsonWriter(canonical)) Canonical(document.RootElement, writer, bodies);
            return Convert.ToHexString(SHA256.HashData(canonical.Written.Span));
        }
        finally { bodies.Clear(); }
    }
    private static void Canonical(JsonElement element, Utf8JsonWriter writer, List<ImmutableArray<byte>> bodies)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(property.Value, writer, bodies); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Canonical(item, writer, bodies); writer.WriteEndArray(); }
        else if (element.ValueKind == JsonValueKind.String && Deferred(JsonMarshal.GetRawUtf8Value(element), bodies) is { IsDefault: false } body)
            WriteBytes(writer, body.AsSpan());
        else element.WriteTo(writer);
    }

    // The placeholder carries a per-process random marker, so no serialized text can collide with it.
    private static readonly string DeferredMarker = "hvo-deferred-bytes-" + Guid.NewGuid().ToString("N") + "-";
    private static readonly byte[] DeferredQuoted = Encoding.UTF8.GetBytes("\"" + DeferredMarker);
    private static readonly JsonSerializerOptions FingerprintSerializer = new() { Converters = { new DeferredBytes() } };
    [ThreadStatic] private static List<ImmutableArray<byte>>? DeferredBodies;

    private static ImmutableArray<byte> Deferred(ReadOnlySpan<byte> raw, List<ImmutableArray<byte>> bodies)
    {
        if (!raw.StartsWith(DeferredQuoted) || raw[^1] != (byte)'"') return default;
        var digits = raw[DeferredQuoted.Length..^1];
        return Utf8Parser.TryParse(digits, out int index, out var consumed) && consumed == digits.Length && index >= 0 && index < bodies.Count
            ? bodies[index] : default;
    }
    /// <summary>Writes the default serializer's compact decimal number array for <paramref name="body"/>.</summary>
    private static void WriteBytes(Utf8JsonWriter writer, ReadOnlySpan<byte> body)
    {
        var text = ArrayPool<byte>.Shared.Rent(checked(2 + 4 * body.Length));
        try
        {
            var length = 0;
            text[length++] = (byte)'[';
            for (var i = 0; i < body.Length; i++)
            {
                if (i > 0) text[length++] = (byte)',';
                var value = body[i];
                if (value >= 100) text[length++] = (byte)('0' + value / 100);
                if (value >= 10) text[length++] = (byte)('0' + value / 10 % 10);
                text[length++] = (byte)('0' + value % 10);
            }
            text[length++] = (byte)']';
            writer.WriteRawValue(text.AsSpan(0, length), skipInputValidation: true);
        }
        finally { ArrayPool<byte>.Shared.Return(text); }
    }
    private sealed class DeferredBytes : JsonConverter<ImmutableArray<byte>>
    {
        public override ImmutableArray<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ImmutableArray<byte> value, JsonSerializerOptions options)
        {
            if (value.IsDefault || DeferredBodies is not { } bodies) { JsonSerializer.Serialize(writer, value, JsonSerializerOptions.Default); return; }
            writer.WriteStringValue(DeferredMarker + bodies.Count.ToString(CultureInfo.InvariantCulture));
            bodies.Add(value);
        }
    }

    /// <summary>Growable pooled scratch for one fingerprint; the rented array returns on dispose.</summary>
    private sealed class PooledBuffer : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private int _written;
        public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _written);
        public void Advance(int count) => _written += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => Reserve(sizeHint).AsMemory(_written);
        public Span<byte> GetSpan(int sizeHint = 0) => Reserve(sizeHint).AsSpan(_written);
        private byte[] Reserve(int sizeHint)
        {
            var required = checked(_written + Math.Max(sizeHint, 1));
            if (required <= _buffer.Length) return _buffer;
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, checked(_buffer.Length * 2)));
            _buffer.AsSpan(0, _written).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            return _buffer = grown;
        }
        public void Dispose() { ArrayPool<byte>.Shared.Return(_buffer); _buffer = []; }
    }
}
