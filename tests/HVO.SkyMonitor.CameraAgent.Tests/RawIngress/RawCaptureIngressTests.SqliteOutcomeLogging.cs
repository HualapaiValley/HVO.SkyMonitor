using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    private const int SqliteSucceededEventId = 2038;
    private const int SqliteFailedEventId = 2048;
    private const int SqliteIoError = 10;
    private static readonly string[] SqliteSuccessOperations = ["checkpoint", "commit"];

    [TestMethod]
    public void SqliteOutcomeMessages_HaveFixedLevelsAndTheFailureKeepsEvent2048()
    {
        // A successful SQLite operation is never a warning (#1295). The failure keeps event 2048 with its name and the
        // Operation/Result fields that the sanitized telemetry export retains for that event ID.
        var logger = new SqliteOutcomeLogger();

        logger.RawIngressSqliteSucceeded("commit");
        logger.RawIngressSqliteFailed("commit");

        var events = logger.Snapshot;
        Assert.HasCount(2, events);
        Assert.AreEqual(
            new SqliteOutcomeLogEntry(
                LogLevel.Debug,
                SqliteSucceededEventId,
                "RawIngressSqliteSucceeded",
                "Raw ingress SQLite operation commit completed with result success",
                "commit",
                "success"),
            events[0]);
        Assert.AreEqual(
            new SqliteOutcomeLogEntry(
                LogLevel.Warning,
                SqliteFailedEventId,
                "RawIngressSqliteResult",
                "Raw ingress SQLite operation commit completed with result failure",
                "commit",
                "failure"),
            events[1]);
    }

    [TestMethod]
    public async Task InitializeAndCommit_LogSqliteSuccessAtDebugAndNothingAtWarningAsync()
    {
        var root = CreateRoot();
        try
        {
            var logger = new SqliteOutcomeLogger();
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System), logger: logger);

            var receipt = await ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(1), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(receipt);
            Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
            var events = logger.Snapshot;
            var successes = events.Where(entry => entry.EventId == SqliteSucceededEventId).ToArray();
            CollectionAssert.AreEqual(SqliteSuccessOperations, successes.Select(entry => entry.Operation).ToArray());
            Assert.IsTrue(successes.All(entry => entry.Level == LogLevel.Debug));
            Assert.IsFalse(events.Any(entry => entry.EventId == SqliteFailedEventId));
            Assert.IsFalse(
                events.Any(entry => entry.Level >= LogLevel.Warning),
                string.Join(Environment.NewLine, events.Where(entry => entry.Level >= LogLevel.Warning)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptAsync_SqliteCommitFailure_LogsTheFailureAtWarningAndNoSuccessAsync()
    {
        var root = CreateRoot();
        try
        {
            var logger = new SqliteOutcomeLogger();
            using var ingress = CreateIngress(
                root,
                new RawIngressState(TimeProvider.System),
                new OneShotFaultInjector(
                    RawIngressFaultPoint.BeforeJournalTransactionCommit,
                    static () => new SqliteException("Injected raw ingress commit failure.", SqliteIoError, SqliteIoError)),
                logger: logger);
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var eventsBeforeAccept = logger.Snapshot.Length;

            await Assert.ThrowsExactlyAsync<SqliteException>(() => ingress.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(2), [2, 2, 2, 2]), CancellationToken.None).AsTask())
                .ConfigureAwait(false);

            var duringAccept = logger.Snapshot.Skip(eventsBeforeAccept).ToArray();
            var failure = duringAccept.Single(entry => entry.EventId == SqliteFailedEventId);
            Assert.AreEqual(LogLevel.Warning, failure.Level);
            Assert.AreEqual("commit", failure.Operation);
            Assert.AreEqual("failure", failure.Result);
            Assert.IsFalse(duringAccept.Any(entry => entry.EventId == SqliteSucceededEventId));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_SqliteFailure_LogsTheFailureAtWarningAndNoSuccessAsync()
    {
        var root = CreateRoot();
        try
        {
            var logger = new SqliteOutcomeLogger();
            using var ingress = CreateIngress(
                root,
                new RawIngressState(TimeProvider.System),
                new OneShotFaultInjector(
                    RawIngressFaultPoint.BeforeMigrationCommit,
                    static () => new SqliteException("Injected raw ingress initialization failure.", SqliteIoError, SqliteIoError)),
                logger: logger);

            await Assert.ThrowsExactlyAsync<SqliteException>(
                () => ingress.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);

            var events = logger.Snapshot;
            var failure = events.Single(entry => entry.EventId == SqliteFailedEventId);
            Assert.AreEqual(LogLevel.Warning, failure.Level);
            Assert.AreEqual("initialization", failure.Operation);
            Assert.AreEqual("failure", failure.Result);
            Assert.IsFalse(events.Any(entry => entry.EventId == SqliteSucceededEventId));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private sealed record SqliteOutcomeLogEntry(
        LogLevel Level,
        int EventId,
        string? EventName,
        string Message,
        string? Operation,
        string? Result);

    private sealed class SqliteOutcomeLogger : ILogger<RawCaptureIngress>
    {
        private readonly List<SqliteOutcomeLogEntry> _entries = [];

        public SqliteOutcomeLogEntry[] Snapshot
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var entry = new SqliteOutcomeLogEntry(
                logLevel,
                eventId.Id,
                eventId.Name,
                formatter(state, exception),
                fields?.FirstOrDefault(field => field.Key == "Operation").Value as string,
                fields?.FirstOrDefault(field => field.Key == "Result").Value as string);
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }
}
