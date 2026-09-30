using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class TimeSyncSettingsTests
{
    [TestMethod]
    public void Read_WithNoSection_IsTheDefault()
    {
        var settings = TimeSyncSettings.Read(Configuration());

        Assert.IsTrue(settings.Enabled);
        Assert.IsEmpty(settings.Servers);
        CollectionAssert.AreEqual(TimeSyncSettings.DefaultServers.ToArray(), settings.EffectiveServers.ToArray());
        Assert.AreEqual(TimeSyncSettings.DefaultInterval, settings.Interval);
        Assert.AreEqual(TimeSyncSettings.DefaultTolerance, settings.Tolerance);
        Assert.AreEqual(TimeSyncSettings.DefaultQueryTimeout, settings.QueryTimeout);
        Assert.AreEqual(TimeSyncSettings.Default with { Servers = settings.Servers }, settings);
        Assert.IsEmpty(TimeSyncSettings.Default.Servers);
    }

    [TestMethod]
    public void Read_ClampsOutOfRangeDurationsInsteadOfRejectingThem()
    {
        var low = TimeSyncSettings.Read(Configuration(
            ("Interval", "00:00:01"), ("Tolerance", "00:00:00.001"), ("QueryTimeout", "00:00:00.010")));
        var high = TimeSyncSettings.Read(Configuration(
            ("Interval", "7.00:00:00"), ("Tolerance", "00:10:00"), ("QueryTimeout", "00:05:00")));

        Assert.AreEqual(TimeSyncSettings.MinimumInterval, low.Interval);
        Assert.AreEqual(TimeSyncSettings.MinimumTolerance, low.Tolerance);
        Assert.AreEqual(TimeSyncSettings.MinimumQueryTimeout, low.QueryTimeout);
        Assert.AreEqual(TimeSyncSettings.MaximumInterval, high.Interval);
        Assert.AreEqual(TimeSyncSettings.MaximumTolerance, high.Tolerance);
        Assert.AreEqual(TimeSyncSettings.MaximumQueryTimeout, high.QueryTimeout);
    }

    [TestMethod]
    public void Read_AnUnparseableValue_FallsBackToTheDefault()
    {
        var settings = TimeSyncSettings.Read(Configuration(
            ("Enabled", "sometimes"), ("Interval", "hourly"), ("Tolerance", "tight")));

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(TimeSyncSettings.DefaultInterval, settings.Interval);
        Assert.AreEqual(TimeSyncSettings.DefaultTolerance, settings.Tolerance);
    }

    [TestMethod]
    public void Read_CanTurnCheckingOff()
        => Assert.IsFalse(TimeSyncSettings.Read(Configuration(("Enabled", "false"))).Enabled);

    [TestMethod]
    public void Read_ServersAsOneCommaSeparatedValue_SplitsThem()
    {
        var settings = TimeSyncSettings.Read(Configuration(("Servers", " time.example.org, 192.168.1.10:123\n[::1] ")));

        string[] expected = ["time.example.org", "192.168.1.10:123", "[::1]"];
        CollectionAssert.AreEqual(expected, settings.Servers.ToArray());
        CollectionAssert.AreEqual(expected, settings.EffectiveServers.ToArray());
    }

    [TestMethod]
    public void Read_ServersAsAJsonArray_ReadsTheSameList()
    {
        var settings = TimeSyncSettings.Read(Configuration(
            ("Servers:0", "time.example.org"), ("Servers:1", " "), ("Servers:2", "192.168.1.10")));

        string[] expected = ["time.example.org", "192.168.1.10"];
        CollectionAssert.AreEqual(expected, settings.Servers.ToArray());
    }

    [TestMethod]
    [DataRow("pool.ntp.org", "pool.ntp.org", 123)]
    [DataRow("time-a.example.org:1123", "time-a.example.org", 1123)]
    [DataRow("ntp", "ntp", 123)]
    [DataRow("192.168.1.10", "192.168.1.10", 123)]
    [DataRow("192.168.1.10:65535", "192.168.1.10", 65535)]
    [DataRow("[::1]", "::1", 123)]
    [DataRow("[2001:db8::1]:1", "2001:db8::1", 1)]
    public void TryParseServer_AcceptsHostNamesAndAddresses(string entry, string host, int port)
    {
        Assert.IsTrue(TimeSyncSettings.TryParseServer(entry, out var parsedHost, out var parsedPort));
        Assert.AreEqual(host, parsedHost);
        Assert.AreEqual(port, parsedPort);
    }

    [TestMethod]
    [DataRow("", DisplayName = "Empty")]
    [DataRow(" pool.ntp.org", DisplayName = "Leading space")]
    [DataRow("ntp://pool.ntp.org", DisplayName = "URL")]
    [DataRow("user:secret@pool.ntp.org", DisplayName = "Credentials")]
    [DataRow("pool.ntp.org/time", DisplayName = "Path")]
    [DataRow("pool.ntp.org:", DisplayName = "Empty port")]
    [DataRow("pool.ntp.org:0", DisplayName = "Port 0")]
    [DataRow("pool.ntp.org:65536", DisplayName = "Port beyond 65535")]
    [DataRow("pool.ntp.org:+123", DisplayName = "Signed port")]
    [DataRow("-pool.ntp.org", DisplayName = "Label starting with a hyphen")]
    [DataRow("pool..ntp.org", DisplayName = "Empty label")]
    [DataRow("192.168.1", DisplayName = "Three-part address")]
    [DataRow("300.1.1.1", DisplayName = "Octet beyond 255")]
    [DataRow("10.0.0.123.4", DisplayName = "Numeric last label")]
    [DataRow("::1", DisplayName = "Unbracketed IPv6")]
    [DataRow("[::1", DisplayName = "Unclosed bracket")]
    [DataRow("[192.168.1.10]", DisplayName = "Bracketed IPv4")]
    [DataRow("[::1]123", DisplayName = "Port without colon")]
    public void TryParseServer_RejectsAnythingButAHostAndPort(string entry)
        => Assert.IsFalse(TimeSyncSettings.TryParseServer(entry, out _, out _));

    [TestMethod]
    public void TryParseServer_RejectsNamesLongerThanDnsAllows()
    {
        var label = new string('a', 63);
        var longest = string.Join('.', label, label, label, new string('b', 61));
        var tooLong = string.Join('.', label, label, label, new string('b', 62));

        Assert.AreEqual(253, longest.Length);
        Assert.IsTrue(TimeSyncSettings.TryParseServer(longest, out _, out _));
        Assert.IsTrue(TimeSyncSettings.TryParseServer(longest + ":65535", out _, out _));
        Assert.IsFalse(TimeSyncSettings.TryParseServer(tooLong, out _, out _));
        Assert.IsFalse(TimeSyncSettings.TryParseServer(new string('a', 64) + ".example", out _, out _));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(static value => new KeyValuePair<string, string?>(
                $"{TimeSyncSettings.SectionKey}:{value.Key}", value.Value)))
            .Build();
}
