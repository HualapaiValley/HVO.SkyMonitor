using System.Buffers.Binary;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class SntpPacketTests
{
    private const ulong Nonce = 0x0123_4567_89AB_CDEF;
    private static readonly DateTimeOffset ServerReceived = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ServerTransmitted = ServerReceived.AddMilliseconds(3);

    [TestMethod]
    public void CreateRequest_IsAVersion4ClientPacketCarryingTheNonceAsItsTransmitTime()
    {
        var request = SntpPacket.CreateRequest(Nonce);

        Assert.HasCount(SntpPacket.Length, request);
        Assert.AreEqual(0x23, request[0], "LI 0, version 4, mode 3 (client).");
        Assert.AreEqual(Nonce, BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(40)));
        Assert.IsTrue(request.AsSpan(1, 39).IndexOfAnyExcept((byte)0) < 0, "Nothing but the header and nonce is sent.");
    }

    [TestMethod]
    [DataRow(4, DisplayName = "Version 4")]
    [DataRow(3, DisplayName = "Version 3")]
    public void Parse_AValidReply_ReturnsTheServerTimesAndStratum(int version)
    {
        var reply = SntpPacket.Parse(Reply(version: version, stratum: 2), Nonce);

        Assert.IsNull(reply.Failure);
        Assert.AreEqual(2, reply.Stratum);
        Assert.AreEqual(ServerReceived, reply.Received);
        AssertClose(ServerTransmitted, reply.Transmitted);
    }

    [TestMethod]
    [DataRow("short", DisplayName = "Shorter than a packet")]
    [DataRow("client-mode", DisplayName = "Client mode instead of server")]
    [DataRow("broadcast-mode", DisplayName = "Broadcast mode")]
    [DataRow("version-2", DisplayName = "Version 2")]
    [DataRow("version-5", DisplayName = "Version 5")]
    [DataRow("wrong-nonce", DisplayName = "Originate timestamp is not the request's")]
    [DataRow("zero-receive", DisplayName = "Zero receive time")]
    [DataRow("zero-transmit", DisplayName = "Zero transmit time")]
    public void Parse_AMalformedOrUnmatchedReply_IsInvalid(string scenario)
    {
        byte[] packet = scenario switch
        {
            "short" => Reply()[..47],
            "client-mode" => Reply(mode: 3),
            "broadcast-mode" => Reply(mode: 5),
            "version-2" => Reply(version: 2),
            "version-5" => Reply(version: 5),
            "wrong-nonce" => Reply(originate: Nonce + 1),
            "zero-receive" => Reply(received: 0),
            "zero-transmit" => Reply(transmitted: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var reply = SntpPacket.Parse(packet, Nonce);

        Assert.AreEqual(SntpFailure.InvalidReply, reply.Failure);
    }

    [TestMethod]
    public void Parse_AKissOfDeath_IsARefusalNotATime()
        => Assert.AreEqual(SntpFailure.Refused, SntpPacket.Parse(Reply(stratum: 0), Nonce).Failure);

    [TestMethod]
    [DataRow(3, 2, 0d, 0d, DisplayName = "Leap indicator alarm")]
    [DataRow(0, 16, 0d, 0d, DisplayName = "Stratum 16")]
    [DataRow(0, 2, 2d, 0.6d, DisplayName = "Root distance 1.6 s")]
    [DataRow(0, 2, 0d, 1.6d, DisplayName = "Root dispersion 1.6 s")]
    [DataRow(0, 2, -0.5d, 0d, DisplayName = "Root delay with its high bit set")]
    [DataRow(0, 2, 0d, -32768d, DisplayName = "Root dispersion with its high bit set")]
    public void Parse_AReplyFromAServerThatIsNotSynchronized_IsRejected(
        int leap, int stratum, double rootDelaySeconds, double rootDispersionSeconds)
    {
        var reply = SntpPacket.Parse(
            Reply(leap: leap, stratum: stratum, rootDelay: rootDelaySeconds, rootDispersion: rootDispersionSeconds),
            Nonce);

        Assert.AreEqual(SntpFailure.Unsynchronized, reply.Failure);
    }

    [TestMethod]
    public void Parse_ARootDistanceAtTheLimit_IsAccepted()
        => Assert.IsNull(SntpPacket.Parse(Reply(rootDelay: 1, rootDispersion: 1), Nonce).Failure);

    [TestMethod]
    [DataRow("1900-01-01T00:00:00Z", 0x0000_0000_0000_0000UL)]
    [DataRow("1970-01-01T00:00:00Z", 0x83AA_7E80_0000_0000UL)]
    [DataRow("2036-02-07T06:28:15Z", 0xFFFF_FFFF_0000_0000UL)]
    [DataRow("2036-02-07T06:28:16Z", 0x0000_0000_0000_0000UL)]
    [DataRow("2036-02-07T06:28:17.5Z", 0x0000_0001_8000_0000UL)]
    public void ToTimestamp_EncodesEachEraFromItsOwnStart(string utc, ulong expected)
        => Assert.AreEqual(expected, SntpPacket.ToTimestamp(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

    [TestMethod]
    public void ToDateTimeOffset_ReadsATimestampWithoutItsHighBitAsTheNextEra()
    {
        // RFC 4330 section 3: a clear most significant bit means the era that starts in 2036, so 2026 reads in era 0.
        Assert.AreEqual(new DateTimeOffset(2036, 2, 7, 6, 28, 17, TimeSpan.Zero), SntpPacket.ToDateTimeOffset(0x0000_0001_0000_0000UL));
        Assert.AreEqual(new DateTimeOffset(1968, 1, 20, 3, 14, 8, TimeSpan.Zero), SntpPacket.ToDateTimeOffset(0x8000_0000_0000_0000UL));
    }

    [TestMethod]
    [DataRow("2026-09-30T12:00:00.1234567Z")]
    [DataRow("2035-12-31T23:59:59.9999999Z")]
    [DataRow("2040-06-01T00:00:00.5Z")]
    public void Timestamps_RoundTripWithinTheirResolution(string utc)
    {
        var value = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);

        AssertClose(value, SntpPacket.ToDateTimeOffset(SntpPacket.ToTimestamp(value)));
    }

    internal static byte[] Reply(
        int leap = 0,
        int version = 4,
        int mode = 4,
        int stratum = 2,
        double rootDelay = 0.01,
        double rootDispersion = 0.02,
        ulong originate = Nonce,
        ulong? received = null,
        ulong? transmitted = null)
    {
        var packet = new byte[SntpPacket.Length];
        packet[0] = (byte)((leap << 6) | (version << 3) | mode);
        packet[1] = (byte)stratum;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), (int)Math.Round(rootDelay * 65536));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), (int)Math.Round(rootDispersion * 65536));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24), originate);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(32), received ?? SntpPacket.ToTimestamp(ServerReceived));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(40), transmitted ?? SntpPacket.ToTimestamp(ServerTransmitted));
        return packet;
    }

    // A 32-bit NTP fraction resolves about 233 ps, coarser than nothing but finer than one 100 ns tick.
    private static void AssertClose(DateTimeOffset expected, DateTimeOffset actual)
        => Assert.IsLessThanOrEqualTo(1L, Math.Abs((expected - actual).Ticks), $"{expected:O} vs {actual:O}");
}
