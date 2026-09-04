using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OwnerRecoveryClientTests
{
    private const string AttestationPurpose = "HVO.SkyMonitor.CameraAgent.OwnerRecovery.Attestation.v1";

    [TestMethod]
    public async Task Recover_AuthenticatesPeerBeforeSendingLifecycleTokenOrPassword()
    {
        var operationId = Guid.NewGuid();
        const string token = "retained-lifecycle-token";
        const string password = "RecoveredOwnerPassword!515";
        const string challenge = "protected-recovery-challenge";
        using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) =>
        {
            if (sequence == 1)
            {
                Assert.AreEqual("/api/internal/owner-bootstrap/recovery/attestation", request.RequestUri?.AbsolutePath);
                Assert.IsFalse(request.Headers.Contains("X-HVO-Installation-Token"));
                Assert.AreEqual(operationId.ToString("D"), request.Headers.GetValues("X-HVO-Recovery-Operation").Single());
                var nonce = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                Assert.HasCount(32, nonce);
                return Bytes(CreateAttestationProof(token, operationId, nonce));
            }

            Assert.AreEqual(token, request.Headers.GetValues("X-HVO-Installation-Token").Single());
            if (sequence == 2)
            {
                Assert.AreEqual("/api/internal/owner-bootstrap/recovery/challenge", request.RequestUri?.AbsolutePath);
                Assert.AreEqual(0, request.Content?.Headers.ContentLength);
                return Bytes(Encoding.UTF8.GetBytes(challenge));
            }

            Assert.AreEqual(3, sequence);
            Assert.AreEqual("/api/internal/owner-bootstrap/recovery/complete", request.RequestUri?.AbsolutePath);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1, body[0]);
            var challengeLength = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1, 4));
            var passwordLength = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(5, 4));
            Assert.AreEqual(challenge, Encoding.UTF8.GetString(body, 9, challengeLength));
            Assert.AreEqual(password, Encoding.UTF8.GetString(body, 9 + challengeLength, passwordLength));
            return Bytes(Encoding.UTF8.GetBytes("owner-password-change-required"));
        });
        var client = new OwnerRecoveryClient("/tmp/hvo-owner-recovery-test.sock", handler);

        await client.RecoverAsync(operationId, token, password, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task Recover_RejectsUnattestedOrRedirectingListenerBeforeDisclosingSecrets()
    {
        foreach (var response in new[]
        {
            Bytes(new byte[32]),
            new HttpResponseMessage(HttpStatusCode.Found)
        })
        {
            var requestCount = 0;
            using var handler = new ScriptedHandler(async (request, _, cancellationToken) =>
            {
                requestCount++;
                Assert.IsFalse(request.Headers.Contains("X-HVO-Installation-Token"));
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                Assert.IsFalse(Encoding.UTF8.GetString(body).Contains("RecoveredOwnerPassword", StringComparison.Ordinal));
                return response;
            });
            var client = new OwnerRecoveryClient("/tmp/hvo-owner-recovery-test.sock", handler);

            var exception = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => client.RecoverAsync(
                Guid.NewGuid(),
                "retained-lifecycle-token",
                "RecoveredOwnerPassword!515",
                CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(OwnerRecoveryFailureDisposition.Unsupported, exception.Disposition);
            Assert.AreEqual(1, requestCount);
        }
    }

    [TestMethod]
    public async Task Recover_RejectsOversizedResponseBeforeReadingRecoveryMaterial()
    {
        using var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Bytes(new byte[33])));
        var client = new OwnerRecoveryClient("/tmp/hvo-owner-recovery-test.sock", handler);

        var exception = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => client.RecoverAsync(
            Guid.NewGuid(),
            "retained-lifecycle-token",
            "RecoveredOwnerPassword!515",
            CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(OwnerRecoveryFailureDisposition.Unsupported, exception.Disposition);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Recover_ClassifiesAttestationStatusWithoutDisclosingSecrets()
    {
        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.Unauthorized, OwnerRecoveryFailureDisposition.Unsupported),
            (HttpStatusCode.NotFound, OwnerRecoveryFailureDisposition.Unsupported),
            (HttpStatusCode.RequestTimeout, OwnerRecoveryFailureDisposition.ResumeRequired),
            (HttpStatusCode.TooManyRequests, OwnerRecoveryFailureDisposition.ResumeRequired),
            (HttpStatusCode.ServiceUnavailable, OwnerRecoveryFailureDisposition.ResumeRequired)
        })
        {
            using var handler = new ScriptedHandler((request, _, _) =>
            {
                Assert.IsFalse(request.Headers.Contains("X-HVO-Installation-Token"));
                return Task.FromResult(new HttpResponseMessage(status));
            });
            var client = new OwnerRecoveryClient("/tmp/hvo-owner-recovery-test.sock", handler);

            var exception = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => client.RecoverAsync(
                Guid.NewGuid(),
                "retained-lifecycle-token",
                "RecoveredOwnerPassword!515",
                CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(expected, exception.Disposition);
            Assert.AreEqual(1, handler.RequestCount);
        }
    }

    [TestMethod]
    public async Task Recover_ClassifiesChallengeAndCompletionStatusByCommitRisk()
    {
        foreach (var (failingSequence, status, expected) in new[]
        {
            (2, HttpStatusCode.BadRequest, OwnerRecoveryFailureDisposition.FreshOperationRequired),
            (2, HttpStatusCode.Unauthorized, OwnerRecoveryFailureDisposition.Unsupported),
            (2, HttpStatusCode.TooManyRequests, OwnerRecoveryFailureDisposition.ResumeRequired),
            (3, HttpStatusCode.Conflict, OwnerRecoveryFailureDisposition.FreshOperationRequired),
            (3, HttpStatusCode.ServiceUnavailable, OwnerRecoveryFailureDisposition.ResumeRequired)
        })
        {
            var operationId = Guid.NewGuid();
            const string token = "retained-lifecycle-token";
            using var handler = new ScriptedHandler(async (request, sequence, cancellationToken) =>
            {
                if (sequence == failingSequence)
                {
                    return new HttpResponseMessage(status);
                }
                if (sequence == 1)
                {
                    var nonce = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    return Bytes(CreateAttestationProof(token, operationId, nonce));
                }
                return Bytes(Encoding.UTF8.GetBytes("protected-recovery-challenge"));
            });
            var client = new OwnerRecoveryClient("/tmp/hvo-owner-recovery-test.sock", handler);

            var exception = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => client.RecoverAsync(
                operationId,
                token,
                "RecoveredOwnerPassword!515",
                CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(expected, exception.Disposition);
            Assert.AreEqual(failingSequence, handler.RequestCount);
        }
    }

    private static byte[] CreateAttestationProof(string token, Guid operationId, ReadOnlySpan<byte> nonce)
    {
        var prefix = Encoding.UTF8.GetBytes($"{AttestationPurpose}\n{operationId:D}\n");
        var payload = new byte[prefix.Length + nonce.Length];
        prefix.CopyTo(payload, 0);
        nonce.CopyTo(payload.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), payload);
    }

    private static HttpResponseMessage Bytes(byte[] value)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
}
