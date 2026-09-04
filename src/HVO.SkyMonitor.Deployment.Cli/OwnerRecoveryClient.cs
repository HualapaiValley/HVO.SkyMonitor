using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Deployment;

internal interface IOwnerRecoveryClient
{
    Task<string> RecoverAsync(
        Guid operationId,
        string lifecycleControlToken,
        string temporaryPassword,
        CancellationToken cancellationToken);
}

internal enum OwnerRecoveryFailureDisposition
{
    FreshOperationRequired,
    ResumeRequired,
    Unsupported
}

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Every protocol failure requires an explicit recovery disposition.")]
internal sealed class OwnerRecoveryProtocolException(
    OwnerRecoveryFailureDisposition disposition,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    internal OwnerRecoveryFailureDisposition Disposition { get; } = disposition;
}

internal enum OwnerRecoveryProtocolPhase
{
    Attestation,
    Challenge,
    Completion
}

internal sealed class OwnerRecoveryClient : IOwnerRecoveryClient
{
    private const int AttestationNonceLength = 32;
    private const int MaximumChallengeBytes = 4096;
    private const int MaximumPasswordBytes = 256;
    private const int MaximumStateBytes = 128;
    private const byte CompletionProtocolVersion = 1;
    private const string RecoveryOperationHeader = "X-HVO-Recovery-Operation";
    private const string AttestationPurpose = "HVO.SkyMonitor.CameraAgent.OwnerRecovery.Attestation.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Uri BaseAddress = new("http://localhost", UriKind.Absolute);
    private readonly HttpMessageHandler _handler;

