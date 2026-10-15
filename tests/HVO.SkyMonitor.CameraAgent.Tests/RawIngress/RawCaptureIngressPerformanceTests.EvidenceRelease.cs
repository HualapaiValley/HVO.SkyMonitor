using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressPerformanceTests
{
    [TestMethod]
    public async Task FullResolutionEvidenceRelease_RecordsBoundedOperatorRecoveryMeasurement()
    {
        var root = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-1175-item4", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = await LoadCanonicalConfigurationAsync().ConfigureAwait(false);
            var rendered = await RenderCanonicalW2InputAsync(configuration).ConfigureAwait(false);
            var input = rendered with { Metadata = rendered.Metadata with { Scene = null } };
            string recoveredPath;
            string writtenOffPath;
            using (var original = CreateIngress(root))
            {
                recoveredPath = (await original.Ingress.AcceptAsync(configuration, CreateSubmission(1, input), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath;
                writtenOffPath = (await original.Ingress.AcceptAsync(configuration, CreateSubmission(2, input), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath;
            }
            var backup = Path.Combine(root, "recovered-original.bin");
            File.Move(recoveredPath, backup);
            File.Move(writtenOffPath, Path.Combine(root, "written-off-original.bin"));
            using var restarted = CreateIngress(root);
            var items = (await restarted.Ingress.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items;
            File.Move(backup, recoveredPath);
            var measurements = new List<object>();
            foreach (var action in new[] { RawEvidenceReleaseAction.Revalidate, RawEvidenceReleaseAction.WriteOff })
            {
                var target = items.Single(item => item.CaptureSequence == (action == RawEvidenceReleaseAction.Revalidate ? 1 : 2)).Target;
                var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
                using var process = Process.GetCurrentProcess();
                var beforeCpu = process.TotalProcessorTime;
                var beforeWorkingSet = Environment.WorkingSet;
                var beforeTransactions = restarted.Telemetry.TransactionCount;
                var beforeFlushes = restarted.Telemetry.FileFlushCount;
                var beforeSyncs = restarted.Telemetry.DirectorySyncCount;
                var start = Stopwatch.GetTimestamp();
                var receipt = await restarted.Ingress.ReleaseEvidenceAsync(target, action, $"measurement-{action}", "measurement-operator",
                    action == RawEvidenceReleaseAction.Revalidate ? "evidence-restored" : "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(start);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated;
                Assert.IsFalse(receipt.Duplicate);
                Assert.IsLessThan(input.Payload.LongLength, allocated, "Operator release must not allocate a full-frame payload copy.");
                measurements.Add(new
                {
                    Action = action.ToString(),
                    elapsed.TotalMilliseconds,
                    CpuMilliseconds = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds,
                    AllocatedBytes = allocated,
                    WorkingSetBefore = beforeWorkingSet,
                    WorkingSetAfter = Environment.WorkingSet,
                    PayloadBytesRead = action == RawEvidenceReleaseAction.Revalidate ? input.Payload.LongLength : 0,
                    PayloadBytesWritten = 0,
                    Transactions = restarted.Telemetry.TransactionCount - beforeTransactions,
                    FileFlushes = restarted.Telemetry.FileFlushCount - beforeFlushes,
                    DirectorySyncs = restarted.Telemetry.DirectorySyncCount - beforeSyncs,
                    receipt.Audit
                });
            }
            await restarted.Ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var health = await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5).ReadHealthTotalsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, health.FailureCount);
            var retainedFiles = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories);
            Assert.AreEqual(2, retainedFiles.Length);
            foreach (var file in retainedFiles)
            {
                using var stream = File.OpenRead(file);
                Assert.AreEqual(PayloadChecksum.ComputeSha256(input.Payload), await PayloadChecksum.ComputeSha256Async(stream, CancellationToken.None).ConfigureAwait(false));
            }
            var output = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-1175-item4", "release-measurement.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                Workload = new { Width = W2Width, Height = W2Height, Stride = W2Stride, Format = "BayerRggb16", PayloadBytes = input.Payload.LongLength, Captures = 2, Concurrency = 1 },
                Environment = new { OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Configuration = BuildConfiguration },
                Baseline = "No supported operator release existed; new action has no pre-change latency baseline.",
                Measurements = measurements,
                Backlog = new { UnreleasedFailures = health.FailureCount, RetainedRawFiles = retainedFiles.Length, NewCaptureAdmission = restarted.State.Snapshot.Availability.ToString() }
            }, EvidenceOptions)).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
