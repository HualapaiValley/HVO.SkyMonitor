using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.DataProtection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Operations;

[TestClass]
[TestCategory("Unit")]
public sealed class CalibrationOperationsTokenServiceTests
{
    [TestMethod]
    public void IdempotencyKeyValidation_RejectsUnsafeValues()
    {
        Assert.IsFalse(CameraAgentCalibrationOperationsEndpoints.IsValidIdempotencyKey(string.Empty));
        Assert.IsFalse(CameraAgentCalibrationOperationsEndpoints.IsValidIdempotencyKey("   "));
        Assert.IsFalse(CameraAgentCalibrationOperationsEndpoints.IsValidIdempotencyKey(new string('a', 129)));
        Assert.IsFalse(CameraAgentCalibrationOperationsEndpoints.IsValidIdempotencyKey("unsafe\nkey"));
        Assert.IsTrue(CameraAgentCalibrationOperationsEndpoints.IsValidIdempotencyKey(new string('a', 128)));
    }

    [TestMethod]
    public void BundleCursor_IsOpaqueAndRejectsTamper()
    {
        var tokens = new CalibrationOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(1));
        var expected = new CalibrationLibraryBundleCursor(
            new DateTimeOffset(2026, 7, 26, 1, 2, 3, TimeSpan.Zero), "bundle-private-id");

        var token = tokens.Protect(expected);

        Assert.IsTrue(tokens.TryUnprotect(token, out var actual));
        Assert.AreEqual(expected, actual);
        Assert.IsFalse(token.Contains(expected.BundleId, StringComparison.Ordinal));
        Assert.IsFalse(tokens.TryUnprotect(token + "x", out _));
    }

    [TestMethod]
    public async Task BundleCursor_Expires()
    {
        var tokens = new CalibrationOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMilliseconds(1));
        var token = tokens.Protect(new CalibrationLibraryBundleCursor(DateTimeOffset.UnixEpoch, "bundle"));

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.IsFalse(tokens.TryUnprotect(token, out _));
    }
}
