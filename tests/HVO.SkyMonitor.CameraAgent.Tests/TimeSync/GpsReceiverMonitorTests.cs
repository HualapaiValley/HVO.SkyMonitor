using System.Runtime.InteropServices;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "TimeSync")]
public sealed class GpsReceiverMonitorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly GpsReceiverSettings Configured = new(true, "/dev/ttyUSB0", 9600, true);

    [TestMethod]
    public void StandaloneRegistrationExposesReceiverAndStartsWithDeviceAccessOff()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IClockSyncMonitor>();
        Assert.AreEqual(LinuxGpsSerialDevice.Supported, monitor.GpsReaderRegistered);
        Assert.AreEqual(GpsReceiverSettings.Default, monitor.Settings.Gps);
        Assert.IsNull(provider.GetService<GpsReceiverMonitor>()?.Status(monitor.Settings.Gps));
    }

    [TestMethod]
    public async Task SimulatedSerial_QualifiesFreshPpsOfflineAndRevokesOnUnplugReconfigureAndStop()
    {
        var time = new ExposureTestTimeProvider(Epoch);
        var configured = Configured;
        var serial = new SimulatedSerial();
        var opens = new List<GpsReceiverSettings>();
        using var monitor = new GpsReceiverMonitor(() => configured, setting =>
        {
            opens.Add(setting);
            if (opens.Count == 1) throw new IOException("Device missing.");
            return serial;
        }, time);
        await monitor.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            WaitForPoll(time);
            Assert.AreEqual(GpsSignalState.Unavailable, monitor.Status(configured)!.State);
            Advance(time, 1000);
            Assert.AreEqual(GpsSignalState.Missing, monitor.Status(configured)!.State);

            // First poll sampled low. Bracket the next rising edge, then deliver RMC in two serial fragments.
            serial.Carrier = true;
            Advance(time, 10);
            var rmc = GpsTimeReaderTests.Sentence("GNRMC,120001,A,,,,,,,121126,,,A");
            serial.Next = rmc[..12];
            Advance(time, 10);
            serial.Next = rmc[12..] + "\r\n" + GpsTimeReaderTests.Gga() + "\r\n";
            Advance(time, 10);
            var gps = monitor.Status(configured)!;
            Assert.AreEqual(Epoch.AddSeconds(1), gps.Utc);
            Assert.AreEqual(8, gps.Satellites);
            Assert.AreEqual(1, gps.FixQuality);
            Assert.AreEqual(TimeSpan.FromMilliseconds(-30), gps.ArrivalOffset);
            Assert.AreEqual(TimeSpan.FromMilliseconds(-10), gps.PpsOffset);
            Assert.AreEqual(TimeSpan.FromMilliseconds(15), gps.PpsUncertainty);
            Assert.AreEqual(ClockSyncStatus.InTolerance, Assess(gps));
            Assert.AreEqual(ClockSyncStatus.Unsynchronized, Assess(gps, KernelClockStatus.Unsynchronized));
            time.ShiftUtc(TimeSpan.FromHours(1));
            Assert.AreEqual(ClockSyncStatus.Drifting, Assess(monitor.Status(configured)!));

            serial.Unplugged = true;
            Advance(time, 10);
            Assert.IsTrue(serial.Closed);
            Assert.AreEqual(GpsSignalState.Unavailable, monitor.Status(configured)!.State);
            Assert.AreEqual(ClockSyncStatus.HostSynchronized, Assess(monitor.Status(configured)!, KernelClockStatus.Synchronized));

            configured = configured with { Device = "/dev/ttyACM1", UsePps = false };
            serial = new SimulatedSerial { Next = GpsTimeReaderTests.Rmc() + "\n" };
            Advance(time, 1000);
            Assert.AreEqual(GpsSignalState.Available, monitor.Status(configured)!.State);
            Assert.IsFalse(monitor.Status(configured)!.CanVerifyClock);
            Assert.AreEqual(0, serial.CarrierPolls);
            Assert.AreEqual(GpsSignalState.Unavailable, monitor.Status(Configured)!.State);
            CollectionAssert.AreEqual(new[] { Configured, Configured, configured }, opens);
            configured = GpsReceiverSettings.Default;
            Advance(time, 1000);
            Assert.IsTrue(serial.Closed);
            Assert.IsNull(monitor.Status(configured));
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    [DataRow("wide-edge")]
    [DataRow("late-sentence")]
    [DataRow("partial-before-edge")]
    [DataRow("fractional-second")]
    [DataRow("invalid-fix")]
    [DataRow("rollover")]
    [DataRow("estimated-quality")]
    [DataRow("lost-carrier")]
    [DataRow("expired")]
    public void AmbiguousOrInvalidPpsCannotVerifyOffline(string scenario)
    {
        var time = new ExposureTestTimeProvider(Epoch);
        using var reader = new GpsTimeReader(time, GpsReceiverMonitor.ReceiverEra);
        var rmc = GpsTimeReaderTests.Sentence(scenario switch
        {
            "fractional-second" => "GNRMC,120000.2,A,,,,,,,121126,,,A",
            "invalid-fix" => "GNRMC,120000,V,,,,,,,121126,,,N",
            "rollover" => "GNRMC,120000,A,,,,,,,290307,,,A",
            _ => "GNRMC,120000,A,,,,,,,121126,,,A",
        }) + "\n";
        if (scenario == "partial-before-edge") reader.ConsumeSerialBytes("$"u8);
        var low = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(scenario == "wide-edge" ? 101 : 10));
        reader.ObserveRisingEdge(low, time.GetTimestamp());
        if (scenario == "late-sentence") time.Advance(TimeSpan.FromMilliseconds(800));
        reader.ConsumeSerialBytes(Encoding.ASCII.GetBytes(scenario == "partial-before-edge" ? rmc[1..] : rmc));
        if (scenario == "estimated-quality")
            reader.ConsumeSerialBytes(Encoding.ASCII.GetBytes(GpsTimeReaderTests.Sentence("GPGGA,120000,,,,,6,08,0.9") + "\n"));
        if (scenario == "lost-carrier") reader.LosePps();
        if (scenario == "expired") time.Advance(GpsTimeReader.StaleAfter);
        var gps = reader.GetStatus();
        Assert.IsFalse(gps.CanVerifyClock);
        Assert.IsNull(gps.PpsOffset);
        Assert.AreEqual(ClockSyncStatus.Unverified, Assess(gps));
    }

    [TestMethod]
    public void UncertaintyStraddlingToleranceDoesNotVerifyClock()
    {
        var time = new ExposureTestTimeProvider(Epoch.AddMilliseconds(450));
        using var reader = new GpsTimeReader(time, GpsReceiverMonitor.ReceiverEra);
        var low = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(50));
        reader.ObserveRisingEdge(low, time.GetTimestamp());
        reader.ConsumeSerialBytes(Encoding.ASCII.GetBytes(GpsTimeReaderTests.Rmc() + "\n"));
        Assert.AreEqual(ClockSyncStatus.Unverified, Assess(reader.GetStatus()));
    }

    [TestMethod]
    public void LinuxSerialReadsOnlyConfiguredPseudoTerminalAndDetectsDisconnect()
    {
        if (!LinuxGpsSerialDevice.Supported) Assert.Inconclusive("Linux tty ABI test.");
        var name = new byte[128];
        Assert.AreEqual(0, OpenPty(out var masterFd, out var slaveFd, name, IntPtr.Zero, IntPtr.Zero));
        using var master = new SafeFileHandle(masterFd, ownsHandle: true);
        using var slave = new SafeFileHandle(slaveFd, ownsHandle: true);
        var path = Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0));
        using var device = new LinuxGpsSerialDevice(Configured with { Device = path, BaudRate = 38400 });
        var bytes = new byte[128];
        Assert.AreEqual(0, device.Read(bytes));
        using var writer = new FileStream(master, FileAccess.Write);
        var sentence = Encoding.ASCII.GetBytes(GpsTimeReaderTests.Rmc() + "\r\n");
        writer.Write(sentence);
        writer.Flush();
        var count = device.Read(bytes);
        CollectionAssert.AreEqual(sentence, bytes[..count]);
        Assert.IsNull(device.CarrierDetect(), "A pseudo-terminal supplies no carrier signal.");
        writer.Dispose();
        Assert.ThrowsExactly<IOException>(() => device.Read(bytes));
    }

