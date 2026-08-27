using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Unit")]
public sealed class CalibrationLibraryHealthCheckTests
{
    private static readonly string[] ExpectedDataKeys =
    [
        "Required", "Active", "ActiveState", "StateVersion", "PendingAcquisitionState",
        "LastSelectionReason", "LastSelectionUtc", "BundleCount", "QuarantineCount",
        "LastActivationResult", "LastActivationUtc", "LastReconciliationResult", "LastReconciliationUtc"
    ];

    [TestMethod]
    public async Task CheckHealthAsync_DegradesWhileConfigurationIsPending()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var check = new CalibrationLibraryHealthCheck(store, new CameraAgentConfigurationAccessor());

            var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

            Assert.AreEqual(HealthStatus.Degraded, result.Status);
            CollectionAssert.AreEquivalent(ExpectedDataKeys, result.Data.Keys.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CheckHealthAsync_ReportsRequiredMissingAndOptionalHealthy()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            _ = await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await store.RecordReconciliationResultAsync(
                "calibration.library.reconciled", CancellationToken.None).ConfigureAwait(false);

            var requiredAccessor = new CameraAgentConfigurationAccessor();
            requiredAccessor.SetConfiguration(Configuration(required: true, strategy: "CalibrationLibrary"));
            var required = await new CalibrationLibraryHealthCheck(store, requiredAccessor)
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

            var optionalAccessor = new CameraAgentConfigurationAccessor();
            optionalAccessor.SetConfiguration(Configuration(required: false, strategy: "None"));
            var optional = await new CalibrationLibraryHealthCheck(store, optionalAccessor)
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

            Assert.AreEqual(HealthStatus.Degraded, required.Status);
            Assert.AreEqual(true, required.Data["Required"]);
            Assert.AreEqual("inactive", required.Data["ActiveState"]);
            Assert.AreEqual(HealthStatus.Healthy, optional.Status);
            Assert.AreEqual(false, optional.Data["Required"]);
            CollectionAssert.AreEquivalent(ExpectedDataKeys, optional.Data.Keys.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteCalibrationLibraryStore CreateStore(string root)
        => new(
            new JournalInitializer(root),
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            }),
            TimeProvider.System);

    private static CameraModuleConfig Configuration(bool required, string strategy)
        => new(
            new ObservatoryLocation(35, -114, 1000, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    1,
                    10)),
            new CapturePipelineConfig([new CaptureProcessingStepConfig(
                "Calibration",
                Options: JsonSerializer.SerializeToElement(new CalibrationProcessingStepOptions
                {
                    Strategy = strategy
                }),
                DependsOn: ["$raw"],
                Required: required)]));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-calibration-health", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(
                    Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
