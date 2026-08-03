using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Modules.Zwo;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class AsiNativeApiBoundaryTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public unsafe void CompiledCdeclExportsExerciseEveryNativeBoundaryAndRepeatedFree()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The independent ASI C ABI shim is built on Linux only.");
        }

        var libraryPath = Path.Combine(AppContext.BaseDirectory, "libhvo_asi_test_shim.so");
        Assert.IsTrue(File.Exists(libraryPath), $"Missing compiled ASI C ABI shim at {libraryPath}.");
        var api = AsiNativeApi.Load(libraryPath);

        Assert.AreEqual("1.41-test", api.GetSdkVersion());
        Assert.AreEqual(1, api.GetCameraCount());
        var camera = api.GetCameraProperty(0);
        Assert.AreEqual("ZWO ASI676MC", camera.Model);
        Assert.AreEqual(3552L, camera.MaximumWidth);
        Assert.AreEqual(3552L, camera.MaximumHeight);
        Assert.AreEqual(12, camera.BitDepth);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, api.GetSerialNumber(7));

        api.OpenCamera(7);
        api.InitializeCamera(7);
        var controls = api.GetControlCapabilities(7);
        Assert.HasCount(1, controls);
        Assert.AreEqual(5_000_000_123L, controls[0].Maximum);
        api.SetControlValue(7, AsiControlType.Exposure, 4_500_000_123L, automatic: false);
        Assert.AreEqual(4_500_000_123L, api.GetControlValue(7, AsiControlType.Exposure).Value);

        api.SetRoiFormat(7, 3552, 3552, 1, AsiImageType.Raw16);
        Assert.AreEqual((3552, 3552, 1, AsiImageType.Raw16), api.GetRoiFormat(7));
        api.SetStartPosition(7, 0, 0);
        Assert.AreEqual((0, 0), api.GetStartPosition(7));
        api.StartExposure(7, dark: false);
        Assert.AreEqual(AsiExposureStatus.Success, api.GetExposureStatus(7));
        api.StopExposure(7);

        var expected = Enumerable.Range(0, 4097).Select(index => (byte)(index % 251)).ToArray();
        var actual = new byte[expected.Length];
        fixed (byte* buffer = actual)
        {
            api.GetDataAfterExposure(7, (IntPtr)buffer, actual.LongLength);
        }
        CollectionAssert.AreEqual(expected, actual);
        api.CloseCamera(7);

        api.Dispose();
        api.Dispose();
        using var reloaded = AsiNativeApi.Load(libraryPath);
        Assert.AreEqual("1.41-test", reloaded.GetSdkVersion());
    }
}