    internal OwnerRecoveryClient(string socketPath, HttpMessageHandler? handler = null)
    {
        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new InstallerException("Owner recovery requires an absolute owner-only Unix socket path.");
        }
        _ = new UnixDomainSocketEndPoint(socketPath);
        _handler = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    public async Task<string> RecoverAsync(
        Guid operationId,
        string lifecycleControlToken,
        string temporaryPassword,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var client = new HttpClient(_handler, disposeHandler: true)
            {
                BaseAddress = BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            await VerifyPeerAsync(client, operationId, lifecycleControlToken, timeout.Token).ConfigureAwait(false);

            using var challengeRequest = CreateRecoveryRequest(
                "/api/internal/owner-bootstrap/recovery/challenge",
                operationId,
                lifecycleControlToken,
                []);
            using var challengeResponse = await client.SendAsync(
                challengeRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            ThrowForRecoveryFailure(challengeResponse, OwnerRecoveryProtocolPhase.Challenge);
            var challengeBytes = await ReadBoundedAsync(
                challengeResponse,
                MaximumChallengeBytes,
                OwnerRecoveryFailureDisposition.ResumeRequired,
                timeout.Token).ConfigureAwait(false);
            var challenge = StrictUtf8.GetString(challengeBytes);
            if (string.IsNullOrWhiteSpace(challenge))
            {
                throw new OwnerRecoveryProtocolException(
                    OwnerRecoveryFailureDisposition.ResumeRequired,
                    "CameraAgent returned an invalid owner recovery challenge; resume the retained operation.");
            }

            var completionPayload = CreateCompletionPayload(challenge, temporaryPassword);
            using var completeRequest = CreateRecoveryRequest(
                "/api/internal/owner-bootstrap/recovery/complete",
                operationId,
                lifecycleControlToken,
                completionPayload);
            using var completeResponse = await client.SendAsync(
                completeRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            ThrowForRecoveryFailure(completeResponse, OwnerRecoveryProtocolPhase.Completion);
            var stateBytes = await ReadBoundedAsync(
                completeResponse,
                MaximumStateBytes,
                OwnerRecoveryFailureDisposition.ResumeRequired,
                timeout.Token).ConfigureAwait(false);
            var state = StrictUtf8.GetString(stateBytes);
            if (string.IsNullOrWhiteSpace(state))
            {
                throw new OwnerRecoveryProtocolException(
                    OwnerRecoveryFailureDisposition.ResumeRequired,
                    "CameraAgent returned an invalid owner recovery result; resume the retained operation.");
            }
            return state;
        }
        catch (Exception exception) when (exception is OwnerRecoveryProtocolException or InstallerException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or DecoderFallbackException or InvalidOperationException)
        {
            throw new OwnerRecoveryProtocolException(
                OwnerRecoveryFailureDisposition.ResumeRequired,
                "CameraAgent owner recovery may be incomplete; resume the retained operation.",
                exception);
        }
    }

    private static async Task VerifyPeerAsync(
        HttpClient client,
        Guid operationId,
        string lifecycleControlToken,
        CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(AttestationNonceLength);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/internal/owner-bootstrap/recovery/attestation", UriKind.Relative));
        request.Headers.Add(RecoveryOperationHeader, operationId.ToString("D"));
        request.Content = new ByteArrayContent(nonce);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        ThrowForRecoveryFailure(response, OwnerRecoveryProtocolPhase.Attestation);
        var proof = await ReadBoundedAsync(
            response,
            AttestationNonceLength,
            OwnerRecoveryFailureDisposition.Unsupported,
            cancellationToken).ConfigureAwait(false);
        var expected = CreateAttestationProof(lifecycleControlToken, operationId, nonce);
        if (proof.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(proof, expected))
        {
            throw new OwnerRecoveryProtocolException(
                OwnerRecoveryFailureDisposition.Unsupported,
                "The loopback listener is not the installed CameraAgent.");
        }
    }

    private static HttpRequestMessage CreateRecoveryRequest(
        string path,
        Guid operationId,
        string lifecycleControlToken,
        byte[] payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(RecoveryOperationHeader, operationId.ToString("D"));
        request.Headers.Add("X-HVO-Installation-Token", lifecycleControlToken);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private static byte[] CreateCompletionPayload(string challenge, string temporaryPassword)
    {
        var challengeBytes = StrictUtf8.GetBytes(challenge);
        var passwordBytes = StrictUtf8.GetBytes(temporaryPassword);
        if (challengeBytes.Length is < 1 or > MaximumChallengeBytes ||
            passwordBytes.Length is < 16 or > MaximumPasswordBytes)
        {
            throw new InstallerException("The owner recovery credential payload is invalid.");
        }

        var payload = new byte[9 + challengeBytes.Length + passwordBytes.Length];
        payload[0] = CompletionProtocolVersion;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1, 4), challengeBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5, 4), passwordBytes.Length);
        challengeBytes.CopyTo(payload, 9);
        passwordBytes.CopyTo(payload, 9 + challengeBytes.Length);
        return payload;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        OwnerRecoveryFailureDisposition invalidResponseDisposition,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new OwnerRecoveryProtocolException(
                invalidResponseDisposition,
                "CameraAgent returned an oversized owner recovery response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        if (total > maximumBytes)
        {
            throw new OwnerRecoveryProtocolException(
                invalidResponseDisposition,
                "CameraAgent returned an oversized owner recovery response.");
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    private static byte[] CreateAttestationProof(string token, Guid operationId, ReadOnlySpan<byte> nonce)
    {
        var prefix = Encoding.UTF8.GetBytes($"{AttestationPurpose}\n{operationId:D}\n");
        var payload = new byte[prefix.Length + nonce.Length];
        prefix.CopyTo(payload, 0);
        nonce.CopyTo(payload.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), payload);
    }

    private static void ThrowForRecoveryFailure(
        HttpResponseMessage response,
        OwnerRecoveryProtocolPhase phase)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500)
        {
            throw new OwnerRecoveryProtocolException(
                OwnerRecoveryFailureDisposition.ResumeRequired,
                "CameraAgent owner recovery may be incomplete; resume the retained operation.");
        }

        if (phase == OwnerRecoveryProtocolPhase.Attestation ||
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound ||
            (int)response.StatusCode is >= 300 and < 400)
        {
            throw new OwnerRecoveryProtocolException(
                OwnerRecoveryFailureDisposition.Unsupported,
                "CameraAgent owner recovery is unsupported or is not enabled for this installation.");
        }

        throw new OwnerRecoveryProtocolException(
            OwnerRecoveryFailureDisposition.FreshOperationRequired,
            "CameraAgent rejected owner recovery before completion; start a new recovery operation.");
    }
}
