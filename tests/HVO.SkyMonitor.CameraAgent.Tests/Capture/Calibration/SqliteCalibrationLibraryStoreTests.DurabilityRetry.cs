using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

public sealed partial class SqliteCalibrationLibraryStoreTests
{
    [TestMethod]
    public async Task QuarantineRetryCannotCompleteBeforeBothParentsAreSynchronized()
        => await VerifyQuarantineDurabilityRetryAsync(kernelFault: false).ConfigureAwait(false);

    internal static async Task VerifyQuarantineDurabilityRetryAsync(bool kernelFault)
    {
        var control = kernelFault ? DurableCaptureProcessingTests.RequireKernelRetryControl() : null;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var source = Path.Combine(root, "calibration", "synthetic", new string('A', 64));
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "bias.bin"), new byte[] { 1, 2, 3, 4 }).ConfigureAwait(false);
            using var store = new SqliteCalibrationLibraryStore(new InitializedIngress(), CreateOptions(root), TimeProvider.System);
            var reconciler = new CalibrationLibraryReconciler(store, CreateOptions(root));
            var attempts = 0;
            var fail = true;
            reconciler.BeforeQuarantineDirectorySyncForTest = directory =>
            {
                attempts++;
                if (!fail) return;
                if (control is null) throw new IOException("Injected quarantine parent fsync EIO.", 5);
                if (attempts == 1) DurableCaptureProcessingTests.ArmKernelRetryFault(control, directory);
            };
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, result.Failed, "Failed parent synchronization cannot become a completed quarantine on retry.");
                Assert.HasCount(1, await store.ReadPlannedReconciliationsAsync(CancellationToken.None).ConfigureAwait(false));
                Assert.IsFalse(Directory.Exists(source));
            }
            Assert.AreEqual(2, attempts);
            if (control is not null) DurableCaptureProcessingTests.WaitForKernelController(control + ".released");
            fail = false;
            var settled = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, settled.Failed);
            Assert.IsEmpty(await store.ReadPlannedReconciliationsAsync(CancellationToken.None).ConfigureAwait(false));
            var retained = Directory.GetFiles(Path.Combine(root, "quarantine"), "bias.bin", SearchOption.AllDirectories);
            Assert.HasCount(1, retained);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(retained[0]).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }
}

[TestClass]
[TestCategory("Manual")]
public sealed class CalibrationQuarantineKernelFaultTests
{
    [TestMethod]
    public async Task QuarantineRetryWithRealDirectoryFsyncFailure()
        => await SqliteCalibrationLibraryStoreTests.VerifyQuarantineDurabilityRetryAsync(kernelFault: true).ConfigureAwait(false);
}