#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "openpty")]
    private static extern int OpenPty(out int master, out int slave, [Out] byte[] name, IntPtr attributes, IntPtr size);
#pragma warning restore SYSLIB1054

    private static ClockSyncStatus Assess(GpsTimeStatus gps, KernelClockStatus kernel = KernelClockStatus.Unknown) =>
        ClockAssessment.Evaluate(TimeSyncSettings.Default with { Gps = Configured },
            new ClockSyncSnapshot(Epoch, true, null, [], 0, new(kernel, null, null)) { Gps = gps }).Status;

    private static void Advance(ExposureTestTimeProvider time, int milliseconds)
    {
        time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        WaitForPoll(time);
    }

    private static void WaitForPoll(ExposureTestTimeProvider time) =>
        Assert.IsTrue(SpinWait.SpinUntil(() => time.ActiveTimers == 1, TimeSpan.FromSeconds(5)), "Receiver did not finish its poll.");

    private sealed class SimulatedSerial : IGpsSerialDevice
    {
        internal string Next { get; set; } = string.Empty;
        internal bool Carrier { get; set; }
        internal int CarrierPolls { get; private set; }
        internal bool Unplugged { get; set; }
        internal bool Closed { get; private set; }

        public int Read(byte[] buffer)
        {
            if (Unplugged) throw new IOException("Simulated disconnect.");
            var count = Encoding.ASCII.GetBytes(Next, buffer);
            Next = string.Empty;
            return count;
        }

        public bool? CarrierDetect()
        {
            CarrierPolls++;
            return Carrier;
        }

        public void Dispose() => Closed = true;
    }
}
