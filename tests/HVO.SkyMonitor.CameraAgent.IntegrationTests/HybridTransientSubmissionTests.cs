using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class HybridTransientSubmissionTests
{
    private const int MaximumTemporalPendingTail = 2;

    [TestMethod]
    public async Task CameraAgentDurableSubmissionAcknowledgesAndRetriesAgainstOneCentralJob()
    {
        using var fixture = new CameraAgentIntegrationFixture(hybridTransientMode: true);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var scope = fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var captureService = services.GetServices<IHostedService>().OfType<CameraCaptureService>().Single();
        try
        {
            TransientCandidateSubmissionEnvelopeV1? submission = null;
            await WaitUntilAsync(async () =>
            {
                submission = await ReadAcknowledgedSubmissionAsync(fixture.StorageRoot).ConfigureAwait(false);
                return submission is not null;
            }, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            Assert.IsNotNull(submission);
            var causalTailCompletedUtc = fixture.TransientEpochUtc.AddSeconds(3);
            var causalTailDelay = causalTailCompletedUtc - DateTimeOffset.UtcNow;
            if (causalTailDelay > TimeSpan.Zero)
            {
                await Task.Delay(causalTailDelay).ConfigureAwait(false);
            }
            await captureService.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(3, submission.Candidate.ContextSources.Count);
            Assert.AreEqual(fixture.DeviceId, submission.Candidate.AgentId);
            var submissionBytes = await ReadSubmissionPayloadAsync(
                fixture.StorageRoot, submission.CandidateId).ConfigureAwait(false);
            CollectionAssert.AreEqual(TransientCandidateDeliveryJson.Serialize(submission), submissionBytes);
            var journal = services.GetRequiredService<ITransientCandidateJournal>();
            var artifactIds = submission.Candidate.ContextSources
                .Select(item => item.Locator.Artifact.ArtifactId).ToArray();
            await WaitUntilAsync(async () =>
            {
                using var hostScope = fixture.CreateHostScope();
                var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var connection = db.Database.GetDbConnection();
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM CentralArtifacts AS center
                    INNER JOIN CentralFrames AS centerFrame ON centerFrame.Id = center.CentralFrameId
                    INNER JOIN CentralArtifacts AS future ON future.DevicePublicId = center.DevicePublicId
                    INNER JOIN CentralFrames AS futureFrame ON futureFrame.Id = future.CentralFrameId
                    WHERE center.ArtifactId = @center
                      AND futureFrame.AgentId = centerFrame.AgentId
                      AND futureFrame.RigId = centerFrame.RigId
                      AND futureFrame.CaptureSequence IN (centerFrame.CaptureSequence + 1, centerFrame.CaptureSequence + 2)
                      AND future.Role = center.Role
                      AND future.ObjectState = 'Available'
                      AND future.ReconstructionState = 'Complete';
                    """;
                AddParameter(command, "@center", artifactIds[2]);
                return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) == 2;
            }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            var acknowledged = await journal.ReadAsync(submission.CandidateId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(acknowledged);
            Assert.AreEqual(TransientCandidateWorkflowPhase.Acknowledged, acknowledged.Phase);
            Assert.IsTrue(acknowledged.SourceHoldReleased);
            Assert.IsNotNull(acknowledged.AcknowledgementPayloadSha256);
            var acknowledgement = acknowledged.Acknowledgement;
            Assert.IsNotNull(acknowledgement);
            Assert.AreEqual(TransientCandidateSubmissionDisposition.Accepted, acknowledgement.Disposition);

            TransientCandidateDeliveryAggregate? aggregate = null;
            DeliverySummary? deliverySummary = null;
            await WaitUntilAsync(async () =>
            {
                aggregate = await journal.ReadDeliveryAggregateAsync(CancellationToken.None).ConfigureAwait(false);
                deliverySummary = await ReadDeliverySummaryAsync(fixture.StorageRoot).ConfigureAwait(false);
                return aggregate.PendingCount == 0 && aggregate.QuarantinedCount == 0 &&
                    deliverySummary.TotalSubmissions > 0 && deliverySummary.IncompleteSubmissions == 0 &&
                    deliverySummary.HandoffPending == 0 && deliverySummary.DeliveryQuarantined == 0 &&
                    deliverySummary.PendingWorkerFrames == 0 &&
                    deliverySummary.PendingWorkerCandidates <= MaximumTemporalPendingTail;
            }, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            Assert.IsNotNull(aggregate);
            Assert.IsNotNull(deliverySummary);
            Assert.AreEqual(0L, aggregate.PendingCount);
            Assert.AreEqual(0L, aggregate.QuarantinedCount);
            Assert.AreEqual(0L, deliverySummary.IncompleteSubmissions);
            Assert.AreEqual(0L, deliverySummary.HandoffPending);
            Assert.AreEqual(0L, deliverySummary.DeliveryQuarantined);
            Assert.IsLessThanOrEqualTo(MaximumTemporalPendingTail, deliverySummary.PendingWorkerCandidates);

            using var reopenedScope = fixture.CreateCameraAgentScope();
            var reopenedJournal = reopenedScope.ServiceProvider.GetRequiredService<ITransientCandidateJournal>();
            var reopened = await reopenedJournal.ReadAsync(submission.CandidateId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(reopened);
            Assert.IsTrue(reopened.SourceHoldReleased);
            Assert.IsNotNull(reopened.Acknowledgement);

            using var retryScope = fixture.CreateCameraAgentScope();
            using var duplicate = await SendAsync(
                retryScope.ServiceProvider,
                submission.Candidate.AgentId,
                submission.SubmissionIdentitySha256,
                submissionBytes).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
            var duplicateAcknowledgement = TransientCandidateDeliveryJson.ParseAcknowledgement(
                await duplicate.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Value;
            Assert.IsNotNull(duplicateAcknowledgement);
            Assert.AreEqual(TransientCandidateSubmissionDisposition.Duplicate, duplicateAcknowledgement.Disposition);
            Assert.AreEqual(acknowledgement.ReceivedAtUtc, duplicateAcknowledgement.ReceivedAtUtc);

            using var assertionScope = fixture.CreateHostScope();
            var centralDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var centralConnection = centralDb.Database.GetDbConnection();
            await centralConnection.OpenAsync().ConfigureAwait(false);
            using var centralCommand = centralConnection.CreateCommand();
            centralCommand.CommandText = """
                SELECT COUNT(*) FROM CentralTransientValidationJobs
                WHERE SubmissionIdentitySha256 = @submission;
                """;
            AddParameter(centralCommand, "@submission", submission.SubmissionIdentitySha256);
            Assert.AreEqual(1, Convert.ToInt32(
                await centralCommand.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
            centralCommand.CommandText = """
                SELECT COUNT(*)
                FROM CentralDerivativeJobInputs AS input
                INNER JOIN CentralTransientValidationJobs AS validation
                    ON validation.CentralDerivativeJobId = input.CentralDerivativeJobId
                WHERE validation.SubmissionIdentitySha256 = @submission;
                """;
            Assert.AreEqual(5, Convert.ToInt32(
                await centralCommand.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await captureService.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IServiceProvider services,
        string deviceId,
        string submissionIdentitySha256,
        byte[] submissionBytes)
    {
        using var client = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(SkyMonitorClientOptions.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/device/transient-candidates");
        request.Headers.Add("X-HVO-Device-Id", deviceId);
        request.Headers.Add("X-HVO-Device-Key", "cameraagent-integration-key");
        request.Headers.Add("Idempotency-Key", submissionIdentitySha256);
        request.Content = new ByteArrayContent(submissionBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadSubmissionPayloadAsync(string storageRoot, Guid candidateId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storageRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT submission_payload FROM transient_candidates WHERE candidate_id = $candidate;";
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        return (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidDataException("The Hybrid worker did not persist submission bytes."));
    }

    private static async Task<TransientCandidateSubmissionEnvelopeV1?> ReadAcknowledgedSubmissionAsync(
        string storageRoot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storageRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT submission_payload
            FROM transient_candidates
            WHERE acknowledgement_payload IS NOT NULL
            ORDER BY updated_unix_ms
            LIMIT 1;
            """;
        var payload = await command.ExecuteScalarAsync().ConfigureAwait(false) as byte[];
        return payload is null ? null : TransientCandidateDeliveryJson.ParseSubmission(payload).Value;
    }

    private static async Task<DeliverySummary> ReadDeliverySummaryAsync(string storageRoot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storageRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE submission_payload IS NOT NULL),
                COUNT(*) FILTER (WHERE submission_payload IS NOT NULL AND
                    (phase != 'acknowledged' OR acknowledgement_payload IS NULL OR source_hold_released = 0)),
                COUNT(*) FILTER (WHERE phase = 'handoff_pending'),
                COUNT(*) FILTER (WHERE phase = 'quarantined' AND submission_payload IS NOT NULL),
                (SELECT COUNT(*) FROM transient_worker_frames WHERE state IN ('queued', 'retry_wait')),
                (SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'pending')
            FROM transient_candidates;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);
        return new DeliverySummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Assert.Fail($"Condition was not met within {timeout}.");
    }

    private sealed record DeliverySummary(
        long TotalSubmissions,
        long IncompleteSubmissions,
        long HandoffPending,
        long DeliveryQuarantined,
        long PendingWorkerFrames,
        long PendingWorkerCandidates);
}
