using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal sealed partial class LocalOperationsTokenService
{
    private readonly IDataProtectionProvider _provider;
    private readonly TimeSpan _lifetime;

    public LocalOperationsTokenService(
        IDataProtectionProvider provider,
        IOptions<CameraAgentHostOptions> options)
        : this(provider, TimeSpan.FromMinutes(options.Value.OperationsReferenceLifetimeMinutes))
    {
    }

    internal LocalOperationsTokenService(IDataProtectionProvider provider, TimeSpan lifetime)
    {
        _provider = provider;
        _lifetime = lifetime;
    }

    public string ProtectEnvironmentalHistoryReference(long recordId)
        => Protect("environmental-history.reference", new EnvironmentalHistoryReference(recordId));

    public bool TryReadEnvironmentalHistoryReference(string token, out long recordId)
    {
        recordId = 0;
        if (!TryUnprotect<EnvironmentalHistoryReference>("environmental-history.reference", token, out var reference) ||
            reference is null || reference.RecordId <= 0)
        {
            return false;
        }
        recordId = reference.RecordId;
        return true;
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

    private string Protect<T>(string purpose, T payload)
        => _provider.CreateProtector("HVO.SkyMonitor.CameraAgent.LocalOperations.v1", purpose)
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
            var json = _provider.CreateProtector("HVO.SkyMonitor.CameraAgent.LocalOperations.v1", purpose)
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

    private sealed record EnvironmentalHistoryReference(long RecordId);

    private sealed record CursorPayload(string Alias, string? Target, long Position, long RecordId);
}
