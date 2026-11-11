using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

internal sealed partial class RetainedIntegrityLedger
{
    internal bool RefreshDue => _timeProvider.GetUtcNow().AddHours(1) >= _scrubDue;
    internal long Generation => _generation;

    internal void PrepareStartup(byte[] binding, CancellationToken cancellationToken)
    {
        _barrier.Wait(cancellationToken);
        try
        {
            ValidatePathMetadata();
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, _checkpoint);
            if (!File.Exists(_checkpoint)) return;
            using var stream = File.OpenRead(_checkpoint);
            if (stream.Length > 16384) return;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var previous = JsonSerializer.Deserialize<Checkpoint>(_protector.Unprotect(bytes));
            if (previous is null || previous.Nonce != _nonce || previous.Generation != _generation ||
                previous.Chain != Convert.ToHexString(_chain) || previous.Count != _count ||
                previous.Binding != Convert.ToHexString(binding)) return;
            _binding = binding;
            // The new process must finish authentication before any lifecycle success.
            WriteCheckpoint(qualified: false);
        }
        catch (Exception exception) when (OptionalIo(exception)) { Invalidate(); }
        finally { _barrier.Release(); }
    }

    internal IDeploymentLocationProtector ParseProofProtector
        => _entryKey is null ? _protector : new CacheProtector(_protector, HMACSHA256.HashData(_entryKey, "parse-proof-v1"u8));

    private sealed class CacheProtector(IDeploymentLocationProtector legacy, byte[] key) : IDeploymentLocationProtector
    {
        public byte[] Protect(byte[] plaintext)
            => [.. "HVOMAC01"u8, .. HMACSHA256.HashData(key, plaintext), .. plaintext];

        public byte[] Unprotect(byte[] protectedPayload)
        {
            if (!protectedPayload.AsSpan().StartsWith("HVOMAC01"u8)) return legacy.Unprotect(protectedPayload);
            if (protectedPayload.Length < 40 || !CryptographicOperations.FixedTimeEquals(
                protectedPayload.AsSpan(8, 32), HMACSHA256.HashData(key, protectedPayload.AsSpan(40))))
                throw new CryptographicException("Retained parse proof authentication failed.");
            return protectedPayload[40..];
        }
    }
}
