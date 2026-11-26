using System.Runtime.InteropServices;
using System.Text;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Moq;

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
    [DataRow(KernelClockStatus.Unknown, ClockSyncStatus.Unverified, HealthStatus.Degraded)]
    [DataRow(KernelClockStatus.Synchronized, ClockSyncStatus.HostSynchronized, HealthStatus.Healthy)]
    public async Task OfflineReceiverSuppliesClockChecksAndOperatorViewsUntilUnplugged(
        KernelClockStatus kernelStatus, ClockSyncStatus fallback, HealthStatus fallbackHealth)
    {
        var time = new ExposureTestTimeProvider(Epoch);
        var serial = new SimulatedSerial();
        using var receiver = new GpsReceiverMonitor(() => Configured, _ => serial, time);
        var settings = TimeSyncSettings.Default with { Servers = ["offline.example"], Gps = Configured };
        var client = new Mock<ISntpClient>(MockBehavior.Strict);
        client.Setup(s => s.QueryAsync("offline.example", settings.QueryTimeout, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TimeServerResult.Failed("offline.example", SntpFailure.Timeout));
        var kernel = Mock.Of<IKernelClockReader>(k => k.Read() == new KernelClockState(kernelStatus, null, null));
        using var clock = new ClockSyncMonitor(() => settings, client.Object, kernel, time, NullLogger<ClockSyncMonitor>.Instance)
        {
            GpsReader = receiver,
        };
        var health = new ClockHealthCheck(clock);
        using var system = new CameraAgentSystemUiServiceTests.Fixture(clockMonitor: clock);
        system.Report();
        var panel = CameraAgentTimeSyncUiServiceTests.CreateService(clock, read: true, mutate: false, timeProvider: time);

        await receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            WaitForPoll(time);
            serial.Carrier = true;
            Advance(time, 10);
            var pulseUtc = time.GetUtcNow();
            serial.Next = GpsTimeReaderTests.Rmc() + "\n";
            Advance(time, 10);

            var check = await clock.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ClockCheckOutcome.Measured, check.Outcome);
            Assert.IsNull(check.Snapshot!.Selected);
            Assert.AreEqual(pulseUtc, check.Snapshot.Gps?.PpsMeasuredUtc);
            Assert.AreNotEqual(pulseUtc, check.Snapshot.MeasuredUtc);
            var gps = clock.Latest!.Gps;
            Assert.IsNotNull(gps);
            Assert.IsTrue(gps.CanVerifyClock);

            var result = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            Assert.AreEqual(HealthStatus.Healthy, result.Status);
            Assert.AreEqual("InTolerance", result.Data["Status"]);

            var overview = (await system.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
            Assert.AreEqual(new SystemClockFact(ClockSyncStatus.InTolerance, TimeSpan.FromMilliseconds(10), pulseUtc, UsesGpsPps: true), overview.Clock);
            var view = (await panel.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
            Assert.AreEqual(Configured, view.Receiver);
            Assert.AreEqual(gps, view.Gps);
            Assert.AreEqual(ClockSyncStatus.InTolerance, view.Status);
            Assert.AreEqual(pulseUtc, view.MeasuredUtc);

            using var context = new BunitContext();
            HealthPageTests.Configure(context, out _, out _);
            context.Services.AddSingleton<ICameraAgentSystemUiService>(system.Service);
            context.Services.AddSingleton<ICameraAgentTimeSyncUiService>(panel);
            context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(pulseUtc.AddMinutes(1)));
            var page = context.Render<HealthPage>();
            var drift = page.Find("#health-clock-drift");
            Assert.AreEqual("+10 ms", drift.TextContent);
            Assert.AreEqual("This agent's clock minus GPS/PPS time, measured 60s ago", drift.GetAttribute("title"));
            var timePanel = context.Render<TimeSyncPanel>();
            Assert.AreEqual("GPS/PPS", timePanel.Find("#time-server").TextContent);
            Assert.AreEqual("10 ms ago", timePanel.Find("#time-measured").TextContent);

            // The 10 ms offset with 15 ms uncertainty straddles the new 10 ms tolerance.
            settings = settings with { Tolerance = TimeSpan.FromMilliseconds(10) };
            var inconclusive = (await system.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
            Assert.AreEqual(fallback, inconclusive.Clock!.Status);
            Assert.IsNull(inconclusive.Clock.Drift);
            await page.Find("#health-refresh").ClickAsync().ConfigureAwait(false);
            Assert.AreEqual(fallback == ClockSyncStatus.HostSynchronized ? "Host synced" : "Unverified",
                page.Find("#health-clock-drift").TextContent);
            var title = page.Find("#health-clock-drift").GetAttribute("title");
            Assert.IsNotNull(title);
            Assert.Contains("No time server answered", title, StringComparison.Ordinal);

            // The last network round stays cached. Its consumers must still see the loss of live PPS.
            serial.Unplugged = true;
            Advance(time, 10);
            Assert.AreEqual(GpsSignalState.Unavailable, clock.Latest!.Gps?.State);
            result = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            Assert.AreEqual(fallbackHealth, result.Status);
            Assert.AreEqual(fallback.ToString(), result.Data["Status"]);
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task RmcAlreadyQueuedAtFirstHighCannotBeAssociatedWithThatPulse()
    {
        var time = new ExposureTestTimeProvider(Epoch);
        var serial = new SimulatedSerial();
        using var receiver = new GpsReceiverMonitor(() => Configured, _ => serial, time);
        await receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            WaitForPoll(time);
            serial.Carrier = true;
            serial.Next = GpsTimeReaderTests.Rmc() + "\n";
            Advance(time, 10);
            var gps = receiver.Status(Configured)!;
            Assert.AreEqual(GpsSignalState.Available, gps.State);
            Assert.IsNull(gps.PpsOffset);
            Assert.IsFalse(gps.CanVerifyClock);
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PpsBracketIncludesBothCarrierReadsAndIsRevokedWhenCarrierBecomesUnreadable()
    {
        var time = new ExposureTestTimeProvider(Epoch);
        var serial = new SimulatedSerial { OnCarrierRead = () => time.Advance(TimeSpan.FromMilliseconds(3)) };
        using var receiver = new GpsReceiverMonitor(() => Configured, _ => serial, time);
        await receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            WaitForPoll(time); // Low sampled at 0..3 ms.
            serial.Carrier = true;
            Advance(time, 10); // High sampled at 13..16 ms.
            serial.Next = GpsTimeReaderTests.Rmc() + "\n";
            Advance(time, 10);
            var gps = receiver.Status(Configured)!;
            Assert.AreEqual(TimeSpan.FromMilliseconds(-16), gps.PpsOffset);
            Assert.AreEqual(TimeSpan.FromMilliseconds(21), gps.PpsUncertainty);

            serial.Carrier = null;
            Advance(time, 10);
            gps = receiver.Status(Configured)!;
            Assert.AreEqual(GpsSignalState.Available, gps.State);
            Assert.AreEqual(GpsSignalState.Missing, gps.PpsState);
            Assert.IsNull(gps.PpsOffset);
            Assert.IsFalse(gps.CanVerifyClock);
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
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
    [DataRow(450)]
    [DataRow(470)]
    public void UncertaintyStraddlingToleranceDoesNotVerifyClockOrReportDrift(int initialOffsetMs)
    {
        var time = new ExposureTestTimeProvider(Epoch.AddMilliseconds(initialOffsetMs));
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

    [TestMethod]
    public async Task DisablingClockChecksClosesTheRegisteredReceiverDevice()
    {
        if (!LinuxGpsSerialDevice.Supported) Assert.Inconclusive("Linux tty ABI test.");
        var name = new byte[128];
        Assert.AreEqual(0, OpenPty(out var masterFd, out var slaveFd, name, IntPtr.Zero, IntPtr.Zero));
        using var master = new SafeFileHandle(masterFd, ownsHandle: true);
        using var slave = new SafeFileHandle(slaveFd, ownsHandle: true);
        var path = Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        configuration[GpsReceiverSettings.SectionKey + ":Enabled"] = "true";
        configuration[GpsReceiverSettings.SectionKey + ":Device"] = path;
        var time = new ExposureTestTimeProvider(Epoch);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();
        services.AddCameraAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        var receiver = provider.GetRequiredService<GpsReceiverMonitor>();
        await receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            WaitForPoll(time);
            // Leave the production reader as the sole owner of the slave descriptor.
            slave.Dispose();
            Assert.IsTrue(SlaveIsOpen());
            configuration[TimeSyncSettings.SectionKey + ":Enabled"] = "false";
            Advance(time, 1000);
            Assert.IsFalse(SlaveIsOpen());
            Advance(time, 1000);
            Assert.IsFalse(SlaveIsOpen());
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        bool SlaveIsOpen() => Directory.EnumerateFiles("/proc/self/fd").Any(fd => new FileInfo(fd).LinkTarget == path);
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
        internal bool? Carrier { get; set; } = false;
        internal Action? OnCarrierRead { get; init; }
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
            OnCarrierRead?.Invoke();
            CarrierPolls++;
            return Carrier;
        }

        public void Dispose() => Closed = true;
    }
}
