using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using Microsoft.Extensions.Configuration;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "TimeSync")]
public sealed class GpsTimeUiTests
{
    [TestMethod]
    public void QualifiedPpsShowsReferenceOffsetAndUncertaintyEvenWhenSettingsAreOverridden()
    {
        using var context = new BunitContext();
        var service = TimeSyncPanelTests.Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View() with
        {
            Selected = null,
            Receiver = new(true, "/dev/ttyUSB0", 9600, true),
            GpsOverridden = true,
            Gps = GpsTimeStatus.Empty(GpsSignalState.Available) with
            {
                PpsState = GpsSignalState.Available,
                PpsOffset = TimeSpan.FromMilliseconds(10),
                PpsUncertainty = TimeSpan.FromMilliseconds(15),
                PpsMeasuredUtc = OperatorUiTestData.Now,
            },
        });

        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("GPS/PPS", cut.Find("#time-server").TextContent);
            Assert.AreEqual("10 ms behind", cut.Find("#time-offset").TextContent);
            Assert.Contains("10 ms behind ±15 ms", cut.Find("#time-gps-heading + dl").TextContent, StringComparison.Ordinal);
            Assert.IsTrue(cut.Find("fieldset.time-gps").HasAttribute("disabled"));
            Assert.Contains("Another configuration source", cut.Find("#time-gps-unavailable").TextContent, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public async Task InvalidPathIsRejectedLocallyAndCorrectingItClearsTheErrorWhileSaving()
    {
        using var context = new BunitContext();
        var service = TimeSyncPanelTests.Configure(context);
        var receiver = new GpsReceiverSettings(true, "/dev/ttyACM0", 9600, false);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View() with { Receiver = receiver });
        var completion = new TaskCompletionSource<OperatorUiResult<GpsReceiverSettings>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SaveGpsHandler = _ => new(completion.Task);

        var cut = context.Render<TimeSyncPanel>();
        await cut.Find("#time-gps-device").ChangeAsync(new() { Value = "/dev/watchdog" }).ConfigureAwait(false);
        await cut.Find("#time-gps-save").ClickAsync().ConfigureAwait(false);
        Assert.Contains("serial device", cut.Find(".time-message[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.IsEmpty(service.GpsSaves);

        await cut.Find("#time-gps-device").ChangeAsync(new() { Value = receiver.Device }).ConfigureAwait(false);
        var save = cut.Find("#time-gps-save").ClickAsync();
        try
        {
            cut.WaitForAssertion(() =>
            {
                Assert.IsEmpty(cut.FindAll(".time-message"));
                Assert.AreEqual("Saving…", cut.Find("#time-gps-save").TextContent);
                Assert.IsTrue(cut.Find("fieldset.time-gps").HasAttribute("disabled"));
                Assert.AreEqual((receiver, "version-1"), service.GpsSaves.Single());
            });
        }
        finally
        {
            completion.SetResult(OperatorUiResult<GpsReceiverSettings>.Success(receiver));
            await save.ConfigureAwait(false);
        }
        Assert.AreEqual("GPS receiver settings saved.", cut.Find(".time-message").TextContent.Trim());
        Assert.AreEqual("Save receiver", cut.Find("#time-gps-save").TextContent);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisablingGpsHidesEarlierEvidenceAndRetainsTheNetworkRoundTime(bool registered)
    {
        var snapshot = new ClockSyncSnapshot(OperatorUiTestData.Now, true, null, [], 0, KernelClockState.Unknown)
        {
            Gps = GpsTimeStatus.Empty(GpsSignalState.Available) with
            {
                PpsOffset = TimeSpan.Zero,
                PpsUncertainty = TimeSpan.FromMilliseconds(15),
                PpsMeasuredUtc = OperatorUiTestData.Now.AddSeconds(-1),
            },
        };
        var monitor = Mock.Of<IClockSyncMonitor>(m => m.GpsReaderRegistered == registered &&
            m.Settings == TimeSyncSettings.Default && m.Latest == snapshot);
        var service = CameraAgentTimeSyncUiServiceTests.CreateService(monitor, read: true, mutate: true);
        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(registered ? GpsReceiverSettings.Default : null, view.Receiver);
        Assert.IsNull(view.Gps);
        Assert.AreEqual(OperatorUiTestData.Now, view.MeasuredUtc);
        Assert.AreEqual(ClockSyncStatus.Unverified, view.Status);
    }

    [TestMethod]
    [DataRow("Enabled", "sometimes")]
    [DataRow("UsePps", "occasionally")]
    [DataRow("BaudRate", "fast")]
    public void MalformedGpsOptionsRemainVisibleAndPreventDeviceAccess(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[GpsReceiverSettings.SectionKey + ":Device"] = "/dev/ttyUSB0";
        config[GpsReceiverSettings.SectionKey + ":" + key] = value;
        var gps = TimeSyncSettings.Read(config).Gps;
        Assert.IsTrue(gps.Enabled, "The Time panel must show the misconfigured receiver.");
        var problem = gps.Validate();
        Assert.IsNotNull(problem, "The monitor must refuse to open the device.");

        using var context = new BunitContext();
        var service = TimeSyncPanelTests.Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View() with { Receiver = gps });
        var panel = context.Render<TimeSyncPanel>();
        Assert.AreEqual(problem, panel.Find("#time-gps-fix").TextContent);
    }

    private static ValueTask<OperatorUiResult<TimeSyncView>> Success(TimeSyncView view)
        => ValueTask.FromResult(OperatorUiResult<TimeSyncView>.Success(view));
}
