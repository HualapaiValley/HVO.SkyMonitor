using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>Queries one time server for this host's clock offset.</summary>
public interface ISntpClient
{
    /// <summary>
    /// Sends one SNTP request to <paramref name="server"/> and measures the offset of this host's clock from it. Every
    /// failure, including a timeout, is returned as a result; only cancellation by <paramref name="cancellationToken"/>
    /// throws.
    /// </summary>
    Task<TimeServerResult> QueryAsync(string server, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// One server's answer. <see cref="Offset"/> is how far the server's clock is ahead of this host's: positive means this
/// host is behind.
/// </summary>
public sealed record TimeServerResult(
    string Server,
    SntpFailure? Failure,
    TimeSpan? Offset,
    TimeSpan? RoundTrip,
    int? Stratum)
{
    public bool Succeeded => Failure is null;

    public static TimeServerResult Failed(string server, SntpFailure failure) => new(server, failure, null, null, null);
}

/// <summary>
/// A single-request SNTP client over UDP. The round trip is timed on the monotonic clock, so a step of the wall clock
/// during the query cannot distort it, and only the send time is read from the wall clock.
/// </summary>
public sealed class SntpClient(TimeProvider timeProvider) : ISntpClient
{
    private const int ReceiveBufferLength = 512;

    public async Task<TimeServerResult> QueryAsync(string server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!TimeSyncSettings.TryParseServer(server, out var host, out var port))
        {
            return TimeServerResult.Failed(server, SntpFailure.InvalidServer);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var address = await ResolveAsync(host, deadline.Token).ConfigureAwait(false);
            if (address is null)
            {
                return TimeServerResult.Failed(server, SntpFailure.Unresolved);
            }
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(new IPEndPoint(address, port), deadline.Token).ConfigureAwait(false);

            var nonce = CreateNonce();
            var request = SntpPacket.CreateRequest(nonce);
            var buffer = new byte[ReceiveBufferLength];
            var sentUtc = timeProvider.GetUtcNow();
            var started = timeProvider.GetTimestamp();
            await socket.SendAsync(request, SocketFlags.None, deadline.Token).ConfigureAwait(false);
            var length = await socket.ReceiveAsync(buffer, SocketFlags.None, deadline.Token).ConfigureAwait(false);
            var receivedUtc = sentUtc + timeProvider.GetElapsedTime(started);

            var reply = SntpPacket.Parse(buffer.AsSpan(0, length), nonce);
            if (reply.Failure is { } failure)
            {
                return TimeServerResult.Failed(server, failure);
            }
            var (offset, roundTrip) = Measure(sentUtc, reply.Received, reply.Transmitted, receivedUtc);
            return new TimeServerResult(server, null, offset, roundTrip, reply.Stratum);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimeServerResult.Failed(server, SntpFailure.Timeout);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.HostNotFound or
            SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery)
        {
            return TimeServerResult.Failed(server, SntpFailure.Unresolved);
        }
        catch (SocketException)
        {
            return TimeServerResult.Failed(server, SntpFailure.Unreachable);
        }
    }

    /// <summary>
    /// The RFC 4330 offset and round-trip delay from the client send (T1), server receive (T2), server transmit (T3)
    /// and client receive (T4) times. A negative delay, which only rounding can produce, is reported as zero.
    /// </summary>
    public static (TimeSpan Offset, TimeSpan RoundTrip) Measure(
        DateTimeOffset sent, DateTimeOffset serverReceived, DateTimeOffset serverTransmitted, DateTimeOffset received)
    {
        var offset = ((serverReceived - sent) + (serverTransmitted - received)) / 2;
        var roundTrip = (received - sent) - (serverTransmitted - serverReceived);
        return (offset, roundTrip < TimeSpan.Zero ? TimeSpan.Zero : roundTrip);
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork) ??
            addresses.FirstOrDefault();
    }

    private static ulong CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong nonce;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            nonce = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        }
        while (nonce == 0);
        return nonce;
    }
}
