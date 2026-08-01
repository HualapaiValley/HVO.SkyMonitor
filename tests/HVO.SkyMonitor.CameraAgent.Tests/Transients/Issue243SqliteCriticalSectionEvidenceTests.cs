using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Issue243SqliteCriticalSectionEvidenceTests
{
    private const int SourceCount = 5;
    private const int SourceBytes = 4096;
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task CandidateReservation_AllowsUnrelatedWriterWhilePhysicalEvidenceReadIsPaused()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The deterministic FIFO evidence barrier requires Linux.");
        }

        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var sources = new TransientSourceEvidenceReferenceV1[SourceCount];
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index] = await fixture.AddRawSourceAsync(index + 1, SourceBytes).ConfigureAwait(false);
        }

        var sidecarPath = Path.ChangeExtension(fixture.ResolvePayload(sources[^1]), ".json");
        var sidecarBytes = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
        File.Delete(sidecarPath);
        await CreateFifoAsync(sidecarPath).ConfigureAwait(false);

        var writerConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fifoWriter = Task.Run(async () =>
        {
            using var stream = new FileStream(
                sidecarPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous);
            writerConnected.TrySetResult();
            await releaseWriter.Task.ConfigureAwait(false);
            await stream.WriteAsync(sidecarBytes).ConfigureAwait(false);
        });

        using var competingConnection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.Root, "journal", "raw-ingress.db")};Default Timeout=15");
        await competingConnection.OpenAsync().ConfigureAwait(false);
        using var competingCommand = competingConnection.CreateCommand();
        competingCommand.CommandText = """
            UPDATE raw_captures
            SET failure_reason = 'issue-243-sentinel'
            WHERE agent_id = 'agent' AND capture_sequence = 1;
            """;
        using var operationCancellation = new CancellationTokenSource();
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(sources);
        var reservationTask = fixture.Journal.ReserveAsync(
            reservation, operationCancellation.Token).AsTask();
        Task<(int Rows, TimeSpan Elapsed)>? competingWriter = null;
        try
        {
            await writerConnected.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var competingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            competingWriter = Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                competingEntered.TrySetResult();
                var rows = await competingCommand.ExecuteNonQueryAsync(operationCancellation.Token).ConfigureAwait(false);
                return (rows, Stopwatch.GetElapsedTime(started));
            });
            await competingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await competingWriter.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            Assert.IsFalse(reservationTask.IsCompleted,
                "The FIFO must still hold physical validation after the unrelated writer commits.");
            await Task.Delay(HoldDuration).ConfigureAwait(false);
            releaseWriter.TrySetResult();
        }
        finally
        {
            var drain = writerConnected.Task.IsCompleted ? null : DrainFifoAsync(sidecarPath);
            releaseWriter.TrySetResult();
            var cleanupTasks = new List<Task> { fifoWriter, reservationTask };
            if (drain is not null)
            {
                cleanupTasks.Add(drain);
            }
            if (competingWriter is not null)
            {
                cleanupTasks.Add(competingWriter);
            }
            await EvidenceTaskCleanup.DrainAsync(
                cleanupTasks, operationCancellation, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        Assert.IsNotNull(competingWriter);
        var competingResult = await competingWriter.ConfigureAwait(false);
        var competingWait = competingResult.Elapsed;
        Assert.AreEqual(1, competingResult.Rows);
        Assert.IsLessThanOrEqualTo(250, competingWait.TotalMilliseconds);
        Assert.AreEqual("issue-243-sentinel", await fixture.ScalarStringAsync(
            "SELECT failure_reason FROM raw_captures WHERE agent_id = 'agent' AND capture_sequence = 1;")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;")
            .ConfigureAwait(false));
        Assert.AreEqual(SourceCount, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_sources;")
            .ConfigureAwait(false));

        var repositoryRoot = FindRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(Issue243SqliteCriticalSectionEvidenceTests),
            typeof(CameraAgentServiceCollectionExtensions)).ConfigureAwait(false);
        var output = Path.Combine(
            repositoryRoot, "TestResults", "issue-243", source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(output);
        var evidence = new
        {
            Schema = "hvo-issue-243-sqlite-critical-section-v2",
            Source = source,
            Workload = new
            {
                Sources = SourceCount,
                BytesPerSource = SourceBytes,
                TotalPayloadBytes = SourceCount * SourceBytes,
                SidecarBytes = sidecarBytes.Length,
                BarrierMilliseconds = HoldDuration.TotalMilliseconds,
                UnrelatedWriters = 1
            },
            Observation = new
            {
                CompetingWriterWaitMilliseconds = competingWait.TotalMilliseconds,
                WriterBlockedAt250Milliseconds = false,
                WriterCompletedBeforePhysicalRelease = true,
                CompetingWriterRows = competingResult.Rows,
                CompetingWriterSentinel = "issue-243-sentinel",
                CandidateRows = 1,
                SourceRows = SourceCount
            },
            Correctness = "Corrected production completed the unrelated sentinel mutation while physical evidence remained blocked, then completed one reservation with all source identities retained.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(output, "sqlite-critical-section.json"),
            evidence,
            EvidenceJsonOptions).ConfigureAwait(false);
    }

    private static async Task CreateFifoAsync(string path)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "mkfifo",
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(path);
        Assert.IsTrue(process.Start(), "mkfifo did not start.");
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, error);
    }

    private static async Task DrainFifoAsync(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous);
        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
