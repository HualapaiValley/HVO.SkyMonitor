using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OutboxOperationsEndpointTests
{
    [TestMethod]
    public async Task EndpointsEnforceOwnerAntiforgeryOpaqueTokensAndGenericMutationResults()
    {
        var artifactOutbox = new OperationalArtifactOutbox();
        var environmentalOutbox = new OperationalEnvironmentalOutbox();
        var evidenceOutbox = new OperationalExecutionEvidenceOutbox();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<IArtifactOutbox>();
            services.AddSingleton<IArtifactOutbox>(artifactOutbox);
            services.RemoveAll<IEnvironmentalObservationOutbox>();
            services.AddSingleton<IEnvironmentalObservationOutbox>(environmentalOutbox);
            services.RemoveAll<IExecutionEvidenceOutbox>();
            services.AddSingleton<IExecutionEvidenceOutbox>(evidenceOutbox);
            services.RemoveAll<CameraAgentStorageResolver>();
            services.AddSingleton(provider => new CameraAgentStorageResolver(
                provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = AssemblyHooks.Fixture.StorageRoot,
                    CentralIntegration = new CentralIntegrationOptions
                    {
                        Mode = CentralIntegrationMode.Enabled
                    },
                    CaptureDistribution = new CaptureDistributionOptions
                    {
                        UploadEnabled = true
                    }
                })));
        });
        string ownerId;
        string nonOwnerId;
        using (var scope = factory.Services.CreateScope())
        {
            var configurationAccessor = scope.ServiceProvider.GetRequiredService<ICameraAgentConfigurationAccessor>();
            if (!configurationAccessor.IsConfigured)
            {
                configurationAccessor.SetConfiguration(await scope.ServiceProvider
                    .GetRequiredService<ICameraAgentConfigurationLoader>()
                    .LoadAsync(CancellationToken.None).ConfigureAwait(false));
            }
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            ownerId = owner.Id;
            var nonOwner = await users.FindByEmailAsync("outbox-non-owner@cameraagent.integration").ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "outbox-non-owner@cameraagent.integration",
                    Email = "outbox-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                var created = await users.CreateAsync(nonOwner, "OutboxNonOwner!123").ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
            }
            nonOwnerId = nonOwner.Id;
        }

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var anonymousResponse = await anonymous.GetAsync(
            new Uri("/api/v1/operations/outboxes/artifacts?storage=raw-ingress", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var nonOwnerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerResponse = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/outboxes/environmental", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerResponse.StatusCode);

        using var ownerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var artifactPageResponse = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/outboxes/artifacts?storage=raw-ingress&pageSize=1", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, artifactPageResponse.StatusCode);
        var artifactJson = await artifactPageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertNonDisclosure(artifactJson);
        using var artifactPage = JsonDocument.Parse(artifactJson);
        var artifactItem = artifactPage.RootElement.GetProperty("items")[0];
        Assert.AreEqual("raw-ingress", artifactItem.GetProperty("storageAlias").GetString());
        var artifactReference = artifactItem.GetProperty("reference").GetString();
        var replayToken = artifactItem.GetProperty("allowedActions").GetProperty("replayToken").GetString();
        var abandonToken = artifactItem.GetProperty("allowedActions").GetProperty("abandonToken").GetString();
        Assert.IsNotNull(artifactReference);
        Assert.IsNotNull(replayToken);
        Assert.IsNotNull(abandonToken);
        Assert.IsFalse(artifactReference.Contains(OperationalArtifactOutbox.RecordKey, StringComparison.Ordinal));

        using var crossKind = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/outboxes/environmental/{Uri.EscapeDataString(artifactReference)}", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, crossKind.StatusCode);
        using var invalidReference = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/outboxes/artifacts/not-a-token", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, invalidReference.StatusCode);

        using var missingAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/outboxes/artifacts/replay", UriKind.Relative),
            new { actionToken = replayToken, reasonCode = "upstream-recovered" }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

        var antiforgery = await GetAntiforgeryTokenAsync(ownerClient).ConfigureAwait(false);
        using var wrongAction = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/artifacts/abandon", "wrong-action-1",
            replayToken, "operator-approved-loss", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongAction.StatusCode);

        using var applied = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/artifacts/replay", "artifact-http-operation-1",
            replayToken, "upstream-recovered", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NoContent, applied.StatusCode);
        using var duplicate = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/artifacts/replay", "artifact-http-operation-1",
            replayToken, "upstream-recovered", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
        using var collision = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/artifacts/replay", "artifact-http-operation-1",
            replayToken, "configuration-corrected", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, collision.StatusCode);
        AssertNonDisclosure(await collision.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var artifactAudit = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/outboxes/artifacts/{Uri.EscapeDataString(artifactReference)}/audit", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, artifactAudit.StatusCode);
        var auditJson = await artifactAudit.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(auditJson, "\"actorKind\":\"owner\"", StringComparison.Ordinal);
        AssertNonDisclosure(auditJson);

        using var environmentalPageResponse = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/outboxes/environmental", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, environmentalPageResponse.StatusCode);
        var environmentalJson = await environmentalPageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertNonDisclosure(environmentalJson);
        using var environmentalPage = JsonDocument.Parse(environmentalJson);
        var environmentalItem = environmentalPage.RootElement.GetProperty("items")[0];
        var environmentalReplay = environmentalItem.GetProperty("allowedActions").GetProperty("replayToken").GetString();
        Assert.IsNotNull(environmentalReplay);
        Assert.IsFalse(environmentalJson.Contains("recordId", StringComparison.OrdinalIgnoreCase));

        var wakeup = factory.Services.GetRequiredService<EnvironmentalObservationDeliveryWakeup>();
        using var wakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wake = wakeup.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, wakeTimeout.Token).AsTask();
        using var environmentalReplayResponse = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/environmental/replay", "environment-http-operation-1",
            environmentalReplay, "upstream-recovered", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NoContent, environmentalReplayResponse.StatusCode);
        await wake.ConfigureAwait(false);

        using var evidenceAnonymous = await anonymous.GetAsync(
            new Uri("/api/v1/operations/outboxes/execution-evidence", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, evidenceAnonymous.StatusCode);
        using var evidenceNonOwner = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/outboxes/execution-evidence", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, evidenceNonOwner.StatusCode);

        using var evidencePageResponse = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/outboxes/execution-evidence?pageSize=1", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, evidencePageResponse.StatusCode);
        var evidenceJson = await evidencePageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertNonDisclosure(evidenceJson);
        // The origin identity, the payload hash, and the SQLite row id never travel in an operator response.
        Assert.IsFalse(evidenceJson.Contains(
            OperationalExecutionEvidenceOutbox.OriginIdentity, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(evidenceJson.Contains("payloadSha256", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(evidenceJson.Contains("\"recordId\"", StringComparison.OrdinalIgnoreCase));
        using var evidencePage = JsonDocument.Parse(evidenceJson);
        var evidenceItem = evidencePage.RootElement.GetProperty("items")[0];
        Assert.AreEqual("GraphExecution", evidenceItem.GetProperty("bodyKind").GetString());
        Assert.AreEqual(7, evidenceItem.GetProperty("originSequence").GetInt64());
        Assert.AreEqual(
            "evidence.sequence-conflict",
            evidenceItem.GetProperty("reasonCode").GetString(),
            "A namespaced reason code must survive sanitization or an operator cannot tell why a unit is held.");
        var evidenceReference = evidenceItem.GetProperty("reference").GetString();
        var evidenceReplay = evidenceItem.GetProperty("allowedActions").GetProperty("replayToken").GetString();
        Assert.IsNotNull(evidenceReference);
        Assert.IsNotNull(evidenceReplay);

        using var evidenceCrossKind = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/outboxes/environmental/{Uri.EscapeDataString(evidenceReference)}", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, evidenceCrossKind.StatusCode);

        using var evidenceWrongAction = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/execution-evidence/abandon", "evidence-http-operation-0",
            evidenceReplay, "operator-approved-loss", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, evidenceWrongAction.StatusCode);

        var evidenceWakeup = factory.Services.GetRequiredService<ExecutionEvidenceExportWakeup>();
        using var evidenceWakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var evidenceWake = evidenceWakeup
            .WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, evidenceWakeTimeout.Token).AsTask();
        using var evidenceReplayResponse = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/execution-evidence/replay", "evidence-http-operation-1",
            evidenceReplay, "evidence-restored", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NoContent, evidenceReplayResponse.StatusCode);
        await evidenceWake.ConfigureAwait(false);

        using var evidenceDuplicate = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/execution-evidence/replay", "evidence-http-operation-1",
            evidenceReplay, "evidence-restored", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NoContent, evidenceDuplicate.StatusCode);
        using var evidenceCollision = await SendMutationAsync(
            ownerClient, "/api/v1/operations/outboxes/execution-evidence/replay", "evidence-http-operation-1",
            evidenceReplay, "configuration-corrected", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, evidenceCollision.StatusCode);
        AssertNonDisclosure(await evidenceCollision.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var evidenceAudit = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/outboxes/execution-evidence/{Uri.EscapeDataString(evidenceReference)}/audit",
                UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, evidenceAudit.StatusCode);
        AssertNonDisclosure(await evidenceAudit.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string path,
        string operationKey,
        string actionToken,
        string reasonCode,
        string antiforgery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("Idempotency-Key", operationKey);
        request.Headers.Add("RequestVerificationToken", antiforgery);
        request.Content = JsonContent.Create(new { actionToken, reasonCode });
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        const string name = "name=\"__RequestVerificationToken\"";
        var nameIndex = html.IndexOf(name, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, nameIndex);
        const string value = "value=\"";
        var valueIndex = html.IndexOf(value, nameIndex, StringComparison.Ordinal) + value.Length;
        var endIndex = html.IndexOf('"', valueIndex);
        return WebUtility.HtmlDecode(html[valueIndex..endIndex]);
    }

    private static void AssertNonDisclosure(string content)
    {
        Assert.IsFalse(content.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains(OperationalArtifactOutbox.RecordKey, StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("manifest", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("checksum", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("relativePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("leaseToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("acknowledgement", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A bounded stand-in for the durable evidence outbox. Only the operator surface is implemented; every other
    /// member throws, which proves the endpoints read nothing else.
    /// </summary>
    private sealed class OperationalExecutionEvidenceOutbox : IExecutionEvidenceOutbox
    {
        internal const string OriginIdentity =
            "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";
        private const long RecordId = 4242;
        private readonly Dictionary<string, OperationRequest> _operations = new(StringComparer.Ordinal);
        private static readonly ExecutionEvidenceOutboxOperationsRecord Record = new(
            RecordId,
            nameof(ExecutionEvidenceBodyKind.GraphExecution),
            7,
            nameof(ExecutionEvidenceUnitStatus.Quarantined),
            4,
            9347,
            DateTimeOffset.Parse("2026-09-01T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-01T01:05:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-01T01:06:00Z", System.Globalization.CultureInfo.InvariantCulture),
            // A namespaced contract reason code, so a sanitizer that rejected the dot would be caught here.
            "evidence.sequence-conflict",
            CanReplay: true,
            CanAbandon: true,
            new ExecutionEvidenceOutboxOperationsCursor(RecordId));

        public ValueTask<ExecutionEvidenceOutboxOperationsPage> ReadOperationsPageAsync(
            string root, int pageSize, ExecutionEvidenceOutboxOperationsCursor? cursor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new ExecutionEvidenceOutboxOperationsPage([Record], null));

        public ValueTask<ExecutionEvidenceOutboxOperationsRecord?> ReadOperationsDetailAsync(
            string root, long recordId, CancellationToken cancellationToken)
            => ValueTask.FromResult<ExecutionEvidenceOutboxOperationsRecord?>(
                recordId == RecordId ? Record : null);

        public ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
            string root, long recordId, int pageSize, OutboxOperationsAuditCursor? cursor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new OutboxOperationsAuditPage(
                [new OutboxOperationsAuditRecord(1, "replay", "owner", "evidence-restored", DateTimeOffset.UtcNow)],
                null));

        public ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
            string root, long recordId, OutboxOperationAction action, string operationKey, string actorKind,
            string reasonCode, CancellationToken cancellationToken)
        {
            var request = new OperationRequest(
                recordId.ToString(System.Globalization.CultureInfo.InvariantCulture), action, actorKind, reasonCode);
            if (_operations.TryGetValue(operationKey, out var existing))
            {
                return existing == request
                    ? ValueTask.FromResult(OutboxOperationDisposition.Duplicate)
                    : throw new OutboxOperationCollisionException("operation key reuse");
            }
            _operations[operationKey] = request;
            return ValueTask.FromResult(OutboxOperationDisposition.Applied);
        }

        public ValueTask InitializeAsync(string root, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ExecutionEvidenceOriginRecord> EnsureOriginAsync(
            string root, ExecutionEvidenceOriginV1 origin, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ExecutionEvidenceDiscoveryCursor> ReadDiscoveryCursorAsync(
            string root, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ExecutionEvidenceEnlistmentResult> EnlistAsync(
            string root, string originIdentitySha256, Guid executionId,
            IReadOnlyList<ExecutionEvidenceEnlistmentUnit> units,
            ExecutionEvidenceDiscoveryCursor cursor, ExecutionEvidenceEnlistmentLimits limits,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RecordProjectionRejectedAsync(
            string root, ExecutionEvidenceDiscoveryCursor cursor, Guid executionId, string reasonCode,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RecordSourcePrunedAsync(
            string root, ExecutionEvidenceDiscoveryCursor cursor, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ExecutionEvidenceOriginRecord>> ReadOriginsWithWorkAsync(
            string root, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ExecutionEvidenceUnit>> ReadPendingAsync(
            string root, string originIdentitySha256, int maximumUnits, long maximumBytes, DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ExecutionEvidenceUnit>> ReadRangeAsync(
            string root, string originIdentitySha256, IReadOnlyList<ExecutionEvidenceSequenceRangeV1> ranges,
            int maximumUnits, long maximumBytes, DateTimeOffset nowUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask AcknowledgeAsync(
            string root, string originIdentitySha256, long originSequence, string payloadSha256,
            DateTimeOffset acknowledgedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RetryAsync(
            string root, string originIdentitySha256, long originSequence, DateTimeOffset nextAttemptUtc,
            string reasonCode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask QuarantineAsync(
            string root, string originIdentitySha256, long originSequence, string reasonCode,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RecordAcknowledgedThroughAsync(
            string root, string originIdentitySha256, long acknowledgedThroughSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RecordConflictAsync(
            string root, string originIdentitySha256, long originSequence, string localPayloadSha256,
            string? receiverPayloadSha256, string reasonCode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ExecutionEvidenceBacklog> ReadBacklogAsync(
            string root, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<int> RetainAsync(
            string root, TimeSpan acknowledgementRetention, int maximumRetainedAcknowledgements,
            string? retainedOriginIdentitySha256, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class OperationalArtifactOutbox : IArtifactOutbox
    {
        internal const string RecordKey = "private-artifact-idempotency-key";
        private readonly Dictionary<string, OperationRequest> _operations = new(StringComparer.Ordinal);
        private static readonly ArtifactOutboxOperationsRecord Record = new(
            RecordKey,
            ArtifactOutboxManifestKind.ManifestV2,
            ArtifactOutboxStatus.Quarantined,
            3,
            4096,
            "image/fits",
            FrameArtifactRole.Raw,
            DateTimeOffset.Parse("2026-07-20T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-20T01:05:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-20T01:06:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "upstream-rejected",
            CanReplay: true,
            CanAbandon: true,
            new ArtifactOutboxOperationsCursor(1));

        public ValueTask<ArtifactOutboxOperationsPage> ReadOperationsPageAsync(
            string root, int pageSize, ArtifactOutboxOperationsCursor? cursor, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ArtifactOutboxOperationsPage([Record], null));

        public ValueTask<ArtifactOutboxOperationsRecord?> ReadOperationsDetailAsync(
            string root, string recordKey, CancellationToken cancellationToken)
            => ValueTask.FromResult<ArtifactOutboxOperationsRecord?>(recordKey == RecordKey ? Record : null);

        public ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
            string root, string recordKey, int pageSize, OutboxOperationsAuditCursor? cursor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new OutboxOperationsAuditPage(
                [new OutboxOperationsAuditRecord(1, "replay", "owner", "upstream-recovered", DateTimeOffset.UtcNow)], null));

        public ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
            string root, string recordKey, OutboxOperationAction action, string operationKey, string actorKind,
            string reasonCode, CancellationToken cancellationToken)
        {
            var request = new OperationRequest(recordKey, action, actorKind, reasonCode);
            lock (_operations)
            {
                if (_operations.TryGetValue(operationKey, out var existing))
                {
                    if (existing != request)
                    {
                        throw new OutboxOperationCollisionException();
                    }
                    return ValueTask.FromResult(OutboxOperationDisposition.Duplicate);
                }
                _operations.Add(operationKey, request);
            }
            return ValueTask.FromResult(OutboxOperationDisposition.Applied);
        }

        public ValueTask InitializeAsync(string root, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask EnqueueAsync(
            string root, ArtifactManifestV2 manifest, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask EnqueueAsync(
            string root, StructuredProcessingProductManifestV1 manifest, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<ArtifactOutboxLease?> ClaimAsync(
            string root, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RenewAsync(
            string root, ArtifactOutboxLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RetryAsync(
            string root, ArtifactOutboxLease lease, DateTimeOffset nextAttemptUtc, string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AcknowledgeAsync(
            string root, ArtifactOutboxLease lease, ArtifactUploadAcknowledgement acknowledgement,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask QuarantineAsync(
            string root, ArtifactOutboxLease lease, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask ReplayAsync(
            string root, string idempotencyKey, string actor, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask AbandonAsync(
            string root, string idempotencyKey, string actor, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ArtifactOutboxRecord?> ReadAsync(
            string root, string idempotencyKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ArtifactOutboxAuditEntry>> ReadAuditAsync(
            string root, string idempotencyKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ArtifactOutboxRetentionHold>> GetRetentionHoldsAsync(
            string root, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<ArtifactOutboxRetentionHold>>([]);

        public ValueTask<IReadOnlyList<Guid>> GetAcknowledgedArtifactIdsAsync(
            string root, IReadOnlySet<Guid> artifactIds, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<Guid>>([]);

        public ValueTask<ArtifactOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 0, 0, 0));
    }

    private sealed class OperationalEnvironmentalOutbox : IEnvironmentalObservationOutbox
    {
        private static readonly EnvironmentalOutboxOperationsRecord Record = new(
            731,
            "terminal",
            4,
            2048,
            DateTimeOffset.Parse("2026-07-20T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-20T02:05:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "upstream-rejected",
            CanReplay: true,
            CanAbandon: true,
            new EnvironmentalOutboxOperationsCursor(731));

        public ValueTask<EnvironmentalOutboxOperationsPage> ReadOperationsPageAsync(
            string root, int pageSize, EnvironmentalOutboxOperationsCursor? cursor, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalOutboxOperationsPage([Record], null));

        public ValueTask<EnvironmentalOutboxOperationsRecord?> ReadOperationsDetailAsync(
            string root, long recordId, CancellationToken cancellationToken)
            => ValueTask.FromResult<EnvironmentalOutboxOperationsRecord?>(recordId == Record.RecordId ? Record : null);

        public ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
            string root, long recordId, OutboxOperationAction action, string operationKey, string actorKind,
            string reasonCode, CancellationToken cancellationToken)
            => ValueTask.FromResult(OutboxOperationDisposition.Applied);

        public ValueTask<EnvironmentalObservationEnqueueDisposition> EnqueueAsync(
            string root, HVO.SkyMonitor.Processing.EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<EnvironmentalObservationOutboxLease?> ClaimAsync(
            string root, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask AcknowledgeAsync(
            string root, EnvironmentalObservationOutboxLease lease,
            EnvironmentalObservationAcknowledgement acknowledgement, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RetryAsync(
            string root, EnvironmentalObservationOutboxLease lease, DateTimeOffset retryAtUtc, string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask QuarantineAsync(
            string root, EnvironmentalObservationOutboxLease lease, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask TerminalAsync(
            string root, EnvironmentalObservationOutboxLease lease, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<EnvironmentalObservationOutboxSnapshot> GetSnapshotAsync(
            string root, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<EnvironmentalObservationDeadLetter>> ReadDeadLettersAsync(
            string root, int maximumResults, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ReplayAsync(
            string root, long recordId, string actor, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask AbandonAsync(
            string root, long recordId, string actor, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed record OperationRequest(
        string RecordKey,
        OutboxOperationAction Action,
        string ActorKind,
        string ReasonCode);
}
