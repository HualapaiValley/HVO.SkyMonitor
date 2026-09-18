using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Issue #823. Retention names its stored days in UTC at every writer, so it must read them
/// back in UTC. These cases pin the process time zone west of UTC, because the continuous
/// integration runners are UTC and a UTC runner cannot observe the difference between reading
/// a directory name as UTC and reading it as local time. Nothing else in the repository
/// exercises a non-UTC host, which is why the defect reached <c>main</c>.
///
/// The pin is process-global, so the class is <see cref="DoNotParallelizeAttribute"/> and
/// restores the ambient zone in cleanup. <see cref="TimeZoneInfo.ClearCachedData"/> is what
/// makes a mid-process <c>TZ</c> change visible; setting the variable alone does nothing.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class RetentionBackgroundServiceTimeZoneTests
{
    // No daylight saving, so the offset is -07:00 on every date and the case cannot drift.
    private const string WestOfUtcZone = "America/Phoenix";

    // retentionDays is 7 below, so with this clock the cutoff is 2026-07-04.
    private static readonly DateTimeOffset EvaluatedUtc = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string CutoffDay = "2026-07-04";
    private const string RetainedDay = "2026-07-05";

    private string? _originalTimeZone;

    [TestInitialize]
    public void PinProcessTimeZoneWestOfUtc()
    {
        _originalTimeZone = Environment.GetEnvironmentVariable("TZ");
        Environment.SetEnvironmentVariable("TZ", WestOfUtcZone);
        TimeZoneInfo.ClearCachedData();

        // Without this the case would pass vacuously on any host where the pin did not take:
        // the pre-fix parse only diverges from UTC when the local zone is actually west of it.
        Assert.IsLessThan(
            TimeSpan.Zero,
            TimeZoneInfo.Local.GetUtcOffset(EvaluatedUtc),
            $"The process time zone was not pinned west of UTC; TimeZoneInfo.Local is {TimeZoneInfo.Local.Id}.");
    }

    [TestCleanup]
    public void RestoreAmbientTimeZone()
    {
        Environment.SetEnvironmentVariable("TZ", _originalTimeZone);
        TimeZoneInfo.ClearCachedData();
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_WestOfUtcHost_KeepsTheFrameDayInsideTheWindow()
    {
        var root = CreateRoot();
        try
        {
            var retained = CreateFrameFile(root, RetainedDay, "retained.bin");
            var expired = CreateFrameFile(root, CutoffDay, "expired.bin");

            await CreateService().ApplyRetentionAsync(CreateConfig(root), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsTrue(
                File.Exists(retained),
                $"The frame day {RetainedDay} is one day inside the retention window and must survive the sweep.");

            // The negative control. Without it a fix that simply stopped pruning would pass.
            Assert.IsFalse(
                File.Exists(expired),
                $"The frame day {CutoffDay} is on the cutoff and must still be pruned.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_WestOfUtcHost_KeepsTheIndexFileInsideTheWindow()
    {
        var root = CreateRoot();
        try
        {
            var retained = CreateIndexFile(root, RetainedDay);
            var expired = CreateIndexFile(root, CutoffDay);

            await CreateService().ApplyRetentionAsync(CreateConfig(root), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsTrue(
                File.Exists(retained),
                $"The index file for {RetainedDay} is one day inside the retention window and must survive the sweep.");

            Assert.IsFalse(
                File.Exists(expired),
                $"The index file for {CutoffDay} is on the cutoff and must still be pruned.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateFrameFile(string root, string day, string fileName)
    {
        var parts = day.Split('-');
        var path = Path.Combine(root, "frames", parts[0], parts[1], parts[2], "Raw", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, day);
        return path;
    }

    private static string CreateIndexFile(string root, string day)
    {
        var path = Path.Combine(root, "index", $"frames_{day}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // An entry no hold references, so an eligible file is emptied and deleted.
        File.WriteAllLines(path, [$"{{\"artifactId\":\"{Guid.NewGuid()}\"}}"]);
        return path;
    }

    private static RetentionBackgroundService CreateService()
        => new(
            new StubConfigurationAccessor(),
            Options.Create(new CameraAgentHostOptions()),
            new FixedTimeProvider(EvaluatedUtc),
            new SqliteArtifactOutbox(),
            new FixedCapacityProvider(50),
            new StoragePressureState(),
            NullLogger<RetentionBackgroundService>.Instance);

    private static CameraModuleConfig CreateConfig(string root)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("Virtual", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig([new CaptureProcessingStepConfig(
                FileStorageCaptureProcessingStep.StableAlias,
                Options: JsonSerializer.SerializeToElement(new { storageRoot = root, retentionDays = 7 }))]));

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "skymonitor-retention-tz", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FixedCapacityProvider(double availablePercent) : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot)
            => new(1000, (long)(10 * availablePercent));
    }

    private sealed class StubConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => false;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<CameraModuleConfig>(new NotSupportedException());
    }
}
