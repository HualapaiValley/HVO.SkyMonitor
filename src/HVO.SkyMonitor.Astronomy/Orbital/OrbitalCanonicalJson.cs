using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Streams the fixed snapshot graphs without a DOM; other public canonical-JSON inputs retain v1 semantics.</summary>
internal static class OrbitalCanonicalJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions(omitRecordIdentity: false);
    private static readonly JsonSerializerOptions RecordIdentityOptions = CreateOptions(omitRecordIdentity: true);

    internal static byte[] Serialize<T>(T value)
    {
        // SerializeToUtf8Bytes uses pooled scratch space and only allocates the returned payload array.
        return IsSnapshotGraph<T>() ? JsonSerializer.SerializeToUtf8Bytes(value, Options) : SerializeOther(value);
    }

    internal static (string Sha256, int Length) Digest<T>(T value, bool omitRecordIdentity = false)
    {
        using var stream = new HashStream();
        // The Stream overload flushes in bounded chunks even for the entire record array.
        if (IsSnapshotGraph<T>())
            JsonSerializer.Serialize(stream, value, omitRecordIdentity ? RecordIdentityOptions : Options);
        else
        {
            using var writer = new Utf8JsonWriter(stream);
            WriteNormalized(writer, CaptureContractJson.SerializeToElement(value));
        }
        return (Convert.ToHexString(stream.Hash.GetHashAndReset()), checked((int)stream.Length));
    }

    // Keep the generic API's declared-type and custom-converter semantics for all other inputs.
    private static bool IsSnapshotGraph<T>() => typeof(T) == typeof(OrbitalRecordV1) ||
        typeof(T) == typeof(OrbitalRecordV1[]) || typeof(T) == typeof(IReadOnlyList<OrbitalRecordV1>) ||
        typeof(T) == typeof(OrbitalSnapshotManifestV1);

    private static byte[] SerializeOther<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteNormalized(writer, CaptureContractJson.SerializeToElement(value));
        return buffer.WrittenSpan.ToArray();
    }

    private static JsonSerializerOptions CreateOptions(bool omitRecordIdentity)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            if (omitRecordIdentity && info.Type == typeof(OrbitalRecordV1))
                info.Properties.Remove(info.Properties.Single(static p => p.Name == "recordSha256"));
            var order = 0;
            foreach (var property in info.Properties.OrderBy(static p => p.Name, StringComparer.Ordinal))
                property.Order = order++;
        });
        return new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
            Converters = { new ZeroDoubleConverter(), new CanonicalDateTimeOffsetConverter(),
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
        };
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteNormalized(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteNormalized(writer, item);
            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number == 0)
            writer.WriteNumberValue(0);
        else value.WriteTo(writer);
    }

    private sealed class ZeroDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value == 0 ? 0 : value);
    }

    private sealed class CanonicalDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            // The v1 DOM writer escapes '+' in the ISO offset, unlike the built-in date writer.
            => writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
    }

    private sealed class HashStream : Stream
    {
        internal IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Hash.AppendData(buffer);
            length += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
