using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal sealed class CalibrationOperationsTokenService
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public CalibrationOperationsTokenService(
        IDataProtectionProvider provider,
        IOptions<CameraAgentHostOptions> options)
        : this(provider, TimeSpan.FromMinutes(options.Value.OperationsReferenceLifetimeMinutes))
    {
    }

    internal CalibrationOperationsTokenService(IDataProtectionProvider provider, TimeSpan lifetime)
    {
        _protector = provider
            .CreateProtector("HVO.SkyMonitor.CameraAgent.CalibrationOperations.v1", "bundle.cursor")
            .ToTimeLimitedDataProtector();
        _lifetime = lifetime;
    }

    internal string Protect(CalibrationLibraryBundleCursor cursor)
        => _protector.Protect(JsonSerializer.Serialize(new CursorPayload(
            Version, cursor.CreatedUtc.ToUnixTimeMilliseconds(), cursor.BundleId), SerializerOptions), _lifetime);

    internal bool TryUnprotect(string? token, out CalibrationLibraryBundleCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(_protector.Unprotect(token), SerializerOptions);
            if (payload is null || payload.Version != Version || payload.CreatedUnixMs < 0 ||
                string.IsNullOrWhiteSpace(payload.BundleId) || payload.BundleId.Length > 128)
            {
                return false;
            }
            cursor = new CalibrationLibraryBundleCursor(
                DateTimeOffset.FromUnixTimeMilliseconds(payload.CreatedUnixMs), payload.BundleId);
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(int Version, long CreatedUnixMs, string BundleId);
}
