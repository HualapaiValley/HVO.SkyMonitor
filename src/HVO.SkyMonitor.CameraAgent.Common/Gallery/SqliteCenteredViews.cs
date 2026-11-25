using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews(
    SqliteCaptureProcessingStore store, TimeProvider clock, ILogger<SqliteCenteredViews> logger,
    RawCaptureIngress? ingress = null)
    : ICenteredViewReadIndex, ICenteredViewPublisher
{
    private const int MaximumPageSize = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new CaptureCycleWireConverter() }
    };
    public event Action<CenteredViewStream>? Changed;

    private ValueTask<SqliteConnection> OpenAsync(CancellationToken token) => store.OpenCenteredViewsAsync(token);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A subscriber cannot undo a committed publication or prevent other subscribers from waking.")]
    private void Notify(CenteredViewStream stream)
    {
        var listeners = Changed?.GetInvocationList();
        if (listeners is null)
            return;
        foreach (var listener in listeners)
        {
            try
            {
                ((Action<CenteredViewStream>)listener)(stream);
            }
            catch (Exception exception)
            {
                LogSubscriberFailure(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A centered view subscriber failed after publication committed.")]
    private static partial void LogSubscriberFailure(ILogger logger, Exception exception);

    private static SqliteTransaction BeginWrite(SqliteConnection connection)
    {
#pragma warning disable CA1849 // SQLite requires its synchronous overload to acquire the write transaction up front.
        return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement comes from an internal constant and uses bound parameters.")]
    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async ValueTask ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static string Serialize<T>(T value) => CaptureContractJson.Canonicalize(
        JsonSerializer.SerializeToElement(value, JsonOptions)).GetRawText();

    private static T Parse<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Centered view record is missing.");

    // CaptureCycleEvidence predates the strict view record contract. Its wire
    // format omits three null constructor values; JsonRequired still enforces
    // the actual mandatory capture facts. Writing with the same options as the
    // previous view writer keeps published base identities unchanged.
    private sealed class CaptureCycleWireConverter : JsonConverter<CaptureCycleEvidence>
    {
        private static readonly JsonSerializerOptions CaptureOptions = new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = false
        };

        public override CaptureCycleEvidence? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<CaptureCycleEvidence>(ref reader, CaptureOptions);

        public override void Write(Utf8JsonWriter writer, CaptureCycleEvidence value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, CaptureOptions);
    }

    private static string Identity<T>(T value) => CaptureContractJson.ComputeCanonicalJsonSha256(
        JsonSerializer.SerializeToElement(value, JsonOptions));

    internal static string ExposureIdentity(ExposureKey exposure) => Identity(exposure);

    private static string StreamId(CenteredViewStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(stream.Camera);
        ValidateText(stream.Camera.AgentId);
        ValidateText(stream.Camera.CameraId);
        ValidateText(stream.GenerationId);
        ArgumentOutOfRangeException.ThrowIfEqual(stream.RunId, Guid.Empty);
        return Identity(stream);
    }

    private static void ValidatePage(long afterOrder, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumPageSize);
    }

    private static long ToTicks(DateTimeOffset time) => time.UtcTicks;
    private static DateTimeOffset ReadTime(SqliteDataReader reader, int index) =>
        new(reader.GetInt64(index), TimeSpan.Zero);
    private static long? ReadNullableOrder(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static string? ReadNullableText(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset? ReadNullableTime(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : ReadTime(reader, index);

}
