using System.Buffers.Binary;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>
/// The SNTP (RFC 4330) packet format: builds a client request and validates a server reply, with no I/O, so the checks
/// that decide whether a reply may be trusted can be exercised against fixed bytes.
/// </summary>
public static class SntpPacket
{
    public const int Port = 123;

    public const int Length = 48;

    /// <summary>
    /// The largest root distance (root delay / 2 + root dispersion) a reply may claim. A server further than this from its
    /// reference clock cannot say anything useful about a 500 ms tolerance; RFC 5905 uses the same bound.
    /// </summary>
    public static readonly TimeSpan MaximumRootDistance = TimeSpan.FromSeconds(1.5);

    private const int ModeClient = 3;
    private const int ModeServer = 4;
    private const int LeapAlarm = 3;
    private const int OriginateOffset = 24;
    private const int ReceiveOffset = 32;
    private const int TransmitOffset = 40;

    // RFC 4330 section 3: a timestamp whose most significant bit is set counts from 1900, and one whose bit is clear
    // counts from 7 February 2036, the start of the next 136-year era, which keeps timestamps unambiguous until 2104.
    private static readonly DateTimeOffset Era0 = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Era1 = new(2036, 2, 7, 6, 28, 16, TimeSpan.Zero);

    /// <summary>
    /// Builds a version 4 client request. Its transmit timestamp is <paramref name="nonce"/>, not the local time: the
    /// server echoes it as the reply's originate timestamp, which is how a reply is matched to this request without
    /// revealing the local clock, as RFC 9109 recommends.
    /// </summary>
    public static byte[] CreateRequest(ulong nonce)
    {
        var packet = new byte[Length];
        packet[0] = (0 << 6) | (4 << 3) | ModeClient;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(TransmitOffset), nonce);
        return packet;
    }

    /// <summary>
    /// Validates <paramref name="reply"/> as the answer to the request carrying <paramref name="nonce"/> and returns its
    /// receive (T2) and transmit (T3) times, or the reason it cannot be used. A reply that fails any check is never
    /// treated as a zero offset.
    /// </summary>
    public static SntpReply Parse(ReadOnlySpan<byte> reply, ulong nonce)
    {
        if (reply.Length < Length)
        {
            return SntpReply.Rejected(SntpFailure.InvalidReply);
        }
        var leap = reply[0] >> 6;
        var version = (reply[0] >> 3) & 0x7;
        var mode = reply[0] & 0x7;
        var stratum = reply[1];
        if (mode != ModeServer || version is < 3 or > 4)
        {
            return SntpReply.Rejected(SntpFailure.InvalidReply);
        }
        if (BinaryPrimitives.ReadUInt64BigEndian(reply[OriginateOffset..]) != nonce)
        {
            return SntpReply.Rejected(SntpFailure.InvalidReply);
        }
        if (stratum == 0)
        {
            // A kiss-o'-death packet: the server declines to serve this client (RATE, DENY, RSTR).
            return SntpReply.Rejected(SntpFailure.Refused);
        }
        if (leap == LeapAlarm || stratum >= 16)
        {
            return SntpReply.Rejected(SntpFailure.Unsynchronized);
        }
        var received = BinaryPrimitives.ReadUInt64BigEndian(reply[ReceiveOffset..]);
        var transmitted = BinaryPrimitives.ReadUInt64BigEndian(reply[TransmitOffset..]);
        if (received == 0 || transmitted == 0)
        {
            return SntpReply.Rejected(SntpFailure.InvalidReply);
        }
        var rootDelay = ShortFormat(BinaryPrimitives.ReadUInt32BigEndian(reply[4..]));
        var rootDispersion = ShortFormat(BinaryPrimitives.ReadUInt32BigEndian(reply[8..]));
        if (rootDelay < TimeSpan.Zero || (rootDelay / 2) + rootDispersion > MaximumRootDistance)
        {
            return SntpReply.Rejected(SntpFailure.Unsynchronized);
        }
        return new SntpReply(null, stratum, ToDateTimeOffset(received), ToDateTimeOffset(transmitted));
    }

    /// <summary>Encodes <paramref name="value"/> as a 64-bit NTP timestamp in the era that contains it.</summary>
    public static ulong ToTimestamp(DateTimeOffset value)
    {
        var era = value >= Era1 ? Era1 : Era0;
        var ticks = (value - era).Ticks;
        var seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        var fraction = (ulong)((ticks % TimeSpan.TicksPerSecond) * (1L << 32) / TimeSpan.TicksPerSecond);
        return (seconds << 32) | fraction;
    }

    public static DateTimeOffset ToDateTimeOffset(ulong timestamp)
    {
        var seconds = timestamp >> 32;
        var fraction = timestamp & 0xFFFF_FFFF;
        var era = (seconds & 0x8000_0000) != 0 ? Era0 : Era1;
        var ticks = ((long)seconds * TimeSpan.TicksPerSecond) + (long)((fraction * TimeSpan.TicksPerSecond) >> 32);
        return era.AddTicks(ticks);
    }

    // NTP short format: a signed 16.16 fixed-point number of seconds.
    private static TimeSpan ShortFormat(uint value)
        => TimeSpan.FromTicks((long)(int)value * TimeSpan.TicksPerSecond >> 16);
}

/// <summary>A validated server reply, or the reason it was rejected.</summary>
public sealed record SntpReply(SntpFailure? Failure, int Stratum, DateTimeOffset Received, DateTimeOffset Transmitted)
{
    public static SntpReply Rejected(SntpFailure failure) => new(failure, 0, default, default);
}

/// <summary>Why a time server gave no usable measurement.</summary>
public enum SntpFailure
{
    /// <summary>The configured entry is not a host name or address this agent may query.</summary>
    InvalidServer,

    /// <summary>The name did not resolve to an address.</summary>
    Unresolved,

    /// <summary>No reply arrived within the query timeout.</summary>
    Timeout,

    /// <summary>The network refused or could not route the query.</summary>
    Unreachable,

    /// <summary>A reply arrived but failed validation: wrong mode, version or originate timestamp, or zero times.</summary>
    InvalidReply,

    /// <summary>The server sent a kiss-o'-death reply and declines to serve this client.</summary>
    Refused,

    /// <summary>The server says its own clock is not synchronized, or is too far from its reference clock.</summary>
    Unsynchronized,
}
