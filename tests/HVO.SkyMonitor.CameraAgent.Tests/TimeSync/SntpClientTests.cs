using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class SntpClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Measure_OverASymmetricPath_ReturnsTheServerClockOffsetAndTheNetworkDelay()
    {
        // The server's clock is 5 s ahead, each direction takes 10 ms, and the server holds the request for 1 ms.
        var (offset, roundTrip) = SntpClient.Measure(
            Now,
            Now.AddSeconds(5).AddMilliseconds(10),
            Now.AddSeconds(5).AddMilliseconds(11),
            Now.AddMilliseconds(21));

        Assert.AreEqual(TimeSpan.FromSeconds(5), offset);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), roundTrip);
    }

    [TestMethod]
    public void Measure_WhenTheServerClockIsBehind_ReturnsANegativeOffset()
    {
        var (offset, _) = SntpClient.Measure(
            Now, Now.AddMilliseconds(-240), Now.AddMilliseconds(-240), Now.AddMilliseconds(20));

        Assert.AreEqual(TimeSpan.FromMilliseconds(-250), offset);
    }

    [TestMethod]
    public void Measure_ANegativeDelayFromRounding_IsReportedAsZero()
    {
        var (_, roundTrip) = SntpClient.Measure(Now, Now, Now.AddMilliseconds(2), Now.AddMilliseconds(1));

        Assert.AreEqual(TimeSpan.Zero, roundTrip);
    }

    [TestMethod]
    [DataRow("ntp://pool.ntp.org")]
    [DataRow("user@pool.ntp.org")]
    [DataRow("")]
    public async Task QueryAsync_AnEntryThatIsNotAServer_FailsWithoutAnyNetworkUseAsync(string server)
    {
        var client = new SntpClient(new FixedTimeProvider(Now));

        var result = await client.QueryAsync(server, Timeout, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TimeServerResult.Failed(server, SntpFailure.InvalidServer), result);
    }

    [TestMethod]
    public async Task QueryAsync_AgainstAServerFiveSecondsAhead_MeasuresItsOffsetAsync()
    {
        using var server = new FakeTimeServer(nonce => SntpPacketTests.Reply(
            stratum: 1,
            originate: nonce,
            received: SntpPacket.ToTimestamp(Now.AddSeconds(5)),
            transmitted: SntpPacket.ToTimestamp(Now.AddSeconds(5))));
        var client = new SntpClient(new FixedTimeProvider(Now));

        var result = await client.QueryAsync(server.Address, Timeout, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.AreEqual(1, result.Stratum);
        // The receive time is the send time plus the monotonic round trip, so the offset is 5 s less half of it.
        Assert.IsGreaterThan(TimeSpan.Zero, result.RoundTrip!.Value);
        Assert.IsLessThan(Timeout, result.RoundTrip.Value);
        var expected = TimeSpan.FromSeconds(5) - (result.RoundTrip.Value / 2);
        Assert.IsLessThanOrEqualTo(1L, Math.Abs((expected - result.Offset!.Value).Ticks));
        Assert.AreEqual(1, server.Requests);
    }

    [TestMethod]
    [DataRow("client-mode", SntpFailure.InvalidReply, DisplayName = "Echoed request")]
    [DataRow("wrong-nonce", SntpFailure.InvalidReply, DisplayName = "Reply to another request")]
    [DataRow("kiss-of-death", SntpFailure.Refused, DisplayName = "Kiss-o'-death")]
    [DataRow("unsynchronized", SntpFailure.Unsynchronized, DisplayName = "Leap alarm")]
    public async Task QueryAsync_ABadReply_IsAFailureNeverAZeroOffsetAsync(string scenario, SntpFailure expected)
    {
        using var server = new FakeTimeServer(nonce => scenario switch
        {
            "client-mode" => SntpPacketTests.Reply(mode: 3, originate: nonce),
            "wrong-nonce" => SntpPacketTests.Reply(originate: nonce ^ 1),
            "kiss-of-death" => SntpPacketTests.Reply(stratum: 0, originate: nonce),
            _ => SntpPacketTests.Reply(leap: 3, originate: nonce),
        });
        var client = new SntpClient(new FixedTimeProvider(Now));

        var result = await client.QueryAsync(server.Address, Timeout, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TimeServerResult.Failed(server.Address, expected), result);
    }

    [TestMethod]
    public async Task QueryAsync_WhenTheServerNeverAnswers_TimesOutWithinTheBoundAsync()
    {
        using var server = new FakeTimeServer(static _ => null);
        var client = new SntpClient(TimeProvider.System);
        var started = TimeProvider.System.GetTimestamp();

        var result = await client.QueryAsync(server.Address, TimeSpan.FromMilliseconds(250), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(TimeServerResult.Failed(server.Address, SntpFailure.Timeout), result);
        Assert.IsLessThan(Timeout, TimeProvider.System.GetElapsedTime(started));
        Assert.AreEqual(1, server.Requests);
    }

    [TestMethod]
    public async Task QueryAsync_ToAClosedPort_IsUnreachableAsync()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }
        var address = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}");
        var client = new SntpClient(TimeProvider.System);

        var result = await client.QueryAsync(address, Timeout, CancellationToken.None).ConfigureAwait(false);

        // Loopback answers a closed UDP port with ICMP port unreachable, which a connected socket reports as refused.
        Assert.AreEqual(TimeServerResult.Failed(address, SntpFailure.Unreachable), result);
    }

    [TestMethod]
    public async Task QueryAsync_WhenTheCallerCancels_ThrowsRatherThanReportingATimeoutAsync()
    {
        using var server = new FakeTimeServer(static _ => null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = new SntpClient(TimeProvider.System);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.QueryAsync(server.Address, Timeout, cancellation.Token)).ConfigureAwait(false);
    }

    /// <summary>A UDP time server on loopback that answers each request with the packet its handler builds.</summary>
    private sealed class FakeTimeServer : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        internal FakeTimeServer(Func<ulong, byte[]?> reply)
        {
            Address = string.Create(
                CultureInfo.InvariantCulture, $"127.0.0.1:{((IPEndPoint)_socket.Client.LocalEndPoint!).Port}");
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    UdpReceiveResult request;
                    try
                    {
                        request = await _socket.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    Interlocked.Increment(ref _requests);
                    var nonce = BinaryPrimitives.ReadUInt64BigEndian(request.Buffer.AsSpan(40));
                    if (reply(nonce) is { } packet)
                    {
                        await _socket.SendAsync(packet, request.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
                    }
                }
            });
        }

        internal string Address { get; }

        internal int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _stop.Cancel();
            _loop.Wait(TimeSpan.FromSeconds(5));
            _socket.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
