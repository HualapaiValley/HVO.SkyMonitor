using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
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

    public string ProtectEnvironmentalHistoryReference(long recordId)
        => Protect("environmental-history.reference", new TokenPayload("local-history", recordId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));

    public bool TryReadEnvironmentalHistoryReference(string token, out long recordId)
    {
        recordId = 0;
        return TryReadTarget("environmental-history.reference", token, out var alias, out var value) &&
            alias == "local-history" &&
            long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out recordId) &&
            recordId > 0;
    }

    public string ProtectEnvironmentalHistoryCursor(
        LocalEnvironmentalObservationCursor cursor,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind? kind)
        => Protect("environmental-history.cursor", new CursorPayload(
            "local-history", kind?.ToString() ?? "All", cursor.ObservedAtUtc.ToUnixTimeMilliseconds(), cursor.RecordId));

    public bool TryReadEnvironmentalHistoryCursor(
        string token,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind? kind,
        out LocalEnvironmentalObservationCursor? cursor)
    {
        cursor = null;
        if (!TryUnprotect("environmental-history.cursor", token, out CursorPayload? payload) ||
            payload?.Alias != "local-history" || payload.Target != (kind?.ToString() ?? "All") || payload.RecordId < 1)
        {
            return false;
        }
        try
        {
            cursor = new LocalEnvironmentalObservationCursor(
                DateTimeOffset.FromUnixTimeMilliseconds(payload.Position), payload.RecordId);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
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

    public string ProtectExecutionEvidenceReference(long recordId)
        => Protect("execution-evidence.reference", new TokenPayload(
            "evidence", recordId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public bool TryReadExecutionEvidenceReference(string token, out long recordId)
    {
        recordId = 0;
        return TryReadTarget("execution-evidence.reference", token, out var alias, out var value) &&
            alias == "evidence" &&
            long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out recordId) &&
            recordId > 0;
    }

    public string ProtectExecutionEvidenceAction(OutboxOperationAction action, long recordId)
        => Protect(
            ActionPurpose("execution-evidence", action),
            new TokenPayload("evidence", recordId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public bool TryReadExecutionEvidenceAction(OutboxOperationAction action, string token, out long recordId)
    {
        recordId = 0;
        return TryReadTarget(ActionPurpose("execution-evidence", action), token, out var alias, out var value) &&
            alias == "evidence" &&
            long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out recordId) &&
            recordId > 0;
    }

    public string ProtectExecutionEvidenceCursor(ExecutionEvidenceOutboxOperationsCursor cursor)
        => Protect("execution-evidence.cursor", new CursorPayload("evidence", null, cursor.RecordId, cursor.RecordId));

    public bool TryReadExecutionEvidenceCursor(string token, out ExecutionEvidenceOutboxOperationsCursor? cursor)
    {
        cursor = null;
        if (!TryUnprotect("execution-evidence.cursor", token, out CursorPayload? payload) ||
            payload?.Alias != "evidence" || payload.RecordId < 1)
        {
            return false;
        }
        cursor = new ExecutionEvidenceOutboxOperationsCursor(payload.RecordId);
        return true;
    }

    public string ProtectTransientRuntimeReference(TransientRuntimeOperationTarget target)
        => Protect("transient-runtime.reference", target);

    public bool TryReadTransientRuntimeReference(
        string token,
        out TransientRuntimeOperationTarget? target)
        => TryUnprotect("transient-runtime.reference", token, out target) &&
            target?.ExternalOwnershipEvidence is null;

    public string ProtectTransientRuntimeAction(TransientRuntimeOperationTarget target)
        => Protect("transient-runtime.action.abandon", target);

    public bool TryReadTransientRuntimeAction(
        string token,
        out TransientRuntimeOperationTarget? target)
        => TryUnprotect("transient-runtime.action.abandon", token, out target) &&
            target?.ExternalOwnershipEvidence is not null;

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

    public string ProtectTransientRuntimeCursor(TransientRuntimeQuarantineCursor cursor)
        => Protect("transient-runtime.cursor", new CursorPayload(
            "transient-runtime", null, cursor.UpdatedUnixMs, cursor.RawCaptureRowId));

    public bool TryReadTransientRuntimeCursor(
        string token,
        out TransientRuntimeQuarantineCursor? cursor)
    {
        cursor = null;
        if (!TryUnprotect("transient-runtime.cursor", token, out CursorPayload? payload) ||
            payload?.Alias != "transient-runtime" || payload.Position < 1 || payload.RecordId < 1)
        {
            return false;
        }
        cursor = new TransientRuntimeQuarantineCursor(payload.Position, payload.RecordId);
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

    private static string ActionPurpose(string kind, OutboxOperationAction action) => action switch
    {
        OutboxOperationAction.Replay => string.Concat(kind, ".action.replay"),
        OutboxOperationAction.Abandon => string.Concat(kind, ".action.abandon"),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private sealed record TokenPayload(string Alias, string RecordKey);

    private sealed record CursorPayload(string Alias, string? Target, long Position, long RecordId);
}
