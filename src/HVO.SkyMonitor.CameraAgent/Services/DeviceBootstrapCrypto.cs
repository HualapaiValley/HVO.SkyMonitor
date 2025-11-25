using System;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Services.Models;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal static class DeviceBootstrapCrypto
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static DeviceBootstrapSecretsPayload Decrypt(DeviceBootstrapPayloadDto payload, string deviceKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(deviceKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Device key was not valid Base64.", ex);
        }

        if (keyBytes.Length != 32)
        {
            throw new InvalidOperationException("Device key must be 256 bits.");
        }

        var nonce = Convert.FromBase64String(payload.Nonce ?? throw new InvalidOperationException("Payload missing nonce."));
        var ciphertext = Convert.FromBase64String(payload.Ciphertext ?? throw new InvalidOperationException("Payload missing ciphertext."));
        var tag = Convert.FromBase64String(payload.Tag ?? throw new InvalidOperationException("Payload missing tag."));

        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(keyBytes, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        var secrets = JsonSerializer.Deserialize<DeviceBootstrapSecretsPayload>(plaintext, SerializerOptions)
            ?? throw new InvalidOperationException("Bootstrap payload could not be parsed.");
        return secrets;
    }
}
