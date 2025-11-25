using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface IDeviceIdentityStore
{
    Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default);
}

internal sealed record DeviceIdentity(
    string DeviceId,
    string VerificationCode,
    DateTimeOffset CreatedUtc);

internal sealed class DeviceIdentityStore(
    IOptions<DeviceProvisioningOptions> optionsAccessor,
    TimeProvider timeProvider,
    ILogger<DeviceIdentityStore> logger) : IDeviceIdentityStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly DeviceProvisioningOptions options = optionsAccessor.Value;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private DeviceIdentity? cached;

    public async Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = cached;
        if (snapshot is not null)
        {
            return snapshot;
        }

        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = cached;
            if (snapshot is not null)
            {
                return snapshot;
            }

            var path = options.GetIdentityPath();
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                cached = await JsonSerializer.DeserializeAsync<DeviceIdentity>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Device identity file was empty or malformed.");
                return cached;
            }

            var identity = new DeviceIdentity(
                Guid.NewGuid().ToString("N"),
                GenerateVerificationCode(),
                timeProvider.GetUtcNow());

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(stream, identity, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Generated new device identity {DeviceId}", identity.DeviceId);
            cached = identity;
            return identity;
        }
        finally
        {
            mutex.Release();
        }
    }

    public void Dispose() => mutex.Dispose();

    private static string GenerateVerificationCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[10];
        Span<byte> random = stackalloc byte[chars.Length];
        RandomNumberGenerator.Fill(random);
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[random[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
