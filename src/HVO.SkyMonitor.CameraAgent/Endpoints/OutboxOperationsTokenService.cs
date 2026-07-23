using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal sealed class OutboxOperationsTokenService
{
    private readonly IDataProtectionProvider _provider;
    private readonly TimeSpan _lifetime;

    public OutboxOperationsTokenService(
        IDataProtectionProvider provider,
        IOptions<CameraAgentHostOptions> options)
        : this(provider, TimeSpan.FromMinutes(options.Value.OperationsReferenceLifetimeMinutes))
    {
    }

    internal OutboxOperationsTokenService(IDataProtectionProvider provider, TimeSpan lifetime)
    {
        _provider = provider;
        _lifetime = lifetime;
    }

    public string ProtectArtifactReference(string alias, string recordKey)
        => Protect("artifact.reference", new TokenPayload(alias, recordKey));

    public bool TryReadArtifactReference(string token, out string alias, out string recordKey)
        => TryReadTarget("artifact.reference", token, out alias, out recordKey);

    public string ProtectEnvironmentalReference(long recordId)
        => Protect("environmental.reference", new TokenPayload("raw-ingress", recordId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public bool TryReadEnvironmentalReference(string token, out long recordId)
    {
        recordId = 0;
        return TryReadTarget("environmental.reference", token, out var alias, out var value) &&
            alias == "raw-ingress" &&
            long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out recordId) &&
            recordId > 0;
    }

    public string ProtectArtifactAction(OutboxOperationAction action, string alias, string recordKey)
        => Protect(ActionPurpose("artifact", action), new TokenPayload(alias, recordKey));

    public bool TryReadArtifactAction(
        OutboxOperationAction action,
        string token,
        out string alias,
        out string recordKey)
        => TryReadTarget(ActionPurpose("artifact", action), token, out alias, out recordKey);

    public string ProtectEnvironmentalAction(OutboxOperationAction action, long recordId)
        => Protect(
            ActionPurpose("environmental", action),
            new TokenPayload("raw-ingress", recordId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public bool TryReadEnvironmentalAction(OutboxOperationAction action, string token, out long recordId)
    {
        recordId = 0;
        return TryReadTarget(ActionPurpose("environmental", action), token, out var alias, out var value) &&
            alias == "raw-ingress" &&
            long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out recordId) &&
            recordId > 0;
    }

    public string ProtectArtifactCursor(string alias, ArtifactOutboxOperationsCursor cursor)
        => Protect("artifact.cursor", new CursorPayload(alias, null, cursor.RecordId, cursor.RecordId));

    public bool TryReadArtifactCursor(string token, string alias, out ArtifactOutboxOperationsCursor? cursor)
    {
        cursor = null;
        if (!TryUnprotect("artifact.cursor", token, out CursorPayload? payload) || payload?.Alias != alias || payload.RecordId < 1)
        {
            return false;
        }
        cursor = new ArtifactOutboxOperationsCursor(payload.RecordId);
        return true;
    }

    public string ProtectEnvironmentalCursor(EnvironmentalOutboxOperationsCursor cursor)
        => Protect("environmental.cursor", new CursorPayload("raw-ingress", null, cursor.RecordId, cursor.RecordId));

    public bool TryReadEnvironmentalCursor(string token, out EnvironmentalOutboxOperationsCursor? cursor)
    {
        cursor = null;
        if (!TryUnprotect("environmental.cursor", token, out CursorPayload? payload) ||
            payload?.Alias != "raw-ingress" || payload.RecordId < 1)
        {
            return false;
        }
        cursor = new EnvironmentalOutboxOperationsCursor(payload.RecordId);
        return true;
    }

    public string ProtectAuditCursor(string kind, string target, OutboxOperationsAuditCursor cursor)
        => Protect($"{kind}.audit.cursor", new CursorPayload(string.Empty, target, cursor.Sequence, 0));

    public bool TryReadAuditCursor(
        string kind,
        string token,
        string target,
        out OutboxOperationsAuditCursor? cursor)
    {
        cursor = null;
        if (!TryUnprotect($"{kind}.audit.cursor", token, out CursorPayload? payload) ||
            payload?.Target != target || payload.Position < 1)
        {
            return false;
        }
        cursor = new OutboxOperationsAuditCursor(payload.Position);
        return true;
    }

    private bool TryReadTarget(string purpose, string token, out string alias, out string recordKey)
    {
        alias = string.Empty;
        recordKey = string.Empty;
        if (!TryUnprotect(purpose, token, out TokenPayload? payload) ||
            string.IsNullOrWhiteSpace(payload?.Alias) || string.IsNullOrWhiteSpace(payload.RecordKey))
        {
            return false;
        }
        alias = payload.Alias;
        recordKey = payload.RecordKey;
        return true;
    }

    private string Protect<T>(string purpose, T payload)
        => _provider.CreateProtector("HVO.SkyMonitor.CameraAgent.OutboxOperations.v1", purpose)
            .ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(payload), _lifetime);

    private bool TryUnprotect<T>(string purpose, string token, out T? payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        try
        {
            var json = _provider.CreateProtector("HVO.SkyMonitor.CameraAgent.OutboxOperations.v1", purpose)
                .ToTimeLimitedDataProtector()
                .Unprotect(token);
            payload = JsonSerializer.Deserialize<T>(json);
            return payload is not null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private static string ActionPurpose(string kind, OutboxOperationAction action)
        => string.Concat(kind, ".action.", action == OutboxOperationAction.Replay ? "replay" : "abandon");

    private sealed record TokenPayload(string Alias, string RecordKey);

    private sealed record CursorPayload(string Alias, string? Target, long Position, long RecordId);
}
